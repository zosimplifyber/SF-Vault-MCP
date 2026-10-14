using System.Net.Sockets;
using System.Text;
using SFVaultBridge.Core;
using SFVaultBridge.Core.McMaster;

namespace SFVaultBridge.Core.Tests;

public class McMasterOAuthTests
{
    // Live probe of https://mcmaster.simplifyber-app.com/mcp on 2026-10-14 (unauthenticated).
    private const string LiveChallenge = "Bearer realm=\"OAuth\", error=\"invalid_token\", error_description=\"Missing or invalid access token\", resource_metadata=\"https://mcmaster.simplifyber-app.com/.well-known/cloudflare-access-protected-resource/mcp\"";
    private const string LiveResource = "{\"resource\":\"https://mcmaster.simplifyber-app.com/mcp\",\"protected\":true,\"team_domain\":\"old-flower-628d.cloudflareaccess.com\",\"authorization_servers\":[\"https://old-flower-628d.cloudflareaccess.com\"],\"authentication_methods\":[]}";
    private const string LiveAuthServer = "{\"issuer\":\"https://old-flower-628d.cloudflareaccess.com\",\"authorization_endpoint\":\"https://old-flower-628d.cloudflareaccess.com/cdn-cgi/access/oauth/authorization\",\"token_endpoint\":\"https://old-flower-628d.cloudflareaccess.com/cdn-cgi/access/oauth/token\",\"response_types_supported\":[\"code\"],\"response_modes_supported\":[\"query\"],\"grant_types_supported\":[\"authorization_code\",\"refresh_token\"],\"token_endpoint_auth_methods_supported\":[\"client_secret_basic\",\"client_secret_post\",\"none\"],\"revocation_endpoint\":\"https://old-flower-628d.cloudflareaccess.com/cdn-cgi/access/oauth/revoke\",\"registration_endpoint\":\"https://old-flower-628d.cloudflareaccess.com/cdn-cgi/access/oauth/registration\",\"code_challenge_methods_supported\":[\"S256\"]}";
    private const string LiveServer = "https://mcmaster.simplifyber-app.com/mcp";

    [Fact]
    public void Resource_metadata_url_comes_from_the_live_challenge()
    {
        Assert.Equal("https://mcmaster.simplifyber-app.com/.well-known/cloudflare-access-protected-resource/mcp", McMasterOAuth.ResourceMetadataUrl(new[] { LiveChallenge }));
        Assert.Null(McMasterOAuth.ResourceMetadataUrl(new[] { "Bearer realm=\"x\"" }));
        Assert.Null(McMasterOAuth.ResourceMetadataUrl(new[] { "Bearer resource_metadata=\"http://evil.example/x\"" }));
        Assert.Null(McMasterOAuth.ResourceMetadataUrl(null));
    }

    [Fact]
    public void Well_known_fallbacks_follow_rfc_9728_and_8414()
    {
        Assert.Equal(new[] { "https://h.test/.well-known/oauth-protected-resource/mcp", "https://h.test/.well-known/oauth-protected-resource" },
            McMasterOAuth.DefaultResourceMetadataUrls("https://h.test/mcp"));
        Assert.Equal(new[] { "https://h.test/.well-known/oauth-protected-resource" }, McMasterOAuth.DefaultResourceMetadataUrls("https://h.test/"));
        Assert.Equal(new[] { "https://a.test/.well-known/oauth-authorization-server", "https://a.test/.well-known/openid-configuration" },
            McMasterOAuth.AuthServerMetadataUrls("https://a.test"));
        Assert.Equal("https://a.test/.well-known/oauth-authorization-server/t1", McMasterOAuth.AuthServerMetadataUrls("https://a.test/t1")[0]);
    }

    [Fact]
    public void Live_resource_and_auth_server_metadata_parse()
    {
        var pr = McMasterOAuth.ParseProtectedResource(LiveResource, LiveServer);
        Assert.Equal(LiveServer, pr.Resource);
        Assert.Equal("https://old-flower-628d.cloudflareaccess.com", Assert.Single(pr.AuthorizationServers));
        var a = McMasterOAuth.ParseAuthServer(LiveAuthServer, pr.AuthorizationServers[0]);
        Assert.EndsWith("/oauth/authorization", a.AuthorizationEndpoint);
        Assert.EndsWith("/oauth/token", a.TokenEndpoint);
        Assert.EndsWith("/oauth/registration", a.RegistrationEndpoint);
        Assert.EndsWith("/oauth/revoke", a.RevocationEndpoint);
    }

