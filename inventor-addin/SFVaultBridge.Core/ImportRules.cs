using System.Text.RegularExpressions;

namespace SFVaultBridge.Core;

public sealed record Prefill(string? Vendor, string VendorNumber, string Description);

/// <summary>Best-effort form prefill from vendor file names. Never throws.</summary>
public static class VendorFileNames
{
    private static readonly Regex McMaster = new(@"^(?<pn>\d{3,5}[A-Z]\d{1,4})(?:_(?<desc>.+))?$", RegexOptions.IgnoreCase);

    public static Prefill FromFiles(string stepPath, string? pdfPath)
    {
        var step = Parse(Path.GetFileNameWithoutExtension(stepPath ?? ""));
        if (pdfPath is null || step.Description.Length > 0) return step;
        var pdf = Parse(Path.GetFileNameWithoutExtension(pdfPath));
        return step with { Description = pdf.Description, VendorNumber = step.VendorNumber.Length > 0 ? step.VendorNumber : pdf.VendorNumber };
    }

    private static Prefill Parse(string stem)
    {
        stem = (stem ?? "").Trim();
        var m = McMaster.Match(stem);
        if (m.Success) return new Prefill("McMaster-Carr", m.Groups["pn"].Value.ToUpperInvariant(), m.Groups["desc"].Value.Trim());
        var i = stem.IndexOf('_');
        return i > 0 ? new Prefill(null, stem[..i].Trim(), stem[(i + 1)..].Trim()) : new Prefill(null, "", stem);
    }
}

public sealed record SelectionResult(string? Step, string? Pdf, string? Error);

public static class SelectionCheck
{
    public static SelectionResult Check(IEnumerable<string> paths)
    {
        var list = (paths ?? Array.Empty<string>()).ToList();
        var steps = list.Where(p => p.EndsWith(".stp", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".step", StringComparison.OrdinalIgnoreCase)).ToList();
        var pdfs = list.Where(p => p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)).ToList();
        if (steps.Count == 1 && pdfs.Count == 1) return new SelectionResult(steps[0], pdfs[0], null);
        if (steps.Count > 1 || pdfs.Count > 1) return new SelectionResult(null, null, "Select exactly one STEP file and one PDF.");
        if (steps.Count == 0 && pdfs.Count == 0) return new SelectionResult(null, null, "Select the vendor's STEP file and its PDF.");
        return new SelectionResult(null, null, steps.Count == 0 ? "Also select the STEP file." : "Also select the PDF.");
    }
}

/// <summary>EstimatedCost (per piece) and PackSize come from a McMaster Fetch; null leaves the Estimated Cost and Pack Size iProperties alone.</summary>
public sealed record ImportPlan(string Vendor, string Subfolder, string VendorNumber, string Description, string Material, string MaterialFinish, string Designer, string Comments = "",
                                double? EstimatedCost = null, int? PackSize = null);

public static class ImportRules
{
    public const string LibraryRoot = "$/DESIGNS/Simplifyber Library";
    private static readonly char[] Bad = { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };

    public static bool IsValidName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name == name.Trim() && !name.EndsWith('.') && name.IndexOfAny(Bad) < 0 && !name.Any(char.IsControl);

    public const string VendorFolderLabel = "(vendor folder)";

