using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SFVaultBridge.Core;

namespace SFVaultBridge.Addin;

/// <summary>
/// Dropbox OAuth 2 with PKCE. The refresh token lives in a DPAPI (CurrentUser) file; access tokens stay in memory.
/// Nothing here writes a token to a log or an exception message.
/// </summary>
public sealed class DropboxAuth
{
    public const string RedirectUri = "http://localhost:53682/";
    private const string AuthorizeUrl = "https://www.dropbox.com/oauth2/authorize";
    private const string TokenUrl = "https://api.dropboxapi.com/oauth2/token";
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(3);
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SFVaultBridge.Dropbox.v1");

    private readonly string _appKey;
    private readonly string _tokenPath;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _access;
    private DateTime _accessExpiresUtc = DateTime.MinValue;

    public DropboxAuth(string appKey, HttpClient http, string? tokenPath = null)
    {
        _appKey = (appKey ?? "").Trim();
        _http = http;
        _tokenPath = tokenPath ?? DefaultTokenPath;
    }

    public static string DefaultTokenPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Simplifyber", "VaultBridge", "dropbox.token");

    /// <summary>Raised after a sign-in or sign-out so callers drop account-specific caches.</summary>
    public event Action? AccountChanged;

    public bool IsSignedIn => File.Exists(_tokenPath);

