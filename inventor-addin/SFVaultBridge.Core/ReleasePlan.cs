using System.Text;

namespace SFVaultBridge.Core;

public enum ReleaseKind { Part, SubAssembly, Top, Drawing }

/// <summary>Ready, Revise (a Released drawing whose model changes: revise and re-release), AlreadyReleased, Blocked and HeldBack come from
/// the preview; Released, Failed and NotAttempted from the run.</summary>
public enum ReleaseStatus { Ready, Revise, AlreadyReleased, Blocked, HeldBack, Released, Failed, NotAttempted }

/// <summary>How the lifecycle transition to Released bumps the revision (Vault's BumpRevisionEnum); Unknown when it could not be read.</summary>
public enum RevisionBump { None, Primary, Secondary, Tertiary, Unknown }

/// <summary>One engineering model in bottom-up order. Below lists the keys of every engineering model under an assembly, all levels.</summary>
public sealed record ReleaseModel(string Key, string FileName, string PartNumber, ReleaseKind Kind, string? Category,
                                  string Material, string Process, IReadOnlyList<string> Below);

/// <summary>The drawing picked for a model (DrawingFinder / ReviewRules.PickDrawings first pick). Key is its workspace path.</summary>
public sealed record ReleaseDrawingRef(string Key, string FileName);

/// <summary>One row of the release: a model, or a drawing placed after the last of the models it documents. Drawing is a model's drawing key.</summary>
public sealed record ReleaseRowSpec(string Key, string FileName, string PartNumber, ReleaseKind Kind,
                                    IReadOnlyList<string> Models, IReadOnlyList<string> Below, ReleaseModel? Model, bool NoDrawing,
                                    string? Drawing = null);

/// <summary>A Vault child (file dependency) of a row that is not itself a row of this release. Released is null when its state could not be read.
/// Category is its Vault category (engineering children block whatever the child check says).</summary>
public sealed record ReleaseChild(string Name, bool? Released, string Category = "", bool OpenOlder = false);

/// <summary>One Vault version of a child file: id, Vault checksum (File.Cksum) and size.</summary>
public sealed record ChildVersion(long Id, long Cksum, long Size);

/// <summary>
/// A parent's Dependency child as it is on disk. Touched: this run signed or released it (use LatestId). LocalCksum/LocalSize are the local
/// file's CRC-32 and length (null when there is no local file). LocalEqualsLatest is asked only when no version checksum matches: a byte
/// compare with a fresh download of the latest version.
/// </summary>
public sealed record ChildOnDisk(string Name, bool Touched, long LatestId, IReadOnlyList<ChildVersion> Versions,
                                 long? LocalCksum, long? LocalSize, Func<bool>? LocalEqualsLatest = null);

/// <summary>
/// What Vault and Inventor say about one row's file. Released is the state's released flag (Vault LfCycState.ReleasedState).
/// ChildCheck is the transition's child-state control (files must be released first), null when no transition was found.
/// Designer null means it could not be read (no warning); empty means blank (warning).
/// </summary>
public sealed record ReleaseFacts(
    bool Found = true, string VaultPath = "", string Category = "", string State = "", bool Released = false,
    string? LifecycleUnreadable = null, string Revision = "", long Version = 0,
    bool CheckedOut = false, string CheckedOutBy = "",
    bool OpenDirty = false, bool OpenOlder = false, bool LocalEdits = false, bool ControlledByChangeOrder = false,
    string? TransitionProblem = null, string Lifecycle = "", bool? ChildCheck = null, string RevisionAfter = "",
    IReadOnlyList<ReleaseChild>? OtherChildren = null, string? Compliance = null, string? Designer = null,
    string? Problem = null, IReadOnlyList<string>? RowDependencies = null, string? ReviseProblem = null, bool SyncProps = false);

/// <summary>A previewed row: its status, why it is skipped (Blockers), notes (Warnings) and, when held back, the child that holds it.</summary>
public sealed record ReleaseRow(ReleaseRowSpec Spec, ReleaseFacts Facts, ReleaseStatus Status,
                                IReadOnlyList<string> Blockers, IReadOnlyList<string> Warnings, string? HeldBackBy);

/// <summary>What the host reports after signing off and releasing one file. Step names the failed step (sign off, release, ...).</summary>
public sealed record ReleaseOutcome(bool Ok, string Step, string Message, string RevisionAfter = "", string? Warning = null)
{
    public static ReleaseOutcome Done(string revisionAfter, string? warning = null) => new(true, "", "", revisionAfter, warning);
    public static ReleaseOutcome Fail(string step, string message) => new(false, step, message);
}

/// <summary>A row's final result in the run.</summary>
public sealed record ReleaseResult(ReleaseRow Row, ReleaseStatus Status, string Detail, string RevisionAfter = "");

/// <summary>The Vault and Inventor side of the release run (ReleaseHost). Core only decides.</summary>
public interface IReleaseWriter
{
    /// <summary>Get Latest of the whole tree (children included) under the LocalCopy rule; returns notes for files kept as they are.
    /// Throws when it fails, and the run stops before anything is written.</summary>
    IReadOnlyList<string> GetLatest();

    /// <summary>Sign off (check out, set Engr Approved By, save, check in) and change the state to Released. Never leaves the file checked out.
    /// Throws BridgeException("not_signed_in") when the Vault session is gone.</summary>
    ReleaseOutcome Release(ReleaseRow row, string approver);
}

