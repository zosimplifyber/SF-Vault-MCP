using System.Text.Json.Nodes;
using SFVaultBridge.Core;

namespace SFVaultBridge.Core.Tests;

// Numeric properties and the opt-in revise-and-re-release path of fix_data_card.
public partial class DataCardFixTests
{
    // ---- numeric properties ----

    [Theory]
    [InlineData("12.5", 12.5)]
    [InlineData(" 3 ", 3)]
    [InlineData("1e2", 100)]
    [InlineData("0", 0)]
    public void Numbers_parse_with_the_invariant_culture(string text, double expected)
    {
        Assert.True(DataCardFix.TryParseNumber(text, out var d));
        Assert.Equal(expected, d);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("12,5")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1.2.3")]
    public void Non_numbers_do_not_parse(string text) => Assert.False(DataCardFix.TryParseNumber(text, out _));

    [Fact]
    public void Number_parsing_ignores_the_current_culture()
    {
        var old = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.True(DataCardFix.TryParseNumber("12.5", out var d));
            Assert.Equal(12.5, d);
            Assert.Equal("12.5", DataCardFix.FormatNumber(d));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = old; }
    }

    [Fact]
    public void Cost_is_numeric_and_other_properties_are_not()
    {
        Assert.True(DataCardFix.IsNumeric(new PropRef(PropSet.Design, "Cost")));
        Assert.True(DataCardFix.IsNumeric(new PropRef(PropSet.Design, "cost")));
        Assert.False(DataCardFix.IsNumeric(new PropRef(PropSet.Design, "Vendor")));
        Assert.False(DataCardFix.IsNumeric(new PropRef(PropSet.Custom, "Cost")));
    }

    private static string Status(JsonObject o) => o["results"]![0]!["status"]!.GetValue<string>();

    [Fact]
    public void Cost_preview_is_invariant_and_equal_numbers_are_no_change()
    {
        var cost = new PropRef(PropSet.Design, "Cost");
        var h = new FakeHost();
        h.Files["a.ipt"] = new() { Info("a.ipt", "$/a.ipt") };
        var want = new Dictionary<string, string> { ["Cost"] = "12.50" };
        h.Current = new() { [cost] = "3" };
        var r = DataCardFix.Run(Req(new[] { "a.ipt" }, pn: false, design: want), h)["results"]![0]!;
        Assert.Equal("preview", r["status"]!.GetValue<string>());
        Assert.Equal("12.5", r["changes"]!["Cost"]!["to"]!.GetValue<string>());
        h.Current = new() { [cost] = "12.5" };
        Assert.Equal("no_change", Status(DataCardFix.Run(Req(new[] { "a.ipt" }, pn: false, design: want), h)));
        h.Current = new() { [cost] = "12.500" };
        Assert.Equal("no_change", Status(DataCardFix.Run(Req(new[] { "a.ipt" }, pn: false, design: want), h)));
    }

    [Fact]
    public void Cost_that_is_not_a_number_is_a_per_file_error_before_anything_is_touched()
    {
        var h = new FakeHost();
        h.Files["a.ipt"] = new() { Info("a.ipt", "$/a.ipt") };
        var o = DataCardFix.Run(Req(new[] { "a.ipt" }, dry: false, pn: false, design: new() { ["Cost"] = "twelve" }), h);
        var r = o["results"]![0]!;
        Assert.Equal("error", r["status"]!.GetValue<string>());
        Assert.Equal("Cost must be a number", r["message"]!.GetValue<string>());
        Assert.Empty(h.Calls);
    }

    // ---- reviseReleased ----

    private static (FakeHost, DataCardRequest) ReleasedCase(bool dry, bool revise = true, string category = "Part - Purchased")
    {
        var h = new FakeHost();
        h.Files["a.ipt"] = new() { Info("a.ipt", "$/a.ipt", category: category, state: "Released", released: true, ver: 3) with { Revision = "A" } };
        h.Current = new() { [new PropRef(PropSet.Design, "Cost")] = "1" };
        return (h, Req(new[] { "a.ipt" }, dry: dry, pn: false, design: new() { ["Cost"] = "2.5" }, revise: revise));
    }

    private static string[] Moves(FakeHost h) => h.Calls.Where(c => c.StartsWith("state") || c.StartsWith("apply")).ToArray();

    [Fact]
    public void Revise_flag_defaults_to_false_and_released_files_stay_skipped()
    {
        Assert.False(DataCardFix.ParseRequest(new JsonObject { ["files"] = new JsonArray("a.ipt") }).ReviseReleased);
        Assert.True(DataCardFix.ParseRequest(JsonNode.Parse("""{"files":["a.ipt"],"reviseReleased":true}""")!.AsObject()).ReviseReleased);
        var (h, req) = ReleasedCase(dry: false, revise: false);
        Assert.Equal("skipped", Status(DataCardFix.Run(req, h)));
        Assert.Empty(Moves(h));
    }

