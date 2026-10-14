using System.Text.Json.Nodes;

namespace SFVaultBridge.Core;

/// <summary>What a tool does to Vault. Read: no writes. Preview: computes a change and writes nothing, but unlocks the matching Write tool. Write: needs a preview and confirm.</summary>
public enum McpToolKind { Read, Preview, Write }

/// <summary>One MCP tool. UiThread false means the handler is thread-safe and runs on the request thread (job_status). A handler result that is a JSON object with "ok": false is reported to the client as a tool error (isError).</summary>
public sealed record McpTool(string Name, string Title, string Description, JsonObject InputSchema, McpToolKind Kind,
                             Func<JsonObject, JsonNode?> Handler, TimeSpan? Timeout = null, bool UiThread = true);

/// <summary>Small helpers that build the JSON Schema objects for tool inputs.</summary>
public static class Schema
{
    public static JsonObject Obj(JsonObject properties, params string[] required)
    {
        var o = new JsonObject { ["type"] = "object", ["properties"] = properties, ["additionalProperties"] = false };
        if (required.Length > 0) o["required"] = new JsonArray(required.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray());
        return o;
    }

    public static JsonObject Str(string description) => new() { ["type"] = "string", ["description"] = description };

    public static JsonObject Int(string description, int? min = null, int? max = null, int? dflt = null)
    {
        var o = new JsonObject { ["type"] = "integer", ["description"] = description };
        if (min is { } lo) o["minimum"] = lo;
        if (max is { } hi) o["maximum"] = hi;
        if (dflt is { } d) o["default"] = d;
        return o;
    }

    public static JsonObject Bool(string description, bool? dflt = null)
    {
        var o = new JsonObject { ["type"] = "boolean", ["description"] = description };
        if (dflt is { } d) o["default"] = d;
        return o;
    }

    public static JsonObject StrList(string description) =>
        new() { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = description };

    public static JsonObject StrMap(string description) =>
        new() { ["type"] = "object", ["additionalProperties"] = new JsonObject { ["type"] = "string" }, ["description"] = description };

    public static JsonObject Props(params (string Name, JsonObject Schema)[] items)
    {
        var o = new JsonObject();
        foreach (var (n, s) in items) o[n] = s;
        return o;
    }
}
