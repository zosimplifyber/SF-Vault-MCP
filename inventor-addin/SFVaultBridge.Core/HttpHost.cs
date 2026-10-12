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

    private void Loop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = _listener.GetContext(); }
            catch (Exception) when (!_listener.IsListening) { return; }
            catch (Exception ex) { _log("accept failed: " + ex.Message); continue; }

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
                if (status >= 400) _log($"{path} -> {status} {json}");
            }
            catch (Exception ex)
            {
                _log("request failed: " + ex);
                try { ctx.Response.Abort(); } catch { }
            }
        }
    }

    public void Dispose()
    {
        try { if (_listener.IsListening) _listener.Stop(); } catch (ObjectDisposedException) { }
        _listener.Close();
    }
}
