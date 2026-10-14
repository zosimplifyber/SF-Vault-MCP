using System.Globalization;
using System.Text.Json.Nodes;

namespace SFVaultBridge.Core;

/// <summary>Pure argument parsing and mapping for the folder, advanced search and job tools. No Vault types.</summary>
public static class ExtraRules
{
    public const int MaxConditions = 10;
    public const int ListDefault = 100, ListMax = 500;
    public const int SearchDefault = 50, SearchMax = 200;
    public const int JobsDefault = 25, JobsMax = 200;
    public const int MemberListMax = 200;

    /// <summary>THE table of Vault SrchCond.SrchOper codes, one place to change after live verification. Taken from SDK memory, NOT yet verified against a live Vault:
    /// 1 contains, 2 does not contain, 3 is exactly, 4 is not, 5 greater than, 6 greater or equal, 7 less than, 8 less or equal, 9 is empty, 10 is not empty.
    /// starts_with has no Vault operator: it is sent as contains and checked client-side (see SearchRunner).</summary>
    public static readonly IReadOnlyDictionary<SearchOp, int> OperatorCodes = new Dictionary<SearchOp, int>
    {
        [SearchOp.Contains] = 1,
        [SearchOp.StartsWith] = 1,
        [SearchOp.Equals] = 3,
        [SearchOp.NotEquals] = 4,
        [SearchOp.GreaterThan] = 5,
        [SearchOp.LessThan] = 7,
        [SearchOp.IsEmpty] = 9,
        [SearchOp.IsNotEmpty] = 10,
    };

    public const int ListCap = 500;

    public static readonly string[] OperatorNames = { "contains", "equals", "not_equals", "starts_with", "greater_than", "less_than", "is_empty", "is_not_empty" };

    // ---- folders ----

    /// <summary>A Vault folder path as typed: "$" or "$/Designs/x", forward slashes, no trailing slash. Anything else is bad_request.</summary>
    public static string NormalizeFolderPath(string text)
    {
        var p = text.Trim().Replace('\\', '/');
        while (p.Length > 1 && p.EndsWith('/')) p = p[..^1];
        if (p != "$" && !p.StartsWith("$/", StringComparison.Ordinal))
            throw new BridgeException("bad_request", $"'{text}' is not a Vault folder path. Folder paths start with $/ (for example $/DESIGNS/Simplifyber Library) or are just $ for the root.");
        return p;
    }

    /// <summary>The folder a list call means: 'path' or 'folder_id', never both; neither means the root.</summary>
    public static FolderRef ParseFolderRef(JsonObject a)
    {
        var path = Args.OptStr(a, "path").Trim();
        long? id = null;
        if (a["folder_id"] is { } n)
        {
            if (n is not JsonValue v || !v.TryGetValue<long>(out var l) || l <= 0)
                throw new BridgeException("bad_request", "'folder_id' must be a positive integer from a folder listing.");
            id = l;
        }
        if (path.Length > 0 && id is not null) throw new BridgeException("bad_request", "Give 'path' or 'folder_id', not both.");
        if (id is not null) return new FolderRef(null, id);
        return new FolderRef(NormalizeFolderPath(path.Length == 0 ? "$" : path), null);
    }

    // ---- advanced search ----

    public static AdvancedSearchSpec ParseSearch(JsonObject a)
    {
        if (a["conditions"] is not JsonArray arr || arr.Count == 0)
            throw new BridgeException("bad_request", "'conditions' is required: a list of {property, operator, value}.");
        if (arr.Count > MaxConditions) throw new BridgeException("bad_request", $"At most {MaxConditions} conditions.");
        var list = new List<SearchCondition>();
        for (var i = 0; i < arr.Count; i++)
        {
            if (arr[i] is not JsonObject c) throw new BridgeException("bad_request", $"conditions[{i}] must be an object with property, operator and value.");
            var prop = c["property"] is JsonValue pv && pv.TryGetValue<string>(out var ps) ? ps.Trim() : "";
            if (prop.Length == 0) throw new BridgeException("bad_request", $"conditions[{i}].property is required (a Vault property name such as State or Part Number).");
            var opText = c["operator"] is JsonValue ov && ov.TryGetValue<string>(out var os) ? os : "";
            var op = ParseOperator(opText) ?? throw new BridgeException("bad_request",
                $"conditions[{i}].operator '{opText}' is not known. Use one of: {string.Join(", ", OperatorNames)}.");
            var value = ValueText(c["value"], i);
            var needsValue = op is not (SearchOp.IsEmpty or SearchOp.IsNotEmpty);
            if (needsValue && value is null) throw new BridgeException("bad_request", $"conditions[{i}].value is required for {OpName(op)}.");
            list.Add(new SearchCondition(prop, op, needsValue ? value!.Trim() : ""));
        }
        var folder = Args.OptStr(a, "folder").Trim();
        return new AdvancedSearchSpec(list, Args.Bool(a, "any", false), folder.Length == 0 ? null : NormalizeFolderPath(folder),
            Args.Bool(a, "recurse", true), Args.Bool(a, "latest_only", true), ReadTools.Clamp(a, "limit", SearchDefault, 1, SearchMax));
    }