    /// <summary>Trims and removes control characters (data-card text).</summary>
    public static string CleanText(string? s) => new string((s ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();

    /// <summary>True when a latest file name is the CD number's own file ("CD-000123.ipt") or starts with "CD-000123 " (its PDF).</summary>
    public static bool IsNumberClash(string fileName, string cd) =>
        (fileName ?? "").StartsWith(cd + ".", StringComparison.OrdinalIgnoreCase) || (fileName ?? "").StartsWith(cd + " ", StringComparison.OrdinalIgnoreCase);

    public static string NumberKey(string? vendorNumber) => Regex.Replace(vendorNumber ?? "", @"\s+", "").ToUpperInvariant();

    /// <summary>
    /// Duplicate rule: Vendor Number keys match AND (vendor keys match, or the file's Vendor is blank, or the file sits under the chosen vendor's Library folder).
    /// </summary>
    public static bool IsDuplicate(string planVendor, string planNumber, string? fileVendor, string? fileNumber, string? fileFolderPath)
    {
        if (NumberKey(fileNumber) != NumberKey(planNumber) || NumberKey(planNumber).Length == 0) return false;
        if (string.IsNullOrWhiteSpace(fileVendor)) return true;
        if (DuplicateKey(fileVendor!, "") == DuplicateKey(planVendor, "")) return true;
        var root = $"{LibraryRoot}/{(planVendor ?? "").Trim()}";
        var path = (fileFolderPath ?? "").TrimEnd('/');
        return path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
    }

    public const string DefaultFinish = "As Finished";

    /// <summary>Material Finish for the data card: empty becomes "As Finished".</summary>
    public static string NormalizeFinish(string? finish) => string.IsNullOrWhiteSpace(finish) ? DefaultFinish : CleanText(finish);

    /// <summary>Exact "Steel" (case-insensitive), else the first name starting with "Steel", else the first name, else null.</summary>
    public static string? PreferredMaterial(IEnumerable<string> names)
    {
        var list = names.ToList();
        return list.FirstOrDefault(n => string.Equals(n, "Steel", StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(n => n.StartsWith("Steel", StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault();
    }

    /// <summary>Designer in the "First L." form used on existing parts: "Zak Olech" becomes "Zak O.". One word or an already short name is kept as is.</summary>
    public static string DesignerName(string? userName)
    {
        var parts = (userName ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return parts.Length == 1 ? parts[0] : "";
        var last = parts[^1].TrimEnd('.');
        return last.Length == 0 ? parts[0] : $"{parts[0]} {char.ToUpperInvariant(last[0])}.";
    }

    /// <summary>True when Inventor saved the part under a different CD-###### .ipt name in the same folder (Vault numbering on save).</summary>
    public static bool IsVaultRenumberedSave(string requested, string saved)
    {
        if (string.Equals(requested, saved, StringComparison.OrdinalIgnoreCase)) return false;
        var sameDir = string.Equals(System.IO.Path.GetDirectoryName(requested), System.IO.Path.GetDirectoryName(saved), StringComparison.OrdinalIgnoreCase);
        return sameDir && System.Text.RegularExpressions.Regex.IsMatch(System.IO.Path.GetFileName(saved), @"^CD-\d{6}\.ipt$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>Comments iProperty: each line trimmed, blank lines dropped, joined with CRLF.</summary>
    public static string CleanComments(string? text) =>
        string.Join("\r\n", (text ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));

    /// <summary>Title iProperty: "CD-001909 - Black-Oxide Alloy Steel Socket Head Screw" (Part Number - Description; Zak, 2026-10-13).</summary>
    public static string PartTitle(string partNumber, string? description)
    {
        var d = System.Text.RegularExpressions.Regex.Replace(CleanText(description ?? ""), @"\s+", " ");
        return d.Length == 0 ? partNumber.Trim() : $"{partNumber.Trim()} - {d}";
    }

    public static readonly string[] CardProperties = { "Source", "Vendor", "Vendor Number", "Material", "Material Finish" };

    /// <summary>Names of the data-card properties whose value (read through valueOf) is blank.</summary>
    public static IReadOnlyList<string> EmptyCardProperties(Func<string, string?> valueOf) =>
        CardProperties.Where(n => string.IsNullOrWhiteSpace(valueOf(n))).ToList();

    public static IReadOnlyList<string> Validate(ImportPlan p)
    {
        var errors = new List<string>();
        if (!IsValidName(p.Vendor ?? "")) errors.Add("Vendor: choose a vendor, or a new name without / \\ : * ? \" < > | and no trailing dot or space.");
        var sub = p.Subfolder ?? "";
        if (sub.Length > 0 && sub.Split('/').Any(s => !IsValidName(s) || string.Equals(s, VendorFolderLabel, StringComparison.OrdinalIgnoreCase))) errors.Add("Subfolder: each level needs a name without \\ : * ? \" < > | and no trailing dot or space, and cannot be \"(vendor folder)\".");
        var vn = CleanText(p.VendorNumber);
        if (vn.Length == 0) errors.Add("Vendor Number is required.");
        else if (!SafeFilePart(vn).Any(char.IsLetterOrDigit)) errors.Add("Vendor Number must contain at least one letter or digit.");
        if (string.IsNullOrWhiteSpace(p.Description)) errors.Add("Description is required.");
        if (string.IsNullOrWhiteSpace(p.Material)) errors.Add("Material is required.");
        return errors;
    }

    public static string VaultFolder(ImportPlan p) =>
        string.IsNullOrEmpty(p.Subfolder) ? $"{LibraryRoot}/{p.Vendor}" : $"{LibraryRoot}/{p.Vendor}/{p.Subfolder.Trim('/')}";

    public static string DataCardVendor(string vendorFolder) =>
        string.Equals(vendorFolder, "McMaster-Carr", StringComparison.OrdinalIgnoreCase) ? "McMASTER-CARR" : vendorFolder;

    public static string PartFileName(string cd) => cd + ".ipt";

    public static string PdfFileName(string cd, ImportPlan p) => $"{cd} {p.Vendor} {SafeFilePart(p.VendorNumber.Trim())}.pdf";

    private static readonly Regex UnsafeFileChars = new(@"[/\\:*?""<>|\p{Cc}]+");

    /// <summary>Makes a file-name fragment safe: each run of unsafe characters becomes one '-', and spaces/dots are trimmed at the ends.</summary>
    public static string SafeFilePart(string s) =>
        UnsafeFileChars.Replace(s ?? "", "-").Trim(' ', '.');

    public static string DuplicateKey(string vendor, string vendorNumber) =>
        Regex.Replace(DataCardVendor((vendor ?? "").Trim()), @"\s+", "").ToUpperInvariant() + "|" +
        Regex.Replace(vendorNumber ?? "", @"\s+", "").ToUpperInvariant();
}
