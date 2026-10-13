using System.Runtime.InteropServices;
using Autodesk.Connectivity.WebServices;
using SFVaultBridge.Core;
using VDF = Autodesk.DataManagement.Client.Framework;
using WsFile = Autodesk.Connectivity.WebServices.File;
using Inv = Inventor;

namespace SFVaultBridge.Addin;

/// <summary>
/// Fixes iProperties of existing Vault parts: check out through the signed-in VDF connection, edit in Inventor, check in a new version.
/// Runs on Inventor's UI thread. Never connects on its own; uses the Vault add-in's session.
/// </summary>
internal sealed class DataCardHost : IDataCardHost
{
    private readonly Inv.Application _app;

    public DataCardHost(Inv.Application app) => _app = app;

    private static VDF.Vault.Currency.Connections.Connection Conn => VaultSession.Connection;

    // ---- Resolve ----

    public IReadOnlyList<DataCardFileInfo> Find(DataCardFileRef file)
    {
        IEnumerable<WsFile> found;
        if (file.IsPath)
        {
            var f = VaultFiles.FindLatest(file.Text);
            found = f is null ? Array.Empty<WsFile>() : new[] { f };
        }
        else
        {
            found = VaultFiles.FindByNameContaining(file.Text)
                .Where(f => string.Equals(f.Name, file.Text, StringComparison.OrdinalIgnoreCase))
                .GroupBy(f => f.MasterId).Select(g => g.First());
        }
        return found.Select(Describe).ToList();
    }

    private static DataCardFileInfo Describe(WsFile f)
    {
        var folder = VaultFiles.FolderPathOf(f).TrimEnd('/');
        var by = "";
        if (f.CheckedOut)
        {
            try
            {
                by = f.CkOutUserId == Conn.UserID
                    ? "you"
                    : Conn.WebServiceManager.AdminService.GetUserByUserId(f.CkOutUserId)?.Name ?? "someone else";
            }
            catch (Exception) { by = "someone"; }
        }
        return new DataCardFileInfo(folder + "/" + f.Name, f.Name, VaultFiles.CategoryName(f) ?? "", VaultFiles.StateOf(f),
            IsReleased(f), f.CheckedOut, by, f.VerNum);
    }

    private static bool IsReleased(WsFile f)
    {
        try
        {
            var id = f.FileLfCyc?.LfCycStateId ?? 0;
            if (id <= 0) return false;
            var state = Conn.WebServiceManager.LifeCycleService.GetLifeCycleStatesByIds(new[] { id })?.FirstOrDefault();
            return state?.ReleasedState == true;
        }
        catch (Exception) { return false; }
    }

    // ---- Read (dry run and pre-check) ----

    public IReadOnlyDictionary<PropRef, string?> Read(DataCardFileInfo file, IReadOnlyList<PropRef> props)
    {
        var latest = Latest(file);
        var local = VaultFiles.LocalPathOf(latest);
        var open = DrawingExporter.FindOpen(_app, local);
        if (open is not null)
        {
            if (open.Dirty) throw new DataCardSkipException($"{file.Name} is open in Inventor with unsaved changes. Save or close it first.");
            RequireCurrentLocalCopy(file, latest, local);
            return ReadProps(open, props);
        }
        string path;
        try
        {
            var got = VaultFiles.AcquireLatest(new[] { latest });
            path = got.FirstOrDefault() ?? local;
        }
        catch (Exception ex) { throw new DataCardStepException("download", ex.Message); }
        if (!System.IO.File.Exists(path)) throw new DataCardStepException("download", $"Vault did not download {file.Name} to {path}.");
        RequireCurrentLocalCopy(file, latest, path);
        return WithDocument(path, doc => ReadProps(doc, props));
    }

    private static WsFile Latest(DataCardFileInfo file)
    {
        var f = VaultFiles.FindLatest(file.VaultPath) ?? throw new DataCardStepException("resolve", $"{file.VaultPath} is no longer in Vault.");
        return f;
    }

