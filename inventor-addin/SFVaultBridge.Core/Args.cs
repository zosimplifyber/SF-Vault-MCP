using System.Text.Json.Nodes;

namespace SFVaultBridge.Core;

/// <summary>Typed reads from a request body; any problem is a bad_request naming the field.</summary>
public static class Args
{
    public static string Str(JsonObject b, string key) =>
        b[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)
            ? s
            : throw Bad($"'{key}' is required and must be a string.");

    public static string OptStr(JsonObject b, string key, string fallback = "") =>
        b[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : fallback;

    public static bool Bool(JsonObject b, string key, bool fallback) =>
        b[key] is null ? fallback
        : b[key] is JsonValue v && v.TryGetValue<bool>(out var x) ? x
        : throw Bad($"'{key}' must be true or false.");

    public static long Long(JsonObject b, string key) =>
        b[key] is JsonValue v && v.TryGetValue<long>(out var l) ? l : throw Bad($"'{key}' is required and must be an integer.");

    public static long[] Longs(JsonObject b, string key, bool required = true)
    {
        if (b[key] is null)
            return required ? throw Bad($"'{key}' is required and must be a list of integers.") : Array.Empty<long>();
        if (b[key] is not JsonArray a) throw Bad($"'{key}' must be a list of integers.");
        return a.Select((n, i) => n is JsonValue v && v.TryGetValue<long>(out var l) ? l : throw Bad($"'{key}[{i}]' must be an integer.")).ToArray();
    }

    public static Dictionary<string, string> StrMap(JsonObject b, string key)
    {
        if (b[key] is null) return new Dictionary<string, string>();
        if (b[key] is not JsonObject o) throw Bad($"'{key}' must be an object of strings.");
        return o.ToDictionary(
            p => p.Key,
            p => p.Value is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw Bad($"'{key}.{p.Key}' must be a string."));
    }

    private static BridgeException Bad(string message) => new("bad_request", message);
}
