using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SFVaultBridge.Core;

/// <summary>The per-user settings file: Process values, print materials and recent partner names.</summary>
public sealed record BridgeSettings(IReadOnlyList<string> ProcessValues, IReadOnlyList<string> PrintMaterials, IReadOnlyList<string> RecentPartners)
{
    public const int MaxRecent = 10;

    private static readonly BridgeSettings DefaultsValue = new(
        new[] { "CNC", "3D Print", "Sheet Metal", "Weldment", "In-house", "Other" },
        new[] { "PLA", "PETG", "ABS", "ASA", "PA11", "PA12", "Nylon", "TPU", "PC", "Resin", "CF" },
        Array.Empty<string>());

    public static BridgeSettings Defaults => DefaultsValue;

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Simplifyber", "VaultBridge", "settings.json");

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static BridgeSettings Load(string path, Action<string> log)
    {
        if (!File.Exists(path))
        {
            TryWrite(path, ToJson(DefaultsValue, DefaultsValue.RecentPartners), log);
            return DefaultsValue;
        }
        if (!TryRead(path, log, out var root) || root is null) return DefaultsValue;
        return new BridgeSettings(
            Strings(root, "processValues") ?? DefaultsValue.ProcessValues,
            Strings(root, "printMaterials") ?? DefaultsValue.PrintMaterials,
            Normalize(Strings(root, "recentPartners") ?? DefaultsValue.RecentPartners));
    }

    public static IReadOnlyList<string> WithRecent(IReadOnlyList<string> recent, string partner)
    {
        var p = (partner ?? "").Trim();
        if (p.Length == 0) return recent;
        return Normalize(new[] { p }.Concat(recent));
    }

    public static bool RememberPartner(string path, string partner, Action<string> log)
    {
        var p = (partner ?? "").Trim();
        if (p.Length == 0) return false;
        if (!File.Exists(path))
            return TryWrite(path, ToJson(DefaultsValue, WithRecent(Array.Empty<string>(), p)), log);
        if (!TryRead(path, log, out var root) || root is null) return false;
        var current = Strings(root, "recentPartners") ?? new List<string>();
        root["recentPartners"] = new JsonArray(WithRecent(current, p).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
        return TryWrite(path, root.ToJsonString(Indented), log);
    }

    private static IReadOnlyList<string> Normalize(IEnumerable<string> names)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return names.Where(n => seen.Add(n)).Take(MaxRecent).ToList();
    }

    private static List<string>? Strings(JsonObject root, string key)
    {
        if (root[key] is not JsonArray a) return null;
        var list = new List<string>();
        foreach (var item in a)
            if (item is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)) list.Add(s.Trim());
        return list;
    }

    private static string ToJson(BridgeSettings s, IReadOnlyList<string> recent) =>
        new JsonObject
        {
            ["processValues"] = new JsonArray(s.ProcessValues.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
            ["printMaterials"] = new JsonArray(s.PrintMaterials.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
            ["recentPartners"] = new JsonArray(recent.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
        }.ToJsonString(Indented);

    private static bool TryRead(string path, Action<string> log, out JsonObject? root)
    {
        root = null;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (root is null) log("settings.json is not valid JSON (the top level is not an object); using defaults.");
            return root is not null;
        }
        catch (JsonException e)
        {
            log($"settings.json is not valid JSON ({e.Message}); using defaults.");
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log($"settings.json could not be read ({e.Message}); using defaults.");
            return false;
        }
    }

    private static bool TryWrite(string path, string text, Action<string> log)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log($"settings.json could not be written ({e.Message}).");
            return false;
        }
    }
}
