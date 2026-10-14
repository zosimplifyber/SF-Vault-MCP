using System.Text;

namespace SFVaultBridge.Core;

/// <summary>
/// McMaster-Carr's API terms forbid giving McMaster CAD to suppliers or using it for 3D printing or fabrication.
/// A part is McMaster CAD when its Vendor is McMaster or its Vault path is under $/DESIGNS/Simplifyber Library/McMaster-Carr/.
/// </summary>
public static class McMasterRule
{
    public const string LeftOutHeading = "Left out: McMaster-Carr CAD (supplier terms)";
    public const string BomNote = "Buy from McMaster-Carr; CAD not supplied";
    public const string AssemblyStepLeftOut = "assembly STEP left out: contains McMaster-Carr CAD";
    public const string LibraryFolder = "/simplifyber library/mcmaster-carr/";

    /// <summary>"McMaster-Carr", "McMASTER-CARR", "McMaster Carr" and "McMaster" all match: case, spaces and punctuation are ignored.</summary>
    public static bool IsMcMasterVendor(string? vendor)
    {
        if (string.IsNullOrWhiteSpace(vendor)) return false;
        var sb = new StringBuilder();
        foreach (var ch in vendor) if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString().StartsWith("mcmaster", StringComparison.Ordinal);
    }

    /// <summary>True for a Vault path ($/DESIGNS/Simplifyber Library/McMaster-Carr/...) or its local workspace path.</summary>
    public static bool IsMcMasterPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var p = path.Trim().Replace('\\', '/');
        if (!p.EndsWith("/", StringComparison.Ordinal)) p += "/";
        return p.Contains(LibraryFolder, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsMcMasterCad(string? vendor, string? path) => IsMcMasterVendor(vendor) || IsMcMasterPath(path);

    public static bool IsMcMasterCad(BomNode node) => IsMcMasterCad(node.Vendor, node.FullPath);

    /// <summary>"Left out: McMaster-Carr CAD (supplier terms): A, B"; empty when nothing was left out.</summary>
    public static string LeftOutText(IEnumerable<string> partNumbers)
    {
        var list = partNumbers.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return list.Count == 0 ? "" : $"{LeftOutHeading}: {string.Join(", ", list)}";
    }

    /// <summary>
    /// The McMaster part names found in an exported STEP (empty when clean). Only PRODUCT and assembly-usage lines are read.
    /// Used as a last check on an assembly STEP exported with McMaster components suppressed.
    /// </summary>
    public static IReadOnlyList<string> FindLeaks(IEnumerable<string> stepLines, IEnumerable<string> mcmasterStems)
    {
        var stems = mcmasterStems.Select(s => (s ?? "").Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var found = new List<string>();
        if (stems.Count == 0) return found;
        foreach (var line in stepLines)
        {
            if (line.IndexOf("PRODUCT(", StringComparison.OrdinalIgnoreCase) < 0
                && line.IndexOf("NEXT_ASSEMBLY_USAGE_OCCURRENCE", StringComparison.OrdinalIgnoreCase) < 0) continue;
            foreach (var s in stems)
                if (line.Contains(s, StringComparison.OrdinalIgnoreCase) && !found.Contains(s, StringComparer.OrdinalIgnoreCase)) found.Add(s);
        }
        return found;
    }
}
