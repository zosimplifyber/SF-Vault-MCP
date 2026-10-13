using SFVaultBridge.Core;

namespace SFVaultBridge.Core.Tests;

public class ReleasePlanTests
{
    private const string Root = @"C:\Vault\Designs\";

    private static BomNode N(string stem, string ext = ".ipt", string structure = "Normal", string material = "Steel", string process = "CNC",
                             params BomNode[] children) =>
        new(stem, stem + " desc", 1, "Each", structure, "", material, stem + ext, Root + stem + ext, children, Process: process);

    private static BomNode A(string stem, params BomNode[] children) => N(stem, ".iam", "Normal", "", "", children);

    private static BomNode Top(params BomNode[] _) => new("TOP", "Top", 1, "Each", "Normal", "", "", "TOP.iam", Root + "TOP.iam", Array.Empty<BomNode>());

    private static string? Cat(BomNode n) =>
        n.FileName.StartsWith("BUY", StringComparison.Ordinal) ? "Part - Purchased"
        : n.FileName.StartsWith("CC", StringComparison.Ordinal) ? "Part - Content Center"
        : n.FileName.EndsWith(".iam", StringComparison.OrdinalIgnoreCase) ? ReleasePlan.AssemblyCategory : ReleasePlan.PartCategory;

    private static string K(string file) => Root + file;

    private static IReadOnlyList<ReleaseModel> Models(params BomNode[] roots) =>
        ReleasePlan.Models(Top(), ReleasePlan.AssemblyCategory, roots, Cat);

    private static Dictionary<string, ReleaseDrawingRef?> DrawingsFor(IEnumerable<ReleaseModel> models) =>
        models.ToDictionary(m => m.Key, m => (ReleaseDrawingRef?)new ReleaseDrawingRef(K(Path.GetFileNameWithoutExtension(m.FileName) + ".idw"),
                                                                                         Path.GetFileNameWithoutExtension(m.FileName) + ".idw"), StringComparer.OrdinalIgnoreCase);

    private static ReleaseFacts Ok(ReleaseRowSpec s) => new(VaultPath: "$/Designs/" + s.FileName,
        Category: s.Kind == ReleaseKind.Drawing ? "Drawing" : s.Kind == ReleaseKind.Part ? ReleasePlan.PartCategory : ReleasePlan.AssemblyCategory,
        State: "Work in Progress", Revision: "A", Version: 3, Lifecycle: "Engineering", ChildCheck: false, RevisionAfter: "A (no change)",
        OtherChildren: Array.Empty<ReleaseChild>(), Compliance: "Compliant", Designer: "Jane Smith");

    private static IReadOnlyList<ReleaseRowSpec> Specs(params BomNode[] roots)
    {
        var models = Models(roots);
        return ReleasePlan.Order(models, DrawingsFor(models));
    }

    private static IReadOnlyList<ReleaseRow> Eval(IReadOnlyList<ReleaseRowSpec> specs, Func<ReleaseRowSpec, ReleaseFacts>? facts = null, string approver = "Zak O.") =>
        ReleasePlan.Evaluate(specs, facts ?? Ok, approver);

    private static ReleaseRow Row(IReadOnlyList<ReleaseRow> rows, string file) => rows.Single(r => r.Spec.FileName == file);

    // ---- Scope ----

    [Fact]
    public void Scope_excludes_purchased_content_center_phantom_reference_and_virtual()
    {
        var virt = new BomNode("VIRT", "", 1, "Each", "Normal", "", "", "", "", Array.Empty<BomNode>());
        var models = Models(N("P1"), N("BUY1"), N("CC1"), N("REF1", structure: "Reference"), N("PUR1", structure: "Purchased"), virt,
                            N("ccpath") with { FullPath = @"C:\Vault\Content Center Files\x.ipt", FileName = "x.ipt" });
        Assert.Equal(new[] { "P1.ipt", "TOP.iam" }, models.Select(m => m.FileName));
    }

    [Fact]
    public void Phantom_row_is_skipped_but_its_children_are_in_scope()
    {
        var models = Models(N("PH", ".iam", "Phantom", "", "", N("P1")));
        Assert.Equal(new[] { "P1.ipt", "TOP.iam" }, models.Select(m => m.FileName));
        Assert.Contains(K("P1.ipt"), models[^1].Below);
    }

    [Fact]
    public void Children_of_purchased_and_reference_assemblies_are_pruned()
    {
        var models = Models(N("BUYASM", ".iam", "Normal", "", "", N("P1")), N("REFASM", ".iam", "Reference", "", "", N("P2")));
        Assert.Equal(new[] { "TOP.iam" }, models.Select(m => m.FileName));
        Assert.Empty(models[0].Below);
    }

    [Fact]
    public void Order_is_bottom_up_with_each_file_once()
    {
        var models = Models(A("S1", N("P1"), A("S2", N("P2"), N("P1"))), N("P3"), N("P1"));
        var names = models.Select(m => m.FileName).ToList();
        Assert.Equal(names.Distinct().Count(), names.Count);
        Assert.True(names.IndexOf("P1.ipt") < names.IndexOf("S2.iam"));
        Assert.True(names.IndexOf("P2.ipt") < names.IndexOf("S2.iam"));
        Assert.True(names.IndexOf("S2.iam") < names.IndexOf("S1.iam"));
        Assert.Equal("TOP.iam", names[^1]);
        Assert.Equal(ReleaseKind.Top, models[^1].Kind);
        Assert.Equal(ReleaseKind.SubAssembly, models.Single(m => m.FileName == "S1.iam").Kind);
    }

    [Fact]
    public void Below_lists_all_levels_of_engineering_models()
    {
        var models = Models(A("S1", A("S2", N("P2"), N("BUY1")), N("P1")));
        var s1 = models.Single(m => m.FileName == "S1.iam");
        Assert.Equal(new[] { K("P1.ipt"), K("P2.ipt"), K("S2.iam") }.OrderBy(x => x), s1.Below.OrderBy(x => x));
        Assert.Equal(4, models[^1].Below.Count);
    }