    [Fact]
    public void Resource_for_another_host_is_refused()
    {
        var other = LiveResource.Replace("mcmaster.simplifyber-app.com", "evil.example");
        Assert.Throws<McMasterAuthException>(() => McMasterOAuth.ParseProtectedResource(other, LiveServer));
        Assert.Throws<McMasterAuthException>(() => McMasterOAuth.ParseProtectedResource("{\"resource\":\"" + LiveServer + "\"}", LiveServer));
        Assert.Throws<McMasterAuthException>(() => McMasterOAuth.ParseProtectedResource("not json", LiveServer));
    }

    [Fact]
    public void Auth_server_without_s256_or_with_other_issuer_is_refused()
    {
        Assert.Throws<McMasterAuthException>(() => McMasterOAuth.ParseAuthServer(LiveAuthServer.Replace("[\"S256\"]", "[\"plain\"]"), "https://old-flower-628d.cloudflareaccess.com"));
        Assert.Throws<McMasterAuthException>(() => McMasterOAuth.ParseAuthServer(LiveAuthServer, "https://someone-else.cloudflareaccess.com"));
        Assert.Throws<McMasterAuthException>(() => McMasterOAuth.ParseAuthServer(LiveAuthServer.Replace("https://old-flower-628d.cloudflareaccess.com/cdn-cgi/access/oauth/token", "http://x.test/token"), "https://old-flower-628d.cloudflareaccess.com"));
    }

    [Fact]
    public void Registration_request_is_a_public_client_with_one_loopback_redirect()
    {
        var o = System.Text.Json.Nodes.JsonNode.Parse(McMasterOAuth.RegistrationRequest("http://127.0.0.1:50123/callback"))!.AsObject();
        Assert.Equal("none", (string)o["token_endpoint_auth_method"]!);
        Assert.Equal("http://127.0.0.1:50123/callback", (string)o["redirect_uris"]![0]!);
        Assert.Equal(new[] { "authorization_code", "refresh_token" }, o["grant_types"]!.AsArray().Select(x => (string)x!).ToArray());
        Assert.Equal("SF Vault Bridge", (string)o["client_name"]!);
        Assert.Equal(new RegisteredClient("abc", null), McMasterOAuth.ParseRegistration("{\"client_id\":\"abc\"}"));
        Assert.Equal("s", McMasterOAuth.ParseRegistration("{\"client_id\":\"abc\",\"client_secret\":\"s\"}").ClientSecret);
        var ex = Assert.Throws<McMasterAuthException>(() => McMasterOAuth.ParseRegistration("{\"error\":\"invalid_redirect_uri\"}"));
        Assert.Contains("invalid_redirect_uri", ex.Message);
    }

    [Fact]
    public void Authorize_url_carries_pkce_state_and_resource()
    {
        var a = McMasterOAuth.ParseAuthServer(LiveAuthServer, "https://old-flower-628d.cloudflareaccess.com");
        var url = McMasterOAuth.AuthorizeUrl(a, "cid", "http://127.0.0.1:5000/callback", "chal", "st&x", LiveServer);
        var q = McMasterOAuth.ParseQuery(new Uri(url).Query);
        Assert.Equal("code", q["response_type"]);
        Assert.Equal("cid", q["client_id"]);
        Assert.Equal("http://127.0.0.1:5000/callback", q["redirect_uri"]);
        Assert.Equal("chal", q["code_challenge"]);
        Assert.Equal("S256", q["code_challenge_method"]);
        Assert.Equal("st&x", q["state"]);
        Assert.Equal(LiveServer, q["resource"]);
    }

    [Fact]
    public void Token_forms()
    {
        var code = McMasterOAuth.CodeForm("c", "http://127.0.0.1:1/callback", "cid", null, "v", "r");
        Assert.Equal("authorization_code", code["grant_type"]);
        Assert.Equal("v", code["code_verifier"]);
        Assert.False(code.ContainsKey("client_secret"));
        var s = new StoredSignIn("https://s.test/mcp", "r", "https://i.test", "https://i.test/t", null, "cid", "sec", "rt");
        var refresh = McMasterOAuth.RefreshForm(s);
        Assert.Equal(("refresh_token", "rt", "cid", "r", "sec"), (refresh["grant_type"], refresh["refresh_token"], refresh["client_id"], refresh["resource"], refresh["client_secret"]));
        Assert.Equal("refresh_token", McMasterOAuth.RevokeForm(s)["token_type_hint"]);
    }

