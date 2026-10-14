using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using SFVaultBridgeRelay;
using Xunit;

namespace SFVaultBridgeRelay.Tests;

sealed record Seen(Dictionary<string, string> Headers, JsonObject? Body);

sealed class FakeServer : IDisposable
{
    readonly HttpListener _l = new();
    public List<Seen> Seen { get; } = new();
    public string Url { get; }

    public FakeServer(Action<Seen, HttpListenerResponse> handler)
    {
        var port = Random.Shared.Next(20000, 60000);
        while (true)
        {
            try
            {
                _l.Prefixes.Clear();
                _l.Prefixes.Add($"http://127.0.0.1:{port}/");
                _l.Start();
                break;
            }
            catch (HttpListenerException) { port++; }
        }
        Url = $"http://127.0.0.1:{port}/mcp";
        _ = Task.Run(async () =>
        {
            while (_l.IsListening)
            {
                HttpListenerContext c;
                try { c = await _l.GetContextAsync(); } catch { return; }
                var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string k in c.Request.Headers) h[k] = c.Request.Headers[k]!;
                string b;
                using (var r = new StreamReader(c.Request.InputStream, Encoding.UTF8)) b = r.ReadToEnd();
                var rec = new Seen(h, b.Length > 0 ? JsonNode.Parse(b) as JsonObject : null);
                lock (Seen) Seen.Add(rec);
                handler(rec, c.Response);
                c.Response.Close();
            }
        });
    }

    public void Dispose() { try { _l.Stop(); _l.Close(); } catch { } }
}

public class RelayTests
{
    static void Json(HttpListenerResponse res, string body, string? sid = null)
    {
        res.StatusCode = 200;
        res.ContentType = "application/json";
        if (sid != null) res.AddHeader("Mcp-Session-Id", sid);
        res.OutputStream.Write(Encoding.UTF8.GetBytes(body));
    }

    static string TmpToken(string v)
    {
        var f = Path.Combine(Path.GetTempPath(), "sfvb-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(f, v + "\n");
        return f;
    }

    static (Relay relay, List<string> output) Make(string url, string tokenFile)
    {
        var o = new List<string>();
        return (new Relay(url, tokenFile, l => { lock (o) o.Add(l); }, _ => { }, "9.9.9", TimeSpan.FromSeconds(10)), o);
    }

    [Fact]
    public async Task RoundTripSendsHeadersAndWritesResponse()
    {
        using var s = new FakeServer((r, res) => Json(res, $"{{\"jsonrpc\":\"2.0\",\"id\":{r.Body!["id"]},\"result\":{{\"ok\":1}}}}"));
        var (relay, o) = Make(s.Url, TmpToken("tok1"));
        await relay.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}");
        Assert.Single(o);
        var m = JsonNode.Parse(o[0])!;
        Assert.Equal(1, (int)m["id"]!);
        Assert.Equal(1, (int)m["result"]!["ok"]!);
        var h = s.Seen[0].Headers;
        Assert.StartsWith("application/json", h["Content-Type"]);
        Assert.Equal("application/json, text/event-stream", h["Accept"]);
        Assert.Equal("tok1", h["X-Bridge-Token"]);
    }

