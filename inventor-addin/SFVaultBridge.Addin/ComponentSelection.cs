using Inv = Inventor;

namespace SFVaultBridge.Addin;

/// <summary>The components selected in an assembly (SelectSet): occurrences and proxies (picks inside a sub-assembly). Shared by Fix Data Card and Partner Package.</summary>
internal static class ComponentSelection
{
    /// <summary>The file of each selected component occurrence (null when it has none, for example a virtual component). Other selected items are ignored.</summary>
    public static IReadOnlyList<string?> Files(Inv.Document doc, Action<string> log)
    {
        var list = new List<string?>();
        var set = doc.SelectSet;
        for (var i = 1; i <= set.Count; i++)
        {
            if (!TryComponentFile(set[i], log, out var file)) continue;
            list.Add(file);
        }
        return list;
    }

    /// <summary>True for a component occurrence or a proxy of one (a pick inside a sub-assembly); file is null when it has none (virtual).</summary>
    public static bool TryComponentFile(object item, Action<string> log, out string? file)
    {
        file = null;
        var isComponent = item is Inv.ComponentOccurrenceProxy or Inv.ComponentOccurrence;
        if (!isComponent) return false;
        try
        {
            if (item is Inv.ComponentOccurrenceProxy proxy)
            {
                try { file = proxy.NativeObject.ReferencedDocumentDescriptor.FullDocumentName; }
                catch (Exception) { file = ((Inv.Document)proxy.Definition.Document).FullFileName; }
            }
            else file = ((Inv.ComponentOccurrence)item).ReferencedDocumentDescriptor.FullDocumentName;
        }
        catch (Exception ex) { log("selected component has no file: " + ex.Message); file = null; }
        return true;
    }
}
