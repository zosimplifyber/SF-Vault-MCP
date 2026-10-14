using System.Text.Json.Nodes;
using SFVaultBridge.Core;

namespace SFVaultBridge.Core.Tests;

public class ExtraToolsTests
{
    private static readonly PropDefLite[] Defs =
    {
        new(1, "Name", "Name", "String", true, true, 100),
        new(2, "State", "State", "String", true, true, 100),
        new(3, "Part Number", "PartNumber", "String", true, false, 50),
        new(4, "Cost", "Cost", "Numeric", true, false, 10),
        new(5, "Released Date", "ReleasedDate", "DateTime", true, false, 10),
        new(6, "Approved", "Approved", "Bool", true, false, 10),
        new(7, "Old Thing", "OldThing", "String", false, false, 0),
    };

    private sealed class FakeExtras : IVaultExtras
    {
        public MappedSearch? LastSearch; public FolderRef? LastFolder; public bool? LastIncludeFiles;
        public bool QueueEnabled = true;
        public List<VaultJobInfo> Jobs = new();
        public PdfTarget Target = new(500, 50, "A-100.idw", "$/DESIGNS/A-100.idw", 4, "Work in Progress", "A", false, "");
        public int Submits; public string? SubmittedType; public IReadOnlyDictionary<string, string>? SubmittedParams; public string? SubmittedDesc; public int SubmittedPriority;
        public VaultFileRef? LastTargetRef;
        public int FilesInFolder = 2;

        public VaultFolderInfo FindFolder(string path) =>
            path == "$/Nope" ? throw new BridgeException("not_found", "No Vault folder at $/Nope.") : new VaultFolderInfo(7, path.Split('/').Last(), path, 3);
        public FolderListing ListFolder(FolderRef folder, bool includeFiles, int limit)
        {
            LastFolder = folder; LastIncludeFiles = includeFiles;
            var files = Enumerable.Range(1, FilesInFolder).Select(i => new VaultFileSummary(i, i + 100, $"F{i}.ipt", "$/X", "Released", "A", "Part", 1, 1)).ToList();
            return new FolderListing(new VaultFolderInfo(7, "X", "$/X", 3), new[] { new VaultFolderInfo(8, "sub", "$/X/sub", 0) }, files, false, false);
        }
        public IReadOnlyList<PropDefLite> FileSearchProperties() => Defs;
        public VaultSearchResult Search(MappedSearch search)
        {
            LastSearch = search;
            return new VaultSearchResult(new[] { new VaultFileSummary(1, 101, "CD-1.ipt", "$/X", "Released", "A", "Part", 1, 1) }, false);
        }
        public bool JobQueueEnabled() => QueueEnabled;
        public IReadOnlyList<VaultJobInfo> RecentJobs(int max) => Jobs;
        public PdfTarget ResolvePdfTarget(VaultFileRef file) { LastTargetRef = file; return Target; }
        public long SubmitJob(string jobType, string description, IReadOnlyDictionary<string, string> parameters, int priority)
        {
            Submits++; SubmittedType = jobType; SubmittedParams = parameters; SubmittedDesc = description; SubmittedPriority = priority;
            return 9001;
        }
        public IReadOnlyList<PropDefLite> PropertyDefinitions(string entityClass) => Defs;
        public IReadOnlyList<CategoryInfoLite> Categories() => new[] { new CategoryInfoLite("Part - Purchased", "", new[] { "FILE" }), new CategoryInfoLite("Base", "x", new[] { "FILE", "FLDR" }) };
        public IReadOnlyList<LifecycleDefInfo> Lifecycles() => new[]
        {
            new LifecycleDefInfo("Flexible Release", "", new[]
            {
                new LifecycleStateInfo(1, "Work in Progress", "Work in Progress", false, false, true),
                new LifecycleStateInfo(2, "Released", "Released", true, false, false),
                new LifecycleStateInfo(3, "Obsolete", "Obsolete", false, true, false),
            }, new[] { new LifecycleTransitionInfo(1, 2), new LifecycleTransitionInfo(2, 3), new LifecycleTransitionInfo(2, 99) }),
        };
        public IReadOnlyList<RevisionSchemeInfo> RevisionSchemes() => new[]
        {
            new RevisionSchemeInfo("Letters", "", ".", new[] { new RevisionSequenceInfo("primary", "A-Z", Enumerable.Range(0, 26).Select(i => ((char)('A' + i)).ToString()).ToList()) }),
        };
        public IReadOnlyList<VaultUserInfo> Users() => new[]
        {
            new VaultUserInfo(1, "Zed", "z@x.com", true, "Windows"), new VaultUserInfo(2, "Amy", "", true, "Vault"), new VaultUserInfo(3, "Old", "", false, "Vault"),
        };
        public IReadOnlyList<VaultGroupInfo> Groups() => new[] { new VaultGroupInfo(1, "Engineers", "e@x.com", true, new[] { "Zed", "Amy" }, new[] { "Interns" }) };
    }