    [Fact]
    public async Task SessionIdFromInitializeIsCarried()
    {
        using var s = new FakeServer((r, res) => Json(res, $"{{\"jsonrpc\":\"2.0\",\"id\":{r.Body!["id"]},\"result\":{{}}}}",
            (string?)r.Body["method"] == "initialize" ? "sess-9" : null));
        var (relay, _) = Make(s.Url, TmpToken("t"));
        await relay.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}");
        await relay.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}");
        Assert.False(s.Seen[0].Headers.ContainsKey("Mcp-Session-Id"));
        Assert.Equal("sess-9", s.Seen[1].Headers["Mcp-Session-Id"]);
    }

    [Fact]
    public async Task Http401RereadsTokenAndRetries()
    {
        var f = TmpToken("old");
        using var s = new FakeServer((r, res) =>
        {
            if (r.Headers.GetValueOrDefault("X-Bridge-Token") != "new") { res.StatusCode = 401; return; }
            Json(res, $"{{\"jsonrpc\":\"2.0\",\"id\":{r.Body!["id"]},\"result\":{{\"ok\":true}}}}");
        });
        var (relay, o) = Make(s.Url, f);
        File.WriteAllText(f, "new\n");
        await relay.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/list\"}");
        Assert.Equal(2, s.Seen.Count);
        Assert.Equal("new", s.Seen[1].Headers["X-Bridge-Token"]);
        Assert.True((bool)JsonNode.Parse(o[0])!["result"]!["ok"]!);
    }

    [Fact]
    public async Task NotificationsAreForwardedAndNothingWrittenBack()
    {
        using var s = new FakeServer((r, res) => { res.StatusCode = 202; });
        var (relay, o) = Make(s.Url, TmpToken("t"));
        await relay.HandleAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
        Assert.Single(s.Seen);
        Assert.Empty(o);
    }

    [Fact]
    public async Task EventStreamResponsesAreParsed()
    {
        using var s = new FakeServer((r, res) =>
        {
            res.StatusCode = 200;
            res.ContentType = "text/event-stream";
            var b = Encoding.UTF8.GetBytes($"event: message\r\ndata: {{\"jsonrpc\":\"2.0\",\"id\":{r.Body!["id"]},\"result\":{{\"s\":1}}}}\r\n\r\n");
            res.OutputStream.Write(b);
        });
        var (relay, o) = Make(s.Url, TmpToken("t"));
        await relay.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/list\"}");
        Assert.Single(o);
        Assert.Equal(1, (int)JsonNode.Parse(o[0])!["result"]!["s"]!);
    }

    [Fact]
    public async Task BridgeDownAnswersLocally()
    {
        string url;
        using (var s = new FakeServer((r, res) => { })) url = s.Url;
        var (relay, o) = Make(url, TmpToken("t"));
        await relay.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\"}}");
        await relay.HandleAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
        await relay.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}");
        await relay.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"x\"}}");
        Assert.Equal(3, o.Count);
        Assert.Equal("sf-vault-bridge-relay", (string?)JsonNode.Parse(o[0])!["result"]!["serverInfo"]!["name"]);
        Assert.Empty(JsonNode.Parse(o[1])!["result"]!["tools"]!.AsArray());
        var call = JsonNode.Parse(o[2])!["result"]!;
        Assert.True((bool)call["isError"]!);
        Assert.Equal(Relay.DownMessage, (string?)call["content"]![0]!["text"]);
    }

    [Fact]
    public async Task ServerDiscoverIsAnsweredLocallyWithMethodNotFound()
    {
        using var s = new FakeServer((r, res) => Json(res, "{}"));
        var (relay, o) = Make(s.Url, TmpToken("t"));
        await relay.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"server/discover\"}");
        Assert.Empty(s.Seen);
        var m = JsonNode.Parse(o[0])!;
        Assert.Equal(7, (int)m["id"]!);
        Assert.Equal(-32601, (int)m["error"]!["code"]!);
    }

    [Fact]
    public async Task IdZeroIsARequestNotANotification()
    {
        using var s = new FakeServer((r, res) => Json(res, "{\"jsonrpc\":\"2.0\",\"id\":0,\"result\":{\"z\":1}}"));
        var (relay, o) = Make(s.Url, TmpToken("t"));
        await relay.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"initialize\",\"params\":{}}");
        Assert.Single(o);
        Assert.Equal(0, (int)JsonNode.Parse(o[0])!["id"]!);
    }

    [Fact]
    public async Task NullIdGetsAnInvalidRequestErrorAndIsNotForwarded()
    {
        using var s = new FakeServer((r, res) => Json(res, "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}"));
        var (relay, o) = Make(s.Url, TmpToken("t"));
        await relay.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":null,\"method\":\"tools/list\"}");
        Assert.Empty(s.Seen);
        var m = JsonNode.Parse(o.Single())!;
        Assert.Null(m["id"]);
        Assert.Equal(-32600, (int)m["error"]!["code"]!);
    }

    [Fact]
    public async Task IdZeroGetsLocalAnswerWhenBridgeDown()
    {
        string url;
        using (var s = new FakeServer((r, res) => { })) url = s.Url;
        var (relay, o) = Make(url, TmpToken("t"));
        await relay.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"initialize\",\"params\":{}}");
        Assert.Single(o);
        Assert.Equal(0, (int)JsonNode.Parse(o[0])!["id"]!);
    }

    [Fact]
    public async Task ConcurrentHandlesWriteWholeLines()
    {
        using var s = new FakeServer((r, res) => Json(res, $"{{\"jsonrpc\":\"2.0\",\"id\":{r.Body!["id"]},\"result\":{{}}}}"));
        var (relay, o) = Make(s.Url, TmpToken("t"));
        await Task.WhenAll(Enumerable.Range(1, 20).Select(i => relay.HandleAsync($"{{\"jsonrpc\":\"2.0\",\"id\":{i},\"method\":\"tools/list\"}}")));
        Assert.Equal(20, o.Count);
        foreach (var l in o) Assert.NotNull(JsonNode.Parse(l));
    }
}
