using Autodesk.Connectivity.WebServices;
using SFVaultBridge.Core;
using VDF = Autodesk.DataManagement.Client.Framework;
using Inv = Inventor;
using WsFile = Autodesk.Connectivity.WebServices.File;

namespace SFVaultBridge.Addin;

/// <summary>
/// The Vault and Inventor side of the check-out, check-in, undo and state-change tools. Runs on Inventor's UI thread on the Vault add-in's session.
/// Reuses DataCardHost's checkout, association carry-forward (ChildAssociations + ChooseOnDisk), undo and lifecycle lookup, which already run live
/// for the data-card fix and I Approve!.
/// </summary>
internal sealed class VaultFileOpsHost : IVaultFileOps
{
    private static readonly string[] InventorExtensions = { ".ipt", ".iam", ".idw", ".dwg", ".ipn" };

    private readonly Inv.Application _app;
    private readonly DataCardHost _card;
    private readonly Action<string> _log;
    private readonly Dictionary<long, string> _lifecycleNames = new();

    public VaultFileOpsHost(Inv.Application app, DataCardHost card, Action<string> log) { _app = app; _card = card; _log = log; }

    private static VDF.Vault.Currency.Connections.Connection Conn => VaultSession.Connection;

    private static void RequireSignedIn()
    {
        if (!VaultSession.IsSignedIn) throw new BridgeException("not_signed_in", BridgeOps.NotSignedInMessage);
    }

    // ---- facts ----

    private FileFacts Describe(WsFile f, bool withLocal)
    {
        var info = _card.Describe(f);
        var local = "";
        try { local = VaultFiles.LocalPathOf(f); } catch (Exception) { }
        var exists = local.Length > 0 && System.IO.File.Exists(local);
        var open = local.Length > 0 ? DocumentOpener.FindOpen(_app, local) : null;
        var dirty = false;
        try { dirty = open?.Dirty ?? false; } catch (Exception) { }
        var differs = false;
        if (withLocal && exists)
        {
            var temp = DataCardHost.NewTempDir();
            try { differs = !DataCardHost.SameBytes(local, DataCardHost.DownloadBaseline(f, temp)); }
            catch (Exception ex) { _log($"fileops: could not compare {f.Name} with Vault: {ex.Message}"); differs = true; }
            finally { DataCardHost.DeleteQuietly(temp); }
        }
        var ext = System.IO.Path.GetExtension(f.Name).ToLowerInvariant();
        return new FileFacts(info.VaultPath, f.Name, f.MasterId, f.VerNum, info.Category, LifecycleName(f), info.State, info.Released, info.Revision,
                             f.CheckedOut, info.CheckedOutBy, f.CheckedOut && f.CkOutUserId == Conn.UserID, local, exists, differs, open is not null, dirty,
                             InventorExtensions.Contains(ext));
    }

    private string LifecycleName(WsFile f)
    {
        var id = f.FileLfCyc?.LfCycDefId ?? 0;
        if (id <= 0) return "";
        if (_lifecycleNames.TryGetValue(id, out var n)) return n;
        try
        {
            var d = Conn.WebServiceManager.LifeCycleService.GetLifeCycleDefinitionsByIds(new[] { id })?.FirstOrDefault();
            return _lifecycleNames[id] = d?.DispName ?? d?.Name ?? "";
        }
        catch (Exception) { return ""; }
    }

    private static WsFile Latest(long masterId) =>
        Conn.WebServiceManager.DocumentService.GetLatestFileByMasterId(masterId) is { Id: > 0 } f ? f : throw new BridgeException("not_found", "The file is no longer in Vault.");

    public FileLookup Lookup(string fileRef)
    {
        RequireSignedIn();
        IEnumerable<WsFile> found;
        if (fileRef.StartsWith("$", StringComparison.Ordinal))
        {
            var f = VaultFiles.FindLatest(fileRef);
            found = f is null ? Array.Empty<WsFile>() : new[] { f };
        }
        else
        {
            found = VaultFiles.FindByNameContaining(fileRef)
                .Where(f => string.Equals(f.Name, fileRef, StringComparison.OrdinalIgnoreCase))
                .GroupBy(f => f.MasterId).Select(g => Latest(g.Key)).ToList();
        }
        return new FileLookup(fileRef, found.Select(f => Describe(f, withLocal: true)).ToList());
    }

    public FileFacts Refresh(long masterId)
    {
        RequireSignedIn();
        return Describe(Latest(masterId), withLocal: true);
    }

