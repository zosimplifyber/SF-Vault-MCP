using SFVaultBridge.Core;
using Inv = Inventor;

namespace SFVaultBridge.Addin;

/// <summary>Structured, all-levels BOM of the open assembly as a BomNode tree (unsaved edits included).</summary>
internal static class BomReader
{
    public static IReadOnlyList<BomNode> Read(Inv.AssemblyDocument asm)
    {
        var bom = asm.ComponentDefinition.BOM;
        var wasDirty = asm.Dirty;
        var oldEnabled = bom.StructuredViewEnabled;
        var oldFirstLevel = bom.StructuredViewFirstLevelOnly;
        var changedEnabled = false;
        var changedFirstLevel = false;
        try
        {
            if (!oldEnabled) { bom.StructuredViewEnabled = true; changedEnabled = true; }
            if (oldFirstLevel) { bom.StructuredViewFirstLevelOnly = false; changedFirstLevel = true; }
            Inv.BOMView? view = null;
            foreach (Inv.BOMView v in bom.BOMViews)
                if (v.ViewType == Inv.BOMViewTypeEnum.kStructuredBOMViewType) { view = v; break; }
            if (view is null) throw new BridgeException("no_bom", "The assembly has no structured BOM view.");
            return Rows(view.BOMRows);
        }
        finally
        {
            if (changedFirstLevel) { try { bom.StructuredViewFirstLevelOnly = oldFirstLevel; } catch { } }
            if (changedEnabled) { try { bom.StructuredViewEnabled = oldEnabled; } catch { } }
            try { if (!wasDirty && asm.Dirty) asm.Dirty = false; } catch { }
        }
    }

    /// <summary>The "Model state / Design view / BOM view" line for the purchasing sheet; "(none)" where Inventor has no such thing.</summary>
    public static string SourceLine(Inv.AssemblyDocument asm)
    {
        string? modelState = null, designView = null;
        try { modelState = asm.ComponentDefinition.ModelStates.ActiveModelState.Name; } catch { }
        try { designView = asm.ComponentDefinition.RepresentationsManager.ActiveDesignViewRepresentation.Name; } catch { }
        return BomSource.Describe(modelState, designView);
    }

    private const string SheetMetalSubType = "{9C464203-9BAE-11D3-8BAD-0060B0CE6BB4}";

    /// <summary>The assembly itself as a childless BOM node (for the package's top-level drawing and quote).</summary>
    internal static BomNode TopNode(Inv.AssemblyDocument asm, string revision, string state)
    {
        string Prop(string set, string name)
        {
            try { return Convert.ToString(asm.PropertySets[set][name].Value) ?? ""; } catch { return ""; }
        }
        var fullPath = "";
        try { fullPath = asm.FullFileName ?? ""; } catch { }
        var fileName = fullPath.Length > 0 ? System.IO.Path.GetFileName(fullPath) : "";
        var stem = fullPath.Length > 0 ? System.IO.Path.GetFileNameWithoutExtension(fullPath) : "";
        return new BomNode(stem, Prop("Design Tracking Properties", "Description"), 1, "Each", "Normal", revision,
            Prop("Design Tracking Properties", "Material"), fileName, fullPath, Array.Empty<BomNode>(), State: state,
            Process: Prop("Inventor User Defined Properties", "Process"),
            MaterialFinish: Prop("Inventor User Defined Properties", "Material Finish"));
    }

    /// <summary>The open part as a childless BOM row of quantity 1 (unsaved edits included), read the same way a BOM row is.</summary>
    internal static BomNode PartNode(Inv.PartDocument part)
    {
        string Prop(string set, string name)
        {
            try { return Convert.ToString(part.PropertySets[set][name].Value) ?? ""; } catch { return ""; }
        }
        var fullPath = "";
        try { fullPath = part.FullFileName ?? ""; } catch { }
        var fileName = fullPath.Length > 0 ? System.IO.Path.GetFileName(fullPath) : "";
        var partNumber = Prop("Design Tracking Properties", "Part Number");
        if (string.IsNullOrWhiteSpace(partNumber)) partNumber = fileName.Length > 0 ? System.IO.Path.GetFileNameWithoutExtension(fileName) : "";
        var isSheetMetal = false;
        try { isSheetMetal = string.Equals(part.SubType, SheetMetalSubType, StringComparison.OrdinalIgnoreCase); } catch { }
        var structure = "Normal";
        try { structure = Structure(part.ComponentDefinition.BOMStructure); } catch { }
        double? cost = null;
        try { cost = ParseCost(part.PropertySets["Design Tracking Properties"]["Cost"].Value); } catch { }
        return new BomNode(partNumber, Prop("Design Tracking Properties", "Description"), 1, "Each", structure,
            Prop("Inventor Summary Information", "Revision Number"), Prop("Design Tracking Properties", "Material"), fileName, fullPath,
            Array.Empty<BomNode>(), Prop("Design Tracking Properties", "Vendor"),
            SourceRules.VendorNumber(Prop("Design Tracking Properties", "Catalog Web Link"), Prop("Inventor User Defined Properties", "Vendor Number")),
            Process: Prop("Inventor User Defined Properties", "Process"), MaterialFinish: Prop("Inventor User Defined Properties", "Material Finish"),
            IsSheetMetal: isSheetMetal, Cost: cost, HsCode: Prop("Inventor User Defined Properties", "HS/HTS Code").Trim());
    }

