using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace SFVaultBridge.Core;

/// <summary>Local HTTP listener. One thread accepts; every request is served on its own pool thread. Only loopback peers
/// with a localhost Host header are served, and the token and Origin are judged before the (size-capped) body is read.</summary>
public sealed class HttpHost : IDisposable
{
    public const string TokenHeader = "X-Bridge-Token";
    public const int MaxBodyBytes = 1024 * 1024;
    public static readonly TimeSpan BodyReadDeadline = TimeSpan.FromSeconds(30);

    private readonly HttpListener _listener = new();
    private readonly Router _router;
    private readonly Action<string> _log;
    private readonly HashSet<string> _allowedHosts;
    private Thread? _thread;

    public bool IsListening
    {
        get { try { return _listener.IsListening; } catch (ObjectDisposedException) { return false; } }
    }

    public DateTime? LastRequestUtc { get; private set; }

    /// <summary>When set, POST /mcp is served by this MCP endpoint (same token, localhost only).</summary>
    public McpServer? Mcp { get; set; }

    public HttpHost(string prefix, Router router, Action<string> log)
    {
        _listener.Prefixes.Add(prefix);
        _router = router;
        _log = log;
        var port = new Uri(prefix.Replace("+", "localhost").Replace("*", "localhost")).Port;
        _allowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { $"localhost:{port}", $"127.0.0.1:{port}", $"[::1]:{port}" };
    }

    public void Start()
    {
        _listener.Start();
        _thread = new Thread(Loop) { IsBackground = true, Name = "SFVaultBridge" };
        _thread.Start();
    }

    // A throwing log sink must never escape a background thread: an unhandled exception there terminates Inventor.
    private void SafeLog(string message)
    {
        try { _log(message); } catch { }
    }

    private static readonly HashSet<string> WriteOps = new(StringComparer.Ordinal)
    {
        "generate_file_number", "add_folder", "add_file", "update_file_categories", "import_step_as_part",
    };

    private const int LogLimit = 500;

    private static string Truncate(string s) => s.Length <= LogLimit ? s : s[..LogLimit] + "...";

    // Every request gets an outcome line. Errors include the reply; successful writes include request and reply.
    private static string Describe(string path, int status, string body, string reply)
    {
        var line = $"{path} -> {status}";
        if (status >= 400) return $"{line} {reply}";
        var op = path.StartsWith("/op/", StringComparison.Ordinal) ? path["/op/".Length..].TrimEnd('/') : "";
        return WriteOps.Contains(op) ? $"{line} request={Truncate(body)} reply={Truncate(reply)}" : line;
    }

