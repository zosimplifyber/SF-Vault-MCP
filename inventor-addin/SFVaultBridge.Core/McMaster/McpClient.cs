using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SFVaultBridge.Core.McMaster;

/// <summary>A tool answered with isError, or the server sent a JSON-RPC error. The message is the server's text (no tokens).</summary>
public sealed class McMasterToolException : Exception
{
    public McMasterToolException(string message) : base(message) { }

    /// <summary>True when the text says the daily subscription budget is used up.</summary>
    public bool IsBudget => Message.Contains("budget", StringComparison.OrdinalIgnoreCase);
}

/// <summary>JSON-RPC shaping for MCP Streamable HTTP, and reading replies sent as JSON or as an SSE stream. No I/O.</summary>
public static class McpJson
{
    public const string ProtocolVersion = "2025-06-18";

    public static JsonObject InitializeParams(string clientVersion) => new()
    {
        ["protocolVersion"] = ProtocolVersion,
        ["capabilities"] = new JsonObject(),
        ["clientInfo"] = new JsonObject { ["name"] = McMasterOAuth.ClientName, ["version"] = clientVersion },
    };

    public static string Request(long id, string method, JsonObject? @params = null)
    {
        var o = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (@params is not null) o["params"] = @params;
        return o.ToJsonString();
    }

    public static string Notification(string method) => new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method }.ToJsonString();

    public static JsonObject ToolCallParams(string name, JsonObject arguments) => new() { ["name"] = name, ["arguments"] = arguments };

    /// <summary>
    /// The JSON-RPC response with this id from a reply body: a JSON object, a JSON array (batch) or an SSE stream (data: lines,
    /// events split by a blank line). Null when none matches.
    /// </summary>
    public static JsonObject? FindResponse(string? contentType, string body, long id)
    {
        var sse = (contentType ?? "").Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);
        foreach (var message in sse ? SseMessages(body) : new[] { body })
        {
            JsonNode? node;
            try { node = JsonNode.Parse(message); }
            catch (JsonException) { continue; }
            foreach (var o in node is JsonArray a ? a.OfType<JsonObject>() : node is JsonObject single ? new[] { single } : Array.Empty<JsonObject>())
                if (o["id"] is JsonValue v && v.TryGetValue<long>(out var got) && got == id && (o["result"] is not null || o["error"] is not null))
                    return o;
        }
        return null;
    }

    /// <summary>The data payload of each SSE event.</summary>
    public static IEnumerable<string> SseMessages(string body)
    {
        var data = new StringBuilder();
        foreach (var raw in (body ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.Length == 0)
            {
                if (data.Length > 0) { yield return data.ToString(); data.Clear(); }
                continue;
            }
            if (raw.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(raw.AsSpan(5).TrimStart(' '));
            }
        }
        if (data.Length > 0) yield return data.ToString();
    }

    /// <summary>
    /// The tool's JSON object from a tools/call response: the first text content that parses as an object, else structuredContent
    /// (a lone "result" wrapper is unwrapped). Throws McMasterToolException for isError and for a JSON-RPC error.
    /// </summary>
    public static JsonObject ToolResult(JsonObject response)
    {
        if (response["error"] is JsonObject err)
            throw new McMasterToolException(err["message"]?.ToString() ?? "The McMaster server returned an error.");
        var result = response["result"] as JsonObject ?? throw new McMasterToolException("The McMaster server sent an empty result.");
        var texts = (result["content"] as JsonArray)?.OfType<JsonObject>()
            .Where(c => (string?)c["type"] == "text").Select(c => c["text"]?.ToString() ?? "").ToList() ?? new List<string>();
        if (result["isError"] is JsonValue ie && ie.TryGetValue<bool>(out var isError) && isError)
            throw new McMasterToolException(texts.Count > 0 && texts[0].Length > 0 ? texts[0] : "The McMaster tool failed.");
        foreach (var t in texts)
        {
            try { if (JsonNode.Parse(t) is JsonObject o) return o; }
            catch (JsonException) { }
        }
        if (result["structuredContent"] is JsonObject sc)
            return sc.Count == 1 && sc["result"] is JsonObject inner ? (JsonObject)inner.DeepClone() : (JsonObject)sc.DeepClone();
        throw new McMasterToolException(texts.Count > 0 ? texts[0] : "The McMaster server sent no tool output.");
    }
}