/// <summary>Pure rules of the I Approve! sign-off and release: scope, bottom-up order, blockers, hold-back, texts.</summary>
public static class ReleasePlan
{
    public const string ApprovedByProperty = "Engr Approved By";
    public const string PartCategory = "Part - Engineering";
    public const string AssemblyCategory = "Assembly - Engineering";
    public const int MaxChildrenListed = 5;

    private static readonly StringComparer Keys = StringComparer.OrdinalIgnoreCase;

    private static bool Is(string? a, string b) => string.Equals(a?.Trim(), b, StringComparison.OrdinalIgnoreCase);

    private static bool Ext(string? name, string ext) => (name ?? "").EndsWith(ext, StringComparison.OrdinalIgnoreCase);

    // ---- Scope and order ----

    /// <summary>
    /// The top assembly and every engineering (Make) part and sub-assembly in its BOM, bottom up (each file once, before every assembly that uses it).
    /// Never in scope: virtual rows (no file), Phantom rows (their children are), Reference and Purchased rows and everything under them,
    /// buy categories (Part - Purchased, Part - Content Center) and files under Content Center Files.
    /// </summary>
    public static IReadOnlyList<ReleaseModel> Models(BomNode top, string? topCategory, IReadOnlyList<BomNode> roots, Func<BomNode, string?> categoryOf)
    {
        var order = new List<ReleaseModel>();
        var index = new Dictionary<string, int>(Keys);
        var belowOf = new Dictionary<string, HashSet<string>>(Keys);

        // Returns the engineering keys at and under the node (empty for pruned rows).
        IReadOnlyCollection<string> Visit(BomNode n)
        {
            if (string.IsNullOrWhiteSpace(n.FullPath) || string.IsNullOrWhiteSpace(n.FileName)) return Array.Empty<string>();
            var category = categoryOf(n);
            var structure = SourceRules.EffectiveStructure(n.BomStructure ?? "", category);
            if (Is(structure, "Phantom")) return n.Children.SelectMany(Visit).Distinct(Keys).ToList();
            if (Is(structure, "Purchased") || Is(structure, "Reference") || SourceRules.IsBuyCategory(category)) return Array.Empty<string>();
            if (ReviewRules.Classify(n, category) != PartKind.Make) return Array.Empty<string>();
            var kind = Ext(n.FileName, ".iam") ? ReleaseKind.SubAssembly : Ext(n.FileName, ".ipt") ? ReleaseKind.Part : (ReleaseKind?)null;
            if (kind is null) return Array.Empty<string>();
            var below = new HashSet<string>(Keys);
            if (kind == ReleaseKind.SubAssembly) foreach (var k in n.Children.SelectMany(Visit)) below.Add(k);
            if (!index.ContainsKey(n.FullPath))
            {
                index[n.FullPath] = order.Count;
                belowOf[n.FullPath] = below;
                order.Add(new ReleaseModel(n.FullPath, n.FileName, n.PartNumber, kind.Value, category, n.Material ?? "", n.Process ?? "", Array.Empty<string>()));
            }
            else foreach (var k in below) belowOf[n.FullPath].Add(k);
            var result = new HashSet<string>(belowOf[n.FullPath], Keys) { n.FullPath };
            return result;
        }

        var all = new HashSet<string>(Keys);
        foreach (var r in roots) foreach (var k in Visit(r)) all.Add(k);
        all.Remove(top.FullPath ?? "");
        var models = order.Select(m => m with { Below = belowOf[m.Key].Where(k => !Keys.Equals(k, m.Key)).ToList() }).ToList();
        if (!string.IsNullOrWhiteSpace(top.FullPath) && !index.ContainsKey(top.FullPath))
            models.Add(new ReleaseModel(top.FullPath, top.FileName, top.PartNumber, ReleaseKind.Top, topCategory, top.Material ?? "", top.Process ?? "", all.ToList()));
        return models;
    }

    /// <summary>
    /// Release rows: each model, followed by its drawing once the last model that drawing documents is placed (so a drawing always comes after
    /// every one of its models). A model missing from drawings, or mapped to null, has NoDrawing.
    /// </summary>
    public static IReadOnlyList<ReleaseRowSpec> Order(IReadOnlyList<ReleaseModel> models, IReadOnlyDictionary<string, ReleaseDrawingRef?> drawings)
    {
        ReleaseDrawingRef? DrawingOf(ReleaseModel m) => drawings.TryGetValue(m.Key, out var d) ? d : null;
        var modelsOf = new Dictionary<string, List<ReleaseModel>>(Keys);
        var modelKeys = new HashSet<string>(models.Select(m => m.Key), Keys);
        foreach (var m in models)
        {
            var d = DrawingOf(m);
            if (d is null || modelKeys.Contains(d.Key)) continue;
            if (!modelsOf.TryGetValue(d.Key, out var list)) modelsOf[d.Key] = list = new List<ReleaseModel>();
            list.Add(m);
        }
        var rows = new List<ReleaseRowSpec>();
        foreach (var m in models)
        {
            var d = DrawingOf(m);
            rows.Add(new ReleaseRowSpec(m.Key, m.FileName, m.PartNumber, m.Kind, Array.Empty<string>(), m.Below, m, d is null,
                                        d is null || modelKeys.Contains(d.Key) ? null : d.Key));
            if (d is null || !modelsOf.TryGetValue(d.Key, out var owners) || !ReferenceEquals(owners[^1], m)) continue;
            rows.Add(new ReleaseRowSpec(d.Key, d.FileName, owners[0].PartNumber, ReleaseKind.Drawing, owners.Select(o => o.Key).ToList(),
                                        Array.Empty<string>(), null, false));
        }
        return rows;
    }