    private sealed class Rig
    {
        public readonly FakeExtras V = new();
        public readonly PreviewStore<PdfJobPlan> Store = new();
        public readonly IReadOnlyList<McpTool> Tools;
        public Rig() { Tools = ExtraTools.Build(V, Store); }
        public JsonObject Call(string name, JsonObject? args = null) =>
            (JsonObject)Tools.First(t => t.Name == name).Handler(args ?? new JsonObject())!;
        public BridgeException Fails(string name, JsonObject? args = null) =>
            Assert.Throws<BridgeException>(() => Call(name, args));
    }

    private static JsonObject Cond(string prop, string op, JsonNode? value = null)
    {
        var o = new JsonObject { ["property"] = prop, ["operator"] = op };
        if (value is not null) o["value"] = value;
        return o;
    }

    private static JsonObject Search(params JsonObject[] conds) => new() { ["conditions"] = new JsonArray(conds.Select(c => (JsonNode?)c).ToArray()) };

    // ---- catalog ----

    [Fact]
    public void Catalog_lists_the_thirteen_new_tools_with_the_right_kinds()
    {
        var r = new Rig();
        Assert.Equal(13, r.Tools.Count);
        var kinds = r.Tools.ToDictionary(t => t.Name, t => t.Kind);
        Assert.Equal(McpToolKind.Preview, kinds["vault_submit_pdf_job_preview"]);
        Assert.Equal(McpToolKind.Write, kinds["vault_submit_pdf_job_apply"]);
        Assert.Single(kinds, k => k.Value == McpToolKind.Write);
        Assert.All(r.Tools.Where(t => t.Name.StartsWith("vault_") && !t.Name.Contains("submit")), t => Assert.Equal(McpToolKind.Read, t.Kind));
        Assert.All(r.Tools, t => Assert.False(string.IsNullOrWhiteSpace(t.Description)));
        Assert.All(r.Tools, t => Assert.DoesNotContain("\u2014", t.Description));
    }

    [Fact]
    public void Catalog_wires_extras_only_when_given()
    {
        // The write tool goes through the job manager like the other writes.
        var jobs = new JobManager(w => w());
        var list = McpCatalog.Build(new NoVault(), new NoInventor(), new NoSession(), new NoImporter(), "1", null, null, new PathGuard(new[] { Path.GetTempPath() }), Path.GetTempPath(), jobs);
        Assert.DoesNotContain(list, t => t.Name == "vault_list_folder");
        var with = McpCatalog.Build(new NoVault(), new NoInventor(), new NoSession(), new NoImporter(), "1", null, null, new PathGuard(new[] { Path.GetTempPath() }), Path.GetTempPath(), jobs, null, new FakeExtras());
        Assert.Equal(list.Count + 13, with.Count);
        Assert.Equal(with.Count, with.Select(t => t.Name).Distinct().Count());
    }

