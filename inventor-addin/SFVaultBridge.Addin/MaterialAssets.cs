using System.Runtime.InteropServices;
using SFVaultBridge.Core;
using Inv = Inventor;

namespace SFVaultBridge.Addin;

/// <summary>
/// Inventor's material asset API for the shared material library: find a loaded AssetLibrary by file, dump a material's asset values, build a
/// material in a temporary hidden part document, copy it into a library, and read it back. The API path: AssetLibraries.Open(fullFileName) to load,
/// AssetLibrary.IsReadOnly to see whether the library can be written, Document.Assets.Add to create the asset, Asset.CopyTo(AssetLibrary) to add
/// it, AssetLibrary.Remove() to close the library (it has no Save: the file is written as the asset is copied in). Runs on Inventor's UI thread.
/// Value names are the structural_* names from the schemas inside SF Materials.adsklib (see MaterialRules.Properties); nothing here is run
/// against Inventor until list_material_properties has confirmed them on a live material.
/// </summary>
internal static class MaterialAssets
{
    private const int MaxRows = 600;
    private const int MaxDepth = 4;

    public sealed record LibraryState(bool WasLoaded, bool WasActiveMaterial, bool WasActiveAppearance);

    private static string Norm(string? p)
    {
        try { return string.IsNullOrWhiteSpace(p) ? "" : System.IO.Path.GetFullPath(p).TrimEnd('\\'); }
        catch (Exception) { return (p ?? "").Trim(); }
    }