    private static string? ValueText(JsonNode? n, int i)
    {
        if (n is null) return null;
        if (n is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s)) return s;
            if (v.TryGetValue<bool>(out var b)) return b ? "True" : "False";
            if (v.TryGetValue<long>(out var l)) return l.ToString(CultureInfo.InvariantCulture);
            if (v.TryGetValue<double>(out var d)) return d.ToString("R", CultureInfo.InvariantCulture);
        }
        throw new BridgeException("bad_request", $"conditions[{i}].value must be a string, number or boolean.");
    }

    public static SearchOp? ParseOperator(string text)
    {
        var t = text.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        return t switch
        {
            "contains" => SearchOp.Contains, "equals" => SearchOp.Equals, "not_equals" => SearchOp.NotEquals, "starts_with" => SearchOp.StartsWith,
            "greater_than" => SearchOp.GreaterThan, "less_than" => SearchOp.LessThan, "is_empty" => SearchOp.IsEmpty, "is_not_empty" => SearchOp.IsNotEmpty,
            _ => null,
        };
    }

    public static string OpName(SearchOp op) => OperatorNames[(int)op];

    private static string Norm(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    /// <summary>The definition a property name means: system name, then display name (case-insensitive, spaces and underscores ignored). Unknown names are an error that names them and lists the closest.</summary>
    public static PropDefLite ResolveProperty(string name, IEnumerable<PropDefLite> defs)
    {
        var all = defs.Where(d => d.Active).ToList();
        var key = Norm(name);
        var hits = all.Where(d => string.Equals(d.SysName, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hits.Count == 0) hits = all.Where(d => string.Equals(d.DispName, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hits.Count == 0 && key.Length > 0) hits = all.Where(d => Norm(d.DispName) == key || Norm(d.SysName) == key).ToList();
        if (hits.Count == 1) return hits[0];
        if (hits.Count > 1)
            throw new BridgeException("ambiguous_property", $"Property '{name}' matches {hits.Count} definitions ({string.Join(", ", hits.Take(6).Select(h => h.SysName))}). Use the system name.");
        var near = all.Where(d => key.Length > 1 && Norm(d.DispName).Length > 1 && (Norm(d.DispName).Contains(key) || key.Contains(Norm(d.DispName))))
            .Select(d => d.DispName).Distinct().Take(8).ToList();
        throw new BridgeException("unknown_property", $"Unknown property '{name}'." +
            (near.Count > 0 ? $" Closest: {string.Join(", ", near)}." : "") + " Call vault_property_definitions to list the names.");
    }

    private static bool Is(PropDefLite d, string type) => d.DataType.Equals(type, StringComparison.OrdinalIgnoreCase);

    /// <summary>Resolves every property, checks each operator and value suits the property type, and returns what the Vault search needs.</summary>
    public static MappedSearch Map(AdvancedSearchSpec spec, IReadOnlyList<PropDefLite> defs)
    {
        var mapped = new List<MappedCondition>();
        foreach (var c in spec.Conditions)
        {
            var def = ResolveProperty(c.Property, defs);
            if (c.Op is SearchOp.Contains or SearchOp.StartsWith && !Is(def, "String"))
                throw new BridgeException("bad_request", $"'{OpName(c.Op)}' only works on text properties; '{def.DispName}' is {def.DataType}. Use equals, not_equals, greater_than or less_than.");
            if (c.Op is SearchOp.GreaterThan or SearchOp.LessThan && !(Is(def, "Numeric") || Is(def, "DateTime")))
                throw new BridgeException("bad_request", $"'{OpName(c.Op)}' only works on number and date properties; '{def.DispName}' is {def.DataType}.");
            var text = c.Op is SearchOp.IsEmpty or SearchOp.IsNotEmpty ? "" : CheckValue(def, c.Value);
            mapped.Add(new MappedCondition(def.Id, def.DispName, Code(c.Op), text, c.Op == SearchOp.StartsWith));
        }
        return new MappedSearch(mapped, spec.Any, spec.FolderPath, spec.Recurse, spec.LatestOnly, spec.Limit);
    }

    private static string CheckValue(PropDefLite def, string text)
    {
        if (Is(def, "Numeric"))
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                throw new BridgeException("bad_request", $"'{text}' is not a number, and '{def.DispName}' is a number property.");
            return d.ToString("R", CultureInfo.InvariantCulture);
        }
        if (Is(def, "DateTime"))
        {
            if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                throw new BridgeException("bad_request", $"'{text}' is not a date, and '{def.DispName}' is a date property. Use yyyy-MM-dd.");
            return dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
        if (Is(def, "Bool"))
        {
            return text.Trim().ToLowerInvariant() switch
            {
                "true" or "yes" or "1" => "True",
                "false" or "no" or "0" => "False",
                _ => throw new BridgeException("bad_request", $"'{text}' is not true or false, and '{def.DispName}' is a yes/no property."),
            };
        }
        return text;
    }

    public static int Code(SearchOp op) => OperatorCodes[op];

    // ---- jobs ----

    public static readonly string[] JobStatuses = { "ready", "running", "success", "failure", "active", "all" };

    public static bool JobMatches(VaultJobInfo j, string filter) => filter switch
    {
        "all" => true,
        "active" => j.Status is "Ready" or "Running",
        _ => string.Equals(j.Status, filter, StringComparison.OrdinalIgnoreCase),
    };

    /// <summary>The standard Vault drawing PDF job, as the Python vault_publish_pdf_from_drawing and publish_bom build it (type Autodesk.Vault.PDF.Create.idw or .dwg,
    /// params FileVersionId and UpdateViewOption, PascalCase keys). Only .idw and .dwg drawings qualify.</summary>
    public static (string Type, IReadOnlyDictionary<string, string> Params) PdfJobFor(string fileName, long fileVersionId)
    {
        var ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        if (ext is not ("idw" or "dwg"))
            throw new BridgeException("bad_request", $"PDF jobs are only for .idw and .dwg drawings; {fileName} is not one.");
        return ($"Autodesk.Vault.PDF.Create.{ext}", new Dictionary<string, string>
        {
            ["FileVersionId"] = fileVersionId.ToString(CultureInfo.InvariantCulture),
            ["UpdateViewOption"] = "False",
        });
    }
}
