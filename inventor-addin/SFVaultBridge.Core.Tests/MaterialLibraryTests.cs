using System.Text.Json.Nodes;
using SFVaultBridge.Core;

namespace SFVaultBridge.Core.Tests;

// add_library_material: value validation and unit conversion, skip rules, the run (preview and apply, released libraries), the MCP tools.
public class MaterialLibraryTests
{
    private const string NylonJson = """
    {
      "name": "Nylon 12 (PA12, MJF)",
      "description": "HP MJF PA12, printed by ABC Corp",
      "material_class": "Plastic",
      "density": { "value": 1.01, "unit": "g/cm3" },
      "youngs_modulus": { "value": 1800, "unit": "MPa" },
      "poissons_ratio": { "value": 0.40, "unit": "" },
      "yield_strength": { "value": 48, "unit": "MPa" },
      "tensile_strength": { "value": 48, "unit": "MPa" },
      "elongation": { "value": 20, "unit": "%" },
      "thermal_expansion": { "value": 1.0e-4, "unit": "1/C" },
      "thermal_conductivity": { "value": 0.2, "unit": "W/(m K)" }
    }
    """;

    private const string LibPath = "$/VAULT STANDARDS/DESIGN DATA/Materials/SF Materials.adsklib";

    private static JsonObject Obj(string json) => (JsonObject)JsonNode.Parse(json)!;

    private static MaterialSpec Nylon() => MaterialRules.ParseSpec(Obj(NylonJson));

    private static MaterialValue V(MaterialSpec s, string key) => s.Values.Single(v => v.Key == key);

    // ---- values and units ----

    [Fact]
    public void Nylon_values_are_converted_to_the_units_Inventor_stores()
    {
        var s = Nylon();
        Assert.Equal("Nylon 12 (PA12, MJF)", s.Name);
        Assert.Equal("HP MJF PA12, printed by ABC Corp", s.Description);
        Assert.Equal("Plastic", s.MaterialClass);
        Assert.Equal(1010.0, V(s, "density").AssetValue, 9);
        Assert.Equal("structural_Density", V(s, "density").AssetName);
        Assert.Equal(1800000.0, V(s, "youngs_modulus").AssetValue, 6);                 // kPa
        Assert.Equal("structural_Young_modulus", V(s, "youngs_modulus").AssetName);
        Assert.Equal(0.4, V(s, "poissons_ratio").AssetValue, 12);
        Assert.Equal(48000.0, V(s, "yield_strength").AssetValue, 6);
        Assert.Equal("structural_Minimum_yield_stress", V(s, "yield_strength").AssetName);
        Assert.Equal(48000.0, V(s, "tensile_strength").AssetValue, 6);
        Assert.Equal("structural_Minimum_tensile_strength", V(s, "tensile_strength").AssetName);
        Assert.Equal(100.0, V(s, "thermal_expansion").AssetValue, 9);                   // um/m/C
        Assert.Equal("structural_Thermal_expansion_coefficient", V(s, "thermal_expansion").AssetName);
        Assert.Equal(0.2, V(s, "thermal_conductivity").AssetValue, 12);
        Assert.Equal("structural_Thermal_conductivity", V(s, "thermal_conductivity").AssetName);
        Assert.DoesNotContain(s.Values, v => v.Key == "specific_heat");
    }

    [Fact]
    public void Elongation_is_reported_as_not_stored()
    {
        var s = Nylon();
        Assert.Single(s.NotStored);
        Assert.Contains("elongation 20 %", s.NotStored[0]);
    }