    private static bool SamePath(string? a, string? b) =>
        Norm(a).Length > 0 && string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);

    // ---- Libraries ----

    /// <summary>The libraries Inventor has loaded (a library that throws when asked is skipped).</summary>
    public static IReadOnlyList<Inv.AssetLibrary> Loaded(Inv.Application app)
    {
        var list = new List<Inv.AssetLibrary>();
        var libs = app.AssetLibraries;
        var n = libs.Count;
        for (var i = 1; i <= n; i++)
        {
            try { list.Add(libs[i]); }
            catch (COMException) { }
            catch (ArgumentException) { }
        }
        return list;
    }

    public static string FullName(Inv.AssetLibrary lib)
    {
        try { return lib.FullFileName ?? ""; }
        catch (COMException) { return ""; }
    }

    public static Inv.AssetLibrary? FindLoaded(Inv.Application app, string fullPath) =>
        Loaded(app).FirstOrDefault(l => SamePath(FullName(l), fullPath));

    public static Inv.Asset? FindMaterial(Inv.AssetLibrary lib, string displayName)
    {
        foreach (Inv.Asset a in lib.MaterialAssets)
        {
            try { if (string.Equals(a.DisplayName, displayName, StringComparison.OrdinalIgnoreCase)) return a; }
            catch (COMException) { }
        }
        return null;
    }

    public static int MaterialCount(Inv.AssetLibrary lib)
    {
        try { return lib.MaterialAssets.Count; }
        catch (COMException) { return -1; }
    }

    public static IReadOnlyList<string> MaterialNames(Inv.AssetLibrary lib)
    {
        var names = new List<string>();
        foreach (Inv.Asset a in lib.MaterialAssets)
        {
            try { names.Add(a.DisplayName); }
            catch (COMException) { }
        }
        return names;
    }

    /// <summary>Closes the library in Inventor (AssetLibrary.Remove; the file is not deleted) and remembers whether it was the active material and
    /// appearance library. Returns WasLoaded false when Inventor did not have it.</summary>
    public static LibraryState Unload(Inv.Application app, string fullPath)
    {
        var lib = FindLoaded(app, fullPath);
        if (lib is null) return new LibraryState(false, false, false);
        var activeMaterial = false;
        var activeAppearance = false;
        try { activeMaterial = SamePath(app.ActiveMaterialLibrary?.FullFileName, fullPath); } catch (COMException) { }
        try { activeAppearance = SamePath(app.ActiveAppearanceLibrary?.FullFileName, fullPath); } catch (COMException) { }
        lib.Remove();
        return new LibraryState(true, activeMaterial, activeAppearance);
    }

    /// <summary>Closes a library handle this code opened.</summary>
    public static void Close(Inv.AssetLibrary lib) => lib.Remove();

    public static Inv.AssetLibrary Open(Inv.Application app, string fullPath) => app.AssetLibraries.Open(fullPath);

    /// <summary>Puts Inventor's library list back the way it was: the library loaded again and active where it was active.</summary>
    public static void Restore(Inv.Application app, Inv.AssetLibrary lib, LibraryState state)
    {
        if (state.WasActiveMaterial) app.ActiveMaterialLibrary = lib;
        if (state.WasActiveAppearance) app.ActiveAppearanceLibrary = lib;
    }

    /// <summary>After a failed run: reload the library from the (restored) local file when Inventor had it. Returns a short note, never throws.</summary>
    public static string ReloadAfterFailure(Inv.Application app, string fullPath, LibraryState state)
    {
        try
        {
            var stillLoaded = FindLoaded(app, fullPath);
            if (stillLoaded is not null) stillLoaded.Remove();
            if (!state.WasLoaded) return "";
            var lib = Open(app, fullPath);
            Restore(app, lib, state);
            return "; Inventor has the library loaded again";
        }
        catch (Exception ex) { return "; Inventor could not load the library again (" + ex.Message + "), restart Inventor"; }
    }

    // ---- Reading values ----

    public static string TypeName(Inv.AssetValue v)
    {
        try { return v.ValueType.ToString().Replace("kAssetValueType", "").Replace("kAssetValue", ""); }
        catch (COMException) { return "?"; }
    }

    private static string Num(double d) => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The value as text plus its unit (floats only).</summary>
    public static (string Text, string Units) ValueText(Inv.AssetValue v)
    {
        try
        {
            switch (v.ValueType)
            {
                case Inv.AssetValueTypeEnum.kAssetValueTypeFloat:
                    var f = (Inv.FloatAssetValue)v;
                    return (Num(f.Value), SafeUnits(f));
                case Inv.AssetValueTypeEnum.kAssetValueTypeInteger: return (((Inv.IntegerAssetValue)v).Value.ToString(System.Globalization.CultureInfo.InvariantCulture), "");
                case Inv.AssetValueTypeEnum.kAssetValueTypeBoolean: return (((Inv.BooleanAssetValue)v).Value ? "true" : "false", "");
                case Inv.AssetValueTypeEnum.kAssetValueTypeString: return (((Inv.StringAssetValue)v).Value ?? "", "");
                case Inv.AssetValueTypeEnum.kAssetValueTypeChoice: return (((Inv.ChoiceAssetValue)v).Value ?? "", "");
                case Inv.AssetValueTypeEnum.kAssetValueTypeFilename: return (((Inv.FilenameAssetValue)v).Value ?? "", "");
                case Inv.AssetValueTypeEnum.kAssetValueTypeColor:
                    var c = ((Inv.ColorAssetValue)v).Value;
                    return ($"{c.Red},{c.Green},{c.Blue}", "");
                case Inv.AssetValueTypeEnum.kAssetValueTypeReference:
                    return (string.Join("; ", Referenced((Inv.ReferenceAssetValue)v).Select(a => SafeName(a))), "");
                default: return ("(not shown)", "");
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException) { return ("(unreadable: " + ex.Message + ")", ""); }
    }

    private static string SafeUnits(Inv.FloatAssetValue f)
    {
        try { return f.Units ?? ""; }
        catch (COMException) { return ""; }
    }

    private static string SafeName(Inv.Asset a)
    {
        try { return $"{a.DisplayName} [{a.AssetType.ToString().Replace("kAssetType", "")}]"; }
        catch (COMException) { return "(asset)"; }
    }

    /// <summary>The assets a reference value points at (empty when none or unreadable).</summary>
    public static IReadOnlyList<Inv.Asset> Referenced(Inv.ReferenceAssetValue rv)
    {
        var list = new List<Inv.Asset>();
        try
        {
            var many = rv.get_Values();
            if (many is not null) list.AddRange(many.Where(a => a is not null));
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException) { }
        if (list.Count == 0)
        {
            try { var one = rv.Value; if (one is not null) list.Add(one); }
            catch (Exception ex) when (ex is COMException or InvalidCastException) { }
        }
        return list;
    }

    /// <summary>Every asset value of the material, and of every asset it references (physical, appearance), with the path to each.</summary>
    public static IReadOnlyList<MaterialPropertyRow> Dump(Inv.Asset material, string label)
    {
        var rows = new List<MaterialPropertyRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        DumpAsset(material, label, rows, seen, 0);
        return rows;
    }

    private static void DumpAsset(Inv.Asset asset, string path, List<MaterialPropertyRow> rows, HashSet<string> seen, int depth)
    {
        string key;
        try { key = asset.Name + "|" + asset.AssetType; }
        catch (COMException) { return; }
        if (!seen.Add(key) || rows.Count >= MaxRows) return;
        foreach (Inv.AssetValue v in asset)
        {
            if (rows.Count >= MaxRows) return;
            string name = "", display = "";
            try { name = v.Name; display = v.DisplayName; } catch (COMException) { }
            var (text, units) = ValueText(v);
            rows.Add(new MaterialPropertyRow(path, name, display, TypeName(v), units, text));
            if (depth < MaxDepth && v.ValueType == Inv.AssetValueTypeEnum.kAssetValueTypeReference)
                foreach (var target in Referenced((Inv.ReferenceAssetValue)v))
                    DumpAsset(target, $"{path} > {name} > {SafeName(target)}", rows, seen, depth + 1);
        }
    }

    /// <summary>Finds an asset value by its name on the material or, failing that, on the assets it references (breadth first, cycle safe).</summary>
    public static Inv.AssetValue? Resolve(Inv.Asset material, string valueName)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(Inv.Asset Asset, int Depth)>();
        queue.Enqueue((material, 0));
        while (queue.Count > 0)
        {
            var (asset, depth) = queue.Dequeue();
            string key;
            try { key = asset.Name + "|" + asset.AssetType; }
            catch (COMException) { continue; }
            if (!seen.Add(key)) continue;
            foreach (Inv.AssetValue v in asset)
            {
                string n;
                try { n = v.Name; } catch (COMException) { continue; }
                if (string.Equals(n, valueName, StringComparison.Ordinal)) return v;
                if (depth < MaxDepth && v.ValueType == Inv.AssetValueTypeEnum.kAssetValueTypeReference)
                    foreach (var t in Referenced((Inv.ReferenceAssetValue)v)) queue.Enqueue((t, depth + 1));
            }
        }
        return null;
    }

    // ---- Building the material ----

    /// <summary>The material asset in its temporary hidden part document. Dispose closes the document without saving.</summary>
    public sealed class TempMaterial : IDisposable
    {
        private readonly Inv.Application _app;
        private readonly bool _silent;
        private Inv.PartDocument? _doc;
        public Inv.Asset Material { get; }
        public List<string> Warnings { get; } = new();

        internal TempMaterial(Inv.Application app, Inv.PartDocument doc, Inv.Asset material, bool silent)
        {
            _app = app; _doc = doc; Material = material; _silent = silent;
        }

        public void Dispose()
        {
            try { _doc?.Close(true); } catch (COMException) { }
            _doc = null;
            try { _app.SilentOperation = _silent; } catch (COMException) { }
        }
    }

    /// <summary>Creates the material in a hidden part document and sets description, class and every value. Nothing outside the temporary
    /// document is touched. Throws DataCardStepException("build") naming what could not be set.</summary>
    public static TempMaterial Build(Inv.Application app, MaterialSpec spec)
    {
        var silent = app.SilentOperation;
        app.SilentOperation = true;
        Inv.PartDocument? doc = null;
        try
        {
            try { doc = (Inv.PartDocument)app.Documents.Add(Inv.DocumentTypeEnum.kPartDocumentObject, "", false); }
            catch (COMException ex) { throw new DataCardStepException("build", "Inventor could not create the temporary part document: " + ex.Message); }
            var internalName = "SF_" + Guid.NewGuid().ToString("N");
            Inv.Asset material;
            try { material = doc.Assets.Add(Inv.AssetTypeEnum.kAssetTypeMaterial, "Generic", internalName, spec.Name); }
            catch (COMException ex) { throw new DataCardStepException("build", "Inventor refused to create the material asset (Assets.Add kAssetTypeMaterial \"Generic\"): " + ex.Message); }

            var temp = new TempMaterial(app, doc, material, silent);
            var tempDoc = doc;
            doc = null;
            try
            {
                Fill(app, temp, tempDoc, spec, internalName);
                return temp;
            }
            catch (Exception)
            {
                temp.Dispose();
                throw;
            }
        }
        catch (Exception)
        {
            try { doc?.Close(true); } catch (COMException) { }
            try { app.SilentOperation = silent; } catch (COMException) { }
            throw;
        }
    }

    private static void Fill(Inv.Application app, TempMaterial temp, Inv.PartDocument doc, MaterialSpec spec, string internalName)
    {
        var material = temp.Material;
        EnsurePhysicalAspect(doc, material, spec, internalName, spec.Values.Select(v => v.AssetName).ToList());

        if (spec.Description.Length > 0)
        {
            if (Resolve(material, "description") is Inv.StringAssetValue d && !d.IsReadOnly)
            {
                try { d.Value = spec.Description; }
                catch (COMException ex) { temp.Warnings.Add("description not set: " + ex.Message); }
            }
            else temp.Warnings.Add("description not set: the material has no writable 'description' value.");
        }
        if (spec.MaterialClass is not null) SetClass(material, spec.MaterialClass, temp.Warnings);

        var problems = new List<string>();
        foreach (var v in spec.Values)
        {
            var av = Resolve(material, v.AssetName);
            if (av is null) { problems.Add($"{v.AssetName} (not on the new material)"); continue; }
            if (av.ValueType != Inv.AssetValueTypeEnum.kAssetValueTypeFloat) { problems.Add($"{v.AssetName} (is {TypeName(av)}, not Float)"); continue; }
            var f = (Inv.FloatAssetValue)av;
            if (f.IsReadOnly) { problems.Add($"{v.AssetName} (read-only)"); continue; }
            var target = ValueInUnitsOf(v, SafeUnits(f), temp.Warnings);
            try { f.Value = target; }
            catch (COMException ex) { problems.Add($"{v.AssetName} (Inventor refused {Num(target)}: {ex.Message})"); continue; }
            var back = f.Value;
            if (!MaterialRules.SameNumber(back, target)) problems.Add($"{v.AssetName} (wrote {Num(target)}, Inventor reads {Num(back)})");
        }
        if (problems.Count > 0)
            throw new DataCardStepException("build", "could not set " + string.Join("; ", problems) + ". Nothing was changed in Vault. Run list_material_properties on an existing material and compare the names.");

        AddMatteAppearance(app, doc, material, temp.Warnings);
    }

    /// <summary>The number to write: the value in the unit Inventor reports for that asset value (usually the schema unit). An unrecognised unit
    /// text writes the schema unit and adds a warning.</summary>
    internal static double ValueInUnitsOf(MaterialValue v, string reportedUnits, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(reportedUnits) || MaterialUnits.Normalize(reportedUnits) == MaterialUnits.Normalize(v.AssetUnit)) return v.AssetValue;
        if (MaterialUnits.TryConvert(v.Quantity, v.AssetValue, v.AssetUnit, reportedUnits, out var converted)) return converted;
        warnings.Add($"{v.AssetName}: Inventor reports the unit '{reportedUnits}', which is not recognised; wrote {Num(v.AssetValue)} {v.AssetUnit}.");
        return v.AssetValue;
    }

    // A new material asset has to reference a physical (structural) asset holding the values. Use it when it is already there; otherwise create one
    // and add it to the material's physmat_aspects reference.
    private static void EnsurePhysicalAspect(Inv.PartDocument doc, Inv.Asset material, MaterialSpec spec, string internalName, IReadOnlyList<string> needed)
    {
        if (needed.Count == 0 || Resolve(material, needed[0]) is not null) return;
        if (Resolve(material, "physmat_aspects") is not Inv.ReferenceAssetValue aspects || aspects.IsReadOnly)
            throw new DataCardStepException("build", $"the new material has no {needed[0]} and no writable physmat_aspects reference to add a physical asset to.");
        Inv.Asset phys;
        try { phys = doc.Assets.Add(Inv.AssetTypeEnum.kAssetTypePhysicalProperties, "Generic", internalName + "_phys", spec.Name); }
        catch (COMException ex) { throw new DataCardStepException("build", "Inventor refused to create the physical asset (Assets.Add kAssetTypePhysicalProperties \"Generic\"): " + ex.Message); }
        AddAspect(aspects, phys);
    }

    private static void AddAspect(Inv.ReferenceAssetValue aspects, Inv.Asset asset)
    {
        var all = Referenced(aspects).ToList();
        all.Add(asset);
        var arr = all.ToArray();
        aspects.set_Values(ref arr);
    }

    private static void SetClass(Inv.Asset material, string label, List<string> warnings)
    {
        var id = MaterialRules.MaterialClasses[label];
        try
        {
            if (Resolve(material, "physmat_Type") is not Inv.ChoiceAssetValue choice || choice.IsReadOnly)
            { warnings.Add("material class not set: no writable physmat_Type value."); return; }
            foreach (var candidate in new[] { id, label })
            {
                try
                {
                    choice.Value = candidate;
                    var now = choice.Value ?? "";
                    if (string.Equals(now, id, StringComparison.OrdinalIgnoreCase) || string.Equals(now, label, StringComparison.OrdinalIgnoreCase)) return;
                }
                catch (COMException) { }
            }
            warnings.Add($"material class not set: Inventor would not take '{id}' or '{label}'.");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException) { warnings.Add("material class not set: " + ex.Message); }
    }

    // Best effort: copy a matte plastic appearance from the active appearance library and add it to the material's aspects. A failure leaves the
    // default appearance and a warning; it never stops the run.
    private static void AddMatteAppearance(Inv.Application app, Inv.PartDocument doc, Inv.Asset material, List<string> warnings)
    {
        try
        {
            if (Resolve(material, "physmat_aspects") is not Inv.ReferenceAssetValue aspects || aspects.IsReadOnly)
            { warnings.Add("appearance left at the default: no writable physmat_aspects reference."); return; }
            if (Referenced(aspects).Any(a => SafeIsAppearance(a))) return;
            var template = FindMatteAppearance(app);
            if (template is null) { warnings.Add("appearance left at the default: no matte plastic appearance in the active appearance library."); return; }
            var copy = template.CopyTo(doc, true);
            AddAspect(aspects, copy);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            warnings.Add("appearance left at the default: " + ex.Message);
        }
    }

    private static bool SafeIsAppearance(Inv.Asset a)
    {
        try { return a.AssetType == Inv.AssetTypeEnum.kAssetTypeAppearance; }
        catch (COMException) { return false; }
    }

    private static Inv.Asset? FindMatteAppearance(Inv.Application app)
    {
        var rx = new System.Text.RegularExpressions.Regex(@"plastic.*matte|matte.*plastic", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var lib = app.ActiveAppearanceLibrary;
        if (lib is null) return null;
        foreach (Inv.Asset a in lib.AppearanceAssets)
        {
            try { if (rx.IsMatch(a.DisplayName ?? "")) return a; }
            catch (COMException) { }
        }
        return null;
    }

    // ---- Writing and checking the library ----

    /// <summary>Copies the material (with the physical and appearance assets it references) into the library. A material of that name is never replaced.</summary>
    public static void CopyInto(Inv.Asset material, Inv.AssetLibrary library) => material.CopyTo(library, false);

    /// <summary>Null when the library has the material with every value as built; otherwise what is wrong.</summary>
    public static string? Verify(Inv.AssetLibrary lib, MaterialSpec spec)
    {
        var asset = FindMaterial(lib, spec.Name);
        if (asset is null) return $"{spec.Name} is not in the library.";
        var problems = new List<string>();
        foreach (var v in spec.Values)
        {
            var av = Resolve(asset, v.AssetName);
            if (av is not Inv.FloatAssetValue f) { problems.Add($"{v.AssetName} missing"); continue; }
            double expected;
            try { expected = ValueInUnitsOf(v, SafeUnits(f), new List<string>()); }
            catch (COMException) { continue; }
            double got;
            try { got = f.Value; } catch (COMException ex) { problems.Add($"{v.AssetName} unreadable ({ex.Message})"); continue; }
            if (!MaterialRules.SameNumber(got, expected)) problems.Add($"{v.AssetName} is {Num(got)}, expected {Num(expected)}");
        }
        return problems.Count == 0 ? null : string.Join("; ", problems);
    }
}
