using Autodesk.Connectivity.WebServices;
using SFVaultBridge.Core;
using Inv = Inventor;
using WsFile = Autodesk.Connectivity.WebServices.File;

namespace SFVaultBridge.Addin;

/// <summary>
/// The Vault and Inventor side of I Approve!: preview reads (drawing pick, lifecycle transition with its child-state control and revision bump,
/// state released flags of the children, compliance, open and local-copy state) and the writes (Get Latest of the tree, sign-off through
/// DataCardHost.SignOff, state change through DataCardHost.ChangeState). Runs on Inventor's UI thread on the Vault add-in's session.
/// </summary>
internal sealed class ReleaseHost : IReleaseWriter
{
    private readonly Inv.Application _app;
    private readonly DataCardHost _card;
    private readonly Action<string> _log;
    private readonly string _root;
    private readonly IReadOnlyList<string> _tree;
    private readonly Dictionary<string, WsFile?> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, WsFile> _children = new();
    private readonly HashSet<long> _touched = new();
    private long? _designerDefId;
    private bool _designerDefRead;

    /// <param name="tree">Every file in the assembly's BOM (purchased and Content Center included) and the assembly itself, for Get Latest.</param>
    public ReleaseHost(Inv.Application app, DataCardHost card, Action<string> log, string workingRoot, IReadOnlyList<string> tree)
    {
        _app = app; _card = card; _log = log; _root = workingRoot; _tree = tree;
    }

    private static Autodesk.DataManagement.Client.Framework.Vault.Currency.Connections.Connection Conn => VaultSession.Connection;

    /// <summary>Workspace paths are compared after GetFullPath, case-insensitive.</summary>
    public static string Key(string path)
    {
        try { return System.IO.Path.GetFullPath(path); } catch { return path; }
    }

    private string VaultPathOf(string key) => VaultPaths.ToVaultFolder(key, _root) + "/" + System.IO.Path.GetFileName(key);

    private WsFile? Resolve(string key)
    {
        key = Key(key);
        if (_files.TryGetValue(key, out var f)) return f;
        return _files[key] = VaultFiles.FindLatest(VaultPathOf(key));
    }

    private static void RequireSignedIn()
    {
        if (!VaultSession.IsSignedIn) throw new BridgeException("not_signed_in", BridgeOps.NotSignedInMessage);
    }

    // ---- Preview ----

    /// <summary>The drawing the packages would pick for the model (DrawingFinder.PartDrawing), or null. Throws on a Vault failure.</summary>
    public ReleaseDrawingRef? FindDrawing(string modelKey)
    {
        var model = Resolve(modelKey);
        if (model is null) return null;
        var d = DrawingFinder.PartDrawing(model, _log);
        if (d is null) return null;
        var key = Key(VaultFiles.LocalPathOf(d));
        _files[key] = d;
        return new ReleaseDrawingRef(key, d.Name);
    }

    /// <summary>One line when Vault restricts lifecycle changes to change orders (then the release will be refused), else null.</summary>
    public string? RestrictionNote()
    {
        try
        {
            var r = Conn.WebServiceManager.DocumentServiceExtensions.GetRestrictFileLifeCycleStateChangeToChangeOrder();
            return r == RestrictLifecycleChange.None ? null
                : "Vault restricts lifecycle state changes to change orders, so Vault will likely refuse these releases.";
        }
        catch (BridgeException) { throw; }
        catch (Exception ex) { _log($"approve: change order restriction unreadable: {ex.Message}"); return null; }
    }

