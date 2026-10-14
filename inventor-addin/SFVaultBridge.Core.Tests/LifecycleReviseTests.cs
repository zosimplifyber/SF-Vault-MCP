using System.Text.Json.Nodes;
using SFVaultBridge.Core;

namespace SFVaultBridge.Core.Tests;

// State selection for the revise path: flags and the file's own lifecycle, never the names Released / Work in Progress.
public partial class DataCardFixTests
{
    private static readonly LifecycleState Rel = new(1, "Released", true, false, 1);
    private static readonly LifecycleState Wip = new(2, "Work in Progress", false, false, 2);
    private static readonly LifecycleState Lib = new(10, "Library", true, false, 1);
    private static readonly LifecycleState LibEdit = new(11, "Library Edit", false, false, 2);
    private static readonly LifecycleState Obs = new(3, "Obsolete", false, true, 0);
    private static readonly LifecycleState Draft = new(4, "Draft", false, false, 0);

    private static LifecycleTransition T(long id, LifecycleState from, LifecycleState to) => new(id, from.Id, to.Id);

    private static ISet<long> All(params LifecycleTransition[] t) => new HashSet<long>(t.Select(x => x.Id));

    [Fact]
    public void Released_detection_uses_the_flag_not_the_name()
    {
        var states = new[] { Rel, Wip, Lib, LibEdit };
        Assert.True(LifecycleRevise.IsReleasedState(states, Lib.Id));
        Assert.True(LifecycleRevise.IsReleasedState(states, Rel.Id));
        Assert.False(LifecycleRevise.IsReleasedState(states, LibEdit.Id));
        Assert.False(LifecycleRevise.IsReleasedState(states, 999));
        Assert.True(DataCardFix.IsReleased(Info(state: "Library", released: true)));
        Assert.False(DataCardFix.IsReleased(Info(state: "Library Edit", released: false)));
    }

    [Fact]
    public void Work_in_progress_is_preferred_over_other_editable_states()
    {
        var t = new[] { T(1, Rel, Wip), T(2, Wip, Rel), T(3, Rel, Draft), T(4, Draft, Rel), T(5, Rel, LibEdit), T(6, LibEdit, Rel) };
        var p = LifecycleRevise.Plan(new[] { Rel, Wip, Draft, LibEdit }, t, All(t), Rel.Id);
        Assert.Null(p.Problem);
        Assert.Equal("Work in Progress", p.EditState);
        Assert.Equal("Released", p.ReturnState);
        Assert.Equal("Released -> Work in Progress", p.ToWorkInProgress);
        Assert.Equal("Work in Progress -> Released", p.ToReleased);
    }

    [Fact]
    public void Library_edit_is_chosen_when_it_is_the_only_editable_state_and_the_return_is_to_library()
    {
        var t = new[] { T(1, Lib, LibEdit), T(2, LibEdit, Lib) };
        var p = LifecycleRevise.Plan(new[] { Lib, LibEdit }, t, All(t), Lib.Id);
        Assert.Null(p.Problem);
        Assert.Equal("Library Edit", p.EditState);
        Assert.Equal("Library", p.ReturnState);
        Assert.Equal(new[] { "Library -> Library Edit", "Library Edit -> Library" }, new[] { p.ToWorkInProgress, p.ToReleased });
    }

    [Fact]
    public void An_edit_suffix_beats_display_order_and_display_order_breaks_the_last_tie()
    {
        var a = new LifecycleState(20, "Rework", false, false, 1);
        var b = new LifecycleState(21, "Parts Edit", false, false, 5);
        var c = new LifecycleState(22, "Another", false, false, 0);
        var t = new[] { T(1, Lib, a), T(2, a, Lib), T(3, Lib, b), T(4, b, Lib), T(5, Lib, c), T(6, c, Lib) };
        Assert.Equal("Parts Edit", LifecycleRevise.Plan(new[] { Lib, a, b, c }, t, All(t), Lib.Id).EditState);
        Assert.Equal("Another", LifecycleRevise.Plan(new[] { Lib, a, c }, t, All(t), Lib.Id).EditState);
    }

    [Fact]
    public void Obsolete_and_released_states_are_never_the_edit_target()
    {
        var t = new[] { T(1, Rel, Obs), T(2, Obs, Rel), T(3, Rel, Lib), T(4, Lib, Rel) };
        var p = LifecycleRevise.Plan(new[] { Rel, Obs, Lib }, t, All(t), Rel.Id);
        Assert.Equal("no transition from Released to an editable state", p.Problem);
    }

    [Fact]
    public void No_editable_state_skips_with_a_reason()
    {
        var p = LifecycleRevise.Plan(new[] { Lib }, Array.Empty<LifecycleTransition>(), All(), Lib.Id);
        Assert.Equal("no transition from Library to an editable state", p.Problem);
    }

    [Fact]
    public void A_missing_way_back_skips_before_anything_changes()
    {
        var t = new[] { T(1, Lib, LibEdit) };
        var p = LifecycleRevise.Plan(new[] { Lib, LibEdit }, t, All(t), Lib.Id);
        Assert.Equal("no transition from Library Edit to Library", p.Problem);
    }