    // ---- Facts to rows ----

    public static string KindText(ReleaseKind kind) => kind switch
    {
        ReleaseKind.Part => "Part",
        ReleaseKind.SubAssembly => "Sub-assembly",
        ReleaseKind.Top => "Top",
        _ => "Drawing",
    };

    /// <summary>Why the row is skipped; empty when it can be released. Not called for rows already Released.</summary>
    public static IReadOnlyList<string> Blockers(ReleaseRowSpec s, ReleaseFacts f)
    {
        var b = new List<string>();
        if (!f.Found) { b.Add("not in Vault"); return b; }
        if (f.Problem is not null) { b.Add(f.Problem); return b; }
        if (f.LifecycleUnreadable is not null) b.Add(f.LifecycleUnreadable);
        if (s.Model is not null)
        {
            var want = s.Kind == ReleaseKind.Part ? PartCategory : AssemblyCategory;
            if (!Is(f.Category, want))
                b.Add($"category is {(string.IsNullOrWhiteSpace(f.Category) ? "blank" : "'" + f.Category.Trim() + "'")}, not {want}");
        }
        if (f.CheckedOut) b.Add("checked out by " + (string.IsNullOrWhiteSpace(f.CheckedOutBy) ? "someone" : f.CheckedOutBy.Trim()));
        if (f.OpenDirty) b.Add("open in Inventor with unsaved changes");
        else if (f.OpenOlder) b.Add(OpenOlderText);
        if (f.LocalEdits) b.Add("local edits without checkout; check it out or discard the edits");
        if (f.ControlledByChangeOrder) b.Add("controlled by a change order");
        if (s.Model is not null && s.NoDrawing) b.Add("no drawing");
        if (s.Kind == ReleaseKind.Part && s.Model is not null)
        {
            var material = (s.Model.Material ?? "").Trim();
            if (material.Length == 0) b.Add("Material is blank");
            else if (Is(material, "Generic")) b.Add("Material is Generic");
        }
        if (IsCompliant(f.Compliance) == false) b.Add($"Property Compliance is {ComplianceText(f.Compliance)}");
        if (f.TransitionProblem is not null) b.Add(f.TransitionProblem);
        var kids = f.OtherChildren ?? Array.Empty<ReleaseChild>();
        // Engineering files the row depends on but that are not rows (skeletons, derived bases, BOM-excluded components): never release over them.
        var engineering = kids.Where(c => IsEngineeringCategory(c.Category) && c.Released != true).ToList();
        var library = f.TransitionProblem is null && f.ChildCheck == true
            ? kids.Where(c => !IsEngineeringCategory(c.Category) && c.Released != true).ToList() : new List<ReleaseChild>();
        var lines = engineering.Select(c => c.Released is null ? $"depends on {c.Name} (state unreadable)" : $"depends on {c.Name} (not Released)")
            .Concat(library.Select(c => c.Released is null
                ? $"could not read the state of {c.Name} (Vault's child check is on for this lifecycle)"
                : $"{c.Name} must be Released first (Vault's child check is on for this lifecycle)")).ToList();
        // A child loaded in Inventor at an older version would be referenced at that older version on the check-in.
        lines.InsertRange(0, kids.Where(c => c.OpenOlder).Select(c => $"{c.Name} is {OpenOlderText}"));
        b.AddRange(lines.Take(MaxChildrenListed));
        if (lines.Count > MaxChildrenListed) b.Add($"and {lines.Count - MaxChildrenListed} more children must be Released first");
        return b;
    }

    public const string OpenOlderText = "open in Inventor at an older version; close and reopen the assembly";

    public static bool IsEngineeringCategory(string? category) => Is(category, PartCategory) || Is(category, AssemblyCategory);

    public const string ReviseText = "revise and re-release (model changes)";

    public static IReadOnlyList<string> Warnings(ReleaseRowSpec s, ReleaseFacts f, string approver)
    {
        var w = new List<string>();
        if (f.SyncProps) w.Add("the transition to Released syncs properties (Vault may add a version later)");
        if (f.Designer is not null)
        {
            if (f.Designer.Trim().Length == 0) w.Add("Designer is blank");
            else if (SelfApproval(approver, f.Designer)) w.Add("you are this file's Designer (self-approval)");
        }
        if (s.Kind == ReleaseKind.Part && s.Model is not null && string.IsNullOrWhiteSpace(s.Model.Process)) w.Add("Process is blank");
        if (f.Found && f.Problem is null && IsCompliant(f.Compliance) is null) w.Add("Property Compliance could not be read");
        return w;
    }

