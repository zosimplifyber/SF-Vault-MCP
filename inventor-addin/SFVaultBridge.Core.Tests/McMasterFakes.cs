using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using SFVaultBridge.Core;
using SFVaultBridge.Core.McMaster;

namespace SFVaultBridge.Core.Tests;

/// <summary>In-memory sign-in store.</summary>
internal sealed class MemoryStore : ISignInStore
{
    public string? Text;
    public int Saves;
    public string? Load() => Text;
    public void Save(string text) { Text = text; Saves++; }
    public void Delete() => Text = null;
}

/// <summary>
/// A fake of the McMaster MCP server behind Cloudflare Access managed OAuth, shaped like the live one (2026-10-14 probes): 401 with
/// resource_metadata, the cloudflare-access-protected-resource document, RFC 8414 metadata, DCR, PKCE-checked token endpoint with
/// refresh rotation, revocation, MCP Streamable HTTP (JSON or SSE replies, Mcp-Session-Id) and Access-gated /files links.
/// </summary>
internal sealed class FakeMcMasterServer : HttpMessageHandler
{
    public const string Server = "https://mcmaster.example.test/mcp";
    public const string Issuer = "https://team.example-access.test";
    public const string FileUrl = "https://mcmaster.example.test/files/abc";

    public readonly List<string> Calls = new();
    public readonly List<string> ToolCalls = new();
    public readonly List<HttpRequestMessage> Requests = new();
    public readonly Dictionary<string, Func<JsonObject, JsonObject>> Tools = new();
    public readonly HashSet<string> ToolErrors = new();
    public string? LastChallenge, LastRedirect, LastResource, ClientId;
    public bool UseSse, SendSessionId = true, OfferS256 = true, OfferRegistration = true, NamesMetadataIn401 = true, Revoked;
    public int ExpiresIn = 900, TokenCalls, RefreshCalls, Initializes, Reject401Remaining;
    public bool RefreshIsDead;
    /// <summary>When set, a refresh answers this error with RefreshErrorStatus (invalid_client when Access purged the client).</summary>
    public string? RefreshError;
    public int RefreshErrorStatus = 400;
    /// <summary>Paths that answer 302 to elsewhere (token endpoint, /mcp, /files/abc).</summary>
    public readonly HashSet<string> Redirects = new();
    private int _issued;
    private readonly HashSet<string> _validAccess = new();
    private string _refresh = "";
    private string? _session;
    public byte[] FileBytes = Encoding.ASCII.GetBytes("ISO-10303-21;");

    public string IssuedRefresh => _refresh;

    /// <summary>Makes an access token the server accepts without a sign-in (for MCP-only tests).</summary>
    public string GrantAccess() { var a = "access-" + (++_issued); _validAccess.Add(a); return a; }

