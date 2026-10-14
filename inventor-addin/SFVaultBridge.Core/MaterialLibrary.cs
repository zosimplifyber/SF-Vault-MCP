using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SFVaultBridge.Core;

/// <summary>The kinds of number a material value can be. Each has one base unit in MaterialUnits.</summary>
public enum MaterialQuantity { Density, Stress, Ratio, Expansion, Conductivity, SpecificHeat, Percent }

/// <summary>One structural value Inventor stores on a material: the key used in the request JSON, the Inventor asset value name (read from the
/// Structural*Schema.xml inside SF Materials.adsklib), the unit Inventor stores it in and the range its schema allows (inclusive).</summary>
public sealed record MaterialProperty(string Key, string Label, string AssetName, MaterialQuantity Quantity, string AssetUnit, double Min, double Max, bool Required);

/// <summary>A validated value ready to write: AssetValue is already in AssetUnit.</summary>
public sealed record MaterialValue(string Key, string Label, string AssetName, string Given, double AssetValue, string AssetUnit, MaterialQuantity Quantity);

/// <summary>The material to add. MaterialClass is the label of the physmat_Type choice (Plastic, Metal, ...) or null. NotStored lists request values
/// Inventor has no place for (kept out of the library, shown to the engineer).</summary>
public sealed record MaterialSpec(string Name, string Description, string? MaterialClass, IReadOnlyList<MaterialValue> Values, IReadOnlyList<string> NotStored)
{
    public JsonObject ToJson()
    {
        var values = new JsonArray();
        foreach (var v in Values)
            values.Add(new JsonObject { ["key"] = v.Key, ["asset_value"] = v.AssetName, ["given"] = v.Given, ["value"] = v.AssetValue, ["unit"] = v.AssetUnit });
        return new JsonObject
        {
            ["name"] = Name, ["description"] = Description, ["material_class"] = MaterialClass, ["values"] = values,
            ["not_stored"] = new JsonArray(NotStored.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
        };
    }
}

/// <summary>What the host found out about the library without changing anything. MaterialExists is null when Inventor does not have the library loaded
/// (the apply opens it and checks again).</summary>
public sealed record MaterialLibraryFacts(string LocalPath, bool LocalExists, bool LocalMatchesVault, bool LoadedInInventor,
                                          bool? ReadOnlyInInventor, bool? MaterialExists, int? MaterialCount);

/// <summary>Result of a successful apply. Verified is true when the material was found again after the library was reopened from the checked-in file.</summary>
public sealed record MaterialApplied(long Version, string BackupPath, string? Warning, bool Verified);

/// <summary>One asset value of a library material as Inventor reports it (list_material_properties).</summary>
public sealed record MaterialPropertyRow(string Asset, string Name, string DisplayName, string ValueType, string Units, string Value);

/// <summary>The Vault and Inventor side of add_library_material and list_material_properties. Core decides; the host touches the world.</summary>
public interface IMaterialLibraryHost : IDataCardHost
{
    /// <summary>Reads the library file's local state and, when Inventor has it loaded, whether the material is already in it. Changes nothing.</summary>
    MaterialLibraryFacts Inspect(DataCardFileInfo file, string materialName);

    /// <summary>Backs up the local copy, builds the material in a temporary hidden part document, checks the library out, adds the material, checks the
    /// library in and verifies it. Any failure before the check-in succeeds undoes the checkout and restores the local file; nothing after it does.</summary>
    MaterialApplied AddMaterial(DataCardFileInfo file, MaterialSpec spec, string comment);

    /// <summary>Every asset value (name, type, unit, value) of the material with that display name in the loaded libraries; library narrows it to one.
    /// Reads only. Throws BridgeException("not_found") when no such material is loaded.</summary>
    IReadOnlyList<MaterialPropertyRow> ListProperties(string materialName, string? library);
}

/// <summary>Unit names and conversions for material values. Every quantity has a base unit; a unit is a factor to it. Unit text is compared
/// with case, spaces, brackets, dots, stars and degree signs ignored, so "W/(m K)", "W/mK" and "w/(m*k)" are one unit.</summary>
public static class MaterialUnits
{
    public static string Normalize(string? unit)
    {
        var t = (unit ?? "").Trim().ToLowerInvariant()
            .Replace("\u00b5", "u").Replace("\u03bc", "u").Replace("\u00b3", "3").Replace("\u00b2", "2").Replace("\u00b7", "").Replace("\u22c5", "")
            .Replace("deg", "");
        return Regex.Replace(t, @"[\s()\[\]*.\u00b0^]", "");
    }

