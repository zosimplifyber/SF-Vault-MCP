using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SFVaultBridge.Core.McMaster;

/// <summary>
/// The OAuth loopback redirect: a one-shot listener on 127.0.0.1 and a random free port (RFC 8252 section 7.3). A raw socket, not
/// HttpListener, so no URL reservation or admin right is needed. Serves GET /callback only; every other request gets a 404.
/// </summary>
public sealed class LoopbackRedirect : IDisposable
{
    private const int MaxRequestBytes = 16 * 1024;
    private readonly TcpListener _listener;

    public LoopbackRedirect()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public int Port { get; }

    public string RedirectUri => McMasterOAuth.LoopbackRedirect(Port);

    /// <summary>How long one connection may take to send its request line before it is dropped (idle browser preconnects).</summary>
    public static readonly TimeSpan ConnectionReadTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Waits for the browser to come back with the code. Each connection is served on its own task with its own read timeout, so an
    /// idle socket (a browser's speculative preconnect, or any local process that connects and sends nothing) never holds up the real
    /// callback. A wrong state is answered and ignored (another tab), an error from the sign-in server ends the wait with
    /// McMasterSignInCancelled, and the timeout or ct ends it with McMasterSignInCancelled too.
    /// </summary>
    public async Task<string> WaitForCodeAsync(string state, TimeSpan timeout, CancellationToken ct, TimeSpan? readTimeout = null)
    {
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        using var reg = linked.Token.Register(() => done.TrySetCanceled());
        _ = AcceptLoopAsync(state, done, readTimeout ?? ConnectionReadTimeout, linked.Token);
        try { return await done.Task.ConfigureAwait(false); }
        catch (TaskCanceledException)
        {
            if (ct.IsCancellationRequested) throw new McMasterSignInCancelled("McMaster sign-in was cancelled.");
            throw new McMasterSignInCancelled($"McMaster sign-in timed out after {timeout.TotalMinutes:0} minutes.");
        }
        finally
        {
            try { linked.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    private async Task AcceptLoopAsync(string state, TaskCompletionSource<string> done, TimeSpan readTimeout, CancellationToken token)
    {
        while (!token.IsCancellationRequested && !done.Task.IsCompleted)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false); }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException or InvalidOperationException) { return; }
            _ = HandleAsync(client, state, done, readTimeout, token);
        }
    }

    private static async Task HandleAsync(TcpClient client, string state, TaskCompletionSource<string> done, TimeSpan readTimeout, CancellationToken token)
    {
        using (client)
        {
            string? target;
            try
            {
                using var read = CancellationTokenSource.CreateLinkedTokenSource(token);
                read.CancelAfter(readTimeout);
                target = await ReadTargetAsync(client.GetStream(), read.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or SocketException or ObjectDisposedException) { return; }
            if (target is null) { await ReplyAsync(client, 400, "Bad request.").ConfigureAwait(false); return; }
            var q = target.IndexOf('?');
            var path = q < 0 ? target : target[..q];
            if (!string.Equals(path, McMasterOAuth.CallbackPath, StringComparison.Ordinal))
            {
                await ReplyAsync(client, 404, "Waiting for the McMaster sign-in.").ConfigureAwait(false);
                return;
            }
            var (kind, value) = McMasterOAuth.JudgeCallback(McMasterOAuth.ParseQuery(q < 0 ? "" : target[(q + 1)..]), state);
            switch (kind)
            {
                case McMasterOAuth.CallbackKind.Ignore:
                    await ReplyAsync(client, 404, "Waiting for the McMaster sign-in.").ConfigureAwait(false);
                    return;
                case McMasterOAuth.CallbackKind.StateMismatch:
                    await ReplyAsync(client, 400, "Sign-in state did not match. Close this tab and try again from Inventor.").ConfigureAwait(false);
                    return;
                case McMasterOAuth.CallbackKind.Error:
                    await ReplyAsync(client, 200, "McMaster sign-in was not completed. You can close this tab.").ConfigureAwait(false);
                    done.TrySetException(new McMasterSignInCancelled("McMaster sign-in was not completed (" + value + ")."));
                    return;
                default:
                    await ReplyAsync(client, 200, "Signed in to McMaster-Carr for SF Vault Bridge. You can close this tab and go back to Inventor.").ConfigureAwait(false);
                    done.TrySetResult(value);
                    return;
            }
        }
    }

    /// <summary>The request target of a GET request line ("/callback?code=..."); null for anything else.</summary>
    internal static async Task<string?> ReadTargetAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxRequestBytes];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) break;
            read += n;
            if (Encoding.ASCII.GetString(buffer, 0, read).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
        }
        return RequestTarget(Encoding.ASCII.GetString(buffer, 0, read));
    }

    /// <summary>"GET /callback?x=1 HTTP/1.1" gives "/callback?x=1"; anything but a GET gives null.</summary>
    public static string? RequestTarget(string head)
    {
        var line = head.Split("\r\n", 2)[0];
        var parts = line.Split(' ');
        if (parts.Length != 3 || parts[0] != "GET" || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal) || !parts[1].StartsWith('/')) return null;
        return parts[1];
    }

    private static async Task ReplyAsync(TcpClient client, int status, string message)
    {
        try
        {
            var body = Encoding.UTF8.GetBytes("<!doctype html><html><head><meta charset=\"utf-8\"><title>SF Vault Bridge</title></head>" +
                                              "<body style=\"font-family:sans-serif\"><p>" + WebUtility.HtmlEncode(message) + "</p></body></html>");
            var reason = status switch { 200 => "OK", 400 => "Bad Request", _ => "Not Found" };
            var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\n" +
                                               "Cache-Control: no-store\r\nConnection: close\r\n\r\n");
            var s = client.GetStream();
            await s.WriteAsync(head).ConfigureAwait(false);
            await s.WriteAsync(body).ConfigureAwait(false);
            await s.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException) { }
    }

    public void Dispose()
    {
        try { _listener.Stop(); } catch (SocketException) { }
    }
}