    private static IReadOnlyList<BomNode> Rows(Inv.BOMRowsEnumerator? rows)
    {
        var list = new List<BomNode>();
        if (rows is null) return list;
        foreach (Inv.BOMRow row in rows) list.Add(Node(row));
        return list;
    }

    private static BomNode Node(Inv.BOMRow row)
    {
        string process = "", finish = "";
        var isSheetMetal = false;
        double? cost = null;
        string hsCode = "";
        string partNumber = "", description = "", revision = "", material = "", fileName = "", fullPath = "", vendor = "", vendorNumber = "";
        double qty;
        try { qty = Convert.ToDouble(row.ItemQuantity, System.Globalization.CultureInfo.InvariantCulture); } catch { qty = 0; }
        var structure = "Normal";
        try { structure = Structure(row.BOMStructure); } catch { }
        try
        {
            var def = row.ComponentDefinitions[1];
            Inv.PropertySets? sets = null;
            if (def is Inv.VirtualComponentDefinition virt)
            {
                try { sets = virt.PropertySets; } catch { }
            }
            else
            {
                var doc = def.Document as Inv.Document;
                try { fullPath = doc?.FullFileName ?? ""; } catch { }
                fileName = fullPath.Length > 0 ? System.IO.Path.GetFileName(fullPath) : "";
                try { sets = doc?.PropertySets; } catch { }
                try { isSheetMetal = doc is Inv.PartDocument pd && string.Equals(pd.SubType, SheetMetalSubType, StringComparison.OrdinalIgnoreCase); } catch { isSheetMetal = false; }
            }
            string Prop(string set, string name)
            {
                try { return Convert.ToString(sets?[set][name].Value) ?? ""; } catch { return ""; }
            }
            var pn = Prop("Design Tracking Properties", "Part Number");
            if (!string.IsNullOrWhiteSpace(pn)) partNumber = pn;
            description = Prop("Design Tracking Properties", "Description");
            revision = Prop("Inventor Summary Information", "Revision Number");
            material = Prop("Design Tracking Properties", "Material");
            vendor = Prop("Design Tracking Properties", "Vendor");
            process = Prop("Inventor User Defined Properties", "Process");
            finish = Prop("Inventor User Defined Properties", "Material Finish");
            hsCode = Prop("Inventor User Defined Properties", "HS/HTS Code").Trim();
            try { cost = ParseCost(sets?["Design Tracking Properties"]["Cost"].Value); } catch { cost = null; }
            vendorNumber = SourceRules.VendorNumber(Prop("Design Tracking Properties", "Catalog Web Link"), Prop("Inventor User Defined Properties", "Vendor Number"));
        }
        catch { }
        if (string.IsNullOrWhiteSpace(partNumber))
        {
            if (fileName.Length > 0) partNumber = System.IO.Path.GetFileNameWithoutExtension(fileName);
            else try { partNumber = ((dynamic)row.ComponentDefinitions[1]).DisplayName ?? ""; } catch { }
        }
        IReadOnlyList<BomNode> children = Array.Empty<BomNode>();
        try { children = Rows(row.ChildRows); }
        catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"BOM child rows of {partNumber} could not be read: {ex.Message}"); }
        return new BomNode(partNumber, description, qty, "Each", structure, revision, material, fileName, fullPath, children, vendor, vendorNumber, Process: process, MaterialFinish: finish, IsSheetMetal: isSheetMetal, Cost: cost, HsCode: hsCode);
    }

    /// <summary>Estimated Cost from COM (double, decimal or string); 0, negative, blank or unparseable is no cost.</summary>
    internal static double? ParseCost(object? v)
    {
        double d;
        switch (v)
        {
            case null: return null;
            case string s:
                if (!double.TryParse(s.Trim().TrimStart('$'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d)) return null;
                break;
            default:
                try { d = Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture); } catch { return null; }
                break;
        }
        return d > 0 && !double.IsNaN(d) && !double.IsInfinity(d) ? d : null;
    }

    private static string Structure(Inv.BOMStructureEnum s) => s switch
    {
        Inv.BOMStructureEnum.kPurchasedBOMStructure => "Purchased",
        Inv.BOMStructureEnum.kPhantomBOMStructure => "Phantom",
        Inv.BOMStructureEnum.kReferenceBOMStructure => "Reference",
        Inv.BOMStructureEnum.kInseparableBOMStructure => "Inseparable",
        _ => "Normal",
    };
}