    [Fact]
    public void A_way_back_to_a_different_released_state_is_not_a_way_back()
    {
        var t = new[] { T(1, Lib, LibEdit), T(2, LibEdit, Rel) };
        var p = LifecycleRevise.Plan(new[] { Lib, Rel, LibEdit }, t, All(t), Lib.Id);
        Assert.Equal("no transition from Library Edit to Library", p.Problem);
    }

    [Fact]
    public void Transitions_the_user_may_not_take_are_refused()
    {
        var t = new[] { T(1, Lib, LibEdit), T(2, LibEdit, Lib) };
        Assert.Equal("you are not allowed to change the state from Library to Library Edit",
            LifecycleRevise.Plan(new[] { Lib, LibEdit }, t, All(t[1]), Lib.Id).Problem);
        Assert.Equal("you are not allowed to change the state from Library Edit to Library",
            LifecycleRevise.Plan(new[] { Lib, LibEdit }, t, All(t[0]), Lib.Id).Problem);
    }

    [Fact]
    public void An_unusable_preferred_state_falls_back_to_one_with_an_allowed_way_back()
    {
        var t = new[] { T(1, Rel, Wip), T(2, Rel, Draft), T(3, Draft, Rel) };
        var p = LifecycleRevise.Plan(new[] { Rel, Wip, Draft }, t, All(t), Rel.Id);
        Assert.Null(p.Problem);
        Assert.Equal("Draft", p.EditState);
    }

    // ---- through RunOne ----

    private static FakeHost LibraryCase()
    {
        var h = new FakeHost();
        h.Files["CD-001162.ipt"] = new() { Info("CD-001162.ipt", "$/CD-001162.ipt", category: "Part - Purchased", state: "Library", released: true, ver: 3) with { Revision = "A" } };
        h.Current = new() { [new PropRef(PropSet.Design, "Cost")] = "1" };
        h.Plan = new RevisePlan(null, "Library -> Library Edit", "Library Edit -> Library", "Library Edit", "Library");
        return h;
    }

    private static DataCardRequest LibReq(bool dry, bool revise = true) =>
        Req(new[] { "CD-001162.ipt" }, dry: dry, pn: false, design: new() { ["Cost"] = "2.5" }, revise: revise);

    private static JsonNode Only(JsonObject o) => o["results"]![0]!;

    [Fact]
    public void A_library_file_without_the_revise_tick_is_skipped_naming_its_state()
    {
        var r = Only(DataCardFix.Run(LibReq(dry: false, revise: false), LibraryCase()));
        Assert.Equal("CD-001162.ipt is in state Library (released). Tick Revise to edit it.", r["reason"]!.GetValue<string>());
    }

    [Fact]
    public void A_library_file_is_revised_through_library_edit_and_returned_to_library()
    {
        var h = LibraryCase();
        var r = Only(DataCardFix.Run(LibReq(dry: false), h));
        Assert.Equal("fixed", r["status"]!.GetValue<string>());
        Assert.Equal("Library", r["state_after"]!.GetValue<string>());
        Assert.Equal("Library", h.StateByPath["$/CD-001162.ipt"]);
        var moves = Moves(h);
        Assert.Equal(3, moves.Length);
        Assert.Equal("state CD-001162.ipt -> Library Edit", moves[0]);
        Assert.StartsWith("apply", moves[1]);
        Assert.Equal("state CD-001162.ipt -> Library", moves[2]);
    }

    [Fact]
    public void The_dry_run_shows_the_library_transition_names()
    {
        var r = Only(DataCardFix.Run(LibReq(dry: true), LibraryCase()));
        Assert.Equal(new[] { "Library -> Library Edit", "Library Edit -> Library" }, r["transitions"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
    }

    [Fact]
    public void No_editable_state_skips_the_file_and_changes_nothing()
    {
        var h = LibraryCase();
        h.Plan = new RevisePlan("no transition from Library to an editable state");
        var r = Only(DataCardFix.Run(LibReq(dry: false), h));
        Assert.Equal("skipped", r["status"]!.GetValue<string>());
        Assert.Equal("no transition from Library to an editable state", r["reason"]!.GetValue<string>());
        Assert.Empty(Moves(h));
    }

    [Fact]
    public void A_failed_return_to_library_names_library()
    {
        var h = LibraryCase();
        h.FailChangeTo = "Library";
        var r = Only(DataCardFix.Run(LibReq(dry: false), h));
        Assert.Equal("re_release", r["step"]!.GetValue<string>());
        Assert.Contains("Could not return the file to Library", r["message"]!.GetValue<string>());
        Assert.Equal("Library Edit", r["state_after"]!.GetValue<string>());
    }

    [Fact]
    public void A_content_center_file_in_the_library_lifecycle_is_still_never_edited()
    {
        var h = LibraryCase();
        h.Files["CD-001162.ipt"] = new() { Info("CD-001162.ipt", "$/Content Center Files/x/CD-001162.ipt", category: "Part - Content Center", state: "Library", released: true) };
        var r = Only(DataCardFix.Run(LibReq(dry: false), h));
        Assert.Equal("skipped", r["status"]!.GetValue<string>());
        Assert.Contains("Content Center", r["reason"]!.GetValue<string>());
        Assert.Empty(Moves(h));
    }

    [Fact]
    public void A_released_type_state_with_another_name_is_revised_like_any_released_file()
    {
        Assert.Null(DataCardFix.SkipReason(Info(state: "Library", released: true), reviseReleased: true));
    }
}