    public IReadOnlyList<FileFacts> Children(FileFacts f)
    {
        RequireSignedIn();
        var ds = Conn.WebServiceManager.DocumentService;
        var latest = Latest(f.MasterId);
        var lites = ds.GetFileAssociationLitesByIds(new[] { latest.Id }, FileAssocAlg.Actual,
            FileAssociationTypeEnum.None, false, FileAssociationTypeEnum.Dependency, false, true, false, false) ?? Array.Empty<FileAssocLite>();
        var ids = lites.Where(a => a.ParFileId == latest.Id).Select(a => a.CldFileId).Distinct().ToArray();
        if (ids.Length == 0) return Array.Empty<FileFacts>();
        var masters = (ds.GetFilesByIds(ids) ?? Array.Empty<WsFile>()).Select(x => x.MasterId).Distinct().ToArray();
        return (ds.GetLatestFilesByMasterIds(masters) ?? Array.Empty<WsFile>()).Where(x => x.Id > 0).Select(c => Describe(c, withLocal: false)).ToList();
    }

    // ---- check out ----

    public OpResult Checkout(FileFacts f, string comment, bool replaceLocal, bool reopenInInventor)
    {
        RequireSignedIn();
        var latest = Latest(f.MasterId);
        var local = VaultFiles.LocalPathOf(latest);
        var open = DocumentOpener.FindOpen(_app, local);
        // A document the user has open is never overwritten underneath Inventor; a differing local copy is kept unless the plan said replace.
        var overwrite = replaceLocal && open is null;
        string path;
        try { (_, path) = DataCardHost.CheckOut(Conn, latest, overwrite, comment); }
        catch (Exception ex) { return new OpResult(false, $"Vault checkout failed: {ex.Message}"); }
        DataCardHost.ClearReadOnly(path);
        var notes = new List<string>();
        if (open is not null) notes.Add(WritableInInventor(open, local, reopenInInventor));
        var now = Latest(f.MasterId);
        _log($"fileops: checked out {f.VaultPath} (version {now.VerNum}) to {path}");
        return new OpResult(true, $"Checked out to {path}", now.VerNum, Notes: notes);
    }

    /// <summary>Inventor keeps a document it opened from a read-only file read-only even after the file is writable. A clean document is closed and
    /// reopened (and re-activated if it was the active one); if an open assembly keeps it loaded, say so.</summary>
    private string WritableInInventor(Inv.Document open, string local, bool reopen)
    {
        const string manual = "Close it in Inventor (and the assembly that references it) and reopen it so Inventor treats it as writable.";
        if (!reopen) return "Open in Inventor and still read-only there. " + manual;
        var silent = _app.SilentOperation;
        try
        {
            if (open.Dirty) return "Open in Inventor with unsaved changes, so it was not reopened. " + manual;
            var wasActive = false;
            try { wasActive = string.Equals(_app.ActiveDocument?.FullFileName, local, StringComparison.OrdinalIgnoreCase); } catch (Exception) { }
            _app.SilentOperation = true;
            open.Close(true);
            var doc = _app.Documents.Open(local, true);
            if (wasActive) { try { doc.Activate(); } catch (Exception) { } }
            return doc.IsModifiable ? "Reopened in Inventor; it is writable." : "Reopened, but Inventor still treats it as read-only (an open assembly keeps it loaded). " + manual;
        }
        catch (Exception ex)
        {
            _log($"fileops: reopen of {local} failed: {ex.Message}");
            return $"Could not reopen it in Inventor ({ex.Message}). " + manual;
        }
        finally { try { _app.SilentOperation = silent; } catch (Exception) { } }
    }

    // ---- check in ----

    public CheckinFacts CheckinPreflight(FileFacts f)
    {
        RequireSignedIn();
        var notes = new List<string>();
        try
        {
            var latest = Latest(f.MasterId);
            var assocs = Associations(latest);
            var newRefs = new List<string>();
            if (f.IsInventorFile)
            {
                var open = DocumentOpener.FindOpen(_app, f.LocalPath);
                if (open is not null) newRefs.AddRange(NewReferences(open, latest, assocs));
                else notes.Add("closed in Inventor: references added since the checkout cannot be detected; the previous version's associations are carried forward.");
            }
            if (assocs is { Length: > 0 }) notes.Add($"{assocs.Length} association(s) from the previous version are carried forward.");
            return new CheckinFacts(!f.LocalDiffers, null, newRefs, assocs?.Length ?? 0, notes);
        }
        catch (DataCardStepException ex) { return new CheckinFacts(false, ex.Message, Array.Empty<string>(), 0, notes); }
        catch (BridgeException) { throw; }
        catch (Exception ex) { return new CheckinFacts(false, "could not read the file's associations: " + ex.Message, Array.Empty<string>(), 0, notes); }
    }

