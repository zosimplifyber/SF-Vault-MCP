using System.Text.Json.Nodes;
using SFVaultBridge.Core;

namespace SFVaultBridge.Core.Tests;

public partial class DataCardFixTests
{
    private static DataCardFileInfo Info(string name = "CD-001885.ipt", string path = "$/DESIGNS/Simplifyber Library/McMaster-Carr/washers/CD-001885.ipt",
        string category = "Part - Purchased", string state = "Work in Progress", bool released = false, bool co = false, string by = "", long ver = 1)
        => new(path, name, category, state, released, co, by, ver);

    private static readonly PropRef Pn = new(PropSet.Design, "Part Number");

    private sealed class FakeHost : IDataCardHost
    {
        public Dictionary<string, List<DataCardFileInfo>> Files = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<PropRef, string?> Current = new() { [new PropRef(PropSet.Design, "Part Number")] = "93501A035_Steel Washers" };
        public List<string> Calls = new();
        public bool FailApply;
        public bool SkipRead;
        public Exception? ReadThrows;
        public Exception? FindThrows;
        public string? Warning;
        public Dictionary<string, string> StateByPath = new();      // simulated live state, set by ChangeState
        public RevisePlan Plan = new(null, "Released -> Work in Progress", "Work in Progress -> Released");
        public string? FailChangeTo;                                  // ChangeState to this state throws
        public bool? Linked;
        public bool ApplySkips;

        public IReadOnlyList<DataCardFileInfo> Find(DataCardFileRef file)
        {
            if (FindThrows is not null) throw FindThrows;
            var l = Files.TryGetValue(file.Text, out var x) ? x
                  : Files.Values.SelectMany(v => v).Where(i => string.Equals(i.VaultPath, file.Text, StringComparison.OrdinalIgnoreCase)).ToList();
            return l.Select(i => StateByPath.TryGetValue(i.VaultPath, out var st)
                ? i with { State = st, Released = st == "Released", Revision = st == "Released" ? "B" : "A" } : i).ToList();
        }

        public RevisePlan PlanRevise(DataCardFileInfo f) { Calls.Add("plan " + f.Name); return Plan; }

        public DataCardStateChange ChangeState(DataCardFileInfo f, string toStateName, string comment)
        {
            Calls.Add($"state {f.Name} -> {toStateName}");
            if (FailChangeTo == toStateName) throw new DataCardStepException("revise", "Vault refused");
            StateByPath[f.VaultPath] = toStateName;
            return new DataCardStateChange(toStateName, toStateName == "Released" ? "B" : "A");
        }

        public bool? LinkedToItem(DataCardFileInfo f) => Linked;

        public IReadOnlyDictionary<PropRef, string?> Read(DataCardFileInfo f, IReadOnlyList<PropRef> props)
        {
            Calls.Add("read " + f.Name);
            if (ReadThrows is not null) throw ReadThrows;
            if (SkipRead) throw new DataCardSkipException("open and dirty");
            return Current;
        }

        public DataCardApplied Apply(DataCardFileInfo f, IReadOnlyList<PropChange> changes, string comment)
        {
            if (ApplySkips) throw new DataCardSkipException("open and dirty");
            Calls.Add($"apply {f.Name} [{string.Join(",", changes.Select(c => c.Prop.Name))}] {comment}");
            if (FailApply) throw new DataCardStepException("checkin", "boom (the checkout was undone)");
            return new DataCardApplied(Warning is null ? f.Version + 1 : -1, Warning);
        }
    }

    private static DataCardRequest Req(string[] files, bool dry = true, string? comment = null, bool pn = true,
        Dictionary<string, string>? design = null, Dictionary<string, string>? custom = null, bool revise = false)
        => new(files, design ?? new(), custom ?? new(), pn, dry, comment, revise);

    [Fact]
    public void ParseFile_tells_bare_names_from_vault_paths()
    {
        Assert.False(DataCardFix.ParseFile(" CD-001885.ipt ").IsPath);
        Assert.Equal("CD-001885.ipt", DataCardFix.ParseFile(" CD-001885.ipt ").Text);
        Assert.True(DataCardFix.ParseFile("$/DESIGNS/x/CD-001897.ipt").IsPath);
    }