    // NoOverwrite download keeps an older local copy; reading or editing it would edit a stale version.
    private static void RequireCurrentLocalCopy(DataCardFileInfo file, WsFile latest, string local)
    {
        if (!System.IO.File.Exists(local)) return;
        if (latest.FileSize > 0 && new System.IO.FileInfo(local).Length != latest.FileSize)
            throw new DataCardSkipException($"The local copy of {file.Name} is not the latest Vault version. Get Latest in Inventor's Vault tab, or delete {local}, then retry.");
    }

    private IReadOnlyDictionary<PropRef, string?> ReadProps(Inv.Document doc, IReadOnlyList<PropRef> props)
    {
        var result = new Dictionary<PropRef, string?>();
        foreach (var p in props)
        {
            try
            {
                var set = doc.PropertySets[p.Set == PropSet.Design ? "Design Tracking Properties" : "Inventor User Defined Properties"];
                result[p] = set[p.Name].Value?.ToString();
            }
            catch (Exception ex) when (ex is COMException or ArgumentException) { result[p] = null; }
        }
        return result;
    }

    // ---- Apply ----

    public long Apply(DataCardFileInfo file, IReadOnlyList<PropChange> changes, string comment)
    {
        var conn = Conn;
        var latest = Latest(file);
        if (latest.CheckedOut) throw new DataCardSkipException($"{file.Name} was checked out while the tool ran.");
        var local = VaultFiles.LocalPathOf(latest);
        var open = DrawingExporter.FindOpen(_app, local);
        if (open is not null && open.Dirty)
            throw new DataCardSkipException($"{file.Name} is open in Inventor with unsaved changes. Save or close it first.");
        if (open is not null) RequireCurrentLocalCopy(file, latest, local);

        // Check out and download. A clean document the user has open keeps its local file (never overwritten underneath it).
        VDF.Vault.Currency.Entities.FileIteration checkedOut;
        string path;
        try { (checkedOut, path) = CheckOut(conn, latest, overwrite: open is null); }
        catch (Exception ex) { throw new DataCardStepException("checkout", ex.Message); }

        var step = "open";
        try
        {
            WithDocument(path, doc =>
            {
                step = "set";
                foreach (var c in changes) SetProp(doc, c);
                step = "save";
                doc.Save();
                return true;
            }, open);
            step = "checkin";
            var assocs = ExistingAssociations(conn, latest);
            var after = conn.FileManager.CheckinFile(checkedOut, comment, false, assocs, null, false, null,
                FileClassification.None, false, new VDF.Currency.FilePathAbsolute(path));
            return conn.WebServiceManager.DocumentService.GetLatestFileByMasterId(after.EntityMasterId).VerNum;
        }
        catch (Exception ex)
        {
            var undone = "the checkout was undone";
            try { conn.FileManager.UndoCheckoutFile(checkedOut, new VDF.Currency.FilePathAbsolute(path)); }
            catch (Exception undoEx) { undone = "the checkout could NOT be undone (" + undoEx.Message + "); undo it in Vault"; }
            throw new DataCardStepException(ex is DataCardStepException se ? se.Step : step, $"{ex.Message} ({undone})");
        }
    }

