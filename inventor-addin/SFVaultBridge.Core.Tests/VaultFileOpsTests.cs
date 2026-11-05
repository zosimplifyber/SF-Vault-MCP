using System.Text.Json.Nodes;
using SFVaultBridge.Core;

namespace SFVaultBridge.Core.Tests;

public class VaultFileOpsTests
{
    private sealed class FakeOps : IVaultFileOps
    {
        public Dictionary<string, FileFacts> Files = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<long, List<FileFacts>> Kids = new();
        public Dictionary<long, CheckinFacts> Pre = new();
        public Dictionary<string, StateFacts> States = new();   // key: name|target
        public List<string> Log = new();
        public bool FailState;
        private long _next = 1;

        public FileFacts Add(string name, string state = "Work in Progress", bool released = false, bool checkedOut = false, bool mine = true,
                             bool localDiffers = false, bool open = false, bool dirty = false, string rev = "A", string folder = "$/DESIGNS/Test")
        {
            var f = new FileFacts($"{folder}/{name}", name, _next++, 3, "Part - Engineering", "Simplifyber Lifecycle", state, released, rev, checkedOut,
                                  checkedOut ? (mine ? "you" : "Alan") : "", checkedOut && mine, @"C:\Vault\" + name, true, localDiffers, open, dirty, name.EndsWith(".ipt"));
            Files[name] = f;
            return f;
        }

        public FileLookup Lookup(string fileRef) => new(fileRef, Files.Values.Where(f => f.Name.Equals(fileRef, StringComparison.OrdinalIgnoreCase) || f.VaultPath == fileRef).ToList());
        public FileFacts Refresh(long masterId) => Files.Values.First(f => f.MasterId == masterId);
        public CheckinFacts CheckinPreflight(FileFacts f) => Pre.TryGetValue(f.MasterId, out var p) ? p : new CheckinFacts(false, null, Array.Empty<string>(), 2, Array.Empty<string>());
        public StateFacts PlanState(FileFacts f, string target) => States.TryGetValue(f.Name + "|" + target, out var s) ? s
            : new StateFacts("Simplifyber Lifecycle", f.State, f.StateReleased, target, true, target == "Released", f.State == target, true, true, false, "", Array.Empty<string>(), null);
        public IReadOnlyList<FileFacts> Children(FileFacts f) => Kids.TryGetValue(f.MasterId, out var k) ? k : new List<FileFacts>();
        public OpResult Checkout(FileFacts f, string comment, bool replaceLocal, bool reopen) { Log.Add($"checkout {f.Name} replace={replaceLocal} reopen={reopen}"); return new OpResult(true, "", f.Version); }
        public OpResult Checkin(FileFacts f, string comment, bool keep) { Log.Add($"checkin {f.Name} '{comment}' keep={keep}"); return new OpResult(true, "", f.Version + 1); }
        public OpResult UndoCheckout(FileFacts f) { Log.Add($"undo {f.Name}"); return new OpResult(true, ""); }
        public OpResult ChangeState(FileFacts f, string target, string comment)
        {
            Log.Add($"state {f.Name} -> {target}");
            return FailState ? new OpResult(false, "Vault refused") : new OpResult(true, "", 0, target, "B");
        }
    }

    private static (IReadOnlyDictionary<string, McpTool> Tools, FakeOps Ops) Rig()
    {
        var ops = new FakeOps();
        var tools = VaultFileOpsTools.Build(ops, new PreviewStore<FileOpPlan>()).ToDictionary(t => t.Name);
        return (tools, ops);
    }

    private static JsonObject Call(IReadOnlyDictionary<string, McpTool> t, string name, JsonObject a) => (JsonObject)t[name].Handler(a)!;
    private static JsonObject Files(params string[] f) => new() { ["files"] = new JsonArray(f.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()) };
    private static JsonObject Apply(string hash) => new() { ["preview_hash"] = hash, ["confirm"] = true };
    private static JsonObject With(JsonObject o, string k, JsonNode? v) { o[k] = v; return o; }

