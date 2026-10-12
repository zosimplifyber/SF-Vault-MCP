using System.Text.Json.Nodes;
using SFVaultBridge.Core;

namespace SFVaultBridge.Core.Tests;

public class BridgeOpsTests : IDisposable
{
    private const string Token = "0123456789abcdef0123456789abcdef";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sfvb-ops-" + Guid.NewGuid().ToString("N"));

    private sealed class FakeVault : IVaultSession
    {
        public bool SignedIn = true;
        public readonly List<string> Calls = new();
        private void Need() { if (!SignedIn) throw new BridgeException("not_signed_in", BridgeOps.NotSignedInMessage); }

        public StatusInfo Status() => SignedIn ? new(true, "zak@simplifyber.com", "Simplifyber", "simplifyber1") : new(false, null, null, null);
        public IReadOnlyList<SchemeInfo> GetNumberingSchemes() { Need(); return new[] { new SchemeInfo(12, "CAD Data Autonumbering", true) }; }
        public string GenerateFileNumber(long schemeId) { Need(); Calls.Add($"gen {schemeId}"); return "CD-001900"; }
        public FolderInfo? GetFolderByPath(string path) { Need(); return path.EndsWith("/nuts") ? new FolderInfo(84835, path, false) : null; }
        public FolderInfo AddFolder(string parentPath, string name) { Need(); return new FolderInfo(2, parentPath + "/" + name, false); }
        public IReadOnlyList<CategoryInfo> GetFileCategories() { Need(); return new[] { new CategoryInfo(30, "Part - Purchased") }; }
        public VaultFile AddFile(string folderPath, string localPath, string fileName, long[] attachmentFileIds, string comment)
        {
            Need(); Calls.Add($"add {fileName} [{string.Join(",", attachmentFileIds)}] {comment}");
            return new VaultFile(510, 509, fileName, false);
        }
        public IReadOnlyList<VaultFile> UpdateFileCategories(long[] masterIds, long categoryId, string comment)
        {
            Need(); Calls.Add($"cat {string.Join(",", masterIds)} -> {categoryId}");
            return masterIds.Select(m => new VaultFile(m + 1, m, "f", false)).ToList();
        }
    }

    private sealed class FakeInventor : IStepImporter
    {
        public string? LastDesign;
        public string InventorVersion => "2026";
        public string ImportStepAsPart(string stepPath, string outIpt, IReadOnlyDictionary<string, string> design, IReadOnlyDictionary<string, string> custom)
        {
            LastDesign = string.Join(";", design.Select(kv => kv.Key + "=" + kv.Value)) + "|" + string.Join(";", custom.Select(kv => kv.Key + "=" + kv.Value));
            return outIpt;
        }
    }

    private (Router router, FakeVault vault, FakeInventor inv, List<string> uiCalls) Make()
    {
        var vault = new FakeVault();
        var inv = new FakeInventor();
        var ui = new List<string>();
        var router = new Router(Token);
        BridgeOps.RegisterAll(router, vault, inv, new PathGuard(new[] { _root }), "0.1.0", work => { ui.Add("ui"); return work(); });
        return (router, vault, inv, ui);
    }

    public void Dispose() { }

    private static JsonObject Call(Router r, string op, JsonObject body, out int status)
    {
        var (s, json) = r.Handle("POST", "/op/" + op, Token, body.ToJsonString());
        status = s;
        return (JsonObject)JsonNode.Parse(json)!;
    }

    [Fact]
    public void Status_reports_session_and_versions()
    {
        var (r, _, _, ui) = Make();
        var o = Call(r, "status", new JsonObject(), out var st);
        Assert.Equal(200, st);
        Assert.True(o["signed_in"]!.GetValue<bool>());
        Assert.Equal("zak@simplifyber.com", o["user"]!.GetValue<string>());
        Assert.Equal("2026", o["inventor_version"]!.GetValue<string>());
        Assert.Equal("0.1.0", o["bridge_version"]!.GetValue<string>());
        Assert.Single(ui);
    }

    [Fact]
    public void Status_works_when_not_signed_in()
    {
        var (r, vault, _, _) = Make();
        vault.SignedIn = false;
        var o = Call(r, "status", new JsonObject(), out var st);
        Assert.Equal(200, st);
        Assert.False(o["signed_in"]!.GetValue<bool>());
    }

