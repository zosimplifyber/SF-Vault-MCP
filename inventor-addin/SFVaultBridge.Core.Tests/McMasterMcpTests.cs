using System.Text.Json.Nodes;
using SFVaultBridge.Core;
using SFVaultBridge.Core.McMaster;

namespace SFVaultBridge.Core.Tests;

public class McpJsonTests
{
    [Fact]
    public void Requests_are_json_rpc_2()
    {
        var o = JsonNode.Parse(McpJson.Request(7, "tools/call", McpJson.ToolCallParams("get_price", new JsonObject { ["part_number"] = "X", ["qty"] = 1 })))!.AsObject();
        Assert.Equal("2.0", (string)o["jsonrpc"]!);
        Assert.Equal(7, (long)o["id"]!);
        Assert.Equal("get_price", (string)o["params"]!["name"]!);
        Assert.Equal("X", (string)o["params"]!["arguments"]!["part_number"]!);
        var n = JsonNode.Parse(McpJson.Notification("notifications/initialized"))!.AsObject();
        Assert.False(n.ContainsKey("id"));
        var init = McpJson.InitializeParams("1.2.0");
        Assert.Equal(McpJson.ProtocolVersion, (string)init["protocolVersion"]!);
        Assert.Equal("SF Vault Bridge", (string)init["clientInfo"]!["name"]!);
    }

    [Fact]
    public void Finds_the_response_in_json_batch_and_sse()
    {
        Assert.NotNull(McpJson.FindResponse("application/json", "{\"jsonrpc\":\"2.0\",\"id\":3,\"result\":{}}", 3));
        Assert.Null(McpJson.FindResponse("application/json", "{\"jsonrpc\":\"2.0\",\"id\":4,\"result\":{}}", 3));
        Assert.NotNull(McpJson.FindResponse("application/json", "[{\"jsonrpc\":\"2.0\",\"method\":\"x\"},{\"jsonrpc\":\"2.0\",\"id\":3,\"result\":{}}]", 3));
        var sse = "event: message\r\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\"}\r\n\r\n: keepalive\r\n\r\nevent: message\ndata: {\"jsonrpc\":\"2.0\",\n\ndata: {\"jsonrpc\":\"2.0\",\"id\":3,\"result\":{\"ok\":true}}\n\n";
        var hit = McpJson.FindResponse("text/event-stream; charset=utf-8", sse, 3);
        Assert.True((bool)hit!["result"]!["ok"]!);
        Assert.Null(McpJson.FindResponse("application/json", "not json", 3));
    }

    [Fact]
    public void Sse_data_lines_join_and_events_split()
    {
        Assert.Equal(new[] { "a\nb", "c" }, McpJson.SseMessages("data: a\ndata: b\n\nid: 1\ndata:c\n").ToArray());
    }

    [Fact]
    public void Tool_result_from_text_structured_and_errors()
    {
        JsonObject Resp(JsonObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = 1, ["result"] = result };
        var text = Resp(new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "{\"family\":\"F\"}" }) });
        Assert.Equal("F", (string)McpJson.ToolResult(text)["family"]!);
        var structured = Resp(new JsonObject { ["content"] = new JsonArray(), ["structuredContent"] = new JsonObject { ["result"] = new JsonObject { ["x"] = 1 } } });
        Assert.Equal(1, (int)McpJson.ToolResult(structured)["x"]!);
        var plainStructured = Resp(new JsonObject { ["structuredContent"] = new JsonObject { ["a"] = 1, ["b"] = 2 } });
        Assert.Equal(2, McpJson.ToolResult(plainStructured).Count);
        var isError = Resp(new JsonObject { ["isError"] = true, ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "budget used up" }) });
        Assert.True(Assert.Throws<McMasterToolException>(() => McpJson.ToolResult(isError)).IsBudget);
        var rpcError = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["error"] = new JsonObject { ["code"] = -32602, ["message"] = "Invalid params" } };
        Assert.Equal("Invalid params", Assert.Throws<McMasterToolException>(() => McpJson.ToolResult(rpcError)).Message);
        Assert.Throws<McMasterToolException>(() => McpJson.ToolResult(Resp(new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "plain words" }) })));
    }
}

