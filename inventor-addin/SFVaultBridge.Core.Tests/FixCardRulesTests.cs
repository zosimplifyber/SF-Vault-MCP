using System.Text.Json.Nodes;
using SFVaultBridge.Core;

namespace SFVaultBridge.Core.Tests;

// Fix Data Card ribbon button: which parts, proposed values, changed-fields-only requests, summary properties, results.
public partial class DataCardFixTests
{
    private static Dictionary<PropRef, string?> Card(string pn = "93501A035_Steel Washers", string desc = "Steel Washers", string vendor = "McMaster-Carr",
        string source = "Buy", string title = "old title", string cost = "12.5", string process = "", string finish = "As Finished", string vnum = "93501A035")
        => new()
        {
            [FixCardRules.PartNumberProp] = pn, [FixCardRules.DescriptionProp] = desc, [FixCardRules.VendorProp] = vendor,
            [FixCardRules.SourceProp] = source, [FixCardRules.TitleProp] = title, [FixCardRules.CostProp] = cost,
            [FixCardRules.ProcessProp] = process, [FixCardRules.FinishProp] = finish, [FixCardRules.VendorNumberProp] = vnum,
        };

    private static Dictionary<PropRef, string> Edited(string name, string? category, Dictionary<PropRef, string?> current) =>
        new(FixCardRules.Propose(name, category, current));

    // ---- Selection ----

    [Fact]
    public void Pick_part_uses_the_active_part_and_needs_a_saved_file()
    {
        var p = FixCardRules.Pick(FixCardDocKind.Part, @"C:\V\a\B.ipt", Array.Empty<string?>());
        Assert.Equal(new[] { @"C:\V\a\B.ipt" }, p.Files);
        Assert.Null(p.Message);
        Assert.Equal(FixCardRules.SavePartMessage, FixCardRules.Pick(FixCardDocKind.Part, "", Array.Empty<string?>()).Message);
    }

    [Fact]
    public void Pick_drawing_or_nothing_open_gets_the_open_a_part_message()
    {
        Assert.Equal("Open a part, or select parts in an assembly.", FixCardRules.Pick(FixCardDocKind.Other, "x.idw", Array.Empty<string?>()).Message);
        Assert.Equal("Open a part, or select parts in an assembly.", FixCardRules.Pick(FixCardDocKind.None, null, Array.Empty<string?>()).Message);
    }

    [Fact]
    public void Pick_assembly_with_no_selection_asks_for_one()
    {
        var p = FixCardRules.Pick(FixCardDocKind.Assembly, @"C:\V\A.iam", Array.Empty<string?>());
        Assert.Empty(p.Files);
        Assert.Equal("Select the parts to fix in the browser, then click Fix Data Card.", p.Message);
    }