    /// <summary>True when the value reads as compliant, false when it reads as anything else, null when blank (unreadable).</summary>
    public static bool? IsCompliant(string? value)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0) return null;
        // Vault's system property "Compliance" is numeric: 0 is compliant, anything else is not (2 is Non-Compliant).
        if (int.TryParse(v, out var code)) return code == 0;
        return v.StartsWith("Compliant", StringComparison.OrdinalIgnoreCase) || Is(v, "Not Applicable") || Is(v, "N/A");
    }

    /// <summary>Vault's compliance value in words for a blocker line.</summary>
    public static string ComplianceText(string? value)
    {
        var v = (value ?? "").Trim();
        return int.TryParse(v, out var code) ? code switch { 0 => "Compliant", 2 => "Non-Compliant", _ => $"not compliant (code {code})" } : v;
    }

    /// <summary>The approver's "First L." name from Inventor's user name (ImportRules.DesignerName).</summary>
    public static string ApproverName(string? inventorUserName) => ImportRules.DesignerName(inventorUserName);

    /// <summary>True when the Designer is the approver, written either as the full name or as "First L.".</summary>
    public static bool SelfApproval(string approver, string? designer)
    {
        var a = (approver ?? "").Trim();
        var d = (designer ?? "").Trim();
        if (a.Length == 0 || d.Length == 0) return false;
        return Is(d, a) || Is(ImportRules.DesignerName(d), a) || Is(ImportRules.DesignerName(a), ImportRules.DesignerName(d));
    }

    /// <summary>The revision text after the transition: unchanged for no bump, Vault's next label for a bump when it was read, else "Vault decides".</summary>
    public static string RevisionAfter(RevisionBump bump, string current, IReadOnlyList<string>? next)
    {
        var cur = (current ?? "").Trim();
        if (bump == RevisionBump.None) return cur.Length > 0 ? cur + " (no change)" : "(no change)";
        var i = bump switch { RevisionBump.Primary => 0, RevisionBump.Secondary => 1, RevisionBump.Tertiary => 2, _ => -1 };
        if (i < 0 || next is null || next.Count <= i || string.IsNullOrWhiteSpace(next[i])) return "Vault decides";
        return next[i].Trim();
    }

    /// <summary>A key counts as released after the run when it was already Released or this run released it.</summary>
    public static bool CountsAsReleased(ReleaseStatus s) => s is ReleaseStatus.AlreadyReleased or ReleaseStatus.Released;

    /// <summary>
    /// Why the row must wait: the first dependency that is not released, or null. Dependencies: a drawing's models, an assembly's engineering
    /// models (all levels) and each of their drawings, plus the row's Vault dependencies that are rows (RowDependencies). statusOf is null for a
    /// row not decided yet: that fails closed for models and dependencies; a drawing placed later (a drawing shared with a parent) is not waited for.
    /// preview counts Ready and Revise rows as released (what a run would do).
    /// </summary>
    public static string? HeldBackBy(ReleaseRowSpec s, ReleaseFacts f, Func<string, ReleaseStatus?> statusOf, bool preview,
                                     Func<string, string> nameOf, Func<string, string?> drawingOf)
    {
        bool Ok(ReleaseStatus? st) => st is ReleaseStatus v && (CountsAsReleased(v) || (preview && v is ReleaseStatus.Ready or ReleaseStatus.Revise));
        var deps = (s.Kind == ReleaseKind.Drawing ? s.Models : s.Below).Concat(f.RowDependencies ?? Array.Empty<string>())
                   .Where(k => !Keys.Equals(k, s.Key)).Distinct(Keys).ToList();
        var rowDeps = new HashSet<string>(f.RowDependencies ?? Array.Empty<string>(), Keys);
        // A Vault dependency on a row later in the order waits for it; it is released by this run, so a second run releases this row.
        var not = deps.Where(k => !Ok(statusOf(k)))
                      .Select(k => statusOf(k) is null && rowDeps.Contains(k) ? $"{nameOf(k)} (later in the order; run I Approve! again)" : nameOf(k)).ToList();
        if (s.Kind != ReleaseKind.Drawing)
            foreach (var k in s.Below)
            {
                var d = drawingOf(k);
                if (d is null) continue;
                var st = statusOf(d);
                if (st is not null && !Ok(st)) not.Add(nameOf(d));
            }
        not = not.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (not.Count == 0) return null;
        return not.Count == 1 ? not[0] : $"{not[0]} and {not.Count - 1} more";
    }

    public static string HeldBackText(string by) => $"held back: {by} is not Released";

    /// <summary>
    /// Preview: status, blockers and warnings per row, then the hold-back a run would apply if every Ready row were released. A drawing that is
    /// already Released while one of its models is Ready is revised (Released, Work in Progress, signed, Released) with the revise plan as its
    /// transition check.
    /// </summary>
    public static IReadOnlyList<ReleaseRow> Evaluate(IReadOnlyList<ReleaseRowSpec> specs, Func<ReleaseRowSpec, ReleaseFacts> facts, string approver)
    {
        var rows = new List<ReleaseRow>();
        var statusOf = new Dictionary<string, ReleaseStatus>(Keys);
        var names = NamesOf(specs);
        var drawings = DrawingsOf(specs);
        ReleaseStatus? St(string k) => statusOf.TryGetValue(k, out var v) ? v : null;
        foreach (var s in specs)
        {
            var f = facts(s);
            ReleaseRow row;
            var readable = f.Found && f.Problem is null && f.LifecycleUnreadable is null;
            var reviseCase = readable && f.Released && s.Kind == ReleaseKind.Drawing && s.Models.Any(k => St(k) == ReleaseStatus.Ready);
            if (readable && f.Released && !reviseCase)
                // An already-Released file loaded in Inventor at an older version would be what its parents are checked in against.
                row = f.OpenOlder
                    ? new ReleaseRow(s, f, ReleaseStatus.Blocked, new[] { OpenOlderText }, Array.Empty<string>(), null)
                    : new ReleaseRow(s, f, ReleaseStatus.AlreadyReleased, Array.Empty<string>(), Array.Empty<string>(), null);
            else
            {
                var revise = readable && f.Released;
                var blockers = Blockers(s, revise ? f with { TransitionProblem = f.ReviseProblem } : f);
                var warnings = Warnings(s, f, approver);
                if (blockers.Count > 0) row = new ReleaseRow(s, f, ReleaseStatus.Blocked, blockers, warnings, null);
                else
                {
                    var by = HeldBackBy(s, f, St, true, k => names.TryGetValue(k, out var n) ? n : Path.GetFileName(k),
                                        k => drawings.TryGetValue(k, out var d) ? d : null);
                    row = new ReleaseRow(s, f, by is not null ? ReleaseStatus.HeldBack : revise ? ReleaseStatus.Revise : ReleaseStatus.Ready,
                                         blockers, warnings, by);
                }
            }
            statusOf[s.Key] = row.Status;
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>Display name per row key: the part number for models, the file name for drawings.</summary>
    internal static Dictionary<string, string> NamesOf(IEnumerable<ReleaseRowSpec> specs) =>
        specs.GroupBy(s => s.Key, Keys).ToDictionary(g => g.Key, g =>
        {
            var s = g.First();
            return s.Kind == ReleaseKind.Drawing || string.IsNullOrWhiteSpace(s.PartNumber) ? s.FileName : s.PartNumber;
        }, Keys);

    internal static Dictionary<string, string> DrawingsOf(IEnumerable<ReleaseRowSpec> specs) =>
        specs.Where(s => s.Drawing is not null).GroupBy(s => s.Key, Keys).ToDictionary(g => g.Key, g => g.First().Drawing!, Keys);

    // ---- Texts ----

    /// <summary>Release counts Ready and Revise rows; Revise is also given on its own.</summary>
    public static (int Release, int Revise, int Skipped, int HeldBack, int AlreadyReleased) Counts(IReadOnlyList<ReleaseRow> rows) =>
        (rows.Count(r => r.Status is ReleaseStatus.Ready or ReleaseStatus.Revise), rows.Count(r => r.Status == ReleaseStatus.Revise),
         rows.Count(r => r.Status == ReleaseStatus.Blocked), rows.Count(r => r.Status == ReleaseStatus.HeldBack),
         rows.Count(r => r.Status == ReleaseStatus.AlreadyReleased));

    private static string Files(int n) => n == 1 ? "1 file" : $"{n} files";

    public static string ConfirmText(IReadOnlyList<ReleaseRow> rows, string approver)
    {
        var c = Counts(rows);
        return $"Release {Files(c.Release)} as {approver}? {c.Skipped} will be skipped, {c.HeldBack} held back."
               + "\n\nEach file is checked out, signed (Engr Approved By), checked in and changed to Released, bottom up."
               + (c.Revise == 0 ? "" : $"\n\n{c.Revise} Released {(c.Revise == 1 ? "drawing is" : "drawings are")} revised and re-released because the model changes: "
                                      + "moved to Work in Progress (new revision), signed and returned to Released.");
    }

    /// <summary>The preview's one line about Vault's child check, per lifecycle that the rows use.</summary>
    public static string ChildCheckLine(IEnumerable<(string Lifecycle, bool? ChildCheck)> lifecycles)
    {
        var known = lifecycles.Where(l => l.ChildCheck is not null && !string.IsNullOrWhiteSpace(l.Lifecycle))
                              .GroupBy(l => l.Lifecycle.Trim(), StringComparer.OrdinalIgnoreCase)
                              .Select(g => (Name: g.Key, On: g.Any(x => x.ChildCheck == true))).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (known.Count == 0) return "Vault's child check (children must be Released first): could not be read.";
        var on = known.Where(k => k.On).Select(k => k.Name).ToList();
        var off = known.Where(k => !k.On).Select(k => k.Name).ToList();
        if (off.Count == 0) return $"Vault's child check is ON for {string.Join(", ", on)}: purchased and Content Center children must be in a Released state first (see README: Library lifecycle).";
        if (on.Count == 0) return $"Vault's child check is off for {string.Join(", ", off)}.";
        return $"Vault's child check is ON for {string.Join(", ", on)} and off for {string.Join(", ", off)}.";
    }

    public static string StatusText(ReleaseStatus s) => s switch
    {
        ReleaseStatus.Ready => "Ready",
        ReleaseStatus.Revise => "revise",
        ReleaseStatus.AlreadyReleased => "already Released",
        ReleaseStatus.Blocked => "skipped",
        ReleaseStatus.HeldBack => "held back",
        ReleaseStatus.Released => "released",
        ReleaseStatus.Failed => "failed",
        _ => "not attempted",
    };

    /// <summary>The preview's Ready / Blocker cell.</summary>
    public static string PreviewText(ReleaseRow r) => r.Status switch
    {
        ReleaseStatus.Blocked => string.Join("; ", r.Blockers),
        ReleaseStatus.HeldBack => HeldBackText(r.HeldBackBy ?? "a child"),
        ReleaseStatus.AlreadyReleased => "already Released",
        ReleaseStatus.Revise => ReviseText,
        _ => "Ready",
    };

    public static string ResultLine(ReleaseResult r) => r.Status switch
    {
        ReleaseStatus.Released => $"released ({Dash(r.Row.Facts.Revision)} -> {Dash(r.RevisionAfter)})" + (r.Detail.Length > 0 ? "; " + r.Detail : ""),
        ReleaseStatus.Ready => "Ready",
        ReleaseStatus.Revise => ReviseText,
        ReleaseStatus.AlreadyReleased => "already Released",
        ReleaseStatus.Blocked => "skipped: " + r.Detail,
        ReleaseStatus.HeldBack => r.Detail,
        ReleaseStatus.Failed => "failed: " + r.Detail,
        _ => "not attempted" + (r.Detail.Length > 0 ? ": " + r.Detail : ""),
    };

    private static string Dash(string s) => string.IsNullOrWhiteSpace(s) ? "-" : s.Trim();

    public static string ResultsText(IEnumerable<ReleaseResult> results)
    {
        var sb = new StringBuilder();
        sb.Append("File\tKind\tResult\tRevision before\tRevision after\r\n");
        foreach (var r in results)
            sb.Append(string.Join("\t", new[]
            {
                r.Row.Spec.FileName, KindText(r.Row.Spec.Kind), ResultLine(r), r.Row.Facts.Revision,
                r.Status == ReleaseStatus.Released ? r.RevisionAfter : "",
            }.Select(BomFile.Field))).Append("\r\n");
        return sb.ToString();
    }

    public static string Summary(IReadOnlyList<ReleaseResult> results)
    {
        int C(ReleaseStatus s) => results.Count(r => r.Status == s);
        return $"{C(ReleaseStatus.Released)} released, {C(ReleaseStatus.AlreadyReleased)} already Released, {C(ReleaseStatus.Blocked)} skipped, "
               + $"{C(ReleaseStatus.HeldBack)} held back, {C(ReleaseStatus.Failed)} failed, {C(ReleaseStatus.NotAttempted)} not attempted.";
    }

    /// <summary>After the run: build the packages only when the top assembly and its drawing are Released; otherwise the reason they were not built.</summary>
    public static (bool Build, string Message) AfterRelease(IReadOnlyList<ReleaseResult> results)
    {
        const string notBuilt = "so the Purchasing Sheet, Assembly Package and Partner Package were not built.";
        var top = results.FirstOrDefault(r => r.Row.Spec.Kind == ReleaseKind.Top);
        if (top is null) return (false, "The top assembly was not in the release, " + notBuilt);
        if (!CountsAsReleased(top.Status)) return (false, $"The top assembly {top.Row.Spec.FileName} is not Released ({ResultLine(top)}), {notBuilt}");
        var drawing = results.FirstOrDefault(r => r.Row.Spec.Kind == ReleaseKind.Drawing && r.Row.Spec.Models.Contains(top.Row.Spec.Key, Keys));
        if (drawing is null) return (false, $"The top assembly {top.Row.Spec.FileName} has no drawing in the release, {notBuilt}");
        if (!CountsAsReleased(drawing.Status)) return (false, $"The top assembly's drawing {drawing.Row.Spec.FileName} is not Released ({ResultLine(drawing)}), {notBuilt}");
        return (true, "");
    }

    // ---- Host rules ----

    /// <summary>Why the sign-off edit may not touch the file, or null. Same rules as DataCardFix.SkipReason, but .iam and .idw/.dwg are allowed
    /// (this flow only: the tree is made latest first, so references resolve) and a Released file is never edited.</summary>
    public static string? SignOffSkipReason(DataCardFileInfo f)
    {
        if (!(Ext(f.Name, ".ipt") || Ext(f.Name, ".iam") || Ext(f.Name, ".idw") || Ext(f.Name, ".dwg")))
            return $"{f.Name} is not an Inventor part, assembly or drawing.";
        if (f.Unreadable is not null) return f.Unreadable;
        if (f.CheckedOut)
            return $"{f.Name} is checked out by {(string.IsNullOrWhiteSpace(f.CheckedOutBy) ? "someone" : f.CheckedOutBy)}. Check it in or undo the checkout first.";
        if (string.Equals(f.Category, DataCardFix.ContentCenterCategory, StringComparison.OrdinalIgnoreCase)
            || f.VaultPath.StartsWith(DataCardFix.ContentCenterRoot, StringComparison.OrdinalIgnoreCase))
            return $"{f.Name} is a Content Center file.";
        if (DataCardFix.IsReleased(f)) return $"{f.Name} is already Released.";
        return null;
    }

    /// <summary>
    /// On a parent's sign-off check-in, the version of each Dependency child is chosen from what is on disk: a child this run signed or
    /// released is referenced at its latest version; any other child at the newest version whose checksum and size match the local file, else
    /// at the latest version when the local file is byte-identical to it. A child with no matching version (or no local file) is a problem, and
    /// the parent is not checked in. Attachments pass unchanged.
    /// </summary>
    public static (IReadOnlyList<AssocRow> Rows, IReadOnlyList<string> Problems) ChooseChildVersions(IReadOnlyList<AssocRow> rows, Func<long, ChildOnDisk?> childOf)
    {
        var problems = new List<string>();
        var result = new List<AssocRow>();
        foreach (var r in rows)
        {
            if (r.Type != "Dependency") { result.Add(r); continue; }
            var c = childOf(r.CldFileId);
            if (c is null) { problems.Add($"could not read child file {r.CldFileId} from Vault"); result.Add(r); continue; }
            long? id = null;
            if (c.Touched) id = c.LatestId > 0 ? c.LatestId : null;
            else if (c.LocalCksum is null) problems.Add($"{c.Name} is not in the workspace where Vault maps it; Get Latest, then re-run");
            else
            {
                var match = c.Versions.Where(v => v.Cksum == c.LocalCksum && (c.LocalSize is null || v.Size == c.LocalSize)).OrderByDescending(v => v.Id).FirstOrDefault();
                if (match is not null) id = match.Id;
                else if (c.LatestId > 0 && c.LocalEqualsLatest?.Invoke() == true) id = c.LatestId;
                else problems.Add($"your copy of {c.Name} differs from Vault (Content Center parts regenerated locally do this); Get Latest or check it in, then re-run");
            }
            if (c.Touched && id is null) problems.Add($"could not read the latest version of {c.Name}");
            result.Add(id is long v2 ? r with { CldFileId = v2 } : r);
        }
        return (result, problems);
    }

    /// <summary>Standard CRC-32 (IEEE, reflected 0xEDB88320) of a stream, as a signed 32-bit value like Vault's File.Cksum.</summary>
    public static int Crc32(Stream stream)
    {
        var table = CrcTable.Value;
        var crc = 0xFFFFFFFFu;
        var buf = new byte[81920];
        int n;
        while ((n = stream.Read(buf, 0, buf.Length)) > 0)
            for (var i = 0; i < n; i++) crc = table[(crc ^ buf[i]) & 0xFF] ^ (crc >> 8);
        return unchecked((int)(crc ^ 0xFFFFFFFFu));
    }

    private static readonly Lazy<uint[]> CrcTable = new(() =>
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    });

    public const string ReReleaseStep = "re_release";

    /// <summary>
    /// The revise path for a Released drawing whose model changed: move to Work in Progress, sign off, and only after a successful sign-off
    /// return to Released. The drawing is supposed to change, so a sign-off that fails (or a first move that reported failure but reached
    /// Work in Progress) leaves it in Work in Progress at its new revision: the row fails and the model's parents are held back. A failed
    /// return to Released is step re_release. A lost session (BridgeException not_signed_in) on the first move propagates; later ones are
    /// reported and the host rethrows. revisionNow reads the current revision for the message.
    /// </summary>
    public static ReleaseOutcome ReviseAndRelease(Action toWorkInProgress, Func<string> actualState, Action signOff, Func<string> toReleased,
                                                  Func<string>? revisionNow = null, string editState = DataCardFix.WorkInProgressState,
                                                  string returnState = DataCardFix.ReleasedState)
    {
        string Rev()
        {
            try { var r = revisionNow?.Invoke() ?? ""; return string.IsNullOrWhiteSpace(r) ? "its new revision" : r.Trim(); }
            catch (Exception) { return "its new revision"; }
        }
        string LeftInWip(string why) => $"{why}. The drawing is left in {editState} at {Rev()}; fix and re-run.";
        string? first = null;
        try { toWorkInProgress(); }
        catch (BridgeException ex) when (ex.Code == "not_signed_in") { throw; }
        catch (Exception ex) { first = ex.Message; }
        if (first is not null)
        {
            string actual;
            try { actual = actualState(); } catch (Exception) { actual = "unknown"; }
            if (!string.Equals(actual, editState, StringComparison.OrdinalIgnoreCase))
                return ReleaseOutcome.Fail("revise", $"{first} (the drawing is still {actual})");
        }
        if (first is not null) return ReleaseOutcome.Fail("revise", LeftInWip(first.TrimEnd('.') + $" (the drawing reached {editState} anyway and was not signed)"));
        try { signOff(); }
        catch (BridgeException ex) when (ex.Code == "not_signed_in") { return ReleaseOutcome.Fail("sign off", LeftInWip(ex.Message.TrimEnd('.'))); }
        catch (Exception ex) { return ReleaseOutcome.Fail("sign off", LeftInWip(ex.Message.TrimEnd('.'))); }
        string revision;
        try { revision = toReleased(); }
        catch (Exception ex)
        {
            string actual;
            try { actual = actualState(); } catch (Exception) { actual = "unknown"; }
            return ReleaseOutcome.Fail(ReReleaseStep, $"Could not return the signed drawing to {returnState}: {ex.Message}"
                + $" It is in {actual}; change it to {returnState} in Vault by hand.");
        }
        return ReleaseOutcome.Done(revision);
    }

    /// <summary>Check-in and state-change comment: the sign-off name, plus the Vault login that clicked when known.</summary>
    public static string SignOffComment(string approver, string? vaultUser = null) =>
        $"Approved by {approver}" + (string.IsNullOrWhiteSpace(vaultUser) ? "" : $" (Vault user {vaultUser.Trim()})");

    /// <summary>Message for a save failure on a document the user had open in Inventor (it is read-only or refused the save).</summary>
    public static string OpenDocumentSaveFailure(string detail, string undone) =>
        $"Inventor kept the open file read-only; close it and click I Approve! again. ({detail}; {undone})";
}

