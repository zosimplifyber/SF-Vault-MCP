using System.Text.Json.Nodes;
using SFVaultBridge.Core;

namespace SFVaultBridge.Core.Tests;

public class DropboxToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sfvb-dbx-" + Guid.NewGuid().ToString("N"));
    private readonly string _pkg;

    public DropboxToolsTests()
    {
        _pkg = Path.Combine(_root, "Downloads", "Acme package");
        Directory.CreateDirectory(Path.Combine(_pkg, "01 Drawings"));
        File.WriteAllText(Path.Combine(_pkg, "00 Cover.pdf"), "cover");
        File.WriteAllText(Path.Combine(_pkg, "01 Drawings", "SF-001111.pdf"), "drawing one");
        File.WriteAllText(Path.Combine(_pkg, "01 Drawings", "Thumbs.db"), "junk");
        File.WriteAllText(Path.Combine(_pkg, "~$lock.docx"), "lock");
        Directory.CreateDirectory(Path.Combine(_root, "Elsewhere"));
        File.WriteAllText(Path.Combine(_root, "Elsewhere", "secret.txt"), "no");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private sealed class FakeUploader : IDropboxUploader
    {
        public bool Configured = true, SignedIn = true;
        public DropboxUploadPlan? Uploaded;
        public Func<DropboxUploadPlan, DropboxUploadOutcome>? Outcome;
        public DropboxAccountStatus Status() => new(Configured, SignedIn, @"C:\tok");
        public DropboxUploadOutcome Upload(DropboxUploadPlan plan, CancellationToken ct)
        {
            Uploaded = plan;
            return Outcome?.Invoke(plan) ?? new DropboxUploadOutcome(plan.Items.Select(i => new DropboxFileResult(i.DropboxPath, i.Bytes, true)).ToList(),
                "https://www.dropbox.com/scl/fo/abc", "public", Array.Empty<string>());
        }
    }

    private (IReadOnlyDictionary<string, McpTool> Tools, FakeUploader Up) Rig(PreviewStore<DropboxUploadPlan>? store = null)
    {
        var up = new FakeUploader();
        var guard = new PathGuard(new[] { Path.Combine(_root, "Downloads") }, "Downloads, Desktop or Documents");
        var tools = DropboxTools.Build(up, guard, store ?? new PreviewStore<DropboxUploadPlan>()).ToDictionary(t => t.Name);
        return (tools, up);
    }

    private static JsonObject Call(IReadOnlyDictionary<string, McpTool> tools, string name, JsonObject args) => (JsonObject)tools[name].Handler(args)!;

    private static BridgeException Fails(IReadOnlyDictionary<string, McpTool> tools, string name, JsonObject args) =>
        Assert.Throws<BridgeException>(() => tools[name].Handler(args));

    // ---- rules ----

    [Fact]
    public void Folder_path_is_normalised()
    {
        Assert.Equal("/Suppliers/Acme/2026", DropboxUploadRules.CleanFolderPath(" /Suppliers//Acme\\2026/ "));
        Assert.Equal("/", DropboxUploadRules.CleanFolderPath("/"));
        Assert.Equal("/Suppliers/A B", DropboxUploadRules.CleanFolderPath("/Suppliers/A:B"));
        Assert.Equal("bad_request", Assert.Throws<BridgeException>(() => DropboxUploadRules.CleanFolderPath("Suppliers/Acme")).Code);
        Assert.Equal("bad_request", Assert.Throws<BridgeException>(() => DropboxUploadRules.CleanFolderPath("/Suppliers/../x")).Code);
        Assert.Equal("bad_request", Assert.Throws<BridgeException>(() => DropboxUploadRules.CleanFolderPath("")).Code);
        Assert.Equal("/Suppliers/Acme/pkg.zip", DropboxUploadRules.Join("/Suppliers/Acme", "pkg.zip"));
        Assert.Equal("/pkg.zip", DropboxUploadRules.Join("/", "pkg.zip"));
    }

    [Fact]
    public void Plan_of_a_folder_keeps_structure_and_skips_junk()
    {
        var plan = DropboxUploadRules.Plan(_pkg, "/Suppliers/Acme", null, true, true);
        Assert.Equal("/Suppliers/Acme", plan.DropboxFolder);
        Assert.Equal("/Suppliers/Acme/Acme package", plan.ShareTarget);
        Assert.Equal(new[] { "/Suppliers/Acme/Acme package/00 Cover.pdf", "/Suppliers/Acme/Acme package/01 Drawings/SF-001111.pdf" },
            plan.Items.Select(i => i.DropboxPath).ToArray());
        Assert.Equal(5 + 11, plan.TotalBytes);
        Assert.Equal(new[] { "/Suppliers", "/Suppliers/Acme", "/Suppliers/Acme/Acme package", "/Suppliers/Acme/Acme package/01 Drawings" },
            DropboxUploadRules.FoldersToCreate(plan));
    }

    [Fact]
    public void Plan_honours_subfolder_name_and_top_level_only()
    {
        var plan = DropboxUploadRules.Plan(_pkg, "/Suppliers/Acme", "Oct 2026", false, false);
        Assert.Single(plan.Items);
        Assert.Equal("/Suppliers/Acme/Oct 2026/00 Cover.pdf", plan.Items[0].DropboxPath);
        Assert.Equal("/Suppliers/Acme/Oct 2026", plan.ShareTarget);
        Assert.False(plan.Share);
    }

    [Fact]
    public void Plan_of_a_single_file_goes_straight_into_the_folder()
    {
        var file = Path.Combine(_pkg, "00 Cover.pdf");
        var plan = DropboxUploadRules.Plan(file, "/Suppliers/Acme", null, true, true);
        Assert.Single(plan.Items);
        Assert.Equal("/Suppliers/Acme/00 Cover.pdf", plan.Items[0].DropboxPath);
        Assert.Equal("/Suppliers/Acme/00 Cover.pdf", plan.ShareTarget);
        Assert.Equal(new[] { "/Suppliers", "/Suppliers/Acme" }, DropboxUploadRules.FoldersToCreate(plan));
    }

    [Fact]
    public void Plan_rejects_missing_and_empty()
    {
        Assert.Equal("not_found", Assert.Throws<BridgeException>(() => DropboxUploadRules.Plan(Path.Combine(_root, "nope"), "/Suppliers/A", null, true, true)).Code);
        var empty = Path.Combine(_root, "Downloads", "empty");
        Directory.CreateDirectory(empty);
        Assert.Equal("bad_request", Assert.Throws<BridgeException>(() => DropboxUploadRules.Plan(empty, "/Suppliers/A", null, true, true)).Code);
    }

    [Fact]
    public void Drift_notices_removed_and_resized_files()
    {
        var plan = DropboxUploadRules.Plan(_pkg, "/Suppliers/Acme", null, true, true);
        Assert.Empty(DropboxUploadRules.Drift(plan));
        File.WriteAllText(Path.Combine(_pkg, "00 Cover.pdf"), "cover v2 longer");
        File.Delete(Path.Combine(_pkg, "01 Drawings", "SF-001111.pdf"));
        var d = DropboxUploadRules.Drift(plan);
        Assert.Equal(2, d.Count);
        Assert.Contains(d, x => x.EndsWith("changed size"));
        Assert.Contains(d, x => x.EndsWith("is gone"));
    }

    // ---- tools ----

    [Fact]
    public void No_uploader_means_no_tools()
        => Assert.Empty(DropboxTools.Build(null, new PathGuard(new[] { _root }), new PreviewStore<DropboxUploadPlan>()));

    [Fact]
    public void Tools_have_the_expected_kinds_and_run_off_the_ui_thread()
    {
        var (tools, _) = Rig();
        Assert.Equal(McpToolKind.Read, tools["dropbox_status"].Kind);
        Assert.Equal(McpToolKind.Preview, tools["dropbox_upload_preview"].Kind);
        Assert.Equal(McpToolKind.Write, tools["dropbox_upload_apply"].Kind);
        Assert.All(tools.Values, t => Assert.False(t.UiThread));
    }

    [Fact]
    public void Status_reports_configuration_and_sign_in()
    {
        var (tools, up) = Rig();
        var r = Call(tools, "dropbox_status", new JsonObject());
        Assert.True((bool)r["configured"]!);
        Assert.True((bool)r["signed_in"]!);
        Assert.Null(r["next"]);
        up.SignedIn = false;
        Assert.Contains("sign-in", (string)Call(tools, "dropbox_status", new JsonObject())["next"]!);
        up.Configured = false;
        Assert.Contains("app key", (string)Call(tools, "dropbox_status", new JsonObject())["next"]!);
    }

    [Fact]
    public void Preview_lists_files_and_hands_out_a_hash()
    {
        var (tools, up) = Rig();
        up.SignedIn = false;
        var r = Call(tools, "dropbox_upload_preview", new JsonObject { ["local_path"] = _pkg, ["partner"] = "Acme Co." });
        Assert.True((bool)r["preview"]!);
        Assert.Equal("/Suppliers/Acme Co", (string)r["dropbox_folder"]!);
        Assert.Equal(2, (int)r["file_count"]!);
        Assert.Equal(16, (long)r["total_bytes"]!);
        Assert.Equal("/Suppliers/Acme Co/Acme package", (string)r["share_target"]!);
        Assert.Equal(2, ((JsonArray)r["files"]!).Count);
        Assert.Contains("sign-in", (string)((JsonArray)r["warnings"]!)[0]!);
        Assert.Equal(64, ((string)r["preview_hash"]!).Length);
        Assert.Null(up.Uploaded);
    }

    [Fact]
    public void Preview_refuses_without_key_folder_or_allowed_path()
    {
        var (tools, up) = Rig();
        Assert.Equal("bad_request", Fails(tools, "dropbox_upload_preview", new JsonObject { ["local_path"] = _pkg }).Code);
        Assert.Equal("path_not_allowed", Fails(tools, "dropbox_upload_preview", new JsonObject { ["local_path"] = Path.Combine(_root, "Elsewhere", "secret.txt"), ["partner"] = "A" }).Code);
        Assert.Equal("bad_request", Fails(tools, "dropbox_upload_preview", new JsonObject { ["local_path"] = _pkg, ["dropbox_folder"] = "Suppliers/A" }).Code);
        Assert.Equal("bad_request", Fails(tools, "dropbox_upload_preview", new JsonObject { ["local_path"] = "", ["partner"] = "A" }).Code);
        up.Configured = false;
        Assert.Equal("not_configured", Fails(tools, "dropbox_upload_preview", new JsonObject { ["local_path"] = _pkg, ["partner"] = "A" }).Code);
    }

    [Fact]
    public void Apply_needs_confirm_and_a_fresh_hash_then_uploads_once()
    {
        var (tools, up) = Rig();
        var p = Call(tools, "dropbox_upload_preview", new JsonObject { ["local_path"] = _pkg, ["dropbox_folder"] = "/Suppliers/Acme/2026" });
        var hash = (string)p["preview_hash"]!;
        Assert.Equal("confirmation_required", Fails(tools, "dropbox_upload_apply", new JsonObject { ["preview_hash"] = hash }).Code);
        Assert.Equal("no_matching_preview", Fails(tools, "dropbox_upload_apply", new JsonObject { ["preview_hash"] = new string('0', 64), ["confirm"] = true }).Code);
        Assert.Null(up.Uploaded);

        var r = Call(tools, "dropbox_upload_apply", new JsonObject { ["preview_hash"] = hash, ["confirm"] = true });
        Assert.True((bool)r["ok"]!);
        Assert.Equal(2, (int)r["uploaded"]!);
        Assert.Equal(0, (int)r["failed"]!);
        Assert.Equal("https://www.dropbox.com/scl/fo/abc", (string)r["share_url"]!);
        Assert.Equal("/Suppliers/Acme/2026", up.Uploaded!.DropboxFolder);
        Assert.Equal("/Suppliers/Acme/2026/Acme package", up.Uploaded.ShareTarget);

        // one use only
        Assert.Equal("no_matching_preview", Fails(tools, "dropbox_upload_apply", new JsonObject { ["preview_hash"] = hash, ["confirm"] = true }).Code);
    }

    [Fact]
    public void Apply_refuses_when_a_file_changed_since_the_preview()
    {
        var (tools, up) = Rig();
        var hash = (string)Call(tools, "dropbox_upload_preview", new JsonObject { ["local_path"] = _pkg, ["partner"] = "Acme" })["preview_hash"]!;
        File.WriteAllText(Path.Combine(_pkg, "00 Cover.pdf"), "cover changed");
        Assert.Equal("changed_since_preview", Fails(tools, "dropbox_upload_apply", new JsonObject { ["preview_hash"] = hash, ["confirm"] = true }).Code);
        Assert.Null(up.Uploaded);
    }

    [Fact]
    public void Apply_reports_partial_failure_as_not_ok()
    {
        var (tools, up) = Rig();
        up.Outcome = plan => new DropboxUploadOutcome(
            new[] { new DropboxFileResult(plan.Items[0].DropboxPath, 1, true), new DropboxFileResult(plan.Items[1].DropboxPath, 1, false, "Dropbox 507: insufficient_space") },
            null, null, new[] { "Dropbox share link failed (the files are uploaded): x" });
        var hash = (string)Call(tools, "dropbox_upload_preview", new JsonObject { ["local_path"] = _pkg, ["partner"] = "Acme" })["preview_hash"]!;
        var r = Call(tools, "dropbox_upload_apply", new JsonObject { ["preview_hash"] = hash, ["confirm"] = true });
        Assert.False((bool)r["ok"]!);
        Assert.Equal(1, (int)r["failed"]!);
        Assert.Contains("insufficient_space", (string)((JsonArray)r["failures"]!)[0]!["error"]!);
        Assert.Null(r["share_url"]);
        Assert.Single((JsonArray)r["warnings"]!);
    }

    [Fact]
    public void Preview_hash_expires()
    {
        var now = new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
        var store = new PreviewStore<DropboxUploadPlan>(TimeSpan.FromMinutes(10), () => now);
        var (tools, _) = Rig(store);
        var hash = (string)Call(tools, "dropbox_upload_preview", new JsonObject { ["local_path"] = _pkg, ["partner"] = "Acme" })["preview_hash"]!;
        now = now.AddMinutes(11);
        Assert.Equal("no_matching_preview", Fails(tools, "dropbox_upload_apply", new JsonObject { ["preview_hash"] = hash, ["confirm"] = true }).Code);
    }

    // ---- off-UI jobs ----

    [Fact]
    public void Job_manager_can_run_work_off_the_ui_thread()
    {
        var uiCalls = 0;
        var jobs = new JobManager(w => { uiCalls++; return w(); }, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(50));
        var r = (JsonObject)jobs.RunOrDetach("dropbox_upload_apply", () => new JsonObject { ["ok"] = true }, onUi: false)!;
        Assert.True((bool)r["ok"]!);
        Assert.Equal(0, uiCalls);
        var r2 = (JsonObject)jobs.RunOrDetach("other", () => new JsonObject { ["ok"] = true })!;
        Assert.True((bool)r2["ok"]!);
        Assert.Equal(1, uiCalls);
    }

    [Fact]
    public void Server_runs_a_non_ui_write_as_a_job_off_the_ui_thread()
    {
        var uiCalls = 0;
        var ran = false;
        var tool = new McpTool("off_ui_write", "t", "d", Schema.Obj(new JsonObject()), McpToolKind.Write, _ => { ran = true; return new JsonObject { ["ok"] = true }; }, UiThread: false);
        var server = new McpServer("0123456789abcdef0123456789abcdef", "1", new[] { tool }, w => { uiCalls++; return w(); });
        var body = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"off_ui_write\",\"arguments\":{}}}";
        var resp = server.Handle(new McpHttpRequest("POST", "0123456789abcdef0123456789abcdef", null, null, null, body));
        Assert.Equal(200, resp.Status);
        Assert.True(ran);
        Assert.Equal(0, uiCalls);
        Assert.Contains("\"isError\":false", resp.Body);
        Assert.Contains("ok", resp.Body);
    }
}