    /// <summary>Everything the preview needs about one row. rows maps the Key-normalized key of every row to its row key, so a Vault child
    /// that is itself a row becomes a RowDependency (hold-back) and every other child an OtherChild (blocker rules). A Vault read failure
    /// becomes Problem (a blocker); a lost session throws. A Released drawing also gets its revise plan (Released, Work in Progress, Released).</summary>
    public ReleaseFacts Facts(ReleaseRowSpec spec, IReadOnlyDictionary<string, string> rows)
    {
        RequireSignedIn();
        try
        {
            var f = Resolve(spec.Key);
            if (f is null) return new ReleaseFacts(Found: false);
            var info = _card.Describe(f);
            var local = Key(spec.Key);
            var basic = new ReleaseFacts(VaultPath: info.VaultPath, Category: info.Category, State: info.State, Released: info.Released,
                                         LifecycleUnreadable: info.Unreadable, Revision: info.Revision, Version: info.Version);
            IReadOnlyList<WsFile> childFiles = Array.Empty<WsFile>();
            string? childrenProblem = null;
            try { ReadChildren(f, out childFiles); }
            catch (BridgeException ex) when (ex.Code == "not_signed_in") { throw; }
            catch (Exception ex) { childrenProblem = ex.Message; _log($"approve: children of {f.Name} unreadable: {ex}"); }
            var released = info.Released && info.Unreadable is null;
            if (released && spec.Kind != ReleaseKind.Drawing) return basic;

            LfCycTrans? trans;
            LfCycDef? def;
            string? problem;
            string? reviseProblem = null;
            string revisionAfter;
            if (released)
            {
                // A Released drawing is revised when its model changes: plan both moves by name, read the way back's child check and sync.
                reviseProblem = _card.PlanRevise(info).Problem;
                trans = DataCardHost.TransitionBetween(f, DataCardFix.WorkInProgressState, DataCardFix.ReleasedState);
                var toWip = DataCardHost.TransitionBetween(f, DataCardFix.ReleasedState, DataCardFix.WorkInProgressState);
                def = null; problem = null;
                revisionAfter = toWip is null ? "Vault decides" : ReleasePlan.RevisionAfter(Bump(toWip.Bump), info.Revision, NextRevisions(f, toWip.Bump));
                try { def = Conn.WebServiceManager.LifeCycleService.GetLifeCycleDefinitionsByIds(new[] { f.FileLfCyc!.LfCycDefId })?.FirstOrDefault(); }
                catch (Exception) { }
            }
            else
            {
                (trans, def, _, problem) = DataCardHost.FindTransition(f, DataCardFix.ReleasedState);
                revisionAfter = trans is null ? "" : ReleasePlan.RevisionAfter(Bump(trans.Bump), info.Revision, NextRevisions(f, trans.Bump));
            }
            bool? childCheck = trans is null ? null : (trans.CldState & EnforceChildStateEnum.EnforceChildFiles) != 0;
            var syncProps = trans is not null && (trans.SyncPropOption & JobSyncPropEnum.SyncProp) != 0;
            var others = new List<ReleaseChild>();
            var rowDeps = new List<string>();
            // Unreadable children fail closed as an engineering dependency.
            if (childrenProblem is not null) others.Add(new ReleaseChild($"its children ({childrenProblem})", null, ReleasePlan.PartCategory));
            foreach (var c in childFiles)
            {
                if (rows.TryGetValue(Key(SafeLocal(c)), out var rowKey)) { rowDeps.Add(rowKey); continue; }
                var id = c.FileLfCyc?.LfCycStateId ?? 0;
                var category = VaultFiles.CategoryName(c) ?? "";
                others.Add(id <= 0 ? new ReleaseChild(c.Name + " (no lifecycle)", false, category) : new ReleaseChild(c.Name, _card.StateReleased(id), category));
            }
            return basic with
            {
                RowDependencies = rowDeps, ReviseProblem = reviseProblem, SyncProps = syncProps,
                CheckedOut = info.CheckedOut, CheckedOutBy = info.CheckedOutBy,
                OpenDirty = LocalState.OpenDirty(_app, local), OpenOlder = LocalState.OpenOlder(_app, local, f),
                LocalEdits = LocalState.HasLocalEdits(_app, local, f), ControlledByChangeOrder = f.ControlledByChangeOrder,
                TransitionProblem = problem, Lifecycle = def?.DispName ?? def?.Name ?? "", ChildCheck = childCheck, RevisionAfter = revisionAfter,
                OtherChildren = others, Compliance = _card.ReadPropertyCompliance(info), Designer = Designer(local, f),
            };
        }
        catch (BridgeException ex) when (ex.Code == "not_signed_in") { throw; }
        catch (Exception ex)
        {
            _log($"approve: facts for {spec.Key} failed: {ex}");
            return new ReleaseFacts(Problem: "could not read from Vault: " + ex.Message);
        }
    }

    private static string SafeLocal(WsFile f)
    {
        try { return VaultFiles.LocalPathOf(f); } catch { return f.Name; }
    }

    /// <summary>Latest version of each Vault Dependency child (library files included) of the file's latest version; remembered for Get Latest.</summary>
    private void ReadChildren(WsFile f, out IReadOnlyList<WsFile> children)
    {
        var ds = Conn.WebServiceManager.DocumentService;
        var lites = ds.GetFileAssociationLitesByIds(new[] { f.Id }, FileAssocAlg.Actual,
            FileAssociationTypeEnum.None, false, FileAssociationTypeEnum.Dependency, false, true, false, false) ?? Array.Empty<FileAssocLite>();
        var ids = lites.Where(a => a.ParFileId == f.Id).Select(a => a.CldFileId).Distinct().ToArray();
        if (ids.Length == 0) { children = Array.Empty<WsFile>(); return; }
        var masters = (ds.GetFilesByIds(ids) ?? Array.Empty<WsFile>()).Select(x => x.MasterId).Distinct().ToArray();
        var latest = (ds.GetLatestFilesByMasterIds(masters) ?? Array.Empty<WsFile>()).Where(x => x.Id > 0).ToList();
        foreach (var l in latest) _children[l.MasterId] = l;
        children = latest;
    }

