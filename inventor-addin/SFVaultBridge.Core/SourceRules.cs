namespace SFVaultBridge.Core;

/// <summary>Rules that turn a part's Vault facts into the values the purchasing sheet shows.</summary>
public static class SourceRules
{
    /// <summary>True for the Vault categories that mean the part is bought, not made.</summary>
    public static bool IsBuyCategory(string? category) =>
        string.Equals(category?.Trim(), "Part - Purchased", StringComparison.OrdinalIgnoreCase)
        || string.Equals(category?.Trim(), "Part - Content Center", StringComparison.OrdinalIgnoreCase);

    /// <summary>"Purchased" when the category is a buy category (Phantom and Reference keep their meaning); otherwise the BOM Structure unchanged.</summary>
    public static string EffectiveStructure(string structure, string? category)
    {
        if (!IsBuyCategory(category)) return structure;
        return string.Equals(structure, "Phantom", StringComparison.OrdinalIgnoreCase)
            || string.Equals(structure, "Reference", StringComparison.OrdinalIgnoreCase) ? structure : "Purchased";
    }

    /// <summary>Catalog Web Link first; the custom Vendor Number iProperty only when the link is empty.</summary>
    public static string VendorNumber(string? webLink, string? custom) =>
        !string.IsNullOrWhiteSpace(webLink) ? webLink.Trim() : (custom ?? "").Trim();

    /// <summary>Vault revision and state win; the iProperty revision stays when Vault has none.</summary>
    public static BomNode Apply(BomNode node, string? category, string? vaultState, string? vaultRevision) => node with
    {
        BomStructure = EffectiveStructure(node.BomStructure, category),
        State = string.IsNullOrWhiteSpace(vaultState) ? node.State : vaultState.Trim(),
        Revision = string.IsNullOrWhiteSpace(vaultRevision) ? node.Revision : vaultRevision.Trim(),
    };
}
