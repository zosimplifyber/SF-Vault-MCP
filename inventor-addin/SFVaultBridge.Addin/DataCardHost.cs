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
            released, f.CheckedOut, by, f.VerNum, unreadable, VaultFiles.RevisionOf(f));
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
        try
        {
            // Vault downloads are read-only; clear the flag so the folder can be removed.
            foreach (var f in System.IO.Directory.EnumerateFiles(dir, "*", System.IO.SearchOption.AllDirectories))
                System.IO.File.SetAttributes(f, System.IO.FileAttributes.Normal);
            System.IO.Directory.Delete(dir, true);
        }
        catch (Exception) { }
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
            // NoOverwrite: the temp folder is empty, and if Vault ever ignored LocalPath it must not replace the user's working copy.
            settings.OptionsResolution.OverwriteOption = VDF.Vault.Settings.AcquireFilesSettings.AcquireFileResolutionOptions.OverwriteOptions.NoOverwrite;
            settings.AddFileToAcquire(new VDF.Vault.Currency.Entities.FileIteration(conn, latest), VDF.Vault.Settings.AcquireFilesSettings.AcquisitionOption.Download);
            var r = conn.FileManager.AcquireFiles(settings).FileResults.FirstOrDefault()
                    ?? throw new InvalidOperationException("Vault returned no result for the download.");
            if (r.Status != VDF.Vault.Results.FileAcquisitionResult.AcquisitionStatus.Success)
                throw new InvalidOperationException($"Vault download failed ({r.Status}){(r.Exception is null ? "" : ": " + r.Exception.Message)}.");
            var path = r.LocalPath.FullPath;
            var root = System.IO.Path.GetFullPath(tempDir).TrimEnd('\\') + "\\";
            if (!System.IO.Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Vault downloaded the baseline to {path} instead of the temp folder; stopped before touching the file.");
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
                var set = doc.PropertySets[DataCardFix.SetName(p.Set)];
                result[p] = ValueText(set[p.Name].Value);
            }
            catch (Exception ex) when (ex is COMException or ArgumentException)
            {
                // A custom property that does not exist yet reads as missing; an unknown Design property is a typo, so fail before any checkout.
                if (p.Set != PropSet.Custom) throw new DataCardStepException("read", $"{DataCardFix.SetName(p.Set)} property '{p.Name}' could not be read: {ex.Message}");
                result[p] = null;
            }
        }
        return result;
    }

    // Invariant text for numbers and dates so "12.5" is never "12,5" on a machine with another culture.
    private static string? ValueText(object? v) => v switch
    {
        null => null,
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => v.ToString(),
    };

    // ---- Revise: lifecycle ----

    private static string StateName(LfCycDef def, long id) => def.StateArray?.FirstOrDefault(s => s.Id == id)?.Name ?? "";

    private static LfCycState? StateNamed(LfCycDef def, string name) =>
        def.StateArray?.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    private static LfCycDef Definition(WsFile f)
    {
        var defId = f.FileLfCyc?.LfCycDefId ?? 0;
        if (defId <= 0) throw new InvalidOperationException("could not read the file's lifecycle definition");
        return Conn.WebServiceManager.LifeCycleService.GetLifeCycleDefinitionsByIds(new[] { defId })?.FirstOrDefault()
               ?? throw new InvalidOperationException("could not read the file's lifecycle definition");
    }

    /// <summary>The transition from the file's current state to the state with that name, by NAME in its own lifecycle definition (no ids are
    /// hard-coded), and only when the signed-in user is allowed to take it. Null Problem means the move is available.</summary>
    private static (long? ToStateId, string? Problem) Transition(WsFile f, string toName)
    {
        try
        {
            var def = Definition(f);
            var fromId = f.FileLfCyc?.LfCycStateId ?? 0;
            var fromName = StateName(def, fromId);
            var to = StateNamed(def, toName);
            var trans = to is null ? null : def.TransArray?.FirstOrDefault(t => t.FromId == fromId && t.ToId == to.Id);
            if (trans is null) return (null, DataCardFix.NoTransition(fromName.Length > 0 ? fromName : "the current state", toName));
            var allowed = Conn.WebServiceManager.DocumentServiceExtensions.GetAllowedFileLifeCycleStateTransitionIds();
            if (allowed is null || !allowed.Contains(trans.Id))
                return (null, $"you are not allowed to change the state from {fromName} to {toName}");
            return (to!.Id, null);
        }
        catch (Exception ex) { return (null, "could not read the lifecycle transitions: " + ex.Message); }
    }

    public RevisePlan PlanRevise(DataCardFileInfo file)
    {
        try
        {
            var latest = Latest(file);
            var (_, problem) = Transition(latest, DataCardFix.WorkInProgressState);
            if (problem is not null) return new RevisePlan(problem);
            // The way back is checked from Work in Progress in the same definition, before anything is changed.
            var def = Definition(latest);
            var wip = StateNamed(def, DataCardFix.WorkInProgressState)!;
            var rel = StateNamed(def, DataCardFix.ReleasedState);
            var back = rel is null ? null : def.TransArray?.FirstOrDefault(t => t.FromId == wip.Id && t.ToId == rel.Id);
            if (back is null) return new RevisePlan(DataCardFix.NoTransition(DataCardFix.WorkInProgressState, DataCardFix.ReleasedState));
            var allowed = Conn.WebServiceManager.DocumentServiceExtensions.GetAllowedFileLifeCycleStateTransitionIds();
            if (allowed is null || !allowed.Contains(back.Id))
                return new RevisePlan($"you are not allowed to change the state from {DataCardFix.WorkInProgressState} to {DataCardFix.ReleasedState}");
            return new RevisePlan(null, $"{StateName(def, latest.FileLfCyc!.LfCycStateId)} -> {wip.Name}", $"{wip.Name} -> {rel!.Name}");
        }
        catch (Exception ex) { return new RevisePlan("could not plan the revision: " + ex.Message); }
    }

    // UpdateFileLifeCycleStates(masterIds, toStateIds, comment) on the DocumentServiceExtensions web service: it takes the target STATE id and
    // Vault checks the transition itself; the transition is also verified here from the definition. The result is read back, never assumed.
    public DataCardStateChange ChangeState(DataCardFileInfo file, string toStateName, string comment)
    {
        var latest = Latest(file);
        var (toId, problem) = Transition(latest, toStateName);
        if (problem is not null || toId is null) throw new DataCardStepException("revise", problem ?? DataCardFix.NoTransition(latest.FileLfCyc?.LfCycStateName ?? "", toStateName));
        try
        {
            Conn.WebServiceManager.DocumentServiceExtensions.UpdateFileLifeCycleStates(new[] { latest.MasterId }, new[] { toId.Value }, comment);
        }
        catch (Exception ex) { throw new DataCardStepException("revise", $"Vault refused the change to {toStateName}: {ex.Message}"); }
        var now = Conn.WebServiceManager.DocumentService.GetLatestFileByMasterId(latest.MasterId);
        var state = now?.FileLfCyc?.LfCycStateName ?? "";
        if (!string.Equals(state, toStateName, StringComparison.OrdinalIgnoreCase))
            throw new DataCardStepException("revise", $"Vault reports the state as '{state}' after the change to {toStateName}.");
        return new DataCardStateChange(state, VaultFiles.RevisionOf(now));
    }

    public bool? LinkedToItem(DataCardFileInfo file)
    {
        try
        {
            var latest = Latest(file);
            var items = Conn.WebServiceManager.ItemService.GetItemsByFileId(latest.Id);
            return items is { Length: > 0 };
        }
        catch (Exception) { return null; }
    }

    public string? ReadPropertyCompliance(DataCardFileInfo file)
    {
        try
        {
            var latest = Latest(file);
            var ps = Conn.WebServiceManager.PropertyService;
            var def = ps.GetPropertyDefinitionsByEntityClassId("FILE")?
                .FirstOrDefault(d => string.Equals(d.DispName, "Property Compliance", StringComparison.OrdinalIgnoreCase));
            if (def is null) return null;
            var inst = ps.GetPropertiesByEntityIds("FILE", new[] { latest.Id })?.FirstOrDefault(i => i.PropDefId == def.Id);
            return inst?.Val?.ToString();
        }
        catch (Exception) { return null; }
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
        if (c.Prop.Set == PropSet.Summary)
        {
            doc.PropertySets[DataCardFix.SetName(PropSet.Summary)][c.Prop.Name].Value = c.To;
            return;
        }
        if (c.Prop.Set == PropSet.Design)
        {
            var prop = doc.PropertySets["Design Tracking Properties"][c.Prop.Name];
            // Number-typed properties (Cost) take a double, converted with the invariant culture; Core already rejected non-numbers.
            if (DataCardFix.IsNumeric(c.Prop) || prop.Value is double or decimal or float)
            {
                if (!DataCardFix.TryParseNumber(c.To, out var d)) throw new DataCardStepException("set", $"{c.Prop.Name} must be a number");
                prop.Value = d;
            }
            else prop.Value = c.To;
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