    public void ExpireAllAccess() => _validAccess.Clear();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        Requests.Add(req);
        var url = req.RequestUri!.ToString();
        var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);
        Calls.Add(req.Method + " " + req.RequestUri!.GetLeftPart(UriPartial.Path));
        var path = req.RequestUri.AbsolutePath;

        if (url.StartsWith(Issuer))
        {
            if (path == "/.well-known/oauth-authorization-server") return Json(200, AuthMeta());
            if (path == "/cdn-cgi/access/oauth/registration")
            {
                var reg = JsonNode.Parse(body)!.AsObject();
                LastRedirect = reg["redirect_uris"]![0]!.GetValue<string>();
                ClientId = "client-" + Guid.NewGuid().ToString("N")[..8];
                return Json(201, new JsonObject { ["client_id"] = ClientId, ["redirect_uris"] = reg["redirect_uris"]!.DeepClone() });
            }
            if (Redirects.Contains(path)) return Redirect();
            if (path == "/cdn-cgi/access/oauth/token") return Token(Form(body));
            if (path == "/cdn-cgi/access/oauth/revoke") { Revoked = Form(body).GetValueOrDefault("token") == _refresh; return new HttpResponseMessage(HttpStatusCode.OK); }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        if (path == "/.well-known/cloudflare-access-protected-resource/mcp" || path == "/.well-known/oauth-protected-resource/mcp")
            return Json(200, new JsonObject { ["resource"] = Server, ["authorization_servers"] = new JsonArray(Issuer) });
        if (path == "/.well-known/oauth-protected-resource")
            return Json(200, new JsonObject { ["resource"] = "https://mcmaster.example.test", ["authorization_servers"] = new JsonArray(Issuer) });

        var bearer = req.Headers.Authorization?.Scheme == "Bearer" ? req.Headers.Authorization.Parameter : null;
        if (bearer is null || !_validAccess.Contains(bearer) || Reject401Remaining-- > 0)
        {
            var r = Json(401, new JsonObject { ["error"] = "invalid_token" });
            if (NamesMetadataIn401)
                r.Headers.TryAddWithoutValidation("WWW-Authenticate", "Bearer realm=\"OAuth\", error=\"invalid_token\", error_description=\"Missing or invalid access token\", resource_metadata=\"https://mcmaster.example.test/.well-known/cloudflare-access-protected-resource/mcp\"");
            return r;
        }
        if (Redirects.Contains(path)) return Redirect();
        if (path == "/files/abc") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(FileBytes) };
        if (path != "/mcp") return new HttpResponseMessage(HttpStatusCode.NotFound);

        var msg = JsonNode.Parse(body)!.AsObject();
        var method = msg["method"]!.GetValue<string>();
        if (!msg.ContainsKey("id")) return new HttpResponseMessage(HttpStatusCode.Accepted);
        var id = msg["id"]!.GetValue<long>();
        if (method == "initialize")
        {
            Initializes++;
            _session = SendSessionId ? "sess-" + Initializes : null;
            var resp = Rpc(id, new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(), ["serverInfo"] = new JsonObject { ["name"] = "sf-mcmaster" } });
            if (_session is not null) resp.Headers.Add("Mcp-Session-Id", _session);
            return resp;
        }
        if (_session is not null)
        {
            var sid = req.Headers.TryGetValues("Mcp-Session-Id", out var v) ? v.First() : null;
            if (sid != _session) return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
        if (method == "tools/call")
        {
            var name = msg["params"]!["name"]!.GetValue<string>();
            ToolCalls.Add(name);
            var args = msg["params"]!["arguments"]!.AsObject();
            if (ToolErrors.Contains(name))
                return Rpc(id, new JsonObject { ["isError"] = true, ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "This request needs 3 new McMaster subscriptions but only 0 are left today (daily budget 50)." }) });
            if (!Tools.TryGetValue(name, out var tool)) return Rpc(id, null, error: "Unknown tool");
            JsonObject output;
            try { output = tool(args); }
            catch (McMasterToolException e)
            {
                return Rpc(id, new JsonObject { ["isError"] = true, ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = e.Message }) });
            }
            return Rpc(id, new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = output.ToJsonString() }),
                ["structuredContent"] = output.DeepClone(),
                ["isError"] = false,
            });
        }
        return Rpc(id, null, error: "Method not found");
    }

    private JsonObject AuthMeta()
    {
        var o = new JsonObject
        {
            ["issuer"] = Issuer,
            ["authorization_endpoint"] = Issuer + "/cdn-cgi/access/oauth/authorization",
            ["token_endpoint"] = Issuer + "/cdn-cgi/access/oauth/token",
            ["revocation_endpoint"] = Issuer + "/cdn-cgi/access/oauth/revoke",
            ["response_types_supported"] = new JsonArray("code"),
            ["grant_types_supported"] = new JsonArray("authorization_code", "refresh_token"),
            ["token_endpoint_auth_methods_supported"] = new JsonArray("client_secret_basic", "client_secret_post", "none"),
            ["code_challenge_methods_supported"] = OfferS256 ? new JsonArray("S256") : new JsonArray("plain"),
        };
        if (OfferRegistration) o["registration_endpoint"] = Issuer + "/cdn-cgi/access/oauth/registration";
        return o;
    }

    private HttpResponseMessage Token(Dictionary<string, string> f)
    {
        TokenCalls++;
        if (f["grant_type"] == "authorization_code")
        {
            if (f["code"] != "good-code" || f["client_id"] != ClientId || f["redirect_uri"] != LastRedirect
                || Pkce.Challenge(f["code_verifier"]) != LastChallenge || f.GetValueOrDefault("resource") != Server)
                return Json(400, new JsonObject { ["error"] = "invalid_grant", ["error_description"] = "bad code" });
        }
        else
        {
            RefreshCalls++;
            if (RefreshError is not null && f["refresh_token"] == _refresh)
                return Json(RefreshErrorStatus, new JsonObject { ["error"] = RefreshError });
            if (RefreshIsDead || f["refresh_token"] != _refresh || f.GetValueOrDefault("resource") != Server)
                return Json(400, new JsonObject { ["error"] = "invalid_grant" });
        }
        var access = GrantAccess();
        _refresh = "refresh-" + _issued;
        return Json(200, new JsonObject { ["access_token"] = access, ["token_type"] = "Bearer", ["expires_in"] = ExpiresIn, ["refresh_token"] = _refresh });
    }

    private static HttpResponseMessage Redirect()
    {
        var r = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
        r.Headers.Location = new Uri("https://evil.example/steal");
        return r;
    }

    private HttpResponseMessage Rpc(long id, JsonObject? result, string? error = null)
    {
        var o = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id };
        if (error is not null) o["error"] = new JsonObject { ["code"] = -32601, ["message"] = error };
        else o["result"] = result;
        if (!UseSse) return Json(200, o);
        var sse = "event: message\r\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/message\",\"params\":{}}\r\n\r\n" +
                  "event: message\r\ndata: " + o.ToJsonString() + "\r\n\r\n";
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };
    }

    private static HttpResponseMessage Json(int status, JsonObject o) =>
        new((HttpStatusCode)status) { Content = new StringContent(o.ToJsonString(), Encoding.UTF8, "application/json") };

    private static Dictionary<string, string> Form(string body) => McMasterOAuth.ParseQuery(body);

    /// <summary>A system browser stand-in: records the PKCE challenge, then calls the loopback redirect like the real redirect would.</summary>
    public Func<string, bool> Browser(Func<Dictionary<string, string>, string>? callbackQuery = null, List<string>? opened = null) => url =>
    {
        opened?.Add(url);
        var q = McMasterOAuth.ParseQuery(new Uri(url).Query);
        LastChallenge = q["code_challenge"];
        LastResource = q["resource"];
        var redirect = q["redirect_uri"];
        var query = callbackQuery?.Invoke(q) ?? $"code=good-code&state={Uri.EscapeDataString(q["state"])}";
        _ = Task.Run(async () =>
        {
            using var http = new HttpClient();
            try { await http.GetAsync(redirect + "?" + query); } catch (HttpRequestException) { }
        });
        return true;
    };

    /// <summary>Product shaped like get_product for 91251A054 (live example).</summary>
    public static JsonObject Product(string pn = "91251A054", string? qtyPerPack = "50", string material = "Black-Oxide Alloy Steel")
    {
        var specs = new JsonObject { ["Material"] = new JsonArray(material), ["Thread Size"] = new JsonArray("0-80") };
        if (qtyPerPack is not null) specs["Quantity per Package"] = new JsonArray(qtyPerPack);
        return new JsonObject
        {
            ["part_number"] = pn, ["status"] = "Active", ["family"] = "Black-Oxide Alloy Steel Socket Head Screw",
            ["description"] = "US Origin, 0-80 Thread Size, 3/16\" Long", ["specifications"] = specs, ["dimensions"] = new JsonObject(),
            ["cad_formats"] = new JsonArray("2-D DWG", "2-D PDF", "3-D STEP", "3-D Solidworks"), ["datasheets"] = new JsonArray(),
            ["mcmaster_url"] = $"https://www.mcmaster.com/{pn}/", ["terms"] = "notice",
        };
    }

    public static JsonObject Price(string pn = "91251A054", double unit = 4.68, string uom = "Packs") => new()
    {
        ["part_number"] = pn, ["qty"] = 1, ["unit_price"] = unit, ["unit_of_measure"] = uom, ["extended_price"] = unit,
        ["price_breaks"] = new JsonArray(new JsonObject { ["min_qty"] = 1, ["amount"] = unit, ["unit"] = uom }), ["terms"] = "notice",
    };
}
