using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace SFVaultBridge.Core.McMaster;

/// <summary>Where the sign-in record lives. The add-in's store encrypts it with DPAPI (CurrentUser); tests use memory.</summary>
public interface ISignInStore
{
    /// <summary>The stored text, or null when there is none or it cannot be read (treated as signed out).</summary>
    string? Load();
    void Save(string text);
    void Delete();
}

/// <summary>Raised when a token is needed, no sign-in is stored, and the caller did not allow a browser sign-in.</summary>
public sealed class McMasterNotSignedIn : Exception
{
    public McMasterNotSignedIn() : base("Not signed in to the McMaster server. Sign in from Settings, McMaster.") { }
}

/// <summary>
/// OAuth 2.1 public client for the McMaster MCP server behind Cloudflare Access managed OAuth. Discovery: the server's 401
/// WWW-Authenticate resource_metadata (else /.well-known/oauth-protected-resource), then the authorization server's RFC 8414
/// metadata. Sign-in: dynamic client registration with a loopback redirect, PKCE S256 and state, in the system browser.
/// The refresh token goes to the store; access tokens stay in memory. Nothing here logs a token, a code or a verifier.
/// </summary>
public sealed class McMasterAuth
{
    public static readonly TimeSpan DefaultSignInTimeout = TimeSpan.FromMinutes(3);

    private readonly HttpClient _http;
    private readonly ISignInStore _store;
    private readonly Func<string, bool> _openBrowser;
    private readonly Action<string> _log;
    private readonly TimeSpan _signInTimeout;
    private readonly Func<DateTime> _utcNow;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _access;
    private DateTime _accessExpiresUtc = DateTime.MinValue;

    public McMasterAuth(string serverUrl, HttpClient http, ISignInStore store, Func<string, bool> openBrowser, Action<string> log,
                        TimeSpan? signInTimeout = null, Func<DateTime>? utcNow = null)
    {
        if (!McMasterOAuth.IsAllowedUrl(serverUrl)) throw new ArgumentException("The McMaster server URL must be an https URL.", nameof(serverUrl));
        ServerUrl = serverUrl.Trim();
        _http = http; _store = store; _openBrowser = openBrowser; _log = log;
        _signInTimeout = signInTimeout ?? DefaultSignInTimeout;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public string ServerUrl { get; }

    /// <summary>Progress text for the UI ("Finish the McMaster sign-in in your browser."). Raised on a pool thread.</summary>
    public event Action<string>? Status;

    /// <summary>True when a refresh token for this server URL is stored.</summary>
    public bool IsSignedIn => McMasterOAuth.IsFor(McMasterOAuth.FromJson(SafeLoad()), ServerUrl);

    /// <summary>
    /// A valid access token: the cached one, else a refresh, else (when interactive) a browser sign-in. forceRefresh drops the cached
    /// token first (after a 401). Throws McMasterNotSignedIn, McMasterSignInCancelled, McMasterAuthException or HttpRequestException.
    /// </summary>
    public async Task<string> GetAccessTokenAsync(bool interactive, CancellationToken ct, bool forceRefresh = false)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (forceRefresh) _access = null;
            if (_access is not null && _utcNow() < _accessExpiresUtc) return _access;
            var stored = McMasterOAuth.FromJson(SafeLoad());
            if (McMasterOAuth.IsFor(stored, ServerUrl))
            {
                var reply = await PostFormAsync(stored!.TokenEndpoint, McMasterOAuth.RefreshForm(stored), ct).ConfigureAwait(false);
                if (reply is not null)
                {
                    if (reply.RefreshToken is not null && reply.RefreshToken != stored.RefreshToken)
                        _store.Save(McMasterOAuth.ToJson(stored with { RefreshToken = reply.RefreshToken }));
                    return Remember(reply);
                }
                _log("McMaster sign-in expired; a new sign-in is needed.");
                SafeDelete();
            }
            if (!interactive) throw new McMasterNotSignedIn();
            return await SignInCoreAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Runs the browser sign-in even when signed in (Settings, Sign in).</summary>
    public async Task SignInAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { await SignInCoreAsync(ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    /// <summary>Revokes the refresh token (best effort) and deletes the stored sign-in.</summary>
    public async Task SignOutAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _access = null;
            _accessExpiresUtc = DateTime.MinValue;
            var stored = McMasterOAuth.FromJson(SafeLoad());
            if (stored?.RevocationEndpoint is { } revoke)
            {
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromSeconds(5));
                    using var content = new FormUrlEncodedContent(McMasterOAuth.RevokeForm(stored));
                    using var resp = await _http.PostAsync(revoke, content, cts.Token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is HttpRequestException or OperationCanceledException) { _log("McMaster sign-out: revoke skipped (" + e.GetType().Name + ")."); }
            }
            _store.Delete();
        }
        finally { _gate.Release(); }
    }