    [Fact]
    public void Token_reply_parsing_never_echoes_tokens()
    {
        var ok = McMasterOAuth.ParseToken(200, "{\"access_token\":\"AT\",\"refresh_token\":\"RT\",\"expires_in\":900}")!;
        Assert.Equal(("AT", "RT", 900), (ok.AccessToken, ok.RefreshToken, ok.ExpiresInSeconds));
        Assert.Null(McMasterOAuth.ParseToken(200, "{\"access_token\":\"AT\"}")!.RefreshToken);
        Assert.Equal(900, McMasterOAuth.ParseToken(200, "{\"access_token\":\"AT\"}")!.ExpiresInSeconds);
        Assert.Null(McMasterOAuth.ParseToken(400, "{\"error\":\"invalid_grant\"}"));
        var ex = Assert.Throws<McMasterAuthException>(() => McMasterOAuth.ParseToken(500, "{\"error\":\"server_error\",\"error_description\":\"down\",\"access_token\":\"SECRET\"}"));
        Assert.Contains("server_error", ex.Message);
        Assert.DoesNotContain("SECRET", ex.Message);
        Assert.Throws<McMasterAuthException>(() => McMasterOAuth.ParseToken(502, "<html>bad gateway</html>"));
    }

    [Fact]
    public void Access_token_expiry_is_early()
    {
        var now = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(now.AddSeconds(780), McMasterOAuth.ExpiresAtUtc(now, 900));
        Assert.Equal(now.AddSeconds(30), McMasterOAuth.ExpiresAtUtc(now, 60));
    }

    [Fact]
    public void Stored_sign_in_round_trips_and_belongs_to_one_server()
    {
        var s = new StoredSignIn("https://s.test/mcp", "https://s.test/mcp", "https://i.test", "https://i.test/t", "https://i.test/r", "cid", null, "rt");
        var back = McMasterOAuth.FromJson(McMasterOAuth.ToJson(s));
        Assert.Equal(s, back);
        Assert.True(McMasterOAuth.IsFor(back, "https://S.TEST/mcp/"));
        Assert.False(McMasterOAuth.IsFor(back, "https://s.test/other"));
        Assert.False(McMasterOAuth.IsFor(back, "https://t.test/mcp"));
        Assert.Null(McMasterOAuth.FromJson("{\"server\":\"x\"}"));
        Assert.Null(McMasterOAuth.FromJson("garbage"));
        Assert.Null(McMasterOAuth.FromJson(null));
    }

    [Fact]
    public void Callback_is_judged_on_state_then_error_then_code()
    {
        Dictionary<string, string> Q(string q) => McMasterOAuth.ParseQuery(q);
        Assert.Equal(McMasterOAuth.CallbackKind.Ignore, McMasterOAuth.JudgeCallback(Q("favicon=1"), "s").Kind);
        Assert.Equal(McMasterOAuth.CallbackKind.StateMismatch, McMasterOAuth.JudgeCallback(Q("code=c&state=other"), "s").Kind);
        Assert.Equal(McMasterOAuth.CallbackKind.StateMismatch, McMasterOAuth.JudgeCallback(Q("code=c"), "s").Kind);
        Assert.Equal((McMasterOAuth.CallbackKind.Error, "access_denied: no"), McMasterOAuth.JudgeCallback(Q("error=access_denied&error_description=no&state=s"), "s"));
        Assert.Equal((McMasterOAuth.CallbackKind.Code, "c 1"), McMasterOAuth.JudgeCallback(Q("code=c+1&state=s"), "s"));
    }

    [Fact]
    public void Request_target_only_for_get()
    {
        Assert.Equal("/callback?code=1", LoopbackRedirect.RequestTarget("GET /callback?code=1 HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n"));
        Assert.Null(LoopbackRedirect.RequestTarget("POST /callback HTTP/1.1\r\n\r\n"));
        Assert.Null(LoopbackRedirect.RequestTarget("GET http://x/ HTTP/1.1\r\n\r\n"));
        Assert.Null(LoopbackRedirect.RequestTarget(""));
    }