    private static (VDF.Vault.Currency.Entities.FileIteration, string) CheckOut(VDF.Vault.Currency.Connections.Connection conn, WsFile latest, bool overwrite)
    {
        var settings = new VDF.Vault.Settings.AcquireFilesSettings(conn, false);
        settings.OptionsRelationshipGathering.FileRelationshipSettings.IncludeChildren = false;
        settings.OptionsRelationshipGathering.FileRelationshipSettings.RecurseChildren = false;
        settings.OptionsRelationshipGathering.FileRelationshipSettings.VersionGatheringOption = VDF.Vault.Currency.VersionGatheringOption.Latest;
        settings.OptionsResolution.OverwriteOption = overwrite
            ? VDF.Vault.Settings.AcquireFilesSettings.AcquireFileResolutionOptions.OverwriteOptions.ForceOverwriteAll
            : VDF.Vault.Settings.AcquireFilesSettings.AcquireFileResolutionOptions.OverwriteOptions.NoOverwrite;
        settings.CheckoutComment = "Data card fix";
        var iteration = new VDF.Vault.Currency.Entities.FileIteration(conn, latest);
        settings.AddFileToAcquire(iteration,
            VDF.Vault.Settings.AcquireFilesSettings.AcquisitionOption.Checkout | VDF.Vault.Settings.AcquireFilesSettings.AcquisitionOption.Download);
        var results = conn.FileManager.AcquireFiles(settings);
        var r = results.FileResults.FirstOrDefault() ?? throw new InvalidOperationException("Vault returned no result for the checkout.");
        if (r.Status is not (VDF.Vault.Results.FileAcquisitionResult.AcquisitionStatus.Success or VDF.Vault.Results.FileAcquisitionResult.AcquisitionStatus.Skipped))
            throw new InvalidOperationException($"Vault checkout failed ({r.Status}){(r.Exception is null ? "" : ": " + r.Exception.Message)}.");
        // Skipped can mean the local copy was kept; trust Vault, not the status.
        var now = conn.WebServiceManager.DocumentService.GetLatestFileByMasterId(latest.MasterId);
        if (!now.CheckedOut || now.CkOutUserId != conn.UserID)
            throw new InvalidOperationException("Vault did not check the file out to you.");
        return (r.NewFileIteration ?? new VDF.Vault.Currency.Entities.FileIteration(conn, now), r.LocalPath.FullPath);
    }

    /// <summary>Attachments and dependencies of the version being replaced, so the new version keeps its file associations.</summary>
    private static FileAssocParam[]? ExistingAssociations(VDF.Vault.Currency.Connections.Connection conn, WsFile latest)
    {
        var ds = conn.WebServiceManager.DocumentService;
        var assocs = ds.GetFileAssociationLitesByIds(new[] { latest.Id }, FileAssocAlg.LatestTip,
            FileAssociationTypeEnum.Dependency, false, FileAssociationTypeEnum.Attachment, false, false, false, false) ?? Array.Empty<FileAssocLite>();
        var mine = assocs.Where(a => a.ParFileId == latest.Id).ToList();
        if (mine.Count == 0) return null;
        return mine.Select(a => new FileAssocParam
        {
            CldFileId = a.CldFileId, Typ = a.Typ, RefId = null, Source = null, ExpectedVaultPath = null,
        }).ToArray();
    }

    private static void SetProp(Inv.Document doc, PropChange c)
    {
        if (c.Prop.Set == PropSet.Design)
        {
            doc.PropertySets["Design Tracking Properties"][c.Prop.Name].Value = c.To;
            return;
        }
        var user = doc.PropertySets["Inventor User Defined Properties"];
        try { user[c.Prop.Name].Value = c.To; }
        catch (Exception ex) when (ex is COMException or ArgumentException) { user.Add(c.To, c.Prop.Name); }
    }

    // ---- Inventor document handling ----

    /// <summary>Runs work on the document at path: reuses an open one (never closed), else opens invisibly and closes it again without saving
    /// (callers that save do so inside work). SilentOperation is restored.</summary>
    private T WithDocument<T>(string path, Func<Inv.Document, T> work, Inv.Document? reuse = null)
    {
        var silent = _app.SilentOperation;
        _app.SilentOperation = true;
        Inv.Document? opened = null;
        try
        {
            var doc = reuse ?? DrawingExporter.FindOpen(_app, path);
            if (doc is null)
            {
                try { doc = opened = _app.Documents.Open(path, false); }
                catch (COMException ex) { throw new DataCardStepException("open", $"Inventor could not open {path}: {ex.Message}"); }
            }
            return work(doc);
        }
        finally
        {
            try { opened?.Close(true); } catch (COMException) { }
            try { _app.SilentOperation = silent; } catch (COMException) { }
        }
    }
}