    private sealed class NoVault : IVaultReader
    {
        public VaultSearchResult SearchFiles(string query, int limit) => throw new NotSupportedException();
        public VaultFileDetail GetFile(VaultFileRef file) => throw new NotSupportedException();
        public VaultRelations WhereUsed(VaultFileRef file, int levels, int maxRows) => throw new NotSupportedException();
        public VaultRelations Uses(VaultFileRef file, int levels, int maxRows) => throw new NotSupportedException();
        public VaultVersions Versions(VaultFileRef file, int limit) => throw new NotSupportedException();
    }
    private sealed class NoInventor : IInventorReader
    {
        public OpenBomResult OpenBom() => throw new NotSupportedException();
        public string? ActiveDocument() => null;
    }
    private sealed class NoSession : IVaultSession
    {
        public StatusInfo Status() => new(false, null, null, null);
        public IReadOnlyList<SchemeInfo> GetNumberingSchemes() => throw new NotSupportedException();
        public string GenerateFileNumber(long schemeId) => throw new NotSupportedException();
        public FolderInfo? GetFolderByPath(string path) => throw new NotSupportedException();
        public FolderInfo AddFolder(string parentPath, string name) => throw new NotSupportedException();
        public IReadOnlyList<CategoryInfo> GetFileCategories() => throw new NotSupportedException();
        public VaultFile AddFile(string folderPath, string localPath, string fileName, long[] attachmentFileIds, string comment) => throw new NotSupportedException();
        public IReadOnlyList<VaultFile> UpdateFileCategories(long[] masterIds, long categoryId, string comment) => throw new NotSupportedException();
    }
    private sealed class NoImporter : IStepImporter
    {
        public string InventorVersion => "2026";
        public string ImportStepAsPart(string stepPath, string outIpt, IReadOnlyDictionary<string, string> design, IReadOnlyDictionary<string, string> custom) => outIpt;
    }

    // ---- folders ----

    [Theory]
    [InlineData("$", "$")]
    [InlineData("$/A/B/", "$/A/B")]
    [InlineData("  $\\A\\B  ", "$/A/B")]
    public void Folder_paths_normalize(string input, string expected) => Assert.Equal(expected, ExtraRules.NormalizeFolderPath(input));

    [Theory]
    [InlineData("DESIGNS")]
    [InlineData("C:/x")]
    [InlineData("$A")]
    public void Folder_paths_must_start_with_the_root(string input) =>
        Assert.Equal("bad_request", Assert.Throws<BridgeException>(() => ExtraRules.NormalizeFolderPath(input)).Code);