    [Fact]
    public void Allowed_urls_are_https_or_loopback_http()
    {
        Assert.True(McMasterOAuth.IsAllowedUrl("https://a.test/x"));
        Assert.True(McMasterOAuth.IsAllowedUrl("http://127.0.0.1:5/x"));
        Assert.True(McMasterOAuth.IsAllowedUrl("http://localhost:5/x"));
        Assert.False(McMasterOAuth.IsAllowedUrl("http://a.test/x"));
        Assert.False(McMasterOAuth.IsAllowedUrl("ftp://a.test/x"));
        Assert.False(McMasterOAuth.IsAllowedUrl("/relative"));
    }
}

public class LoopbackRedirectTests
{
    [Fact]
    public async Task Listens_on_127_0_0_1_random_port_and_returns_the_code()
    {
        using var lb = new LoopbackRedirect();
        Assert.StartsWith("http://127.0.0.1:", lb.RedirectUri);
        Assert.EndsWith("/callback", lb.RedirectUri);
        var wait = lb.WaitForCodeAsync("st", TimeSpan.FromSeconds(10), CancellationToken.None);
        using var http = new HttpClient();
        var favicon = await http.GetAsync($"http://127.0.0.1:{lb.Port}/favicon.ico");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, favicon.StatusCode);
        var wrong = await http.GetAsync(lb.RedirectUri + "?code=x&state=nope");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, wrong.StatusCode);
        var ok = await http.GetAsync(lb.RedirectUri + "?code=the-code&state=st");
        Assert.Contains("Signed in", await ok.Content.ReadAsStringAsync());
        Assert.Equal("the-code", await wait);
    }

    [Fact]
    public async Task Error_from_the_sign_in_server_cancels()
    {
        using var lb = new LoopbackRedirect();
        var wait = lb.WaitForCodeAsync("st", TimeSpan.FromSeconds(10), CancellationToken.None);
        using var http = new HttpClient();
        await http.GetAsync(lb.RedirectUri + "?error=access_denied&state=st");
        var ex = await Assert.ThrowsAsync<McMasterSignInCancelled>(() => wait);
        Assert.Contains("access_denied", ex.Message);
    }

    [Fact]
    public async Task Timeout_and_cancel_end_the_wait()
    {
        using (var lb = new LoopbackRedirect())
            Assert.Contains("timed out", (await Assert.ThrowsAsync<McMasterSignInCancelled>(() => lb.WaitForCodeAsync("s", TimeSpan.FromMilliseconds(200), CancellationToken.None))).Message);
        using (var lb = new LoopbackRedirect())
        {
            using var cts = new CancellationTokenSource(200);
            Assert.Contains("cancelled", (await Assert.ThrowsAsync<McMasterSignInCancelled>(() => lb.WaitForCodeAsync("s", TimeSpan.FromMinutes(1), cts.Token))).Message);
        }
    }

    [Fact]
    public void Port_is_free_and_closed_after_dispose()
    {
        int port;
        using (var lb = new LoopbackRedirect()) port = lb.Port;
        var l = new TcpListener(System.Net.IPAddress.Loopback, port);
        l.Start();   // would throw if still held
        l.Stop();
    }
}

public class McMasterAuthTests
{
    private static (McMasterAuth Auth, FakeMcMasterServer Server, MemoryStore Store, List<string> Log) Make(Func<string, bool>? browser = null, TimeSpan? timeout = null)
    {
        var server = new FakeMcMasterServer();
        var store = new MemoryStore();
        var log = new List<string>();
        var auth = new McMasterAuth(FakeMcMasterServer.Server, new HttpClient(server), store, browser ?? server.Browser(), log.Add, timeout ?? TimeSpan.FromSeconds(10));
        return (auth, server, store, log);
    }

    private static McMasterAuth WithBrowser(FakeMcMasterServer server, MemoryStore store, List<string> log, Func<string, bool> browser, TimeSpan? timeout = null) =>
        new(FakeMcMasterServer.Server, new HttpClient(server), store, browser, log.Add, timeout ?? TimeSpan.FromSeconds(10));