/// <summary>Raised when the server keeps answering 401 after a fresh token: the sign-in no longer works.</summary>
public sealed class McMasterUnauthorized : Exception
{
    public McMasterUnauthorized() : base("The McMaster server did not accept the sign-in. Sign out and sign in again in Settings, McMaster.") { }
}

/// <summary>
/// A small MCP Streamable HTTP client: initialize (once per session), notifications/initialized, tools/call; the Mcp-Session-Id header
/// when the server sets one; JSON or SSE replies. A 401 gets one forced token refresh; a 404 on a session restarts the session once.
/// </summary>
public sealed class McpHttpClient
{
    /// <summary>tokenFor(forceRefresh, ct) returns a bearer token.</summary>
    private readonly Func<bool, CancellationToken, Task<string>> _tokenFor;
    private readonly HttpClient _http;
    private readonly string _clientVersion;
    private readonly SemaphoreSlim _init = new(1, 1);
    private long _nextId = 1;
    private string? _sessionId;
    private string? _protocol;
    private bool _initialized;

    public McpHttpClient(string serverUrl, HttpClient http, Func<bool, CancellationToken, Task<string>> tokenFor, string clientVersion)
    {
        ServerUrl = serverUrl; _http = http; _tokenFor = tokenFor; _clientVersion = clientVersion;
    }

    public string ServerUrl { get; }

    public async Task<JsonObject> CallToolAsync(string name, JsonObject arguments, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var id = Interlocked.Increment(ref _nextId);
        var (status, response) = await SendAsync(McpJson.Request(id, "tools/call", McpJson.ToolCallParams(name, arguments)), id, ct).ConfigureAwait(false);
        if (status == HttpStatusCode.NotFound && _sessionId is not null)
        {
            _initialized = false; _sessionId = null;   // the session expired on the server
            await EnsureInitializedAsync(ct).ConfigureAwait(false);
            id = Interlocked.Increment(ref _nextId);
            (status, response) = await SendAsync(McpJson.Request(id, "tools/call", McpJson.ToolCallParams(name, (JsonObject)arguments.DeepClone())), id, ct).ConfigureAwait(false);
        }
        if (response is null) throw new McMasterToolException($"The McMaster server answered {(int)status} to {name}.");
        return McpJson.ToolResult(response);
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;
        await _init.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            var id = Interlocked.Increment(ref _nextId);
            var (status, response) = await SendAsync(McpJson.Request(id, "initialize", McpJson.InitializeParams(_clientVersion)), id, ct, initialize: true).ConfigureAwait(false);
            if (response is null || response["error"] is not null)
                throw new McMasterToolException("The McMaster server did not start an MCP session (" + (int)status + ").");
            _protocol = response["result"]?["protocolVersion"]?.ToString() ?? McpJson.ProtocolVersion;
            await SendAsync(McpJson.Notification("notifications/initialized"), null, ct).ConfigureAwait(false);
            _initialized = true;
        }
        finally { _init.Release(); }
    }

    private async Task<(HttpStatusCode Status, JsonObject? Response)> SendAsync(string json, long? id, CancellationToken ct, bool initialize = false)
    {
        for (var attempt = 0; ; attempt++)
        {
            var token = await _tokenFor(attempt > 0, ct).ConfigureAwait(false);
            using var req = new HttpRequestMessage(HttpMethod.Post, ServerUrl) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Accept.ParseAdd("application/json");
            req.Headers.Accept.ParseAdd("text/event-stream");
            if (_sessionId is not null && !initialize) req.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
            if (_protocol is not null && !initialize) req.Headers.TryAddWithoutValidation("MCP-Protocol-Version", _protocol);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                if (attempt == 0) continue;
                throw new McMasterUnauthorized();
            }
            if (initialize && resp.Headers.TryGetValues("Mcp-Session-Id", out var sid)) _sessionId = sid.FirstOrDefault();
            if (id is null) return (resp.StatusCode, null);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return (resp.StatusCode, null);
            return (resp.StatusCode, McpJson.FindResponse(resp.Content.Headers.ContentType?.MediaType, body, id.Value));
        }
    }
}
