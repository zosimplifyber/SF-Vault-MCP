using System.Text;

namespace SFVaultBridge.Core.McMaster;

/// <summary>
/// HTTP rules for every McMaster sign-in, MCP and download call: redirects are never followed (a 307/308 would re-send a code, a
/// refresh token or a bearer to wherever it points), a 3xx is an error, and reply bodies are size-capped.
/// </summary>
public static class McMasterHttp
{
    public const int MaxReplyBytes = 10 * 1024 * 1024;

    public static SocketsHttpHandler NewHandler() => new() { AllowAutoRedirect = false };

    public static HttpClient NewClient(TimeSpan timeout) => new(NewHandler()) { Timeout = timeout };

    public static bool IsRedirect(HttpResponseMessage resp) => (int)resp.StatusCode is >= 300 and < 400;

    /// <summary>Throws McMasterAuthException for a 3xx from a sign-in call.</summary>
    public static void ThrowOnRedirect(HttpResponseMessage resp, string what)
    {
        if (IsRedirect(resp))
            throw new McMasterAuthException($"{Capital(what)} answered with a redirect ({(int)resp.StatusCode}); SF Vault Bridge does not follow redirects. Check the McMaster server URL in Settings.");
    }

    /// <summary>The body as text, refusing more than max bytes (a stalled or runaway reply cannot fill memory).</summary>
    public static async Task<string> ReadCappedAsync(HttpContent content, CancellationToken ct, int max = MaxReplyBytes)
    {
        if (content.Headers.ContentLength is > 0 and var n && n > max) throw new McMasterToolException("The McMaster server reply is too large.");
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > max) throw new McMasterToolException("The McMaster server reply is too large.");
            buffer.Write(chunk, 0, read);
        }
        var charset = content.Headers.ContentType?.CharSet;
        Encoding enc;
        try { enc = string.IsNullOrEmpty(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset.Trim('"')); }
        catch (ArgumentException) { enc = Encoding.UTF8; }
        return enc.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
