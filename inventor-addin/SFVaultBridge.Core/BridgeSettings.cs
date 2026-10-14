using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SFVaultBridge.Core;

/// <summary>The per-user settings file: Process values, print materials and recent partner names.</summary>
public sealed record BridgeSettings(IReadOnlyList<string> ProcessValues, IReadOnlyList<string> PrintMaterials, IReadOnlyList<string> RecentPartners, string DropboxAppKey = "", double SalesTaxRate = BridgeSettings.DefaultSalesTaxRate, IReadOnlyList<string>? UserOverrides = null,
                                    string McMasterServerUrl = BridgeSettings.DefaultMcMasterServerUrl)
{
    public const double DefaultSalesTaxRate = 0.07;
    public const string DefaultMcMasterServerUrl = "https://mcmaster.simplifyber-app.com/mcp";
    public const int MaxRecent = 10;

    private static readonly BridgeSettings DefaultsValue = new(
        new[] { "CNC", "3D Print", "Sheet Metal", "Weldment", "In-house", "Other" },
        new[] { "PLA", "PETG", "ABS", "ASA", "PA11", "PA12", "Nylon", "TPU", "PC", "Resin", "CF" },
        Array.Empty<string>());

    public static BridgeSettings Defaults => DefaultsValue;

    /// <summary>Team keys the user changed in the Settings window; team settings never overwrite these.</summary>
    public IReadOnlyList<string> Overrides => UserOverrides ?? Array.Empty<string>();

    /// <summary>The team keys whose value differs between the settings the form opened with and the edited ones.</summary>
    public static IReadOnlyList<string> ChangedKeys(BridgeSettings original, BridgeSettings edited)
    {
        var changed = new List<string>();
        if (!original.ProcessValues.SequenceEqual(edited.ProcessValues, StringComparer.Ordinal)) changed.Add("processValues");
        if (!original.PrintMaterials.SequenceEqual(edited.PrintMaterials, StringComparer.Ordinal)) changed.Add("printMaterials");
        if (!string.Equals(original.DropboxAppKey.Trim(), edited.DropboxAppKey.Trim(), StringComparison.Ordinal)) changed.Add("dropboxAppKey");
        if (Math.Abs(original.SalesTaxRate - edited.SalesTaxRate) > 1e-9) changed.Add("salesTaxRate");
        if (!string.Equals(original.McMasterServerUrl.Trim(), edited.McMasterServerUrl.Trim(), StringComparison.Ordinal)) changed.Add("mcmasterServerUrl");
        return changed;
    }

    /// <summary>
    /// Writes the edited settings to path. Keys in the file that this type does not own stay as they are; userOverrides becomes the
    /// file's list plus every key the user changed (edited compared with original). A malformed file is copied to
    /// path.bad-yyyyMMdd-HHmmss first. Returns false (and logs) when the file cannot be read or written.
    /// </summary>
    public static bool Save(string path, BridgeSettings original, BridgeSettings edited, Action<string> log, DateTime? now = null)
    {
        var root = new JsonObject();
        try
        {
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path);
                if (SettingsMerge.IsMalformed(text))
                {
                    File.Copy(path, $"{path}.bad-{(now ?? DateTime.Now):yyyyMMdd-HHmmss}", false);
                    log("settings.json was not valid JSON; kept a .bad copy and wrote a fresh file.");
                }
                else if (!string.IsNullOrWhiteSpace(text)) root = JsonNode.Parse(text) as JsonObject ?? new JsonObject();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log($"settings.json could not be read ({e.Message}).");
            return false;
        }
        var fileOverrides = Strings(root, "userOverrides") ?? new List<string>();
        var overrides = SettingsRules.CleanOverrides(fileOverrides.Concat(ChangedKeys(original, edited)));
        root["processValues"] = StringArray(edited.ProcessValues);
        root["printMaterials"] = StringArray(edited.PrintMaterials);
        root["recentPartners"] = StringArray(edited.RecentPartners);
        root["dropboxAppKey"] = edited.DropboxAppKey.Trim();
        root["salesTaxRate"] = edited.SalesTaxRate;
        root["mcmasterServerUrl"] = edited.McMasterServerUrl.Trim();
        root["userOverrides"] = StringArray(overrides);
        return TryWrite(path, root.ToJsonString(Indented), log);
    }

    private static JsonArray StringArray(IEnumerable<string> items) => new(items.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());

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
            Normalize(Strings(root, "recentPartners") ?? DefaultsValue.RecentPartners),
            AppKey(root), Rate(root), SettingsRules.CleanOverrides(Strings(root, "userOverrides")), McMasterUrl(root));
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

    private static string AppKey(JsonObject root) =>
        root["dropboxAppKey"] is JsonValue v && v.TryGetValue<string>(out var k) ? k.Trim() : "";

    /// <summary>A valid stored mcmasterServerUrl, else the default (blank or junk never turns McMaster off by accident).</summary>
    private static string McMasterUrl(JsonObject root) =>
        root["mcmasterServerUrl"] is JsonValue v && v.TryGetValue<string>(out var u) && SettingsRules.TryParseServerUrl(u, out var clean, out _)
            ? clean : DefaultMcMasterServerUrl;

    private static double Rate(JsonObject root)
    {
        if (root["salesTaxRate"] is JsonValue v && v.TryGetValue<double>(out var r) && r >= 0 && r <= 1 && double.IsFinite(r)) return r;
        return DefaultSalesTaxRate;
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
            ["dropboxAppKey"] = s.DropboxAppKey,
            ["salesTaxRate"] = s.SalesTaxRate,
            ["mcmasterServerUrl"] = s.McMasterServerUrl,
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
            AtomicFile.WriteAllText(path, text);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log($"settings.json could not be written ({e.Message}).");
            return false;
        }
    }
}
