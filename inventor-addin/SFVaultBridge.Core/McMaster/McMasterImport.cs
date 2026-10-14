using System.Globalization;
using System.Text.RegularExpressions;

namespace SFVaultBridge.Core.McMaster;

/// <summary>What Material best matches a McMaster Material spec, the finish words taken out of it, and a hint when nothing matched.</summary>
public sealed record MaterialMatch(string? Material, string Finish, string? Hint);

/// <summary>The Import New Part prefill from a McMaster product and price.</summary>
public sealed record McMasterPrefill(string VendorNumber, string Description, string Comments, MaterialMatch Material,
                                     double? EstimatedCost, int? PackSize, string Summary, string? CostHint);

/// <summary>Rules for Import New Part, Fetch by McMaster number. Pure; see the README, Import New Part from a McMaster number.</summary>
public static class McMasterImport
{
    public const string VendorFolder = "McMaster-Carr";
    public const string StepFormat = "3-D STEP";
    public const string PdfFormat = "2-D PDF";
    public const string PackSpec = "Quantity per Package";

    private static readonly Regex PartNumberShape = new(@"^[0-9A-Z][0-9A-Z\-]{2,24}$");

    /// <summary>Upper-cased, spaces removed; null unless it looks like a McMaster number (letters, digits, dashes; at least one digit).</summary>
    public static string? NormalizePartNumber(string? text)
    {
        var s = Regex.Replace(text ?? "", @"\s+", "").ToUpperInvariant();
        return PartNumberShape.IsMatch(s) && s.Any(char.IsDigit) ? s : null;
    }

    /// <summary>Downloads\SF Vault Bridge\McMaster\&lt;PN&gt;</summary>
    public static string DownloadFolder(string userProfile, string partNumber) =>
        Path.Combine(userProfile, "Downloads", "SF Vault Bridge", "McMaster", ImportRules.SafeFilePart(partNumber));

    /// <summary>"&lt;PN&gt;_&lt;family&gt;" made safe for a file name (the same shape VendorFileNames reads back).</summary>
    public static string FileStem(string partNumber, string? family)
    {
        var fam = Regex.Replace(ImportRules.SafeFilePart(ImportRules.CleanText(family)), @"\s+", " ").Trim();
        if (fam.Length > 80) fam = fam[..80].Trim(' ', '.', '-');
        var pn = ImportRules.SafeFilePart(partNumber);
        return fam.Length == 0 ? pn : pn + "_" + fam;
    }

