using SFVaultBridge.Core;
using Inv = Inventor;

namespace SFVaultBridge.Addin;

/// <summary>Structured, all-levels BOM of the open assembly as a BomNode tree (unsaved edits included).</summary>
internal static class BomReader
{
    public static IReadOnlyList<BomNode> Read(Inv.AssemblyDocument asm)
    {
        var bom = asm.ComponentDefinition.BOM;
        bom.StructuredViewEnabled = true;
        bom.StructuredViewFirstLevelOnly = false;
        Inv.BOMView? view = null;
        foreach (Inv.BOMView v in bom.BOMViews)
            if (v.ViewType == Inv.BOMViewTypeEnum.kStructuredBOMViewType) { view = v; break; }
        if (view is null) throw new BridgeException("no_bom", "The assembly has no structured BOM view.");
        return Rows(view.BOMRows);
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
        var def = row.ComponentDefinitions[1];
        var doc = def.Document as Inv.Document;
        string Prop(string set, string name)
        {
            try { return Convert.ToString(doc?.PropertySets[set][name].Value) ?? ""; } catch { return ""; }
        }
        var fullPath = doc?.FullFileName ?? "";
        var fileName = fullPath.Length > 0 ? System.IO.Path.GetFileName(fullPath) : "";
        var partNumber = Prop("Design Tracking Properties", "Part Number");
        if (string.IsNullOrWhiteSpace(partNumber)) partNumber = System.IO.Path.GetFileNameWithoutExtension(fileName);
        double qty;
        try { qty = Convert.ToDouble(row.ItemQuantity, System.Globalization.CultureInfo.InvariantCulture); } catch { qty = 0; }
        return new BomNode(
            partNumber,
            Prop("Design Tracking Properties", "Description"),
            qty,
            "Each",
            Structure(row.BOMStructure),
            Prop("Inventor Summary Information", "Revision Number"),
            Prop("Design Tracking Properties", "Material"),
            fileName,
            fullPath,
            Rows(row.ChildRows));
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