    [Fact]
    public void Eight_tools_registered_and_none_without_a_host()
    {
        var (tools, _) = Rig();
        Assert.Equal(new[] { "vault_change_state_apply", "vault_change_state_preview", "vault_checkin_apply", "vault_checkin_preview", "vault_checkout_apply", "vault_checkout_preview", "vault_undo_checkout_apply", "vault_undo_checkout_preview" },
                     tools.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Empty(VaultFileOpsTools.Build(null, new PreviewStore<FileOpPlan>()));
        Assert.All(tools.Values.Where(t => t.Name.EndsWith("_apply")), t => Assert.Equal(McpToolKind.Write, t.Kind));
        Assert.All(tools.Values.Where(t => t.Name.EndsWith("_preview")), t => Assert.Equal(McpToolKind.Preview, t.Kind));
    }

    // ---- check out ----

    [Fact]
    public void Checkout_preview_then_apply_checks_out_with_default_comment()
    {
        var (t, ops) = Rig();
        ops.Add("CD-1.ipt");
        var p = Call(t, "vault_checkout_preview", Files("CD-1.ipt"));
        Assert.Equal(0, p["blocked_count"]!.GetValue<int>());
        var r = Call(t, "vault_checkout_apply", Apply((string)p["preview_hash"]!));
        Assert.True(r["ok"]!.GetValue<bool>());
        Assert.Equal("checkout CD-1.ipt replace=False reopen=True", ops.Log.Single());
    }

    [Fact]
    public void Checkout_refuses_a_file_someone_else_holds_and_issues_no_hash()
    {
        var (t, ops) = Rig();
        ops.Add("CD-1.ipt", checkedOut: true, mine: false);
        var p = Call(t, "vault_checkout_preview", Files("CD-1.ipt"));
        Assert.Null(p["preview_hash"]);
        Assert.Contains("Alan", (string)p["files"]![0]!["blockers"]![0]!);
    }

    [Fact]
    public void Checkout_differing_local_copy_needs_a_choice_and_is_never_replaced_silently()
    {
        var (t, ops) = Rig();
        ops.Add("CD-1.ipt", localDiffers: true);
        var p = Call(t, "vault_checkout_preview", Files("CD-1.ipt"));
        Assert.Null(p["preview_hash"]);
        Assert.Contains("local_copy", (string)p["files"]![0]!["blockers"]![0]!);
        var keep = Call(t, "vault_checkout_preview", With(Files("CD-1.ipt"), "local_copy", "keep"));
        Assert.NotNull(keep["preview_hash"]);
        Call(t, "vault_checkout_apply", Apply((string)keep["preview_hash"]!));
        Assert.Contains("replace=False", ops.Log.Single());
        var rep = Call(t, "vault_checkout_preview", With(Files("CD-1.ipt"), "local_copy", "replace"));
        Assert.Contains("REPLACED", (string)rep["files"]![0]!["warnings"]![0]!);
    }

    [Fact]
    public void Checkout_replace_is_refused_when_open_in_inventor()
    {
        var (t, ops) = Rig();
        ops.Add("CD-1.ipt", localDiffers: true, open: true);
        var p = Call(t, "vault_checkout_preview", With(Files("CD-1.ipt"), "local_copy", "replace"));
        Assert.Null(p["preview_hash"]);
        Assert.Contains("open in Inventor", (string)p["files"]![0]!["blockers"]![0]!);
    }

    [Fact]
    public void Missing_names_are_blockers_and_a_duplicate_is_listed_once()
    {
        var (t, ops) = Rig();
        ops.Add("CD-1.ipt");
        var p = Call(t, "vault_checkout_preview", Files("CD-1.ipt", "cd-1.ipt", "CD-9.ipt"));
        Assert.Equal(2, p["file_count"]!.GetValue<int>());
        Assert.Equal(1, p["blocked_count"]!.GetValue<int>());
        Assert.Equal("not found in Vault", (string)p["files"]![1]!["blockers"]![0]!);
    }

    [Fact]
    public void Released_file_gets_a_checkout_warning()
    {
        var (t, ops) = Rig();
        ops.Add("CD-1.ipt", state: "Released", released: true);
        var p = Call(t, "vault_checkout_preview", Files("CD-1.ipt"));
        Assert.Contains("released state", (string)p["files"]![0]!["warnings"]![0]!);
    }

    // ---- check in ----

    [Fact]
    public void Checkin_needs_a_comment_and_carries_it_to_apply()
    {
        var (t, ops) = Rig();
        ops.Add("CD-1.ipt", checkedOut: true, localDiffers: true);
        Assert.Throws<BridgeException>(() => t["vault_checkin_preview"].Handler(Files("CD-1.ipt")));
        var p = Call(t, "vault_checkin_preview", With(Files("CD-1.ipt"), "comment", "Added fillet"));
        Assert.Equal(2, p["files"]![0]!["associations_kept"]!.GetValue<int>());
        Assert.True(Call(t, "vault_checkin_apply", Apply((string)p["preview_hash"]!))["ok"]!.GetValue<bool>());
        Assert.Equal("checkin CD-1.ipt 'Added fillet' keep=False", ops.Log.Single());
    }

    [Fact]
    public void Checkin_blockers()
    {
        var (t, ops) = Rig();
        ops.Add("a.ipt");                                                   // not checked out
        ops.Add("b.ipt", checkedOut: true, mine: false);                    // someone else's
        ops.Add("c.ipt", checkedOut: true, open: true, dirty: true);        // unsaved in Inventor
        var d = ops.Add("d.ipt", checkedOut: true);                         // identical to checked-out version
        ops.Pre[d.MasterId] = new CheckinFacts(true, null, Array.Empty<string>(), 0, Array.Empty<string>());
        var e = ops.Add("e.idw", checkedOut: true, localDiffers: true);     // new reference
        ops.Pre[e.MasterId] = new CheckinFacts(false, null, new[] { "CD-77.ipt" }, 1, Array.Empty<string>());
        var f = ops.Add("f.iam", checkedOut: true, localDiffers: true);     // child matches no version
        ops.Pre[f.MasterId] = new CheckinFacts(false, "a child cannot be matched to a Vault version", Array.Empty<string>(), 0, Array.Empty<string>());
        var p = Call(t, "vault_checkin_preview", With(Files("a.ipt", "b.ipt", "c.ipt", "d.ipt", "e.idw", "f.iam"), "comment", "x"));
        Assert.Null(p["preview_hash"]);
        string B(int i) => (string)p["files"]![i]!["blockers"]![0]!;
        Assert.Contains("not checked out", B(0));
        Assert.Contains("Alan", B(1));
        Assert.Contains("save it in Inventor first", B(2));
        Assert.Contains("identical", B(3));
        Assert.Contains("CD-77.ipt", B(4));
        Assert.Contains("Check In inside Inventor", B(4));
        Assert.Contains("cannot be matched", B(5));
    }

    [Fact]
    public void Checkin_keep_checked_out_flows_through()
    {
        var (t, ops) = Rig();
        ops.Add("CD-1.ipt", checkedOut: true, localDiffers: true);
        var p = Call(t, "vault_checkin_preview", With(With(Files("CD-1.ipt"), "comment", "wip"), "keep_checked_out", true));
        Call(t, "vault_checkin_apply", Apply((string)p["preview_hash"]!));
        Assert.Contains("keep=True", ops.Log.Single());
    }

    // ---- undo ----

    [Fact]
    public void Undo_refuses_to_lose_local_edits_without_the_flag()
    {
        var (t, ops) = Rig();
        ops.Add("CD-1.ipt", checkedOut: true, localDiffers: true);
        var p = Call(t, "vault_undo_checkout_preview", Files("CD-1.ipt"));
        Assert.Null(p["preview_hash"]);
        Assert.Contains("discard_local_changes", (string)p["files"]![0]!["blockers"]![0]!);
        var ok = Call(t, "vault_undo_checkout_preview", With(Files("CD-1.ipt"), "discard_local_changes", true));
        Assert.True(Call(t, "vault_undo_checkout_apply", Apply((string)ok["preview_hash"]!))["ok"]!.GetValue<bool>());
        Assert.Equal("undo CD-1.ipt", ops.Log.Single());
    }

    [Fact]
    public void Undo_refuses_someone_elses_checkout_and_a_file_not_checked_out()
    {
        var (t, ops) = Rig();
        ops.Add("a.ipt", checkedOut: true, mine: false);
        ops.Add("b.ipt");
        var p = Call(t, "vault_undo_checkout_preview", Files("a.ipt", "b.ipt"));
        Assert.Equal(2, p["blocked_count"]!.GetValue<int>());
    }

    // ---- state change ----

    private static JsonObject State(string target, params string[] files) => With(Files(files), "state", target);

    [Fact]
    public void Moving_out_of_released_sets_leaving_released_and_names_the_file_and_revision()
    {
        var (t, ops) = Rig();
        ops.Add("CD-1.ipt", state: "Released", released: true, rev: "C");
        var p = Call(t, "vault_change_state_preview", State("Work in Progress", "CD-1.ipt"));
        Assert.True(p["leaving_released"]!.GetValue<bool>());
        Assert.Equal("CD-1.ipt", (string)p["leaving_released_files"]![0]!["file"]!);
        Assert.Equal("C", (string)p["leaving_released_files"]![0]!["revision"]!);
        var line = (string)p["warnings"]![0]!;
        Assert.Contains("LEAVING RELEASED", line);
        Assert.Contains("CD-1.ipt rev C", line);
        Assert.True(p["files"]![0]!["leaving_released"]!.GetValue<bool>());
        var r = Call(t, "vault_change_state_apply", Apply((string)p["preview_hash"]!));
        Assert.True(r["leaving_released"]!.GetValue<bool>());
        Assert.Equal("CD-1.ipt", (string)r["leaving_released_files"]![0]!["file"]!);
        Assert.Contains("LEAVING RELEASED", (string)r["warning"]!);
    }

    [Fact]
    public void Library_style_released_flag_counts_whatever_the_state_is_called()
    {
        var (t, ops) = Rig();
        ops.Add("P.ipt", state: "Library", released: true);
        var p = Call(t, "vault_change_state_preview", State("Library Edit", "P.ipt"));
        Assert.True(p["leaving_released"]!.GetValue<bool>());
    }

    [Fact]
    public void Moving_into_released_or_between_unreleased_states_is_not_flagged()
    {
        var (t, ops) = Rig();
        ops.Add("a.ipt");
        var into = Call(t, "vault_change_state_preview", State("Released", "a.ipt"));
        Assert.False(into["leaving_released"]!.GetValue<bool>());
        Assert.Null(into["leaving_released_files"]);
        Assert.Null(into["warnings"]);
        var between = Call(t, "vault_change_state_preview", State("For Review", "a.ipt"));
        Assert.False(between["leaving_released"]!.GetValue<bool>());
        var r = Call(t, "vault_change_state_apply", Apply((string)between["preview_hash"]!));
        Assert.False(r["leaving_released"]!.GetValue<bool>());
    }

    [Fact]
    public void Released_to_another_released_state_is_not_leaving()
    {
        var (t, ops) = Rig();
        ops.Add("a.ipt", state: "Released", released: true);
        ops.States["a.ipt|Library"] = new StateFacts("L", "Released", true, "Library", true, true, false, true, true, false, "", Array.Empty<string>(), null);
        Assert.False(Call(t, "vault_change_state_preview", State("Library", "a.ipt"))["leaving_released"]!.GetValue<bool>());
    }

    [Fact]
    public void State_blockers_checked_out_no_transition_unknown_state_not_allowed()
    {
        var (t, ops) = Rig();
        ops.Add("a.ipt", checkedOut: true);
        ops.Add("b.ipt");
        ops.Add("c.ipt");
        ops.Add("d.ipt");
        ops.States["b.ipt|Released"] = new StateFacts("L", "Work in Progress", false, "Released", true, true, false, false, false, false, "", new[] { "For Review" }, null);
        ops.States["c.ipt|Nope"] = new StateFacts("L", "Work in Progress", false, null, false, false, false, false, false, false, "", new[] { "Released", "For Review" }, null);
        ops.States["d.ipt|Released"] = new StateFacts("L", "Work in Progress", false, "Released", true, true, false, true, false, false, "", Array.Empty<string>(), null);
        string B(string target, string file) => (string)Call(t, "vault_change_state_preview", State(target, file))["files"]![0]!["blockers"]![0]!;
        Assert.Contains("checked out", B("Released", "a.ipt"));
        Assert.Contains("no transition from Work in Progress to Released", B("Released", "b.ipt"));
        Assert.Contains("For Review", B("Released", "b.ipt"));
        Assert.Contains("no state 'Nope'", B("Nope", "c.ipt"));
        Assert.Contains("not allowed", B("Released", "d.ipt"));
    }

    [Fact]
    public void Unreleased_children_block_a_release_that_checks_children()
    {
        var (t, ops) = Rig();
        var asm = ops.Add("A.iam");
        var kid = ops.Add("K.ipt");
        ops.Kids[asm.MasterId] = new List<FileFacts> { kid };
        ops.States["A.iam|Released"] = new StateFacts("L", "Work in Progress", false, "Released", true, true, false, true, true, true, "B", Array.Empty<string>(), null);
        var p = Call(t, "vault_change_state_preview", State("Released", "A.iam"));
        Assert.Null(p["preview_hash"]);
        Assert.Contains("K.ipt", (string)p["files"]![0]!["blockers"]![0]!);
    }

    [Fact]
    public void Include_children_moves_children_first_and_satisfies_the_child_check()
    {
        var (t, ops) = Rig();
        var asm = ops.Add("A.iam");
        var kid = ops.Add("K.ipt");
        var done = ops.Add("D.ipt", state: "Released", released: true);
        var lib = ops.Add("L.ipt", state: "Library", released: true, folder: "$/DESIGNS/Simplifyber Library/McMaster-Carr");
        ops.Kids[asm.MasterId] = new List<FileFacts> { kid, done, lib };
        ops.States["A.iam|Released"] = new StateFacts("L", "Work in Progress", false, "Released", true, true, false, true, true, true, "B", Array.Empty<string>(), null);
        var p = Call(t, "vault_change_state_preview", With(State("Released", "A.iam"), "include_children", true));
        Assert.NotNull(p["preview_hash"]);
        Assert.Equal(new[] { "K.ipt", "A.iam" }, ((JsonArray)p["files"]!).Select(f => (string)f!["file"]!).ToArray());
        var skipped = string.Join("|", ((JsonArray)p["children_not_moved"]!).Select(x => (string)x!));
        Assert.Contains("D.ipt: already in Released", skipped);
        Assert.Contains("L.ipt: library file", skipped);
        Call(t, "vault_change_state_apply", Apply((string)p["preview_hash"]!));
        Assert.Equal(new[] { "state K.ipt -> Released", "state A.iam -> Released" }, ops.Log);
    }

    [Fact]
    public void Without_include_children_only_the_named_files_move()
    {
        var (t, ops) = Rig();
        var asm = ops.Add("A.iam");
        ops.Kids[asm.MasterId] = new List<FileFacts> { ops.Add("K.ipt") };
        var p = Call(t, "vault_change_state_preview", State("For Review", "A.iam"));
        Assert.Single((JsonArray)p["files"]!);
    }

    [Fact]
    public void State_apply_stops_at_the_first_failure()
    {
        var (t, ops) = Rig();
        ops.Add("a.ipt"); ops.Add("b.ipt");
        var p = Call(t, "vault_change_state_preview", State("For Review", "a.ipt", "b.ipt"));
        ops.FailState = true;
        var r = Call(t, "vault_change_state_apply", Apply((string)p["preview_hash"]!));
        Assert.False(r["ok"]!.GetValue<bool>());
        Assert.Equal(2, r["failed"]!.GetValue<int>());
        Assert.Equal("Vault refused", (string)r["files"]![0]!["error"]!);
        Assert.True(r["files"]![1]!["not_attempted"]!.GetValue<bool>());
        Assert.Single(ops.Log);
    }

    // ---- apply gate ----

    [Fact]
    public void Apply_needs_confirm_a_known_hash_and_the_matching_tool()
    {
        var (t, ops) = Rig();
        ops.Add("a.ipt");
        var p = Call(t, "vault_checkout_preview", Files("a.ipt"));
        var hash = (string)p["preview_hash"]!;
        Assert.Equal("confirmation_required", Assert.Throws<BridgeException>(() => t["vault_checkout_apply"].Handler(new JsonObject { ["preview_hash"] = hash })).Code);
        Assert.Equal("no_matching_preview", Assert.Throws<BridgeException>(() => t["vault_checkin_apply"].Handler(Apply(hash))).Code);
        Assert.Equal("no_matching_preview", Assert.Throws<BridgeException>(() => t["vault_checkout_apply"].Handler(Apply("nope"))).Code);
        Assert.Empty(ops.Log);
    }

    [Fact]
    public void Apply_refuses_when_the_file_changed_since_preview_and_writes_nothing()
    {
        var (t, ops) = Rig();
        ops.Add("a.ipt"); ops.Add("b.ipt");
        var p = Call(t, "vault_checkout_preview", Files("a.ipt", "b.ipt"));
        ops.Files["b.ipt"] = ops.Files["b.ipt"] with { Version = 4 };
        var ex = Assert.Throws<BridgeException>(() => t["vault_checkout_apply"].Handler(Apply((string)p["preview_hash"]!)));
        Assert.Equal("changed_since_preview", ex.Code);
        Assert.Empty(ops.Log);
    }

    [Fact]
    public void A_preview_hash_works_once()
    {
        var (t, ops) = Rig();
        ops.Add("a.ipt");
        var hash = (string)Call(t, "vault_checkout_preview", Files("a.ipt"))["preview_hash"]!;
        Call(t, "vault_checkout_apply", Apply(hash));
        Assert.Throws<BridgeException>(() => t["vault_checkout_apply"].Handler(Apply(hash)));
    }

    [Fact]
    public void Checkin_apply_refuses_when_the_document_became_dirty_after_preview()
    {
        var (t, ops) = Rig();
        ops.Add("a.ipt", checkedOut: true, localDiffers: true);
        var hash = (string)Call(t, "vault_checkin_preview", With(Files("a.ipt"), "comment", "c"))["preview_hash"]!;
        ops.Files["a.ipt"] = ops.Files["a.ipt"] with { OpenInInventor = true, OpenDirty = true };
        var ex = Assert.Throws<BridgeException>(() => t["vault_checkin_apply"].Handler(Apply(hash)));
        Assert.Contains("unsaved", ex.Message);
        Assert.Empty(ops.Log);
    }

    [Fact]
    public void Bad_local_copy_value_is_rejected()
    {
        var (t, ops) = Rig();
        ops.Add("a.ipt");
        Assert.Throws<BridgeException>(() => t["vault_checkout_preview"].Handler(With(Files("a.ipt"), "local_copy", "maybe")));
    }
}
