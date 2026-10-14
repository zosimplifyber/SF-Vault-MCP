using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SFVaultBridge.Core;

/// <summary>A semantic version x.y.z (no pre-release tags). Compares numerically, so 1.10.0 is newer than 1.9.0.</summary>
public readonly record struct SemVer(int Major, int Minor, int Patch) : IComparable<SemVer>
{
    private static readonly Regex Pattern = new(@"^v?(\d{1,6})\.(\d{1,6})\.(\d{1,6})(?:[+-].*)?$", RegexOptions.CultureInvariant);

    public static bool TryParse(string? text, out SemVer version)
    {
        version = default;
        var m = Pattern.Match((text ?? "").Trim());
        if (!m.Success) return false;
        version = new SemVer(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
        return true;
    }

    public int CompareTo(SemVer other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        return c != 0 ? c : Patch.CompareTo(other.Patch);
    }

    public static bool operator >(SemVer a, SemVer b) => a.CompareTo(b) > 0;
    public static bool operator <(SemVer a, SemVer b) => a.CompareTo(b) < 0;
    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

/// <summary>This build's version (stamped from inventor-addin/VERSION) and the release file naming rules.</summary>
public static class AppVersion
{
    public const string ReleaseFolder = "$/Tools/SF Vault Bridge";
    private const string Prefix = "SF Vault Bridge ";
    private static readonly Regex NamePattern = new(@"^SF Vault Bridge (\d{1,6}\.\d{1,6}\.\d{1,6})\.zip$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>The informational version of this assembly, without any +build suffix; "0.0.0" if unreadable.</summary>
    public static string Current { get; } = ReadCurrent();

    private static string ReadCurrent()
    {
        var info = typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var core = (info ?? "").Split('+')[0];
        return SemVer.TryParse(core, out var v) ? v.ToString() : "0.0.0";
    }

    public static string FileName(SemVer version) => $"{Prefix}{version}.zip";

    /// <summary>Parses "SF Vault Bridge 1.2.3.zip"; false for anything else.</summary>
    public static bool TryParseFileName(string? name, out SemVer version)
    {
        version = default;
        var m = NamePattern.Match((name ?? "").Trim());
        return m.Success && SemVer.TryParse(m.Groups[1].Value, out version);
    }

    /// <summary>The newest release in the file list that is newer than current; null when up to date. Names that do not match are ignored.</summary>
    public static (SemVer Version, string Name)? Newest(IEnumerable<string> names, SemVer current)
    {
        (SemVer Version, string Name)? best = null;
        foreach (var n in names)
        {
            if (!TryParseFileName(n, out var v) || !(v > current)) continue;
            if (best is null || v > best.Value.Version) best = (v, n);
        }
        return best;
    }
}

/// <summary>Team settings merge used by the installer: team keys overwrite, personal keys stay.
/// Install-Package.ps1 applies the same rules in PowerShell (the engineer's PC has no .NET SDK).
/// Rules: a malformed existing file is never discarded (MergeFile backs it up first); an empty team value
/// (null, blank text, empty list) never replaces a non-empty user value.</summary>
public static class SettingsMerge
{
    public static readonly string[] TeamKeys = { "processValues", "printMaterials", "dropboxAppKey", "salesTaxRate" };
    public static readonly string[] PersonalKeys = { "recentPartners", "userOverrides" };

    private static readonly System.Text.Json.JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>True when the text has content that is not a JSON object. Empty or missing text is not malformed.</summary>
    public static bool IsMalformed(string? existingJson)
    {
        if (string.IsNullOrWhiteSpace(existingJson)) return false;
        try { return JsonNode.Parse(existingJson) is not JsonObject; }
        catch (System.Text.Json.JsonException) { return true; }
    }

    /// <summary>Returns the merged JSON. A malformed existing text yields team keys only; callers that own a file use MergeFile so the old text is kept.</summary>
    public static string Merge(string? existingJson, string teamJson)
    {
        var team = JsonNode.Parse(teamJson) as JsonObject ?? throw new ArgumentException("team settings must be a JSON object");
        var root = string.IsNullOrWhiteSpace(existingJson) || IsMalformed(existingJson)
            ? new JsonObject()
            : (JsonNode.Parse(existingJson) as JsonObject ?? new JsonObject());
        var overridden = SettingsRules.CleanOverrides(root["userOverrides"] is JsonArray ua
            ? ua.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : "")
            : null);
        foreach (var key in TeamKeys)
        {
            if (overridden.Contains(key, StringComparer.Ordinal)) continue; // the user changed it in the Settings window
            if (team[key] is not { } value) continue;
            if (IsEmpty(value) && root[key] is { } current && !IsEmpty(current)) continue;
            root[key] = value.DeepClone();
        }
        return root.ToJsonString(Indented);
    }

    /// <summary>Merges into the file at path. A malformed file is copied to path.bad-yyyyMMdd-HHmmss first; returns that backup path or null.</summary>
    public static string? MergeFile(string path, string teamJson, DateTime now)
    {
        string? existing = File.Exists(path) ? File.ReadAllText(path) : null;
        string? backup = null;
        if (IsMalformed(existing))
        {
            backup = $"{path}.bad-{now:yyyyMMdd-HHmmss}";
            File.Copy(path, backup, false);
        }
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        AtomicFile.WriteAllText(path, Merge(existing, teamJson));
        return backup;
    }

    private static bool IsEmpty(JsonNode node) => node switch
    {
        JsonArray a => a.Count == 0,
        JsonValue v when v.TryGetValue<string>(out var s) => string.IsNullOrWhiteSpace(s),
        _ => false,
    };
}