    /// <summary>The previous version's associations, each Dependency child bound to the version matching the file on disk (nothing touched = no run-local versions).</summary>
    private static FileAssocParam[]? Associations(WsFile latest) =>
        DataCardHost.ChildAssociations(Conn, latest, rows => DataCardHost.ChooseOnDisk(Conn, rows, new HashSet<long>()));

    /// <summary>File names the open document references directly that the checked-out version has no Dependency child for.</summary>
    private static IEnumerable<string> NewReferences(Inv.Document open, WsFile latest, FileAssocParam[]? assocs)
    {
        var ds = Conn.WebServiceManager.DocumentService;
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var childIds = (assocs ?? Array.Empty<FileAssocParam>()).Where(a => a.Typ == AssociationType.Dependency).Select(a => a.CldFileId).Distinct().ToArray();
        if (childIds.Length > 0)
            foreach (var c in ds.GetFilesByIds(childIds) ?? Array.Empty<WsFile>()) known.Add(c.Name);
        var fresh = new List<string>();
        foreach (Inv.DocumentDescriptor d in open.ReferencedDocumentDescriptors)
        {
            string name;
            try { name = System.IO.Path.GetFileName(d.FullDocumentName ?? ""); } catch (Exception) { continue; }
            if (name.Length > 0 && !known.Contains(name) && !fresh.Contains(name, StringComparer.OrdinalIgnoreCase)) fresh.Add(name);
        }
        return fresh;
    }

    public OpResult Checkin(FileFacts f, string comment, bool keepCheckedOut)
    {
        RequireSignedIn();
        var conn = Conn;
        var latest = Latest(f.MasterId);
        if (!latest.CheckedOut || latest.CkOutUserId != conn.UserID) return new OpResult(false, "The file is no longer checked out to you.");
        var local = VaultFiles.LocalPathOf(latest);
        if (DocumentOpener.FindOpen(_app, local) is { Dirty: true }) return new OpResult(false, "It is open in Inventor with unsaved changes. Save it first.");
        FileAssocParam[]? assocs;
        try { assocs = Associations(latest); }
        catch (DataCardStepException ex) { return new OpResult(false, "Not checked in. " + ex.Message); }
        var iteration = new VDF.Vault.Currency.Entities.FileIteration(conn, latest);
        try
        {
            conn.FileManager.CheckinFile(iteration, comment, keepCheckedOut, assocs, null, false, null, FileClassification.None, false,
                                         new VDF.Currency.FilePathAbsolute(local));
        }
        catch (Exception ex) { return new OpResult(false, $"Vault refused the check-in (the file is still checked out to you): {ex.Message}"); }
        // The file is in Vault from here on; nothing below may report a failed check-in.
        var notes = new List<string>();
        long version = -1;
        var revision = "";
        try { var now = Latest(f.MasterId); version = now.VerNum; revision = VaultFiles.RevisionOf(now); }
        catch (Exception ex) { notes.Add($"Checked in, but the new version could not be read back: {ex.Message}"); }
        if (!keepCheckedOut && DocumentOpener.FindOpen(_app, local) is not null)
            notes.Add("Still open in Inventor; the file is read-only on disk now. Check it out again to keep editing.");
        _log($"fileops: checked in {f.VaultPath} as version {version} with {assocs?.Length ?? 0} associations");
        return new OpResult(true, $"Checked in with {assocs?.Length ?? 0} association(s) carried forward", Math.Max(version, 0), Revision: revision, Notes: notes);
    }

    // ---- undo ----

    public OpResult UndoCheckout(FileFacts f)
    {
        RequireSignedIn();
        var conn = Conn;
        var latest = Latest(f.MasterId);
        var local = VaultFiles.LocalPathOf(latest);
        var how = DataCardHost.UndoIfStillMine(conn, f.MasterId, local);
        var now = Latest(f.MasterId);
        if (now.CheckedOut) return new OpResult(false, $"Still checked out ({how}).");
        var notes = new List<string>();
        var open = DocumentOpener.FindOpen(_app, local) is not null;
        if (open) notes.Add("Open in Inventor: close it without saving; Inventor's copy is stale.");
        else if (System.IO.File.Exists(local))
        {
            var temp = DataCardHost.NewTempDir();
            try
            {
                if (!DataCardHost.SameBytes(local, DataCardHost.DownloadBaseline(now, temp)))
                {
                    VaultFiles.GetLatestOverwrite(new[] { now });
                    notes.Add("The working copy still had local edits after the undo; it was replaced with the Vault version.");
                }
            }
            catch (Exception ex) { notes.Add($"Could not confirm the working copy matches Vault: {ex.Message}"); }
            finally { DataCardHost.DeleteQuietly(temp); }
        }
        _log($"fileops: undid checkout of {f.VaultPath}");
        return new OpResult(true, "Checkout undone", now.VerNum, Notes: notes);
    }

