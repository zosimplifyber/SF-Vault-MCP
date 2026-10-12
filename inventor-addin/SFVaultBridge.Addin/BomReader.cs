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

    private static IReadOnlyList<BomNode> Rows(Inv.BOMRowsEnumerator? rows)
    {
        var list = new List<BomNode>();
        if (rows is null) return list;
        foreach (Inv.BOMRow row in rows) list.Add(Node(row));
        return list;
    }

    private static BomNode Node(Inv.BOMRow row)
    {
        string partNumber = "", description = "", revision = "", material = "", fileName = "", fullPath = "";
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
        return new BomNode(partNumber, description, qty, "Each", structure, revision, material, fileName, fullPath, children);
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