    // Base units: kg/m3, Pa, 1, 1/C, W/(m K), J/(kg K), %.
    private static readonly Dictionary<MaterialQuantity, Dictionary<string, double>> Table = new()
    {
        [MaterialQuantity.Density] = new()
        {
            ["kg/m3"] = 1, ["g/cm3"] = 1000, ["g/cc"] = 1000, ["g/ml"] = 1000, ["kg/l"] = 1000, ["g/l"] = 1, ["g/m3"] = 0.001,
            ["lb/in3"] = 27679.9047, ["lb/ft3"] = 16.01846337, ["kilogrampercubicmeter"] = 1,
        },
        [MaterialQuantity.Stress] = new()
        {
            ["pa"] = 1, ["kpa"] = 1e3, ["mpa"] = 1e6, ["gpa"] = 1e9, ["n/m2"] = 1, ["n/mm2"] = 1e6,
            ["psi"] = 6894.757293168, ["ksi"] = 6894757.293168, ["msi"] = 6894757293.168,
            ["pascal"] = 1, ["kilopascal"] = 1e3, ["megapascal"] = 1e6, ["gigapascal"] = 1e9,
        },
        [MaterialQuantity.Ratio] = new() { [""] = 1, ["unitless"] = 1, ["ratio"] = 1, ["1"] = 1 },
        [MaterialQuantity.Expansion] = new()
        {
            ["1/c"] = 1, ["1/k"] = 1, ["/c"] = 1, ["/k"] = 1, ["1/f"] = 1.8, ["/f"] = 1.8, ["in/in/f"] = 1.8,
            ["um/m/c"] = 1e-6, ["um/m/k"] = 1e-6, ["um/mc"] = 1e-6, ["um/mk"] = 1e-6, ["ppm/c"] = 1e-6, ["ppm/k"] = 1e-6,
            ["micrometerpermetercelsius"] = 1e-6,
        },
        [MaterialQuantity.Conductivity] = new()
        {
            ["w/mk"] = 1, ["w/mc"] = 1, ["w/m/k"] = 1, ["w/m/c"] = 1, ["mw/mk"] = 0.001, ["wattpermeterkelvin"] = 1,
            ["btu/hftf"] = 1.730734666, ["btu/hrftf"] = 1.730734666,
        },
        [MaterialQuantity.SpecificHeat] = new()
        {
            ["j/kgk"] = 1, ["j/kgc"] = 1, ["kj/kgk"] = 1000, ["kj/kgc"] = 1000, ["j/gk"] = 1000, ["j/gc"] = 1000,
            ["joulepergramcelsius"] = 1000, ["cal/gc"] = 4184, ["btu/lbf"] = 4186.8,
        },
        [MaterialQuantity.Percent] = new() { ["%"] = 1, ["percent"] = 1, ["pct"] = 1 },
    };

    public static bool TryFactor(MaterialQuantity q, string? unit, out double factor) =>
        Table[q].TryGetValue(Normalize(unit), out factor);

    /// <summary>The unit names a quantity accepts, for error messages.</summary>
    public static string Accepted(MaterialQuantity q) => q switch
    {
        MaterialQuantity.Density => "g/cm3, kg/m3, kg/L, lb/in3",
        MaterialQuantity.Stress => "Pa, kPa, MPa, GPa, psi, ksi",
        MaterialQuantity.Ratio => "no unit",
        MaterialQuantity.Expansion => "1/C, 1/K, 1/F, um/m/C, ppm/C",
        MaterialQuantity.Conductivity => "W/(m K), W/(m C), BTU/(h ft F)",
        MaterialQuantity.SpecificHeat => "J/(g C), J/(kg K), kJ/(kg K)",
        _ => "%",
    };

    /// <summary>Converts a value between two units of one quantity; false when either unit is not known for it.</summary>
    public static bool TryConvert(MaterialQuantity q, double value, string? from, string? to, out double result)
    {
        result = 0;
        if (!TryFactor(q, from, out var f) || !TryFactor(q, to, out var t) || t == 0) return false;
        result = value * f / t;
        return !double.IsNaN(result) && !double.IsInfinity(result);
    }
}

/// <summary>Request validation, value conversion, skip rules and file naming for add_library_material. No Inventor, no Vault.</summary>
public static class MaterialRules
{
    public const string LibraryExtension = ".adsklib";
    public const string LocalDiffers = "local copy differs from Vault; check in or discard local changes first";

