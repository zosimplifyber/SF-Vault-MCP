using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SFVaultBridge.Core;

/// <summary>An incoming HTTP request to /mcp, reduced to what the server needs.</summary>
public sealed record McpHttpRequest(string Method, string? BridgeToken, string? Authorization, string? Origin, string? SessionId, string Body);

/// <summary>The reply. Body is null for 202/200 answers with no content.</summary>
public sealed record McpHttpResponse(int Status, string? Body, string? SessionId = null, string? Allow = null);

/// <summary>MCP over Streamable HTTP with JSON responses (no SSE): JSON-RPC 2.0 dispatch, tool registry, auth and origin checks.</summary>
public sealed class McpServer
{
    public const string ProtocolVersion = "2025-06-18";
    public const string ServerName = "sf-vault-bridge";
    public const string SessionHeader = "Mcp-Session-Id";
    public const int MaxResultChars = 200_000;
    public static readonly TimeSpan DefaultCallTimeout = TimeSpan.FromSeconds(90);

    private static readonly string[] SupportedVersions = { "2025-06-18", "2025-03-26", "2024-11-05" };

    private readonly byte[] _token;
    private readonly string _serverVersion;
    private readonly IReadOnlyList<McpTool> _tools;
    private readonly Dictionary<string, McpTool> _byName;
    private readonly Func<Func<JsonNode?>, JsonNode?> _onUi;
    private readonly TimeSpan _callTimeout;
    private readonly Action<string> _log;
    private readonly JobManager _jobs;