public class McpHttpClientTests
{
    private static (McpHttpClient Client, FakeMcMasterServer Server, List<bool> Refreshes) Make(Action<FakeMcMasterServer>? setup = null)
    {
        var server = new FakeMcMasterServer();
        setup?.Invoke(server);
        server.Tools["get_product"] = a => FakeMcMasterServer.Product((string)a["part_number"]!);
        var token = server.GrantAccess();
        var refreshes = new List<bool>();
        var client = new McpHttpClient(FakeMcMasterServer.Server, new HttpClient(server), (force, _) =>
        {
            refreshes.Add(force);
            if (force) token = server.GrantAccess();
            return Task.FromResult(token);
        }, "1.2.0");
        return (client, server, refreshes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initializes_once_then_calls_tools_with_the_session(bool sse)
    {
        var (client, server, _) = Make(s => s.UseSse = sse);
        var a = await client.CallToolAsync("get_product", new JsonObject { ["part_number"] = "91251A054" }, CancellationToken.None);
        var b = await client.CallToolAsync("get_product", new JsonObject { ["part_number"] = "91251A055" }, CancellationToken.None);
        Assert.Equal("91251A054", (string)a["part_number"]!);
        Assert.Equal("91251A055", (string)b["part_number"]!);
        Assert.Equal(1, server.Initializes);
        var toolReq = server.Requests.Last();
        Assert.Equal("sess-1", toolReq.Headers.GetValues("Mcp-Session-Id").Single());
        Assert.Equal("2025-06-18", toolReq.Headers.GetValues("MCP-Protocol-Version").Single());
        Assert.Contains("text/event-stream", toolReq.Headers.Accept.ToString());
    }

    [Fact]
    public async Task Stateless_server_without_session_id_works()
    {
        var (client, server, _) = Make(s => s.SendSessionId = false);
        await client.CallToolAsync("get_product", new JsonObject { ["part_number"] = "X1" }, CancellationToken.None);
        Assert.False(server.Requests.Last().Headers.Contains("Mcp-Session-Id"));
    }

    [Fact]
    public async Task A_401_gets_one_forced_refresh()
    {
        var (client, server, refreshes) = Make();
        await client.CallToolAsync("get_product", new JsonObject { ["part_number"] = "X1" }, CancellationToken.None);
        server.ExpireAllAccess();
        await client.CallToolAsync("get_product", new JsonObject { ["part_number"] = "X2" }, CancellationToken.None);
        Assert.Contains(true, refreshes);

        server.Reject401Remaining = 2;
        await Assert.ThrowsAsync<McMasterUnauthorized>(() => client.CallToolAsync("get_product", new JsonObject { ["part_number"] = "X3" }, CancellationToken.None));
    }

    [Fact]
    public async Task Expired_session_restarts_once()
    {
        var (client, server, _) = Make();
        await client.CallToolAsync("get_product", new JsonObject { ["part_number"] = "X1" }, CancellationToken.None);
        // The server forgets the session (a restart): the next call gets 404 and the client starts a new one.
        typeof(FakeMcMasterServer).GetField("_session", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(server, "sess-gone");
        var r = await client.CallToolAsync("get_product", new JsonObject { ["part_number"] = "X2" }, CancellationToken.None);
        Assert.Equal("X2", (string)r["part_number"]!);
        Assert.Equal(2, server.Initializes);
    }

    [Fact]
    public async Task Tool_errors_surface_as_tool_exceptions()
    {
        var (client, server, _) = Make();
        server.ToolErrors.Add("get_product");
        var ex = await Assert.ThrowsAsync<McMasterToolException>(() => client.CallToolAsync("get_product", new JsonObject { ["part_number"] = "X1" }, CancellationToken.None));
        Assert.True(ex.IsBudget);
        await Assert.ThrowsAsync<McMasterToolException>(() => client.CallToolAsync("no_such_tool", new JsonObject(), CancellationToken.None));
    }
}

public class McMasterToolsTests
{
    private static (McMasterTools Tools, FakeMcMasterServer Server) Make()
    {
        var server = new FakeMcMasterServer();
        var token = server.GrantAccess();
        Task<string> TokenFor(bool force, CancellationToken _) { if (force) token = server.GrantAccess(); return Task.FromResult(token); }
        var http = new HttpClient(server);
        return (new McMasterTools(new McpHttpClient(FakeMcMasterServer.Server, http, TokenFor, "1.2.0"), http, TokenFor), server);
    }

    [Fact]
    public async Task Product_price_cad_and_bom_shapes()
    {
        var (tools, server) = Make();
        server.Tools["get_product"] = a => FakeMcMasterServer.Product((string)a["part_number"]!);
        server.Tools["get_price"] = a => FakeMcMasterServer.Price((string)a["part_number"]!);
        server.Tools["get_cad"] = a => new JsonObject { ["format"] = (string)a["cad_format"]!, ["filename"] = "91251A054.STEP", ["url"] = FakeMcMasterServer.FileUrl, ["expires_in_minutes"] = 60 };
        server.Tools["enrich_bom"] = a => new JsonObject
        {
            ["lines"] = new JsonArray(
                new JsonObject { ["line"] = 1, ["part_number"] = "91251A054", ["qty"] = 1, ["unit_price"] = 4.68, ["unit_of_measure"] = "Packs" },
                new JsonObject { ["line"] = 2, ["part_number"] = "BAD1", ["qty"] = 1, ["error"] = "not found" }),
        };

        var p = await tools.GetProductAsync("91251A054", CancellationToken.None);
        Assert.Equal("Black-Oxide Alloy Steel Socket Head Screw", p.Family);
        Assert.Equal("US Origin, 0-80 Thread Size, 3/16\" Long", p.Description);
        Assert.Equal(new[] { "Black-Oxide Alloy Steel" }, p.Specifications["Material"]);
        Assert.Equal(new[] { "50" }, p.Specifications["quantity per package"]);
        Assert.Contains("3-D STEP", p.CadFormats);

        var price = await tools.GetPriceAsync("91251A054", 1, CancellationToken.None);
        Assert.Equal((4.68, "Packs"), (price.UnitPrice, price.UnitOfMeasure));

        var cad = await tools.GetCadAsync("91251A054", "3-D STEP", CancellationToken.None);
        Assert.Equal(FakeMcMasterServer.FileUrl, cad.Url);

        var bom = await tools.EnrichBomAsync(new[] { ("91251A054", 1), ("BAD1", 0) }, CancellationToken.None);
        Assert.Equal(4.68, bom[0].UnitPrice);
        Assert.Equal("not found", bom[1].Error);
        Assert.Null(bom[1].UnitPrice);
    }

    [Fact]
    public async Task Download_sends_the_token_only_to_the_server_host()
    {
        var (tools, server) = Make();
        var dir = Path.Combine(Path.GetTempPath(), "sfvb-mc-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "x.step");
            await tools.DownloadAsync(FakeMcMasterServer.FileUrl, path, CancellationToken.None);
            Assert.Equal(server.FileBytes, File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".part"));
            Assert.Equal("Bearer", server.Requests.Last().Headers.Authorization!.Scheme);

            await Assert.ThrowsAsync<McMasterToolException>(() => tools.DownloadAsync("https://elsewhere.example.test/files/abc", path, CancellationToken.None));
            Assert.Null(server.Requests.Last().Headers.Authorization);
            await Assert.ThrowsAsync<McMasterToolException>(() => tools.DownloadAsync("http://mcmaster.example.test/files/abc", path, CancellationToken.None));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Download_retries_once_after_a_401()
    {
        var (tools, server) = Make();
        var dir = Path.Combine(Path.GetTempPath(), "sfvb-mc-" + Guid.NewGuid().ToString("N"));
        try
        {
            server.ExpireAllAccess();
            await tools.DownloadAsync(FakeMcMasterServer.FileUrl, Path.Combine(dir, "x.step"), CancellationToken.None);
            Assert.True(File.Exists(Path.Combine(dir, "x.step")));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Shapes_tolerate_missing_and_string_numbers()
    {
        Assert.Equal(4.5, McMasterShapes.Price(new JsonObject { ["unit_price"] = "$4.50", ["unit_of_measure"] = "Each" }).UnitPrice);
        Assert.Throws<McMasterToolException>(() => McMasterShapes.Price(new JsonObject()));
        Assert.Throws<McMasterToolException>(() => McMasterShapes.File(new JsonObject { ["format"] = "3-D STEP" }));
        var p = McMasterShapes.Product(new JsonObject { ["part_number"] = "X" });
        Assert.Empty(p.Specifications);
        Assert.Equal("", p.Family);
        var args = McMasterShapes.BomArgs(new[] { ("A", 0), ("B", 3) });
        Assert.Equal(new[] { 1, 3 }, args["lines"]!.AsArray().Select(l => (int)l!["qty"]!).ToArray());
    }
}