    /// <summary>The structural values Inventor materials take, with the asset value names, units and bounds from the schemas inside the library file.
    /// Density kg/m3; modulus and strengths kPa; expansion um/m/C; conductivity W/(m K); specific heat J/(g C).</summary>
    public static readonly IReadOnlyList<MaterialProperty> Properties = new[]
    {
        new MaterialProperty("density", "Density", "structural_Density", MaterialQuantity.Density, "KilogramPerCubicMeter", 0, 25000, true),
        new MaterialProperty("youngs_modulus", "Young's modulus", "structural_Young_modulus", MaterialQuantity.Stress, "Kilopascal", 0.01, 1.3e9, true),
        new MaterialProperty("poissons_ratio", "Poisson's ratio", "structural_Poisson_ratio", MaterialQuantity.Ratio, "Unitless", -1, 0.5, true),
        new MaterialProperty("yield_strength", "Yield strength", "structural_Minimum_yield_stress", MaterialQuantity.Stress, "Kilopascal", 0, 1e7, false),
        new MaterialProperty("tensile_strength", "Tensile strength", "structural_Minimum_tensile_strength", MaterialQuantity.Stress, "Kilopascal", 0, 1e8, false),
        new MaterialProperty("thermal_expansion", "Thermal expansion coefficient", "structural_Thermal_expansion_coefficient", MaterialQuantity.Expansion, "MicrometerPerMeterCelsius", 0.1, 500, false),
        new MaterialProperty("thermal_conductivity", "Thermal conductivity", "structural_Thermal_conductivity", MaterialQuantity.Conductivity, "WattPerMeterKelvin", 0.001, 5000, false),
        new MaterialProperty("specific_heat", "Specific heat", "structural_Specific_heat", MaterialQuantity.SpecificHeat, "JoulePerGramCelsius", 0.1, 15, false),
    };

    /// <summary>The physmat_Type choices: label to the choice id Inventor stores.</summary>
    public static readonly IReadOnlyDictionary<string, string> MaterialClasses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Plastic"] = "physmat_plastic", ["Metal"] = "physmat_metal", ["Wood"] = "physmat_wood", ["Glass"] = "physmat_glass",
        ["Ceramic"] = "physmat_ceramic", ["Stone"] = "physmat_stone", ["Liquid"] = "physmat_liquid", ["Gas"] = "physmat_gas",
        ["Masonry"] = "physmat_masonry", ["Textile"] = "physmat_textile", ["Paint/Coating"] = "physmat_paint", ["Earth"] = "physmat_earth",
        ["Generic"] = "physmat_generic", ["Concrete"] = "physmat_concrete",
    };

    private static readonly HashSet<string> OtherKeys = new(StringComparer.OrdinalIgnoreCase) { "name", "description", "material_class", "elongation" };

    private static BridgeException Bad(string message) => new("bad_request", message);