    public McpServer(string token, string serverVersion, IEnumerable<McpTool> tools, Func<Func<JsonNode?>, JsonNode?> onUi,
                     TimeSpan? callTimeout = null, Action<string>? log = null, JobManager? jobs = null)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("token is required", nameof(token));
        _token = Encoding.UTF8.GetBytes(token);
        _serverVersion = serverVersion;
        _tools = tools.ToList();
        _byName = _tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
        _onUi = onUi;
        _callTimeout = callTimeout ?? DefaultCallTimeout;
        _log = log ?? (_ => { });
        _jobs = jobs ?? new JobManager(onUi, log: _log);
    }

    public IReadOnlyList<McpTool> Tools => _tools;

    /// <summary>Session ids are issued on initialize and accepted when sent back; the server keeps no per-session state, so a missing or stale id is never an error.</summary>
    /// <summary>Origin and token, judged from headers alone so the host can refuse before reading a body. Null when fine.</summary>
    public McpHttpResponse? Precheck(string? origin, string? bridgeToken, string? authorization)
    {
        if (!OriginAllowed(origin))
            return Http(403, RpcError(null, -32600, "Origin not allowed: the MCP endpoint only serves localhost."));
        if (!TokenMatches(bridgeToken, authorization))
            return Http(401, RpcError(null, -32001, "Missing or wrong token. Send X-Bridge-Token or Authorization: Bearer."));
        return null;
    }

    public McpHttpResponse Handle(McpHttpRequest req)
    {
        var refused = Precheck(req.Origin, req.BridgeToken, req.Authorization);
        if (refused is not null) return refused;
        switch (req.Method)
        {
            case "POST": break;
            case "DELETE": return new McpHttpResponse(200, null);
            default: return new McpHttpResponse(405, RpcError(null, -32600, "Use POST /mcp."), Allow: "POST, DELETE");
        }

        JsonNode? parsed;
        try { parsed = string.IsNullOrWhiteSpace(req.Body) ? null : JsonNode.Parse(req.Body); }
        catch (JsonException ex) { return Http(400, RpcError(null, -32700, "Parse error: " + ex.Message)); }
        if (parsed is JsonArray) return Http(400, RpcError(null, -32600, "Batch requests are not supported; send one message per POST."));
        if (parsed is not JsonObject msg) return Http(400, RpcError(null, -32600, "Body must be one JSON-RPC message object."));

        var hasMethod = msg["method"] is JsonValue mv && mv.TryGetValue<string>(out _);
        var id = msg["id"];
        if (!hasMethod)
        {
            // A client answering a server request, or junk.
            return msg.ContainsKey("result") || msg.ContainsKey("error")
                ? new McpHttpResponse(202, null)
                : Http(400, RpcError(id?.DeepClone(), -32600, "Message has no method."));
        }
        var method = msg["method"]!.GetValue<string>();
        if (id is null && msg.ContainsKey("id")) return Http(400, RpcError(null, -32600, "A request id must not be null."));
        if (id is null) return new McpHttpResponse(202, null); // notification (notifications/initialized, cancelled, ...)

        var idClone = id.DeepClone();
        var p = msg["params"] as JsonObject;
        switch (method)
        {
            case "initialize": return Initialize(idClone, p);
            case "ping": return Http(200, Result(idClone, new JsonObject()));
            case "tools/list": return Http(200, Result(idClone, ListTools()));
            case "tools/call": return Http(200, CallTool(idClone, p));
            default: return Http(200, RpcError(idClone, -32601, $"Method not found: {method}"));
        }
    }

    // ---- checks ----

    internal static bool OriginAllowed(string? origin)
    {
        if (string.IsNullOrEmpty(origin)) return true; // not a browser
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var u)) return false;
        return u.Host is "localhost" or "127.0.0.1" or "[::1]" or "::1";
    }

    private bool TokenMatches(string? bridgeToken, string? authorization)
    {
        var supplied = bridgeToken;
        if (string.IsNullOrEmpty(supplied) && authorization is { } a && a.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            supplied = a["Bearer ".Length..].Trim();
        return !string.IsNullOrEmpty(supplied) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), _token);
    }

    // ---- methods ----

    private McpHttpResponse Initialize(JsonNode id, JsonObject? p)
    {
        var asked = p?["protocolVersion"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        var version = asked is not null && SupportedVersions.Contains(asked) ? asked : ProtocolVersion;
        var result = new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["version"] = _serverVersion },
            ["instructions"] = Instructions,
        };
        return new McpHttpResponse(200, Result(id, result), Guid.NewGuid().ToString("N"));
    }

    public const string Instructions =
        "SF Vault Bridge serves the engineer's own Inventor and Vault session. The vault_* tools and inventor_open_bom only read. " +
        "Anything that writes to Vault is a preview then apply pair: call the preview tool, show the engineer exactly what will change, and only after " +
        "they say yes call the apply or commit tool with confirm true. Never apply without showing the preview.";

    private JsonObject ListTools() => new()
    {
        ["tools"] = new JsonArray(_tools.Select(t => (JsonNode?)new JsonObject
        {
            ["name"] = t.Name,
            ["title"] = t.Title,
            ["description"] = t.Description,
            ["inputSchema"] = t.InputSchema.DeepClone(),
            ["annotations"] = new JsonObject
            {
                ["title"] = t.Title,
                ["readOnlyHint"] = t.Kind != McpToolKind.Write,
                ["destructiveHint"] = false,
                ["idempotentHint"] = t.Kind != McpToolKind.Write,
                ["openWorldHint"] = false,
            },
        }).ToArray()),
    };

    private string CallTool(JsonNode id, JsonObject? p)
    {
        var name = p?["name"] is JsonValue nv && nv.TryGetValue<string>(out var n) ? n : null;
        if (name is null || !_byName.TryGetValue(name, out var tool))
            return RpcError(id, -32602, $"Unknown tool: {name ?? "(none)"}");
        JsonObject args;
        if (p!["arguments"] is null) args = new JsonObject();
        else if (p["arguments"] is JsonObject o) args = (JsonObject)o.DeepClone();
        else return RpcError(id, -32602, "'arguments' must be an object.");

        var sw = Stopwatch.StartNew();
        string text;
        var isError = false;
        try
        {
            var result = RunWithTimeout(tool, args) ?? new JsonObject();
            isError = result is JsonObject ro && ro["ok"] is JsonValue ok && ok.TryGetValue<bool>(out var b) && !b;
            text = result.ToJsonString();
        }
        catch (BridgeException ex)
        {
            isError = true;
            text = ErrorText(ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            isError = true;
            SafeLog($"mcp tool {name} crashed: {ex}");
            text = ErrorText("internal", $"An unexpected error occurred ({ex.GetType().Name}); see bridge.log.");
        }
        if (text.Length > MaxResultChars)
            text = $"[TRUNCATED: the result was {text.Length} characters and was cut at {MaxResultChars}. Narrow the request (limit, levels, a specific file) and call again.]\n" + text[..MaxResultChars];
        SafeLog($"mcp tool {name} ({tool.Kind}) -> {(isError ? "error" : "ok")} in {sw.ElapsedMilliseconds} ms" +
                (tool.Kind == McpToolKind.Read ? "" : " args=" + Truncate(Redacted(args), 500)) +
                (isError ? " " + Truncate(text, 300) : ""));
        var content = new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["isError"] = isError,
        };
        return Result(id, content);
    }

    private JsonNode? RunWithTimeout(McpTool tool, JsonObject args)
    {
        // Writes run as jobs: never abandoned, and a long one hands back a job_id instead of timing out. A write that never touches
        // Inventor (UiThread false) runs its job on a worker thread so Inventor stays responsive.
        if (tool.Kind == McpToolKind.Write) return _jobs.RunOrDetach(tool.Name, () => tool.Handler(args), onUi: tool.UiThread);
        if (!tool.UiThread) return tool.Handler(args);
        return UiCall.Run(_onUi, tool.Name, () => tool.Handler(args), tool.Timeout ?? _callTimeout);
    }

    private static string Redacted(JsonObject args)
    {
        var c = (JsonObject)args.DeepClone();
        if (c.ContainsKey("token")) c["token"] = "***";
        return c.ToJsonString();
    }

    private void SafeLog(string line)
    {
        try { _log(line); } catch { }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "...";

    private static string ErrorText(string code, string message) =>
        new JsonObject { ["error"] = code, ["message"] = message }.ToJsonString();

    // ---- JSON-RPC framing ----

    private static McpHttpResponse Http(int status, string body) => new(status, body);

    private static string Result(JsonNode id, JsonObject result) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToJsonString();

    private static string RpcError(JsonNode? id, int code, string message) =>
        new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        }.ToJsonString();
}