    [Fact]
    public void Drawings_follow_their_model()
    {
        var specs = Specs(A("S1", N("P1")), N("P2"));
        Assert.Equal(new[] { "P1.ipt", "P1.idw", "S1.iam", "S1.idw", "P2.ipt", "P2.idw", "TOP.iam", "TOP.idw" }, specs.Select(s => s.FileName));
        var d = specs.Single(s => s.FileName == "P1.idw");
        Assert.Equal(ReleaseKind.Drawing, d.Kind);
        Assert.Equal(new[] { K("P1.ipt") }, d.Models);
        Assert.Equal("P1", d.PartNumber);
    }

    [Fact]
    public void Shared_drawing_comes_after_the_last_of_its_models()
    {
        var models = Models(N("P1"), N("P2"));
        var shared = new ReleaseDrawingRef(K("SHEET.idw"), "SHEET.idw");
        var drawings = new Dictionary<string, ReleaseDrawingRef?>(StringComparer.OrdinalIgnoreCase)
        {
            [K("P1.ipt")] = shared, [K("P2.ipt")] = shared, [K("TOP.iam")] = new ReleaseDrawingRef(K("TOP.idw"), "TOP.idw"),
        };
        var specs = ReleasePlan.Order(models, drawings);
        Assert.Equal(new[] { "P1.ipt", "P2.ipt", "SHEET.idw", "TOP.iam", "TOP.idw" }, specs.Select(s => s.FileName));
        Assert.Equal(2, specs.Single(s => s.FileName == "SHEET.idw").Models.Count);
    }

    [Fact]
    public void Missing_drawing_marks_the_model()
    {
        var models = Models(N("P1"));
        var specs = ReleasePlan.Order(models, new Dictionary<string, ReleaseDrawingRef?>(StringComparer.OrdinalIgnoreCase) { [K("P1.ipt")] = null });
        Assert.True(specs[0].NoDrawing);
        Assert.Equal(new[] { "P1.ipt", "TOP.iam" }, specs.Select(s => s.FileName));
        var rows = Eval(specs);
        Assert.Contains("no drawing", Row(rows, "P1.ipt").Blockers);
        Assert.Contains("no drawing", Row(rows, "TOP.iam").Blockers);
    }

    // ---- Blockers ----

    [Fact]
    public void Clean_tree_is_all_ready()
    {
        var rows = Eval(Specs(A("S1", N("P1"))));
        Assert.All(rows, r => Assert.Equal(ReleaseStatus.Ready, r.Status));
        Assert.Equal((6, 0, 0, 0, 0), ReleasePlan.Counts(rows));
    }

    [Theory]
    [InlineData("checkedout", "checked out by jsmith")]
    [InlineData("dirty", "open in Inventor with unsaved changes")]
    [InlineData("older", "open in Inventor at an older version; close it or Get Latest")]
    [InlineData("local", "local edits without checkout; check it out or discard the edits")]
    [InlineData("co", "controlled by a change order")]
    [InlineData("transition", "no transition from For Review to Released")]
    [InlineData("compliance", "Property Compliance is Non-Compliant")]
    [InlineData("missing", "not in Vault")]
    [InlineData("lifecycle", "could not read lifecycle state")]
    [InlineData("problem", "could not look up the drawing: boom")]
    [InlineData("category", "category is 'Part - Purchased', not Part - Engineering")]
    public void Every_blocker_skips_the_part(string which, string expected)
    {
        var specs = Specs(N("P1"));
        var rows = Eval(specs, s => s.FileName != "P1.ipt" ? Ok(s) : which switch
        {
            "checkedout" => Ok(s) with { CheckedOut = true, CheckedOutBy = "jsmith" },
            "dirty" => Ok(s) with { OpenDirty = true, OpenOlder = true },
            "older" => Ok(s) with { OpenOlder = true },
            "local" => Ok(s) with { LocalEdits = true },
            "co" => Ok(s) with { ControlledByChangeOrder = true },
            "transition" => Ok(s) with { TransitionProblem = "no transition from For Review to Released" },
            "compliance" => Ok(s) with { Compliance = "Non-Compliant" },
            "missing" => new ReleaseFacts(Found: false),
            "lifecycle" => Ok(s) with { LifecycleUnreadable = "could not read lifecycle state", Released = true },
            "problem" => Ok(s) with { Problem = "could not look up the drawing: boom" },
            _ => Ok(s) with { Category = "Part - Purchased" },
        });
        var p1 = Row(rows, "P1.ipt");
        Assert.Equal(ReleaseStatus.Blocked, p1.Status);
        Assert.Equal(new[] { expected }, p1.Blockers);
    }

    [Fact]
    public void Checked_out_without_a_name_says_someone() =>
        Assert.Contains("checked out by someone", ReleasePlan.Blockers(Specs(N("P1"))[0], Ok(Specs(N("P1"))[0]) with { CheckedOut = true }));

    [Theory]
    [InlineData("", "Material is blank")]
    [InlineData(" generic ", "Material is Generic")]
    public void Material_blank_or_generic_blocks_a_part(string material, string expected)
    {
        var rows = Eval(Specs(N("P1", material: material)));
        Assert.Equal(new[] { expected }, Row(rows, "P1.ipt").Blockers);
    }

    [Fact]
    public void Assemblies_and_drawings_need_no_material()
    {
        var rows = Eval(Specs(A("S1", N("P1"))));
        Assert.Equal(ReleaseStatus.Ready, Row(rows, "S1.iam").Status);
        Assert.Equal(ReleaseStatus.Ready, Row(rows, "S1.idw").Status);
    }

    [Fact]
    public void Assembly_category_must_be_assembly_engineering()
    {
        var rows = Eval(Specs(A("S1", N("P1"))), s => s.FileName == "S1.iam" ? Ok(s) with { Category = "" } : Ok(s));
        Assert.Equal(new[] { "category is blank, not Assembly - Engineering" }, Row(rows, "S1.iam").Blockers);
    }

