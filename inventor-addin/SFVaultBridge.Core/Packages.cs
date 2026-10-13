using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SFVaultBridge.Core;

public enum PackageKind { None, Cnc, SheetMetal, Print, OtherMake }

public enum ManufacturingMode { AllMake, Cnc, SheetMetal, Print }

[Flags]
public enum PackageFiles { None = 0, Step = 1, Pdf = 2, Dxf = 4, Print = 8 }

public readonly record struct PackageClass(PackageKind Kind, bool Guessed);

public sealed record PackagePart(string Item, BomNode Node, PackageClass Class, PackageFiles Files, double TotalQty);

/// <summary>Material names that mean "print this part". Each entry matches as a whole token, case-insensitive.</summary>
public sealed class PrintMaterials
{
    private readonly List<Regex> _patterns;

    public PrintMaterials(IEnumerable<string> entries)
    {
        _patterns = (entries ?? Array.Empty<string>())
            .Select(e => (e ?? "").Trim())
            .Where(e => e.Length > 0)
            .Select(e => new Regex("(?<![A-Za-z0-9])" + Regex.Escape(e) + "(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .ToList();
    }

    public bool Matches(string? material) =>
        !string.IsNullOrWhiteSpace(material) && _patterns.Any(p => p.IsMatch(material));
}

public static class PackageRules
{
    public const string TopItem = "Top";

    public static bool IsReleased(string? state) => string.Equals(state?.Trim(), "Released", StringComparison.OrdinalIgnoreCase);

    public static PackageClass Classify(BomNode node, string? vaultCategory, PrintMaterials materials, bool isSheetMetal)
    {
        if (string.IsNullOrWhiteSpace(node.FullPath) || string.IsNullOrWhiteSpace(node.FileName)) return new PackageClass(PackageKind.None, false);
        if (ReviewRules.Classify(node, vaultCategory) != PartKind.Make) return new PackageClass(PackageKind.None, false);
        var process = (node.Process ?? "").Trim();
        if (process.Length > 0)
        {
            static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            var kind = Is(process, "CNC") ? PackageKind.Cnc
                     : Is(process, "Sheet Metal") ? PackageKind.SheetMetal
                     : Is(process, "3D Print") ? PackageKind.Print
                     : PackageKind.OtherMake;
            return new PackageClass(kind, false);
        }
        if (isSheetMetal) return new PackageClass(PackageKind.SheetMetal, true);
        if (materials.Matches(node.Material)) return new PackageClass(PackageKind.Print, true);
        if (node.FileName.EndsWith(".ipt", StringComparison.OrdinalIgnoreCase)) return new PackageClass(PackageKind.Cnc, true);
        return new PackageClass(PackageKind.OtherMake, true);
    }

    public static PackageFiles FilesFor(ManufacturingMode mode, PackageKind kind)
    {
        if (kind == PackageKind.None) return PackageFiles.None;
        return mode switch
        {
            ManufacturingMode.AllMake => PackageFiles.Step | PackageFiles.Pdf | (kind == PackageKind.SheetMetal ? PackageFiles.Dxf : PackageFiles.None),
            ManufacturingMode.Cnc => kind == PackageKind.Cnc ? PackageFiles.Step | PackageFiles.Pdf : PackageFiles.None,
            ManufacturingMode.SheetMetal => kind == PackageKind.SheetMetal ? PackageFiles.Dxf | PackageFiles.Pdf : PackageFiles.None,
            ManufacturingMode.Print => kind == PackageKind.Print ? PackageFiles.Print : PackageFiles.None,
            _ => PackageFiles.None,
        };
    }

    private sealed class Entry
    {
        public string Item = "";
        public BomNode Node = null!;
        public PackageClass Class;
        public double Total;
    }

    public static IReadOnlyList<PackagePart> Select(IReadOnlyList<BomNode> roots, ManufacturingMode mode, PrintMaterials materials,
                                                    Func<BomNode, string?> categoryOf, BomNode? top = null)
    {
        var lines = BomOrdering.Flatten(roots);
        var mult = new List<double>();
        var pruned = new List<bool>();
        var byPath = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        var order = new List<Entry>();
        foreach (var line in lines)
        {
            var d = line.Depth;
            if (mult.Count > d) { mult.RemoveRange(d, mult.Count - d); pruned.RemoveRange(d, pruned.Count - d); }
            var node = line.Node;
            var category = categoryOf(node);
            var skipped = d > 0 && pruned[d - 1];
            var m = node.Quantity * (d > 0 ? mult[d - 1] : 1);
            var structure = SourceRules.EffectiveStructure(node.BomStructure ?? "", category);
            var prunes = skipped
                || string.Equals(structure, "Purchased", StringComparison.OrdinalIgnoreCase)
                || string.Equals(structure, "Reference", StringComparison.OrdinalIgnoreCase)
                || SourceRules.IsBuyCategory(category);
            mult.Add(m);
            pruned.Add(prunes);
            if (skipped) continue;
            if (byPath.TryGetValue(node.FullPath ?? "", out var e)) { e.Total += m; continue; }
            e = new Entry { Item = line.Item, Node = node, Class = Classify(node, category, materials, node.IsSheetMetal), Total = m };
            byPath[node.FullPath ?? ""] = e;
            order.Add(e);
        }
        var parts = new List<PackagePart>();
        if (top is not null && mode == ManufacturingMode.AllMake)
            parts.Add(new PackagePart(TopItem, top, Classify(top, null, materials, false), PackageFiles.Step | PackageFiles.Pdf, 1));
        foreach (var e in order)
        {
            var files = FilesFor(mode, e.Class.Kind);
            if (files != PackageFiles.None) parts.Add(new PackagePart(e.Item, e.Node, e.Class, files, e.Total));
        }
        return parts;
    }

    public static string KindLabel(PackageKind kind) => kind switch
    {
        PackageKind.Cnc => "CNC",
        PackageKind.SheetMetal => "Sheet Metal",
        PackageKind.Print => "3D Print",
        PackageKind.OtherMake => "Other make",
        _ => "",
    };

    public static string ModeName(ManufacturingMode mode) => mode switch
    {
        ManufacturingMode.Cnc => "CNC",
        ManufacturingMode.SheetMetal => "Sheet Metal",
        ManufacturingMode.Print => "3D Print",
        _ => "Make",
    };

    public static string ProcessText(PackagePart part) =>
        part.Class.Guessed ? "guessed: " + KindLabel(part.Class.Kind) : (part.Node.Process ?? "").Trim();

    public static string GuessedEntry(PackagePart part) => $"{part.Node.PartNumber} ({KindLabel(part.Class.Kind)})";

    public static string QtyText(double qty) => qty.ToString("0.###", CultureInfo.InvariantCulture);

    public static string FileStem(BomNode node)
    {
        var rev = ImportRules.SafeFilePart(node.Revision ?? "");
        return $"{ImportRules.SafeFilePart(Path.GetFileNameWithoutExtension(node.FileName ?? ""))} Rev {(rev.Length == 0 ? "-" : rev)}";
    }

    public static string Dest(PackageFiles file, PackagePart part)
    {
        var stem = FileStem(part.Node);
        return file switch
        {
            PackageFiles.Step => $"STEP/{stem}.stp",
            PackageFiles.Pdf => $"PDF/{stem}.pdf",
            PackageFiles.Dxf => $"DXF/{stem}.dxf",
            _ => throw new ArgumentException("Dest takes Step, Pdf or Dxf.", nameof(file)),
        };
    }

    public static string PrintDest(PackagePart part, string extension) =>
        $"{MaterialFolder(part.Node.Material)}/{FileStem(part.Node)} x{QtyText(part.TotalQty)}.{extension.TrimStart('.')}";

    public static string MaterialFolder(string? material)
    {
        var safe = ImportRules.SafeFilePart(material ?? "");
        return safe.Length == 0 ? "No material" : safe;
    }

    public static string? PartnerError(string? partner)
    {
        var p = (partner ?? "").Trim();
        if (p.Length == 0) return "Partner is required.";
        if (!ImportRules.IsValidName(p)) return "Partner: use a name without / \\ : * ? \" < > | and no trailing dot.";
        if (p.Length > 60) return "Partner: 60 characters at most.";
        return null;
    }

    public static string PartnerZipName(string assemblyStem, string partner, ManufacturingMode mode, bool quote = false) =>
        $"{assemblyStem} {ImportRules.SafeFilePart(partner.Trim())} {ModeName(mode)} {(quote ? "Quote " : "")}Package.zip";

    public static string PrintZipName(string assemblyStem) => $"{assemblyStem} 3D Print Package.zip";

    public static string PreviewText(IReadOnlyList<PackagePart> parts, ManufacturingMode mode)
    {
        if (parts.Count == 0)
            return mode switch
            {
                ManufacturingMode.Cnc => "No CNC parts found.",
                ManufacturingMode.SheetMetal => "No sheet metal parts found.",
                ManufacturingMode.Print => "No 3D print parts found.",
                _ => "No Make parts found.",
            };
        var n = parts.Count;
        var head = n == 1 ? "1 part" : $"{n} parts";
        if (mode == ManufacturingMode.Print) return $"{head}: {n} print file{(n == 1 ? "" : "s")}";
        int Count(PackageFiles f) => parts.Count(p => p.Files.HasFlag(f));
        return $"{head}: {Count(PackageFiles.Step)} STEP, {Count(PackageFiles.Pdf)} PDF, {Count(PackageFiles.Dxf)} DXF";
    }
}

public sealed record PackageRow(string Item, string PartNumber, string Description, double Qty, string Material, string Finish,
                                string Process, string Revision, string State, IReadOnlyList<string> Files);
public sealed record PackageFile(string Src, string Dest, bool Stamp);
public sealed record PackageFailure(string Part, string Reason);
public sealed record PackageManifestData(string Kind, string Assembly, string Description, string Revision, string State,
                                         string Partner, string Mode, string Source, string User, DateTime Created,
                                         IReadOnlyList<PackageRow> Rows, IReadOnlyList<PackageFile> Files,
                                         IReadOnlyList<string> Guessed, IReadOnlyList<string> MissingDrawings,
                                         IReadOnlyList<string> NoFlatPattern, IReadOnlyList<PackageFailure> Failed,
                                         bool Quote = false);

public static class PackageManifest
{
    private static JsonArray Strings(IEnumerable<string> items) => new(items.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());

    public static string ToJson(PackageManifestData m) =>
        new JsonObject
        {
            ["kind"] = m.Kind, ["assembly"] = m.Assembly, ["description"] = m.Description, ["revision"] = m.Revision,
            ["state"] = m.State, ["partner"] = m.Partner, ["mode"] = m.Mode, ["source"] = m.Source, ["user"] = m.User,
            ["created"] = m.Created.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            ["rows"] = new JsonArray(m.Rows.Select(r => (JsonNode?)new JsonObject
            {
                ["item"] = r.Item, ["part_number"] = r.PartNumber, ["description"] = r.Description, ["qty"] = r.Qty,
                ["material"] = r.Material, ["finish"] = r.Finish, ["process"] = r.Process, ["revision"] = r.Revision,
                ["state"] = r.State, ["files"] = Strings(r.Files),
            }).ToArray()),
            ["files"] = new JsonArray(m.Files.Select(f => (JsonNode?)new JsonObject { ["src"] = f.Src, ["dest"] = f.Dest, ["stamp"] = f.Stamp }).ToArray()),
            ["guessed"] = Strings(m.Guessed),
            ["missing_drawings"] = Strings(m.MissingDrawings),
            ["no_flat_pattern"] = Strings(m.NoFlatPattern),
            ["failed"] = new JsonArray(m.Failed.Select(f => (JsonNode?)new JsonObject { ["part"] = f.Part, ["reason"] = f.Reason }).ToArray()),
            ["quote"] = m.Quote,
        }.ToJsonString();
}

/// <summary>What the release gate needs to know about one included part. Drawing fields are ignored when no drawing is required.</summary>
public sealed record GatePart(string PartNumber, bool ModelFound, string ModelState, bool ModelCheckedOut, string? ModelCheckedOutBy,
                              bool ModelOpenDirty, bool DrawingRequired, bool HasDrawing, string DrawingState, bool DrawingCheckedOut,
                              string? DrawingCheckedOutBy, bool DrawingOpenDirty);

/// <summary>A part counts as released only when its model and its drawing are Released on the latest version and neither is checked out or open with edits.</summary>
public static class ReleaseGate
{
    public static IReadOnlyList<string> Check(IEnumerable<GatePart> parts)
    {
        var blockers = new List<string>();
        foreach (var p in parts)
        {
            if (!p.ModelFound) { blockers.Add($"{p.PartNumber}: model not in Vault"); continue; }
            var reasons = new List<string>();
            Item("model", p.ModelState, p.ModelCheckedOut, p.ModelCheckedOutBy, p.ModelOpenDirty, reasons);
            if (p.DrawingRequired)
            {
                if (!p.HasDrawing) reasons.Add("no drawing");
                else Item("drawing", p.DrawingState, p.DrawingCheckedOut, p.DrawingCheckedOutBy, p.DrawingOpenDirty, reasons);
            }
            if (reasons.Count > 0) blockers.Add($"{p.PartNumber}: {string.Join("; ", reasons)}");
        }
        return blockers;
    }

    private static void Item(string what, string state, bool checkedOut, string? by, bool dirty, List<string> reasons)
    {
        if (!PackageRules.IsReleased(state)) reasons.Add($"{what} is {(string.IsNullOrWhiteSpace(state) ? "in an unknown state" : state.Trim())}");
        if (checkedOut) reasons.Add($"{what} is checked out" + (string.IsNullOrWhiteSpace(by) ? "" : $" by {by.Trim()}"));
        if (dirty) reasons.Add($"{what} is open in Inventor with unsaved changes");
    }

    public static string Message(IReadOnlyList<string> blockers) =>
        "Not everything is released, so no package was built:" + NL + string.Join(NL, blockers)
        + NL + NL + "Release these in Vault, or tick \"For quote only (allow unreleased)\" to build a stamped quote package.";

    private const string NL = "\n";
}

public enum LocalCopyAction { Refresh, KeepOpenCopy, KeepCheckedOutCopy }

/// <summary>What to do with a model or drawing's local workspace copy before exporting it.</summary>
public static class LocalCopy
{
    /// <summary>
    /// Anything Inventor has open or the user has checked out is exported as it is (those are gate blockers in normal mode);
    /// every other copy is made the latest Vault version first, so the package matches what the gate checked.
    /// </summary>
    public static LocalCopyAction Decide(bool openInInventor, bool checkedOut) =>
        openInInventor ? LocalCopyAction.KeepOpenCopy
        : checkedOut ? LocalCopyAction.KeepCheckedOutCopy
        : LocalCopyAction.Refresh;
}