    private static RevisionBump Bump(BumpRevisionEnum b) => b switch
    {
        BumpRevisionEnum.None => RevisionBump.None,
        BumpRevisionEnum.BumpPrimary => RevisionBump.Primary,
        BumpRevisionEnum.BumpSecondary => RevisionBump.Secondary,
        BumpRevisionEnum.BumpTertiary => RevisionBump.Tertiary,
        _ => RevisionBump.Unknown,
    };

    /// <summary>RevisionService.GetNextRevisionNumbersByMasterIds for the file's revision scheme; null when not needed or unreadable.</summary>
    private IReadOnlyList<string>? NextRevisions(WsFile f, BumpRevisionEnum bump)
    {
        if (bump == BumpRevisionEnum.None) return null;
        try
        {
            var defId = f.FileRev?.RevDefId ?? 0;
            if (defId <= 0) return null;
            return Conn.WebServiceManager.RevisionService.GetNextRevisionNumbersByMasterIds(new[] { f.MasterId }, new[] { defId })?.FirstOrDefault()?.Items;
        }
        catch (Exception ex) { _log($"approve: next revision of {f.Name} unreadable: {ex.Message}"); return null; }
    }

    /// <summary>Designer from the open Inventor document, else the Vault property named Designer; null when neither can be read.</summary>
    private string? Designer(string local, WsFile f)
    {
        try
        {
            var doc = DocumentOpener.FindOpen(_app, local);
            if (doc is not null) return Convert.ToString(doc.PropertySets["Design Tracking Properties"]["Designer"].Value) ?? "";
        }
        catch (Exception) { }
        try
        {
            var ps = Conn.WebServiceManager.PropertyService;
            if (!_designerDefRead)
            {
                _designerDefRead = true;
                _designerDefId = (ps.GetPropertyDefinitionsByEntityClassId("FILE") ?? Array.Empty<PropDef>())
                    .FirstOrDefault(d => string.Equals(d.DispName, "Designer", StringComparison.OrdinalIgnoreCase))?.Id;
            }
            if (_designerDefId is not long id) return null;
            var inst = ps.GetProperties("FILE", new[] { f.Id }, new[] { id })?.FirstOrDefault();
            return inst?.Val?.ToString() ?? "";
        }
        catch (Exception) { return null; }
    }

    // ---- Writes ----

