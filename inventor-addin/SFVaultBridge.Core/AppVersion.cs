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
/// Install-Package.ps1 applies the same rules in PowerShell (the engineer's PC has no .NET SDK).</summary>
public static class SettingsMerge
{
    public static readonly string[] TeamKeys = { "processValues", "printMaterials", "dropboxAppKey", "salesTaxRate" };
    public static readonly string[] PersonalKeys = { "recentPartners" };

    /// <summary>Returns the merged JSON. existing may be null or invalid (then only the team keys are written). Other keys in existing are kept.</summary>
    public static string Merge(string? existingJson, string teamJson)
    {
        var team = JsonNode.Parse(teamJson) as JsonObject ?? throw new ArgumentException("team settings must be a JSON object");
        JsonObject root;
        try { root = (string.IsNullOrWhiteSpace(existingJson) ? null : JsonNode.Parse(existingJson) as JsonObject) ?? new JsonObject(); }
        catch (System.Text.Json.JsonException) { root = new JsonObject(); }
        foreach (var key in TeamKeys)
            if (team[key] is { } value) root[key] = value.DeepClone();
        return root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
}
