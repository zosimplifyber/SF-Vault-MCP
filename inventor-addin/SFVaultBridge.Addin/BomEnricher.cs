using SFVaultBridge.Core;

namespace SFVaultBridge.Addin;

/// <summary>Overlays each BOM line with its Vault facts (category-driven structure, state, revision). One lookup per unique file path.</summary>
internal static class BomEnricher
{
    private sealed record Facts(string? Category, string State, string Revision);

    public static IReadOnlyList<BomNode> Apply(IReadOnlyList<BomNode> roots, string workingRoot, Action<string> log)
    {
        var cache = new Dictionary<string, Facts?>(StringComparer.OrdinalIgnoreCase);
        return Walk(roots, workingRoot, cache, log);
    }

    private static IReadOnlyList<BomNode> Walk(IReadOnlyList<BomNode> nodes, string root, Dictionary<string, Facts?> cache, Action<string> log) =>
        nodes.Select(n =>
        {
            var children = n.Children.Count == 0 ? n.Children : Walk(n.Children, root, cache, log);
            var facts = Lookup(n, root, cache, log);
            var updated = facts is null ? n : SourceRules.Apply(n, facts.Category, facts.State, facts.Revision);
            return updated with { Children = children };
        }).ToList();

    private static Facts? Lookup(BomNode n, string root, Dictionary<string, Facts?> cache, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(n.FullPath) || string.IsNullOrWhiteSpace(n.FileName)) return null;
        if (cache.TryGetValue(n.FullPath, out var hit)) return hit;
        Facts? facts = null;
        try
        {
            var f = VaultFiles.FindLatest(VaultPaths.ToVaultFolder(n.FullPath, root) + "/" + n.FileName);
            if (f is not null) facts = new Facts(VaultFiles.CategoryName(f), VaultFiles.StateOf(f), VaultFiles.RevisionOf(f));
        }
        catch (Exception ex) { log($"BOM Vault lookup for {n.PartNumber} failed: {ex.Message}"); }
        cache[n.FullPath] = facts;
        return facts;
    }
}
