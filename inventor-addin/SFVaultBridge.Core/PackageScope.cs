using System.Globalization;

namespace SFVaultBridge.Core;

/// <summary>What a Partner Package covers.</summary>
public enum PackageScopeKind { Assembly, Selected, Part }

/// <summary>The kind of document active in Inventor when Partner Package is clicked.</summary>
public enum ScopeDoc { None, Assembly, Part, Other }

/// <summary>Kind is null (and Message set) when the button must not run.</summary>
public sealed record ScopeDecision(PackageScopeKind? Kind, string? Message);

/// <summary>What the selection pick found: Make candidates in BOM order, and the picks that were skipped with their text ("not a Make part: CD-1.ipt").</summary>
public sealed record ScopePick(IReadOnlyList<ScopeCandidate> Candidates, IReadOnlyList<string> Skipped);

/// <summary>One grid row for a single or selected scope. Note is set when the chosen Parts mode excludes the part (shown greyed, not built).</summary>
public sealed record ScopeRow(ScopeCandidate Candidate, int DefaultQty, PackageFiles Files, string? Note)
{
    public bool Included => Note is null && Files != PackageFiles.None;
}

/// <summary>Pure rules for the Partner Package scopes: whole assembly, selected parts, one part.</summary>
public static class PackageScope
{
    public const int MaxQty = 100000;
    public const string OpenMessage = "Open an assembly or a part.";

    /// <summary>Assembly with components selected = those parts; assembly alone = whole assembly; part = that part; anything else = a message.</summary>
    public static ScopeDecision Decide(ScopeDoc doc, int selectedComponents) => doc switch
    {
        ScopeDoc.Assembly => new ScopeDecision(selectedComponents > 0 ? PackageScopeKind.Selected : PackageScopeKind.Assembly, null),
        ScopeDoc.Part => new ScopeDecision(PackageScopeKind.Part, null),
        _ => new ScopeDecision(null, OpenMessage),
    };

    private static string Norm(string p) => p.Trim().Replace('/', '\\');

