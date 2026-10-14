using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SFVaultBridgeRelay;

/// <summary>stdio MCP (newline-delimited JSON-RPC) to HTTP POST relay for the SF Vault Bridge add-in.</summary>
public sealed class Relay
{
    public const string DownMessage = "Start Inventor and sign in to Vault; the SF Vault Bridge add-in serves these tools.";
    public const string SlowMessage = "SF Vault Bridge is still busy in Inventor (a dialog may be open there). If this was a write, call job_status before retrying.";
    const string ProtocolVersion = "2025-06-18";
    static readonly JsonSerializerOptions Out = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    readonly string _url;
    readonly string _tokenFile;
    readonly Action<string> _write;
    readonly Action<string> _log;
    readonly string _version;
    readonly TimeSpan _timeout;
    readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    readonly object _writeLock = new();
    readonly object _stateLock = new();
    string? _token;
    string? _sessionId;

    public Relay(string url, string tokenFile, Action<string> write, Action<string> log, string version, TimeSpan? timeout = null)
    {
        _url = url; _tokenFile = tokenFile; _write = write; _log = log; _version = version;
        _timeout = timeout ?? TimeSpan.FromSeconds(120);
        ReadToken();
    }

    public bool HasToken { get { lock (_stateLock) return _token != null; } }

    public static string DefaultTokenFile()
    {
        var appData = Environment.GetEnvironmentVariable("APPDATA");
        if (string.IsNullOrEmpty(appData))
            appData = Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE") ?? ".", "AppData", "Roaming");
        return Path.Combine(appData, "Simplifyber", "VaultBridge", "token");
    }

    void ReadToken()
    {
        try
        {
            var t = File.ReadAllText(_tokenFile, Encoding.UTF8).Trim();
            lock (_stateLock) _token = t.Length > 0 ? t : null;
        }
        catch (Exception e)
        {
            lock (_stateLock) _token = null;
            _log($"token not readable at {_tokenFile}: {e.GetType().Name}");
        }
    }

    void Emit(JsonNode obj)
    {
        var line = obj.ToJsonString(Out);
        lock (_writeLock) _write(line);
    }

    static JsonObject Reply(JsonNode? id) => new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone() };

    static JsonObject Error(JsonNode? id, int code, string message)
    {
        var o = Reply(id);
        o["error"] = new JsonObject { ["code"] = code, ["message"] = message };
        return o;
    }

    JsonObject LocalAnswer(JsonObject msg, string? method)
    {
        var id = msg["id"];
        var o = Reply(id);
        switch (method)
        {
            case "initialize":
                string? pv = null;
                if (msg["params"] is JsonObject p && p["protocolVersion"] is JsonValue v && v.TryGetValue<string>(out var s)) pv = s;
                o["result"] = new JsonObject
                {
                    ["protocolVersion"] = string.IsNullOrEmpty(pv) ? ProtocolVersion : pv,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "sf-vault-bridge-relay", ["version"] = _version },
                };
                return o;
            case "tools/list":
                _log("bridge unreachable: returning an empty tool list. " + DownMessage);
                o["result"] = new JsonObject { ["tools"] = new JsonArray() };
                return o;
            case "tools/call":
                o["result"] = new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = DownMessage }),
                    ["isError"] = true,
                };
                return o;
            case "ping":
                o["result"] = new JsonObject();
                return o;
            default:
                return Error(id, -32000, DownMessage);
        }
    }

    async Task<HttpResponseMessage> PostAsync(string body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, _url);
        req.Content = new StringContent(body, new UTF8Encoding(false), "application/json");
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        string? token, sid;
        lock (_stateLock) { token = _token; sid = _sessionId; }
        if (token != null) req.Headers.TryAddWithoutValidation("X-Bridge-Token", token);
        if (sid != null) req.Headers.TryAddWithoutValidation("Mcp-Session-Id", sid);
        return await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
    }

    List<JsonNode> ParseResponse(string text, string ctype)
    {
        var res = new List<JsonNode>();
        if (ctype.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var d = line[5..].Trim();
                if (d.Length == 0) continue;
                try { var n = JsonNode.Parse(d); if (n != null) res.Add(n); } catch { _log("bad SSE data line"); }
            }
        }
        else if (text.Trim().Length > 0)
        {
            try
            {
                var n = JsonNode.Parse(text);
                if (n is JsonArray a) { foreach (var x in a) { if (x != null) res.Add(x.DeepClone()); } }
                else if (n != null) res.Add(n);
            }
            catch { _log("non-JSON response body"); }
        }
        return res;
    }

    /// <summary>Handle one stdin line. Safe to call concurrently.</summary>
    public async Task HandleAsync(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        JsonObject? msg;
        try { msg = JsonNode.Parse(line) as JsonObject; } catch { msg = null; }
        if (msg == null) { _log("ignoring unparseable stdin line"); return; }

        var id = msg["id"];
        var isNotification = id == null; // JSON null parses to a C# null; id 0 is a real node
        string? method = null;
        if (msg["method"] is JsonValue mv && mv.TryGetValue<string>(out var ms)) method = ms;

        // Never forward the version probe; answer at once so Claude Desktop does not wait.
        if (method == "server/discover")
        {
            if (!isNotification) Emit(Error(id, -32601, "Method not found"));
            return;
        }

        var body = msg.ToJsonString(Out);
        HttpResponseMessage res;
        try
        {
            using var cts = new CancellationTokenSource(_timeout);
            res = await PostAsync(body, cts.Token);
            if (res.StatusCode == HttpStatusCode.Unauthorized)
            {
                res.Dispose();
                ReadToken();
                res = await PostAsync(body, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            _log("bridge request timed out");
            if (!isNotification) Emit(Error(id, -32002, SlowMessage));
            return;
        }
        catch (Exception e)
        {
            _log($"bridge request failed: {(e.InnerException ?? e).GetType().Name}");
            if (!isNotification) Emit(LocalAnswer(msg, method));
            return;
        }

        using (res)
        {
            string text;
            try { text = await res.Content.ReadAsStringAsync(); } catch { text = ""; }
            if (method == "initialize" && res.Headers.TryGetValues("Mcp-Session-Id", out var sids))
            {
                var sid = sids.FirstOrDefault();
                if (!string.IsNullOrEmpty(sid)) lock (_stateLock) _sessionId = sid;
            }
            if (isNotification) return;
            var status = (int)res.StatusCode;
            if (status is 401 or 403)
            {
                _log($"bridge rejected the token (HTTP {status})");
                Emit(Error(id, -32001, $"SF Vault Bridge refused the token (HTTP {status}). Restart Inventor so the add-in rewrites it."));
                return;
            }
            var ctype = res.Content.Headers.ContentType?.ToString() ?? "";
            var msgs = ParseResponse(text, ctype);
            if (msgs.Count == 0)
            {
                _log($"empty response (HTTP {status})");
                Emit(Error(id, -32603, $"SF Vault Bridge returned HTTP {status} with no body."));
                return;
            }
            foreach (var m in msgs) Emit(m);
        }
    }
}
