using System.Globalization;
using System.Text.Json.Nodes;

namespace SFVaultBridge.Core;

public enum PropSet { Design, Custom, Summary }

public sealed record PropRef(PropSet Set, string Name);

public sealed record PropChange(PropRef Prop, string? From, string To);

/// <summary>What the Vault says about the latest version of one file.</summary>
public sealed record DataCardFileInfo(string VaultPath, string Name, string Category, string State, bool Released,
                                      bool CheckedOut, string CheckedOutBy, long Version,
                                      string? Unreadable = null, string Revision = "");

/// <summary>State and revision of a file after a lifecycle change, as Vault reports them.</summary>
public sealed record DataCardStateChange(string State, string Revision);

/// <summary>Result of looking up the two lifecycle moves a revise needs. Problem is set when either is missing. EditState is the editable state in the
/// file's own lifecycle (Work in Progress, Library Edit, ...); ReturnState is the exact state the file started in (Released, Library, ...).</summary>
public sealed record RevisePlan(string? Problem, string ToWorkInProgress = "", string ToReleased = "",
                                string EditState = DataCardFix.WorkInProgressState, string ReturnState = DataCardFix.ReleasedState);

/// <summary>One state of a lifecycle definition as Vault reports it (LfCycState).</summary>
public sealed record LifecycleState(long Id, string Name, bool Released, bool Obsolete, int Order);

/// <summary>One transition of a lifecycle definition (LfCycTrans).</summary>
public sealed record LifecycleTransition(long Id, long FromId, long ToId);

/// <summary>Picks the edit state for a revise from the file's own lifecycle by flags, never by the names Released / Work in Progress.</summary>
public static class LifecycleRevise
{
    public const string EditSuffix = " Edit";

    /// <summary>True when the state with this id is flagged released (any name).</summary>
    public static bool IsReleasedState(IReadOnlyList<LifecycleState> states, long stateId) =>
        states.FirstOrDefault(s => s.Id == stateId)?.Released == true;

    /// <summary>The edit state and the way back, or a Problem. Edit target: same definition, not released, not obsolete, reachable from the current
    /// state by an allowed transition and with an allowed transition back to the original state. Preference: Work in Progress, then a name ending in
    /// " Edit", then the lowest display order. Changes nothing.</summary>
    public static RevisePlan Plan(IReadOnlyList<LifecycleState> states, IReadOnlyList<LifecycleTransition> transitions,
                                  ISet<long> allowedTransitionIds, long currentStateId)
    {
        var cur = states.FirstOrDefault(s => s.Id == currentStateId);
        if (cur is null) return new RevisePlan(DataCardFix.NoTransition("the current state", "an editable state"));
        var curName = cur.Name;
        if (cur.Obsolete) return new RevisePlan($"{curName} is an obsolete state; the file is not revised");
        var forward = states.Where(s => !s.Released && !s.Obsolete && s.Id != cur.Id)
            .Select(s => (State: s, Trans: transitions.FirstOrDefault(t => t.FromId == cur.Id && t.ToId == s.Id)))
            .Where(x => x.Trans is not null).ToList();
        if (forward.Count == 0) return new RevisePlan(DataCardFix.NoTransition(curName, "an editable state"));
        var allowedForward = forward.Where(x => allowedTransitionIds.Contains(x.Trans!.Id)).ToList();
        if (allowedForward.Count == 0)
            return new RevisePlan($"you are not allowed to change the state from {curName} to {Preferred(forward.Select(x => x.State)).Name}");
        var withBack = allowedForward
            .Select(x => (x.State, Back: transitions.FirstOrDefault(t => t.FromId == x.State.Id && t.ToId == cur.Id)))
            .ToList();
        var usable = withBack.Where(x => x.Back is not null && allowedTransitionIds.Contains(x.Back.Id)).Select(x => x.State).ToList();
        if (usable.Count == 0)
        {
            var pick = Preferred(allowedForward.Select(x => x.State));
            var back = withBack.First(x => x.State.Id == pick.Id).Back;
            return new RevisePlan(back is null ? DataCardFix.NoTransition(pick.Name, curName)
                                               : $"you are not allowed to change the state from {pick.Name} to {curName}");
        }
        var edit = Preferred(usable);
        return new RevisePlan(null, $"{curName} -> {edit.Name}", $"{edit.Name} -> {curName}", edit.Name, curName);
    }