    /// <summary>Deletes the stored refresh token and forgets the access token.</summary>
    public void SignOut()
    {
        RevokeBestEffort();
        AccountChanged?.Invoke();
        _access = null;
        _accessExpiresUtc = DateTime.MinValue;
        try { if (File.Exists(_tokenPath)) File.Delete(_tokenPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new InvalidOperationException("Could not delete the Dropbox sign-in file: " + e.Message); }
    }

    /// <summary>Tells Dropbox to drop the current access token (which also ends its refresh token). Failures are ignored.</summary>
    private void RevokeBestEffort()
    {
        var access = _access;
        if (access is null) return;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.dropboxapi.com/2/auth/token/revoke");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", access);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            _http.Send(req, cts.Token).Dispose();
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException) { }
    }

    /// <summary>Returns a valid access token, refreshing or signing in as needed. forceRefresh drops the cached token first.</summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken ct, bool forceRefresh = false)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (forceRefresh) _access = null;
            if (_access is not null && DateTime.UtcNow < _accessExpiresUtc) return _access;
            var refresh = ReadRefreshToken();
            if (refresh is null) return await SignInCoreAsync(ct).ConfigureAwait(false);
            var json = await PostTokenAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refresh,
                ["client_id"] = _appKey,
            }, ct).ConfigureAwait(false);
            if (json is null) // refresh token revoked or expired: sign in again
            {
                SignOut();
                return await SignInCoreAsync(ct).ConfigureAwait(false);
            }
            return Remember(json);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Runs the browser sign-in even when a token exists (used after a 401 that a refresh did not fix).</summary>
    public async Task<string> SignInAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await SignInCoreAsync(ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<string> SignInCoreAsync(CancellationToken ct)
    {
        if (_appKey.Length == 0) throw new InvalidOperationException("Dropbox app key is not set. Put it in settings.json as dropboxAppKey.");
        var verifier = Pkce.NewVerifier();
        var state = Pkce.Base64Url(RandomNumberGenerator.GetBytes(16));
        var url = AuthorizeUrl + "?" + Query(new Dictionary<string, string>
        {
            ["client_id"] = _appKey,
            ["response_type"] = "code",
            ["redirect_uri"] = RedirectUri,
            ["code_challenge"] = Pkce.Challenge(verifier),
            ["code_challenge_method"] = "S256",
            ["token_access_type"] = "offline",
            ["state"] = state,
        });

        using var listener = new HttpListener();
        listener.Prefixes.Add(RedirectUri);
        try { listener.Start(); }
        catch (HttpListenerException e) { throw new InvalidOperationException("Could not listen on " + RedirectUri + " for the Dropbox sign-in (" + e.Message + "). Close other sign-ins and try again."); }

        using (Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })) { }
        var code = await WaitForCodeAsync(listener, state, ct).ConfigureAwait(false);

        var json = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = _appKey,
            ["redirect_uri"] = RedirectUri,
            ["code_verifier"] = verifier,
        }, ct).ConfigureAwait(false) ?? throw new InvalidOperationException("Dropbox rejected the sign-in code.");
        var refresh = json["refresh_token"]?.GetValue<string>();
        if (string.IsNullOrEmpty(refresh)) throw new InvalidOperationException("Dropbox did not return a refresh token.");
        WriteRefreshToken(refresh);
        AccountChanged?.Invoke();
        return Remember(json);
    }

    private static async Task<string> WaitForCodeAsync(HttpListener listener, string state, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SignInTimeout);
        using var reg = timeout.Token.Register(() => { try { listener.Stop(); } catch (ObjectDisposedException) { } });
        while (true)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException("Dropbox sign-in timed out after 3 minutes.");
            }
            var q = ctx.Request.QueryString;
            var code = q["code"];
            var err = q["error"];
            if (code is null && err is null) { Reply(ctx, 404, "Waiting for the Dropbox sign-in."); continue; }
            if (q["state"] != state) { Reply(ctx, 400, "Sign-in state did not match. Close this tab and try again from Inventor."); continue; }
            if (err is not null)
            {
                Reply(ctx, 200, "Dropbox sign-in was not completed. You can close this tab.");
                throw new InvalidOperationException("Dropbox sign-in was not completed (" + err + ").");
            }
            Reply(ctx, 200, "Signed in to Dropbox. You can close this tab and go back to Inventor.");
            return code!;
        }
    }

    private static void Reply(HttpListenerContext ctx, int status, string message)
    {
        try
        {
            var body = Encoding.UTF8.GetBytes("<html><body style=\"font-family:sans-serif\"><p>" + WebUtility.HtmlEncode(message) + "</p></body></html>");
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.OutputStream.Write(body, 0, body.Length);
            ctx.Response.Close();
        }
        catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or IOException) { }
    }

    /// <summary>Posts to the token endpoint. Returns null on invalid_grant, throws on any other failure.</summary>
    private async Task<JsonObject?> PostTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(form);
        using var resp = await _http.PostAsync(TokenUrl, content, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        JsonObject? obj = null;
        try { obj = JsonNode.Parse(text) as JsonObject; } catch (System.Text.Json.JsonException) { }
        if (resp.IsSuccessStatusCode && obj is not null && obj["access_token"] is not null) return obj;
        var error = obj?["error"]?.GetValue<string>();
        if (error == "invalid_grant") return null;
        // error and error_description are plain text from Dropbox, never a token.
        var desc = obj?["error_description"]?.GetValue<string>();
        throw new InvalidOperationException("Dropbox token request failed (" + (int)resp.StatusCode + (error is null ? "" : " " + error) + (desc is null ? "" : ": " + desc) + ").");
    }

    private string Remember(JsonObject json)
    {
        _access = json["access_token"]!.GetValue<string>();
        var secs = json["expires_in"]?.GetValue<int>() ?? 14400;
        _accessExpiresUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, secs - 120));
        return _access;
    }

    private string? ReadRefreshToken()
    {
        try
        {
            if (!File.Exists(_tokenPath)) return null;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(_tokenPath), Entropy, DataProtectionScope.CurrentUser);
            var s = Encoding.UTF8.GetString(plain);
            return s.Length == 0 ? null : s;
        }
        catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return null; // unreadable or from another user: treated as signed out
        }
    }

    private void WriteRefreshToken(string refresh)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_tokenPath)!);
        File.WriteAllBytes(_tokenPath, ProtectedData.Protect(Encoding.UTF8.GetBytes(refresh), Entropy, DataProtectionScope.CurrentUser));
    }

    private static string Query(Dictionary<string, string> p) =>
        string.Join("&", p.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));
}
