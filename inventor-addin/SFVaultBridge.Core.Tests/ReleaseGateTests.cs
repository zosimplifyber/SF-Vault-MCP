using SFVaultBridge.Core;

namespace SFVaultBridge.Core.Tests;

public class ReleaseGateTests
{
    private static GatePart Ok(string pn = "CD-1") =>
        new(pn, true, "Released", false, null, false, true, true, "Released", false, null, false);

    [Fact]
    public void Released_model_and_drawing_pass() => Assert.Empty(ReleaseGate.Check(new[] { Ok() }));

    [Fact]
    public void State_match_ignores_case_and_whitespace() =>
        Assert.Empty(ReleaseGate.Check(new[] { Ok() with { ModelState = " released ", DrawingState = "RELEASED" } }));

    [Fact]
    public void Work_in_progress_model_blocks_with_state()
    {
        var b = ReleaseGate.Check(new[] { Ok() with { ModelState = "Work in Progress" } });
        Assert.Equal(new[] { "CD-1: model is Work in Progress" }, b);
    }

    [Fact]
    public void Missing_drawing_blocks()
    {
        var b = ReleaseGate.Check(new[] { Ok() with { HasDrawing = false, DrawingState = "" } });
        Assert.Equal(new[] { "CD-1: no drawing" }, b);
    }

    [Fact]
    public void Drawing_not_required_ignores_drawing_fields()
    {
        var p = Ok() with { DrawingRequired = false, HasDrawing = false, DrawingState = "Work in Progress" };
        Assert.Empty(ReleaseGate.Check(new[] { p }));
    }

    [Fact]
    public void Checked_out_names_the_user()
    {
        var b = ReleaseGate.Check(new[] { Ok() with { DrawingCheckedOut = true, DrawingCheckedOutBy = "jsmith" } });
        Assert.Equal(new[] { "CD-1: drawing is checked out by jsmith" }, b);
    }

    [Fact]
    public void Checked_out_without_name_and_open_dirty_both_listed()
    {
        var b = ReleaseGate.Check(new[] { Ok() with { ModelCheckedOut = true, ModelOpenDirty = true } });
        Assert.Equal(new[] { "CD-1: model is checked out; model is open in Inventor with unsaved changes" }, b);
    }

    [Fact]
    public void Open_at_older_version_blocks_model_and_drawing()
    {
        var b = ReleaseGate.Check(new[] { Ok() with { ModelOpenOlder = true, DrawingOpenOlder = true } });
        Assert.Equal(new[] { "CD-1: model is open in Inventor at an older version; close it or Get Latest; drawing is open in Inventor at an older version; close it or Get Latest" }, b);
    }

    [Fact]
    public void Dirty_wins_over_older()
    {
        var b = ReleaseGate.Check(new[] { Ok() with { ModelOpenDirty = true, ModelOpenOlder = true } });
        Assert.Equal(new[] { "CD-1: model is open in Inventor with unsaved changes" }, b);
    }

    [Fact]
    public void Older_check_uses_two_second_slack()
    {
        var t = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(LocalCopy.IsOlderThanLatest(t.AddMinutes(-5), t));
        Assert.False(LocalCopy.IsOlderThanLatest(t.AddSeconds(-1), t));
        Assert.False(LocalCopy.IsOlderThanLatest(t.AddMinutes(5), t));
    }

    [Fact]
    public void Unknown_state_blocks()
    {
        var b = ReleaseGate.Check(new[] { Ok() with { ModelState = "" } });
        Assert.Equal(new[] { "CD-1: model is in an unknown state" }, b);
    }

    [Fact]
    public void Model_not_in_vault_blocks_alone()
    {
        var b = ReleaseGate.Check(new[] { Ok() with { ModelFound = false } });
        Assert.Equal(new[] { "CD-1: model not in Vault" }, b);
    }

    [Fact]
    public void Every_part_is_listed_and_message_names_override()
    {
        var b = ReleaseGate.Check(new[] { Ok("A"), Ok("B") with { ModelState = "In Review" }, Ok("C") with { HasDrawing = false } });
        Assert.Equal(2, b.Count);
        var m = ReleaseGate.Message(b);
        Assert.Contains("B: model is In Review", m);
        Assert.Contains("C: no drawing", m);
        Assert.Contains("For quote only", m);
        Assert.DoesNotContain("A:", m);
    }

    [Fact]
    public void Quote_zip_name_adds_Quote()
    {
        Assert.Equal("CD-001882 Acme Machining CNC Quote Package.zip", PackageRules.PartnerZipName("CD-001882", "Acme Machining", ManufacturingMode.Cnc, true));
        Assert.Equal("CD-001882 Acme Machining Make Package.zip", PackageRules.PartnerZipName("CD-001882", "Acme Machining", ManufacturingMode.AllMake, false));
    }

    [Theory]
    [InlineData(false, false, LocalCopyAction.Refresh)]
    [InlineData(true, false, LocalCopyAction.KeepOpenCopy)]
    [InlineData(true, true, LocalCopyAction.KeepOpenCopy)]
    [InlineData(false, true, LocalCopyAction.KeepCheckedOutCopy)]
    public void Local_copy_refresh_decision(bool open, bool checkedOut, LocalCopyAction expected) =>
        Assert.Equal(expected, LocalCopy.Decide(open, checkedOut));

    [Fact]
    public void Local_edits_without_checkout_block()
    {
        var b = ReleaseGate.Check(new[] { Ok() with { ModelLocalEdits = true, DrawingLocalEdits = true } });
        Assert.Equal(new[] { "CD-1: model has local edits without checkout; check it out or discard the edits; drawing has local edits without checkout; check it out or discard the edits" }, b);
    }

    [Fact]
    public void Local_edits_detection_uses_two_second_slack()
    {
        var t = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(LocalCopy.HasLocalEdits(t.AddMinutes(5), t));
        Assert.False(LocalCopy.HasLocalEdits(t.AddSeconds(1), t));
        Assert.False(LocalCopy.HasLocalEdits(t.AddMinutes(-5), t));
    }

    [Theory]
    [InlineData(false, "Released", false, false, false, false)]
    [InlineData(false, "Work in Progress", true, true, true, false)]
    [InlineData(true, "Released", false, false, false, false)]
    [InlineData(true, "Work in Progress", false, false, false, true)]
    [InlineData(true, "Released", true, false, false, true)]
    [InlineData(true, "Released", false, true, false, true)]
    [InlineData(true, "Released", false, false, true, true)]
    public void Stamp_rule(bool quote, string state, bool checkedOut, bool dirty, bool older, bool expected) =>
        Assert.Equal(expected, PackageRules.StampDrawing(quote, state, checkedOut, dirty, older));

    [Fact]
    public void Message_caps_the_list_at_25_and_counts_the_rest()
    {
        var many = Enumerable.Range(1, 40).Select(i => $"P{i}: no drawing").ToList();
        var m = ReleaseGate.Message(many, copied: true);
        Assert.Contains("P25: no drawing", m);
        Assert.DoesNotContain("P26: no drawing", m);
        Assert.Contains("and 15 more", m);
        Assert.Contains("copied to the clipboard", m);
        Assert.Equal(40, ReleaseGate.FullList(many).Split((char)10).Length);
    }

    [Fact]
    public void Short_message_has_no_more_line_or_clipboard_note()
    {
        var m = ReleaseGate.Message(new[] { "A: no drawing" }, copied: true);
        Assert.DoesNotContain("more", m);
        Assert.DoesNotContain("clipboard", m);
    }
}