    /// <summary>
    /// Makes every file of the tree (the BOM, purchased and Content Center included, the drawings and the drawings' and assemblies' children)
    /// the latest Vault version, except files open in Inventor, checked out, or with local edits (LocalCopy rule; those engineering files
    /// were blockers in the preview). Never touches a local edit. Throws when the download fails, so nothing is signed or released.
    /// </summary>
    public IReadOnlyList<string> GetLatest()
    {
        RequireSignedIn();
        var ds = Conn.WebServiceManager.DocumentService;
        var byMaster = new Dictionary<long, WsFile>();
        foreach (var path in _tree.Concat(_files.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            WsFile? f;
            try { f = VaultFiles.FindLatest(VaultPathOf(Key(path))); }
            catch (BridgeException ex) when (ex.Code is "not_found" or "not_in_vault") { f = null; }
            if (f is not null) byMaster[f.MasterId] = f;
        }
        foreach (var master in _children.Keys.Where(m => !byMaster.ContainsKey(m)).ToList())
        {
            var f = ds.GetLatestFileByMasterId(master);
            if (f is not null && f.Id > 0) byMaster[master] = f;
        }
        var toGet = new List<WsFile>();
        var notes = new List<string>();
        foreach (var f in byMaster.Values)
        {
            var local = VaultFiles.LocalPathOf(f);
            var open = DocumentOpener.FindOpen(_app, local) is not null;
            var action = LocalCopy.Decide(open, f.CheckedOut);
            if (action == LocalCopyAction.Refresh && LocalState.HasLocalEdits(_app, local, f)) notes.Add($"{f.Name}: local edits, not overwritten");
            else if (action == LocalCopyAction.Refresh) toGet.Add(f);
            else if (action == LocalCopyAction.KeepCheckedOutCopy) notes.Add($"{f.Name}: checked out, kept as it is");
        }
        _log($"approve: get latest of {toGet.Count} of {byMaster.Count} files" + (notes.Count > 0 ? "; " + string.Join("; ", notes) : ""));
        if (toGet.Count > 0) VaultFiles.GetLatestOverwrite(toGet);
        return notes;
    }

    /// <summary>
    /// Sign off and release one row (a Revise row: Released, Work in Progress, sign off, Released). The file must still be the version the
    /// preview read and not checked out. Failures are returned (step and message); SignOff undoes its own checkout on any failure before the
    /// check-in, and a final check undoes a checkout this step still holds. A lost session throws.
    /// </summary>
    public ReleaseOutcome Release(ReleaseRow row, string approver)
    {
        RequireSignedIn();
        var path = row.Facts.VaultPath;
        long? master = null;
        var attempted = false;
        try
        {
            var latest = VaultFiles.FindLatest(path);
            if (latest is null) return ReleaseOutcome.Fail("check", $"{path} is no longer in Vault.");
            master = latest.MasterId;
            var info = _card.Describe(latest);
            if (info.Version != row.Facts.Version)
                return ReleaseOutcome.Fail("check", $"changed in Vault since the preview (version {row.Facts.Version} to {info.Version}); run Preview again.");
            if (info.CheckedOut)
                return ReleaseOutcome.Fail("check", $"checked out by {(string.IsNullOrWhiteSpace(info.CheckedOutBy) ? "someone" : info.CheckedOutBy)} since the preview.");
            var vaultUser = "";
            try { vaultUser = Conn.UserName ?? ""; } catch (Exception) { }
            var comment = ReleasePlan.SignOffComment(approver, vaultUser);
            var change = new PropChange(new PropRef(PropSet.Custom, ReleasePlan.ApprovedByProperty), null, approver);
            // From here a checkout to this user can only be this step's own (it was not checked out just above).
            attempted = true;

            if (row.Status == ReleaseStatus.Revise)
            {
                var o = ReleasePlan.ReviseAndRelease(
                    () => _card.ChangeState(info, DataCardFix.WorkInProgressState, comment),
                    () => StateNow(path),
                    () => SignOff(Fresh(path), change, comment, master.Value),
                    () => { var r = _card.ChangeState(Fresh(path), DataCardFix.ReleasedState, comment).Revision; _touched.Add(master.Value); return r; });
                if (!o.Ok) RequireSignedIn(o.Message);
                return o;
            }

            try { SignOff(info, change, comment, master.Value); }
            catch (DataCardSkipException ex) { RequireSignedIn(); return ReleaseOutcome.Fail("sign off", ex.Message); }
            catch (DataCardStepException ex) { RequireSignedIn(); return ReleaseOutcome.Fail($"sign off ({ex.Step})", ex.Message); }

            try
            {
                var changed = _card.ChangeState(Fresh(path), DataCardFix.ReleasedState, comment);
                return ReleaseOutcome.Done(changed.Revision);
            }
            catch (DataCardStepException ex)
            {
                RequireSignedIn();
                return ReleaseOutcome.Fail("release", ex.Message + $" The sign-off ({ReleasePlan.ApprovedByProperty} = {approver}) is checked in. "
                    + $"Vault shows the file in {StateNow(path)} now; check the state in Vault before trying again.");
            }
        }
        finally
        {
            if (attempted && master is long m) EnsureNotCheckedOut(m, path);
        }
    }

    /// <summary>Sign-off through DataCardHost; the file counts as touched (children of later parents use its latest version) once checked in.</summary>
    private void SignOff(DataCardFileInfo info, PropChange change, string comment, long master)
    {
        var signed = _card.SignOff(info, change, comment, _touched);
        _touched.Add(master);
        if (signed.Warning is not null) _log($"approve: {info.VaultPath}: {signed.Warning}");
    }

    private DataCardFileInfo Fresh(string vaultPath) =>
        _card.Describe(VaultFiles.FindLatest(vaultPath) ?? throw new DataCardStepException("resolve", $"{vaultPath} is no longer in Vault."));

    private string StateNow(string vaultPath)
    {
        try { return VaultFiles.FindLatest(vaultPath)?.FileLfCyc?.LfCycStateName ?? "unknown"; }
        catch (Exception) { return "unknown"; }
    }

    private static void RequireSignedIn(string detail)
    {
        if (!VaultSession.IsSignedIn) throw new BridgeException("not_signed_in", detail + " " + BridgeOps.NotSignedInMessage);
    }

    /// <summary>The preview refused files that were checked out, so any checkout to this user now is the run's own: undo it and log it.</summary>
    private void EnsureNotCheckedOut(long master, string path)
    {
        try
        {
            var now = Conn.WebServiceManager.DocumentService.GetLatestFileByMasterId(master);
            if (!now.CheckedOut || now.CkOutUserId != Conn.UserID) return;
            var local = VaultFiles.LocalPathOf(now);
            _log($"approve: {path} was still checked out after the run step; {DataCardHost.UndoIfStillMine(Conn, master, local)}");
        }
        catch (Exception ex) { _log($"approve: could not check whether {path} is still checked out: {ex.Message}"); }
    }
}