/// <summary>
/// Runs the release one step at a time (the form pumps messages between steps): first Get Latest, then each row in order. Ready rows are
/// re-checked for hold-back against the results so far; Stop and a lost Vault session leave the remaining rows not attempted.
/// </summary>
public sealed class ReleaseRun
{
    private readonly IReadOnlyList<ReleaseRow> _rows;
    private readonly string _approver;
    private readonly IReleaseWriter _writer;
    private readonly ReleaseResult?[] _results;
    private readonly Dictionary<string, ReleaseStatus> _statusOf = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _drawings;
    private bool _gotLatest;
    private bool _stop;
    private bool _reviseStopped;
    private int _next;

    public ReleaseRun(IReadOnlyList<ReleaseRow> rows, string approver, IReleaseWriter writer)
    {
        _rows = rows; _approver = approver; _writer = writer;
        _results = new ReleaseResult?[rows.Count];
        foreach (var (k, n) in ReleasePlan.NamesOf(rows.Select(r => r.Spec))) _names[k] = n;
        _drawings = ReleasePlan.DrawingsOf(rows.Select(r => r.Spec));
    }

    public bool Done { get; private set; }

    /// <summary>Why the run stopped early (Stop, signed out, Get Latest failed); null when it ran to the end.</summary>
    public string? StoppedBecause { get; private set; }