    [Fact]
    public void SkipReason_rules()
    {
        Assert.Null(DataCardFix.SkipReason(Info()));
        Assert.Contains("not an .ipt", DataCardFix.SkipReason(Info("a.idw", "$/a.idw"))!);
        Assert.Equal("assemblies are not supported yet", DataCardFix.SkipReason(Info("A.IAM", "$/A.IAM")));
        Assert.Equal("could not read lifecycle state", DataCardFix.SkipReason(Info() with { Unreadable = "could not read lifecycle state" }));
        Assert.Contains("checked out by Zak", DataCardFix.SkipReason(Info(co: true, by: "Zak"))!);
        Assert.Contains("Released", DataCardFix.SkipReason(Info(state: "released"))!);
        Assert.Contains("Released", DataCardFix.SkipReason(Info(state: "Anything", released: true))!);
        Assert.Contains("Content Center", DataCardFix.SkipReason(Info(category: "Part - Content Center"))!);
        Assert.Contains("Content Center", DataCardFix.SkipReason(Info(path: "$/content center files/x/CD-1.ipt"))!);
    }

    [Fact]
    public void Changes_set_part_number_from_file_name_and_omit_equal_values()
    {
        var r = Req(new[] { "x" }, design: new() { ["Vendor"] = "McMASTER-CARR" }, custom: new() { ["Source"] = "Buy" });
        DataCardFix.Targets(r, "CD-001885.ipt", out var wanted);
        Assert.Equal("CD-001885", wanted[Pn]);
        var current = new Dictionary<PropRef, string?>
        {
            [Pn] = "93501A035_Steel", [new PropRef(PropSet.Design, "Vendor")] = "McMASTER-CARR",
        };
        var changes = DataCardFix.ComputeChanges(wanted, current);
        Assert.Equal(new[] { "Part Number", "Source" }, changes.Select(c => c.Prop.Name).OrderBy(n => n).ToArray());
        Assert.Null(changes.Single(c => c.Prop.Name == "Source").From);
        Assert.Empty(DataCardFix.ComputeChanges(wanted, new Dictionary<PropRef, string?>
        {
            [Pn] = "CD-001885", [new PropRef(PropSet.Design, "Vendor")] = "McMASTER-CARR", [new PropRef(PropSet.Custom, "Source")] = "Buy",
        }));
    }

    [Fact]
    public void Part_number_can_be_turned_off_and_an_explicit_value_wins()
    {
        DataCardFix.Targets(Req(new[] { "x" }, pn: false), "CD-1.ipt", out var none);
        Assert.Empty(none);
        DataCardFix.Targets(Req(new[] { "x" }, design: new() { ["Part Number"] = "ZZ" }), "CD-1.ipt", out var explicitPn);
        Assert.Equal("ZZ", explicitPn[Pn]);
    }

    [Fact]
    public void Default_comment_names_changed_properties()
    {
        var c = new[] { new PropChange(Pn, "a", "b"), new PropChange(new PropRef(PropSet.Custom, "Source"), null, "Buy") };
        Assert.Equal("Data card fix: Part Number, Source", DataCardFix.DefaultComment(c));
    }

    [Fact]
    public void Dry_run_previews_and_writes_nothing()
    {
        var h = new FakeHost();
        h.Files["CD-001885.ipt"] = new() { Info() };
        var log = new List<string>();
        var o = DataCardFix.Run(Req(new[] { "CD-001885.ipt" }), h, log.Add);
        var r = o["results"]![0]!;
        Assert.Equal("preview", r["status"]!.GetValue<string>());
        Assert.Equal("CD-001885", r["changes"]!["Part Number"]!["to"]!.GetValue<string>());
        Assert.Equal("93501A035_Steel Washers", r["changes"]!["Part Number"]!["from"]!.GetValue<string>());
        Assert.Equal(1, o["counts"]!["preview"]!.GetValue<int>());
        Assert.DoesNotContain(h.Calls, c => c.StartsWith("apply"));
        Assert.Single(log);
        Assert.Contains("preview", log[0]);
    }

    [Fact]
    public void Real_run_applies_with_default_comment_and_reports_version()
    {
        var h = new FakeHost();
        h.Files["CD-001885.ipt"] = new() { Info(ver: 1) };
        var o = DataCardFix.Run(Req(new[] { "CD-001885.ipt" }, dry: false), h);
        var r = o["results"]![0]!;
        Assert.Equal("fixed", r["status"]!.GetValue<string>());
        Assert.Equal(2, r["version"]!.GetValue<long>());
        Assert.Contains("apply CD-001885.ipt [Part Number] Data card fix: Part Number", h.Calls);
    }