    [Fact]
    public void The_sample_values_file_parses()
    {
        var dir = AppContext.BaseDirectory;
        string? file = null;
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "scripts", "materials", "nylon12_pa12_mjf.json");
            if (File.Exists(p)) { file = p; break; }
        }
        Assert.NotNull(file);
        var s = MaterialRules.ParseSpec(Obj(File.ReadAllText(file!)));
        Assert.Equal("Nylon 12 (PA12, MJF)", s.Name);
        Assert.Equal(1010.0, V(s, "density").AssetValue, 9);
    }

    [Theory]
    [InlineData(1.01, "g/cm3", 1010.0)]
    [InlineData(1010, "kg/m3", 1010.0)]
    [InlineData(1.01, "g/cm^3", 1010.0)]
    [InlineData(1.01, "kg/L", 1010.0)]
    [InlineData(0.0364, "lb/in3", 1007.5)]
    public void Density_units(double v, string unit, double expectedKgM3)
    {
        var s = MaterialRules.ParseSpec(Obj($$"""{"name":"x","density":{"value":{{v.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"unit":"{{unit}}"},"youngs_modulus":"1 GPa","poissons_ratio":0.3}"""));
        Assert.Equal(expectedKgM3, V(s, "density").AssetValue, 0);
    }

    [Theory]
    [InlineData("1800 MPa", 1800000.0)]
    [InlineData("1.8 GPa", 1800000.0)]
    [InlineData("1800000 kPa", 1800000.0)]
    [InlineData("1800000000 Pa", 1800000.0)]
    [InlineData("261000 psi", 1799532.0)]
    [InlineData("261 ksi", 1799532.0)]
    public void Stress_units_become_kPa(string given, double expectedKpa)
    {
        var s = MaterialRules.ParseSpec(Obj($$"""{"name":"x","density":"1 g/cm3","youngs_modulus":"{{given}}","poissons_ratio":0.3}"""));
        Assert.InRange(V(s, "youngs_modulus").AssetValue, expectedKpa - 5, expectedKpa + 5);
    }

    [Theory]
    [InlineData("1.0e-4 1/C", 100.0)]
    [InlineData("1.0e-4 1/K", 100.0)]
    [InlineData("100 um/m/C", 100.0)]
    [InlineData("100 ppm/K", 100.0)]
    [InlineData("5.5556e-5 1/F", 100.0)]
    public void Expansion_units_become_um_per_m_C(string given, double expected)
    {
        var s = MaterialRules.ParseSpec(Obj($$"""{"name":"x","density":"1 g/cm3","youngs_modulus":"1 GPa","poissons_ratio":0.3,"thermal_expansion":"{{given}}"}"""));
        Assert.Equal(expected, V(s, "thermal_expansion").AssetValue, 2);
    }

    [Theory]
    [InlineData("0.2 W/(m K)", 0.2)]
    [InlineData("0.2 W/mK", 0.2)]
    [InlineData("0.2 W/(m*C)", 0.2)]
    [InlineData("0.2 W/(m\u00b0C)", 0.2)]
    public void Conductivity_units(string given, double expected)
    {
        var s = MaterialRules.ParseSpec(Obj($$"""{"name":"x","density":"1 g/cm3","youngs_modulus":"1 GPa","poissons_ratio":0.3,"thermal_conductivity":"{{given}}"}"""));
        Assert.Equal(expected, V(s, "thermal_conductivity").AssetValue, 9);
    }

    [Theory]
    [InlineData("1.7 J/(g C)", 1.7)]
    [InlineData("1700 J/(kg K)", 1.7)]
    [InlineData("1.7 kJ/(kg K)", 1.7)]
    public void Specific_heat_units(string given, double expected)
    {
        var s = MaterialRules.ParseSpec(Obj($$"""{"name":"x","density":"1 g/cm3","youngs_modulus":"1 GPa","poissons_ratio":0.3,"specific_heat":"{{given}}"}"""));
        Assert.Equal(expected, V(s, "specific_heat").AssetValue, 9);
    }

    [Fact]
    public void Text_form_and_name_override()
    {
        var s = MaterialRules.ParseSpec(Obj("""{"name":"old","density":"1.01 g/cm3","youngs_modulus":"1800 MPa","poissons_ratio":"0.4"}"""), "New name");
        Assert.Equal("New name", s.Name);
        Assert.Equal(0.4, V(s, "poissons_ratio").AssetValue, 12);
        Assert.Equal("1.01 g/cm3", V(s, "density").Given);
    }

    [Theory]
    [InlineData("""{"name":"x","density":1.01,"youngs_modulus":"1 GPa","poissons_ratio":0.3}""", "density: give a unit")]
    [InlineData("""{"name":"x","density":"1.01 furlongs","youngs_modulus":"1 GPa","poissons_ratio":0.3}""", "unit 'furlongs'")]
    [InlineData("""{"name":"x","density":"1 MPa","youngs_modulus":"1 GPa","poissons_ratio":0.3}""", "unit 'MPa'")]
    [InlineData("""{"name":"x","density":"1 g/cm3","youngs_modulus":"1 GPa"}""", "poissons_ratio is required")]
    [InlineData("""{"name":"x","youngs_modulus":"1 GPa","poissons_ratio":0.3}""", "density is required")]
    [InlineData("""{"density":"1 g/cm3","youngs_modulus":"1 GPa","poissons_ratio":0.3}""", "material name is required")]
    [InlineData("""{"name":"x","density":"1 g/cm3","youngs_modulus":"1 GPa","poissons_ratio":0.7}""", "poissons_ratio")]
    [InlineData("""{"name":"x","density":"30 g/cm3","youngs_modulus":"1 GPa","poissons_ratio":0.3}""", "outside the allowed")]
    [InlineData("""{"name":"x","density":"1 g/cm3","youngs_modulus":"1 GPa","poissons_ratio":0.3,"colour":"red"}""", "Unknown material value 'colour'")]
    [InlineData("""{"name":"x","material_class":"Cheese","density":"1 g/cm3","youngs_modulus":"1 GPa","poissons_ratio":0.3}""", "material_class 'Cheese'")]
    [InlineData("""{"name":"x","density":"abc","youngs_modulus":"1 GPa","poissons_ratio":0.3}""", "not a number with a unit")]
    [InlineData("""{"name":"x","density":{"value":"a","unit":"g/cm3"},"youngs_modulus":"1 GPa","poissons_ratio":0.3}""", "density.value must be a number")]
    [InlineData("""{"name":"x","density":"1 g/cm3","youngs_modulus":"1 GPa","poissons_ratio":0.3,"elongation":"5000 %"}""", "elongation")]
    [InlineData("""{"name":"x","density":"1 g/cm3","youngs_modulus":"1 GPa","poissons_ratio":0.3,"elongation":5}""", "elongation: give a unit")]
    public void Bad_values_are_rejected_naming_the_field(string json, string fragment)
    {
        var ex = Assert.Throws<BridgeException>(() => MaterialRules.ParseSpec(Obj(json)));
        Assert.Equal("bad_request", ex.Code);
        Assert.Contains(fragment, ex.Message);
    }

    [Fact]
    public void Name_rules()
    {
        Assert.Throws<BridgeException>(() => MaterialRules.ParseSpec(Obj("""{"name":"a\u0001b","density":"1 g/cm3","youngs_modulus":"1 GPa","poissons_ratio":0.3}""")));
        var longName = new string('a', 101);
        Assert.Throws<BridgeException>(() => MaterialRules.ParseSpec(Obj($$"""{"name":"{{longName}}","density":"1 g/cm3","youngs_modulus":"1 GPa","poissons_ratio":0.3}""")));
    }

    [Fact]
    public void Every_material_class_has_a_choice_id_and_the_schema_names_are_unique()
    {
        Assert.Equal("physmat_plastic", MaterialRules.MaterialClasses["plastic"]);
        Assert.Equal(14, MaterialRules.MaterialClasses.Count);
        Assert.Equal(MaterialRules.Properties.Count, MaterialRules.Properties.Select(p => p.AssetName).Distinct().Count());
        Assert.All(MaterialRules.Properties, p => Assert.StartsWith("structural_", p.AssetName));
    }

    [Fact]
    public void Unit_conversion_round_trips_and_refuses_the_wrong_quantity()
    {
        Assert.True(MaterialUnits.TryConvert(MaterialQuantity.Stress, 48, "MPa", "Kilopascal", out var kpa));
        Assert.Equal(48000.0, kpa, 6);
        Assert.True(MaterialUnits.TryConvert(MaterialQuantity.Stress, kpa, "Kilopascal", "MPa", out var back));
        Assert.Equal(48.0, back, 9);
        Assert.False(MaterialUnits.TryConvert(MaterialQuantity.Stress, 1, "g/cm3", "kPa", out _));
        Assert.Equal("w/mk", MaterialUnits.Normalize("W/(m K)"));
        Assert.Equal("um/mc", MaterialUnits.Normalize("\u00b5m/(m \u00b0C)"));
    }

    // ---- skip rules and small pure pieces ----

    private static DataCardFileInfo Lib(string state = "Work in Progress", bool released = false, bool checkedOut = false, string by = "", string name = "SF Materials.adsklib") =>
        new($"$/VAULT STANDARDS/DESIGN DATA/Materials/{name}", name, "Library", state, released, checkedOut, by, 7);

    [Fact]
    public void Skip_reasons()
    {
        Assert.Null(MaterialRules.SkipReason(Lib(), false));
        Assert.Contains("not an .adsklib", MaterialRules.SkipReason(Lib(name: "x.ipt"), false));
        Assert.Contains("checked out by Dana", MaterialRules.SkipReason(Lib(checkedOut: true, by: "Dana"), false));
        Assert.Contains("checked out by someone", MaterialRules.SkipReason(Lib(checkedOut: true), false));
        Assert.Contains("released", MaterialRules.SkipReason(Lib("Library", released: true), false));
        Assert.Null(MaterialRules.SkipReason(Lib("Library", released: true), true));
        Assert.Equal("could not read lifecycle state", MaterialRules.SkipReason(Lib() with { Unreadable = "could not read lifecycle state" }, false));
    }

    [Fact]
    public void Facts_outcomes()
    {
        MaterialLibraryFacts F(bool exists = true, bool matches = true, bool? has = false) => new("C:\\x.adsklib", exists, matches, has is not null, false, has, 3);
        Assert.Null(MaterialRules.FactsOutcome(F(), "n"));
        Assert.Null(MaterialRules.FactsOutcome(F(has: null), "n"));
        Assert.Equal("skipped", MaterialRules.FactsOutcome(F(matches: false), "n")!.Value.Status);
        Assert.Null(MaterialRules.FactsOutcome(F(exists: false, matches: false), "n"));
        var dup = MaterialRules.FactsOutcome(F(has: true), "Nylon")!.Value;
        Assert.Equal("no_change", dup.Status);
        Assert.Contains("already has a material named Nylon", dup.Reason);
    }

    [Fact]
    public void Backup_name_and_library_match()
    {
        Assert.Equal("SF Materials-20261014-153012.adsklib", MaterialRules.BackupFileName("SF Materials.adsklib", new DateTime(2026, 10, 14, 15, 30, 12)));
        Assert.Equal("a_b-20261014-000000.adsklib", MaterialRules.BackupFileName("a|b.adsklib", new DateTime(2026, 10, 14)));
        const string full = @"C:\Vault Workspace\VAULT STANDARDS\DESIGN DATA\Materials\SF Materials.adsklib";
        Assert.True(MaterialRules.MatchesLibrary(full, "SF Materials"));
        Assert.True(MaterialRules.MatchesLibrary(full, "sf materials.adsklib"));
        Assert.True(MaterialRules.MatchesLibrary(full, "C:/Vault Workspace/VAULT STANDARDS/DESIGN DATA/Materials/SF Materials.adsklib"));
        Assert.False(MaterialRules.MatchesLibrary(full, "Inventor Material Library"));
        Assert.False(MaterialRules.MatchesLibrary(full, ""));
        Assert.True(MaterialRules.SameNumber(1010, 1010.0000000001));
        Assert.False(MaterialRules.SameNumber(1010, 1010.1));
    }

    // ---- the run ----

    private sealed class FakeHost : IMaterialLibraryHost
    {
        public Dictionary<string, DataCardFileInfo> Files = new(StringComparer.OrdinalIgnoreCase);
        public MaterialLibraryFacts Facts = new(@"C:\Vault Workspace\SF Materials.adsklib", true, true, true, true, false, 12);
        public List<string> Calls = new();
        public RevisePlan Plan = new(null, "Library -> Library Edit", "Library Edit -> Library", "Library Edit", "Library");
        public string? FailAdd;
        public string? FailChangeTo;
        public List<MaterialPropertyRow> Rows = new();
        public string? ListedMaterial;

        public IReadOnlyList<DataCardFileInfo> Find(DataCardFileRef file) =>
            Files.Values.Where(f => string.Equals(f.Name, file.Text, StringComparison.OrdinalIgnoreCase) || string.Equals(f.VaultPath, file.Text, StringComparison.OrdinalIgnoreCase)).ToList();

        public MaterialLibraryFacts Inspect(DataCardFileInfo file, string materialName) { Calls.Add("inspect " + materialName); return Facts; }

        public MaterialApplied AddMaterial(DataCardFileInfo file, MaterialSpec spec, string comment)
        {
            Calls.Add($"add {spec.Name} comment={comment} state={file.State}");
            if (FailAdd is not null) throw new DataCardStepException("checkin", FailAdd);
            return new MaterialApplied(file.Version + 1, @"C:\backups\SF Materials-1.adsklib", null, true);
        }

        public IReadOnlyList<MaterialPropertyRow> ListProperties(string materialName, string? library)
        {
            Calls.Add($"list {materialName} lib={library}");
            ListedMaterial = materialName;
            return Rows;
        }

        public RevisePlan PlanRevise(DataCardFileInfo f) { Calls.Add("plan " + f.Name); return Plan; }

        public DataCardStateChange ChangeState(DataCardFileInfo f, string toStateName, string comment)
        {
            Calls.Add($"state {f.Name} -> {toStateName}");
            if (FailChangeTo == toStateName) throw new DataCardStepException("revise", "Vault refused");
            Files[f.Name] = f with { State = toStateName, Released = toStateName == "Library" };
            return new DataCardStateChange(toStateName, "A");
        }

        public bool? LinkedToItem(DataCardFileInfo f) => null;
        public string? ReadPropertyCompliance(DataCardFileInfo f) => null;
        public IReadOnlyDictionary<PropRef, string?> Read(DataCardFileInfo f, IReadOnlyList<PropRef> props) => throw new NotSupportedException();
        public DataCardApplied Apply(DataCardFileInfo f, IReadOnlyList<PropChange> changes, string comment) => throw new NotSupportedException();
    }

    private static FakeHost Rig(DataCardFileInfo? info = null)
    {
        var h = new FakeHost();
        var i = info ?? Lib();
        h.Files[i.Name] = i;
        return h;
    }

    private static AddMaterialRequest Req(bool dry = true, bool revise = false, string? comment = null, string library = LibPath) =>
        new(library, Nylon(), dry, revise, comment);

    private static JsonObject First(JsonObject o) => o["results"]![0]!.AsObject();

    [Fact]
    public void Preview_reports_what_would_happen_and_writes_nothing()
    {
        var h = Rig();
        var r = First(AddLibraryMaterial.Run(Req(), h));
        Assert.Equal("preview", r["status"]!.GetValue<string>());
        Assert.DoesNotContain(h.Calls, c => c.StartsWith("add "));
        Assert.DoesNotContain(h.Calls, c => c.StartsWith("state "));
        var m = r["material"]!;
        Assert.Equal("Nylon 12 (PA12, MJF)", m["name"]!.GetValue<string>());
        var density = m["values"]!.AsArray().Single(v => v!["property"]!.GetValue<string>() == "Density")!;
        Assert.Equal(1010.0, density["will_write"]!.GetValue<double>(), 9);
        Assert.Equal("KilogramPerCubicMeter", density["unit"]!.GetValue<string>());
        Assert.Equal("1.01 g/cm3", density["given"]!.GetValue<string>());
        Assert.Single(m["not_stored"]!.AsArray());
        Assert.False(r["library"]!["material_exists"]!.GetValue<bool>());
        Assert.True(r["library"]!["local_matches_vault"]!.GetValue<bool>());
        Assert.Equal(5, r["steps"]!.AsArray().Count);
        Assert.Equal(1, AddLibraryMaterial.Run(Req(), h)["counts"]!["preview"]!.GetValue<int>());
    }

    [Fact]
    public void Preview_notes_a_library_that_inventor_has_not_loaded()
    {
        var h = Rig();
        h.Facts = h.Facts with { LoadedInInventor = false, ReadOnlyInInventor = null, MaterialExists = null, MaterialCount = null };
        var r = First(AddLibraryMaterial.Run(Req(), h));
        Assert.Equal("preview", r["status"]!.GetValue<string>());
        Assert.Null(r["library"]!["material_exists"]);
        Assert.Contains("checked again when it is applied", r["note"]!.GetValue<string>());
    }

    [Fact]
    public void Apply_adds_the_material_with_the_default_comment_and_reports_backup_and_verification()
    {
        var h = Rig();
        var res = AddLibraryMaterial.Run(Req(dry: false), h);
        var r = First(res);
        Assert.Equal("fixed", r["status"]!.GetValue<string>());
        Assert.Equal(8L, r["version"]!.GetValue<long>());
        Assert.Equal("Nylon 12 (PA12, MJF)", r["material"]!.GetValue<string>());
        Assert.Equal(@"C:\backups\SF Materials-1.adsklib", r["backup"]!.GetValue<string>());
        Assert.True(r["verified"]!.GetValue<bool>());
        Assert.Contains(h.Calls, c => c == "add Nylon 12 (PA12, MJF) comment=Add material Nylon 12 (PA12, MJF) state=Work in Progress");
        Assert.Equal(1, res["counts"]!["fixed"]!.GetValue<int>());
    }

    [Fact]
    public void Apply_uses_a_given_comment()
    {
        var h = Rig();
        AddLibraryMaterial.Run(Req(dry: false, comment: "PA12 for ABC"), h);
        Assert.Contains(h.Calls, c => c.Contains("comment=PA12 for ABC"));
    }

    [Fact]
    public void A_material_that_exists_is_no_change_and_nothing_is_written()
    {
        var h = Rig();
        h.Facts = h.Facts with { MaterialExists = true };
        var r = First(AddLibraryMaterial.Run(Req(dry: false), h));
        Assert.Equal("no_change", r["status"]!.GetValue<string>());
        Assert.Contains("already has a material", r["reason"]!.GetValue<string>());
        Assert.DoesNotContain(h.Calls, c => c.StartsWith("add "));
    }

    [Fact]
    public void A_local_copy_that_differs_from_Vault_stops_the_run()
    {
        var h = Rig();
        h.Facts = h.Facts with { LocalMatchesVault = false };
        var r = First(AddLibraryMaterial.Run(Req(dry: false), h));
        Assert.Equal("skipped", r["status"]!.GetValue<string>());
        Assert.Contains("local copy differs from Vault", r["reason"]!.GetValue<string>());
        Assert.DoesNotContain(h.Calls, c => c.StartsWith("add "));
    }

    [Fact]
    public void Library_checked_out_by_someone_else_is_skipped()
    {
        var h = Rig(Lib(checkedOut: true, by: "Dana"));
        var r = First(AddLibraryMaterial.Run(Req(dry: false), h));
        Assert.Equal("skipped", r["status"]!.GetValue<string>());
        Assert.Contains("checked out by Dana", r["reason"]!.GetValue<string>());
        Assert.DoesNotContain(h.Calls, c => c.StartsWith("inspect"));
    }

    [Fact]
    public void Missing_and_ambiguous_libraries()
    {
        var h = Rig();
        Assert.Equal("skipped", First(AddLibraryMaterial.Run(Req(library: "$/nowhere/x.adsklib"), h))["status"]!.GetValue<string>());
        h.Files["Other.adsklib"] = Lib(name: "Other.adsklib") with { VaultPath = "$/a/SF Materials.adsklib", Name = "SF Materials.adsklib" };
        h.Files["Other.adsklib"] = h.Files["Other.adsklib"];
        var r = First(AddLibraryMaterial.Run(Req(library: "SF Materials.adsklib"), h));
        Assert.Equal("error", r["status"]!.GetValue<string>());
        Assert.Equal("resolve", r["step"]!.GetValue<string>());
    }

    [Fact]
    public void A_released_library_needs_the_revise_flag()
    {
        var h = Rig(Lib("Library", released: true));
        var r = First(AddLibraryMaterial.Run(Req(), h));
        Assert.Equal("skipped", r["status"]!.GetValue<string>());
        Assert.Empty(h.Calls.Where(c => c.StartsWith("state ")));

        var prev = First(AddLibraryMaterial.Run(Req(revise: true), h));
        Assert.Equal("preview", prev["status"]!.GetValue<string>());
        Assert.True(prev["would_revise"]!.GetValue<bool>());
        Assert.Empty(h.Calls.Where(c => c.StartsWith("state ")));
    }

    [Fact]
    public void A_released_library_goes_through_its_edit_state_and_back()
    {
        var h = Rig(Lib("Library", released: true));
        var r = First(AddLibraryMaterial.Run(Req(dry: false, revise: true), h));
        Assert.Equal("fixed", r["status"]!.GetValue<string>());
        Assert.Equal("Library", r["state_after"]!.GetValue<string>());
        Assert.Equal("Nylon 12 (PA12, MJF)", r["material"]!.GetValue<string>());
        Assert.Equal(@"C:\backups\SF Materials-1.adsklib", r["backup"]!.GetValue<string>());
        var order = h.Calls.Where(c => c.StartsWith("state ") || c.StartsWith("add ")).ToList();
        Assert.Equal(new[] { "state SF Materials.adsklib -> Library Edit", "add Nylon 12 (PA12, MJF) comment=Add material Nylon 12 (PA12, MJF) state=Library Edit", "state SF Materials.adsklib -> Library" }, order);
    }

    [Fact]
    public void A_failed_add_is_an_error_naming_the_step_and_a_revised_library_is_still_returned()
    {
        var h = Rig();
        h.FailAdd = "Vault refused the check-in (the checkout was undone)";
        var r = First(AddLibraryMaterial.Run(Req(dry: false), h));
        Assert.Equal("error", r["status"]!.GetValue<string>());
        Assert.Equal("checkin", r["step"]!.GetValue<string>());
        Assert.Contains("checkout was undone", r["message"]!.GetValue<string>());

        var h2 = Rig(Lib("Library", released: true));
        h2.FailAdd = "boom";
        var r2 = First(AddLibraryMaterial.Run(Req(dry: false, revise: true), h2));
        Assert.Equal("error", r2["status"]!.GetValue<string>());
        Assert.True(r2["re_released"]!.GetValue<bool>());
        Assert.Equal("Library", h2.Files["SF Materials.adsklib"].State);
    }

    // ---- request parsing and the bridge op ----

    [Fact]
    public void ParseRequest_reads_library_values_and_flags()
    {
        var body = Obj($$"""{"library":"{{LibPath}}","name":"Override","values":{{NylonJson}},"dryRun":false,"reviseReleased":true,"comment":"c"}""");
        var req = AddLibraryMaterial.ParseRequest(body);
        Assert.Equal(LibPath, req.Library);
        Assert.Equal("Override", req.Spec.Name);
        Assert.False(req.DryRun);
        Assert.True(req.ReviseReleased);
        Assert.Equal("c", req.Comment);
        Assert.True(AddLibraryMaterial.ParseRequest(Obj($$"""{"library":"x","values":{{NylonJson}}}""")).DryRun);
    }

    [Fact]
    public void ParseRequest_rejects_missing_library_or_values()
    {
        Assert.Equal("bad_request", Assert.Throws<BridgeException>(() => AddLibraryMaterial.ParseRequest(Obj($$"""{"values":{{NylonJson}}}"""))).Code);
        Assert.Equal("bad_request", Assert.Throws<BridgeException>(() => AddLibraryMaterial.ParseRequest(Obj("""{"library":"x"}"""))).Code);
    }

    [Fact]
    public void Bridge_op_is_registered_and_previews_through_the_router()
    {
        var token = "0123456789abcdef0123456789abcdef";
        var router = new Router(token);
        var h = Rig();
        BridgeOps.RegisterAll(router, new NoVault(), new NoInventor(), new PathGuard(new[] { Path.GetTempPath() }), "1.0.0", work => work(), materials: h);
        var body = $$"""{"library":"{{LibPath}}","values":{{NylonJson}}}""";
        var (status, json) = router.Handle("POST", "/op/add_library_material", token, body);
        Assert.Equal(200, status);
        Assert.Equal("preview", ((JsonObject)JsonNode.Parse(json)!)["results"]![0]!["status"]!.GetValue<string>());

        var bare = new Router(token);
        BridgeOps.RegisterAll(bare, new NoVault(), new NoInventor(), new PathGuard(new[] { Path.GetTempPath() }), "1.0.0", work => work());
        var (s2, j2) = bare.Handle("POST", "/op/add_library_material", token, body);
        Assert.NotEqual(200, s2);
        Assert.Contains("not available", j2);
    }

    private sealed class NoVault : IVaultSession
    {
        public StatusInfo Status() => new(true, "u", "v", "s");
        public IReadOnlyList<SchemeInfo> GetNumberingSchemes() => Array.Empty<SchemeInfo>();
        public string GenerateFileNumber(long schemeId) => "";
        public FolderInfo? GetFolderByPath(string path) => null;
        public FolderInfo AddFolder(string parentPath, string name) => throw new NotSupportedException();
        public IReadOnlyList<CategoryInfo> GetFileCategories() => Array.Empty<CategoryInfo>();
        public VaultFile AddFile(string folderPath, string localPath, string fileName, long[] attachmentFileIds, string comment) => throw new NotSupportedException();
        public IReadOnlyList<VaultFile> UpdateFileCategories(long[] masterIds, long categoryId, string comment) => throw new NotSupportedException();
    }

    private sealed class NoInventor : IStepImporter
    {
        public string InventorVersion => "2026";
        public string ImportStepAsPart(string stepPath, string outIpt, IReadOnlyDictionary<string, string> design, IReadOnlyDictionary<string, string> custom) => throw new NotSupportedException();
    }

    // ---- MCP ----

    private static IReadOnlyList<McpTool> Tools(FakeHost h, PreviewStore<AddMaterialRequest>? store = null) =>
        MaterialLibraryTools.Build(h, store ?? new PreviewStore<AddMaterialRequest>(), null);

    private static JsonObject ToolArgs() => Obj($$"""{"library":"{{LibPath}}","values":{{NylonJson}}}""");

    [Fact]
    public void Tools_are_absent_without_a_host_and_shaped_with_one()
    {
        Assert.Empty(MaterialLibraryTools.Build(null, new PreviewStore<AddMaterialRequest>(), null));
        var tools = Tools(Rig());
        Assert.Equal(new[] { "list_material_properties", "add_library_material_preview", "add_library_material_apply" }, tools.Select(t => t.Name));
        Assert.Equal(McpToolKind.Read, tools[0].Kind);
        Assert.Equal(McpToolKind.Preview, tools[1].Kind);
        Assert.Equal(McpToolKind.Write, tools[2].Kind);
        Assert.Equal("preview_hash", tools[2].InputSchema["required"]![0]!.GetValue<string>());
        foreach (var t in tools) Assert.DoesNotContain("\u2014", t.Description);
        Assert.Contains("job_status", tools[2].Description);
    }

    [Fact]
    public void Apply_needs_the_matching_preview_hash_and_confirm_and_works_once()
    {
        var h = Rig();
        var tools = Tools(h);
        var preview = (JsonObject)tools[1].Handler(ToolArgs())!;
        var hash = preview["preview_hash"]!.GetValue<string>();
        Assert.NotEmpty(hash);
        Assert.DoesNotContain(h.Calls, c => c.StartsWith("add "));

        Assert.Equal("confirmation_required", Assert.Throws<BridgeException>(() => tools[2].Handler(new JsonObject { ["preview_hash"] = hash })).Code);
        Assert.Equal("no_matching_preview", Assert.Throws<BridgeException>(() => tools[2].Handler(new JsonObject { ["preview_hash"] = "nope", ["confirm"] = true })).Code);

        var applied = (JsonObject)tools[2].Handler(new JsonObject { ["preview_hash"] = hash, ["confirm"] = true })!;
        Assert.Equal("fixed", First(applied)["status"]!.GetValue<string>());
        Assert.Single(h.Calls.Where(c => c.StartsWith("add ")));
        Assert.Equal("no_matching_preview", Assert.Throws<BridgeException>(() => tools[2].Handler(new JsonObject { ["preview_hash"] = hash, ["confirm"] = true })).Code);
    }

    [Fact]
    public void A_changed_value_gives_a_different_hash_and_no_preview_gives_no_hash()
    {
        var tools = Tools(Rig());
        var a = (JsonObject)tools[1].Handler(ToolArgs())!;
        var changed = ToolArgs();
        changed["values"]!["density"]!["value"] = 1.02;
        var b = (JsonObject)tools[1].Handler(changed)!;
        Assert.NotEqual(a["preview_hash"]!.GetValue<string>(), b["preview_hash"]!.GetValue<string>());

        var h = Rig();
        h.Facts = h.Facts with { MaterialExists = true };
        var none = (JsonObject)Tools(h)[1].Handler(ToolArgs())!;
        Assert.Null(none["preview_hash"]);
        Assert.Contains("Nothing to apply", none["next"]!.GetValue<string>());
    }

    [Fact]
    public void List_properties_tool_returns_the_rows_and_passes_the_library()
    {
        var h = Rig();
        h.Rows.Add(new MaterialPropertyRow("SF Materials.adsklib: Nylon 12", "structural_Density", "Density", "Float", "KilogramPerCubicMeter", "1010"));
        var tools = Tools(h);
        var res = (JsonObject)tools[0].Handler(Obj("""{"material":"Nylon 12","library":"SF Materials"}"""))!;
        Assert.Equal(1, res["count"]!.GetValue<int>());
        Assert.Equal("structural_Density", res["rows"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("KilogramPerCubicMeter", res["rows"]![0]!["units"]!.GetValue<string>());
        Assert.Contains("list Nylon 12 lib=SF Materials", h.Calls);
        Assert.Equal("bad_request", Assert.Throws<BridgeException>(() => AddLibraryMaterial.ListProperties("  ", null, h)).Code);
    }

    [Fact]
    public void The_list_tool_is_also_a_bridge_read_op()
    {
        var h = Rig();
        h.Rows.Add(new MaterialPropertyRow("a", "structural_Density", "Density", "Float", "u", "1"));
        var router = new Router("0123456789abcdef0123456789abcdef");
        McpCatalog.RegisterReadOps(router, Tools(h), work => work());
        var (status, json) = router.Handle("POST", "/op/list_material_properties", "0123456789abcdef0123456789abcdef", """{"material":"Nylon 12"}""");
        Assert.Equal(200, status);
        Assert.Equal(1, ((JsonObject)JsonNode.Parse(json)!)["count"]!.GetValue<int>());
    }
}