    [Fact]
    public async Task Discovery_uses_the_401_resource_metadata_then_rfc_8414()
    {
        var (auth, server, _, _) = Make();
        var (resource, authServer) = await auth.DiscoverAsync(CancellationToken.None);
        Assert.Equal(FakeMcMasterServer.Server, resource.Resource);
        Assert.Equal(FakeMcMasterServer.Issuer, authServer.Issuer);
        Assert.Equal(new[]
        {
            "POST https://mcmaster.example.test/mcp",
            "GET https://mcmaster.example.test/.well-known/cloudflare-access-protected-resource/mcp",
            "GET https://team.example-access.test/.well-known/oauth-authorization-server",
        }, server.Calls);
    }

    [Fact]
    public async Task Discovery_falls_back_to_the_well_known_path_when_the_401_names_nothing()
    {
        var (auth, server, _, _) = Make();
        server.NamesMetadataIn401 = false;
        var (resource, _) = await auth.DiscoverAsync(CancellationToken.None);
        Assert.Equal(FakeMcMasterServer.Server, resource.Resource);
        Assert.Contains("GET https://mcmaster.example.test/.well-known/oauth-protected-resource/mcp", server.Calls);
    }

    [Fact]
    public async Task Sign_in_registers_uses_pkce_and_stores_only_the_refresh_side()
    {
        var opened = new List<string>();
        var server = new FakeMcMasterServer();
        var store = new MemoryStore();
        var log = new List<string>();
        var auth = WithBrowser(server, store, log, server.Browser(opened: opened));
        var statuses = new List<string>();
        auth.Status += statuses.Add;
        Assert.False(auth.IsSignedIn);

        var access = await auth.GetAccessTokenAsync(interactive: true, CancellationToken.None);

        Assert.StartsWith("access-", access);
        Assert.True(auth.IsSignedIn);
        Assert.StartsWith("http://127.0.0.1:", server.LastRedirect);
        Assert.EndsWith("/callback", server.LastRedirect);
        Assert.Equal(FakeMcMasterServer.Server, server.LastResource);
        var stored = McMasterOAuth.FromJson(store.Text)!;
        Assert.Equal(server.IssuedRefresh, stored.RefreshToken);
        Assert.Equal(server.ClientId, stored.ClientId);
        Assert.DoesNotContain(access, store.Text);
        Assert.Contains(statuses, s => s.Contains("browser"));
        Assert.All(log, l => { Assert.DoesNotContain(access, l); Assert.DoesNotContain(stored.RefreshToken, l); Assert.DoesNotContain("good-code", l); });
        Assert.Single(opened);
    }

    [Fact]
    public async Task Cached_access_token_is_reused_then_refreshed_with_rotation()
    {
        var (auth, server, store, _) = Make();
        var first = await auth.GetAccessTokenAsync(true, CancellationToken.None);
        var calls = server.TokenCalls;
        Assert.Equal(first, await auth.GetAccessTokenAsync(false, CancellationToken.None));
        Assert.Equal(calls, server.TokenCalls);

        var refreshed = await auth.GetAccessTokenAsync(false, CancellationToken.None, forceRefresh: true);
        Assert.NotEqual(first, refreshed);
        Assert.Equal(1, server.RefreshCalls);
        Assert.Equal(server.IssuedRefresh, McMasterOAuth.FromJson(store.Text)!.RefreshToken);
    }

    [Fact]
    public async Task Expired_access_token_refreshes_on_its_own()
    {
        var server = new FakeMcMasterServer { ExpiresIn = 60 };
        var store = new MemoryStore();
        var now = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
        var auth = new McMasterAuth(FakeMcMasterServer.Server, new HttpClient(server), store, server.Browser(), _ => { }, TimeSpan.FromSeconds(10), () => now);
        var first = await auth.GetAccessTokenAsync(true, CancellationToken.None);
        now = now.AddSeconds(31);
        Assert.NotEqual(first, await auth.GetAccessTokenAsync(false, CancellationToken.None));
        Assert.Equal(1, server.RefreshCalls);
    }

    [Fact]
    public async Task A_new_session_refreshes_from_the_stored_token_without_a_browser()
    {
        var (auth, server, store, _) = Make();
        await auth.GetAccessTokenAsync(true, CancellationToken.None);
        var opened = 0;
        var again = WithBrowser(server, store, new List<string>(), _ => { opened++; return true; });
        Assert.True(again.IsSignedIn);
        Assert.StartsWith("access-", await again.GetAccessTokenAsync(false, CancellationToken.None));
        Assert.Equal(0, opened);
    }