    [Fact]
    public void Vault_ops_return_not_signed_in()
    {
        var (r, vault, _, _) = Make();
        vault.SignedIn = false;
        var o = Call(r, "generate_file_number", new JsonObject { ["schemeId"] = 12 }, out var st);
        Assert.Equal(400, st);
        Assert.Equal("not_signed_in", o["error"]!.GetValue<string>());
        Assert.Equal(BridgeOps.NotSignedInMessage, o["message"]!.GetValue<string>());
    }

    [Fact]
    public void Shapes_match_the_python_wrappers()
    {
        var (r, _, _, _) = Make();
        Assert.Equal("CAD Data Autonumbering", Call(r, "get_numbering_schemes", new(), out _)["schemes"]![0]!["name"]!.GetValue<string>());
        Assert.True(Call(r, "get_numbering_schemes", new(), out _)["schemes"]![0]!["isDefault"]!.GetValue<bool>());
        Assert.Equal("CD-001900", Call(r, "generate_file_number", new() { ["schemeId"] = 12 }, out _)["number"]!.GetValue<string>());
        var found = Call(r, "get_folder_by_path", new() { ["path"] = "$/X/nuts" }, out _);
        Assert.True(found["found"]!.GetValue<bool>());
        Assert.Equal(84835, found["id"]!.GetValue<long>());
        Assert.False(found["isLibrary"]!.GetValue<bool>());
        Assert.False(Call(r, "get_folder_by_path", new() { ["path"] = "$/X/none" }, out _)["found"]!.GetValue<bool>());
        Assert.Equal("$/X/new", Call(r, "add_folder", new() { ["parentPath"] = "$/X", ["name"] = "new" }, out _)["fullName"]!.GetValue<string>());
        Assert.Equal(30, Call(r, "get_file_categories", new(), out _)["categories"]![0]!["id"]!.GetValue<long>());
    }

    [Fact]
    public void Add_file_guards_path_and_passes_attachments()
    {
        var (r, vault, _, _) = Make();
        var local = Path.Combine(_root, "CD-001900.ipt");
        var o = Call(r, "add_file", new() { ["folderPath"] = "$/X", ["localPath"] = local, ["fileName"] = "CD-001900.ipt",
            ["attachmentFileIds"] = new JsonArray(510, 520), ["comment"] = "intake" }, out var st);
        Assert.Equal(200, st);
        Assert.Equal(509, o["masterId"]!.GetValue<long>());
        Assert.Equal("add CD-001900.ipt [510,520] intake", vault.Calls.Single());

        var bad = Call(r, "add_file", new() { ["folderPath"] = "$/X", ["localPath"] = @"C:\Windows\notepad.exe", ["fileName"] = "x" }, out st);
        Assert.Equal(400, st);
        Assert.Equal("path_not_allowed", bad["error"]!.GetValue<string>());
        Assert.Single(vault.Calls);
    }

    [Fact]
    public void Update_categories_requires_ids()
    {
        var (r, vault, _, _) = Make();
        var o = Call(r, "update_file_categories", new() { ["masterIds"] = new JsonArray(529), ["categoryId"] = 30 }, out var st);
        Assert.Equal(200, st);
        Assert.Equal(529, o["updated"]![0]!["masterId"]!.GetValue<long>());
        Assert.Equal("bad_request", Call(r, "update_file_categories", new() { ["masterIds"] = new JsonArray(), ["categoryId"] = 30 }, out _)["error"]!.GetValue<string>());
        Assert.Single(vault.Calls);
    }

    [Fact]
    public void Import_step_guards_both_paths_and_maps_iproperties()
    {
        var (r, _, inv, _) = Make();
        var body = new JsonObject
        {
            ["stepPath"] = Path.Combine(_root, "a.STEP"),
            ["outIpt"] = Path.Combine(_root, "CD-001900.ipt"),
            ["iproperties"] = new JsonObject
            {
                ["design"] = new JsonObject { ["Part Number"] = "CD-001900" },
                ["custom"] = new JsonObject { ["Source"] = "Buy" },
            },
        };
        var o = Call(r, "import_step_as_part", body, out var st);
        Assert.Equal(200, st);
        Assert.EndsWith("CD-001900.ipt", o["outIpt"]!.GetValue<string>());
        Assert.Equal("Part Number=CD-001900|Source=Buy", inv.LastDesign);

        body["outIpt"] = @"C:\Windows\x.ipt";
        Assert.Equal("path_not_allowed", Call(r, "import_step_as_part", body, out _)["error"]!.GetValue<string>());
    }
}