    public IReadOnlyList<string> GetLatestNotes { get; private set; } = Array.Empty<string>();

    public void RequestStop() => _stop = true;

    public ReleaseResult? ResultAt(int i) => _results[i];

    /// <summary>Every row's result once Done (rows never reached are not attempted).</summary>
    public IReadOnlyList<ReleaseResult> Results => _results.Select((r, i) => r ?? Initial(_rows[i], "")).ToList();

    private static ReleaseResult Initial(ReleaseRow r, string notAttemptedDetail) => r.Status switch
    {
        ReleaseStatus.AlreadyReleased => new ReleaseResult(r, ReleaseStatus.AlreadyReleased, ""),
        ReleaseStatus.Blocked => new ReleaseResult(r, ReleaseStatus.Blocked, string.Join("; ", r.Blockers)),
        _ => new ReleaseResult(r, ReleaseStatus.NotAttempted, notAttemptedDetail),
    };

    private void Finish(string? because, string detail)
    {
        for (; _next < _rows.Count; _next++) Set(_next, Initial(_rows[_next], detail));
        StoppedBecause = because;
        Done = true;
    }

    private void Set(int i, ReleaseResult r)
    {
        _results[i] = r;
        _statusOf[_rows[i].Spec.Key] = r.Status;
    }

    /// <summary>Does one unit of work. Returns the row index it finished, -1 for the Get Latest step, or null when the run is done.</summary>
    public int? Step()
    {
        if (Done) return null;
        if (!_gotLatest)
        {
            _gotLatest = true;
            if (_stop) { Finish("stopped", "stopped before this file"); return null; }
            try { GetLatestNotes = _writer.GetLatest(); }
            catch (BridgeException ex) when (ex.Code == "not_signed_in") { Finish("signed out of Vault", "signed out of Vault"); return null; }
            catch (Exception ex) { Finish("Get Latest failed: " + ex.Message, "Get Latest failed; nothing was released"); return null; }
            return -1;
        }
        if (_next >= _rows.Count) { Done = true; return null; }
        if (_stop) { Finish("stopped", "stopped before this file"); return null; }
        var i = _next++;
        var row = _rows[i];
        if (row.Status is ReleaseStatus.AlreadyReleased or ReleaseStatus.Blocked) { Set(i, Initial(row, "")); return i; }
        var by = ReleasePlan.HeldBackBy(row.Spec, row.Facts, k => _statusOf.TryGetValue(k, out var s) ? s : null, false,
                                        k => _names.TryGetValue(k, out var n) ? n : Path.GetFileName(k),
                                        k => _drawings.TryGetValue(k, out var d) ? d : null);
        if (by is not null)
        {
            var text = ReleasePlan.HeldBackText(by) + (row.Status == ReleaseStatus.Revise ? " (the drawing stays Released at its current revision)" : "");
            Set(i, new ReleaseResult(row, ReleaseStatus.HeldBack, text));
            return i;
        }
        if (row.Status == ReleaseStatus.HeldBack)
        {
            // Defensive: the preview predicted a hold-back that the results no longer show; never release what the preview did not offer.
            Set(i, new ReleaseResult(row, ReleaseStatus.NotAttempted, "held back in the preview; run Preview again"));
            return i;
        }
        if (row.Status == ReleaseStatus.Revise && _reviseStopped)
        {
            Set(i, new ReleaseResult(row, ReleaseStatus.NotAttempted, "stopped: an earlier drawing could not be returned to Released"));
            return i;
        }
        try
        {
            var o = _writer.Release(row, _approver);
            if (!o.Ok && o.Step == ReleasePlan.ReReleaseStep) _reviseStopped = true;
            Set(i, o.Ok
                ? new ReleaseResult(row, ReleaseStatus.Released, o.Warning ?? "", o.RevisionAfter)
                : new ReleaseResult(row, ReleaseStatus.Failed, $"{o.Step}: {o.Message}"));
        }
        catch (BridgeException ex) when (ex.Code == "not_signed_in")
        {
            Set(i, new ReleaseResult(row, ReleaseStatus.Failed, "sign in: " + ex.Message));
            Finish("signed out of Vault", "signed out of Vault");
        }
        catch (Exception ex) { Set(i, new ReleaseResult(row, ReleaseStatus.Failed, "unexpected: " + ex.Message)); }
        return i;
    }
}