    [Fact]
    public void Several_blockers_are_all_listed()
    {
        var specs = Specs(N("P1", material: ""));
        var b = ReleasePlan.Blockers(specs[0], Ok(specs[0]) with { CheckedOut = true, CheckedOutBy = "you", LocalEdits = true });
        Assert.Equal(new[] { "checked out by you", "local edits without checkout; check it out or discard the edits", "Material is blank" }, b);
    }

    [Theory]
    [InlineData("Compliant", true)]
    [InlineData(" compliant ", true)]
    [InlineData("Not Applicable", true)]
    [InlineData("Non-Compliant", false)]
    [InlineData("Pending", false)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Compliance_reading(string? value, bool? expected) => Assert.Equal(expected, ReleasePlan.IsCompliant(value));

    [Fact]
    public void Unreadable_compliance_is_a_warning_not_a_blocker()
    {
        var rows = Eval(Specs(N("P1")), s => Ok(s) with { Compliance = null });
        Assert.Equal(ReleaseStatus.Ready, Row(rows, "P1.ipt").Status);
        Assert.Contains("Property Compliance could not be read", Row(rows, "P1.ipt").Warnings);
    }

    // ---- Child check ----

    private static ReleaseFacts WithLibraryChild(ReleaseRowSpec s, bool childCheck, bool? childReleased) =>
        s.FileName == "TOP.iam" ? Ok(s) with { ChildCheck = childCheck, OtherChildren = new[] { new ReleaseChild("BUY1.ipt", childReleased) } } : Ok(s);

    [Fact]
    public void Child_check_on_and_unreleased_library_child_blocks_the_parent()
    {
        var rows = Eval(Specs(N("P1"), N("BUY1")), s => WithLibraryChild(s, true, false));
        var top = Row(rows, "TOP.iam");
        Assert.Equal(ReleaseStatus.Blocked, top.Status);
        Assert.Equal(new[] { "BUY1.ipt must be Released first (Vault's child check is on for this lifecycle)" }, top.Blockers);
        Assert.Equal(ReleaseStatus.HeldBack, Row(rows, "TOP.idw").Status);
    }

    [Fact]
    public void Child_check_on_and_library_child_in_released_state_passes()
    {
        var rows = Eval(Specs(N("P1"), N("BUY1")), s => WithLibraryChild(s, true, true));
        Assert.Equal(ReleaseStatus.Ready, Row(rows, "TOP.iam").Status);
    }

    [Fact]
    public void Child_check_off_ignores_unreleased_library_children()
    {
        var rows = Eval(Specs(N("P1"), N("BUY1")), s => WithLibraryChild(s, false, false));
        Assert.Equal(ReleaseStatus.Ready, Row(rows, "TOP.iam").Status);
    }

    [Fact]
    public void Child_check_on_and_unreadable_child_state_blocks()
    {
        var rows = Eval(Specs(N("P1")), s => WithLibraryChild(s, true, null));
        Assert.Equal(new[] { "could not read the state of BUY1.ipt (Vault's child check is on for this lifecycle)" }, Row(rows, "TOP.iam").Blockers);
    }

    [Fact]
    public void Many_unreleased_children_are_capped()
    {
        var specs = Specs(N("P1"));
        var kids = Enumerable.Range(1, 8).Select(i => new ReleaseChild($"BUY{i}.ipt", false)).ToList();
        var b = ReleasePlan.Blockers(specs[^2], Ok(specs[^2]) with { ChildCheck = true, OtherChildren = kids });
        Assert.Equal(ReleasePlan.MaxChildrenListed + 1, b.Count);
        Assert.Equal("and 3 more children must be Released first", b[^1]);
    }

    [Fact]
    public void Child_check_line_states_on_or_off_once()
    {
        Assert.StartsWith("Vault's child check is ON for Engineering:", ReleasePlan.ChildCheckLine(new[] { ("Engineering", (bool?)true), ("Engineering", (bool?)true) }));
        Assert.Equal("Vault's child check is off for Engineering.", ReleasePlan.ChildCheckLine(new[] { ("Engineering", (bool?)false) }));
        Assert.Equal("Vault's child check is ON for Basic and off for Engineering.", ReleasePlan.ChildCheckLine(new[] { ("Engineering", (bool?)false), ("Basic", (bool?)true) }));
        Assert.Equal("Vault's child check (children must be Released first): could not be read.", ReleasePlan.ChildCheckLine(new[] { ("Engineering", (bool?)null) }));
    }

    // ---- Already Released ----

    [Fact]
    public void Already_released_rows_need_no_action_and_count_as_released()
    {
        var rows = Eval(Specs(A("S1", N("P1"))), s => s.FileName.StartsWith("P1") ? Ok(s) with { Released = true, State = "Released", CheckedOut = true } : Ok(s));
        Assert.Equal(ReleaseStatus.AlreadyReleased, Row(rows, "P1.ipt").Status);
        Assert.Equal(ReleaseStatus.AlreadyReleased, Row(rows, "P1.idw").Status);
        Assert.Equal(ReleaseStatus.Ready, Row(rows, "S1.iam").Status);
        Assert.Equal((4, 0, 0, 0, 2), ReleasePlan.Counts(rows));
    }

    [Fact]
    public void Released_flag_on_a_custom_state_counts_as_released()
    {
        var rows = Eval(Specs(N("P1")), s => Ok(s) with { Released = true, State = "Approved" });
        Assert.All(rows, r => Assert.Equal(ReleaseStatus.AlreadyReleased, r.Status));
    }

    // ---- Hold-back ----

    [Fact]
    public void Blocked_part_holds_back_two_levels_of_assemblies_and_its_drawing()
    {
        var rows = Eval(Specs(A("S1", A("S2", N("P1")), N("P2"))), s => s.FileName == "P1.ipt" ? Ok(s) with { CheckedOut = true } : Ok(s));
        Assert.Equal(ReleaseStatus.Blocked, Row(rows, "P1.ipt").Status);
        Assert.Equal(ReleaseStatus.HeldBack, Row(rows, "P1.idw").Status);
        Assert.Equal("P1", Row(rows, "P1.idw").HeldBackBy);
        Assert.Equal(ReleaseStatus.HeldBack, Row(rows, "S2.iam").Status);
        Assert.Equal(ReleaseStatus.HeldBack, Row(rows, "S1.iam").Status);
        Assert.Equal(ReleaseStatus.HeldBack, Row(rows, "TOP.iam").Status);
        Assert.Equal(ReleaseStatus.HeldBack, Row(rows, "S2.idw").Status);
        Assert.Equal(ReleaseStatus.Ready, Row(rows, "P2.ipt").Status);
        Assert.Equal("held back: P1 and 3 more is not Released", ReleasePlan.PreviewText(Row(rows, "S1.iam")));
    }

    [Fact]
    public void Blocked_drawing_releases_its_model_but_holds_back_the_parents()
    {
        var rows = Eval(Specs(A("S1", N("P1"))), s => s.FileName == "P1.idw" ? Ok(s) with { OpenDirty = true } : Ok(s));
        Assert.Equal(ReleaseStatus.Blocked, Row(rows, "P1.idw").Status);
        Assert.Equal(ReleaseStatus.Ready, Row(rows, "P1.ipt").Status);
        Assert.Equal(ReleaseStatus.HeldBack, Row(rows, "S1.iam").Status);
        Assert.Equal("P1.idw", Row(rows, "S1.iam").HeldBackBy);
        Assert.Equal(ReleaseStatus.HeldBack, Row(rows, "TOP.iam").Status);
    }

    [Fact]
    public void Shared_drawing_placed_after_a_parent_does_not_deadlock_it()
    {
        var models = Models(A("S1", N("P1")));
        var shared = new ReleaseDrawingRef(K("SHEET.idw"), "SHEET.idw");
        var drawings = new Dictionary<string, ReleaseDrawingRef?>(StringComparer.OrdinalIgnoreCase)
        {
            [K("P1.ipt")] = shared, [K("S1.iam")] = shared, [K("TOP.iam")] = new ReleaseDrawingRef(K("TOP.idw"), "TOP.idw"),
        };
        var specs = ReleasePlan.Order(models, drawings);
        Assert.Equal(new[] { "P1.ipt", "S1.iam", "SHEET.idw", "TOP.iam", "TOP.idw" }, specs.Select(s => s.FileName));
        Assert.All(Eval(specs), r => Assert.Equal(ReleaseStatus.Ready, r.Status));
    }

    [Fact]
    public void Already_released_assembly_over_a_blocked_part_stays_released()
    {
        var rows = Eval(Specs(A("S1", N("P1"))), s => s.FileName == "P1.ipt" ? Ok(s) with { CheckedOut = true }
                                                   : s.FileName == "S1.iam" ? Ok(s) with { Released = true } : Ok(s));
        Assert.Equal(ReleaseStatus.AlreadyReleased, Row(rows, "S1.iam").Status);
        Assert.Equal(ReleaseStatus.HeldBack, Row(rows, "TOP.iam").Status);
    }

    [Fact]
    public void Unknown_dependency_counts_as_not_released() =>
        Assert.Equal("X", ReleasePlan.HeldBackBy(new ReleaseRowSpec("k", "a.iam", "A", ReleaseKind.SubAssembly, Array.Empty<string>(), new[] { "x" }, null, false),
                                                  new ReleaseFacts(), _ => null, false, _ => "X", _ => null));

    // ---- Vault dependencies that are not BOM rows ----

    [Fact]
    public void Unreleased_engineering_dependency_blocks_even_with_the_child_check_off()
    {
        var rows = Eval(Specs(N("P1")), s => s.FileName == "P1.ipt"
            ? Ok(s) with { ChildCheck = false, OtherChildren = new[] { new ReleaseChild("BASE.ipt", false, ReleasePlan.PartCategory), new ReleaseChild("CC1.ipt", false, "Part - Content Center") } }
            : Ok(s));
        Assert.Equal(new[] { "depends on BASE.ipt (not Released)" }, Row(rows, "P1.ipt").Blockers);
        Assert.Equal(ReleaseStatus.HeldBack, Row(rows, "TOP.iam").Status);
    }

    [Fact]
    public void Released_engineering_dependency_passes()
    {
        var rows = Eval(Specs(N("P1")), s => Ok(s) with { OtherChildren = new[] { new ReleaseChild("SKEL.ipt", true, ReleasePlan.PartCategory) } });
        Assert.All(rows, r => Assert.Equal(ReleaseStatus.Ready, r.Status));
    }

    [Fact]
    public void Row_dependency_later_in_the_order_holds_back()
    {
        var specs = Specs(N("P1"), N("P2"));
        var rows = Eval(specs, s => s.FileName == "P1.ipt" ? Ok(s) with { RowDependencies = new[] { K("P2.ipt") } } : Ok(s));
        Assert.Equal(ReleaseStatus.HeldBack, Row(rows, "P1.ipt").Status);
        Assert.Equal("P2", Row(rows, "P1.ipt").HeldBackBy);
        var rows2 = Eval(specs, s => s.FileName == "P2.ipt" ? Ok(s) with { RowDependencies = new[] { K("P1.ipt") } } : Ok(s));
        Assert.Equal(ReleaseStatus.Ready, Row(rows2, "P2.ipt").Status);
    }

    [Fact]
    public void Sync_properties_is_a_warning() =>
        Assert.Contains("the transition to Released syncs properties (Vault may add a version later)",
                        Row(Eval(Specs(N("P1")), s => Ok(s) with { SyncProps = true }), "P1.ipt").Warnings);

    // ---- Released drawing whose model changes ----

    private static ReleaseFacts ReleasedDrawing(ReleaseRowSpec s, string? reviseProblem = null) =>
        s.FileName == "P1.idw" ? Ok(s) with { Released = true, State = "Released", ReviseProblem = reviseProblem } : Ok(s);

    [Fact]
    public void Released_drawing_of_a_changing_model_is_revised()
    {
        var rows = Eval(Specs(N("P1")), s => ReleasedDrawing(s));
        Assert.Equal(ReleaseStatus.Revise, Row(rows, "P1.idw").Status);
        Assert.Equal("revise and re-release (model changes)", ReleasePlan.PreviewText(Row(rows, "P1.idw")));
        Assert.Equal(ReleaseStatus.Ready, Row(rows, "TOP.iam").Status);
        Assert.Equal((4, 1, 0, 0, 0), ReleasePlan.Counts(rows));
        Assert.Contains("1 Released drawing is revised and re-released", ReleasePlan.ConfirmText(rows, "Zak O."));
    }

    [Fact]
    public void Released_drawing_of_a_released_model_stays_released()
    {
        var rows = Eval(Specs(N("P1")), s => s.FileName.StartsWith("P1") ? Ok(s) with { Released = true } : Ok(s));
        Assert.Equal(ReleaseStatus.AlreadyReleased, Row(rows, "P1.idw").Status);
    }

    [Fact]
    public void Drawing_that_cannot_be_revised_blocks_the_models_parents()
    {
        var rows = Eval(Specs(N("P1")), s => ReleasedDrawing(s, "no transition from Released to Work in Progress"));
        Assert.Equal(ReleaseStatus.Blocked, Row(rows, "P1.idw").Status);
        Assert.Equal(new[] { "no transition from Released to Work in Progress" }, Row(rows, "P1.idw").Blockers);
        Assert.Equal(ReleaseStatus.Ready, Row(rows, "P1.ipt").Status);
        Assert.Equal(ReleaseStatus.HeldBack, Row(rows, "TOP.iam").Status);
    }

    [Fact]
    public void Revise_drawing_is_held_back_when_its_model_fails()
    {
        var rows = Eval(Specs(N("P1")), s => ReleasedDrawing(s));
        var w = new Writer { OnRelease = r => r.Spec.FileName == "P1.ipt" ? ReleaseOutcome.Fail("release", "no") : ReleaseOutcome.Done("B") };
        var run = RunAll(new ReleaseRun(rows, "Zak O.", w));
        var d = run.Results.Single(r => r.Row.Spec.FileName == "P1.idw");
        Assert.Equal(ReleaseStatus.HeldBack, d.Status);
        Assert.Equal("held back: P1 is not Released (the drawing stays Released at its current revision)", d.Detail);
        Assert.DoesNotContain("P1.idw/Zak O.", w.Calls);
    }

    [Fact]
    public void Failed_re_release_stops_later_revisions_and_holds_back_parents()
    {
        var rows = Eval(Specs(N("P1"), N("P2")), s => s.FileName is "P1.idw" or "P2.idw" ? Ok(s) with { Released = true } : Ok(s));
        var w = new Writer { OnRelease = r => r.Spec.FileName == "P1.idw" ? ReleaseOutcome.Fail(ReleasePlan.ReReleaseStep, "stuck") : ReleaseOutcome.Done("B") };
        var run = RunAll(new ReleaseRun(rows, "Zak O.", w));
        ReleaseResult R(string f) => run.Results.Single(r => r.Row.Spec.FileName == f);
        Assert.Equal(ReleaseStatus.Failed, R("P1.idw").Status);
        Assert.Equal(ReleaseStatus.NotAttempted, R("P2.idw").Status);
        Assert.Equal("stopped: an earlier drawing could not be returned to Released", R("P2.idw").Detail);
        Assert.DoesNotContain("P2.idw/Zak O.", w.Calls);
        Assert.Equal(ReleaseStatus.HeldBack, R("TOP.iam").Status);
    }

    [Fact]
    public void Drawing_failing_in_the_run_holds_back_the_parent()
    {
        var rows = Eval(Specs(N("P1")));
        var w = new Writer { OnRelease = r => r.Spec.FileName == "P1.idw" ? ReleaseOutcome.Fail("release", "no") : ReleaseOutcome.Done("A") };
        var run = RunAll(new ReleaseRun(rows, "Zak O.", w));
        Assert.Equal(ReleaseStatus.Released, run.Results[0].Status);
        Assert.Equal("held back: P1.idw is not Released", run.Results.Single(r => r.Row.Spec.FileName == "TOP.iam").Detail);
    }

    private static ReleaseOutcome Revise(List<string> log, string state = "Work in Progress", bool wipFails = false, bool signFails = false, bool backFails = false) =>
        ReleasePlan.ReviseAndRelease(
            () => { log.Add("wip"); if (wipFails) throw new InvalidOperationException("wip refused"); },
            () => state,
            () => { log.Add("sign"); if (signFails) throw new InvalidOperationException("save refused"); },
            () => { log.Add("released"); if (backFails) throw new InvalidOperationException("criteria"); return "B"; });

    [Fact]
    public void Revise_path_happy()
    {
        var log = new List<string>();
        Assert.Equal(ReleaseOutcome.Done("B"), Revise(log));
        Assert.Equal(new[] { "wip", "sign", "released" }, log);
    }

    [Fact]
    public void Revise_first_move_refused_changes_nothing()
    {
        var log = new List<string>();
        var o = Revise(log, state: "Released", wipFails: true);
        Assert.Equal("revise", o.Step);
        Assert.Equal(new[] { "wip" }, log);
    }

    [Fact]
    public void Revise_first_move_that_committed_is_brought_back()
    {
        var log = new List<string>();
        var o = Revise(log, wipFails: true);
        Assert.Equal("sign off", o.Step);
        Assert.Equal(new[] { "wip", "released" }, log);
        Assert.Contains("returned to Released, revision B", o.Message);
    }

    [Fact]
    public void Revise_failed_sign_off_still_returns_to_released()
    {
        var log = new List<string>();
        var o = Revise(log, signFails: true);
        Assert.Equal("sign off", o.Step);
        Assert.Equal(new[] { "wip", "sign", "released" }, log);
    }

    [Fact]
    public void Revise_failed_return_is_re_release()
    {
        var o = Revise(new List<string>(), backFails: true);
        Assert.Equal(ReleasePlan.ReReleaseStep, o.Step);
        Assert.Contains("It is in Work in Progress; change it to Released in Vault by hand.", o.Message);
    }

    [Fact]
    public void Revise_lost_session_on_first_move_propagates() =>
        Assert.Throws<BridgeException>(() => ReleasePlan.ReviseAndRelease(() => throw new BridgeException("not_signed_in", "x"), () => "", () => { }, () => ""));

    // ---- Warnings and approver ----

    [Theory]
    [InlineData("Zak Olech", true)]
    [InlineData("zak o.", true)]
    [InlineData("Zak Smith", false)]
    [InlineData("Jane Smith", false)]
    [InlineData("", false)]
    public void Self_approval(string designer, bool expected) => Assert.Equal(expected, ReleasePlan.SelfApproval("Zak O.", designer));

    [Fact]
    public void Approver_name_is_first_and_last_initial() => Assert.Equal("Zak O.", ReleasePlan.ApproverName("Zak Olech"));

    [Fact]
    public void Warnings_for_self_approval_blank_process_and_blank_designer()
    {
        var rows = Eval(Specs(N("P1", process: ""), N("P2")), s => s.FileName == "P1.ipt" ? Ok(s) with { Designer = "Zak Olech" }
                                                                    : s.FileName == "P2.ipt" ? Ok(s) with { Designer = " " } : Ok(s));
        Assert.Equal(new[] { "you are this file's Designer (self-approval)", "Process is blank" }, Row(rows, "P1.ipt").Warnings);
        Assert.Equal(new[] { "Designer is blank" }, Row(rows, "P2.ipt").Warnings);
        Assert.Equal(ReleaseStatus.Ready, Row(rows, "P1.ipt").Status);
    }

    [Fact]
    public void Unreadable_designer_gives_no_warning()
    {
        var rows = Eval(Specs(N("P1")), s => Ok(s) with { Designer = null });
        Assert.Empty(Row(rows, "P1.ipt").Warnings);
    }

    // ---- Texts ----

    [Fact]
    public void Confirm_text_counts()
    {
        var rows = Eval(Specs(A("S1", N("P1")), N("P2")), s => s.FileName == "P1.ipt" ? Ok(s) with { CheckedOut = true } : Ok(s));
        Assert.StartsWith("Release 2 files as Zak O.? 1 will be skipped, 5 held back.", ReleasePlan.ConfirmText(rows, "Zak O."));
    }

    [Theory]
    [InlineData(RevisionBump.None, "A", null, "A (no change)")]
    [InlineData(RevisionBump.None, "", null, "(no change)")]
    [InlineData(RevisionBump.Primary, "A", new[] { "B", "A.1", "A.0.1" }, "B")]
    [InlineData(RevisionBump.Secondary, "A", new[] { "B", "A.1", "A.0.1" }, "A.1")]
    [InlineData(RevisionBump.Primary, "A", null, "Vault decides")]
    [InlineData(RevisionBump.Tertiary, "A", new[] { "B" }, "Vault decides")]
    [InlineData(RevisionBump.Unknown, "A", new[] { "B" }, "Vault decides")]
    public void Revision_after(RevisionBump bump, string current, string[]? next, string expected) =>
        Assert.Equal(expected, ReleasePlan.RevisionAfter(bump, current, next));

    [Fact]
    public void After_release_builds_only_when_the_top_is_released()
    {
        var rows = Eval(Specs(N("P1")));
        var top = rows.Single(r => r.Spec.Kind == ReleaseKind.Top);
        Assert.True(ReleasePlan.AfterRelease(new[] { new ReleaseResult(top, ReleaseStatus.Released, "", "A") }).Build);
        Assert.True(ReleasePlan.AfterRelease(new[] { new ReleaseResult(top, ReleaseStatus.AlreadyReleased, "") }).Build);
        var no = ReleasePlan.AfterRelease(new[] { new ReleaseResult(top, ReleaseStatus.HeldBack, "held back: P1 is not Released") });
        Assert.False(no.Build);
        Assert.Contains("TOP.iam is not Released (held back: P1 is not Released)", no.Message);
    }

    [Fact]
    public void Results_text_has_a_row_per_file()
    {
        var rows = Eval(Specs(N("P1")));
        var text = ReleasePlan.ResultsText(rows.Select(r => new ReleaseResult(r, ReleaseStatus.Released, "", "B")));
        Assert.StartsWith("File\tKind\tResult\tRevision before\tRevision after\r\n", text);
        Assert.Contains("P1.ipt\tPart\treleased (A -> B)\tA\tB", text);
        Assert.Equal(6, text.Split("\r\n").Length); // header, four rows, trailing newline
    }

    [Fact]
    public void Preview_rows_copy_as_ready_skipped_or_held_back()
    {
        var rows = Eval(Specs(A("S1", N("P1"))), s => s.FileName == "P1.ipt" ? Ok(s) with { OpenDirty = true } : Ok(s));
        Assert.Equal("skipped: open in Inventor with unsaved changes", ReleasePlan.ResultLine(new ReleaseResult(rows[0], rows[0].Status, string.Join("; ", rows[0].Blockers))));
        var ready = Eval(Specs(N("P2")))[0];
        Assert.Equal("Ready", ReleasePlan.ResultLine(new ReleaseResult(ready, ready.Status, "")));
    }

    [Fact]
    public void Open_document_save_failure_names_the_button() =>
        Assert.Equal("Inventor kept the open file read-only; close it and click I Approve! again. (denied; the checkout was undone)",
                     ReleasePlan.OpenDocumentSaveFailure("denied", "the checkout was undone"));

    // ---- Host rules ----

    private static DataCardFileInfo Info(string name, string state = "Work in Progress", bool released = false) =>
        new("$/Designs/" + name, name, ReleasePlan.PartCategory, state, released, false, "", 3);

    [Theory]
    [InlineData("a.ipt")]
    [InlineData("a.iam")]
    [InlineData("a.idw")]
    [InlineData("a.DWG")]
    public void Sign_off_allows_parts_assemblies_and_drawings(string name) => Assert.Null(ReleasePlan.SignOffSkipReason(Info(name)));

    [Fact]
    public void Sign_off_keeps_the_fix_data_card_rules()
    {
        Assert.Equal("a.pdf is not an Inventor part, assembly or drawing.", ReleasePlan.SignOffSkipReason(Info("a.pdf")));
        Assert.Equal("x", ReleasePlan.SignOffSkipReason(Info("a.ipt") with { Unreadable = "x" }));
        Assert.StartsWith("a.ipt is checked out by jo.", ReleasePlan.SignOffSkipReason(Info("a.ipt") with { CheckedOut = true, CheckedOutBy = "jo" }));
        Assert.Equal("a.ipt is a Content Center file.", ReleasePlan.SignOffSkipReason(Info("a.ipt") with { Category = "Part - Content Center" }));
        Assert.Equal("a.ipt is a Content Center file.", ReleasePlan.SignOffSkipReason(Info("a.ipt") with { VaultPath = "$/Content Center Files/a.ipt" }));
        Assert.Equal("a.ipt is already Released.", ReleasePlan.SignOffSkipReason(Info("a.ipt", "Released")));
        Assert.Equal("a.ipt is already Released.", ReleasePlan.SignOffSkipReason(Info("a.ipt", "Approved", true)));
        // fix_data_card itself still refuses assemblies.
        Assert.Equal("assemblies are not supported yet", DataCardFix.SkipReason(Info("a.iam")));
    }

    private static readonly ChildVersion[] Versions = { new(10, 100, 5), new(11, 200, 6), new(12, 300, 6) };

    private static (IReadOnlyList<AssocRow> Rows, IReadOnlyList<string> Problems) Choose(ChildOnDisk? child)
    {
        var rows = new[] { new AssocRow(1, 10, "Dependency", "r1", "s", "$/a.ipt"), new AssocRow(1, 30, "Attachment", null, null, null) };
        return ReleasePlan.ChooseChildVersions(rows, id => id == 10 ? child : null);
    }

    [Fact]
    public void Signed_child_is_referenced_at_its_latest_version()
    {
        // Referenced checksum 100, signed and released content 300: the old checksum rule would have kept version 10.
        var (rows, problems) = Choose(new ChildOnDisk("a.ipt", true, 12, Versions, 999, 6));
        Assert.Empty(problems);
        Assert.Equal(new long[] { 12, 30 }, rows.Select(r => r.CldFileId));
        Assert.Equal("r1", rows[0].RefId);
    }

    [Fact]
    public void Untouched_child_is_referenced_at_the_version_on_disk()
    {
        var (rows, problems) = Choose(new ChildOnDisk("a.ipt", false, 12, Versions, 200, 6));
        Assert.Empty(problems);
        Assert.Equal(11, rows[0].CldFileId);
    }

    [Fact]
    public void Untouched_child_byte_identical_to_latest_uses_latest_when_no_checksum_matches()
    {
        var asked = 0;
        var (rows, problems) = Choose(new ChildOnDisk("a.ipt", false, 12, Versions, 777, 6, () => { asked++; return true; }));
        Assert.Empty(problems);
        Assert.Equal(12, rows[0].CldFileId);
        Assert.Equal(1, asked);
    }

    [Fact]
    public void Untouched_child_matching_no_version_fails_the_parent()
    {
        var (_, problems) = Choose(new ChildOnDisk("a.ipt", false, 12, Versions, 777, 6, () => false));
        Assert.Equal(new[] { "a.ipt on disk matches no Vault version (local edits or another copy)" }, problems);
    }

    [Fact]
    public void Checksum_match_with_another_size_does_not_count()
    {
        var (_, problems) = Choose(new ChildOnDisk("a.ipt", false, 12, Versions, 200, 99));
        Assert.Single(problems);
    }

    [Fact]
    public void Missing_local_child_or_unreadable_child_fails_the_parent()
    {
        Assert.Equal(new[] { "a.ipt is not in the workspace" }, Choose(new ChildOnDisk("a.ipt", false, 12, Versions, null, null)).Problems);
        Assert.Equal(new[] { "could not read child file 10 from Vault" }, Choose(null).Problems);
    }

    [Fact]
    public void Crc32_matches_the_standard_check_value()
    {
        using var ms = new MemoryStream(System.Text.Encoding.ASCII.GetBytes("123456789"));
        Assert.Equal(unchecked((int)0xCBF43926u), ReleasePlan.Crc32(ms));
    }

    [Fact]
    public void Sign_off_comment_names_the_vault_user()
    {
        Assert.Equal("Approved by Zak O. (Vault user zolech)", ReleasePlan.SignOffComment("Zak O.", "zolech"));
        Assert.Equal("Approved by Zak O.", ReleasePlan.SignOffComment("Zak O.", " "));
    }

    // ---- Run ----

    private sealed class Writer : IReleaseWriter
    {
        public readonly List<string> Calls = new();
        public Func<ReleaseRow, ReleaseOutcome> OnRelease = _ => ReleaseOutcome.Done("A");
        public Func<IReadOnlyList<string>> OnLatest = () => Array.Empty<string>();
        public IReadOnlyList<string> GetLatest() { Calls.Add("latest"); return OnLatest(); }
        public ReleaseOutcome Release(ReleaseRow row, string approver) { Calls.Add(row.Spec.FileName + "/" + approver); return OnRelease(row); }
    }

    private static ReleaseRun RunAll(ReleaseRun run)
    {
        var guard = 0;
        while (run.Step() is not null && guard++ < 1000) { }
        return run;
    }

    [Fact]
    public void Run_gets_latest_first_then_releases_ready_rows_bottom_up()
    {
        var rows = Eval(Specs(A("S1", N("P1"))));
        var w = new Writer();
        var run = RunAll(new ReleaseRun(rows, "Zak O.", w));
        Assert.Equal(new[] { "latest", "P1.ipt/Zak O.", "P1.idw/Zak O.", "S1.iam/Zak O.", "S1.idw/Zak O.", "TOP.iam/Zak O.", "TOP.idw/Zak O." }, w.Calls);
        Assert.All(run.Results, r => Assert.Equal(ReleaseStatus.Released, r.Status));
        Assert.Null(run.StoppedBecause);
        Assert.True(ReleasePlan.AfterRelease(run.Results).Build);
    }

    [Fact]
    public void Failed_part_holds_back_its_drawing_and_two_levels_of_parents()
    {
        var rows = Eval(Specs(A("S1", A("S2", N("P1"))), N("P2")));
        var w = new Writer { OnRelease = r => r.Spec.FileName == "P1.ipt" ? ReleaseOutcome.Fail("release", "Vault refused") : ReleaseOutcome.Done("A") };
        var run = RunAll(new ReleaseRun(rows, "Zak O.", w));
        ReleaseResult R(string f) => run.Results.Single(r => r.Row.Spec.FileName == f);
        Assert.Equal(ReleaseStatus.Failed, R("P1.ipt").Status);
        Assert.Equal("release: Vault refused", R("P1.ipt").Detail);
        Assert.Equal(ReleaseStatus.HeldBack, R("P1.idw").Status);
        Assert.Equal(ReleaseStatus.HeldBack, R("S2.iam").Status);
        Assert.Equal("held back: P1 and 1 more is not Released", R("S2.iam").Detail);
        Assert.Equal(ReleaseStatus.HeldBack, R("S1.iam").Status);
        Assert.Equal(ReleaseStatus.HeldBack, R("TOP.iam").Status);
        Assert.Equal(ReleaseStatus.Released, R("P2.ipt").Status);
        Assert.Equal(ReleaseStatus.HeldBack, R("S2.idw").Status);
        Assert.DoesNotContain("S2.iam/Zak O.", w.Calls);
        Assert.DoesNotContain("TOP.iam/Zak O.", w.Calls);
        Assert.False(ReleasePlan.AfterRelease(run.Results).Build);
    }

    [Fact]
    public void Blocked_and_already_released_rows_are_never_sent_to_the_writer()
    {
        var rows = Eval(Specs(N("P1"), N("P2")), s => s.FileName == "P1.ipt" ? Ok(s) with { Released = true }
                                                  : s.FileName == "P2.idw" ? Ok(s) with { CheckedOut = true } : Ok(s));
        var w = new Writer();
        var run = RunAll(new ReleaseRun(rows, "Zak O.", w));
        Assert.Equal(new[] { "latest", "P1.idw/Zak O.", "P2.ipt/Zak O." }, w.Calls); // the blocked P2 drawing holds back the top
        Assert.Equal(ReleaseStatus.Blocked, run.Results.Single(r => r.Row.Spec.FileName == "P2.idw").Status);
        Assert.Equal("skipped: checked out by someone", ReleasePlan.ResultLine(run.Results.Single(r => r.Row.Spec.FileName == "P2.idw")));
    }

    [Fact]
    public void Stop_between_files_leaves_the_rest_not_attempted()
    {
        var rows = Eval(Specs(N("P1"), N("P2")));
        var w = new Writer();
        var run = new ReleaseRun(rows, "Zak O.", w);
        Assert.Equal(-1, run.Step());
        Assert.Equal(0, run.Step());
        run.RequestStop();
        Assert.Null(run.Step());
        Assert.True(run.Done);
        Assert.Equal("stopped", run.StoppedBecause);
        Assert.Equal(new[] { "latest", "P1.ipt/Zak O." }, w.Calls);
        Assert.All(run.Results.Skip(1), r => Assert.Equal(ReleaseStatus.NotAttempted, r.Status));
        Assert.Equal("not attempted: stopped before this file", ReleasePlan.ResultLine(run.Results[1]));
    }

    [Fact]
    public void Session_loss_stops_the_run()
    {
        var rows = Eval(Specs(N("P1"), N("P2")));
        var w = new Writer { OnRelease = r => r.Spec.FileName == "P1.idw" ? throw new BridgeException("not_signed_in", "Sign in to Vault.") : ReleaseOutcome.Done("A") };
        var run = RunAll(new ReleaseRun(rows, "Zak O.", w));
        Assert.Equal("signed out of Vault", run.StoppedBecause);
        Assert.Equal(new[] { "latest", "P1.ipt/Zak O.", "P1.idw/Zak O." }, w.Calls);
        Assert.Equal(ReleaseStatus.Failed, run.Results[1].Status);
        Assert.All(run.Results.Skip(2), r => Assert.Equal(ReleaseStatus.NotAttempted, r.Status));
    }

    [Fact]
    public void Get_latest_failure_releases_nothing()
    {
        var rows = Eval(Specs(N("P1")));
        var w = new Writer { OnLatest = () => throw new BridgeException("download_failed", "disk full") };
        var run = RunAll(new ReleaseRun(rows, "Zak O.", w));
        Assert.Equal(new[] { "latest" }, w.Calls);
        Assert.Equal("Get Latest failed: disk full", run.StoppedBecause);
        Assert.All(run.Results, r => Assert.Equal(ReleaseStatus.NotAttempted, r.Status));
    }

    [Fact]
    public void Writer_exception_fails_only_that_row()
    {
        var rows = Eval(Specs(N("P1"), N("P2")));
        var w = new Writer { OnRelease = r => r.Spec.FileName == "P1.idw" ? throw new InvalidOperationException("boom") : ReleaseOutcome.Done("A") };
        var run = RunAll(new ReleaseRun(rows, "Zak O.", w));
        Assert.Equal("unexpected: boom", run.Results[1].Detail);
        Assert.Equal(ReleaseStatus.Released, run.Results.Single(r => r.Row.Spec.FileName == "P2.ipt").Status);
        Assert.Equal(ReleaseStatus.HeldBack, run.Results.Single(r => r.Row.Spec.FileName == "TOP.iam").Status);
    }

    [Fact]
    public void Preview_held_back_rows_are_never_released()
    {
        var rows = Eval(Specs(A("S1", N("P1"))), s => s.FileName == "P1.ipt" ? Ok(s) with { CheckedOut = true } : Ok(s));
        var w = new Writer();
        var run = RunAll(new ReleaseRun(rows, "Zak O.", w));
        Assert.Equal(new[] { "latest" }, w.Calls);
        Assert.Equal("held back: P1 and 1 more is not Released", run.Results.Single(r => r.Row.Spec.FileName == "S1.iam").Detail);
        Assert.Equal("0 released, 0 already Released, 1 skipped, 5 held back, 0 failed, 0 not attempted.", ReleasePlan.Summary(run.Results));
    }
}