    private static LifecycleState Preferred(IEnumerable<LifecycleState> candidates) => candidates
        .OrderBy(s => string.Equals(s.Name, DataCardFix.WorkInProgressState, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
        .ThenBy(s => s.Name.EndsWith(EditSuffix, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
        .ThenBy(s => s.Order).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).First();
}

/// <summary>Result of a successful check-in. Version is -1 when it could not be read back; Warning says why.</summary>
public sealed record DataCardApplied(long Version, string? Warning = null);

/// <summary>One file association row as Vault reports it.</summary>
public sealed record AssocRow(long ParFileId, long CldFileId, string Type, string? RefId, string? Source, string? ExpectedVaultPath);

/// <summary>A "files" entry: a bare file name or a full $/ Vault path.</summary>
public sealed record DataCardFileRef(string Text, bool IsPath);

/// <summary>Raised by the host when the file must be left alone (for example open and unsaved in Inventor).</summary>
public sealed class DataCardSkipException : Exception
{
    public DataCardSkipException(string reason) : base(reason) { }
}

/// <summary>Raised by the host when a step fails; Step names it (read, checkout, open, set, save, checkin).</summary>
public sealed class DataCardStepException : Exception
{
    public string Step { get; }

    public DataCardStepException(string step, string message) : base(message) => Step = step;
}

/// <summary>The Vault and Inventor side of the data card fix. Core only decides; the host touches the world.</summary>
public interface IDataCardHost
{
    /// <summary>Latest Vault files matching the reference: exact case-insensitive name for a bare name, the path for a $/ path. Empty when none.</summary>
    IReadOnlyList<DataCardFileInfo> Find(DataCardFileRef file);

    /// <summary>Current values (null when the property does not exist). Downloads the latest version without children, opens it invisibly, closes without saving.
    /// Throws DataCardSkipException when the file is open in Inventor with unsaved changes.</summary>
    IReadOnlyDictionary<PropRef, string?> Read(DataCardFileInfo file, IReadOnlyList<PropRef> props);

    /// <summary>Check out, set, save, check in with the comment. Any failure before the check-in succeeds undoes the checkout; nothing after it does.</summary>
    DataCardApplied Apply(DataCardFileInfo file, IReadOnlyList<PropChange> changes, string comment);

    /// <summary>Looks up, in the file's own lifecycle definition and by state flags (see LifecycleRevise), the transition from its released state to an
    /// editable state (Work in Progress, Library Edit, ...) and the way back to the exact original state, both of which the signed-in user may take. Changes nothing. Fails closed (Problem) on any read failure.</summary>
    RevisePlan PlanRevise(DataCardFileInfo file);

    /// <summary>Moves the file to the state with that name, only along an existing allowed transition. Throws DataCardStepException when it cannot.</summary>
    DataCardStateChange ChangeState(DataCardFileInfo file, string toStateName, string comment);

    /// <summary>True when the file is linked to an item. Null when that could not be read (never an error).</summary>
    bool? LinkedToItem(DataCardFileInfo file);

    /// <summary>The file's Property Compliance value as Vault shows it, or null when it cannot be read (never an error).</summary>
    string? ReadPropertyCompliance(DataCardFileInfo file);
}

/// <summary>Summary holds Summary Information properties (for example Title), applied with the same dry-run, compare and apply rules as Design.</summary>
public sealed record DataCardRequest(IReadOnlyList<string> Files, IReadOnlyDictionary<string, string> Design,
                                     IReadOnlyDictionary<string, string> Custom, bool PartNumberFromFileName,
                                     bool DryRun, string? Comment, bool ReviseReleased = false,
                                     IReadOnlyDictionary<string, string>? Summary = null);

/// <summary>Rules and orchestration for the fix_data_card bridge operation.</summary>
public static class DataCardFix
{
    public const string PartNumber = "Part Number";
    public const string ContentCenterCategory = "Part - Content Center";
    public const string ContentCenterRoot = "$/Content Center Files";
    public const string ReleasedState = "Released";
    public const string WorkInProgressState = "Work in Progress";

    /// <summary>Design Tracking properties Inventor stores as numbers (currency); the request string is converted before assigning.</summary>
    private static readonly HashSet<string> NumericDesign = new(StringComparer.OrdinalIgnoreCase) { "Cost" };

    /// <summary>The Inventor property set name for each set.</summary>
    public static string SetName(PropSet set) => set switch
    {
        PropSet.Design => "Design Tracking Properties",
        PropSet.Summary => "Inventor Summary Information",
        _ => "Inventor User Defined Properties",
    };

    public static bool IsNumeric(PropRef p) => p.Set == PropSet.Design && NumericDesign.Contains(p.Name);

    /// <summary>Invariant-culture parse of a numeric property value; false for blanks, text, NaN and infinity.</summary>
    public static bool TryParseNumber(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return false;
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    /// <summary>Invariant, shortest round-trip form ("12.5"); the same text is shown in previews and compared with the current value.</summary>
    public static string FormatNumber(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Inventor stores currency to 4 decimals, so values are rounded to 4 before they are compared or written; otherwise a rerun would
    /// never see "no change" and would revise a Released file again.</summary>
    public static double RoundCurrency(double value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);

    /// <summary>Normalizes the number-typed targets to invariant text; a non-number is a per-file error ("Cost must be a number").</summary>
    public static IReadOnlyDictionary<PropRef, string> NormalizeWanted(IReadOnlyDictionary<PropRef, string> wanted)
    {
        var o = new Dictionary<PropRef, string>();
        foreach (var (p, v) in wanted)
        {
            if (!IsNumeric(p)) { o[p] = v; continue; }
            if (!TryParseNumber(v, out var d)) throw new DataCardStepException("set", $"{p.Name} must be a number");
            o[p] = FormatNumber(RoundCurrency(d));
        }
        return o;
    }

    private static IReadOnlyDictionary<PropRef, string?> NormalizeCurrent(IReadOnlyDictionary<PropRef, string?> current)
    {
        var o = new Dictionary<PropRef, string?>();
        foreach (var (p, v) in current) o[p] = IsNumeric(p) && TryParseNumber(v, out var d) ? FormatNumber(RoundCurrency(d)) : v;
        return o;
    }

    public static DataCardFileRef ParseFile(string text)
    {
        var t = (text ?? "").Trim();
        return new DataCardFileRef(t, t.StartsWith("$/", StringComparison.Ordinal));
    }

    public static DataCardRequest ParseRequest(JsonObject body)
    {
        if (body["files"] is not JsonArray arr || arr.Count == 0)
            throw new BridgeException("bad_request", "'files' is required and must be a non-empty list of file names or $/ paths.");
        var files = arr.Select((n, i) => n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)
            ? s.Trim() : throw new BridgeException("bad_request", $"'files[{i}]' must be a non-empty string.")).ToList();
        var comment = Args.OptStr(body, "comment", "").Trim();
        return new DataCardRequest(files, Args.StrMap(body, "design"), Args.StrMap(body, "custom"),
            Args.Bool(body, "partNumberFromFileName", true), Args.Bool(body, "dryRun", true), comment.Length > 0 ? comment : null,
            Args.Bool(body, "reviseReleased", false), Args.StrMap(body, "summary"));
    }

    /// <summary>Why the file may not be fixed, or null when it may.</summary>
    public static string? SkipReason(DataCardFileInfo f, bool reviseReleased = false)
    {
        var ext = Path.GetExtension(f.Name);
        if (ext.Equals(".iam", StringComparison.OrdinalIgnoreCase)) return "assemblies are not supported yet";
        if (!ext.Equals(".ipt", StringComparison.OrdinalIgnoreCase))
            return $"{f.Name} is not an .ipt file.";
        if (f.Unreadable is not null) return f.Unreadable;
        if (f.CheckedOut)
            return $"{f.Name} is checked out by {(string.IsNullOrWhiteSpace(f.CheckedOutBy) ? "someone" : f.CheckedOutBy)}. Check it in or undo the checkout first.";
        // Content Center files are never revised, with or without the opt-in, so this is checked before the Released rule.
        if (string.Equals(f.Category, ContentCenterCategory, StringComparison.OrdinalIgnoreCase)
            || f.VaultPath.StartsWith(ContentCenterRoot, StringComparison.OrdinalIgnoreCase))
            return $"{f.Name} is a Content Center file.";
        if (IsReleased(f))
        {
            if (!reviseReleased) return $"{f.Name} is in state {StateText(f.State)} (released). Tick Revise to edit it.";
        }
        return null;
    }

    private static string StateText(string state) => string.IsNullOrWhiteSpace(state) ? "unknown" : state.Trim();

    /// <summary>Released means the state's released flag (any name: Released, Library, ...); a state literally named Released also counts, fail closed.</summary>
    public static bool IsReleased(DataCardFileInfo f) =>
        f.Released || string.Equals(f.State, ReleasedState, StringComparison.OrdinalIgnoreCase);

    /// <summary>Message for a save failure on a document the user had open in Inventor (it is read-only or refused the save).</summary>
    public static string OpenDocumentSaveFailure(string detail, string undone) =>
        $"Inventor kept the open part read-only; close it and click Fix Data Card again. ({detail}; {undone})";

    public static string NoTransition(string from, string to) => $"no transition from {from} to {to}";

    public static IReadOnlyList<PropRef> Targets(DataCardRequest r, string fileName, out IReadOnlyDictionary<PropRef, string> wanted)
    {
        var w = new Dictionary<PropRef, string>();
        foreach (var (k, v) in r.Design) w[new PropRef(PropSet.Design, k)] = v;
        foreach (var (k, v) in r.Custom) w[new PropRef(PropSet.Custom, k)] = v;
        if (r.Summary is not null) foreach (var (k, v) in r.Summary) w[new PropRef(PropSet.Summary, k)] = v;
        var pn = new PropRef(PropSet.Design, PartNumber);
        if (r.PartNumberFromFileName && !w.ContainsKey(pn)) w[pn] = Path.GetFileNameWithoutExtension(fileName);
        wanted = w;
        return w.Keys.ToList();
    }

    /// <summary>Target values that differ from the current ones; a missing property reads as empty. Equal values are omitted.</summary>
    public static IReadOnlyList<PropChange> ComputeChanges(IReadOnlyDictionary<PropRef, string> wanted, IReadOnlyDictionary<PropRef, string?> current)
    {
        var changes = new List<PropChange>();
        foreach (var (p, to) in wanted)
        {
            current.TryGetValue(p, out var from);
            if (string.Equals(from ?? "", to, StringComparison.Ordinal)) continue;
            changes.Add(new PropChange(p, from, to));
        }
        return changes;
    }

    /// <summary>The child Dependency and Attachment rows of the parent file, de-duplicated, to re-pass on check-in so the new version keeps its associations.</summary>
    public static IReadOnlyList<AssocRow> ChildAssociations(IEnumerable<AssocRow> rows, long parentFileId) =>
        rows.Where(r => r.ParFileId == parentFileId && (r.Type == "Dependency" || r.Type == "Attachment"))
            .GroupBy(r => (r.CldFileId, r.Type, r.RefId))
            .Select(g => g.First()).ToList();

    public static string DefaultComment(IEnumerable<PropChange> changes) =>
        "Data card fix: " + string.Join(", ", changes.Select(c => c.Prop.Name).Distinct());

    public static JsonObject Run(DataCardRequest req, IDataCardHost host, Action<string>? log = null) => Run(req, host, log, new RunState());

    /// <summary>Same run, but the revise stop is shared: a caller that runs one request per file (the ribbon form) passes one RunState so a failed
    /// return to Released still stops every later Released file.</summary>
    public static JsonObject Run(DataCardRequest req, IDataCardHost host, Action<string>? log, RunState run)
    {
        var results = new JsonArray();
        var counts = new Dictionary<string, int> { ["preview"] = 0, ["fixed"] = 0, ["no_change"] = 0, ["skipped"] = 0, ["error"] = 0 };
        foreach (var arg in req.Files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            JsonObject r;
            try { r = RunOne(arg, req, host, run); }
            catch (BridgeException ex) when (ex.Code == "not_signed_in")
            {
                // The session dropped: keep what is already done and mark this and every remaining file as an error.
                foreach (var rest in req.Files.Distinct(StringComparer.OrdinalIgnoreCase).SkipWhile(f => !string.Equals(f, arg, StringComparison.OrdinalIgnoreCase)))
                {
                    var e = Error(rest, "sign_in", ex.Message);
                    counts["error"]++;
                    log?.Invoke($"fix_data_card {rest}: error {ex.Message}");
                    results.Add(e);
                }
                break;
            }
            var status = r["status"]!.GetValue<string>();
            counts[status]++;
            var detail = r["reason"] ?? r["message"] ?? r["version"];
            log?.Invoke($"fix_data_card {r["file"]}: {status}" + (detail is null ? "" : " " + detail));
            results.Add(r);
        }
        return new JsonObject
        {
            ["results"] = results,
            ["counts"] = new JsonObject(counts.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value))),
        };
    }

    public sealed class RunState { public bool ReviseStopped { get; internal set; } }

    public const string ReviseStoppedReason = "stopped: an earlier file could not be returned to Released";

    private static JsonObject RunOne(string arg, DataCardRequest req, IDataCardHost host, RunState run)
    {
        DataCardFileInfo? info = null;
        try
        {
            var matches = host.Find(ParseFile(arg));
            if (matches.Count == 0) return Skipped(arg, $"{arg} was not found in Vault.");
            if (matches.Count > 1)
                return Error(arg, "resolve", $"{arg} matches {matches.Count} files ({string.Join(", ", matches.Select(m => m.VaultPath))}). Pass the full $/ path.");
            info = matches[0];
            var skip = SkipReason(info, req.ReviseReleased);
            if (skip is not null) return Skipped(info.VaultPath, skip);

            var props = Targets(req, info.Name, out var rawWanted);
            var wanted = NormalizeWanted(rawWanted);
            IReadOnlyDictionary<PropRef, string?> current;
            try { current = NormalizeCurrent(host.Read(info, props)); }
            catch (Exception ex) when (ex is not DataCardSkipException and not DataCardStepException and not BridgeException)
            { throw new DataCardStepException("read", ex.Message); }
            var changes = ComputeChanges(wanted, current);
            if (changes.Count == 0) return new JsonObject { ["file"] = info.VaultPath, ["status"] = "no_change", ["changes"] = new JsonObject() };

            var revise = IsReleased(info);
            RevisePlan? plan = null;
            if (revise && run.ReviseStopped) return Skipped(info.VaultPath, ReviseStoppedReason);
            if (revise)
            {
                // Both moves are looked up before anything changes, so a file is never stranded in Work in Progress for want of a way back.
                plan = host.PlanRevise(info);
                if (plan.Problem is not null) return Skipped(info.VaultPath, plan.Problem);
            }
            var linked = host.LinkedToItem(info);

            if (req.DryRun)
            {
                var preview = new JsonObject { ["file"] = info.VaultPath, ["status"] = "preview", ["changes"] = ChangesJson(changes) };
                if (revise)
                {
                    preview["would_revise"] = true;
                    preview["transitions"] = new JsonArray(plan!.ToWorkInProgress, plan.ToReleased);
                }
                if (linked == true) preview["linked_to_item"] = true;
                return preview;
            }

            var comment = req.Comment ?? DefaultComment(changes);
            JsonObject result = revise ? ReviseAndFix(info, changes, comment, host, plan!) : FixOnly(info, changes, comment, host);
            // A systemic cause (criteria, restriction, lost session) would hit every Released file the same way: stop revising after the first.
            if (revise && result["step"]?.GetValue<string>() == "re_release") run.ReviseStopped = true;
            if (linked == true) result["linked_to_item"] = true;
            return result;
        }
        catch (DataCardSkipException ex) { return Skipped(info?.VaultPath ?? arg, ex.Message); }
        catch (DataCardStepException ex) { return Error(info?.VaultPath ?? arg, ex.Step, ex.Message); }
        catch (BridgeException ex) when (ex.Code == "not_signed_in") { throw; }
        catch (BridgeException ex) { return Error(info?.VaultPath ?? arg, ex.Code, ex.Message); }
        catch (Exception ex) { return Error(info?.VaultPath ?? arg, "unexpected", ex.Message); }
    }

    private static JsonObject FixOnly(DataCardFileInfo info, IReadOnlyList<PropChange> changes, string comment, IDataCardHost host)
    {
        var applied = host.Apply(info, changes, comment);
        var fixedResult = new JsonObject { ["file"] = info.VaultPath, ["status"] = "fixed", ["version"] = applied.Version >= 0 ? applied.Version : null, ["changes"] = ChangesJson(changes) };
        if (applied.Warning is not null) fixedResult["warning"] = applied.Warning;
        return fixedResult;
    }

    /// <summary>Released state to the edit state, the normal checkout/edit/check-in, then back to the original state. Once the first move was attempted and the file is
    /// found in Work in Progress, the way back is always attempted, whatever happened in the middle, and a failure to get back is reported loudly with
    /// the actual state.</summary>
    private static JsonObject ReviseAndFix(DataCardFileInfo info, IReadOnlyList<PropChange> changes, string comment, IDataCardHost host, RevisePlan plan)
    {
        var revisionBefore = info.Revision;
        string? firstError = null;
        try { host.ChangeState(info, plan.EditState, comment); }
        catch (Exception ex) { firstError = ex.Message; }

        if (firstError is not null)
        {
            // A move that committed but reported failure (timeout, odd read-back) leaves the file in Work in Progress: bring it back, do not edit it.
            var actual = ActualState(host, info);
            if (!string.Equals(actual, plan.EditState, StringComparison.OrdinalIgnoreCase))
            {
                var o = Error(info.VaultPath, "revise", firstError);
                o["state_after"] = actual;
                o["revision_before"] = info.Revision;
                o["transitions"] = TransitionsJson(plan);
                return o;
            }
        }

        // Step 2: edit on the refreshed file (the state change can move the version). Whatever fails is held until the file is back.
        DataCardApplied? applied = null;
        string? editStep = null, editError = null;
        if (firstError is null)
        {
            try
            {
                var fresh = Refresh(host, info);
                applied = host.Apply(fresh, changes, comment);
            }
            catch (DataCardSkipException ex) { editStep = "skipped"; editError = ex.Message; }
            catch (DataCardStepException ex) { editStep = ex.Step; editError = ex.Message; }
            catch (BridgeException ex) { editStep = ex.Code; editError = ex.Message; }
            catch (Exception ex) { editStep = "unexpected"; editError = ex.Message; }
        }
        else { editStep = "revise"; editError = firstError + " (the file was found in " + plan.EditState + " and was not edited)"; }

        // Step 3: back to Released, always.
        DataCardStateChange? back = null;
        string? backError = null;
        try { back = host.ChangeState(Refresh(host, info), plan.ReturnState, comment); }
        catch (Exception ex) { backError = ex.Message; }

        var result = new JsonObject { ["file"] = info.VaultPath, ["changes"] = ChangesJson(changes), ["revision_before"] = revisionBefore, ["transitions"] = TransitionsJson(plan) };
        if (backError is not null)
        {
            result["status"] = "error";
            result["step"] = "re_release";
            result["message"] = $"Could not return the file to {plan.ReturnState}: {backError}" + (editError is null ? "" : $" (the edit also failed at {editStep}: {editError})")
                + $" Change it to {plan.ReturnState} in Vault by hand; if it is linked to an item, check the item.";
            result["state_after"] = ActualState(host, info);
            if (editError is not null) result["edit_error"] = editError;
            if (applied is not null) result["version"] = applied.Version >= 0 ? applied.Version : null;
            result["property_compliance"] = null;
            return result;
        }
        result["state_after"] = back!.State;
        result["revision_after"] = back.Revision;
        string? compliance = null;
        try { compliance = host.ReadPropertyCompliance(Refresh(host, info)); } catch (Exception) { }
        result["property_compliance"] = compliance;
        if (editError is not null)
        {
            result["status"] = "error";
            result["step"] = editStep;
            result["message"] = editError + $" (the file was returned to {plan.ReturnState})";
            result["re_released"] = true;
            return result;
        }
        result["status"] = "fixed";
        result["version"] = applied!.Version >= 0 ? applied.Version : null;
        if (applied.Warning is not null) result["warning"] = applied.Warning;
        return result;
    }

    private static JsonArray TransitionsJson(RevisePlan plan) => new(plan.ToWorkInProgress, plan.ToReleased);

    private static DataCardFileInfo Refresh(IDataCardHost host, DataCardFileInfo info)
    {
        var m = host.Find(ParseFile(info.VaultPath));
        if (m.Count != 1) throw new DataCardStepException("resolve", $"{info.VaultPath} could not be re-read from Vault.");
        return m[0];
    }

    private static string ActualState(IDataCardHost host, DataCardFileInfo info)
    {
        try { return Refresh(host, info).State; }
        catch (Exception) { return "unknown"; }
    }

    private static JsonObject ChangesJson(IEnumerable<PropChange> changes)
    {
        var o = new JsonObject();
        foreach (var c in changes) o[c.Prop.Name] = new JsonObject { ["from"] = c.From, ["to"] = c.To };
        return o;
    }

    private static JsonObject Skipped(string file, string reason) => new() { ["file"] = file, ["status"] = "skipped", ["reason"] = reason };

    private static JsonObject Error(string file, string step, string message) =>
        new() { ["file"] = file, ["status"] = "error", ["step"] = step, ["message"] = message };
}
