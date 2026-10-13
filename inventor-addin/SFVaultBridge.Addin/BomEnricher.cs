using SFVaultBridge.Core;

namespace SFVaultBridge.Addin;

/// <summary>Overlays each BOM line with its Vault facts (category-driven structure, state, revision). One lookup per unique file path.</summary>
internal static class BomEnricher
{
    private sealed record Facts(string? Category, string State, string Revision);

    /// <summary>Set after the first connection-class failure; later lookups are skipped so a dead session cannot slow the click.</summary>
    private sealed class Session { public bool Down; }

    public static IReadOnlyList<BomNode> Apply(IReadOnlyList<BomNode> roots, string workingRoot, Action<string> log,
                                                  IDictionary<string, string?>? categories = null)
    {
        var cache = new Dictionary<string, Facts?>(StringComparer.OrdinalIgnoreCase);
        return Walk(roots, workingRoot, cache, new Session(), log, categories);
    }

    private static IReadOnlyList<BomNode> Walk(IReadOnlyList<BomNode> nodes, string root, Dictionary<string, Facts?> cache, Session session, Action<string> log, IDictionary<string, string?>? categories) =>
        nodes.Select(n =>
        {
            var children = n.Children.Count == 0 ? n.Children : Walk(n.Children, root, cache, session, log, categories);
            var facts = Lookup(n, root, cache, session, log, categories);
            var updated = facts is null ? n : SourceRules.Apply(n, facts.Category, facts.State, facts.Revision);
            return updated with { Children = children };
        }).ToList();

    private static Facts? Lookup(BomNode n, string root, Dictionary<string, Facts?> cache, Session session, Action<string> log, IDictionary<string, string?>? categories)
    {
        if (string.IsNullOrWhiteSpace(n.FullPath) || string.IsNullOrWhiteSpace(n.FileName)) return null;
        if (cache.TryGetValue(n.FullPath, out var hit)) return hit;
        if (session.Down) return null;
        Facts? facts = null;
        try
        {
            var f = VaultFiles.FindLatest(VaultPaths.ToVaultFolder(n.FullPath, root) + "/" + n.FileName);
            if (f is not null) facts = new Facts(VaultFiles.CategoryName(f), VaultFiles.StateOf(f), VaultFiles.RevisionOf(f));
        }
        catch (Exception ex)
        {
            if (IsConnectionFailure(ex))
            {
                session.Down = true;
                log($"BOM Vault lookup for {n.PartNumber} failed ({ex.Message}); skipping the remaining Vault lookups.");
            }
            else log($"BOM Vault lookup for {n.PartNumber} failed: {ex.Message}");
        }
        cache[n.FullPath] = facts;
        if (categories is not null) categories[n.FullPath] = facts?.Category;
        return facts;
    }

    /// <summary>Not "file not found": anything that means Vault itself is unreachable or the user is not signed in.</summary>
    private static bool IsConnectionFailure(Exception ex) =>
        ex is not BridgeException b || b.Code != "not_found";
}