    [Fact]
    public void Pick_assembly_dedupes_by_file_and_lists_sub_assemblies_as_skipped()
    {
        var p = FixCardRules.Pick(FixCardDocKind.Assembly, @"C:\V\A.iam", new string?[]
        {
            @"C:\V\x\P1.ipt", @"c:\v\x\p1.IPT", @"C:/V/x/P1.ipt", @"C:\V\x\P2.ipt", @"C:\V\Sub.iam", @"c:\v\sub.iam", null, @"C:\V\d.idw",
        });
        Assert.Equal(new[] { @"C:\V\x\P1.ipt", @"C:\V\x\P2.ipt" }, p.Files);
        Assert.Null(p.Message);
        Assert.Contains("Sub.iam (.iam not supported yet)", p.Skipped);
        Assert.Equal(1, p.Skipped.Count(s => s.StartsWith("Sub.iam", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains("d.idw (not an .ipt file)", p.Skipped);
        Assert.Contains(p.Skipped, s => s.Contains("virtual"));
    }

    [Fact]
    public void Pick_assembly_with_only_sub_assemblies_gives_a_message_naming_them()
    {
        var p = FixCardRules.Pick(FixCardDocKind.Assembly, @"C:\V\A.iam", new string?[] { @"C:\V\Sub.iam" });
        Assert.Empty(p.Files);
        Assert.Contains(".iam not supported yet", p.Message);
    }

    // ---- Proposal ----

    [Fact]
    public void Propose_part_number_from_file_name_and_title_for_buy_parts()
    {
        var o = Edited("CD-001885.ipt", "Part - Purchased", Card());
        Assert.Equal("CD-001885", o[FixCardRules.PartNumberProp]);
        Assert.Equal("CD-001885-Steel Washers", o[FixCardRules.TitleProp]);
        Assert.Equal("McMASTER-CARR", o[FixCardRules.VendorProp]);
        Assert.Equal("12.5000", o[FixCardRules.CostProp]);
        Assert.Equal("93501A035", o[FixCardRules.VendorNumberProp]);
        Assert.Equal("Steel Washers", o[FixCardRules.DescriptionProp]);
    }

    [Fact]
    public void Propose_title_is_unchanged_for_make_parts_in_a_make_category()
    {
        var o = Edited("CD-001885.ipt", "Part - Made", Card(source: "Make", title: "keep me"));
        Assert.Equal("keep me", o[FixCardRules.TitleProp]);
        Assert.Equal("CD-001885", o[FixCardRules.PartNumberProp]);
    }

    [Fact]
    public void Propose_title_follows_the_category_when_source_is_blank_and_the_source_when_the_category_is_not_purchased()
    {
        Assert.Equal("CD-1-Steel Washers", Edited("CD-1.ipt", "Part - Purchased", Card(source: ""))[FixCardRules.TitleProp]);
        Assert.Equal("CD-1-Steel Washers", Edited("CD-1.ipt", "Part - Made", Card(source: "buy"))[FixCardRules.TitleProp]);
    }

    [Fact]
    public void Propose_title_with_blank_description_is_the_part_number()
    {
        Assert.Equal("CD-1", Edited("CD-1.ipt", "Part - Purchased", Card(desc: ""))[FixCardRules.TitleProp]);
    }

    [Fact]
    public void Propose_vendor_spelling_and_other_vendors_unchanged_and_blank_vendor_stays_blank()
    {
        Assert.Equal("McMASTER-CARR", Edited("a.ipt", "x", Card(vendor: "mcmaster-carr"))[FixCardRules.VendorProp]);
        Assert.Equal("Acme", Edited("a.ipt", "x", Card(vendor: "Acme"))[FixCardRules.VendorProp]);
        Assert.Equal("", Edited("a.ipt", "x", Card(vendor: ""))[FixCardRules.VendorProp]);
    }

    [Fact]
    public void Cost_formats_invariant_with_four_decimals()
    {
        Assert.Equal("12.5000", FixCardRules.FormatCost("12.5"));
        Assert.Equal("0.0000", FixCardRules.FormatCost("0"));
        Assert.Equal("1.2346", FixCardRules.FormatCost("1.23456"));
        Assert.Equal("1234.0000", FixCardRules.FormatCost("1234"));
        Assert.Equal("", FixCardRules.FormatCost(null));
        Assert.Equal("n/a", FixCardRules.FormatCost(" n/a "));
    }

    // ---- Changed fields only ----

    [Fact]
    public void BuildRequest_sends_only_fields_that_differ()
    {
        var current = Card(pn: "93501A035_Steel Washers", title: "old title", vendor: "McMaster-Carr");
        var b = FixCardRules.BuildRequest("$/L/CD-001885.ipt", current, Edited("CD-001885.ipt", "Part - Purchased", current), false, true);
        Assert.Empty(b.Errors);
        var r = b.Request!;
        Assert.Equal(new[] { "$/L/CD-001885.ipt" }, r.Files);
        Assert.False(r.PartNumberFromFileName);
        Assert.True(r.DryRun);
        Assert.Equal(new[] { "Part Number", "Vendor" }, r.Design.Keys.OrderBy(k => k).ToArray());
        Assert.Equal("CD-001885", r.Design["Part Number"]);
        Assert.Equal("McMASTER-CARR", r.Design["Vendor"]);
        Assert.Empty(r.Custom);
        Assert.Equal("CD-001885-Steel Washers", r.Summary!["Title"]);
    }

    [Fact]
    public void BuildRequest_returns_null_when_nothing_changed()
    {
        var current = Card(pn: "CD-001885", title: "CD-001885-Steel Washers", vendor: "McMASTER-CARR", cost: "12.5");
        var b = FixCardRules.BuildRequest("$/L/CD-001885.ipt", current, Edited("CD-001885.ipt", "Part - Purchased", current), false, true);
        Assert.Null(b.Request);
        Assert.Empty(b.Errors);
    }

    [Fact]
    public void BuildRequest_cost_compares_as_a_number_and_sends_the_invariant_value()
    {
        var current = Card(pn: "CD-1", title: "CD-1-Steel Washers", vendor: "McMASTER-CARR", cost: "12.5");
        var same = Edited("CD-1.ipt", "Part - Purchased", current);
        same[FixCardRules.CostProp] = "12.50";
        Assert.Null(FixCardRules.BuildRequest("$/L/CD-1.ipt", current, same, false, true).Request);
        same[FixCardRules.CostProp] = "13.123456";
        var r = FixCardRules.BuildRequest("$/L/CD-1.ipt", current, same, false, true).Request!;
        Assert.Equal("13.1235", r.Design["Cost"]);
        Assert.Single(r.Design);
    }

    [Fact]
    public void BuildRequest_rejects_bad_cost_and_blank_part_number()
    {
        var current = Card(pn: "CD-1");
        var e = Edited("CD-1.ipt", "x", current);
        e[FixCardRules.CostProp] = "twelve";
        Assert.Contains("Estimated Cost must be a number.", FixCardRules.BuildRequest("$/a.ipt", current, e, false, true).Errors);
        e[FixCardRules.CostProp] = "";
        Assert.Contains("Estimated Cost cannot be blank.", FixCardRules.BuildRequest("$/a.ipt", current, e, false, true).Errors);
        e[FixCardRules.CostProp] = "-1";
        Assert.Contains("Estimated Cost cannot be negative.", FixCardRules.BuildRequest("$/a.ipt", current, e, false, true).Errors);
        e[FixCardRules.CostProp] = "12.5";
        e[FixCardRules.PartNumberProp] = "  ";
        Assert.Contains("Part Number cannot be blank.", FixCardRules.BuildRequest("$/a.ipt", current, e, false, true).Errors);
    }

    [Fact]
    public void BuildRequest_blank_cost_over_a_blank_current_is_no_change()
    {
        var current = Card(pn: "CD-1", cost: "", title: "CD-1-Steel Washers", vendor: "McMASTER-CARR");
        var e = Edited("CD-1.ipt", "Part - Purchased", current);
        Assert.Equal("", e[FixCardRules.CostProp]);
        var b = FixCardRules.BuildRequest("$/a.ipt", current, e, false, true);
        Assert.Null(b.Request);
        Assert.Empty(b.Errors);
    }

    [Fact]
    public void BuildRequest_cleans_text_and_applies_vendor_spelling_to_typed_values_and_can_clear_a_field()
    {
        var current = Card(pn: "CD-1", process: "CNC");
        var e = Edited("CD-1.ipt", "x", current);
        e[FixCardRules.VendorProp] = "  mcmaster-carr ";
        e[FixCardRules.ProcessProp] = "";
        e[FixCardRules.FinishProp] = "Black\tOxide ";
        var r = FixCardRules.BuildRequest("$/a.ipt", current, e, true, false, "my comment").Request!;
        Assert.Equal("McMASTER-CARR", r.Design["Vendor"]);
        Assert.Equal("", r.Custom["Process"]);
        Assert.Equal("BlackOxide", r.Custom["Material Finish"]);
        Assert.True(r.ReviseReleased);
        Assert.False(r.DryRun);
        Assert.Equal("my comment", r.Comment);
    }

    [Fact]
    public void Differs_ignores_surrounding_whitespace_only_for_edits()
    {
        Assert.False(FixCardRules.Differs(FixCardRules.DescriptionProp, "a b", "a b"));
        Assert.False(FixCardRules.Differs(FixCardRules.DescriptionProp, "a b", " a b "));
        Assert.True(FixCardRules.Differs(FixCardRules.DescriptionProp, "a b", "a  b"));
        Assert.False(FixCardRules.Differs(FixCardRules.DescriptionProp, null, ""));
        Assert.True(FixCardRules.Differs(FixCardRules.CostProp, "12.5", "12.51"));
        Assert.False(FixCardRules.Differs(FixCardRules.CostProp, "12.5", "12.5000"));
    }

    // ---- Summary properties through the fix ----

    [Fact]
    public void ParseRequest_reads_the_summary_object_and_targets_include_it()
    {
        var body = JsonNode.Parse("{\"files\":[\"a.ipt\"],\"summary\":{\"Title\":\"T1\"},\"design\":{\"Vendor\":\"X\"}}")!.AsObject();
        var req = DataCardFix.ParseRequest(body);
        Assert.Equal("T1", req.Summary!["Title"]);
        var props = DataCardFix.Targets(req, "a.ipt", out var wanted);
        Assert.Contains(new PropRef(PropSet.Summary, "Title"), props);
        Assert.Equal("T1", wanted[new PropRef(PropSet.Summary, "Title")]);
        Assert.Throws<BridgeException>(() => DataCardFix.ParseRequest(JsonNode.Parse("{\"files\":[\"a.ipt\"],\"summary\":{\"Title\":1}}")!.AsObject()));
    }

    [Fact]
    public void Summary_property_set_has_its_own_inventor_name()
    {
        Assert.Equal("Inventor Summary Information", DataCardFix.SetName(PropSet.Summary));
        Assert.Equal("Design Tracking Properties", DataCardFix.SetName(PropSet.Design));
        Assert.Equal("Inventor User Defined Properties", DataCardFix.SetName(PropSet.Custom));
    }

    [Fact]
    public void Summary_title_goes_through_dry_run_compare_and_apply()
    {
        var host = new FakeHost();
        host.Files["CD-1.ipt"] = new() { Info("CD-1.ipt", "$/L/CD-1.ipt") };
        var title = new PropRef(PropSet.Summary, "Title");
        host.Current = new() { [title] = "old" };
        var req = new DataCardRequest(new[] { "CD-1.ipt" }, new Dictionary<string, string>(), new Dictionary<string, string>(), false, true, null, false,
            new Dictionary<string, string> { ["Title"] = "CD-1-Washer" });
        var preview = DataCardFix.Run(req, host);
        var r0 = preview["results"]![0]!;
        Assert.Equal("preview", (string)r0["status"]!);
        Assert.Equal("old", (string)r0["changes"]!["Title"]!["from"]!);
        Assert.Equal("CD-1-Washer", (string)r0["changes"]!["Title"]!["to"]!);
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("apply"));

        var applied = DataCardFix.Run(req with { DryRun = false }, host);
        Assert.Equal("fixed", (string)applied["results"]![0]!["status"]!);
        Assert.Contains(host.Calls, c => c.StartsWith("apply CD-1.ipt [Title]"));

        host.Calls.Clear();
        host.Current = new() { [title] = "CD-1-Washer" };
        Assert.Equal("no_change", (string)DataCardFix.Run(req with { DryRun = false }, host)["results"]![0]!["status"]!);
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("apply"));
    }

    // ---- Load and Batch ----

    [Fact]
    public void Load_reads_all_nine_fields_through_the_host_and_changes_nothing()
    {
        var host = new FakeHost();
        host.Files["$/L/CD-1.ipt"] = new() { Info("CD-1.ipt", "$/L/CD-1.ipt") };
        host.Current = Card();
        var row = FixCardRules.Load(host, "$/L/CD-1.ipt");
        Assert.Null(row.Problem);
        Assert.Equal("Part - Purchased", row.Category);
        Assert.Equal("Steel Washers", row.Current[FixCardRules.DescriptionProp]);
        Assert.Equal(new[] { "read CD-1.ipt" }, host.Calls);
        Assert.Equal(9, FixCardRules.Fields.Count);
    }

    [Fact]
    public void Load_reports_problems_instead_of_throwing()
    {
        var host = new FakeHost();
        Assert.Contains("not found", FixCardRules.Load(host, "$/L/none.ipt").Problem);
        host.Files["$/L/cc.ipt"] = new() { Info("cc.ipt", "$/L/cc.ipt", category: "Part - Content Center") };
        Assert.Contains("Content Center", FixCardRules.Load(host, "$/L/cc.ipt").Problem);
        host.Files["$/L/a.iam"] = new() { Info("a.iam", "$/L/a.iam") };
        Assert.Contains("not supported", FixCardRules.Load(host, "$/L/a.iam").Problem);
        host.Files["$/L/co.ipt"] = new() { Info("co.ipt", "$/L/co.ipt", co: true, by: "Pat") };
        Assert.Contains("checked out by Pat", FixCardRules.Load(host, "$/L/co.ipt").Problem);
        host.Files["$/L/dirty.ipt"] = new() { Info("dirty.ipt", "$/L/dirty.ipt") };
        host.SkipRead = true;
        Assert.Contains("open and dirty", FixCardRules.Load(host, "$/L/dirty.ipt").Problem);
        host.SkipRead = false;
        host.FindThrows = new InvalidOperationException("vault down");
        Assert.Equal("vault down", FixCardRules.Load(host, "$/L/x.ipt").Problem);
        host.FindThrows = new BridgeException("not_signed_in", "Sign in");
        Assert.Throws<BridgeException>(() => FixCardRules.Load(host, "$/L/x.ipt"));
    }

    [Fact]
    public void Load_shows_released_parts_so_the_form_can_gate_them()
    {
        var host = new FakeHost();
        host.Files["$/L/r.ipt"] = new() { Info("r.ipt", "$/L/r.ipt", state: "Released", released: true) };
        host.Current = Card();
        var row = FixCardRules.Load(host, "$/L/r.ipt");
        Assert.Null(row.Problem);
        Assert.True(row.Released);
    }

    private static (FakeHost Host, FixCardLoaded Row) Loaded(string path = "$/L/CD-1.ipt", string state = "Work in Progress", bool released = false)
    {
        var host = new FakeHost();
        var name = path[(path.LastIndexOf('/') + 1)..];
        host.Files[path] = new() { Info(name, path, state: state, released: released) };
        host.Current = Card();
        return (host, FixCardRules.Load(host, path));
    }

    [Fact]
    public void Batch_preview_then_apply_runs_the_normal_fix_per_row()
    {
        var (host, row) = Loaded();
        var edited = Edited(row.Name, row.Category, new(row.Current));
        var preview = new FixCardRules.Batch(host, false).Run(row, edited, true);
        Assert.Equal(FixCardOutcome.WouldChange, preview.Status);
        Assert.Contains("Part Number: 93501A035_Steel Washers -> CD-1", preview.Detail);
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("apply"));
        var applied = new FixCardRules.Batch(host, false).Run(row, edited, false);
        Assert.Equal(FixCardOutcome.Fixed, applied.Status);
        Assert.Equal("2", applied.Version);
        Assert.Contains(host.Calls, c => c.StartsWith("apply CD-1.ipt"));
    }

