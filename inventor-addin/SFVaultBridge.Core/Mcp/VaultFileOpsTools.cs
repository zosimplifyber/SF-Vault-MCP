using System.Text.Json.Nodes;

namespace SFVaultBridge.Core;

/// <summary>One file a plan will act on, as previewed. Target is the state name for a state change.</summary>
public sealed record FileOpRow(long MasterId, string VaultPath, string Name, long Version, string State, bool CheckedOut, string? Target);

/// <summary>A file leaving a released state in a state-change plan.</summary>
public sealed record LeavingInfo(string File, string Revision, string From, string To);

/// <summary>Everything an apply needs to remember from its preview.</summary>
public sealed record FileOpPlan(string Op, string Comment, LocalCopyChoice LocalCopy, bool Reopen, bool KeepCheckedOut, bool DiscardLocal,
                                IReadOnlyList<FileOpRow> Rows, IReadOnlyList<LeavingInfo> Leaving);

/// <summary>Check-out, check-in, undo check-out and lifecycle state change as preview/apply tool pairs. Every preview is all-or-nothing: with any blocker
/// no preview_hash is issued. Vault and Inventor work goes through <see cref="IVaultFileOps"/>.</summary>
public static class VaultFileOpsTools
{
    public const string Checkout = "vault_checkout";
    public const string CheckinOp = "vault_checkin";
    public const string Undo = "vault_undo_checkout";
    public const string State = "vault_change_state";
    public static readonly TimeSpan WriteTimeout = TimeSpan.FromMinutes(15);

