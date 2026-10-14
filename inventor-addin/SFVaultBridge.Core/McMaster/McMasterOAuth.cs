using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SFVaultBridge.Core.McMaster;

/// <summary>Raised when an OAuth step fails. The message is safe to show and to log: it never carries a token or a code.</summary>
public sealed class McMasterAuthException : Exception
{
    public McMasterAuthException(string message) : base(message) { }
}

/// <summary>Raised when the user cancelled or abandoned the browser sign-in.</summary>
public sealed class McMasterSignInCancelled : Exception
{
    public McMasterSignInCancelled(string message) : base(message) { }
}

/// <summary>RFC 9728 protected resource metadata (the part we use).</summary>
public sealed record ProtectedResource(string Resource, IReadOnlyList<string> AuthorizationServers);

/// <summary>RFC 8414 authorization server metadata (the part we use).</summary>
public sealed record AuthServer(string Issuer, string AuthorizationEndpoint, string TokenEndpoint, string? RegistrationEndpoint,
                                string? RevocationEndpoint, IReadOnlyList<string> CodeChallengeMethods);

/// <summary>A dynamically registered public client (RFC 7591). ClientSecret is set only when the server insists on one.</summary>
public sealed record RegisteredClient(string ClientId, string? ClientSecret);

/// <summary>A token endpoint reply. RefreshToken is null when the server did not rotate it.</summary>
public sealed record TokenReply(string AccessToken, string? RefreshToken, int ExpiresInSeconds);

/// <summary>What the stored (DPAPI-encrypted) sign-in file holds. Everything needed to refresh without discovery.</summary>
public sealed record StoredSignIn(string Server, string Resource, string Issuer, string TokenEndpoint, string? RevocationEndpoint,
                                  string ClientId, string? ClientSecret, string RefreshToken);

/// <summary>
/// The pure pieces of the McMaster MCP OAuth 2.1 flow (Cloudflare Access managed OAuth): discovery from the 401
/// WWW-Authenticate header and the well-known documents, dynamic client registration, the authorize URL, the token
/// forms and replies, and the stored sign-in record. No I/O here.
/// </summary>
public static class McMasterOAuth
{
    public const string ClientName = "SF Vault Bridge";
    public const string CallbackPath = "/callback";

    private static readonly Regex ResourceMetadataParam = new("resource_metadata\\s*=\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);

    /// <summary>The resource_metadata URL from a Bearer WWW-Authenticate challenge; null when absent or not a usable URL.</summary>
    public static string? ResourceMetadataUrl(IEnumerable<string>? wwwAuthenticate)
    {
        foreach (var header in wwwAuthenticate ?? Array.Empty<string>())
        {
            var m = ResourceMetadataParam.Match(header ?? "");
            if (m.Success && IsAllowedUrl(m.Groups[1].Value)) return m.Groups[1].Value;
        }
        return null;
    }

    /// <summary>Where to look for protected resource metadata when the 401 names none (RFC 9728 section 3.1): path-specific first, then the root.</summary>
    public static IReadOnlyList<string> DefaultResourceMetadataUrls(string serverUrl)
    {
        var u = new Uri(serverUrl);
        var origin = u.GetLeftPart(UriPartial.Authority);
        var path = u.AbsolutePath.TrimEnd('/');
        var list = new List<string>();
        if (path.Length > 0) list.Add(origin + "/.well-known/oauth-protected-resource" + path);
        list.Add(origin + "/.well-known/oauth-protected-resource");
        return list;
    }

    /// <summary>Authorization server metadata URLs to try for an issuer (RFC 8414 section 3.1, then OpenID discovery).</summary>
    public static IReadOnlyList<string> AuthServerMetadataUrls(string issuer)
    {
        var u = new Uri(issuer);
        var origin = u.GetLeftPart(UriPartial.Authority);
        var path = u.AbsolutePath.TrimEnd('/');
        return path.Length == 0
            ? new[] { origin + "/.well-known/oauth-authorization-server", origin + "/.well-known/openid-configuration" }
            : new[] { origin + "/.well-known/oauth-authorization-server" + path, origin + "/.well-known/openid-configuration" + path, origin + path + "/.well-known/openid-configuration" };
    }

    /// <summary>Parses protected resource metadata and checks it belongs to the MCP server (same origin). Throws McMasterAuthException.</summary>
    public static ProtectedResource ParseProtectedResource(string json, string serverUrl)
    {
        var o = Parse(json) ?? throw new McMasterAuthException("The McMaster server's resource metadata is not valid JSON.");
        var resource = Str(o, "resource");
        if (string.IsNullOrWhiteSpace(resource) || !IsAllowedUrl(resource))
            throw new McMasterAuthException("The McMaster server's resource metadata names no resource.");
        if (!SameOrigin(resource, serverUrl))
            throw new McMasterAuthException($"The resource metadata is for {new Uri(resource).Host}, not {new Uri(serverUrl).Host}. Check the McMaster server URL in Settings.");
        var servers = (o["authorization_servers"] as JsonArray)?.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : null)
            .Where(s => s is not null && IsAllowedUrl(s)).Select(s => s!).ToList() ?? new List<string>();
        if (servers.Count == 0) throw new McMasterAuthException("The McMaster server's resource metadata names no authorization server.");
        return new ProtectedResource(resource, servers);
    }