    [Fact]
    public void Batch_unchanged_row_is_no_change_and_never_touches_the_host_apply()
    {
        var (host, row) = Loaded();
        var o = new FixCardRules.Batch(host, false).Run(row, new Dictionary<PropRef, string>(row.Current.ToDictionary(k => k.Key, k => k.Value ?? "")), false);
        Assert.Equal(FixCardOutcome.NoChange, o.Status);
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("apply"));
    }

    [Fact]
    public void Batch_released_row_is_skipped_unless_revise_is_ticked()
    {
        var (host, row) = Loaded("$/L/CD-2.ipt", "Released", true);
        var edited = Edited(row.Name, row.Category, new(row.Current));
        var o = new FixCardRules.Batch(host, false).Run(row, edited, false);
        Assert.Equal(FixCardOutcome.Skipped, o.Status);
        Assert.StartsWith("Released: skipped", o.Detail);
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("state") || c.StartsWith("apply"));

        var revised = new FixCardRules.Batch(host, true).Run(row, edited, true);
        Assert.Equal(FixCardOutcome.WouldChange, revised.Status);
        Assert.Contains("would move to Work in Progress", revised.Detail);
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("state") || c.StartsWith("apply"));
    }

    [Fact]
    public void Batch_revise_apply_reports_revisions_and_state()
    {
        var (host, row) = Loaded("$/L/CD-2.ipt", "Released", true);
        var edited = Edited(row.Name, row.Category, new(row.Current));
        var o = new FixCardRules.Batch(host, true).Run(row, edited, false);
        Assert.Equal(FixCardOutcome.Fixed, o.Status);
        Assert.Equal("B", o.RevisionAfter);
        Assert.Equal("Released", o.StateAfter);
        Assert.Contains("state Released", o.Detail);
    }

    [Fact]
    public void Batch_a_failed_return_to_released_stops_every_later_released_row()
    {
        var host = new FakeHost { FailChangeTo = "Released" };
        host.Files["$/L/A.ipt"] = new() { Info("A.ipt", "$/L/A.ipt", state: "Released", released: true) };
        host.Files["$/L/B.ipt"] = new() { Info("B.ipt", "$/L/B.ipt", state: "Released", released: true) };
        host.Current = Card();
        var a = FixCardRules.Load(host, "$/L/A.ipt");
        var b = FixCardRules.Load(host, "$/L/B.ipt");
        var batch = new FixCardRules.Batch(host, true);
        var first = batch.Run(a, Edited(a.Name, a.Category, new(a.Current)), false);
        Assert.Equal(FixCardOutcome.Error, first.Status);
        Assert.StartsWith("re_release:", first.Detail);
        var second = batch.Run(b, Edited(b.Name, b.Category, new(b.Current)), false);
        Assert.Equal(FixCardOutcome.Skipped, second.Status);
        Assert.Equal(DataCardFix.ReviseStoppedReason, second.Detail);
        Assert.DoesNotContain(host.Calls, c => c.Contains("B.ipt ->"));
    }

    [Fact]
    public void Batch_bad_input_is_an_error_row_and_a_row_with_a_problem_is_skipped()
    {
        var (host, row) = Loaded();
        var e = Edited(row.Name, row.Category, new(row.Current));
        e[FixCardRules.CostProp] = "x";
        var o = new FixCardRules.Batch(host, false).Run(row, e, true);
        Assert.Equal(FixCardOutcome.Error, o.Status);
        Assert.Contains("Estimated Cost must be a number", o.Detail);
        var bad = row with { Problem = "nope" };
        var s = new FixCardRules.Batch(host, false).Run(bad, e, true);
        Assert.Equal(FixCardOutcome.Skipped, s.Status);
        Assert.Equal("nope", s.Detail);
    }

    [Fact]
    public void Batch_session_drop_stops_the_remaining_rows()
    {
        var host = new FakeHost { ReadThrows = new BridgeException("not_signed_in", "Sign in to Vault") };
        host.Files["$/L/A.ipt"] = new() { Info("A.ipt", "$/L/A.ipt") };
        host.Files["$/L/B.ipt"] = new() { Info("B.ipt", "$/L/B.ipt") };
        var row = new FixCardLoaded("$/L/A.ipt", "A.ipt", "Part - Purchased", "Work in Progress", false, Card(), null);
        var row2 = row with { VaultPath = "$/L/B.ipt", Name = "B.ipt" };
        var batch = new FixCardRules.Batch(host, false);
        var first = batch.Run(row, Edited("A.ipt", row.Category, new(row.Current)), true);
        Assert.Equal(FixCardOutcome.Error, first.Status);
        Assert.StartsWith("sign_in:", first.Detail);
        host.Calls.Clear();
        var second = batch.Run(row2, Edited("B.ipt", row.Category, new(row.Current)), true);
        Assert.Equal("$/L/B.ipt", second.File);
        Assert.StartsWith("sign_in:", second.Detail);
        Assert.Empty(host.Calls);
    }

    // ---- Describe, results text, dropdowns ----

    [Fact]
    public void Describe_maps_every_status()
    {
        Assert.Equal("skipped", FixCardRules.Describe(JsonNode.Parse("{\"file\":\"f\",\"status\":\"skipped\",\"reason\":\"why\"}")!.AsObject()).Status);
        var err = FixCardRules.Describe(JsonNode.Parse("{\"file\":\"f\",\"status\":\"error\",\"step\":\"checkin\",\"message\":\"boom\",\"state_after\":\"Work in Progress\"}")!.AsObject());
        Assert.Equal("error", err.Status);
        Assert.Equal("checkin: boom (state now Work in Progress)", err.Detail);
        var fx = FixCardRules.Describe(JsonNode.Parse("{\"file\":\"f\",\"status\":\"fixed\",\"version\":4,\"revision_before\":\"A\",\"revision_after\":\"B\",\"state_after\":\"Released\"}")!.AsObject());
        Assert.Equal("fixed", fx.Status);
        Assert.Equal("new version 4; revision A -> B; state Released", fx.Detail);
        Assert.Equal("4", fx.Version);
        Assert.Equal("no change", FixCardRules.Describe(JsonNode.Parse("{\"file\":\"f\",\"status\":\"no_change\",\"changes\":{}}")!.AsObject()).Status);
    }

    [Fact]
    public void ResultsText_is_tab_separated_with_a_header_and_no_stray_tabs_or_newlines()
    {
        var text = FixCardRules.ResultsText(new[]
        {
            new FixCardOutcome("$/L/A.ipt", "fixed", "new version 2", "A", "B", "Released", "2"),
            new FixCardOutcome("$/L/B.ipt", "error", "step:\tmulti\nline"),
        });
        var lines = text.TrimEnd((char)13, (char)10).Split("\r\n");
        Assert.Equal(3, lines.Length);
        Assert.Equal("File\tStatus\tDetail\tRevision before\tRevision after\tState after\tVersion", lines[0]);
        Assert.Equal("$/L/A.ipt\tfixed\tnew version 2\tA\tB\tReleased\t2", lines[1]);
        Assert.Equal(7, lines[2].Split('\t').Length);
        Assert.Contains("step: multi line", lines[2]);
    }

    [Fact]
    public void Dropdown_choices_keep_the_current_value()
    {
        Assert.Equal(new[] { "Buy", "Make" }, FixCardRules.SourceChoices("Make"));
        Assert.Equal(new[] { "Buy", "Make", "Lab" }, FixCardRules.SourceChoices("Lab"));
        Assert.Equal(new[] { "", "CNC", "Laser" }, FixCardRules.ProcessChoices(new[] { "CNC", "Laser", " " }, ""));
        Assert.Equal(new[] { "", "CNC", "Weird" }, FixCardRules.ProcessChoices(new[] { "CNC" }, "Weird"));
    }
}