    [Fact]
    public void Explicit_comment_is_used()
    {
        var h = new FakeHost();
        h.Files["CD-001885.ipt"] = new() { Info() };
        DataCardFix.Run(Req(new[] { "CD-001885.ipt" }, dry: false, comment: "Zak fix"), h);
        Assert.Contains("apply CD-001885.ipt [Part Number] Zak fix", h.Calls);
    }

    [Fact]
    public void Files_are_independent_and_counted()
    {
        var h = new FakeHost();
        h.Files["a.ipt"] = new() { Info("a.ipt", "$/a.ipt") };
        h.Files["rel.ipt"] = new() { Info("rel.ipt", "$/rel.ipt", state: "Released") };
        h.Files["dup.ipt"] = new() { Info("dup.ipt", "$/x/dup.ipt"), Info("dup.ipt", "$/y/dup.ipt") };
        h.Current[Pn] = "x";
        var o = DataCardFix.Run(Req(new[] { "missing.ipt", "rel.ipt", "dup.ipt", "a.ipt" }, dry: false), h);
        var st = o["results"]!.AsArray().Select(n => n!["status"]!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "skipped", "skipped", "error", "fixed" }, st);
        Assert.Equal(2, o["counts"]!["skipped"]!.GetValue<int>());
        Assert.Equal(1, o["counts"]!["error"]!.GetValue<int>());
        Assert.Equal(1, o["counts"]!["fixed"]!.GetValue<int>());
        Assert.Equal(0, o["counts"]!["no_change"]!.GetValue<int>());
        Assert.Equal("resolve", o["results"]![2]!["step"]!.GetValue<string>());
    }

    [Fact]
    public void No_change_when_already_at_target()
    {
        var h = new FakeHost();
        h.Files["CD-001885.ipt"] = new() { Info() };
        h.Current[Pn] = "CD-001885";
        var o = DataCardFix.Run(Req(new[] { "CD-001885.ipt" }, dry: false), h);
        Assert.Equal("no_change", o["results"]![0]!["status"]!.GetValue<string>());
        Assert.DoesNotContain(h.Calls, c => c.StartsWith("apply"));
    }

    [Fact]
    public void Apply_failure_is_an_error_with_the_step()
    {
        var h = new FakeHost { FailApply = true };
        h.Files["CD-001885.ipt"] = new() { Info() };
        var o = DataCardFix.Run(Req(new[] { "CD-001885.ipt" }, dry: false), h);
        var r = o["results"]![0]!;
        Assert.Equal("error", r["status"]!.GetValue<string>());
        Assert.Equal("checkin", r["step"]!.GetValue<string>());
    }

    [Fact]
    public void Host_skip_becomes_skipped()
    {
        var h = new FakeHost { SkipRead = true };
        h.Files["CD-001885.ipt"] = new() { Info() };
        var r = DataCardFix.Run(Req(new[] { "CD-001885.ipt" }), h)["results"]![0]!;
        Assert.Equal("skipped", r["status"]!.GetValue<string>());
        Assert.Equal("open and dirty", r["reason"]!.GetValue<string>());
    }

    [Fact]
    public void ParseRequest_defaults_to_dry_run_and_part_number()
    {
        var r = DataCardFix.ParseRequest(new JsonObject { ["files"] = new JsonArray("a.ipt") });
        Assert.True(r.DryRun);
        Assert.True(r.PartNumberFromFileName);
        Assert.Null(r.Comment);
        var r2 = DataCardFix.ParseRequest(JsonNode.Parse("""{"files":["a.ipt"],"dryRun":false,"partNumberFromFileName":false,"comment":"c","design":{"Vendor":"V"},"custom":{"Source":"Buy"}}""")!.AsObject());
        Assert.False(r2.DryRun);
        Assert.False(r2.PartNumberFromFileName);
        Assert.Equal("c", r2.Comment);
        Assert.Equal("V", r2.Design["Vendor"]);
        Assert.Equal("Buy", r2.Custom["Source"]);
    }

    [Fact]
    public void Sign_in_loss_keeps_results_so_far_and_errors_the_rest()
    {
        var h = new SignInHost();
        h.Files["a.ipt"] = new() { Info("a.ipt", "$/a.ipt") };
        var log = new List<string>();
        var o = DataCardFix.Run(Req(new[] { "a.ipt", "b.ipt", "c.ipt" }, dry: false), h, log.Add);
        var st = o["results"]!.AsArray().Select(n => n!["status"]!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "fixed", "error", "error" }, st);
        Assert.Equal("sign_in", o["results"]![1]!["step"]!.GetValue<string>());
        Assert.Equal("c.ipt", o["results"]![2]!["file"]!.GetValue<string>());
        Assert.Equal(2, o["counts"]!["error"]!.GetValue<int>());
        Assert.Equal(3, log.Count);
    }

    private sealed class SignInHost : IDataCardHost
    {
        public Dictionary<string, List<DataCardFileInfo>> Files = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<DataCardFileInfo> Find(DataCardFileRef file) =>
            Files.TryGetValue(file.Text, out var l) ? l : throw new BridgeException("not_signed_in", "Sign in.");
        public IReadOnlyDictionary<PropRef, string?> Read(DataCardFileInfo f, IReadOnlyList<PropRef> props) => new Dictionary<PropRef, string?>();
        public DataCardApplied Apply(DataCardFileInfo f, IReadOnlyList<PropChange> changes, string comment) => new(2);
        public RevisePlan PlanRevise(DataCardFileInfo f) => new("none");
        public DataCardStateChange ChangeState(DataCardFileInfo f, string toStateName, string comment) => new(toStateName, "");
        public bool? LinkedToItem(DataCardFileInfo f) => null;
    }

    [Fact]
    public void Arbitrary_read_failure_is_an_error_at_step_read()
    {
        var h = new FakeHost { ReadThrows = new InvalidOperationException("kaput") };
        h.Files["CD-001885.ipt"] = new() { Info() };
        var r = DataCardFix.Run(Req(new[] { "CD-001885.ipt" }), h)["results"]![0]!;
        Assert.Equal("error", r["status"]!.GetValue<string>());
        Assert.Equal("read", r["step"]!.GetValue<string>());
    }

    [Fact]
    public void Read_back_failure_after_checkin_is_fixed_with_a_warning()
    {
        var h = new FakeHost { Warning = "could not read version" };
        h.Files["CD-001885.ipt"] = new() { Info() };
        var r = DataCardFix.Run(Req(new[] { "CD-001885.ipt" }, dry: false), h)["results"]![0]!;
        Assert.Equal("fixed", r["status"]!.GetValue<string>());
        Assert.Null(r["version"]);
        Assert.Equal("could not read version", r["warning"]!.GetValue<string>());
    }

    [Fact]
    public void Duplicate_files_are_processed_once_and_assemblies_skipped_end_to_end()
    {
        var h = new FakeHost();
        h.Files["CD-001885.ipt"] = new() { Info() };
        h.Files["a.iam"] = new() { Info("a.iam", "$/a.iam") };
        var o = DataCardFix.Run(Req(new[] { "CD-001885.ipt", "cd-001885.IPT", "a.iam" }), h);
        Assert.Equal(2, o["results"]!.AsArray().Count);
        Assert.Equal("assemblies are not supported yet", o["results"]![1]!["reason"]!.GetValue<string>());
    }

    [Fact]
    public void Whitespace_only_differences_are_changes()
    {
        var wanted = new Dictionary<PropRef, string> { [Pn] = "CD-1" };
        Assert.Single(DataCardFix.ComputeChanges(wanted, new Dictionary<PropRef, string?> { [Pn] = "CD-1 " }));
    }

    [Fact]
    public void Bool_arguments_must_be_booleans()
    {
        var ex = Assert.Throws<BridgeException>(() => DataCardFix.ParseRequest(JsonNode.Parse("""{"files":["a.ipt"],"dryRun":"no"}""")!.AsObject()));
        Assert.Equal("bad_request", ex.Code);
    }

    [Fact]
    public void Child_associations_keep_dependencies_and_attachments_of_the_parent_only()
    {
        var rows = new[]
        {
            new AssocRow(10, 20, "Dependency", "ref1", "src", "$/a/child.ipt"),
            new AssocRow(10, 21, "Attachment", null, null, null),
            new AssocRow(10, 20, "Dependency", "ref1", "src", "$/a/child.ipt"),
            new AssocRow(99, 10, "Dependency", "r", null, null),
            new AssocRow(10, 22, "Other", null, null, null),
        };
        var kept = DataCardFix.ChildAssociations(rows, 10);
        Assert.Equal(2, kept.Count);
        Assert.Equal("ref1", kept.Single(k => k.Type == "Dependency").RefId);
        Assert.Contains(kept, k => k.CldFileId == 21 && k.Type == "Attachment");
        Assert.Empty(DataCardFix.ChildAssociations(Array.Empty<AssocRow>(), 10));
    }
}
