using System.Runtime.InteropServices;
using Autodesk.Connectivity.WebServices;
using SFVaultBridge.Core;
using VDF = Autodesk.DataManagement.Client.Framework;
using WsFile = Autodesk.Connectivity.WebServices.File;
using Inv = Inventor;

namespace SFVaultBridge.Addin;

/// <summary>
/// Fixes iProperties of existing Vault .ipt parts: check out through the signed-in VDF connection, edit in Inventor, check in a new version.
/// Runs on Inventor's UI thread. Never connects on its own; uses the Vault add-in's session.
/// </summary>
internal sealed class DataCardHost : IDataCardHost
{
    private const string LocalDiffers = "local copy differs from Vault; check in or discard local changes first";

    private readonly Inv.Application _app;
    private readonly Dictionary<long, bool> _releasedByStateId = new();

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

    private DataCardFileInfo Describe(WsFile f)
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
        var released = IsReleased(f, out var unreadable);
        return new DataCardFileInfo(folder + "/" + f.Name, f.Name, VaultFiles.CategoryName(f) ?? "", VaultFiles.StateOf(f),
            released, f.CheckedOut, by, f.VerNum, unreadable);
    }

    // Fails closed: any problem reading the lifecycle state is reported through unreadable and the file is skipped.
    private bool IsReleased(WsFile f, out string? unreadable)
    {
        unreadable = null;
        try
        {
            var id = f.FileLfCyc?.LfCycStateId ?? 0;
            if (id <= 0) { unreadable = "could not read lifecycle state"; return false; }
            if (_releasedByStateId.TryGetValue(id, out var cached)) return cached;
            var state = Conn.WebServiceManager.LifeCycleService.GetLifeCycleStatesByIds(new[] { id })?.FirstOrDefault();
            if (state is null) { unreadable = "could not read lifecycle state"; return false; }
            return _releasedByStateId[id] = state.ReleasedState;
        }
        catch (Exception) { unreadable = "could not read lifecycle state"; return false; }
    }

    // ---- Read (dry run and pre-check) ----

    public IReadOnlyDictionary<PropRef, string?> Read(DataCardFileInfo file, IReadOnlyList<PropRef> props)
    {
        var latest = Latest(file);
        var local = VaultFiles.LocalPathOf(latest);
        var open = DrawingExporter.FindOpen(_app, local);
        if (open is not null && open.Dirty)
            throw new DataCardSkipException($"{file.Name} is open in Inventor with unsaved changes. Save or close it first.");
        var temp = NewTempDir();
        try
        {
            var baseline = DownloadBaseline(latest, temp);
            RequireLocalMatches(local, baseline);
            // Inventor refuses a second same-named document, so a clean open copy (verified identical to Vault) is read in place.
            return open is not null ? ReadProps(open, props) : WithDocument(baseline, doc => ReadProps(doc, props));
        }
        finally { DeleteQuietly(temp); }
    }

    private static WsFile Latest(DataCardFileInfo file) =>
        VaultFiles.FindLatest(file.VaultPath) ?? throw new DataCardStepException("resolve", $"{file.VaultPath} is no longer in Vault.");

    private static string NewTempDir()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sfvb-datacard-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteQuietly(string dir)
    {
        try { System.IO.Directory.Delete(dir, true); } catch (Exception) { }
    }

    /// <summary>Downloads Vault's copy of the latest version (no children) into a private temp folder; the user's working copy is never touched.</summary>
    private static string DownloadBaseline(WsFile latest, string tempDir)
    {
        try
        {
            var conn = Conn;
            var settings = new VDF.Vault.Settings.AcquireFilesSettings(conn, false);
            settings.LocalPath = new VDF.Currency.FolderPathAbsolute(tempDir);
            settings.OptionsRelationshipGathering.FileRelationshipSettings.IncludeChildren = false;
            settings.OptionsRelationshipGathering.FileRelationshipSettings.RecurseChildren = false;
            settings.OptionsRelationshipGathering.FileRelationshipSettings.VersionGatheringOption = VDF.Vault.Currency.VersionGatheringOption.Latest;
            settings.OptionsResolution.OverwriteOption = VDF.Vault.Settings.AcquireFilesSettings.AcquireFileResolutionOptions.OverwriteOptions.ForceOverwriteAll;
            settings.AddFileToAcquire(new VDF.Vault.Currency.Entities.FileIteration(conn, latest), VDF.Vault.Settings.AcquireFilesSettings.AcquisitionOption.Download);
            var r = conn.FileManager.AcquireFiles(settings).FileResults.FirstOrDefault()
                    ?? throw new InvalidOperationException("Vault returned no result for the download.");
            if (r.Status != VDF.Vault.Results.FileAcquisitionResult.AcquisitionStatus.Success)
                throw new InvalidOperationException($"Vault download failed ({r.Status}){(r.Exception is null ? "" : ": " + r.Exception.Message)}.");
            var path = r.LocalPath.FullPath;
            if (!System.IO.File.Exists(path)) throw new InvalidOperationException($"Vault did not write {path}.");
            return path;
        }
        catch (Exception ex) when (ex is not DataCardStepException) { throw new DataCardStepException("download", ex.Message); }
    }

    /// <summary>A local working copy that is not byte-identical to Vault's means the user has local edits (or an older copy); never read or overwrite it.</summary>
    private static void RequireLocalMatches(string local, string baseline)
    {
        if (!System.IO.File.Exists(local)) return;
        if (!SameBytes(local, baseline)) throw new DataCardSkipException(LocalDiffers);
    }

    private static bool SameBytes(string a, string b)
    {
        try
        {
            using var fa = new System.IO.FileStream(a, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete);
            using var fb = new System.IO.FileStream(b, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete);
            if (fa.Length != fb.Length) return false;
            var ba = new byte[81920];
            var bb = new byte[81920];
            int n;
            while ((n = fa.Read(ba, 0, ba.Length)) > 0)
            {
                var got = 0;
                while (got < n) { var m = fb.Read(bb, got, n - got); if (m <= 0) return false; got += m; }
                if (!ba.AsSpan(0, n).SequenceEqual(bb.AsSpan(0, n))) return false;
            }
            return true;
        }
        catch (Exception) { return false; }
    }

    private static IReadOnlyDictionary<PropRef, string?> ReadProps(Inv.Document doc, IReadOnlyList<PropRef> props)
    {
        var result = new Dictionary<PropRef, string?>();
        foreach (var p in props)
        {
            try
            {
                var set = doc.PropertySets[p.Set == PropSet.Design ? "Design Tracking Properties" : "Inventor User Defined Properties"];
                result[p] = set[p.Name].Value?.ToString();
            }
            catch (Exception ex) when (ex is COMException or ArgumentException)
            {
                // A custom property that does not exist yet reads as missing; an unknown Design property is a typo, so fail before any checkout.
                if (p.Set == PropSet.Design) throw new DataCardStepException("read", $"Design Tracking property '{p.Name}' could not be read: {ex.Message}");
                result[p] = null;
            }
        }
        return result;
    }

    // ---- Apply ----

    public DataCardApplied Apply(DataCardFileInfo file, IReadOnlyList<PropChange> changes, string comment)
    {
        var conn = Conn;
        var ds = conn.WebServiceManager.DocumentService;
        var latest = Latest(file);
        var again = Describe(latest);
        var skip = DataCardFix.SkipReason(again);
        if (skip is not null) throw new DataCardSkipException(skip);
        if (again.Version != file.Version)
            throw new DataCardSkipException($"{file.Name} changed in Vault since the preview (version {file.Version} to {again.Version}). Run it again.");

        var local = VaultFiles.LocalPathOf(latest);
        var open = DrawingExporter.FindOpen(_app, local);
        if (open is not null && open.Dirty)
            throw new DataCardSkipException($"{file.Name} is open in Inventor with unsaved changes. Save or close it first.");
        var localExists = System.IO.File.Exists(local);
        if (localExists)
        {
            var temp = NewTempDir();
            try { RequireLocalMatches(local, DownloadBaseline(latest, temp)); }
            finally { DeleteQuietly(temp); }
        }

        VDF.Vault.Currency.Entities.FileIteration? checkedOut = null;
        var path = local;
        var step = "checkout";
        try
        {
            // The local copy is absent or identical to Vault here, so overwriting it is safe, except under a document the user has open.
            (checkedOut, path) = CheckOut(conn, latest, overwrite: open is null);
            ClearReadOnly(path);
            step = "open";
            WithDocument(path, doc =>
            {
                step = "set";
                foreach (var c in changes) SetProp(doc, c);
                step = "save";
                doc.Save();
                return true;
            }, open);
            step = "checkin";
            var assocs = ChildAssociations(conn, latest);
            var after = conn.FileManager.CheckinFile(checkedOut, comment, false, assocs, null, false, null,
                FileClassification.None, false, new VDF.Currency.FilePathAbsolute(path));
            return ReadBack(ds, after.EntityMasterId);
        }
        catch (Exception ex)
        {
            var undone = UndoIfStillMine(conn, latest.MasterId, path);
            var inventorNote = open is not null ? " The open Inventor document still holds the edited values; close it without saving." : "";
            throw new DataCardStepException(ex is DataCardStepException se ? se.Step : step, $"{ex.Message} ({undone}){inventorNote}");
        }
    }

    // After a successful check-in nothing may report a failure or undo anything.
    private static DataCardApplied ReadBack(DocumentService ds, long masterId)
    {
        try { return new DataCardApplied(ds.GetLatestFileByMasterId(masterId).VerNum); }
        catch (Exception ex) { return new DataCardApplied(-1, $"Checked in, but the new version number could not be read: {ex.Message}"); }
    }

    private static void ClearReadOnly(string path)
    {
        try
        {
            var attrs = System.IO.File.GetAttributes(path);
            if ((attrs & System.IO.FileAttributes.ReadOnly) != 0) System.IO.File.SetAttributes(path, attrs & ~System.IO.FileAttributes.ReadOnly);
        }
        catch (Exception) { }
    }

    /// <summary>Undoes a checkout only when Vault still shows the file checked out to this user (so a completed check-in is never touched).</summary>
    private static string UndoIfStillMine(VDF.Vault.Currency.Connections.Connection conn, long masterId, string localPath)
    {
        try
        {
            var ds = conn.WebServiceManager.DocumentService;
            var now = ds.GetLatestFileByMasterId(masterId);
            if (!now.CheckedOut || now.CkOutUserId != conn.UserID) return "nothing was left checked out";
            try
            {
                conn.FileManager.UndoCheckoutFile(new VDF.Vault.Currency.Entities.FileIteration(conn, now), new VDF.Currency.FilePathAbsolute(localPath));
            }
            catch (Exception)
            {
                ds.UndoCheckoutFile(now.Id, out _);
            }
            return "the checkout was undone";
        }
        catch (Exception undoEx) { return "the checkout could NOT be undone (" + undoEx.Message + "); undo it in Vault"; }
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

    /// <summary>Every child Dependency and Attachment of the version being replaced, with the original reference ids, so the new version keeps them.</summary>
    private static FileAssocParam[]? ChildAssociations(VDF.Vault.Currency.Connections.Connection conn, WsFile latest)
    {
        var ds = conn.WebServiceManager.DocumentService;
        var lites = ds.GetFileAssociationLitesByIds(new[] { latest.Id }, FileAssocAlg.Actual,
            FileAssociationTypeEnum.None, false, FileAssociationTypeEnum.All, false, false, true, false) ?? Array.Empty<FileAssocLite>();
        var rows = lites.Select(a => new AssocRow(a.ParFileId, a.CldFileId, a.Typ.ToString(), a.RefId, a.Source, a.ExpectedVaultPath));
        var mine = DataCardFix.ChildAssociations(rows, latest.Id);
        if (mine.Count == 0) return null;
        return mine.Select(a => new FileAssocParam
        {
            CldFileId = a.CldFileId,
            Typ = a.Type == "Attachment" ? AssociationType.Attachment : AssociationType.Dependency,
            RefId = a.RefId, Source = a.Source, ExpectedVaultPath = a.ExpectedVaultPath,
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
            var doc = reuse;
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