    [Fact]
    public async Task Dead_refresh_token_means_not_signed_in_unless_interactive()
    {
        var (auth, server, store, log) = Make();
        await auth.GetAccessTokenAsync(true, CancellationToken.None);
        server.RefreshIsDead = true;
        var fresh = WithBrowser(server, store, log, server.Browser());
        await Assert.ThrowsAsync<McMasterNotSignedIn>(() => fresh.GetAccessTokenAsync(false, CancellationToken.None));
        Assert.Null(store.Text);
        Assert.Contains(log, l => l.Contains("expired"));

        server.RefreshIsDead = false;
        Assert.StartsWith("access-", await fresh.GetAccessTokenAsync(true, CancellationToken.None));
        Assert.NotNull(store.Text);
    }

    [Fact]
    public async Task Sign_in_for_another_server_url_does_not_count()
    {
        var (auth, server, store, _) = Make();
        await auth.GetAccessTokenAsync(true, CancellationToken.None);
        var other = new McMasterAuth("https://other.example.test/mcp", new HttpClient(server), store, _ => false, _ => { });
        Assert.False(other.IsSignedIn);
        await Assert.ThrowsAsync<McMasterNotSignedIn>(() => other.GetAccessTokenAsync(false, CancellationToken.None));
    }

    [Fact]
    public async Task Sign_out_revokes_and_deletes()
    {
        var (auth, server, store, _) = Make();
        await auth.GetAccessTokenAsync(true, CancellationToken.None);
        await auth.SignOutAsync(CancellationToken.None);
        Assert.True(server.Revoked);
        Assert.Null(store.Text);
        Assert.False(auth.IsSignedIn);
        await Assert.ThrowsAsync<McMasterNotSignedIn>(() => auth.GetAccessTokenAsync(false, CancellationToken.None));
    }

    [Fact]
    public async Task Browser_refusal_and_wrong_state_and_timeout()
    {
        var server = new FakeMcMasterServer();
        var store = new MemoryStore();
        var refused = WithBrowser(server, store, new List<string>(), _ => false);
        await Assert.ThrowsAsync<McMasterAuthException>(() => refused.SignInAsync(CancellationToken.None));

        var wrongState = WithBrowser(server, store, new List<string>(), server.Browser(q => "code=good-code&state=forged"), TimeSpan.FromMilliseconds(800));
        await Assert.ThrowsAsync<McMasterSignInCancelled>(() => wrongState.SignInAsync(CancellationToken.None));
        Assert.Null(store.Text);

        var denied = WithBrowser(server, store, new List<string>(), server.Browser(q => "error=access_denied&state=" + Uri.EscapeDataString(q["state"])));
        await Assert.ThrowsAsync<McMasterSignInCancelled>(() => denied.SignInAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Bad_code_is_refused_and_nothing_is_stored()
    {
        var server = new FakeMcMasterServer();
        var store = new MemoryStore();
        var auth = WithBrowser(server, store, new List<string>(), server.Browser(q => "code=wrong&state=" + Uri.EscapeDataString(q["state"])));
        await Assert.ThrowsAsync<McMasterAuthException>(() => auth.SignInAsync(CancellationToken.None));
        Assert.Null(store.Text);
    }

    [Fact]
    public async Task No_s256_or_no_registration_stops_before_the_browser()
    {
        foreach (var setup in new Action<FakeMcMasterServer>[] { s => s.OfferS256 = false, s => s.OfferRegistration = false })
        {
            var server = new FakeMcMasterServer();
            setup(server);
            var opened = 0;
            var auth = WithBrowser(server, new MemoryStore(), new List<string>(), _ => { opened++; return true; });
            await Assert.ThrowsAsync<McMasterAuthException>(() => auth.SignInAsync(CancellationToken.None));
            Assert.Equal(0, opened);
        }
    }

    [Fact]
    public void Server_url_must_be_https()
    {
        Assert.Throws<ArgumentException>(() => new McMasterAuth("http://mcmaster.example.test/mcp", new HttpClient(), new MemoryStore(), _ => true, _ => { }));
    }
}