    /// <summary>
    /// Keeps the BOM rows whose file is selected (deduped by file path, so proxies and repeat occurrences count once; a selected
    /// sub-assembly is its own row, its children are not added). Picks that are not a Make row are skipped with a reason.
    /// A null or blank selected file is a component without a file (virtual).
    /// </summary>
    public static ScopePick PickSelected(IReadOnlyList<ScopeCandidate> all, IEnumerable<string?> selectedFiles)
    {
        var byPath = new Dictionary<string, ScopeCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in all) byPath.TryAdd(Norm(c.Node.FullPath ?? ""), c);
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skipped = new List<string>();
        var seenSkip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Skip(string key, string what) { if (seenSkip.Add(key)) skipped.Add(NotMake(what)); }
        foreach (var raw in selectedFiles)
        {
            if (string.IsNullOrWhiteSpace(raw)) { Skip("\0virtual", "a component without a file (virtual)"); continue; }
            var key = Norm(raw);
            var name = Path.GetFileName(key);
            if (byPath.TryGetValue(key, out var c) && c.Class.Kind != PackageKind.None) wanted.Add(key);
            else Skip(key, c is null ? name : c.Node.PartNumber);
        }
        var picked = all.Where(c => wanted.Contains(Norm(c.Node.FullPath ?? ""))).ToList();
        return new ScopePick(picked, skipped);
    }

    /// <summary>One part document: the same pick rule as a BOM row, so a purchased part is "not a Make part".</summary>
    public static ScopePick PickPart(IReadOnlyList<ScopeCandidate> all)
    {
        var make = all.Where(c => c.Class.Kind != PackageKind.None).ToList();
        var skipped = all.Where(c => c.Class.Kind == PackageKind.None).Select(c => NotMake(c.Node.PartNumber)).ToList();
        return new ScopePick(make, skipped);
    }

    public const int MaxSkippedLines = 10;

    /// <summary>The skipped picks, one per line, at most <see cref="MaxSkippedLines"/> then "and N more".</summary>
    public static string SkippedText(IReadOnlyList<string> skipped)
    {
        var lines = skipped.Take(MaxSkippedLines).ToList();
        if (skipped.Count > MaxSkippedLines) lines.Add($"and {skipped.Count - MaxSkippedLines} more");
        return string.Join(Environment.NewLine, lines);
    }

    public static string NotMake(string name) => $"not a Make part: {name}";

    /// <summary>The Quantity column's default: the part's total quantity in the assembly, whole and at least 1.</summary>
    public static int DefaultQty(double total) =>
        double.IsNaN(total) || double.IsInfinity(total) || total < 1 ? 1 : (int)Math.Min(int.MaxValue, Math.Ceiling(total - 1e-9));

    /// <summary>Rows for the grid under the chosen Parts mode. A part the mode excludes carries the reason and no files. A single part defaults to 1.</summary>
    public static IReadOnlyList<ScopeRow> Rows(IReadOnlyList<ScopeCandidate> candidates, ManufacturingMode mode, bool single)
    {
        var rows = new List<ScopeRow>();
        foreach (var c in candidates)
        {
            var files = PackageRules.FilesFor(mode, c.Class.Kind);
            rows.Add(new ScopeRow(c, single ? 1 : DefaultQty(c.Total), files, files == PackageFiles.None ? ExcludedReason(mode) : null));
        }
        return rows;
    }

    public static string ExcludedReason(ManufacturingMode mode) => mode switch
    {
        ManufacturingMode.Cnc => "not CNC",
        ManufacturingMode.SheetMetal => "not sheet metal",
        ManufacturingMode.Print => "not 3D print",
        _ => "not a Make part",
    };

    /// <summary>"STEP, PDF, DXF" for the grid's Mode files column.</summary>
    public static string FilesText(PackageFiles files)
    {
        var l = new List<string>();
        if (files.HasFlag(PackageFiles.Step)) l.Add("STEP");
        if (files.HasFlag(PackageFiles.Pdf)) l.Add("PDF");
        if (files.HasFlag(PackageFiles.Dxf)) l.Add("DXF");
        if (files.HasFlag(PackageFiles.Print)) l.Add("Print");
        return string.Join("/", l);
    }

    /// <summary>A whole number of at least 1 (the Quantity column). Value is null and Error set when the text is not.</summary>
    public static (int? Value, string? Error) ParseQty(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return (null, "Quantity is required.");
        if (!int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            return (null, "Quantity must be a whole number of 1 or more.");
        if (n < 1) return (null, "Quantity must be 1 or more.");
        return n <= MaxQty ? (n, null) : (null, $"Quantity must be {MaxQty} or less.");
    }

    /// <summary>The included rows as package parts, in order, with the entered quantities (the default where none was entered).</summary>
    public static IReadOnlyList<PackagePart> ToParts(IEnumerable<ScopeRow> rows, Func<ScopeRow, int?> entered) =>
        rows.Where(r => r.Included)
            .Select(r => new PackagePart(r.Candidate.Item, r.Candidate.Node, r.Candidate.Class, r.Files, entered(r) ?? r.DefaultQty))
            .ToList();

    /// <summary>The line at the top of the form and in the BOM title: "Whole assembly CD-1" / "3 selected parts in CD-1" / "Part CD-2".</summary>
    public static string ScopeLine(PackageScopeKind kind, string stem, int selectedParts = 0, int skipped = 0) => kind switch
    {
        PackageScopeKind.Selected => $"{selectedParts} selected part{(selectedParts == 1 ? "" : "s")} in {stem}{(skipped > 0 ? $" ({skipped} skipped)" : "")}",
        PackageScopeKind.Part => $"Part {stem}",
        _ => $"Whole assembly {stem}",
    };

    /// <summary>
    /// Assembly and Part: "{stem} {Partner} {Mode} Package.zip" (the part's own stem for a part).
    /// Selected: "{stem} {Partner} {Mode} Selected Package.zip". Quote adds "Quote" before "Package".
    /// </summary>
    public static string ZipName(PackageScopeKind kind, string stem, string partner, ManufacturingMode mode, bool quote)
    {
        var selected = kind == PackageScopeKind.Selected ? "Selected " : "";
        return $"{stem} {ImportRules.SafeFilePart(partner.Trim())} {PackageRules.ModeName(mode)} {selected}{(quote ? "Quote " : "")}Package.zip";
    }
}