    /// <summary>Discovery: the protected resource (from the 401 or the well-known path) and its authorization server.</summary>
    public async Task<(ProtectedResource Resource, AuthServer Server)> DiscoverAsync(CancellationToken ct)
    {
        var candidates = new List<string>();
        using (var probe = new HttpRequestMessage(HttpMethod.Post, ServerUrl)
        {
            Content = new StringContent(McpJson.Request(0, "initialize", McpJson.InitializeParams("0")), Encoding.UTF8, "application/json"),
        })
        {
            probe.Headers.Accept.ParseAdd("application/json");
            probe.Headers.Accept.ParseAdd("text/event-stream");
            using var resp = await _http.SendAsync(probe, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.Unauthorized && resp.Headers.TryGetValues("WWW-Authenticate", out var values)
                && McMasterOAuth.ResourceMetadataUrl(values) is { } named)
                candidates.Add(named);
        }
        candidates.AddRange(McMasterOAuth.DefaultResourceMetadataUrls(ServerUrl));

        ProtectedResource? resource = null;
        foreach (var url in candidates.Distinct(StringComparer.Ordinal))
        {
            var json = await GetJsonAsync(url, ct).ConfigureAwait(false);
            if (json is null) continue;
            resource = McMasterOAuth.ParseProtectedResource(json, ServerUrl);
            break;
        }
        if (resource is null) throw new McMasterAuthException("The McMaster server does not say where to sign in (no protected resource metadata). Check the server URL in Settings.");

        var issuer = resource.AuthorizationServers[0];
        foreach (var url in McMasterOAuth.AuthServerMetadataUrls(issuer))
        {
            var json = await GetJsonAsync(url, ct).ConfigureAwait(false);
            if (json is null) continue;
            return (resource, McMasterOAuth.ParseAuthServer(json, issuer));
        }
        throw new McMasterAuthException("Could not read the sign-in server's metadata at " + new Uri(issuer).Host + ".");
    }

    private async Task<string> SignInCoreAsync(CancellationToken ct)
    {
        var (resource, server) = await DiscoverAsync(ct).ConfigureAwait(false);
        if (server.RegistrationEndpoint is null)
            throw new McMasterAuthException("The sign-in server does not allow client registration, so SF Vault Bridge cannot sign in. Ask Zak to turn on dynamic client registration in Cloudflare Access.");

        using var loopback = new LoopbackRedirect();
        var redirect = loopback.RedirectUri;
        RegisteredClient client;
        using (var reg = new StringContent(McMasterOAuth.RegistrationRequest(redirect), Encoding.UTF8, "application/json"))
        using (var resp = await _http.PostAsync(server.RegistrationEndpoint, reg, ct).ConfigureAwait(false))
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new McMasterAuthException("The sign-in server refused to register SF Vault Bridge (" + (int)resp.StatusCode + ").");
            client = McMasterOAuth.ParseRegistration(body);
        }

        var verifier = Pkce.NewVerifier();
        var state = Pkce.Base64Url(RandomNumberGenerator.GetBytes(24));
        var url = McMasterOAuth.AuthorizeUrl(server, client.ClientId, redirect, Pkce.Challenge(verifier), state, resource.Resource);
        if (!_openBrowser(url)) throw new McMasterAuthException("Could not open the browser for the McMaster sign-in.");
        _log("McMaster sign-in: browser opened, waiting for the redirect on 127.0.0.1:" + loopback.Port + ".");
        Status?.Invoke($"Finish the McMaster sign-in in your browser (it times out after {_signInTimeout.TotalMinutes:0} minutes).");
        var code = await loopback.WaitForCodeAsync(state, _signInTimeout, ct).ConfigureAwait(false);

        var reply = await PostFormAsync(server.TokenEndpoint, McMasterOAuth.CodeForm(code, redirect, client.ClientId, client.ClientSecret, verifier, resource.Resource), ct).ConfigureAwait(false)
            ?? throw new McMasterAuthException("The sign-in server rejected the sign-in code. Try again.");
        if (reply.RefreshToken is null)
            _log("McMaster sign-in: no refresh token returned; the sign-in lasts only until Inventor closes.");
        else
            _store.Save(McMasterOAuth.ToJson(new StoredSignIn(ServerUrl, resource.Resource, server.Issuer, server.TokenEndpoint, server.RevocationEndpoint,
                                                              client.ClientId, client.ClientSecret, reply.RefreshToken)));
        _log("McMaster sign-in done.");
        Status?.Invoke("Signed in to McMaster.");
        return Remember(reply);
    }

    private async Task<TokenReply?> PostFormAsync(string url, Dictionary<string, string> form, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(form);
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return McMasterOAuth.ParseToken((int)resp.StatusCode, body);
    }

    private async Task<string?> GetJsonAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private string Remember(TokenReply reply)
    {
        _access = reply.AccessToken;
        _accessExpiresUtc = McMasterOAuth.ExpiresAtUtc(_utcNow(), reply.ExpiresInSeconds);
        return _access;
    }

    private string? SafeLoad()
    {
        try { return _store.Load(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException) { return null; }
    }

    private void SafeDelete()
    {
        try { _store.Delete(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _log("McMaster sign-in file could not be deleted: " + e.Message); }
    }
}
