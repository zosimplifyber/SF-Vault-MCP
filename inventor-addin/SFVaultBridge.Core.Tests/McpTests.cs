using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using SFVaultBridge.Core;

namespace SFVaultBridge.Core.Tests;

public class McpTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";

    // ---- fakes ----

    private sealed class FakeSession : IVaultSession
    {
        public bool SignedIn = true;
        public StatusInfo Status() => SignedIn ? new StatusInfo(true, "Zak Olech", "Vault", "srv") : new StatusInfo(false, null, null, null);
        public IReadOnlyList<SchemeInfo> GetNumberingSchemes() => throw new NotSupportedException();
        public string GenerateFileNumber(long schemeId) => throw new NotSupportedException();
        public FolderInfo? GetFolderByPath(string path) => throw new NotSupportedException();
        public FolderInfo AddFolder(string parentPath, string name) => throw new NotSupportedException();
        public IReadOnlyList<CategoryInfo> GetFileCategories() => throw new NotSupportedException();
        public VaultFile AddFile(string folderPath, string localPath, string fileName, long[] attachmentFileIds, string comment) => throw new NotSupportedException();
        public IReadOnlyList<VaultFile> UpdateFileCategories(long[] masterIds, long categoryId, string comment) => throw new NotSupportedException();
    }

    private sealed class FakeImporter : IStepImporter
    {
        public string InventorVersion => "2026.1";
        public string ImportStepAsPart(string stepPath, string outIpt, IReadOnlyDictionary<string, string> design, IReadOnlyDictionary<string, string> custom) => outIpt;
    }

    private static VaultFileSummary Sum(string name = "CD-001909.ipt", long id = 11, int ver = 3, int latest = 3) =>
        new(id, id + 100, name, "$/DESIGNS/Simplifyber Library/McMaster-Carr/nuts", "Work in Progress", "A", "Part - Purchased", ver, latest);

    private sealed class FakeVault : IVaultReader
    {
        public string? LastQuery; public int LastLimit; public VaultFileRef? LastRef; public int LastLevels;
        public bool TooMany;
        public Exception? Throw;
        public VaultSearchResult SearchFiles(string query, int limit)
        {
            if (Throw is not null) throw Throw;
            LastQuery = query; LastLimit = limit;
            var files = Enumerable.Range(1, TooMany ? limit + 1 : 2).Select(i => Sum($"CD-00190{i}.ipt", i)).ToList();
            return new VaultSearchResult(files, TooMany);
        }
        public VaultFileDetail GetFile(VaultFileRef file)
        {
            LastRef = file;
            return new VaultFileDetail(Sum(), true, 2048, "2026-10-01 10:00", "2026-09-01 09:00", "Zak O.", "import",
                new Dictionary<string, string> { ["Vendor"] = "McMASTER-CARR", ["Source"] = "Buy" }, "Compliant", false);
        }
        public VaultRelations WhereUsed(VaultFileRef file, int levels, int maxRows)
        {
            LastRef = file; LastLevels = levels;
            return new VaultRelations(Sum(), new[] { new VaultRelation(Sum("A-100.iam", 20), "Dependency", 1, "CD-001909.ipt", 0) }, false);
        }
        public VaultRelations Uses(VaultFileRef file, int levels, int maxRows)
        {
            LastRef = file; LastLevels = levels;
            return new VaultRelations(Sum("A-100.iam", 20), new[]
            {
                new VaultRelation(Sum("CD-001909.ipt", 11, ver: 2, latest: 3), "Dependency", 1, "A-100.iam", 2),
                new VaultRelation(Sum("CD-001910.ipt", 12), "Dependency", 1, "A-100.iam", 3),
            }, false);
        }
        public VaultVersions Versions(VaultFileRef file, int limit)
        {
            LastRef = file;
            return new VaultVersions(Sum(), new[]
            {
                new VaultVersionInfo(9, 1, "", "Work in Progress", "first", "2026-09-01 09:00", "Zak O."),
                new VaultVersionInfo(11, 3, "A", "Released", "third", "2026-10-01 10:00", "Zak O."),
            }, false);
        }
    }

    private sealed class FakeInventor : IInventorReader
    {
        public OpenBomResult OpenBom() => new("Asm1", "Asm1.iam", "Model state: Primary | Design view: Master | BOM view: Structured", new[]
        {
            new BomNode("CD-001909", "Hex Nut", 4, "Each", "Purchased", "A", "Steel", "CD-001909.ipt", @"C:\x\CD-001909.ipt", Array.Empty<BomNode>(),
                "McMASTER-CARR", "91290A115", "Work in Progress", Cost: 0.12, Source: "Buy"),
            new BomNode("Sub", "Sub assembly", 1, "Each", "Normal", "", "", "Sub.iam", @"C:\x\Sub.iam", new[]
            {
                new BomNode("P2", "Plate", 2, "Each", "Normal", "", "Aluminum", "P2.ipt", @"C:\x\P2.ipt", Array.Empty<BomNode>(), Process: "Laser"),
            }),
        });
        public string? ActiveDocument() => "Asm1.iam (assembly)";
    }

    private sealed class FakeCard : IDataCardHost
    {
        public int Applies;
        public IReadOnlyList<DataCardFileInfo> Find(DataCardFileRef file) =>
            new[] { new DataCardFileInfo("$/DESIGNS/x/CD-001909.ipt", "CD-001909.ipt", "Part - Purchased", "Work in Progress", false, false, "", 3, Revision: "A") };
        public IReadOnlyDictionary<PropRef, string?> Read(DataCardFileInfo file, IReadOnlyList<PropRef> props) =>
            props.ToDictionary(p => p, p => (string?)(p.Name == "Part Number" ? "CD-001909" : "old"));
        public DataCardApplied Apply(DataCardFileInfo file, IReadOnlyList<PropChange> changes, string comment) { Applies++; return new DataCardApplied(4); }
        public RevisePlan PlanRevise(DataCardFileInfo file) => new(null, "a", "b");
        public DataCardStateChange ChangeState(DataCardFileInfo file, string toStateName, string comment) => new(toStateName, "A");
        public bool? LinkedToItem(DataCardFileInfo file) => false;
        public string? ReadPropertyCompliance(DataCardFileInfo file) => null;
    }

    private sealed class FakeIntake : IIntakeHost
    {
        public (string, string)? Dup;
        public IReadOnlyList<string> Mats = new[] { "Steel", "Steel, Stainless", "Aluminum 6061" };
        public int Runs; public ImportPlan? LastPlan;
        public bool Fail;
        public string Designer() => "Zak O.";
        public IReadOnlyList<string> Materials() => Mats;
        public IReadOnlyList<string> Vendors() => new[] { "McMaster-Carr" };
        public IReadOnlyList<string> Subfolders(string vendor) => new[] { "screws", "nuts" };
        public (string Name, string Folder)? FindDuplicate(string vendor, string vendorNumber) => Dup;
        public string NextPartNumber() => "CD-001950";
        public IntakeOutcome Run(ImportPlan plan, string stepPath, string pdfPath)
        {
            Runs++; LastPlan = plan;
            return Fail
                ? new IntakeOutcome(false, "CD-001950", null, "pdf", "boom", new[] { "CD-001950.ipt" }, Array.Empty<string>(), null)
                : new IntakeOutcome(true, "CD-001950", "$/DESIGNS/Simplifyber Library/McMaster-Carr/nuts/CD-001950.ipt", null, null, Array.Empty<string>(), new[] { "w1" }, "Work in Progress");
        }
    }

    private sealed class Rig : IDisposable
    {
        public readonly FakeVault Vault = new(); public readonly FakeSession Session = new(); public readonly FakeCard Card = new(); public readonly FakeIntake Intake = new();
        public readonly string Downloads = Path.Combine(Path.GetTempPath(), "mcp-dl-" + Guid.NewGuid().ToString("N"));
        public McpServer Server;
        public IReadOnlyList<McpTool> Tools;
        public List<string> Logs = new();
        public Rig(TimeSpan? timeout = null, Func<Func<JsonNode?>, JsonNode?>? onUi = null)
        {
            Directory.CreateDirectory(Downloads);
            Tools = McpCatalog.Build(Vault, new FakeInventor(), Session, new FakeImporter(), "1.2.3", Card, Intake, new PathGuard(new[] { Downloads }), Downloads);
            Server = new McpServer(Token, "1.2.3", Tools, onUi ?? (w => w()), timeout, Logs.Add);
        }
        public void Dispose() { try { Directory.Delete(Downloads, true); } catch { } }

        public McpHttpResponse Post(JsonObject msg, string? token = Token, string? origin = null, string? bearer = null) =>
            Server.Handle(new McpHttpRequest("POST", token, bearer, origin, null, msg.ToJsonString()));

        public JsonObject Rpc(string method, JsonObject? p = null, int id = 1)
        {
            var m = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
            if (p is not null) m["params"] = p;
            var r = Post(m);
            Assert.Equal(200, r.Status);
            return (JsonObject)JsonNode.Parse(r.Body!)!;
        }

        public (JsonObject Result, bool IsError, string Text) Call(string tool, JsonObject? args = null)
        {
            var r = Rpc("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = args ?? new JsonObject() });
            var res = (JsonObject)r["result"]!;
            return (res, res["isError"]!.GetValue<bool>(), res["content"]![0]!["text"]!.GetValue<string>());
        }

        public JsonObject CallJson(string tool, JsonObject? args = null)
        {
            var (_, err, text) = Call(tool, args);
            Assert.False(err, text);
            return (JsonObject)JsonNode.Parse(text)!;
        }
    }

    // ---- protocol ----

    [Fact]
    public void Initialize_returns_version_server_info_tools_capability_and_session_id()
    {
        using var rig = new Rig();
        var resp = rig.Post(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 7, ["method"] = "initialize", ["params"] = new JsonObject { ["protocolVersion"] = "2025-06-18" } });
        Assert.Equal(200, resp.Status);
        Assert.False(string.IsNullOrEmpty(resp.SessionId));
        var r = JsonNode.Parse(resp.Body!)!;
        Assert.Equal(7, r["id"]!.GetValue<int>());
        Assert.Equal("2025-06-18", r["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.Equal("sf-vault-bridge", r["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        Assert.Equal("1.2.3", r["result"]!["serverInfo"]!["version"]!.GetValue<string>());
        Assert.NotNull(r["result"]!["capabilities"]!["tools"]);
    }

    [Fact]
    public void Initialize_with_unknown_client_version_answers_our_latest()
    {
        using var rig = new Rig();
        var r = rig.Rpc("initialize", new JsonObject { ["protocolVersion"] = "1999-01-01" });
        Assert.Equal("2025-06-18", r["result"]!["protocolVersion"]!.GetValue<string>());
        var older = rig.Rpc("initialize", new JsonObject { ["protocolVersion"] = "2025-03-26" });
        Assert.Equal("2025-03-26", older["result"]!["protocolVersion"]!.GetValue<string>());
    }

    [Fact]
    public void Initialized_notification_is_accepted_with_202_and_no_body()
    {
        using var rig = new Rig();
        var r = rig.Post(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });
        Assert.Equal(202, r.Status);
        Assert.Null(r.Body);
    }

    [Fact]
    public void Client_response_message_is_accepted_with_202()
    {
        using var rig = new Rig();
        Assert.Equal(202, rig.Post(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["result"] = new JsonObject() }).Status);
    }

    [Fact]
    public void Ping_returns_empty_result()
    {
        using var rig = new Rig();
        Assert.Empty((JsonObject)rig.Rpc("ping")["result"]!);
    }

    [Fact]
    public void Unknown_method_is_a_jsonrpc_error_with_the_request_id()
    {
        using var rig = new Rig();
        var r = rig.Rpc("resources/list", id: 5);
        Assert.Equal(-32601, r["error"]!["code"]!.GetValue<int>());
        Assert.Equal(5, r["id"]!.GetValue<int>());
    }

    [Fact]
    public void String_ids_are_echoed()
    {
        using var rig = new Rig();
        var r = rig.Post(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = "abc", ["method"] = "ping" });
        Assert.Equal("abc", JsonNode.Parse(r.Body!)!["id"]!.GetValue<string>());
    }

    [Fact]
    public void Invalid_json_is_400_parse_error()
    {
        using var rig = new Rig();
        var r = rig.Server.Handle(new McpHttpRequest("POST", Token, null, null, null, "{nope"));
        Assert.Equal(400, r.Status);
        Assert.Equal(-32700, JsonNode.Parse(r.Body!)!["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public void Batch_and_non_object_bodies_are_rejected()
    {
        using var rig = new Rig();
        Assert.Equal(400, rig.Server.Handle(new McpHttpRequest("POST", Token, null, null, null, "[{}]")).Status);
        Assert.Equal(400, rig.Server.Handle(new McpHttpRequest("POST", Token, null, null, null, "42")).Status);
        Assert.Equal(400, rig.Server.Handle(new McpHttpRequest("POST", Token, null, null, null, "{\"id\":1}")).Status);
    }

    [Fact]
    public void Session_header_is_not_required_and_a_stale_one_is_accepted()
    {
        using var rig = new Rig();
        var r = rig.Server.Handle(new McpHttpRequest("POST", Token, null, null, "stale-id", "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}"));
        Assert.Equal(200, r.Status);
    }

    [Fact]
    public void Get_is_405_and_delete_is_200()
    {
        using var rig = new Rig();
        var g = rig.Server.Handle(new McpHttpRequest("GET", Token, null, null, null, ""));
        Assert.Equal(405, g.Status);
        Assert.Contains("POST", g.Allow);
        Assert.Equal(200, rig.Server.Handle(new McpHttpRequest("DELETE", Token, null, null, null, "")).Status);
    }

    // ---- auth and origin ----

    [Fact]
    public void Missing_or_wrong_token_is_401()
    {
        using var rig = new Rig();
        var ping = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "ping" };
        Assert.Equal(401, rig.Post(ping, token: null).Status);
        Assert.Equal(401, rig.Post(ping, token: "wrong").Status);
        Assert.Equal(401, rig.Post(ping, token: null, bearer: "Bearer wrong").Status);
    }

    [Fact]
    public void Bearer_token_is_accepted()
    {
        using var rig = new Rig();
        var ping = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "ping" };
        Assert.Equal(200, rig.Post(ping, token: null, bearer: "Bearer " + Token).Status);
        Assert.Equal(200, rig.Post(ping, token: null, bearer: "bearer " + Token).Status);
    }

    [Theory]
    [InlineData("https://evil.example", 403)]
    [InlineData("http://localhost.evil.example", 403)]
    [InlineData("null", 403)]
    [InlineData("http://localhost:3000", 200)]
    [InlineData("http://127.0.0.1:8790", 200)]
    [InlineData("http://[::1]:8790", 200)]
    public void Origin_must_be_localhost_when_present(string origin, int expected)
    {
        using var rig = new Rig();
        var ping = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "ping" };
        Assert.Equal(expected, rig.Post(ping, origin: origin).Status);
    }

    [Fact]
    public void Server_requires_a_token()
    {
        Assert.Throws<ArgumentException>(() => new McpServer(" ", "1", Array.Empty<McpTool>(), w => w()));
    }

    // ---- tool list ----

    [Fact]
    public void Tools_list_has_the_expected_tools_with_object_schemas_and_hints()
    {
        using var rig = new Rig();
        var tools = (JsonArray)rig.Rpc("tools/list")["result"]!["tools"]!;
        var names = tools.Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(new[]
        {
            "bridge_status", "vault_search_files", "vault_get_file", "vault_where_used", "vault_uses", "vault_file_versions", "inventor_open_bom",
            "fix_data_card_preview", "fix_data_card_apply", "mcmaster_intake_preview", "mcmaster_intake_commit",
        }, names);
        foreach (var t in tools)
        {
            Assert.Equal("object", t!["inputSchema"]!["type"]!.GetValue<string>());
            Assert.False(string.IsNullOrWhiteSpace(t["description"]!.GetValue<string>()));
            Assert.DoesNotContain("\u2014", t["description"]!.GetValue<string>());
        }
        var byName = tools.ToDictionary(t => t!["name"]!.GetValue<string>(), t => t!);
        Assert.True(byName["vault_search_files"]["annotations"]!["readOnlyHint"]!.GetValue<bool>());
        Assert.False(byName["fix_data_card_apply"]["annotations"]!["readOnlyHint"]!.GetValue<bool>());
        Assert.False(byName["mcmaster_intake_commit"]["annotations"]!["readOnlyHint"]!.GetValue<bool>());
        Assert.Equal("query", byName["vault_search_files"]["inputSchema"]!["required"]![0]!.GetValue<string>());
    }

    [Fact]
    public void Building_the_catalog_twice_does_not_share_schema_nodes()
    {
        using var a = new Rig();
        using var b = new Rig();
        Assert.Equal(a.Tools.Count, b.Tools.Count);
    }

    // ---- tools/call framing ----

    [Fact]
    public void Unknown_tool_and_bad_arguments_shape_are_invalid_params()
    {
        using var rig = new Rig();
        var r = rig.Rpc("tools/call", new JsonObject { ["name"] = "nope" });
        Assert.Equal(-32602, r["error"]!["code"]!.GetValue<int>());
        var r2 = rig.Rpc("tools/call", new JsonObject { ["name"] = "bridge_status", ["arguments"] = "x" });
        Assert.Equal(-32602, r2["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public void Tool_result_is_compact_json_text_with_isError_false()
    {
        using var rig = new Rig();
        var (res, err, text) = rig.Call("bridge_status");
        Assert.False(err);
        Assert.Equal("text", res["content"]![0]!["type"]!.GetValue<string>());
        Assert.DoesNotContain("\n", text);
        var j = JsonNode.Parse(text)!;
        Assert.True(j["signed_in"]!.GetValue<bool>());
        Assert.Equal("1.2.3", j["bridge_version"]!.GetValue<string>());
        Assert.Equal("Asm1.iam (assembly)", j["active_document"]!.GetValue<string>());
        Assert.Equal(11, j["tool_count"]!.GetValue<int>());
    }

    [Fact]
    public void Bridge_status_hints_when_not_signed_in()
    {
        using var rig = new Rig();
        rig.Session.SignedIn = false;
        var j = rig.CallJson("bridge_status");
        Assert.False(j["signed_in"]!.GetValue<bool>());
        Assert.Contains("Sign in", j["hint"]!.GetValue<string>());
    }

    [Fact]
    public void BridgeException_becomes_isError_with_code_and_message()
    {
        using var rig = new Rig();
        rig.Vault.Throw = new BridgeException("not_signed_in", "Sign in to Vault in Inventor (Vault tab, Log In).");
        var (_, err, text) = rig.Call("vault_search_files", new JsonObject { ["query"] = "nut" });
        Assert.True(err);
        var j = JsonNode.Parse(text)!;
        Assert.Equal("not_signed_in", j["error"]!.GetValue<string>());
        Assert.Contains("Sign in", j["message"]!.GetValue<string>());
    }

    [Fact]
    public void Unexpected_exception_becomes_isError_internal()
    {
        using var rig = new Rig();
        rig.Vault.Throw = new InvalidOperationException("boom");
        var (_, err, text) = rig.Call("vault_search_files", new JsonObject { ["query"] = "nut" });
        Assert.True(err);
        Assert.Equal("internal", JsonNode.Parse(text)!["error"]!.GetValue<string>());
        Assert.Contains("boom", text);
    }

    [Fact]
    public void Slow_tool_times_out_with_an_error()
    {
        using var rig = new Rig(TimeSpan.FromMilliseconds(50), w => { Thread.Sleep(600); return w(); });
        var (_, err, text) = rig.Call("bridge_status");
        Assert.True(err);
        Assert.Equal("timeout", JsonNode.Parse(text)!["error"]!.GetValue<string>());
    }

    [Fact]
    public void Tools_run_through_the_ui_marshal()
    {
        var calls = 0;
        using var rig = new Rig(onUi: w => { calls++; return w(); });
        rig.Call("bridge_status");
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Oversized_result_is_truncated_with_a_note()
    {
        var big = new McpTool("big", "Big", "d", Schema.Obj(new JsonObject()), McpToolKind.Read, _ => new JsonObject { ["x"] = new string('a', McpServer.MaxResultChars + 100) });
        var server = new McpServer(Token, "1", new[] { big }, w => w());
        var r = server.Handle(new McpHttpRequest("POST", Token, null, null, null,
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"big\"}}"));
        var text = JsonNode.Parse(r.Body!)!["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.StartsWith("[TRUNCATED", text);
    }

    [Fact]
    public void Tool_calls_are_logged_and_reads_do_not_log_arguments()
    {
        using var rig = new Rig();
        rig.Call("vault_search_files", new JsonObject { ["query"] = "secretword" });
        Assert.Contains(rig.Logs, l => l.Contains("vault_search_files") && !l.Contains("secretword"));
    }

    // ---- read tools ----

    [Fact]
    public void Search_passes_query_clamps_limit_and_shapes_files()
    {
        using var rig = new Rig();
        var j = rig.CallJson("vault_search_files", new JsonObject { ["query"] = " CD-0019 ", ["limit"] = 9999 });
        Assert.Equal("CD-0019", rig.Vault.LastQuery);
        Assert.Equal(ReadTools.SearchMax, rig.Vault.LastLimit);
        Assert.Equal(2, j["count"]!.GetValue<int>());
        var f = j["files"]![0]!;
        Assert.Equal("CD-001901.ipt", f["name"]!.GetValue<string>());
        Assert.Equal("Work in Progress", f["state"]!.GetValue<string>());
        Assert.Equal("A", f["revision"]!.GetValue<string>());
        Assert.Equal("Part - Purchased", f["category"]!.GetValue<string>());
        Assert.Equal(3, f["version"]!.GetValue<int>());
        Assert.Equal(1, f["file_id"]!.GetValue<int>());
        Assert.EndsWith("/nuts/CD-001901.ipt", f["path"]!.GetValue<string>());
        Assert.Null(j["truncated"]);
    }

    [Fact]
    public void Search_default_limit_and_truncation_flag()
    {
        using var rig = new Rig();
        rig.Vault.TooMany = true;
        var j = rig.CallJson("vault_search_files", new JsonObject { ["query"] = "nut" });
        Assert.Equal(ReadTools.SearchDefault, rig.Vault.LastLimit);
        Assert.Equal(ReadTools.SearchDefault, j["count"]!.GetValue<int>());
        Assert.True(j["truncated"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"query\":\"a\"}")]
    [InlineData("{\"query\":\"nut\",\"limit\":\"ten\"}")]
    public void Search_rejects_bad_input(string args)
    {
        using var rig = new Rig();
        var (_, err, text) = rig.Call("vault_search_files", (JsonObject)JsonNode.Parse(args)!);
        Assert.True(err);
        Assert.Equal("bad_request", JsonNode.Parse(text)!["error"]!.GetValue<string>());
    }

    [Fact]
    public void Get_file_shapes_detail_with_properties_sorted_and_flags()
    {
        using var rig = new Rig();
        var j = rig.CallJson("vault_get_file", new JsonObject { ["file"] = "CD-001909" });
        Assert.Equal("CD-001909", rig.Vault.LastRef!.Text);
        Assert.True(j["released"]!.GetValue<bool>());
        Assert.Equal("Compliant", j["property_compliance"]!.GetValue<string>());
        Assert.False(j["linked_to_item"]!.GetValue<bool>());
        Assert.Equal("McMASTER-CARR", j["properties"]!["Vendor"]!.GetValue<string>());
        Assert.Equal(2048, j["size_bytes"]!.GetValue<int>());
    }

    [Fact]
    public void File_reference_needs_exactly_one_of_file_and_file_id()
    {
        using var rig = new Rig();
        foreach (var tool in new[] { "vault_get_file", "vault_where_used", "vault_uses", "vault_file_versions" })
        {
            Assert.True(rig.Call(tool).IsError);
            Assert.True(rig.Call(tool, new JsonObject { ["file"] = "a.ipt", ["file_id"] = 5 }).IsError);
            Assert.True(rig.Call(tool, new JsonObject { ["file_id"] = 0 }).IsError);
            Assert.False(rig.Call(tool, new JsonObject { ["file_id"] = 5 }).IsError);
        }
        Assert.Equal(5, rig.Vault.LastRef!.FileId);
    }

    [Fact]
    public void Where_used_clamps_levels_and_shapes_parents()
    {
        using var rig = new Rig();
        var j = rig.CallJson("vault_where_used", new JsonObject { ["file"] = "CD-001909.ipt", ["levels"] = 50 });
        Assert.Equal(ReadTools.LevelsMax, rig.Vault.LastLevels);
        var p = j["used_by"]![0]!;
        Assert.Equal("A-100.iam", p["name"]!.GetValue<string>());
        Assert.Equal("Dependency", p["relation"]!.GetValue<string>());
        Assert.Equal("CD-001909.ipt", p["uses"]!.GetValue<string>());
        Assert.Equal(1, j["count"]!.GetValue<int>());
    }

    [Fact]
    public void Uses_reports_referenced_versions_and_whether_they_are_latest()
    {
        using var rig = new Rig();
        var j = rig.CallJson("vault_uses", new JsonObject { ["file"] = "A-100.iam" });
        Assert.Equal(1, rig.Vault.LastLevels);
        var rows = (JsonArray)j["uses"]!;
        Assert.Equal(2, rows[0]!["referenced_version"]!.GetValue<int>());
        Assert.False(rows[0]!["referenced_is_latest"]!.GetValue<bool>());
        Assert.Equal(3, rows[0]!["latest_version"]!.GetValue<int>());
        Assert.True(rows[1]!["referenced_is_latest"]!.GetValue<bool>());
    }

    [Fact]
    public void Versions_are_newest_first()
    {
        using var rig = new Rig();
        var j = rig.CallJson("vault_file_versions", new JsonObject { ["file"] = "CD-001909.ipt" });
        var v = (JsonArray)j["versions"]!;
        Assert.Equal(3, v[0]!["version"]!.GetValue<int>());
        Assert.Equal("Released", v[0]!["state"]!.GetValue<string>());
        Assert.Equal("first", v[1]!["comment"]!.GetValue<string>());
        Assert.Null(v[1]!["revision"]);
    }

    [Fact]
    public void Open_bom_flattens_with_item_numbers_and_all_fields()
    {
        using var rig = new Rig();
        var j = rig.CallJson("inventor_open_bom");
        Assert.Equal("Asm1.iam", j["file"]!.GetValue<string>());
        var rows = (JsonArray)j["rows"]!;
        Assert.Equal(3, rows.Count);
        // Description descending: "Sub assembly" first, its child, then "Hex Nut".
        Assert.Equal("1", rows[0]!["item"]!.GetValue<string>());
        Assert.Equal("1.1", rows[1]!["item"]!.GetValue<string>());
        Assert.Equal("Laser", rows[1]!["process"]!.GetValue<string>());
        var nut = rows[2]!;
        Assert.Equal("Buy", nut["source"]!.GetValue<string>());
        Assert.Equal("Purchased", nut["structure"]!.GetValue<string>());
        Assert.Equal(0.12, nut["cost"]!.GetValue<double>());
        Assert.Equal("Work in Progress", nut["state"]!.GetValue<string>());
        Assert.Equal("91290A115", nut["vendor_number"]!.GetValue<string>());
        Assert.Null(nut["process"]);
    }

    [Fact]
    public void Open_bom_honours_max_depth_and_max_rows()
    {
        using var rig = new Rig();
        Assert.Equal(2, ((JsonArray)rig.CallJson("inventor_open_bom", new JsonObject { ["max_depth"] = 1 })["rows"]!).Count);
        var cut = rig.CallJson("inventor_open_bom", new JsonObject { ["max_rows"] = 1 });
        Assert.Single((JsonArray)cut["rows"]!);
        Assert.True(cut["truncated"]!.GetValue<bool>());
        Assert.Equal(3, cut["total_rows"]!.GetValue<int>());
    }

    [Fact]
    public void Read_tools_are_registered_as_bridge_ops_but_write_tools_are_not()
    {
        using var rig = new Rig();
        var router = new Router(Token);
        var ui = 0;
        McpCatalog.RegisterReadOps(router, rig.Tools, w => { ui++; return w(); });
        var (status, json) = router.Handle("POST", "/op/vault_search_files", Token, "{\"query\":\"nut\"}");
        Assert.Equal(200, status);
        Assert.Equal(2, JsonNode.Parse(json)!["count"]!.GetValue<int>());
        Assert.Equal(1, ui);
        Assert.Equal(200, router.Handle("POST", "/op/bridge_status", Token, "").Item1);
        Assert.Equal(404, router.Handle("POST", "/op/fix_data_card_apply", Token, "{}").Item1);
        Assert.Equal(404, router.Handle("POST", "/op/mcmaster_intake_commit", Token, "{}").Item1);
        var (bad, body) = router.Handle("POST", "/op/vault_search_files", Token, "{}");
        Assert.Equal(400, bad);
        Assert.Contains("bad_request", body);
    }

    // ---- FileRefRules ----

    private record Cand(string Name, string Path);

    [Fact]
    public void Pick_prefers_exact_name_then_stem_and_reports_ambiguity()
    {
        var items = new[] { new Cand("CD-1.ipt", "$/a/CD-1.ipt"), new Cand("CD-1.idw", "$/a/CD-1.idw"), new Cand("CD-10.ipt", "$/a/CD-10.ipt") };
        Assert.Equal("$/a/CD-1.idw", FileRefRules.Pick("cd-1.idw", items, c => c.Name, c => c.Path).Path);
        var ex = Assert.Throws<BridgeException>(() => FileRefRules.Pick("CD-1", items, c => c.Name, c => c.Path));
        Assert.Equal("ambiguous", ex.Code);
        Assert.Contains("$/a/CD-1.ipt", ex.Message);
        Assert.Equal("not_found", Assert.Throws<BridgeException>(() => FileRefRules.Pick("CD-2", items, c => c.Name, c => c.Path)).Code);
        Assert.Equal("not_found", Assert.Throws<BridgeException>(() => FileRefRules.Pick("CD-1.pdf", items, c => c.Name, c => c.Path)).Code);
        Assert.Equal("$/a/CD-10.ipt", FileRefRules.Pick("CD-10", items, c => c.Name, c => c.Path).Path);
    }

    [Fact]
    public void Path_detection()
    {
        Assert.True(FileRefRules.IsPath("$/DESIGNS/x.ipt"));
        Assert.False(FileRefRules.IsPath("x.ipt"));
    }

    // ---- preview gate ----

    [Fact]
    public void Hash_ignores_key_order_ignored_keys_and_differs_on_content_or_family()
    {
        var a = (JsonObject)JsonNode.Parse("{\"files\":[\"x\"],\"design\":{\"b\":\"1\",\"a\":\"2\"},\"confirm\":true}")!;
        var b = (JsonObject)JsonNode.Parse("{\"design\":{\"a\":\"2\",\"b\":\"1\"},\"files\":[\"x\"],\"dryRun\":true}")!;
        Assert.Equal(PreviewGate.Hash("f", a, "confirm", "dryRun"), PreviewGate.Hash("f", b, "confirm", "dryRun"));
        var c = (JsonObject)JsonNode.Parse("{\"files\":[\"y\"],\"design\":{\"b\":\"1\",\"a\":\"2\"}}")!;
        Assert.NotEqual(PreviewGate.Hash("f", a, "confirm"), PreviewGate.Hash("f", c, "confirm"));
        Assert.NotEqual(PreviewGate.Hash("f", a, "confirm", "dryRun"), PreviewGate.Hash("g", a, "confirm", "dryRun"));
        var order = (JsonObject)JsonNode.Parse("{\"files\":[\"y\",\"x\"]}")!;
        var order2 = (JsonObject)JsonNode.Parse("{\"files\":[\"x\",\"y\"]}")!;
        Assert.NotEqual(PreviewGate.Hash("f", order), PreviewGate.Hash("f", order2));
    }

    [Fact]
    public void Gate_consumes_once_and_expires_after_the_ttl()
    {
        var now = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
        var gate = new PreviewGate(clock: () => now);
        gate.Record("h");
        Assert.True(gate.Consume("h"));
        Assert.False(gate.Consume("h"));
        gate.Record("h2");
        now = now.AddMinutes(9);
        Assert.Equal(1, gate.Pending);
        now = now.AddMinutes(2);
        Assert.False(gate.Consume("h2"));
        Assert.Equal(TimeSpan.FromMinutes(10), gate.Ttl);
    }

    [Fact]
    public void Preview_store_tokens_are_single_use_and_expire()
    {
        var now = DateTime.UtcNow;
        var store = new PreviewStore<string>(clock: () => now);
        var t = store.Put("p");
        Assert.Equal("p", store.Take(t));
        Assert.Null(store.Take(t));
        var t2 = store.Put("q");
        now = now.AddMinutes(11);
        Assert.Null(store.Take(t2));
        Assert.Null(store.Take(null));
        Assert.Null(store.Take("unknown"));
    }

    // ---- fix data card ----

    private static JsonObject FixArgs(string pn = "91290A115", bool? confirm = null)
    {
        var o = new JsonObject
        {
            ["files"] = new JsonArray("CD-001909.ipt"),
            ["custom"] = new JsonObject { ["Vendor Number"] = pn },
        };
        if (confirm is { } c) o["confirm"] = c;
        return o;
    }

    [Fact]
    public void Fix_preview_runs_a_dry_run_and_does_not_write()
    {
        using var rig = new Rig();
        var j = rig.CallJson("fix_data_card_preview", FixArgs());
        Assert.Equal(1, j["counts"]!["preview"]!.GetValue<int>());
        Assert.Equal(0, rig.Card.Applies);
        Assert.Contains("fix_data_card_apply", j["next"]!.GetValue<string>());
    }

    [Fact]
    public void Fix_apply_without_confirm_is_refused_even_after_a_preview()
    {
        using var rig = new Rig();
        rig.CallJson("fix_data_card_preview", FixArgs());
        foreach (var args in new[] { FixArgs(), FixArgs(confirm: false) })
        {
            var (_, err, text) = rig.Call("fix_data_card_apply", args);
            Assert.True(err);
            Assert.Equal("confirmation_required", JsonNode.Parse(text)!["error"]!.GetValue<string>());
        }
        Assert.Equal(0, rig.Card.Applies);
    }

    [Fact]
    public void Fix_apply_without_a_matching_preview_is_refused()
    {
        using var rig = new Rig();
        var (_, err, text) = rig.Call("fix_data_card_apply", FixArgs(confirm: true));
        Assert.True(err);
        Assert.Equal("no_matching_preview", JsonNode.Parse(text)!["error"]!.GetValue<string>());
        rig.CallJson("fix_data_card_preview", FixArgs("111A1"));
        Assert.True(rig.Call("fix_data_card_apply", FixArgs("222B2", true)).IsError);
        Assert.Equal(0, rig.Card.Applies);
    }

    [Fact]
    public void Fix_apply_after_the_same_preview_writes_once_then_needs_a_new_preview()
    {
        using var rig = new Rig();
        rig.CallJson("fix_data_card_preview", FixArgs());
        var j = rig.CallJson("fix_data_card_apply", FixArgs(confirm: true));
        Assert.Equal(1, j["counts"]!["fixed"]!.GetValue<int>());
        Assert.Equal(1, rig.Card.Applies);
        Assert.True(rig.Call("fix_data_card_apply", FixArgs(confirm: true)).IsError);
        Assert.Equal(1, rig.Card.Applies);
    }

    [Fact]
    public void Fix_apply_requires_files()
    {
        using var rig = new Rig();
        var (_, err, text) = rig.Call("fix_data_card_apply", new JsonObject { ["confirm"] = true });
        Assert.True(err);
        Assert.Equal("bad_request", JsonNode.Parse(text)!["error"]!.GetValue<string>());
    }

    [Fact]
    public void Write_tools_get_a_long_timeout_and_the_kinds_are_right()
    {
        using var rig = new Rig();
        var byName = rig.Tools.ToDictionary(t => t.Name);
        Assert.Equal(McpToolKind.Preview, byName["fix_data_card_preview"].Kind);
        Assert.Equal(McpToolKind.Write, byName["fix_data_card_apply"].Kind);
        Assert.Equal(McpToolKind.Preview, byName["mcmaster_intake_preview"].Kind);
        Assert.Equal(McpToolKind.Write, byName["mcmaster_intake_commit"].Kind);
        Assert.True(byName["fix_data_card_apply"].Timeout >= TimeSpan.FromMinutes(5));
        Assert.True(byName["mcmaster_intake_commit"].Timeout >= TimeSpan.FromMinutes(5));
        Assert.Contains("preview", byName["fix_data_card_apply"].Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("preview", byName["mcmaster_intake_commit"].Description, StringComparison.OrdinalIgnoreCase);
    }

    // ---- McMaster intake ----

    private static JsonObject IntakeArgs(string number = "91290A115", string material = "Steel", string? sub = "screws") => new()
    {
        ["vendor_number"] = number, ["description"] = "Hex Nut", ["material"] = material, ["subfolder"] = sub,
    };

    private static void Download(Rig rig, string number = "91290A115")
    {
        File.WriteAllText(Path.Combine(rig.Downloads, number + "_Hex Nut.STEP"), "step");
        File.WriteAllText(Path.Combine(rig.Downloads, number + ".pdf"), "pdf");
    }

    [Fact]
    public void Intake_preview_finds_downloads_and_shows_the_plan_without_writing()
    {
        using var rig = new Rig();
        Download(rig);
        var j = rig.CallJson("mcmaster_intake_preview", IntakeArgs());
        Assert.True(j["preview"]!.GetValue<bool>());
        Assert.False(string.IsNullOrEmpty(j["token"]!.GetValue<string>()));
        Assert.Equal("$/DESIGNS/Simplifyber Library/McMaster-Carr/screws", j["vault_folder"]!.GetValue<string>());
        Assert.False(j["new_subfolder"]!.GetValue<bool>());
        Assert.Equal("McMASTER-CARR", j["iproperties"]!["Vendor"]!.GetValue<string>());
        Assert.Equal("Buy", j["iproperties"]!["Source"]!.GetValue<string>());
        Assert.Contains("CD-001950", j["iproperties"]!["Title"]!.GetValue<string>());
        Assert.EndsWith("91290A115_Hex Nut.STEP", j["files"]!["step"]!.GetValue<string>());
        Assert.Equal(0, rig.Intake.Runs);
    }

    [Fact]
    public void Intake_preview_flags_a_new_subfolder()
    {
        using var rig = new Rig();
        Download(rig);
        var j = rig.CallJson("mcmaster_intake_preview", IntakeArgs(sub: "hinges"));
        Assert.True(j["new_subfolder"]!.GetValue<bool>());
        Assert.Contains("hinges", j["note"]!.GetValue<string>());
    }

    [Fact]
    public void Intake_preview_stops_on_a_duplicate_and_names_it()
    {
        using var rig = new Rig();
        Download(rig);
        rig.Intake.Dup = ("CD-001800.ipt", "$/DESIGNS/Simplifyber Library/McMaster-Carr/nuts");
        var (_, err, text) = rig.Call("mcmaster_intake_preview", IntakeArgs());
        Assert.True(err);
        var j = JsonNode.Parse(text)!;
        Assert.Equal("duplicate", j["error"]!.GetValue<string>());
        Assert.Equal("CD-001800", j["existing_part"]!.GetValue<string>());
    }

    [Fact]
    public void Intake_preview_checks_part_number_shape_material_and_downloads()
    {
        using var rig = new Rig();
        Assert.Equal("bad_request", JsonNode.Parse(rig.Call("mcmaster_intake_preview", IntakeArgs("NOPE")).Text)!["error"]!.GetValue<string>());
        Assert.Equal("not_found", JsonNode.Parse(rig.Call("mcmaster_intake_preview", IntakeArgs()).Text)!["error"]!.GetValue<string>());
        Download(rig);
        var mat = JsonNode.Parse(rig.Call("mcmaster_intake_preview", IntakeArgs(material: "Stainless")).Text)!;
        Assert.Equal("material", mat["error"]!.GetValue<string>());
        Assert.Contains("Steel, Stainless", mat["message"]!.GetValue<string>());
        Assert.Equal("bad_request", JsonNode.Parse(rig.Call("mcmaster_intake_preview", IntakeArgs(sub: "a:b")).Text)!["error"]!.GetValue<string>());
    }

    [Fact]
    public void Intake_preview_refuses_paths_outside_downloads()
    {
        using var rig = new Rig();
        Download(rig);
        var args = IntakeArgs();
        args["step_path"] = @"C:\Windows\notepad.exe";
        Assert.Equal("path_not_allowed", JsonNode.Parse(rig.Call("mcmaster_intake_preview", args).Text)!["error"]!.GetValue<string>());
    }

    [Fact]
    public void Intake_preview_accepts_explicit_paths_in_downloads()
    {
        using var rig = new Rig();
        var step = Path.Combine(rig.Downloads, "anything.step");
        var pdf = Path.Combine(rig.Downloads, "anything.pdf");
        File.WriteAllText(step, "s"); File.WriteAllText(pdf, "p");
        var args = IntakeArgs();
        args["step_path"] = step; args["pdf_path"] = pdf;
        Assert.Equal(step, rig.CallJson("mcmaster_intake_preview", args)["files"]!["step"]!.GetValue<string>());
    }

    [Fact]
    public void Intake_commit_needs_confirm_a_known_token_and_unchanged_files()
    {
        using var rig = new Rig();
        Download(rig);
        var token = rig.CallJson("mcmaster_intake_preview", IntakeArgs())["token"]!.GetValue<string>();
        Assert.Equal("confirmation_required", JsonNode.Parse(rig.Call("mcmaster_intake_commit", new JsonObject { ["token"] = token }).Text)!["error"]!.GetValue<string>());
        Assert.Equal("no_matching_preview", JsonNode.Parse(rig.Call("mcmaster_intake_commit", new JsonObject { ["token"] = "nope", ["confirm"] = true }).Text)!["error"]!.GetValue<string>());
        // The refused calls above did not burn the token.
        File.WriteAllText(Path.Combine(rig.Downloads, "91290A115.pdf"), "changed");
        var r = rig.Call("mcmaster_intake_commit", new JsonObject { ["token"] = token, ["confirm"] = true });
        Assert.True(r.IsError);
        Assert.Equal("changed_since_preview", JsonNode.Parse(r.Text)!["error"]!.GetValue<string>());
        Assert.Equal(0, rig.Intake.Runs);
    }

    [Fact]
    public void Intake_commit_runs_the_import_once_with_the_previewed_plan()
    {
        using var rig = new Rig();
        Download(rig);
        var token = rig.CallJson("mcmaster_intake_preview", IntakeArgs())["token"]!.GetValue<string>();
        var j = rig.CallJson("mcmaster_intake_commit", new JsonObject { ["token"] = token, ["confirm"] = true });
        Assert.True(j["ok"]!.GetValue<bool>());
        Assert.Equal("CD-001950", j["part"]!.GetValue<string>());
        Assert.Equal(1, rig.Intake.Runs);
        Assert.Equal("McMaster-Carr", rig.Intake.LastPlan!.Vendor);
        Assert.Equal("91290A115", rig.Intake.LastPlan.VendorNumber);
        Assert.Equal("As Finished", rig.Intake.LastPlan.MaterialFinish);
        Assert.Equal("Zak O.", rig.Intake.LastPlan.Designer);
        Assert.True(rig.Call("mcmaster_intake_commit", new JsonObject { ["token"] = token, ["confirm"] = true }).IsError);
        Assert.Equal(1, rig.Intake.Runs);
    }

    [Fact]
    public void Intake_commit_failure_is_an_error_naming_the_step_and_what_reached_vault()
    {
        using var rig = new Rig();
        Download(rig);
        rig.Intake.Fail = true;
        var token = rig.CallJson("mcmaster_intake_preview", IntakeArgs())["token"]!.GetValue<string>();
        var (_, err, text) = rig.Call("mcmaster_intake_commit", new JsonObject { ["token"] = token, ["confirm"] = true });
        Assert.True(err);
        var j = JsonNode.Parse(text)!;
        Assert.Equal("pdf", j["failed_step"]!.GetValue<string>());
        Assert.Equal("CD-001950.ipt", j["already_in_vault"]![0]!.GetValue<string>());
    }

    [Fact]
    public void FindDownload_matches_on_a_boundary_and_prefers_the_newest()
    {
        using var rig = new Rig();
        var old = Path.Combine(rig.Downloads, "91290A115_old.step");
        var fresh = Path.Combine(rig.Downloads, "91290A115 (1).stp");
        File.WriteAllText(old, "a"); File.WriteAllText(fresh, "b");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-2));
        File.WriteAllText(Path.Combine(rig.Downloads, "991290A1155.step"), "c");
        Assert.Equal(fresh, IntakeTools.FindDownload("91290A115", rig.Downloads, ".step", ".stp"));
        Assert.Null(IntakeTools.FindDownload("91290A115", rig.Downloads, ".pdf"));
        Assert.Null(IntakeTools.FindDownload("1", Path.Combine(rig.Downloads, "missing"), ".pdf"));
    }

    // ---- over real HTTP ----

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public async Task Http_host_serves_mcp_next_to_the_bridge_ops()
    {
        using var rig = new Rig();
        var port = FreePort();
        var router = new Router(Token);
        McpCatalog.RegisterReadOps(router, rig.Tools, w => w());
        using var host = new HttpHost($"http://localhost:{port}/", router, _ => { }) { Mcp = rig.Server };
        host.Start();
        using var client = new HttpClient();

        HttpRequestMessage Post(string path, string body, string? token = Token)
        {
            var m = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{port}{path}") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (token is not null) m.Headers.Add(HttpHost.TokenHeader, token);
            return m;
        }

        var init = await client.SendAsync(Post("/mcp", "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\"}}"));
        Assert.Equal(HttpStatusCode.OK, init.StatusCode);
        Assert.Equal("application/json", init.Content.Headers.ContentType!.MediaType);
        Assert.True(init.Headers.Contains("Mcp-Session-Id"));

        var note = await client.SendAsync(Post("/mcp", "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}"));
        Assert.Equal(HttpStatusCode.Accepted, note.StatusCode);
        Assert.Equal("", await note.Content.ReadAsStringAsync());

        var call = await client.SendAsync(Post("/mcp", "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"vault_search_files\",\"arguments\":{\"query\":\"nut\"}}}"));
        var text = JsonNode.Parse(await call.Content.ReadAsStringAsync())!["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Equal(2, JsonNode.Parse(text)!["count"]!.GetValue<int>());

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Post("/mcp", "{}", token: null))).StatusCode);
        var bearer = Post("/mcp", "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"ping\"}", token: null);
        bearer.Headers.Add("Authorization", "Bearer " + Token);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(bearer)).StatusCode);
        var evil = Post("/mcp", "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"ping\"}");
        evil.Headers.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(evil)).StatusCode);
        var get = new HttpRequestMessage(HttpMethod.Get, $"http://localhost:{port}/mcp");
        get.Headers.Add(HttpHost.TokenHeader, Token);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.SendAsync(get)).StatusCode);

        var op = await client.SendAsync(Post("/op/bridge_status", "{}"));
        Assert.Equal(HttpStatusCode.OK, op.StatusCode);
    }

    [Fact]
    public async Task Http_host_without_mcp_answers_404_on_mcp_path()
    {
        var port = FreePort();
        using var host = new HttpHost($"http://localhost:{port}/", new Router(Token), _ => { });
        host.Start();
        using var client = new HttpClient();
        var m = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{port}/mcp") { Content = new StringContent("{}") };
        m.Headers.Add(HttpHost.TokenHeader, Token);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(m)).StatusCode);
    }
}