    public static IReadOnlyList<McpTool> Build(IVaultFileOps? ops, PreviewStore<FileOpPlan> store)
    {
        if (ops is null) return Array.Empty<McpTool>();
        (string, JsonObject) FilesProp() => ("files", new JsonObject
        {
            ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["minItems"] = 1, ["maxItems"] = FileOpRules.MaxFiles,
            ["description"] = $"File names such as CD-001958.ipt (with extension) or full $/ Vault paths. At most {FileOpRules.MaxFiles}.",
        });
        JsonObject ApplySchema(string previewTool) => Schema.Obj(Schema.Props(
            ("preview_hash", Schema.Str($"The preview_hash returned by {previewTool}.")),
            ("confirm", Schema.Bool("Must be true. Set it only after the engineer has seen the preview and said yes."))), "preview_hash", "confirm");
        string Apply = "Needs the preview_hash from the matching preview (valid 10 minutes, one use) and confirm true, only after the engineer approved the preview. " +
                       "Refuses when a file changed in Vault since the preview. Runs as a job: a long run returns a job_id to poll with job_status.";

        return new[]
        {
            new McpTool("vault_checkout_preview", "Preview Vault check-out",
                "Plan checking out files: latest version to the local working folder, local copy writable. Writes nothing. " +
                "Lists each file with state, who holds it (a file someone else has checked out is refused) and whether the local copy differs from Vault. " +
                "A differing local copy is never overwritten silently: choose local_copy \"keep\" (check out, keep the local file) or \"replace\" (overwrite with Vault's copy). " +
                "If a file is open in Inventor the add-in closes and reopens it after the checkout so Inventor sees it writable (reopen_in_inventor, default true). " +
                "Any blocker means no preview_hash. Show the engineer, then call vault_checkout_apply.",
                Schema.Obj(Schema.Props(FilesProp(),
                    ("comment", Schema.Str("Check-out comment (default \"" + FileOpRules.DefaultCheckoutComment + "\").")),
                    ("local_copy", new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("keep", "replace"), ["description"] = "What to do when the local copy differs from Vault." }),
                    ("reopen_in_inventor", Schema.Bool("Close and reopen an open, clean Inventor document after the checkout so it is writable (default true).", true))), "files"),
                McpToolKind.Preview, a => PreviewCheckout(ops, store, a), WriteTimeout),

            new McpTool("vault_checkout_apply", "Check out Vault files",
                "Checks out the previewed files to you. A WRITE to Vault. " + Apply, ApplySchema("vault_checkout_preview"),
                McpToolKind.Write, a => ApplyPlan(ops, store, a, Checkout), WriteTimeout),

            new McpTool("vault_checkin_preview", "Preview Vault check-in",
                "Plan checking in files you have checked out, with a comment. Writes nothing. " +
                "Inventor files keep their Vault associations: the previous version's references (drawing to model, assembly to children, attachments) are carried forward, each child at the version matching the file on disk. " +
                "Refused: not checked out by you, open in Inventor with unsaved changes (save first), local file identical to the checked-out version (undo instead), " +
                "or an open document with references the checked-out version lacks (use Vault's Check In in Inventor). Any blocker means no preview_hash.",
                Schema.Obj(Schema.Props(FilesProp(),
                    ("comment", Schema.Str("Check-in comment, required.")),
                    ("keep_checked_out", Schema.Bool("Check in but keep the file checked out (default false).", false))), "files", "comment"),
                McpToolKind.Preview, a => PreviewCheckin(ops, store, a), WriteTimeout),

            new McpTool("vault_checkin_apply", "Check in Vault files",
                "Checks in the previewed files as new versions. A WRITE to Vault. " + Apply, ApplySchema("vault_checkin_preview"),
                McpToolKind.Write, a => ApplyPlan(ops, store, a, CheckinOp), WriteTimeout),

            new McpTool("vault_undo_checkout_preview", "Preview Vault undo check-out",
                "Plan undoing your check-out of files. Writes nothing. Undo discards local edits: when the local copy differs or the Inventor document has unsaved changes it is a blocker unless discard_local_changes is true. " +
                "Someone else's checkout is refused. Any blocker means no preview_hash.",
                Schema.Obj(Schema.Props(FilesProp(),
                    ("discard_local_changes", Schema.Bool("Allow throwing away local edits (default false).", false))), "files"),
                McpToolKind.Preview, a => PreviewUndo(ops, store, a), WriteTimeout),

            new McpTool("vault_undo_checkout_apply", "Undo Vault check-out",
                "Undoes the previewed check-outs; the working copy returns to the Vault version. A WRITE to Vault. " + Apply, ApplySchema("vault_undo_checkout_preview"),
                McpToolKind.Write, a => ApplyPlan(ops, store, a, Undo), WriteTimeout),

            new McpTool("vault_change_state_preview", "Preview Vault state change",
                "Plan moving files to a lifecycle state (for example Work in Progress, For Review, Released, Library), using each file's own lifecycle definition. Writes nothing. " +
                "Per file: current state, target, whether the transition exists and you may take it, the revision after, and blockers (checked out, no transition, children not released). " +
                "LEAVING RELEASED: when a file is moved out of a released state (Released, or any state flagged released, including Library's) the result has top-level leaving_released true " +
                "and a warning line naming each file and its revision; show it prominently. Nothing cascades unless include_children is true (children are moved first, library files are not moved). " +
                "Any blocker means no preview_hash.",
                Schema.Obj(Schema.Props(FilesProp(),
                    ("state", Schema.Str("Target state name, for example Released or Work in Progress.")),
                    ("comment", Schema.Str("State-change comment (default \"State change via SF Vault Bridge\").")),
                    ("include_children", Schema.Bool("Also move each file's Dependency children (not library files) to the target state first. Default false.", false))), "files", "state"),
                McpToolKind.Preview, a => PreviewState(ops, store, a), WriteTimeout),

            new McpTool("vault_change_state_apply", "Change Vault state",
                "Changes the state of the previewed files (children first when included); stops at the first failure. A WRITE to Vault. The result repeats leaving_released. " + Apply,
                ApplySchema("vault_change_state_preview"), McpToolKind.Write, a => ApplyPlan(ops, store, a, State), WriteTimeout),
        };
    }

    // ---- resolving ----

    private sealed record Item(string Ref, FileFacts? F, string? Problem);

    private static List<Item> Resolve(IVaultFileOps ops, JsonObject a)
    {
        if (a["files"] is not JsonArray arr || arr.Count == 0) throw new BridgeException("bad_request", "'files' is required and must be a non-empty list of file names or $/ paths.");
        if (arr.Count > FileOpRules.MaxFiles) throw new BridgeException("bad_request", $"At most {FileOpRules.MaxFiles} files per call.");
        var items = new List<Item>();
        var seen = new HashSet<long>();
        for (var i = 0; i < arr.Count; i++)
        {
            var text = arr[i] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : throw new BridgeException("bad_request", $"'files[{i}]' must be a non-empty string.");
            var found = ops.Lookup(text).Matches;
            if (found.Count == 0) items.Add(new Item(text, null, "not found in Vault"));
            else if (found.Count > 1) items.Add(new Item(text, null, "matches " + found.Count + " files: " + string.Join("; ", found.Take(5).Select(m => m.VaultPath)) + ". Give the full $/ path."));
            else if (seen.Add(found[0].MasterId)) items.Add(new Item(text, found[0], null));
        }
        return items;
    }

    // ---- previews ----

    private static JsonObject Row(Item it, FileFacts? f, IEnumerable<string> blockers, IEnumerable<string> warnings)
    {
        var o = new JsonObject { ["file"] = f?.Name ?? it.Ref };
        if (f is not null)
        {
            o["vault_path"] = f.VaultPath; o["version"] = f.Version; o["state"] = f.State; o["revision"] = f.Revision;
            if (f.CheckedOut) o["checked_out_by"] = FileOpRules.Holder(f);
            o["local_copy_differs"] = f.LocalExists && f.LocalDiffers;
            o["open_in_inventor"] = f.OpenInInventor;
        }
        o["blockers"] = Arr(blockers); o["warnings"] = Arr(warnings);
        return o;
    }

    private static JsonArray Arr(IEnumerable<string> items) => new(items.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());

    private static JsonNode PreviewCheckout(IVaultFileOps ops, PreviewStore<FileOpPlan> store, JsonObject a)
    {
        var choice = FileOpRules.ParseLocalCopy(Args.OptStr(a, "local_copy"));
        var reopen = Args.Bool(a, "reopen_in_inventor", true);
        var comment = Args.OptStr(a, "comment").Trim();
        if (comment.Length == 0) comment = FileOpRules.DefaultCheckoutComment;
        var rows = new List<FileOpRow>();
        var files = new JsonArray();
        var blocked = 0;
        foreach (var it in Resolve(ops, a))
        {
            if (it.F is null) { files.Add(Row(it, null, new[] { it.Problem! }, Array.Empty<string>())); blocked++; continue; }
            var (b, w) = FileOpRules.Checkout(it.F, choice, reopen);
            files.Add(Row(it, it.F, b, w));
            if (b.Count > 0) blocked++; else rows.Add(ToRow(it.F, null));
        }
        return Finish(store, new FileOpPlan(Checkout, comment, choice, reopen, false, false, rows, Array.Empty<LeavingInfo>()), files, blocked, Array.Empty<string>(), false,
                      "vault_checkout_apply");
    }

    private static JsonNode PreviewCheckin(IVaultFileOps ops, PreviewStore<FileOpPlan> store, JsonObject a)
    {
        var comment = Args.Str(a, "comment").Trim();
        var keep = Args.Bool(a, "keep_checked_out", false);
        var rows = new List<FileOpRow>();
        var files = new JsonArray();
        var blocked = 0;
        foreach (var it in Resolve(ops, a))
        {
            if (it.F is null) { files.Add(Row(it, null, new[] { it.Problem! }, Array.Empty<string>())); blocked++; continue; }
            CheckinFacts? pre = null;
            if (it.F.CheckedOut && it.F.CheckedOutByMe && it.F.LocalExists && !it.F.OpenDirty) pre = ops.CheckinPreflight(it.F);
            var (b, w) = FileOpRules.Checkin(it.F, pre);
            var row = Row(it, it.F, b, w);
            if (pre is not null) row["associations_kept"] = pre.Associations;
            files.Add(row);
            if (b.Count > 0) blocked++; else rows.Add(ToRow(it.F, null));
        }
        return Finish(store, new FileOpPlan(CheckinOp, comment, LocalCopyChoice.None, false, keep, false, rows, Array.Empty<LeavingInfo>()), files, blocked, Array.Empty<string>(), false,
                      "vault_checkin_apply");
    }

    private static JsonNode PreviewUndo(IVaultFileOps ops, PreviewStore<FileOpPlan> store, JsonObject a)
    {
        var discard = Args.Bool(a, "discard_local_changes", false);
        var rows = new List<FileOpRow>();
        var files = new JsonArray();
        var blocked = 0;
        foreach (var it in Resolve(ops, a))
        {
            if (it.F is null) { files.Add(Row(it, null, new[] { it.Problem! }, Array.Empty<string>())); blocked++; continue; }
            var (b, w) = FileOpRules.Undo(it.F, discard);
            files.Add(Row(it, it.F, b, w));
            if (b.Count > 0) blocked++; else rows.Add(ToRow(it.F, null));
        }
        return Finish(store, new FileOpPlan(Undo, "", LocalCopyChoice.None, false, false, discard, rows, Array.Empty<LeavingInfo>()), files, blocked, Array.Empty<string>(), false,
                      "vault_undo_checkout_apply");
    }

    private static JsonNode PreviewState(IVaultFileOps ops, PreviewStore<FileOpPlan> store, JsonObject a)
    {
        var target = Args.Str(a, "state").Trim();
        var includeChildren = Args.Bool(a, "include_children", false);
        var comment = Args.OptStr(a, "comment").Trim();
        if (comment.Length == 0) comment = "State change via SF Vault Bridge";
        var requested = Resolve(ops, a);

        // The order the changes will run in: with include_children each file's eligible children come first.
        var order = new List<(Item Item, bool IsChild)>();
        var skipped = new List<string>();
        var inPlan = new HashSet<long>();
        foreach (var it in requested)
        {
            if (it.F is null) { order.Add((it, false)); continue; }
            if (includeChildren) Expand(ops, it.F, target, order, inPlan, skipped, 1);
            if (inPlan.Add(it.F.MasterId)) order.Add((it, false));
        }

        var rows = new List<FileOpRow>();
        var files = new JsonArray();
        var leaving = new List<LeavingInfo>();
        var warnings = new List<string>();
        var movingReleased = new HashSet<long>();
        var blocked = 0;
        foreach (var (it, isChild) in order)
        {
            if (it.F is null) { files.Add(Row(it, null, new[] { it.Problem! }, Array.Empty<string>())); blocked++; continue; }
            var f = it.F;
            var s = ops.PlanState(f, target);
            var unreleased = new List<string>();
            if (s.ChildCheck && s.TargetReleased && s.TargetFound)
                foreach (var c in ops.Children(f))
                    if (c.State.Length > 0 && !c.StateReleased && !movingReleased.Contains(c.MasterId)) unreleased.Add($"{c.Name} ({c.State})");
            var (b, w) = FileOpRules.State(f, s, target, unreleased);
            var row = Row(it, f, b, w);
            row["target_state"] = s.Target ?? target; row["transition_allowed"] = s.TransitionExists && s.Allowed;
            if (s.RevisionAfter.Length > 0) row["revision_after"] = s.RevisionAfter;
            if (isChild) row["child_of_requested"] = true;
            var leaves = FileOpRules.LeavesReleased(f, s);
            row["leaving_released"] = leaves;
            if (leaves)
            {
                leaving.Add(new LeavingInfo(f.Name, f.Revision, f.State, s.Target ?? target));
                var line = FileOpRules.LeavingLine(f, s);
                warnings.Add(line);
                ((JsonArray)row["warnings"]!).Insert(0, line);
            }
            files.Add(row);
            if (b.Count > 0) blocked++;
            else { rows.Add(ToRow(f, s.Target ?? target)); if (s.TargetReleased) movingReleased.Add(f.MasterId); }
        }
        var o = (JsonObject)Finish(store, new FileOpPlan(State, comment, LocalCopyChoice.None, false, false, false, rows, leaving), files, blocked, warnings, true, "vault_change_state_apply");
        o["target_state"] = target;
        if (skipped.Count > 0) o["children_not_moved"] = Arr(skipped);
        return o;
    }

    private static void Expand(IVaultFileOps ops, FileFacts f, string target, List<(Item, bool)> order, HashSet<long> inPlan, List<string> skipped, int depth)
    {
        if (depth > FileOpRules.MaxDepth) { skipped.Add($"{f.Name}: children deeper than {FileOpRules.MaxDepth} levels not followed"); return; }
        foreach (var c in ops.Children(f))
        {
            if (inPlan.Contains(c.MasterId)) continue;
            if (c.VaultPath.StartsWith(DataCardFix.ContentCenterRoot, StringComparison.OrdinalIgnoreCase)
                || c.VaultPath.StartsWith(ImportRules.LibraryRoot, StringComparison.OrdinalIgnoreCase)) { skipped.Add($"{c.Name}: library file, not moved"); continue; }
            if (c.State.Length == 0) { skipped.Add($"{c.Name}: no lifecycle"); continue; }
            Expand(ops, c, target, order, inPlan, skipped, depth + 1);
            if (inPlan.Count >= FileOpRules.MaxChildren) { skipped.Add($"stopped after {FileOpRules.MaxChildren} files"); return; }
            var s = ops.PlanState(c, target);
            if (s.SameState) { skipped.Add($"{c.Name}: already in {s.Target}"); continue; }
            if (s.Problem is not null || !s.TargetFound || !s.TransitionExists || !s.Allowed)
            {
                skipped.Add($"{c.Name} ({c.State}): cannot move to {target} ({s.Problem ?? (s.TransitionExists ? "not allowed" : "no transition")})");
                continue;
            }
            inPlan.Add(c.MasterId);
            order.Add((new Item(c.Name, c, null), true));
        }
    }

    private static FileOpRow ToRow(FileFacts f, string? target) => new(f.MasterId, f.VaultPath, f.Name, f.Version, f.State, f.CheckedOut, target);

    private static JsonNode Finish(PreviewStore<FileOpPlan> store, FileOpPlan plan, JsonArray files, int blocked, IReadOnlyList<string> topWarnings, bool isState, string applyTool)
    {
        var o = new JsonObject
        {
            ["preview"] = true, ["operation"] = plan.Op, ["file_count"] = files.Count, ["blocked_count"] = blocked, ["files"] = files,
        };
        if (isState) { o["target_state"] = plan.Rows.FirstOrDefault()?.Target; o["leaving_released"] = plan.Leaving.Count > 0; }
        if (isState && plan.Leaving.Count > 0) o["leaving_released_files"] = LeavingJson(plan.Leaving);
        if (topWarnings.Count > 0) o["warnings"] = Arr(topWarnings);
        if (blocked == 0 && plan.Rows.Count > 0)
        {
            var hash = Hash(plan);
            store.PutAs(hash, plan);
            o["preview_hash"] = hash;
            o["expires_in_minutes"] = (int)store.Ttl.TotalMinutes;
            o["next"] = (plan.Leaving.Count > 0 ? "Tell the engineer prominently that these files are LEAVING RELEASED. " : "") +
                        $"Show the engineer the list. If they approve, call {applyTool} with this preview_hash and confirm true within {(int)store.Ttl.TotalMinutes} minutes.";
        }
        else o["next"] = "Blocked: no preview_hash was issued. Fix the blockers above (or drop those files) and preview again.";
        return o;
    }

    private static JsonArray LeavingJson(IEnumerable<LeavingInfo> l) =>
        new(l.Select(x => (JsonNode?)new JsonObject { ["file"] = x.File, ["revision"] = x.Revision, ["from_state"] = x.From, ["to_state"] = x.To }).ToArray());

    private static string Hash(FileOpPlan p)
    {
        var req = new JsonObject
        {
            ["comment"] = p.Comment, ["local"] = p.LocalCopy.ToString(), ["reopen"] = p.Reopen, ["keep"] = p.KeepCheckedOut, ["discard"] = p.DiscardLocal,
            ["rows"] = new JsonArray(p.Rows.Select(r => (JsonNode?)new JsonObject
            {
                ["m"] = r.MasterId, ["v"] = r.Version, ["s"] = r.State, ["c"] = r.CheckedOut, ["t"] = r.Target,
            }).ToArray()),
        };
        return PreviewGate.Hash(p.Op, req);
    }

    // ---- apply ----

    private static JsonNode ApplyPlan(IVaultFileOps ops, PreviewStore<FileOpPlan> store, JsonObject a, string op)
    {
        if (!Args.Bool(a, "confirm", false))
            throw new BridgeException("confirmation_required", $"Nothing changed. Show the engineer the {op}_preview result, and only after they say yes call this again with confirm true.");
        var plan = store.Take(Args.Str(a, "preview_hash").Trim());
        if (plan is null || plan.Op != op)
            throw new BridgeException("no_matching_preview", $"Nothing changed. The preview_hash is unknown, already used, from another tool, or older than {(int)store.Ttl.TotalMinutes} minutes. Run {op}_preview again, show it to the engineer, then apply.");

        // Drift: every file must still be the version, state and checkout the preview saw, and still pass the rules. Checked for all files before any write.
        var current = new Dictionary<long, FileFacts>();
        var drift = new List<string>();
        foreach (var r in plan.Rows)
        {
            var f = ops.Refresh(r.MasterId);
            current[r.MasterId] = f;
            if (f.Version != r.Version || f.CheckedOut != r.CheckedOut || !string.Equals(f.State, r.State, StringComparison.Ordinal))
                drift.Add($"{r.Name} changed in Vault (version {r.Version} to {f.Version}, state {r.State} to {f.State})");
            else
            {
                var blockers = op switch
                {
                    Checkout => FileOpRules.Checkout(f, plan.LocalCopy, plan.Reopen).Blockers,
                    CheckinOp => FileOpRules.Checkin(f, f.CheckedOutByMe && f.LocalExists && !f.OpenDirty ? ops.CheckinPreflight(f) : null).Blockers,
                    Undo => FileOpRules.Undo(f, plan.DiscardLocal).Blockers,
                    _ => new List<string>(),
                };
                if (blockers.Count > 0) drift.Add($"{r.Name}: {blockers[0]}");
            }
        }
        if (drift.Count > 0)
            throw new BridgeException("changed_since_preview", "Nothing changed. " + string.Join("; ", drift.Take(5)) + ". Run the preview again.");

        var results = new JsonArray();
        var failed = 0;
        var stopped = false;
        foreach (var r in plan.Rows)
        {
            var f = current[r.MasterId];
            if (stopped) { results.Add(new JsonObject { ["file"] = r.Name, ["ok"] = false, ["not_attempted"] = true, ["error"] = "not attempted: an earlier file in the order failed" }); failed++; continue; }
            OpResult res;
            try
            {
                res = op switch
                {
                    Checkout => ops.Checkout(f, plan.Comment, plan.LocalCopy == LocalCopyChoice.Replace, plan.Reopen),
                    CheckinOp => ops.Checkin(f, plan.Comment, plan.KeepCheckedOut),
                    Undo => ops.UndoCheckout(f),
                    _ => ops.ChangeState(f, r.Target!, plan.Comment),
                };
            }
            catch (BridgeException ex) when (ex.Code == "not_signed_in") { throw; }
            catch (Exception ex) { res = new OpResult(false, ex.Message); }
            var o = new JsonObject { ["file"] = r.Name, ["ok"] = res.Ok };
            if (res.Ok)
            {
                if (res.Version > 0) o["version"] = res.Version;
                if (res.State.Length > 0) o["state"] = res.State;
                if (res.Revision.Length > 0) o["revision"] = res.Revision;
                if (res.Message.Length > 0) o["message"] = res.Message;
            }
            else { o["error"] = res.Message; failed++; if (op == State) stopped = true; }
            if (res.Notes is { Count: > 0 }) o["notes"] = Arr(res.Notes);
            results.Add(o);
        }
        var result = new JsonObject { ["ok"] = failed == 0, ["operation"] = op, ["succeeded"] = plan.Rows.Count - failed, ["failed"] = failed, ["files"] = results };
        if (op == State)
        {
            result["leaving_released"] = plan.Leaving.Count > 0;
            if (plan.Leaving.Count > 0)
            {
                result["leaving_released_files"] = LeavingJson(plan.Leaving);
                result["warning"] = "LEAVING RELEASED: " + string.Join("; ", plan.Leaving.Select(l => $"{l.File}{(l.Revision.Length > 0 ? " rev " + l.Revision : "")} moved from {l.From} to {l.To}"));
            }
        }
        result["note"] = failed == 0 ? "Done." : "Some files failed; read each file's error, fix the cause and preview again. Files already done stay done.";
        return result;
    }
}