    /// <summary>Parses authorization server metadata. The issuer must match, the endpoints must be https, and PKCE S256 must be offered.</summary>
    public static AuthServer ParseAuthServer(string json, string expectedIssuer)
    {
        var o = Parse(json) ?? throw new McMasterAuthException("The sign-in server's metadata is not valid JSON.");
        var issuer = Str(o, "issuer") ?? "";
        if (!string.Equals(issuer.TrimEnd('/'), expectedIssuer.TrimEnd('/'), StringComparison.Ordinal))
            throw new McMasterAuthException("The sign-in server's metadata names a different issuer.");
        var authorize = Str(o, "authorization_endpoint");
        var token = Str(o, "token_endpoint");
        if (!IsAllowedUrl(authorize) || !IsAllowedUrl(token))
            throw new McMasterAuthException("The sign-in server's metadata has no usable authorization or token endpoint.");
        var methods = (o["code_challenge_methods_supported"] as JsonArray)?.Select(x => x?.ToString() ?? "").ToList() ?? new List<string>();
        if (!methods.Contains("S256", StringComparer.Ordinal))
            throw new McMasterAuthException("The sign-in server does not offer PKCE S256, so SF Vault Bridge will not sign in to it.");
        var register = Str(o, "registration_endpoint");
        var revoke = Str(o, "revocation_endpoint");
        return new AuthServer(issuer, authorize!, token!, IsAllowedUrl(register) ? register : null, IsAllowedUrl(revoke) ? revoke : null, methods);
    }

    /// <summary>RFC 7591 registration body for a public client with one loopback redirect.</summary>
    public static string RegistrationRequest(string redirectUri) =>
        new JsonObject
        {
            ["client_name"] = ClientName,
            ["redirect_uris"] = new JsonArray(redirectUri),
            ["grant_types"] = new JsonArray("authorization_code", "refresh_token"),
            ["response_types"] = new JsonArray("code"),
            ["token_endpoint_auth_method"] = "none",
        }.ToJsonString();

    public static RegisteredClient ParseRegistration(string json)
    {
        var o = Parse(json) ?? throw new McMasterAuthException("The sign-in server's client registration reply is not valid JSON.");
        var id = Str(o, "client_id");
        if (string.IsNullOrWhiteSpace(id))
            throw new McMasterAuthException("The sign-in server did not register SF Vault Bridge" + ErrorSuffix(o) + ".");
        var secret = Str(o, "client_secret");
        return new RegisteredClient(id, string.IsNullOrEmpty(secret) ? null : secret);
    }

    /// <summary>http://127.0.0.1:port/callback</summary>
    public static string LoopbackRedirect(int port) => $"http://127.0.0.1:{port}{CallbackPath}";