    [Fact]
    public void List_folder_defaults_to_root_with_files()
    {
        var r = new Rig();
        var o = r.Call("vault_list_folder");
        Assert.Equal("$", r.V.LastFolder!.Path);
        Assert.True(r.V.LastIncludeFiles);
        Assert.Equal(2, o["file_count"]!.GetValue<int>());
        Assert.Equal("sub", o["subfolders"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("Released", o["files"]![0]!["state"]!.GetValue<string>());
    }

    [Fact]
    public void List_folder_without_files_omits_them_and_truncates_at_limit()
    {
        var r = new Rig { };
        var o = r.Call("vault_list_folder", new JsonObject { ["path"] = "$/X", ["include_files"] = false });
        Assert.Null(o["files"]);
        r.V.FilesInFolder = 5;
        var t = r.Call("vault_list_folder", new JsonObject { ["folder_id"] = 7L, ["limit"] = 2L });
        Assert.Equal(7, r.V.LastFolder!.Id);
        Assert.Equal(2, t["file_count"]!.GetValue<int>());
        Assert.True(t["files_truncated"]!.GetValue<bool>());
    }

    [Fact]
    public void List_folder_rejects_both_path_and_id_and_bad_types()
    {
        var r = new Rig();
        Assert.Equal("bad_request", r.Fails("vault_list_folder", new JsonObject { ["path"] = "$/X", ["folder_id"] = 3L }).Code);
        Assert.Equal("bad_request", r.Fails("vault_list_folder", new JsonObject { ["folder_id"] = "x" }).Code);
        Assert.Equal("bad_request", r.Fails("vault_list_folder", new JsonObject { ["folder_id"] = 0 }).Code);
        Assert.Equal("bad_request", r.Fails("vault_list_folder", new JsonObject { ["include_files"] = "yes" }).Code);
    }

    [Fact]
    public void Find_folder_returns_id_and_reports_missing()
    {
        var r = new Rig();
        var o = r.Call("vault_find_folder", new JsonObject { ["path"] = "$/DESIGNS/Lib/" });
        Assert.Equal(7L, o["folder_id"]!.GetValue<long>());
        Assert.Equal("$/DESIGNS/Lib", o["path"]!.GetValue<string>());
        Assert.Equal("not_found", r.Fails("vault_find_folder", new JsonObject { ["path"] = "$/Nope" }).Code);
        Assert.Equal("bad_request", r.Fails("vault_find_folder", new JsonObject()).Code);
    }

    // ---- advanced search mapping ----

    [Fact]
    public void Search_maps_names_operators_and_defaults()
    {
        var r = new Rig();
        var o = r.Call("vault_advanced_search", Search(Cond("state", "equals", "Released"), Cond("part number", "contains", "CD-")));
        var m = r.V.LastSearch!;
        Assert.False(m.Any); Assert.True(m.Recurse); Assert.True(m.LatestOnly); Assert.Equal(50, m.Limit); Assert.Null(m.FolderPath);
        Assert.Equal(2, m.Conditions[0].PropDefId);
        Assert.Equal(ExtraRules.OpEquals, m.Conditions[0].OperatorCode);
        Assert.Equal(3, m.Conditions[1].PropDefId);
        Assert.Equal(ExtraRules.OpContains, m.Conditions[1].OperatorCode);
        Assert.Equal("all", o["match"]!.GetValue<string>());
        Assert.Equal(1, o["count"]!.GetValue<int>());
    }

    [Fact]
    public void Search_resolves_system_names_and_ignores_spacing()
    {
        var r = new Rig();
        r.Call("vault_advanced_search", Search(Cond("PartNumber", "equals", "x"), Cond("released_date", "greater_than", "2026-01-15")));
        Assert.Equal(new long[] { 3, 5 }, r.V.LastSearch!.Conditions.Select(c => c.PropDefId).ToArray());
        Assert.Equal("2026-01-15 00:00:00", r.V.LastSearch.Conditions[1].Text);
    }

    [Fact]
    public void Search_unknown_property_names_it_and_lists_near_matches()
    {
        var r = new Rig();
        var e = r.Fails("vault_advanced_search", Search(Cond("Part Numbr", "equals", "x"), Cond("State", "equals", "a")));
        Assert.Equal("unknown_property", e.Code);
        Assert.Contains("'Part Numbr'", e.Message);
        var e2 = r.Fails("vault_advanced_search", Search(Cond("Part", "equals", "x")));
        Assert.Contains("Part Number", e2.Message);
        Assert.Contains("vault_property_definitions", e2.Message);
    }

    [Fact]
    public void Search_does_not_use_inactive_definitions() =>
        Assert.Equal("unknown_property", new Rig().Fails("vault_advanced_search", Search(Cond("Old Thing", "equals", "x"))).Code);

    [Theory]
    [InlineData("contains", ExtraRules.OpContains, false)]
    [InlineData("starts_with", ExtraRules.OpContains, true)]
    [InlineData("equals", ExtraRules.OpEquals, false)]
    [InlineData("not_equals", ExtraRules.OpNotEquals, false)]
    [InlineData("Is-Empty", ExtraRules.OpIsEmpty, false)]
    [InlineData("is not empty", ExtraRules.OpIsNotEmpty, false)]
    public void Operators_map_to_vault_codes(string op, int code, bool startsWith)
    {
        var r = new Rig();
        var empty = op.ToLowerInvariant().Contains("empty");
        r.Call("vault_advanced_search", Search(empty ? Cond("State", op) : Cond("State", op, "x")));
        Assert.Equal(code, r.V.LastSearch!.Conditions[0].OperatorCode);
        Assert.Equal(startsWith, r.V.LastSearch.Conditions[0].StartsWith);
    }

    [Fact]
    public void Numeric_comparisons_map_and_validate()
    {
        var r = new Rig();
        r.Call("vault_advanced_search", Search(Cond("Cost", "greater_than", 2.5), Cond("Cost", "less_than", "10")));
        Assert.Equal(ExtraRules.OpGreater, r.V.LastSearch!.Conditions[0].OperatorCode);
        Assert.Equal("2.5", r.V.LastSearch.Conditions[0].Text);
        Assert.Equal(ExtraRules.OpLess, r.V.LastSearch.Conditions[1].OperatorCode);
        Assert.Equal("bad_request", r.Fails("vault_advanced_search", Search(Cond("Cost", "equals", "cheap"))).Code);
        Assert.Equal("bad_request", r.Fails("vault_advanced_search", Search(Cond("Cost", "contains", "1"))).Code);
        Assert.Equal("bad_request", r.Fails("vault_advanced_search", Search(Cond("State", "greater_than", "A"))).Code);
        Assert.Equal("bad_request", r.Fails("vault_advanced_search", Search(Cond("Released Date", "less_than", "soon"))).Code);
    }

    [Fact]
    public void Bool_values_are_normalized()
    {
        var r = new Rig();
        r.Call("vault_advanced_search", Search(Cond("Approved", "equals", true)));
        Assert.Equal("True", r.V.LastSearch!.Conditions[0].Text);
        r.Call("vault_advanced_search", Search(Cond("Approved", "equals", "no")));
        Assert.Equal("False", r.V.LastSearch.Conditions[0].Text);
        Assert.Equal("bad_request", r.Fails("vault_advanced_search", Search(Cond("Approved", "equals", "maybe"))).Code);
    }

    [Fact]
    public void Search_options_pass_through_and_limit_clamps()
    {
        var r = new Rig();
        var args = Search(Cond("State", "equals", "Released"));
        args["any"] = true; args["folder"] = "$/A/"; args["recurse"] = false; args["latest_only"] = false; args["limit"] = 9999L;
        var o = r.Call("vault_advanced_search", args);
        var m = r.V.LastSearch!;
        Assert.True(m.Any); Assert.False(m.Recurse); Assert.False(m.LatestOnly); Assert.Equal("$/A", m.FolderPath); Assert.Equal(200, m.Limit);
        Assert.Equal("any", o["match"]!.GetValue<string>());
        Assert.Equal("$/A", o["folder"]!.GetValue<string>());
    }

    [Fact]
    public void Search_rejects_bad_requests()
    {
        var r = new Rig();
        Assert.Equal("bad_request", r.Fails("vault_advanced_search").Code);
        Assert.Equal("bad_request", r.Fails("vault_advanced_search", new JsonObject { ["conditions"] = new JsonArray() }).Code);
        Assert.Equal("bad_request", r.Fails("vault_advanced_search", new JsonObject { ["conditions"] = "State=Released" }).Code);
        Assert.Equal("bad_request", r.Fails("vault_advanced_search", Search(Cond("State", "like", "x"))).Code);
        Assert.Equal("bad_request", r.Fails("vault_advanced_search", Search(Cond("State", "equals"))).Code);
        Assert.Equal("bad_request", r.Fails("vault_advanced_search", Search(Cond("", "equals", "x"))).Code);
        Assert.Equal("bad_request", r.Fails("vault_advanced_search", Search(Enumerable.Range(0, 11).Select(_ => Cond("State", "equals", "x")).ToArray())).Code);
        var bad = Search(Cond("State", "equals", "x")); bad["folder"] = "DESIGNS";
        Assert.Equal("bad_request", r.Fails("vault_advanced_search", bad).Code);
        Assert.Equal("bad_request", r.Fails("vault_advanced_search", Search(Cond("State", "equals", new JsonObject()))).Code);
    }

    // ---- job queue ----

    private static VaultJobInfo Job(long id, string status, string created, string type = "Autodesk.Vault.PDF.Create.idw", long ver = 0, string file = "") =>
        new(id, type, status, "", "Zak", created, "d", file, ver, 10);

    [Fact]
    public void Job_queue_filters_sorts_and_limits()
    {
        var r = new Rig();
        r.V.Jobs.AddRange(new[]
        {
            Job(1, "Success", "2026-10-01 10:00", file: "A.idw", ver: 5), Job(2, "Failure", "2026-10-03 10:00"),
            Job(3, "Ready", "2026-10-02 10:00"), Job(4, "Running", "2026-10-04 10:00"),
        });
        var all = r.Call("vault_job_queue");
        Assert.Equal(new long[] { 4, 2, 3, 1 }, all["jobs"]!.AsArray().Select(j => j!["job_id"]!.GetValue<long>()).ToArray());
        Assert.Equal("A.idw", all["jobs"]![3]!["file"]!.GetValue<string>());
        var active = r.Call("vault_job_queue", new JsonObject { ["status"] = "active" });
        Assert.Equal(new long[] { 4, 3 }, active["jobs"]!.AsArray().Select(j => j!["job_id"]!.GetValue<long>()).ToArray());
        var fail = r.Call("vault_job_queue", new JsonObject { ["status"] = "FAILURE" });
        Assert.Equal(1, fail["count"]!.GetValue<int>());
        var limited = r.Call("vault_job_queue", new JsonObject { ["limit"] = 2L });
        Assert.True(limited["truncated"]!.GetValue<bool>());
        Assert.Equal(4, limited["total_matching"]!.GetValue<int>());
        Assert.Equal("bad_request", r.Fails("vault_job_queue", new JsonObject { ["status"] = "stuck" }).Code);
    }

    [Fact]
    public void Job_queue_enabled_reports_both_ways()
    {
        var r = new Rig();
        Assert.True(r.Call("vault_job_queue_enabled")["enabled"]!.GetValue<bool>());
        r.V.QueueEnabled = false;
        Assert.False(r.Call("vault_job_queue_enabled")["enabled"]!.GetValue<bool>());
    }

    // ---- PDF job preview / apply ----

    [Fact]
    public void Pdf_job_type_is_the_standard_vault_one_and_only_for_drawings()
    {
        var (t, p) = ExtraRules.PdfJobFor("A-100.IDW", 500);
        Assert.Equal("Autodesk.Vault.PDF.Create.idw", t);
        Assert.Equal("500", p["FileVersionId"]);
        Assert.Equal("False", p["UpdateViewOption"]);
        Assert.Equal("Autodesk.Vault.PDF.Create.dwg", ExtraRules.PdfJobFor("x.dwg", 1).Type);
        Assert.Equal("bad_request", Assert.Throws<BridgeException>(() => ExtraRules.PdfJobFor("x.ipt", 1)).Code);
    }

    [Fact]
    public void Preview_writes_nothing_and_returns_a_hash()
    {
        var r = new Rig();
        var o = r.Call("vault_submit_pdf_job_preview", new JsonObject { ["file"] = "A-100.idw" });
        Assert.Equal(0, r.V.Submits);
        Assert.Equal("A-100.idw", r.V.LastTargetRef!.Text);
        Assert.Equal("Autodesk.Vault.PDF.Create.idw", o["job_type"]!.GetValue<string>());
        Assert.Equal(64, o["preview_hash"]!.GetValue<string>().Length);
        Assert.Equal(4, o["file"]!["version"]!.GetValue<int>());
        Assert.Null(o["warnings"]);
    }

    [Fact]
    public void Preview_rejects_non_drawings_and_unlocks_nothing()
    {
        var r = new Rig();
        r.V.Target = r.V.Target with { Name = "CD-1.ipt" };
        Assert.Equal("bad_request", r.Fails("vault_submit_pdf_job_preview", new JsonObject { ["file"] = "CD-1.ipt" }).Code);
        Assert.Equal("bad_request", r.Fails("vault_submit_pdf_job_preview", new JsonObject()).Code);
    }

    [Fact]
    public void Preview_warns_about_disabled_queue_and_checkout()
    {
        var r = new Rig();
        r.V.QueueEnabled = false;
        r.V.Target = r.V.Target with { CheckedOut = true, CheckedOutBy = "Amy" };
        var o = r.Call("vault_submit_pdf_job_preview", new JsonObject { ["file_id"] = 500L });
        Assert.Equal(500, r.V.LastTargetRef!.FileId);
        var w = string.Join(" ", o["warnings"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Contains("disabled", w);
        Assert.Contains("Amy", w);
        Assert.NotNull(o["preview_hash"]);
    }

    [Fact]
    public void Preview_of_an_already_queued_version_unlocks_nothing()
    {
        var r = new Rig();
        r.V.Jobs.Add(Job(77, "Ready", "2026-10-14 09:00", ver: 500));
        var o = r.Call("vault_submit_pdf_job_preview", new JsonObject { ["file"] = "A-100.idw" });
        Assert.True(o["already_queued"]!.GetValue<bool>());
        Assert.Equal(77, o["existing_job_id"]!.GetValue<long>());
        Assert.Null(o["preview_hash"]);
        // a finished job for the same version does not block
        var r2 = new Rig();
        r2.V.Jobs.Add(Job(78, "Success", "2026-10-14 09:00", ver: 500));
        Assert.NotNull(r2.Call("vault_submit_pdf_job_preview", new JsonObject { ["file"] = "A-100.idw" })["preview_hash"]);
    }

    private static string Preview(Rig r) =>
        r.Call("vault_submit_pdf_job_preview", new JsonObject { ["file"] = "A-100.idw" })["preview_hash"]!.GetValue<string>();

    [Fact]
    public void Apply_needs_confirm_and_a_known_hash()
    {
        var r = new Rig();
        var hash = Preview(r);
        Assert.Equal("confirmation_required", r.Fails("vault_submit_pdf_job_apply", new JsonObject { ["preview_hash"] = hash }).Code);
        Assert.Equal("confirmation_required", r.Fails("vault_submit_pdf_job_apply", new JsonObject { ["preview_hash"] = hash, ["confirm"] = false }).Code);
        Assert.Equal("bad_request", r.Fails("vault_submit_pdf_job_apply", new JsonObject { ["preview_hash"] = hash, ["confirm"] = "true" }).Code);
        Assert.Equal("no_matching_preview", r.Fails("vault_submit_pdf_job_apply", new JsonObject { ["preview_hash"] = new string('0', 64), ["confirm"] = true }).Code);
        Assert.Equal(0, r.V.Submits);
        // none of the refusals used the preview up
        var o = r.Call("vault_submit_pdf_job_apply", new JsonObject { ["preview_hash"] = hash, ["confirm"] = true });
        Assert.Equal(1, r.V.Submits);
        Assert.Equal(9001, o["job_id"]!.GetValue<long>());
    }

    [Fact]
    public void Apply_submits_exactly_the_previewed_job_once()
    {
        var r = new Rig();
        var hash = Preview(r);
        r.Call("vault_submit_pdf_job_apply", new JsonObject { ["preview_hash"] = hash, ["confirm"] = true });
        Assert.Equal("Autodesk.Vault.PDF.Create.idw", r.V.SubmittedType);
        Assert.Equal("500", r.V.SubmittedParams!["FileVersionId"]);
        Assert.Equal("False", r.V.SubmittedParams["UpdateViewOption"]);
        Assert.Equal("PDF Create: A-100.idw", r.V.SubmittedDesc);
        Assert.Equal(10, r.V.SubmittedPriority);
        Assert.Equal("no_matching_preview", r.Fails("vault_submit_pdf_job_apply", new JsonObject { ["preview_hash"] = hash, ["confirm"] = true }).Code);
        Assert.Equal(1, r.V.Submits);
    }

    [Fact]
    public void Apply_refuses_when_the_drawing_has_a_newer_version()
    {
        var r = new Rig();
        var hash = Preview(r);
        r.V.Target = r.V.Target with { FileVersionId = 501, Version = 5 };
        var e = r.Fails("vault_submit_pdf_job_apply", new JsonObject { ["preview_hash"] = hash, ["confirm"] = true });
        Assert.Equal("changed_since_preview", e.Code);
        Assert.Equal(0, r.V.Submits);
    }

    [Fact]
    public void Preview_expires_after_ten_minutes()
    {
        var now = DateTime.UtcNow;
        var store = new PreviewStore<PdfJobPlan>(clock: () => now);
        var v = new FakeExtras();
        var tools = ExtraTools.Build(v, store);
        var hash = tools.First(t => t.Name == "vault_submit_pdf_job_preview").Handler(new JsonObject { ["file"] = "A-100.idw" })!["preview_hash"]!.GetValue<string>();
        now = now.AddMinutes(11);
        var e = Assert.Throws<BridgeException>(() => tools.First(t => t.Name == "vault_submit_pdf_job_apply").Handler(new JsonObject { ["preview_hash"] = hash, ["confirm"] = true }));
        Assert.Equal("no_matching_preview", e.Code);
        Assert.Equal(0, v.Submits);
    }

    [Fact]
    public void Different_files_get_different_hashes()
    {
        var r = new Rig();
        var a = Preview(r);
        r.V.Target = r.V.Target with { FileVersionId = 600, Name = "B-200.idw" };
        var b = Preview(r);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Apply_runs_through_the_server_as_a_job_like_other_writes()
    {
        var jobs = new JobManager(w => w(), TimeSpan.FromSeconds(5));
        var v = new FakeExtras();
        var tools = McpCatalog.Build(new NoVault(), new NoInventor(), new NoSession(), new NoImporter(), "1", null, null, new PathGuard(new[] { Path.GetTempPath() }), Path.GetTempPath(), jobs, null, v);
        var server = new McpServer("0123456789abcdef0123456789abcdef", "1", tools, w => w(), jobs: jobs);
        JsonObject Call(string name, JsonObject args)
        {
            var msg = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = name, ["arguments"] = args } };
            var resp = server.Handle(new McpHttpRequest("POST", "0123456789abcdef0123456789abcdef", null, null, null, msg.ToJsonString()));
            var text = JsonNode.Parse(resp.Body!)!["result"]!["content"]![0]!["text"]!.GetValue<string>();
            return (JsonObject)JsonNode.Parse(text)!;
        }
        var hash = Call("vault_submit_pdf_job_preview", new JsonObject { ["file"] = "A-100.idw" })["preview_hash"]!.GetValue<string>();
        var done = Call("vault_submit_pdf_job_apply", new JsonObject { ["preview_hash"] = hash, ["confirm"] = true });
        Assert.Equal(9001, done["job_id"]!.GetValue<long>());
        Assert.Equal(1, v.Submits);
        var list = JsonNode.Parse(server.Handle(new McpHttpRequest("POST", "0123456789abcdef0123456789abcdef", null, null, null,
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}")).Body!)!["result"]!["tools"]!.AsArray();
        var pdf = list.First(t => t!["name"]!.GetValue<string>() == "vault_submit_pdf_job_apply")!;
        Assert.False(pdf["annotations"]!["readOnlyHint"]!.GetValue<bool>());
        Assert.True(list.First(t => t!["name"]!.GetValue<string>() == "vault_users")!["annotations"]!["readOnlyHint"]!.GetValue<bool>());
    }

    // ---- definitions and users ----

    [Fact]
    public void Property_definitions_filter_sort_and_report_type()
    {
        var r = new Rig();
        var all = r.Call("vault_property_definitions");
        Assert.Equal("FILE", all["entity"]!.GetValue<string>());
        Assert.Equal(Defs.Length, all["count"]!.GetValue<int>());
        var f = r.Call("vault_property_definitions", new JsonObject { ["filter"] = "number" });
        Assert.Equal(1, f["count"]!.GetValue<int>());
        Assert.Equal("PartNumber", f["definitions"]![0]!["system_name"]!.GetValue<string>());
        Assert.Equal("String", f["definitions"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("bad_request", r.Fails("vault_property_definitions", new JsonObject { ["entity"] = "WIDGET" }).Code);
        Assert.Equal("FLDR", r.Call("vault_property_definitions", new JsonObject { ["entity"] = "fldr" })["entity"]!.GetValue<string>());
    }

    [Fact]
    public void Categories_are_sorted_with_what_they_apply_to()
    {
        var o = new Rig().Call("vault_categories");
        Assert.Equal("Base", o["categories"]![0]!["name"]!.GetValue<string>());
        Assert.Equal(2, o["categories"]![0]!["applies_to"]!.AsArray().Count);
    }

    [Fact]
    public void Lifecycles_name_states_flags_and_transitions()
    {
        var o = new Rig().Call("vault_lifecycles");
        var lc = o["lifecycles"]![0]!;
        var states = lc["states"]!.AsArray();
        Assert.True(states[1]!["released"]!.GetValue<bool>());
        Assert.True(states[2]!["obsolete"]!.GetValue<bool>());
        Assert.True(states[0]!["default"]!.GetValue<bool>());
        var trans = lc["transitions"]!.AsArray();
        Assert.Equal(2, trans.Count); // the transition to unknown state 99 is dropped
        Assert.Equal("Work in Progress", trans[0]!["from"]!.GetValue<string>());
        Assert.Equal("Released", trans[0]!["to"]!.GetValue<string>());
    }

    [Fact]
    public void Revision_schemes_show_first_labels_and_count()
    {
        var seq = new Rig().Call("vault_revision_schemes")["revision_schemes"]![0]!["sequences"]![0]!;
        Assert.Equal(26, seq["label_count"]!.GetValue<int>());
        Assert.Equal(12, seq["first_labels"]!.AsArray().Count);
        Assert.Equal("primary", seq["level"]!.GetValue<string>());
    }

    [Fact]
    public void Users_default_to_active_only_sorted()
    {
        var r = new Rig();
        var o = r.Call("vault_users");
        Assert.Equal(new[] { "Amy", "Zed" }, o["users"]!.AsArray().Select(u => u!["name"]!.GetValue<string>()).ToArray());
        Assert.Equal(3, r.Call("vault_users", new JsonObject { ["active_only"] = false })["count"]!.GetValue<int>());
        Assert.Equal("bad_request", r.Fails("vault_users", new JsonObject { ["active_only"] = "no" }).Code);
    }

    [Fact]
    public void Groups_list_members_and_child_groups()
    {
        var g = new Rig().Call("vault_groups")["groups"]![0]!;
        Assert.Equal(2, g["member_count"]!.GetValue<int>());
        Assert.Equal("Amy", g["users"]![0]!.GetValue<string>());
        Assert.Equal("Interns", g["groups"]![0]!.GetValue<string>());
    }
}