    private void Loop()
    {
        try
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch (Exception) when (!_listener.IsListening) { return; }
                catch (Exception ex)
                {
                    SafeLog("accept failed: " + ex.Message);
                    Thread.Sleep(200);
                    continue;
                }

                LastRequestUtc = DateTime.UtcNow;
                // A long Inventor call must not block status, tools/list or other clients. Inventor work itself is still
                // serialized on the UI thread by the operations' onUi marshal.
                ThreadPool.QueueUserWorkItem(_ => Serve(ctx));
            }
        }
        catch (Exception ex)
        {
            // Last resort: nothing may escape the background thread. Stop listening so clients are refused, not left hanging.
            SafeLog("listener loop stopped: " + ex.Message);
            try { _listener.Stop(); } catch { }
        }
    }

    /// <summary>Loopback peer and a localhost Host header with the bridge port. Null when fine, else the refusal text.</summary>
    public string? CheckPeer(IPEndPoint? remote, string? hostHeader)
    {
        var addr = remote?.Address;
        if (addr is null) return "Unknown peer address.";
        if (addr.IsIPv4MappedToIPv6) addr = addr.MapToIPv4();
        if (!IPAddress.IsLoopback(addr)) return "Only local connections are accepted.";
        if (string.IsNullOrEmpty(hostHeader) || !_allowedHosts.Contains(hostHeader))
            return "Host header must be localhost, 127.0.0.1 or [::1] with the bridge port.";
        return null;
    }

    private static void Reply(HttpListenerContext ctx, int status, string? body, string? sessionId = null, string? allow = null)
    {
        var bytes = string.IsNullOrEmpty(body) ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = status;
        if (sessionId is not null) ctx.Response.Headers[McpServer.SessionHeader] = sessionId;
        if (allow is not null) ctx.Response.Headers["Allow"] = allow;
        if (bytes.Length > 0) ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        if (bytes.Length > 0) ctx.Response.OutputStream.Write(bytes);
        ctx.Response.Close();
    }

    private static string ErrorJson(string code, string message) =>
        new JsonObject { ["error"] = code, ["message"] = message }.ToJsonString();

    /// <summary>Reads at most MaxBodyBytes within the deadline. Returns null after replying (413) or aborting (too slow).</summary>
    private string? ReadBody(HttpListenerContext ctx, string path)
    {
        if (ctx.Request.ContentLength64 > MaxBodyBytes) return TooLarge(ctx, path);
        var read = Task.Run(() =>
        {
            using var ms = new MemoryStream();
            var buf = new byte[8192];
            int n;
            while ((n = ctx.Request.InputStream.Read(buf, 0, buf.Length)) > 0)
            {
                ms.Write(buf, 0, n);
                if (ms.Length > MaxBodyBytes) return (string?)null;
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        });
        if (Task.WaitAny(new Task[] { read }, BodyReadDeadline) < 0)
        {
            SafeLog($"{path} -> body read timed out");
            try { ctx.Response.Abort(); } catch { }
            read.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            return null;
        }
        string? body;
        try { body = read.GetAwaiter().GetResult(); }
        catch (Exception) { try { ctx.Response.Abort(); } catch { } return null; }
        return body ?? TooLarge(ctx, path);
    }

    private string? TooLarge(HttpListenerContext ctx, string path)
    {
        Reply(ctx, 413, ErrorJson("too_large", $"Body is larger than {MaxBodyBytes} bytes."));
        SafeLog($"{path} -> 413");
        return null;
    }

    private void Serve(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            var h = ctx.Request.Headers;
            var denied = CheckPeer(ctx.Request.RemoteEndPoint, h["Host"]);
            if (denied is not null)
            {
                Reply(ctx, 403, ErrorJson("forbidden", denied));
                SafeLog($"{path} -> 403 {denied}");
                return;
            }
            var isMcp = Mcp is not null && (path == "/mcp" || path == "/mcp/");
            // Origin and token are judged from headers alone, before the body is read.
            if (isMcp)
            {
                var pre = Mcp!.Precheck(h["Origin"], h[TokenHeader], h["Authorization"]);
                if (pre is not null)
                {
                    SafeLog($"{path} -> {pre.Status} {pre.Body}");
                    Reply(ctx, pre.Status, pre.Body);
                    return;
                }
            }
            else if (!_router.IsAuthorized(h[TokenHeader]))
            {
                var reply = ErrorJson("unauthorized", "Missing or wrong X-Bridge-Token.");
                SafeLog($"{path} -> 401 {reply}");
                Reply(ctx, 401, reply);
                return;
            }
            var body = ReadBody(ctx, path);
            if (body is null) return;

            if (isMcp)
            {
                var mr = Mcp!.Handle(new McpHttpRequest(ctx.Request.HttpMethod, h[TokenHeader], h["Authorization"], h["Origin"], h[McpServer.SessionHeader], body));
                if (mr.Status >= 400) SafeLog($"{path} -> {mr.Status} {mr.Body}");
                Reply(ctx, mr.Status, mr.Body, mr.SessionId, mr.Allow);
                return;
            }
            var (status, json) = _router.Handle(ctx.Request.HttpMethod, path, h[TokenHeader], body);
            SafeLog(Describe(path, status, body, json));
            Reply(ctx, status, json);
        }
        catch (Exception ex)
        {
            SafeLog("request failed: " + ex);
            try { ctx.Response.Abort(); } catch { }
        }
    }

    public void Dispose()
    {
        try { if (_listener.IsListening) _listener.Stop(); } catch (ObjectDisposedException) { }
        _listener.Close();
    }
}