    [Fact]
    public void Revise_dry_run_shows_would_revise_and_transitions_and_changes_nothing()
    {
        var (h, req) = ReleasedCase(dry: true);
        h.Linked = true;
        var r = DataCardFix.Run(req, h)["results"]![0]!;
        Assert.Equal("preview", r["status"]!.GetValue<string>());
        Assert.True(r["would_revise"]!.GetValue<bool>());
        Assert.Equal(new[] { "Released -> Work in Progress", "Work in Progress -> Released" },
            r["transitions"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        Assert.True(r["linked_to_item"]!.GetValue<bool>());
        Assert.Equal("2.5", r["changes"]!["Cost"]!["to"]!.GetValue<string>());
        Assert.Empty(Moves(h));
    }

    [Fact]
    public void Revise_runs_wip_then_edit_then_back_to_released_and_reports_revisions()
    {
        var (h, req) = ReleasedCase(dry: false);
        var r = DataCardFix.Run(req, h)["results"]![0]!;
        Assert.Equal("fixed", r["status"]!.GetValue<string>());
        Assert.Equal("Released", r["state_after"]!.GetValue<string>());
        Assert.Equal("A", r["revision_before"]!.GetValue<string>());
        Assert.Equal("B", r["revision_after"]!.GetValue<string>());
        Assert.Null(r["linked_to_item"]);
        var order = Moves(h);
        Assert.Equal(3, order.Length);
        Assert.Equal("state a.ipt -> Work in Progress", order[0]);
        Assert.StartsWith("apply a.ipt [Cost]", order[1]);
        Assert.Equal("state a.ipt -> Released", order[2]);
        Assert.Equal("Released", h.StateByPath["$/a.ipt"]);
    }

    [Fact]
    public void Revise_edit_failure_still_returns_the_file_to_released_and_reports_both()
    {
        var (h, req) = ReleasedCase(dry: false);
        h.FailApply = true;
        var r = DataCardFix.Run(req, h)["results"]![0]!;
        Assert.Equal("error", r["status"]!.GetValue<string>());
        Assert.Equal("checkin", r["step"]!.GetValue<string>());
        Assert.True(r["re_released"]!.GetValue<bool>());
        Assert.Equal("Released", r["state_after"]!.GetValue<string>());
        Assert.Equal("Released", h.StateByPath["$/a.ipt"]);
    }

    [Fact]
    public void Revise_host_skip_in_the_middle_still_returns_the_file_to_released()
    {
        var (h, req) = ReleasedCase(dry: false);
        h.ApplySkips = true;
        var r = DataCardFix.Run(req, h)["results"]![0]!;
        Assert.Equal("error", r["status"]!.GetValue<string>());
        Assert.Equal("Released", r["state_after"]!.GetValue<string>());
        Assert.Equal("Released", h.StateByPath["$/a.ipt"]);
    }

    [Fact]
    public void Failing_to_return_to_released_is_loud_and_reports_the_actual_state()
    {
        var (h, req) = ReleasedCase(dry: false);
        h.FailChangeTo = "Released";
        var o = DataCardFix.Run(req, h);
        var r = o["results"]![0]!;
        Assert.Equal("error", r["status"]!.GetValue<string>());
        Assert.Equal("re_release", r["step"]!.GetValue<string>());
        Assert.Equal("Work in Progress", r["state_after"]!.GetValue<string>());
        Assert.Equal(4, r["version"]!.GetValue<long>());      // the edit itself was checked in
        Assert.Equal(1, o["counts"]!["error"]!.GetValue<int>());
    }

    [Fact]
    public void Failing_to_return_after_a_failed_edit_reports_both_errors()
    {
        var (h, req) = ReleasedCase(dry: false);
        h.FailApply = true;
        h.FailChangeTo = "Released";
        var r = DataCardFix.Run(req, h)["results"]![0]!;
        Assert.Equal("re_release", r["step"]!.GetValue<string>());
        Assert.Equal("Work in Progress", r["state_after"]!.GetValue<string>());
        Assert.Contains("also failed at checkin", r["message"]!.GetValue<string>());
        Assert.NotNull(r["edit_error"]);
    }

    [Fact]
    public void Failing_the_first_move_is_a_revise_error_and_nothing_is_edited()
    {
        var (h, req) = ReleasedCase(dry: false);
        h.FailChangeTo = "Work in Progress";
        var r = DataCardFix.Run(req, h)["results"]![0]!;
        Assert.Equal("revise", r["step"]!.GetValue<string>());
        Assert.Equal("Released", r["state_after"]!.GetValue<string>());
        Assert.DoesNotContain(h.Calls, c => c.StartsWith("apply"));
    }

    [Fact]
    public void Missing_transition_skips_before_anything_changes_in_dry_and_real_runs()
    {
        foreach (var dry in new[] { true, false })
        {
            var (h, req) = ReleasedCase(dry);
            h.Plan = new RevisePlan("no transition from Released to Work in Progress");
            var r = DataCardFix.Run(req, h)["results"]![0]!;
            Assert.Equal("skipped", r["status"]!.GetValue<string>());
            Assert.Equal("no transition from Released to Work in Progress", r["reason"]!.GetValue<string>());
            Assert.Empty(Moves(h));
        }
    }

    [Fact]
    public void Content_center_files_are_never_revised()
    {
        var (h, req) = ReleasedCase(dry: false, category: "Part - Content Center");
        var r = DataCardFix.Run(req, h)["results"]![0]!;
        Assert.Equal("skipped", r["status"]!.GetValue<string>());
        Assert.Contains("Content Center", r["reason"]!.GetValue<string>());
        Assert.DoesNotContain(h.Calls, c => c.StartsWith("state") || c.StartsWith("plan"));
        var byPath = new FakeHost();
        byPath.Files["c.ipt"] = new() { Info("c.ipt", "$/Content Center Files/x/c.ipt", state: "Released", released: true) };
        Assert.Equal("skipped", Status(DataCardFix.Run(Req(new[] { "c.ipt" }, dry: false, revise: true), byPath)));
        Assert.Empty(byPath.Calls);
    }

    [Fact]
    public void A_file_in_an_obsolete_state_is_not_revised()
    {
        var obs = new LifecycleState(3, "Obsolete", true, true, 0);
        var t = new[] { new LifecycleTransition(1, 3, 2), new LifecycleTransition(2, 2, 3) };
        var p = LifecycleRevise.Plan(new[] { obs, new LifecycleState(2, "Work in Progress", false, false, 1) }, t, new HashSet<long> { 1, 2 }, 3);
        Assert.Equal("Obsolete is an obsolete state; the file is not revised", p.Problem);
    }

    [Fact]
    public void Revise_does_nothing_when_no_property_changes()
    {
        var (h, req) = ReleasedCase(dry: false);
        h.Current = new() { [new PropRef(PropSet.Design, "Cost")] = "2.5" };
        Assert.Equal("no_change", Status(DataCardFix.Run(req, h)));
        Assert.Empty(Moves(h));
    }

    [Fact]
    public void Linked_item_is_reported_after_a_revision()
    {
        var (h, req) = ReleasedCase(dry: false);
        h.Linked = true;
        Assert.True(DataCardFix.Run(req, h)["results"]![0]!["linked_to_item"]!.GetValue<bool>());
    }

    [Fact]
    public void Wip_files_never_touch_the_lifecycle_even_with_the_flag()
    {
        var h = new FakeHost();
        h.Files["a.ipt"] = new() { Info("a.ipt", "$/a.ipt") };
        var r = DataCardFix.Run(Req(new[] { "a.ipt" }, dry: false, revise: true), h)["results"]![0]!;
        Assert.Equal("fixed", r["status"]!.GetValue<string>());
        Assert.Null(r["would_revise"]);
        Assert.DoesNotContain(h.Calls, c => c.StartsWith("state") || c.StartsWith("plan"));
    }

    // ---- fix round 1 ----

    [Fact]
    public void Apply_gets_the_refreshed_version_after_a_state_change_bumps_it()
    {
        var (h, req) = ReleasedCase(dry: false);
        h.BumpVersionOnState = true;
        var r = DataCardFix.Run(req, h)["results"]![0]!;
        Assert.Equal(new long[] { 4 }, h.ApplyVersions);
        Assert.Equal("fixed", r["status"]!.GetValue<string>());
        Assert.Equal(5, r["version"]!.GetValue<long>());
    }

    [Fact]
    public void Revised_result_carries_transitions_revisions_state_and_compliance()
    {
        var (h, req) = ReleasedCase(dry: false);
        h.Linked = true;
        var r = DataCardFix.Run(req, h)["results"]![0]!;
        Assert.Equal(new[] { "Released -> Work in Progress", "Work in Progress -> Released" },
            r["transitions"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        Assert.Equal("A", r["revision_before"]!.GetValue<string>());
        Assert.Equal("B", r["revision_after"]!.GetValue<string>());
        Assert.Equal("Released", r["state_after"]!.GetValue<string>());
        Assert.True(r["linked_to_item"]!.GetValue<bool>());
        Assert.Equal("Compliant", r["property_compliance"]!.GetValue<string>());
        var (h2, req2) = ReleasedCase(dry: false);
        h2.Compliance = null;
        var r2 = DataCardFix.Run(req2, h2)["results"]![0]!;
        Assert.True(r2.AsObject().ContainsKey("property_compliance"));
        Assert.Null(r2["property_compliance"]);
    }

    [Fact]
    public void A_first_move_that_committed_but_reported_failure_is_returned_to_released_without_editing()
    {
        var (h, req) = ReleasedCase(dry: false);
        h.ThrowAfterChangeTo = "Work in Progress";
        var r = DataCardFix.Run(req, h)["results"]![0]!;
        Assert.Equal("error", r["status"]!.GetValue<string>());
        Assert.Equal("revise", r["step"]!.GetValue<string>());
        Assert.True(r["re_released"]!.GetValue<bool>());
        Assert.Equal("Released", r["state_after"]!.GetValue<string>());
        Assert.Contains("timed out", r["message"]!.GetValue<string>());
        Assert.Equal(new[] { "state a.ipt -> Work in Progress", "state a.ipt -> Released" }, Moves(h));
    }

    [Fact]
    public void A_committed_first_move_that_cannot_be_returned_reports_re_release()
    {
        var (h, req) = ReleasedCase(dry: false);
        h.ThrowAfterChangeTo = "Work in Progress";
        h.FailChangeTo = "Released";
        var r = DataCardFix.Run(req, h)["results"]![0]!;
        Assert.Equal("re_release", r["step"]!.GetValue<string>());
        Assert.Equal("Work in Progress", r["state_after"]!.GetValue<string>());
        Assert.Contains("by hand", r["message"]!.GetValue<string>());
    }

    [Fact]
    public void Host_skip_after_the_first_move_reports_skipped_and_re_released()
    {
        var (h, req) = ReleasedCase(dry: false);
        h.ApplySkips = true;
        var r = DataCardFix.Run(req, h)["results"]![0]!;
        Assert.Equal("skipped", r["step"]!.GetValue<string>());
        Assert.True(r["re_released"]!.GetValue<bool>());
    }

    [Fact]
    public void After_a_re_release_failure_later_revisions_stop_but_plain_fixes_continue()
    {
        var h = new FakeHost();
        foreach (var n in new[] { "a.ipt", "b.ipt" })
        {
            h.Files[n] = new() { Info(n, "$/" + n, state: "Released", released: true, ver: 3) };
            h.StateByPath["$/" + n] = "Released";
        }
        h.Files["w.ipt"] = new() { Info("w.ipt", "$/w.ipt") };
        h.Files["c.ipt"] = new() { Info("c.ipt", "$/c.ipt", state: "Released", released: true) };
        h.StateByPath["$/c.ipt"] = "Released";
        h.Current = new() { [new PropRef(PropSet.Design, "Cost")] = "1" };
        h.FailChangeTo = "Released";
        var req = Req(new[] { "a.ipt", "b.ipt", "w.ipt", "c.ipt" }, dry: false, pn: false, design: new() { ["Cost"] = "2.5" }, revise: true);
        var res = DataCardFix.Run(req, h)["results"]!.AsArray();
        Assert.Equal("re_release", res[0]!["step"]!.GetValue<string>());
        Assert.Equal("skipped", res[1]!["status"]!.GetValue<string>());
        Assert.Equal("stopped: an earlier file could not be returned to Released", res[1]!["reason"]!.GetValue<string>());
        Assert.Equal("fixed", res[2]!["status"]!.GetValue<string>());
        Assert.Equal("skipped", res[3]!["status"]!.GetValue<string>());
        Assert.DoesNotContain(h.Calls, c => c.StartsWith("state b.ipt") || c.StartsWith("state c.ipt"));
        Assert.DoesNotContain(h.Calls, c => c.StartsWith("apply b.ipt"));
    }

    [Fact]
    public void Cost_is_rounded_to_four_decimals_before_compare_and_write()
    {
        var cost = new PropRef(PropSet.Design, "Cost");
        var h = new FakeHost();
        h.Files["a.ipt"] = new() { Info("a.ipt", "$/a.ipt") };
        var want = new Dictionary<string, string> { ["Cost"] = "12.34567" };
        h.Current = new() { [cost] = "1" };
        Assert.Equal("12.3457", DataCardFix.Run(Req(new[] { "a.ipt" }, pn: false, design: want), h)["results"]![0]!["changes"]!["Cost"]!["to"]!.GetValue<string>());
        h.Current = new() { [cost] = "12.3457" };
        Assert.Equal("no_change", Status(DataCardFix.Run(Req(new[] { "a.ipt" }, pn: false, design: want), h)));
        Assert.Equal(0.0001, DataCardFix.RoundCurrency(0.00005));
    }
}