    public static string AuthorizeUrl(AuthServer server, string clientId, string redirectUri, string codeChallenge, string state, string resource)
    {
        var sep = server.AuthorizationEndpoint.Contains('?') ? "&" : "?";
        return server.AuthorizationEndpoint + sep + Query(new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["resource"] = resource,
        });
    }

    public static Dictionary<string, string> CodeForm(string code, string redirectUri, string clientId, string? clientSecret, string verifier, string resource)
    {
        var f = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = clientId,
            ["code_verifier"] = verifier,
            ["resource"] = resource,
        };
        if (!string.IsNullOrEmpty(clientSecret)) f["client_secret"] = clientSecret;
        return f;
    }

    public static Dictionary<string, string> RefreshForm(StoredSignIn s)
    {
        var f = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = s.RefreshToken,
            ["client_id"] = s.ClientId,
            ["resource"] = s.Resource,
        };
        if (!string.IsNullOrEmpty(s.ClientSecret)) f["client_secret"] = s.ClientSecret;
        return f;
    }

    public static Dictionary<string, string> RevokeForm(StoredSignIn s)
    {
        var f = new Dictionary<string, string> { ["token"] = s.RefreshToken, ["token_type_hint"] = "refresh_token", ["client_id"] = s.ClientId };
        if (!string.IsNullOrEmpty(s.ClientSecret)) f["client_secret"] = s.ClientSecret;
        return f;
    }

    /// <summary>
    /// A token endpoint reply. Returns null for invalid_grant (the refresh token is dead: sign in again). Throws McMasterAuthException for
    /// anything else that is not a success; the message holds only the server's error code and description, never a token.
    /// </summary>
    public static TokenReply? ParseToken(int status, string body)
    {
        var o = Parse(body);
        var access = o is null ? null : Str(o, "access_token");
        if (status is >= 200 and < 300 && !string.IsNullOrEmpty(access))
        {
            var expires = o!["expires_in"] is JsonValue ev && ev.TryGetValue<int>(out var secs) ? secs
                : o["expires_in"] is JsonValue ev2 && ev2.TryGetValue<double>(out var d) ? (int)d : 900;
            var refresh = Str(o, "refresh_token");
            return new TokenReply(access, string.IsNullOrEmpty(refresh) ? null : refresh, expires);
        }
        if (o is not null && Str(o, "error") == "invalid_grant") return null;
        throw new McMasterAuthException("The McMaster sign-in server refused the token request (" + status + (o is null ? "" : ErrorSuffix(o)) + ").");
    }

    /// <summary>When to treat an access token as expired: two minutes early, never sooner than 30 seconds from now.</summary>
    public static DateTime ExpiresAtUtc(DateTime nowUtc, int expiresInSeconds) =>
        nowUtc.AddSeconds(Math.Max(30, expiresInSeconds - 120));

    public static string ToJson(StoredSignIn s)
    {
        var o = new JsonObject
        {
            ["v"] = 1, ["server"] = s.Server, ["resource"] = s.Resource, ["issuer"] = s.Issuer, ["token_endpoint"] = s.TokenEndpoint,
            ["client_id"] = s.ClientId, ["refresh_token"] = s.RefreshToken,
        };
        if (s.RevocationEndpoint is not null) o["revocation_endpoint"] = s.RevocationEndpoint;
        if (s.ClientSecret is not null) o["client_secret"] = s.ClientSecret;
        return o.ToJsonString();
    }

    /// <summary>The stored sign-in; null when the text is not a complete record.</summary>
    public static StoredSignIn? FromJson(string? json)
    {
        var o = Parse(json ?? "");
        if (o is null) return null;
        string? S(string k) => Str(o, k);
        var server = S("server"); var resource = S("resource"); var issuer = S("issuer"); var token = S("token_endpoint");
        var client = S("client_id"); var refresh = S("refresh_token");
        if (new[] { server, resource, issuer, token, client, refresh }.Any(string.IsNullOrEmpty)) return null;
        return new StoredSignIn(server!, resource!, issuer!, token!, S("revocation_endpoint"), client!, S("client_secret"), refresh!);
    }

    /// <summary>True when the stored sign-in belongs to this server URL (scheme, host, port and path; case and a trailing slash ignored).</summary>
    public static bool IsFor(StoredSignIn? s, string serverUrl) => s is not null && SameServer(s.Server, serverUrl);

    public static bool SameServer(string a, string b)
    {
        if (!Uri.TryCreate(a, UriKind.Absolute, out var ua) || !Uri.TryCreate(b, UriKind.Absolute, out var ub)) return false;
        return SameOrigin(a, b) && string.Equals(ua.AbsolutePath.TrimEnd('/'), ub.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    public static bool SameOrigin(string a, string b) =>
        Uri.TryCreate(a, UriKind.Absolute, out var ua) && Uri.TryCreate(b, UriKind.Absolute, out var ub)
        && string.Equals(ua.Scheme, ub.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(ua.Host, ub.Host, StringComparison.OrdinalIgnoreCase) && ua.Port == ub.Port;

    /// <summary>https anywhere, or http only to a loopback host (local test servers).</summary>
    public static bool IsAllowedUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
        && (u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && u.IsLoopback));

    /// <summary>The outcome of one request to the loopback redirect.</summary>
    public enum CallbackKind { Ignore, StateMismatch, Error, Code }

    /// <summary>Judges the query of a request to the callback path. Value is the code for Code and the error text for Error.</summary>
    public static (CallbackKind Kind, string Value) JudgeCallback(IReadOnlyDictionary<string, string> query, string expectedState)
    {
        query.TryGetValue("code", out var code);
        query.TryGetValue("error", out var error);
        if (string.IsNullOrEmpty(code) && string.IsNullOrEmpty(error)) return (CallbackKind.Ignore, "");
        if (!query.TryGetValue("state", out var state) || !string.Equals(state, expectedState, StringComparison.Ordinal))
            return (CallbackKind.StateMismatch, "");
        if (!string.IsNullOrEmpty(error))
        {
            query.TryGetValue("error_description", out var desc);
            return (CallbackKind.Error, string.IsNullOrEmpty(desc) ? error : error + ": " + desc);
        }
        return (CallbackKind.Code, code!);
    }

    /// <summary>Splits "a=1&amp;b=x%20y" into a dictionary (first value wins).</summary>
    public static Dictionary<string, string> ParseQuery(string? query)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in (query ?? "").TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = part.IndexOf('=');
            var k = Uri.UnescapeDataString((i < 0 ? part : part[..i]).Replace('+', ' '));
            var v = i < 0 ? "" : Uri.UnescapeDataString(part[(i + 1)..].Replace('+', ' '));
            d.TryAdd(k, v);
        }
        return d;
    }

    private static string ErrorSuffix(JsonObject o)
    {
        var e = Str(o, "error");
        var d = Str(o, "error_description");
        if (e is null && d is null) return "";
        return ": " + string.Join(" ", new[] { e, d }.Where(x => !string.IsNullOrEmpty(x)));
    }

    private static string Query(Dictionary<string, string> p) =>
        string.Join("&", p.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));

    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static JsonObject? Parse(string json)
    {
        try { return JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return null; }
    }
}