    /// <summary>The format to ask for: the exact name when listed, else the first containing the keyword (STEP, PDF); null when none.</summary>
    public static string? PickFormat(IReadOnlyList<string> formats, string wanted, string keyword)
    {
        var exact = formats.FirstOrDefault(f => string.Equals(f, wanted, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        var two = keyword == "PDF" ? formats.FirstOrDefault(f => f.Contains("2-D", StringComparison.OrdinalIgnoreCase) && f.Contains(keyword, StringComparison.OrdinalIgnoreCase)) : null;
        return two ?? formats.FirstOrDefault(f => f.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Finish words McMaster puts in front of a material. Longest first so "Black-Oxide" wins over "Black".</summary>
    public static readonly string[] FinishWords =
    {
        "Zinc Yellow-Chromate Plated", "Hot-Dip Galvanized", "Black-Oxide", "Black Oxide", "Zinc-Plated", "Zinc Plated",
        "Nickel-Plated", "Nickel Plated", "Chrome-Plated", "Chrome Plated", "Cadmium-Plated", "Cadmium Plated",
        "Black-Phosphate", "Black Phosphate", "Black-Anodized", "Black Anodized", "Galvanized", "Passivated", "Anodized",
        "Phosphate", "18-8",
    };

    public static MaterialMatch MatchMaterial(IReadOnlyList<string>? materialSpec, IReadOnlyList<string> library)
    {
        var spec = string.Join(" ", (materialSpec ?? Array.Empty<string>()).Select(s => s.Trim()).Where(s => s.Length > 0));
        if (spec.Length == 0) return new MaterialMatch(null, ImportRules.DefaultFinish, "McMaster lists no material; choose one.");

        var finishes = new List<(int Index, string Word)>();
        var bas = " " + spec + " ";
        foreach (var w in FinishWords)
        {
            var re = new Regex(@"(?<![\w-])" + Regex.Escape(w) + @"(?![\w-])", RegexOptions.IgnoreCase);
            var m = re.Match(bas);
            if (!m.Success) continue;
            finishes.Add((m.Index, w));
            bas = re.Replace(bas, " ");
        }
        bas = Regex.Replace(bas, @"\s+", " ").Trim();
        var finish = finishes.Count == 0 ? ImportRules.DefaultFinish : string.Join(" ", finishes.OrderBy(f => f.Index).Select(f => f.Word));
        var picked = Pick(bas, library);
        return new MaterialMatch(picked, finish, picked is null ? $"McMaster material: {spec}. No close match in the material library; choose one." : null);
    }

    private static string? Pick(string bas, IReadOnlyList<string> library)
    {
        string? Exact(string name) => library.FirstOrDefault(n => string.Equals(n.Trim(), name, StringComparison.OrdinalIgnoreCase));
        string? Containing(params string[] words) => library.FirstOrDefault(n => words.All(w => n.Contains(w, StringComparison.OrdinalIgnoreCase)));
        var lower = bas.ToLowerInvariant();
        if (lower.Length == 0) return null;

        if (lower.Contains("stainless"))
        {
            var grade = Regex.Match(bas, @"\b(\d{3}[A-Z]?)\b").Groups[1].Value;
            return (grade.Length > 0 ? Containing("Stainless", grade) : null) ?? Exact("Stainless Steel") ?? Containing("Stainless");
        }
        if (lower.Contains("steel"))
            return Exact("Steel") ?? library.FirstOrDefault(n => n.StartsWith("Steel", StringComparison.OrdinalIgnoreCase)) ?? Containing("Steel");
        if (lower.Contains("aluminum") || lower.Contains("aluminium"))
        {
            var grade = Regex.Match(bas, @"\b(\d{4})\b").Groups[1].Value;
            return (grade.Length > 0 ? Exact("Aluminum " + grade) ?? Containing("Aluminum", grade) : null) ?? Exact("Aluminum") ?? Containing("Aluminum");
        }
        var whole = Exact(bas) ?? Containing(bas);
        if (whole is not null) return whole;
        var word = bas.Split(' ', StringSplitOptions.RemoveEmptyEntries).Last();
        return word.Length < 3 ? null : Containing(word);
    }

    /// <summary>Pieces per pack from "Quantity per Package" (the first whole number in it); null when absent or not a number above 0.</summary>
    public static int? QuantityPerPackage(IReadOnlyDictionary<string, IReadOnlyList<string>> specs)
    {
        if (!specs.TryGetValue(PackSpec, out var values) || values.Count == 0) return null;
        var m = Regex.Match(values[0].Replace(",", ""), @"\d+");
        return m.Success && int.TryParse(m.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
    }

    /// <summary>True when McMaster sells this by the pack ("Packs", "Pack", "Pkg").</summary>
    public static bool IsPackUnit(string? unit)
    {
        var u = (unit ?? "").Trim().ToLowerInvariant();
        return u.StartsWith("pack", StringComparison.Ordinal) || u.StartsWith("pkg", StringComparison.Ordinal);
    }

    /// <summary>Pieces in one purchasing unit: the pack quantity for a pack unit, 1 for each; null for a pack with no pack quantity or any other unit (feet, rolls...).</summary>
    public static int? PackSize(string? unit, int? quantityPerPackage) =>
        IsPackUnit(unit) ? quantityPerPackage : IsEachUnit(unit) ? 1 : null;

    /// <summary>True when McMaster sells this by the piece ("Each", "Ea", or no unit). Rolls, feet, cases and the like are neither.</summary>
    public static bool IsEachUnit(string? unit)
    {
        var u = (unit ?? "").Trim().ToLowerInvariant();
        return u.Length == 0 || u.StartsWith("each", StringComparison.Ordinal) || u == "ea" || u == "ea.";
    }

    /// <summary>Price of one piece: price / pack size for a pack unit, the unit price otherwise; null when a pack unit has no pack size.</summary>
    public static double? PiecePrice(double unitPrice, string? unit, int? quantityPerPackage)
    {
        var size = PackSize(unit, quantityPerPackage);
        return size is > 0 ? Math.Round(unitPrice / size.Value, 6) : null;
    }

    public static McMasterPrefill Prefill(McMasterProduct product, McMasterPrice? price, IReadOnlyList<string> library)
    {
        product.Specifications.TryGetValue("Material", out var mat);
        var match = MatchMaterial(mat, library);
        var qpp = QuantityPerPackage(product.Specifications);
        double? cost = null; int? pack = null; string? costHint = null;
        if (price is not null)
        {
            pack = PackSize(price.UnitOfMeasure, qpp);
            cost = PiecePrice(price.UnitPrice, price.UnitOfMeasure, qpp);
            if (cost is null)
                costHint = IsPackUnit(price.UnitOfMeasure)
                    ? "McMaster sells this by the pack but lists no Quantity per Package; Estimated Cost left blank."
                    : $"McMaster sells this by the {price.UnitOfMeasure}, not by the piece; Estimated Cost left blank.";
        }
        else costHint = "No McMaster price; Estimated Cost left blank.";
        return new McMasterPrefill(product.PartNumber, ImportRules.CleanText(product.Family), ImportRules.CleanText(product.Description), match,
                                   cost, pack, Summary(product, price, qpp), costHint);
    }

    /// <summary>"Black-Oxide Alloy Steel Socket Head Screw | US Origin, 0-80 Thread Size, 3/16" Long | $4.68 per pack of 50"</summary>
    public static string Summary(McMasterProduct p, McMasterPrice? price, int? quantityPerPackage)
    {
        var parts = new List<string>();
        if (p.Family.Length > 0) parts.Add(p.Family);
        if (p.Description.Length > 0) parts.Add(p.Description);
        if (price is not null)
        {
            var money = price.UnitPrice.ToString("C2", CultureInfo.GetCultureInfo("en-US"));
            parts.Add(IsPackUnit(price.UnitOfMeasure)
                ? quantityPerPackage is > 0 ? $"{money} per pack of {quantityPerPackage}" : $"{money} per pack"
                : price.UnitOfMeasure.Length > 0 ? $"{money} ({price.UnitOfMeasure})" : money);
        }
        if (p.Status.Length > 0 && !p.Status.Equals("Active", StringComparison.OrdinalIgnoreCase)) parts.Add("Status: " + p.Status);
        return string.Join(" | ", parts);
    }
}