    // ---- state ----

    public StateFacts PlanState(FileFacts f, string targetState)
    {
        RequireSignedIn();
        try
        {
            var latest = Latest(f.MasterId);
            if ((latest.FileLfCyc?.LfCycDefId ?? 0) <= 0)
                return new StateFacts("", "", false, null, false, false, false, false, false, false, "", Array.Empty<string>(), "has no lifecycle, so it has no state to change");
            var def = DataCardHost.Definition(latest);
            var states = def.StateArray ?? Array.Empty<LfCycState>();
            var curId = latest.FileLfCyc!.LfCycStateId;
            var cur = states.FirstOrDefault(s => s.Id == curId);
            var curName = cur is null ? latest.FileLfCyc.LfCycStateName ?? "" : DataCardHost.Shown(cur);
            var lifecycle = def.DispName ?? def.Name ?? "";
            var to = DataCardHost.StateNamed(def, targetState);
            var trans = def.TransArray ?? Array.Empty<LfCycTrans>();
            if (to is null)
                return new StateFacts(lifecycle, curName, cur?.ReleasedState ?? false, null, false, false, false, false, false, false, "",
                                      states.Where(s => s.Id != curId).Select(DataCardHost.Shown).ToList(), null);
            var reachable = trans.Where(t => t.FromId == curId).Select(t => states.FirstOrDefault(s => s.Id == t.ToId)).Where(s => s is not null).Select(s => DataCardHost.Shown(s!)).ToList();
            var t0 = trans.FirstOrDefault(t => t.FromId == curId && t.ToId == to.Id);
            var allowed = false;
            if (t0 is not null)
            {
                var ids = Conn.WebServiceManager.DocumentServiceExtensions.GetAllowedFileLifeCycleStateTransitionIds();
                allowed = ids is not null && ids.Contains(t0.Id);
            }
            var childCheck = t0 is not null && (t0.CldState & EnforceChildStateEnum.EnforceChildFiles) != 0;
            return new StateFacts(lifecycle, curName, cur?.ReleasedState ?? false, DataCardHost.Shown(to), true, to.ReleasedState, to.Id == curId,
                                  t0 is not null, allowed, childCheck, t0 is null ? "" : RevisionAfter(latest, t0.Bump), reachable, null);
        }
        catch (BridgeException) { throw; }
        catch (Exception ex) { return new StateFacts("", "", false, null, false, false, false, false, false, false, "", Array.Empty<string>(), "could not read the lifecycle: " + ex.Message); }
    }

    private string RevisionAfter(WsFile f, BumpRevisionEnum bump)
    {
        var kind = bump switch
        {
            BumpRevisionEnum.None => RevisionBump.None,
            BumpRevisionEnum.BumpPrimary => RevisionBump.Primary,
            BumpRevisionEnum.BumpSecondary => RevisionBump.Secondary,
            BumpRevisionEnum.BumpTertiary => RevisionBump.Tertiary,
            _ => RevisionBump.Unknown,
        };
        IReadOnlyList<string>? next = null;
        if (bump != BumpRevisionEnum.None)
        {
            try
            {
                var defId = f.FileRev?.RevDefId ?? 0;
                if (defId > 0) next = Conn.WebServiceManager.RevisionService.GetNextRevisionNumbersByMasterIds(new[] { f.MasterId }, new[] { defId })?.FirstOrDefault()?.Items;
            }
            catch (Exception ex) { _log($"fileops: next revision of {f.Name} unreadable: {ex.Message}"); }
        }
        return ReleasePlan.RevisionAfter(kind, VaultFiles.RevisionOf(f), next);
    }

    public OpResult ChangeState(FileFacts f, string targetState, string comment)
    {
        RequireSignedIn();
        try
        {
            var info = _card.Describe(Latest(f.MasterId));
            var r = _card.ChangeState(info, targetState, comment);
            _log($"fileops: {f.VaultPath} {f.State} -> {r.State} (revision {r.Revision})");
            return new OpResult(true, "", 0, r.State, r.Revision);
        }
        catch (DataCardStepException ex)
        {
            RequireSignedIn();
            return new OpResult(false, ex.Message);
        }
    }
}
