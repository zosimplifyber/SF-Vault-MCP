using System.Net;
using System.Text;

namespace SFVaultBridge.Core;

/// <summary>Local HTTP listener. Requests are handled one at a time on one background thread.</summary>
public sealed class HttpHost : IDisposable
{
    public const string TokenHeader = "X-Bridge-Token";

    private readonly HttpListener _listener = new();
    private readonly Router _router;
    private readonly Action<string> _log;
    private Thread? _thread;

    public HttpHost(string prefix, Router router, Action<string> log)
    {
        _listener.Prefixes.Add(prefix);
        _router = router;
        _log = log;
    }

    public void Start()
    {
        _listener.Start();
        _thread = new Thread(Loop) { IsBackground = true, Name = "SFVaultBridge" };
        _thread.Start();
    }

    // A throwing log sink must never escape the background thread: an unhandled exception there terminates Inventor.
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

                try
                {
                    string body;
                    using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) body = reader.ReadToEnd();
                    var path = ctx.Request.Url?.AbsolutePath ?? "/";
                    var (status, json) = _router.Handle(ctx.Request.HttpMethod, path, ctx.Request.Headers[TokenHeader], body);
                    var bytes = Encoding.UTF8.GetBytes(json);
                    ctx.Response.StatusCode = status;
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.ContentLength64 = bytes.Length;
                    ctx.Response.OutputStream.Write(bytes);
                    ctx.Response.Close();
                    SafeLog(Describe(path, status, body, json));
                }
                catch (Exception ex)
                {
                    SafeLog("request failed: " + ex);
                    try { ctx.Response.Abort(); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            // Last resort: nothing may escape the background thread. Stop listening so clients are refused, not left hanging.
            SafeLog("listener loop stopped: " + ex.Message);
            try { _listener.Stop(); } catch { }
        }
    }

    public void Dispose()
    {
        try { if (_listener.IsListening) _listener.Stop(); } catch (ObjectDisposedException) { }
        _listener.Close();
    }
}