    /// <summary>Reads the values JSON: name, description, material_class, elongation and one entry per Properties key. A number is {"value": 1.01, "unit": "g/cm3"}
    /// or the text "1.01 g/cm3"; a bare number is allowed only for Poisson's ratio. Every problem is a bad_request naming the field.</summary>
    public static MaterialSpec ParseSpec(JsonObject values, string? nameOverride = null)
    {
        var known = new HashSet<string>(Properties.Select(p => p.Key).Concat(OtherKeys), StringComparer.OrdinalIgnoreCase);
        var unknown = values.Select(p => p.Key).Where(k => !known.Contains(k)).ToList();
        if (unknown.Count > 0)
            throw Bad($"Unknown material value '{unknown[0]}'. Known: {string.Join(", ", known.OrderBy(k => k, StringComparer.Ordinal))}.");

        var name = (nameOverride is { Length: > 0 } ? nameOverride : Text(values, "name")).Trim();
        if (name.Length == 0) throw Bad("The material name is required (name).");
        if (name.Length > 100) throw Bad("The material name is longer than 100 characters.");
        if (name.Any(char.IsControl)) throw Bad("The material name must not contain control characters.");
        var description = Text(values, "description").Trim();
        if (description.Length > 500) throw Bad("The description is longer than 500 characters.");
        if (description.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'))) throw Bad("The description must not contain control characters.");

        string? cls = null;
        var clsText = Text(values, "material_class").Trim();
        if (clsText.Length > 0)
        {
            var hit = MaterialClasses.Keys.FirstOrDefault(k => string.Equals(k, clsText, StringComparison.OrdinalIgnoreCase))
                      ?? throw Bad($"material_class '{clsText}' is not one of: {string.Join(", ", MaterialClasses.Keys)}.");
            cls = hit;
        }

        var list = new List<MaterialValue>();
        foreach (var p in Properties)
        {
            var node = values.FirstOrDefault(kv => string.Equals(kv.Key, p.Key, StringComparison.OrdinalIgnoreCase)).Value;
            if (node is null)
            {
                if (p.Required) throw Bad($"{p.Key} is required.");
                continue;
            }
            var (number, unit) = ReadQuantity(p.Key, node, allowBare: p.Quantity == MaterialQuantity.Ratio);
            if (!MaterialUnits.TryConvert(p.Quantity, number, unit, p.AssetUnit, out var converted))
                throw Bad($"{p.Key}: unit '{unit}' is not one of {MaterialUnits.Accepted(p.Quantity)}.");
            if (converted < p.Min || converted > p.Max)
                throw Bad($"{p.Key}: {Fmt(number)} {unit}".TrimEnd() + $" is {Fmt(converted)} {p.AssetUnit} in Inventor, outside the allowed {Fmt(p.Min)} to {Fmt(p.Max)}.");
            list.Add(new MaterialValue(p.Key, p.Label, p.AssetName, $"{Fmt(number)} {unit}".TrimEnd(), converted, p.AssetUnit, p.Quantity));
        }

        var notStored = new List<string>();
        var elong = values.FirstOrDefault(kv => string.Equals(kv.Key, "elongation", StringComparison.OrdinalIgnoreCase)).Value;
        if (elong is not null)
        {
            var (e, eu) = ReadQuantity("elongation", elong, allowBare: false);
            if (!MaterialUnits.TryConvert(MaterialQuantity.Percent, e, eu, "%", out var pct) || pct < 0 || pct > 1000)
                throw Bad("elongation: give a percent between 0 and 1000, for example {\"value\": 20, \"unit\": \"%\"}.");
            notStored.Add($"elongation {Fmt(pct)} % (Inventor materials have no value for it)");
        }
        return new MaterialSpec(name, description, cls, list, notStored);
    }

    private static string Text(JsonObject o, string key)
    {
        var node = o.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
        if (node is null) return "";
        return node is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw Bad($"'{key}' must be text.");
    }

    private static (double Number, string Unit) ReadQuantity(string key, JsonNode node, bool allowBare)
    {
        double number;
        string unit;
        if (node is JsonValue v && v.TryGetValue<string>(out var text))
        {
            var m = Regex.Match(text.Trim(), @"^([-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?)\s*(.*)$");
            if (!m.Success) throw Bad($"{key}: '{text}' is not a number with a unit, for example \"48 MPa\".");
            number = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            unit = m.Groups[2].Value.Trim();
        }
        else if (node is JsonValue n && (n.TryGetValue<double>(out var d)))
        {
            if (!allowBare) throw Bad($"{key}: give a unit, for example {{\"value\": {Fmt(d)}, \"unit\": \"...\"}}.");
            number = d;
            unit = "";
        }
        else if (node is JsonObject o)
        {
            var val = o.FirstOrDefault(kv => string.Equals(kv.Key, "value", StringComparison.OrdinalIgnoreCase)).Value;
            if (val is not JsonValue vv || !vv.TryGetValue<double>(out number)) throw Bad($"{key}.value must be a number.");
            var u = o.FirstOrDefault(kv => string.Equals(kv.Key, "unit", StringComparison.OrdinalIgnoreCase)).Value;
            unit = u is null ? "" : u is JsonValue uv && uv.TryGetValue<string>(out var us) ? us.Trim() : throw Bad($"{key}.unit must be text.");
            if (unit.Length == 0 && !allowBare) throw Bad($"{key}.unit is required.");
        }
        else throw Bad($"{key} must be {{\"value\": number, \"unit\": text}} or text such as \"48 MPa\".");
        if (double.IsNaN(number) || double.IsInfinity(number)) throw Bad($"{key} must be a finite number.");
        return (number, unit);
    }

    /// <summary>Invariant, shortest round-trip number text.</summary>
    public static string Fmt(double d) => d.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Why the library file may not be edited, or null. Not .adsklib, unreadable state, checked out (by anyone: the check-out stops the
    /// write), or released without the opt-in.</summary>
    public static string? SkipReason(DataCardFileInfo f, bool reviseReleased)
    {
        if (!Path.GetExtension(f.Name).Equals(LibraryExtension, StringComparison.OrdinalIgnoreCase))
            return $"{f.Name} is not an {LibraryExtension} material library.";
        if (f.Unreadable is not null) return f.Unreadable;
        if (f.CheckedOut)
        {
            var who = string.IsNullOrWhiteSpace(f.CheckedOutBy) ? "someone" : f.CheckedOutBy;
            return $"{f.Name} is checked out by {who}. Check it in or undo the checkout first.";
        }
        if (DataCardFix.IsReleased(f) && !reviseReleased)
            return $"{f.Name} is in state {(string.IsNullOrWhiteSpace(f.State) ? "unknown" : f.State.Trim())} (released). Pass reviseReleased to edit it through its edit state.";
        return null;
    }

    /// <summary>Why nothing should happen given what the host saw, or null. A local copy that is not Vault's latest stops the run; a material that
    /// is already there is "no change".</summary>
    public static (string Status, string Reason)? FactsOutcome(MaterialLibraryFacts f, string materialName)
    {
        if (f.LocalExists && !f.LocalMatchesVault) return ("skipped", LocalDiffers);
        if (f.MaterialExists == true) return ("no_change", $"The library already has a material named {materialName}.");
        return null;
    }

    /// <summary>"SF Materials-20261014-153012.adsklib": library name, local time stamp, characters Windows rejects replaced.</summary>
    public static string BackupFileName(string libraryFileName, DateTime local)
    {
        var stem = Path.GetFileNameWithoutExtension(libraryFileName);
        var clean = new string(stem.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
        if (clean.Length == 0) clean = "library";
        return $"{clean}-{local:yyyyMMdd-HHmmss}{LibraryExtension}";
    }

    /// <summary>True when the query names the loaded library: its full file name, its file name, or the file name without extension.</summary>
    public static bool MatchesLibrary(string fullFileName, string query)
    {
        var q = (query ?? "").Trim().Replace('/', '\\');
        if (q.Length == 0) return false;
        var full = (fullFileName ?? "").Replace('/', '\\');
        if (string.Equals(full, q, StringComparison.OrdinalIgnoreCase)) return true;
        var file = Path.GetFileName(full);
        return string.Equals(file, q, StringComparison.OrdinalIgnoreCase)
               || string.Equals(Path.GetFileNameWithoutExtension(file), q, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A number from Inventor compared with the number sent, with a tiny relative tolerance for the unit round trip.</summary>
    public static bool SameNumber(double a, double b) =>
        Math.Abs(a - b) <= 1e-9 * Math.Max(1, Math.Max(Math.Abs(a), Math.Abs(b)));

    public static JsonObject RowJson(MaterialPropertyRow r) => new()
    {
        ["asset"] = r.Asset, ["name"] = r.Name, ["display_name"] = r.DisplayName, ["type"] = r.ValueType, ["units"] = r.Units, ["value"] = r.Value,
    };
}

public sealed record AddMaterialRequest(string Library, MaterialSpec Spec, bool DryRun, bool ReviseReleased, string? Comment);

/// <summary>The add_library_material bridge operation: preview or apply for one library, through IMaterialLibraryHost.</summary>
public static class AddLibraryMaterial
{
    public const string Family = "add_library_material";

    public static AddMaterialRequest ParseRequest(JsonObject body)
    {
        var library = Args.Str(body, "library").Trim();
        if (body["values"] is not JsonObject values) throw new BridgeException("bad_request", "'values' is required: an object with the material's name, description and property values.");
        var spec = MaterialRules.ParseSpec(values, Args.OptStr(body, "name").Trim());
        var comment = Args.OptStr(body, "comment").Trim();
        return new AddMaterialRequest(library, spec, Args.Bool(body, "dryRun", true), Args.Bool(body, "reviseReleased", false), comment.Length > 0 ? comment : null);
    }

    public static string DefaultComment(MaterialSpec spec) => $"Add material {spec.Name}";

    public static JsonObject Run(AddMaterialRequest req, IMaterialLibraryHost host, Action<string>? log = null) =>
        Run(req, host, log, new DataCardFix.RunState());

    public static JsonObject Run(AddMaterialRequest req, IMaterialLibraryHost host, Action<string>? log, DataCardFix.RunState run)
    {
        JsonObject r;
        try { r = RunOne(req, host, run); }
        catch (BridgeException ex) when (ex.Code == "not_signed_in") { r = DataCardFix.Error(req.Library, "sign_in", ex.Message); }
        var status = r["status"]!.GetValue<string>();
        var detail = r["reason"] ?? r["message"] ?? r["version"];
        log?.Invoke($"add_library_material {r["file"]}: {status}" + (detail is null ? "" : " " + detail));
        var counts = new Dictionary<string, int> { ["preview"] = 0, ["fixed"] = 0, ["no_change"] = 0, ["skipped"] = 0, ["error"] = 0 };
        counts[status]++;
        return new JsonObject
        {
            ["results"] = new JsonArray(r),
            ["counts"] = new JsonObject(counts.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value))),
        };
    }

    /// <summary>What a preview tells the engineer: the values as they will be written, and the steps an apply takes.</summary>
    public static JsonObject PreviewJson(DataCardFileInfo info, MaterialSpec spec, MaterialLibraryFacts facts, bool revise, RevisePlan? plan, bool? linked)
    {
        var values = new JsonArray();
        foreach (var v in spec.Values)
            values.Add(new JsonObject { ["property"] = v.Label, ["asset_value"] = v.AssetName, ["given"] = v.Given, ["will_write"] = v.AssetValue, ["unit"] = v.AssetUnit });
        var material = new JsonObject
        {
            ["name"] = spec.Name, ["description"] = spec.Description, ["material_class"] = spec.MaterialClass, ["values"] = values,
            ["not_stored"] = new JsonArray(spec.NotStored.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
        };
        var steps = new JsonArray
        {
            "Copy the local library file to the backups folder (%APPDATA%\\Simplifyber\\VaultBridge\\backups).",
            "Build the material in a temporary hidden part document and set the values (nothing in Vault yet).",
            "Close the library in Inventor, check it out of Vault, open it again and add the material.",
            "Close it, check it in with the comment, reopen it from the checked-in file and confirm the material is there.",
            "On any failure: undo the checkout, restore the local file from the backup, report the step.",
        };
        var p = new JsonObject
        {
            ["file"] = info.VaultPath, ["status"] = "preview", ["action"] = "add_material", ["material"] = material,
            ["library"] = new JsonObject
            {
                ["local_path"] = facts.LocalPath, ["local_exists"] = facts.LocalExists, ["local_matches_vault"] = facts.LocalMatchesVault,
                ["loaded_in_inventor"] = facts.LoadedInInventor, ["read_only_in_inventor"] = facts.ReadOnlyInInventor,
                ["material_exists"] = facts.MaterialExists, ["materials_in_library"] = facts.MaterialCount,
                ["vault_version"] = info.Version, ["state"] = info.State, ["category"] = info.Category,
            },
            ["steps"] = steps,
            ["changes"] = new JsonObject { ["add_material"] = new JsonObject { ["from"] = null, ["to"] = spec.Name } },
        };
        if (facts.MaterialExists is null)
            p["note"] = "Inventor does not have this library loaded, so whether the material exists is checked again when it is applied.";
        if (revise)
        {
            p["would_revise"] = true;
            p["transitions"] = new JsonArray(plan!.ToWorkInProgress, plan.ToReleased);
        }
        if (linked == true) p["linked_to_item"] = true;
        return p;
    }

    private static JsonObject RunOne(AddMaterialRequest req, IMaterialLibraryHost host, DataCardFix.RunState run)
    {
        DataCardFileInfo? info = null;
        try
        {
            var matches = host.Find(DataCardFix.ParseFile(req.Library));
            if (matches.Count == 0) return DataCardFix.Skipped(req.Library, $"{req.Library} was not found in Vault.");
            if (matches.Count > 1)
                return DataCardFix.Error(req.Library, "resolve", $"{req.Library} matches {matches.Count} files ({string.Join(", ", matches.Select(m => m.VaultPath))}). Pass the full $/ path.");
            info = matches[0];
            var skip = MaterialRules.SkipReason(info, req.ReviseReleased);
            if (skip is not null) return DataCardFix.Skipped(info.VaultPath, skip);

            MaterialLibraryFacts facts;
            try { facts = host.Inspect(info, req.Spec.Name); }
            catch (Exception ex) when (ex is not DataCardSkipException and not DataCardStepException and not BridgeException)
            { throw new DataCardStepException("read", ex.Message); }
            var outcome = MaterialRules.FactsOutcome(facts, req.Spec.Name);
            if (outcome is { Status: "skipped" } s) return DataCardFix.Skipped(info.VaultPath, s.Reason);
            if (outcome is { Status: "no_change" } nc)
                return new JsonObject { ["file"] = info.VaultPath, ["status"] = "no_change", ["reason"] = nc.Reason, ["changes"] = new JsonObject() };

            var revise = DataCardFix.IsReleased(info);
            RevisePlan? plan = null;
            if (revise && run.ReviseStopped) return DataCardFix.Skipped(info.VaultPath, DataCardFix.ReviseStoppedReason);
            if (revise)
            {
                plan = host.PlanRevise(info);
                if (plan.Problem is not null) return DataCardFix.Skipped(info.VaultPath, plan.Problem);
            }
            var linked = host.LinkedToItem(info);
            if (req.DryRun) return PreviewJson(info, req.Spec, facts, revise, plan, linked);

            var comment = req.Comment ?? DefaultComment(req.Spec);
            var changes = new JsonObject { ["add_material"] = new JsonObject { ["from"] = null, ["to"] = req.Spec.Name } };
            string? backup = null;
            var verified = false;
            DataCardApplied Edit(DataCardFileInfo fresh)
            {
                var m = host.AddMaterial(fresh, req.Spec, comment);
                backup = m.BackupPath;
                verified = m.Verified;
                return new DataCardApplied(m.Version, m.Warning);
            }

            JsonObject result;
            if (revise) result = DataCardFix.ReviseAround(info, changes, comment, host, plan!, Edit);
            else
            {
                var applied = Edit(info);
                result = new JsonObject { ["file"] = info.VaultPath, ["status"] = "fixed", ["version"] = applied.Version >= 0 ? applied.Version : null, ["changes"] = changes };
                if (applied.Warning is not null) result["warning"] = applied.Warning;
            }
            result["material"] = req.Spec.Name;
            if (backup is not null) result["backup"] = backup;
            if (result["status"]?.GetValue<string>() == "fixed") result["verified"] = verified;
            if (revise && result["step"]?.GetValue<string>() == "re_release") run.ReviseStopped = true;
            if (linked == true) result["linked_to_item"] = true;
            return result;
        }
        catch (DataCardSkipException ex) { return DataCardFix.Skipped(info?.VaultPath ?? req.Library, ex.Message); }
        catch (DataCardStepException ex) { return DataCardFix.Error(info?.VaultPath ?? req.Library, ex.Step, ex.Message); }
        catch (BridgeException ex) when (ex.Code == "not_signed_in") { throw; }
        catch (BridgeException ex) { return DataCardFix.Error(info?.VaultPath ?? req.Library, ex.Code, ex.Message); }
        catch (Exception ex) { return DataCardFix.Error(info?.VaultPath ?? req.Library, "unexpected", ex.Message); }
    }

    /// <summary>The read-only list_material_properties operation: every asset value of one library material.</summary>
    public static JsonObject ListProperties(string material, string? library, IMaterialLibraryHost host)
    {
        var name = (material ?? "").Trim();
        if (name.Length == 0) throw new BridgeException("bad_request", "'material' is required: the display name of a material in a loaded library.");
        var rows = host.ListProperties(name, string.IsNullOrWhiteSpace(library) ? null : library.Trim());
        return new JsonObject
        {
            ["material"] = name,
            ["count"] = rows.Count,
            ["rows"] = new JsonArray(rows.Select(r => (JsonNode?)MaterialRules.RowJson(r)).ToArray()),
            ["note"] = "Names, types and units are what Inventor reports. add_library_material writes the structural_* names in MaterialRules.Properties; compare them with this list before the first apply.",
        };
    }
}

/// <summary>Preview then apply for add_library_material over MCP, plus the read-only list_material_properties.</summary>
public static class MaterialLibraryTools
{
    public static readonly TimeSpan WriteTimeout = TimeSpan.FromMinutes(10);
    private static readonly string[] Ignored = { "dryRun" };

    private static JsonObject RequestProps() => Schema.Props(
        ("library", Schema.Str("The material library in Vault: a full $/ path such as $/VAULT STANDARDS/DESIGN DATA/Materials/SF Materials.adsklib.")),
        ("name", Schema.Str("Display name of the new material. Optional when values has a name.")),
        ("values", new JsonObject
        {
            ["type"] = "object",
            ["description"] = "name, description, material_class (Plastic, Metal, ...), density, youngs_modulus, poissons_ratio, yield_strength, tensile_strength, thermal_expansion, thermal_conductivity, specific_heat, elongation. " +
                              "Each number is {\"value\": 1.01, \"unit\": \"g/cm3\"} or the text \"48 MPa\"; density, youngs_modulus and poissons_ratio are required.",
        }),
        ("revise_released", Schema.Bool("Allow a library in a released state by moving it to its edit state and back (default false).", false)),
        ("comment", Schema.Str("Vault check-in comment. Default: Add material <name>.")));

    public static IReadOnlyList<McpTool> Build(IMaterialLibraryHost? host, PreviewStore<AddMaterialRequest> store, Action<string>? log)
    {
        if (host is null) return Array.Empty<McpTool>();
        return new[]
        {
            new McpTool("list_material_properties", "List a library material's asset values",
                "Read only. Dumps every asset value (name, type, unit, value) of a material that is loaded in Inventor, followed through its referenced physical and appearance assets. " +
                "Run it on an existing material first, to confirm the structural_* names and units add_library_material writes. Changes nothing.",
                Schema.Obj(Schema.Props(
                    ("material", Schema.Str("Display name of a material in a loaded library, for example Nylon 12.")),
                    ("library", Schema.Str("Optional: the loaded library's file name or full path, to narrow the search."))), "material"),
                McpToolKind.Read, a => AddLibraryMaterial.ListProperties(Args.Str(a, "material"), Args.OptStr(a, "library"), host), WriteTimeout),
            new McpTool("add_library_material_preview", "Preview adding a material to the library",
                "Dry run of adding a material to the team's shared Inventor material library in Vault. Writes nothing. Resolves the library file, checks the local copy matches Vault, " +
                "checks whether the material already exists and shows the values exactly as they will be written (converted to Inventor's units). " +
                "Show the result to the engineer; only after they approve call add_library_material_apply with the preview_hash and confirm true within 10 minutes.",
                Schema.Obj(RequestProps(), "library", "values"), McpToolKind.Preview, a => Preview(a, host, store, log), WriteTimeout),
            new McpTool("add_library_material_apply", "Apply adding a material to the library",
                "Backs up the local library file, checks the library out of Vault, adds the material, checks it back in and verifies it. A WRITE. Needs the preview_hash from add_library_material_preview " +
                "(valid 10 minutes, one use) and confirm true, only after the engineer approved the preview. If it returns {status:\"running\", job_id}, the write is still going: call job_status and never start it again.",
                Schema.Obj(Schema.Props(
                    ("preview_hash", Schema.Str("The preview_hash returned by add_library_material_preview.")),
                    ("confirm", Schema.Bool("Must be true. Set it only after the engineer has seen the preview and said yes."))), "preview_hash", "confirm"),
                McpToolKind.Write, a => Apply(a, host, store, log), WriteTimeout),
        };
    }

    private static JsonObject Canonical(AddMaterialRequest r) => new()
    {
        ["library"] = r.Library, ["spec"] = r.Spec.ToJson(), ["reviseReleased"] = r.ReviseReleased, ["comment"] = r.Comment,
    };

    private static JsonNode Preview(JsonObject a, IMaterialLibraryHost host, PreviewStore<AddMaterialRequest> store, Action<string>? log)
    {
        var body = new JsonObject
        {
            ["library"] = Args.Str(a, "library"), ["name"] = Args.OptStr(a, "name"), ["values"] = a["values"]?.DeepClone(),
            ["reviseReleased"] = Args.Bool(a, "revise_released", false), ["comment"] = Args.OptStr(a, "comment"), ["dryRun"] = true,
        };
        var req = AddLibraryMaterial.ParseRequest(body);
        var result = AddLibraryMaterial.Run(req, host, log);
        if ((result["counts"]?["preview"]?.GetValue<int>() ?? 0) > 0)
        {
            var hash = PreviewGate.Hash(AddLibraryMaterial.Family, Canonical(req), Ignored);
            store.PutAs(hash, req with { DryRun = false });
            result["preview_hash"] = hash;
            result["next"] = $"Show this to the engineer. If they approve, call add_library_material_apply with this preview_hash and confirm true within {(int)store.Ttl.TotalMinutes} minutes.";
        }
        else result["next"] = "Nothing to apply: the library would not change.";
        return result;
    }

    private static JsonNode Apply(JsonObject a, IMaterialLibraryHost host, PreviewStore<AddMaterialRequest> store, Action<string>? log)
    {
        if (!Args.Bool(a, "confirm", false))
            throw new BridgeException("confirmation_required", "Not applied. Show the engineer the add_library_material_preview result, and only after they say yes call this again with confirm true.");
        var req = store.Take(Args.Str(a, "preview_hash").Trim())
                  ?? throw new BridgeException("no_matching_preview", $"Not applied. The preview_hash is unknown, already used, or older than {(int)store.Ttl.TotalMinutes} minutes. Run add_library_material_preview again, show it to the engineer, then apply.");
        return AddLibraryMaterial.Run(req, host, log);
    }
}
