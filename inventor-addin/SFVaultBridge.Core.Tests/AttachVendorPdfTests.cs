using System.Text.Json.Nodes;
using SFVaultBridge.Core;

namespace SFVaultBridge.Core.Tests;

// attach_vendor_pdf: naming, skip rules, association building, the run (dry and apply, released parts) and the MCP preview/apply pair.
public class AttachVendorPdfTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sfvb-attach-" + Guid.NewGuid().ToString("N"));

    public AttachVendorPdfTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string WriteFile(string name, string content)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, content, System.Text.Encoding.Latin1);
        return p;
    }

    private sealed class FakeHost : IAttachPdfHost
    {
        public Dictionary<string, DataCardFileInfo> Files = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, AttachFacts> Facts = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, AttachFileRef> InFolder = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Calls = new();
        public RevisePlan Plan = new(null, "Library -> Library Edit", "Library Edit -> Library", "Library Edit", "Library");
        public string? FailAttach;
        public string? FailEnsure;
        public string? FailChangeTo;
        public long NextPdfId = 900;

        public IReadOnlyList<DataCardFileInfo> Find(DataCardFileRef file) =>
            Files.Values.Where(f => string.Equals(f.Name, file.Text, StringComparison.OrdinalIgnoreCase) || string.Equals(f.VaultPath, file.Text, StringComparison.OrdinalIgnoreCase)).ToList();

        public AttachFacts ReadFacts(DataCardFileInfo file) { Calls.Add("facts " + file.Name); return Facts[file.Name]; }
        public AttachFileRef? FindInFolder(string folder, string name) => InFolder.TryGetValue(folder + "/" + name, out var r) ? r : null;

        public PdfInVault EnsurePdf(string folder, string pdfName, string? localPath, AttachFileRef? existing, string comment)
        {
            Calls.Add($"ensure {pdfName} local={(localPath is null ? "none" : Path.GetFileName(localPath))} existing={(existing is null ? "none" : existing.Id.ToString())}");
            if (FailEnsure is not null) throw new DataCardStepException("pdf", FailEnsure);
            return existing is not null ? new PdfInVault(existing.Id, false) : new PdfInVault(NextPdfId, true);
        }

        public DataCardApplied AttachToPart(DataCardFileInfo file, long pdfFileId, string comment)
        {
            Calls.Add($"attach {file.Name} pdf={pdfFileId} state={file.State}");
            if (FailAttach is not null) throw new DataCardStepException("checkin", FailAttach);
            return new DataCardApplied(file.Version + 1);
        }

        public RevisePlan PlanRevise(DataCardFileInfo f) { Calls.Add("plan " + f.Name); return Plan; }

        public DataCardStateChange ChangeState(DataCardFileInfo f, string toStateName, string comment)
        {
            Calls.Add($"state {f.Name} -> {toStateName}");
            if (FailChangeTo == toStateName) throw new DataCardStepException("revise", "Vault refused");
            Files[f.Name] = f with { State = toStateName, Released = toStateName == "Library" };
            return new DataCardStateChange(toStateName, "A");
        }

        public bool? LinkedToItem(DataCardFileInfo f) => null;
        public string? ReadPropertyCompliance(DataCardFileInfo f) => null;
        public IReadOnlyDictionary<PropRef, string?> Read(DataCardFileInfo f, IReadOnlyList<PropRef> props) => throw new NotSupportedException();
        public DataCardApplied Apply(DataCardFileInfo f, IReadOnlyList<PropChange> changes, string comment) => throw new NotSupportedException();
    }

    private sealed class FakeFetcher : IVendorPdfFetcher
    {
        public string Dir = "";
        public string Content = "%PDF-1.4 fake";
        public bool Throws;
        public List<string> Fetched = new();
        public List<string> Discarded = new();

        public string Fetch(string vendorNumber, string pdfName)
        {
            Fetched.Add($"{vendorNumber}|{pdfName}");
            if (Throws) throw new InvalidOperationException("McMaster download failed: no PDF");
            var p = Path.Combine(Dir, pdfName);
            File.WriteAllText(p, Content, System.Text.Encoding.Latin1);
            return p;
        }

        public void Discard(string path) => Discarded.Add(Path.GetFileName(path));
    }

    private const string Folder = "$/DESIGNS/Simplifyber Library/McMaster-Carr/screws";

    private static DataCardFileInfo Part(string name, string state = "Work in Progress", bool released = false, string category = "Part - Purchased", string? path = null) =>
        new(path ?? $"{Folder}/{name}", name, category, state, released, false, "", 3);

    private static AttachFacts McFacts(string vn = "91290A115", params string[] attached) =>
        new("McMASTER-CARR", vn, attached, Folder);

    private (FakeHost Host, FakeFetcher Fetcher) Rig(string name = "CD-001897.ipt", DataCardFileInfo? info = null, AttachFacts? facts = null)
    {
        var h = new FakeHost();
        h.Files[name] = info ?? Part(name);
        h.Facts[name] = facts ?? McFacts();
        return (h, new FakeFetcher { Dir = _dir });
    }

    private static AttachPdfRequest Req(string[] files, string source = "mcmaster", bool dry = true, bool revise = false, string? comment = null) =>
        new(files, source, dry, revise, comment);

    private static JsonObject First(JsonObject o) => o["results"]![0]!.AsObject();

    // ---- naming ----

    [Theory]
    [InlineData("CD-001897", "McMASTER-CARR", "91290A115", "CD-001897 McMaster-Carr 91290A115.pdf")]
    [InlineData("CD-001885", "McMaster-Carr", " 91290A115 ", "CD-001885 McMaster-Carr 91290A115.pdf")]
    [InlineData("CD-001900", "McMaster Carr", "9001/K2", "CD-001900 McMaster-Carr 9001-K2.pdf")]
    [InlineData("CD-001162", "Misumi", "ABC-12", "CD-001162 Misumi ABC-12.pdf")]
    public void Pdf_name_follows_the_import_rule(string cd, string vendor, string vn, string expected) =>
        Assert.Equal(expected, AttachRules.PdfName(cd, vendor, vn));

    [Fact]
    public void Pdf_name_matches_what_Import_New_Part_would_call_it()
    {
        var plan = new ImportPlan("McMaster-Carr", "", "91290A115", "x", "Steel", "", "");
        Assert.Equal(ImportRules.PdfFileName("CD-001897", plan), AttachRules.PdfName("CD-001897", "McMASTER-CARR", "91290A115"));
    }

    [Theory]
    [InlineData("McMASTER-CARR", true)]
    [InlineData("mcmaster-carr", true)]
    [InlineData("McMaster Carr", true)]
    [InlineData("MISUMI", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void McMaster_vendor_is_recognised_by_letters_only(string? vendor, bool expected) =>
        Assert.Equal(expected, AttachRules.IsMcMasterVendor(vendor));

    // ---- skip rules ----

    [Fact]
    public void Already_attached_by_exact_name_ignores_case()
    {
        var why = AttachRules.AlreadyAttached(new[] { "cd-001897 mcmaster-carr 91290a115.pdf" }, "CD-001897 McMaster-Carr 91290A115.pdf", "91290A115");
        Assert.NotNull(why);
    }

    [Fact]
    public void Any_pdf_attachment_with_the_vendor_number_counts()
    {
        Assert.NotNull(AttachRules.AlreadyAttached(new[] { "drawing 91290A115 rev2.pdf" }, "CD-001897 McMaster-Carr 91290A115.pdf", "91290A115"));
        Assert.NotNull(AttachRules.AlreadyAttached(new[] { "91290a115.PDF" }, "CD-001897 McMaster-Carr 91290A115.pdf", "91290A115"));
    }

    [Fact]
    public void Other_attachments_do_not_count()
    {
        var name = "CD-001897 McMaster-Carr 91290A115.pdf";
        Assert.Null(AttachRules.AlreadyAttached(Array.Empty<string>(), name, "91290A115"));
        Assert.Null(AttachRules.AlreadyAttached(new[] { "91290A115.step" }, name, "91290A115"));          // not a PDF
        Assert.Null(AttachRules.AlreadyAttached(new[] { "other part.pdf" }, name, "91290A115"));
        Assert.Null(AttachRules.AlreadyAttached(new[] { "CD-001897 McMaster-Carr 91290A1150.pdf" }, name, "91290A115")); // longer number
    }

    [Fact]
    public void Skip_rules_in_order()
    {
        var name = "CD-001897 McMaster-Carr 91290A115.pdf";
        Assert.Contains("no Vendor Number", AttachRules.SkipReason(new AttachFacts("McMASTER-CARR", "  ", Array.Empty<string>(), Folder), "mcmaster", name));
        Assert.Contains("not a McMaster part", AttachRules.SkipReason(new AttachFacts("MISUMI", "ABC-123", Array.Empty<string>(), Folder), "mcmaster", name)!);
        Assert.Contains("blank", AttachRules.SkipReason(new AttachFacts("", "91290A115", Array.Empty<string>(), Folder), "mcmaster", name)!);
        Assert.Contains("does not look like a McMaster", AttachRules.SkipReason(new AttachFacts("McMASTER-CARR", "??", Array.Empty<string>(), Folder), "mcmaster", name)!);
        Assert.Contains("already attached", AttachRules.SkipReason(McFacts("91290A115", name), "mcmaster", name)!);
        Assert.Null(AttachRules.SkipReason(McFacts(), "mcmaster", name));
        // A local PDF may go to a part of another vendor.
        Assert.Null(AttachRules.SkipReason(new AttachFacts("MISUMI", "ABC-123", Array.Empty<string>(), Folder), @"C:\Users\z\Downloads\x.pdf", "CD-1 MISUMI ABC-123.pdf"));
    }

    // ---- association list ----

    [Fact]
    public void Association_list_is_existing_plus_the_attachment_and_drops_nothing()
    {
        var existing = new List<AssocRow>
        {
            new(10, 21, "Dependency", "ref-a", "src", "$/a.ipt"),
            new(10, 22, "Attachment", null, null, null),
            new(10, 23, "Dependency", "ref-b", null, null),
        };
        var list = AttachRules.AttachAssociations(existing, 10, 900);
        Assert.Equal(4, list.Count);
        for (var i = 0; i < existing.Count; i++) Assert.Equal(existing[i], list[i]);
        Assert.Equal(new AssocRow(10, 900, "Attachment", null, null, null), list[3]);
    }

    [Fact]
    public void Association_list_with_no_existing_rows_is_just_the_attachment()
    {
        var list = AttachRules.AttachAssociations(Array.Empty<AssocRow>(), 10, 900);
        Assert.Single(list);
        Assert.Equal("Attachment", list[0].Type);
        Assert.Equal(900, list[0].CldFileId);
    }

    [Fact]
    public void Association_list_never_adds_the_same_attachment_twice()
    {
        var existing = new List<AssocRow> { new(10, 900, "Attachment", null, null, null) };
        Assert.Single(AttachRules.AttachAssociations(existing, 10, 900));
        // The same file as a dependency is a different thing and stays alongside.
        var dep = new List<AssocRow> { new(10, 900, "Dependency", "r", null, null) };
        Assert.Equal(2, AttachRules.AttachAssociations(dep, 10, 900).Count);
    }

    [Fact]
    public void Pdf_sniff_accepts_pdf_and_refuses_html()
    {
        Assert.True(AttachRules.LooksLikePdf(WriteFile("ok.pdf", "%PDF-1.7\n...")));
        Assert.True(AttachRules.LooksLikePdf(WriteFile("bom.pdf", "\u00EF\u00BB\u00BF  %PDF-1.4")));
        Assert.False(AttachRules.LooksLikePdf(WriteFile("bad.pdf", "<html>error</html>")));
        Assert.False(AttachRules.LooksLikePdf(Path.Combine(_dir, "missing.pdf")));
    }

    // ---- request parsing ----

    [Fact]
    public void Request_takes_file_or_files_and_defaults_to_a_mcmaster_dry_run()
    {
        var one = AttachVendorPdf.ParseRequest(new JsonObject { ["file"] = "CD-001897.ipt" });
        Assert.Equal(new[] { "CD-001897.ipt" }, one.Files);
        Assert.Equal("mcmaster", one.Source);
        Assert.True(one.DryRun);
        Assert.False(one.ReviseReleased);
        var many = AttachVendorPdf.ParseRequest(new JsonObject { ["files"] = new JsonArray("a.ipt", "A.ipt", "b.ipt"), ["source"] = "McMaster", ["dryRun"] = false, ["reviseReleased"] = true });
        Assert.Equal(new[] { "a.ipt", "b.ipt" }, many.Files);
        Assert.Equal("mcmaster", many.Source);
        Assert.False(many.DryRun);
        Assert.True(many.ReviseReleased);
        Assert.Throws<BridgeException>(() => AttachVendorPdf.ParseRequest(new JsonObject()));
    }

    [Fact]
    public void Local_source_needs_one_part_a_pdf_path_and_the_guard()
    {
        var pdf = WriteFile("a.pdf", "%PDF-1.4");
        var guard = new PathGuard(new[] { _dir });
        var ok = AttachVendorPdf.ParseRequest(new JsonObject { ["file"] = "a.ipt", ["source"] = pdf }, guard);
        Assert.Equal(Path.GetFullPath(pdf), ok.Source);
        Assert.Throws<BridgeException>(() => AttachVendorPdf.ParseRequest(new JsonObject { ["files"] = new JsonArray("a.ipt", "b.ipt"), ["source"] = pdf }, guard));
        Assert.Throws<BridgeException>(() => AttachVendorPdf.ParseRequest(new JsonObject { ["file"] = "a.ipt", ["source"] = Path.Combine(_dir, "a.step") }, guard));
        Assert.Equal("path_not_allowed", Assert.Throws<BridgeException>(() =>
            AttachVendorPdf.ParseRequest(new JsonObject { ["file"] = "a.ipt", ["source"] = @"C:\Windows\x.pdf" }, guard)).Code);
    }

    // ---- run: preview ----

    [Fact]
    public void Dry_run_shows_what_would_be_downloaded_added_and_attached_and_writes_nothing()
    {
        var (h, f) = Rig();
        var o = AttachVendorPdf.Run(Req(new[] { "CD-001897.ipt" }), h, f);
        var r = First(o);
        Assert.Equal("preview", r["status"]!.GetValue<string>());
        Assert.Equal("CD-001897 McMaster-Carr 91290A115.pdf", r["pdf_name"]!.GetValue<string>());
        Assert.Equal("download_add_attach", r["action"]!.GetValue<string>());
        Assert.Equal("Documents", r["category"]!.GetValue<string>());
        Assert.Equal(Folder, r["vault_folder"]!.GetValue<string>());
        Assert.Equal(1, o["counts"]!["preview"]!.GetValue<int>());
        Assert.Empty(f.Fetched);
        Assert.DoesNotContain(h.Calls, c => c.StartsWith("ensure") || c.StartsWith("attach") || c.StartsWith("state"));
    }

    [Fact]
    public void Part_that_already_has_the_pdf_is_skipped_with_a_reason()
    {
        var (h, f) = Rig(facts: McFacts("91290A115", "CD-001897 McMaster-Carr 91290A115.pdf"));
        var r = First(AttachVendorPdf.Run(Req(new[] { "CD-001897.ipt" }, dry: false), h, f));
        Assert.Equal("skipped", r["status"]!.GetValue<string>());
        Assert.Contains("already attached", r["reason"]!.GetValue<string>());
        Assert.Empty(f.Fetched);
        Assert.DoesNotContain(h.Calls, c => c.StartsWith("ensure") || c.StartsWith("attach"));
    }

    [Fact]
    public void Part_with_another_pdf_carrying_the_vendor_number_is_skipped()
    {
        var (h, f) = Rig(facts: McFacts("91290A115", "McMaster 91290A115 drawing.pdf"));
        var r = First(AttachVendorPdf.Run(Req(new[] { "CD-001897.ipt" }, dry: false), h, f));
        Assert.Equal("skipped", r["status"]!.GetValue<string>());
        Assert.Empty(f.Fetched);
    }

    [Fact]
    public void File_already_in_the_folder_but_not_attached_is_attached_not_added_again()
    {
        var (h, f) = Rig();
        h.InFolder[$"{Folder}/CD-001897 McMaster-Carr 91290A115.pdf"] = new AttachFileRef(555, 5, "CD-001897 McMaster-Carr 91290A115.pdf");
        var preview = First(AttachVendorPdf.Run(Req(new[] { "CD-001897.ipt" }), h, f));
        Assert.Equal("attach_existing", preview["action"]!.GetValue<string>());
        var r = First(AttachVendorPdf.Run(Req(new[] { "CD-001897.ipt" }, dry: false), h, f));
        Assert.Equal("fixed", r["status"]!.GetValue<string>());
        Assert.False(r["pdf_added"]!.GetValue<bool>());
        Assert.Empty(f.Fetched);                                  // nothing downloaded for an existing file
        Assert.Contains("ensure CD-001897 McMaster-Carr 91290A115.pdf local=none existing=555", h.Calls);
        Assert.Contains("attach CD-001897.ipt pdf=555 state=Work in Progress", h.Calls);
    }

    [Fact]
    public void Content_Center_parts_are_never_touched()
    {
        var (h, f) = Rig(info: Part("CD-001897.ipt", category: "Part - Content Center"));
        var r = First(AttachVendorPdf.Run(Req(new[] { "CD-001897.ipt" }, dry: false), h, f));
        Assert.Equal("skipped", r["status"]!.GetValue<string>());
        Assert.Contains("Content Center", r["reason"]!.GetValue<string>());
        Assert.DoesNotContain(h.Calls, c => c.StartsWith("facts"));

        var (h2, f2) = Rig(info: Part("CD-001897.ipt", path: "$/Content Center Files/Screws/CD-001897.ipt"));
        var r2 = First(AttachVendorPdf.Run(Req(new[] { "CD-001897.ipt" }, dry: false, revise: true), h2, f2));
        Assert.Equal("skipped", r2["status"]!.GetValue<string>());
        Assert.Empty(f2.Fetched);
    }

    [Fact]
    public void Non_McMaster_part_is_skipped_for_a_mcmaster_source()
    {
        var (h, f) = Rig(facts: new AttachFacts("MISUMI", "ABC-123", Array.Empty<string>(), Folder));
        var r = First(AttachVendorPdf.Run(Req(new[] { "CD-001897.ipt" }, dry: false), h, f));
        Assert.Equal("skipped", r["status"]!.GetValue<string>());
        Assert.Contains("not a McMaster part", r["reason"]!.GetValue<string>());
        Assert.Empty(f.Fetched);
    }

    [Fact]
    public void Missing_and_ambiguous_parts_are_reported()
    {
        var (h, f) = Rig();
        Assert.Equal("skipped", First(AttachVendorPdf.Run(Req(new[] { "CD-009999.ipt" }), h, f))["status"]!.GetValue<string>());
        h.Files["x"] = Part("CD-001897.ipt", path: "$/other/CD-001897.ipt");
        Assert.Equal("error", First(AttachVendorPdf.Run(Req(new[] { "CD-001897.ipt" }), h, f))["status"]!.GetValue<string>());
    }

    // ---- run: apply ----

    [Fact]
    public void Apply_downloads_adds_and_attaches_then_discards_the_download()
    {
        var (h, f) = Rig();
        var r = First(AttachVendorPdf.Run(Req(new[] { "CD-001897.ipt" }, dry: false), h, f));
        Assert.Equal("fixed", r["status"]!.GetValue<string>());
        Assert.True(r["pdf_added"]!.GetValue<bool>());
        Assert.Equal(4, r["version"]!.GetValue<long>());
        Assert.Equal(new[] { "91290A115|CD-001897 McMaster-Carr 91290A115.pdf" }, f.Fetched);
        Assert.Equal(new[] { "CD-001897 McMaster-Carr 91290A115.pdf" }, f.Discarded);
        var calls = h.Calls.Where(c => c.StartsWith("ensure") || c.StartsWith("attach")).ToList();
        Assert.Equal(2, calls.Count);
        Assert.StartsWith("ensure CD-001897 McMaster-Carr 91290A115.pdf local=CD-001897 McMaster-Carr 91290A115.pdf existing=none", calls[0]);
        Assert.Equal("attach CD-001897.ipt pdf=900 state=Work in Progress", calls[1]);
    }

    [Fact]
    public void Apply_checks_the_download_is_a_pdf_before_anything_reaches_Vault()
    {
        var (h, f) = Rig();
        f.Content = "<html>Sign in</html>";
        var r = First(AttachVendorPdf.Run(Req(new[] { "CD-001897.ipt" }, dry: false), h, f));
        Assert.Equal("error", r["status"]!.GetValue<string>());
        Assert.Equal("download", r["step"]!.GetValue<string>());
        Assert.DoesNotContain(h.Calls, c => c.StartsWith("ensure") || c.StartsWith("attach"));
        Assert.Empty(f.Discarded);
    }

    [Fact]
    public void Failed_download_is_a_download_error_and_nothing_is_written()
    {
        var (h, f) = Rig();
        f.Throws = true;
        var r = First(AttachVendorPdf.Run(Req(new[] { "CD-001897.ipt" }, dry: false), h, f));
        Assert.Equal("error", r["status"]!.GetValue<string>());
        Assert.Equal("download", r["step"]!.GetValue<string>());
        Assert.Contains("McMaster download failed", r["message"]!.GetValue<string>());
        Assert.DoesNotContain(h.Calls, c => c.StartsWith("ensure") || c.StartsWith("attach") || c.StartsWith("state"));
    }

    [Fact]
    public void Attach_failure_after_the_pdf_was_added_says_so_and_keeps_the_download()
    {
        var (h, f) = Rig();
        h.FailAttach = "boom (the checkout was undone)";
        var r = First(AttachVendorPdf.Run(Req(new[] { "CD-001897.ipt" }, dry: false), h, f));
        Assert.Equal("error", r["status"]!.GetValue<string>());
        Assert.Equal("checkin", r["step"]!.GetValue<string>());
        Assert.Contains("was added to Vault", r["message"]!.GetValue<string>());
        Assert.Empty(f.Discarded);
    }

    [Fact]
    public void Failed_pdf_add_stops_before_the_part_is_touched()
    {
        var (h, f) = Rig(info: Part("CD-001897.ipt", "Library", released: true));
        h.FailEnsure = "Vault refused the add";
        var r = First(AttachVendorPdf.Run(Req(new[] { "CD-001897.ipt" }, dry: false, revise: true), h, f));
        Assert.Equal("error", r["status"]!.GetValue<string>());
        Assert.Equal("pdf", r["step"]!.GetValue<string>());
        Assert.DoesNotContain(h.Calls, c => c.StartsWith("state") || c.StartsWith("attach"));
        Assert.Empty(f.Discarded);
    }

    [Fact]
    public void Released_part_without_revise_is_skipped_and_nothing_is_downloaded()
    {
        var (h, f) = Rig(info: Part("CD-001897.ipt", "Library", released: true));
        var r = First(AttachVendorPdf.Run(Req(new[] { "CD-001897.ipt" }, dry: false), h, f));
        Assert.Equal("skipped", r["status"]!.GetValue<string>());
        Assert.Contains("released", r["reason"]!.GetValue<string>());
        Assert.Empty(f.Fetched);
    }

    [Fact]
    public void Released_part_with_revise_goes_Library_to_Library_Edit_attaches_and_returns()
    {
        var (h, f) = Rig(info: Part("CD-001897.ipt", "Library", released: true));
        var preview = First(AttachVendorPdf.Run(Req(new[] { "CD-001897.ipt" }, revise: true), h, f));
        Assert.True(preview["would_revise"]!.GetValue<bool>());
        Assert.Equal("Library -> Library Edit", preview["transitions"]![0]!.GetValue<string>());
        Assert.DoesNotContain(h.Calls, c => c.StartsWith("state"));

        var r = First(AttachVendorPdf.Run(Req(new[] { "CD-001897.ipt" }, dry: false, revise: true), h, f));
        Assert.Equal("fixed", r["status"]!.GetValue<string>());
        Assert.Equal("Library", r["state_after"]!.GetValue<string>());
        var steps = h.Calls.Where(c => c.StartsWith("ensure") || c.StartsWith("state") || c.StartsWith("attach")).ToList();
        Assert.StartsWith("ensure", steps[0]);                      // the PDF goes in before the part is revised
        Assert.Equal("state CD-001897.ipt -> Library Edit", steps[1]);
        Assert.Equal("attach CD-001897.ipt pdf=900 state=Library Edit", steps[2]);   // attached while in the edit state
        Assert.Equal("state CD-001897.ipt -> Library", steps[3]);
        Assert.Equal("Library", h.Files["CD-001897.ipt"].State);
        Assert.Equal(new[] { "CD-001897 McMaster-Carr 91290A115.pdf" }, f.Discarded);
    }

    [Fact]
    public void Released_part_is_returned_to_its_state_when_the_attach_fails()
    {
        var (h, f) = Rig(info: Part("CD-001897.ipt", "Library", released: true));
        h.FailAttach = "checkin refused (the checkout was undone)";
        var r = First(AttachVendorPdf.Run(Req(new[] { "CD-001897.ipt" }, dry: false, revise: true), h, f));
        Assert.Equal("error", r["status"]!.GetValue<string>());
        Assert.True(r["re_released"]!.GetValue<bool>());
        Assert.Equal("Library", h.Files["CD-001897.ipt"].State);
        Assert.Equal("state CD-001897.ipt -> Library", h.Calls.Last());
    }

    [Fact]
    public void Failed_return_to_the_released_state_stops_later_released_parts()
    {
        var h = new FakeHost { FailChangeTo = "Library" };
        var f = new FakeFetcher { Dir = _dir };
        foreach (var n in new[] { "CD-001897.ipt", "CD-001885.ipt" })
        {
            h.Files[n] = Part(n, "Library", released: true);
            h.Facts[n] = McFacts(n == "CD-001897.ipt" ? "91290A115" : "91290A116");
        }
        var o = AttachVendorPdf.Run(Req(new[] { "CD-001897.ipt", "CD-001885.ipt" }, dry: false, revise: true), h, f);
        Assert.Equal("re_release", o["results"]![0]!["step"]!.GetValue<string>());
        Assert.Equal("skipped", o["results"]![1]!["status"]!.GetValue<string>());
        Assert.Equal(DataCardFix.ReviseStoppedReason, o["results"]![1]!["reason"]!.GetValue<string>());
    }

    [Fact]
    public void Local_pdf_source_adds_that_file_under_the_import_name()
    {
        var (h, f) = Rig();
        var pdf = WriteFile("whatever.pdf", "%PDF-1.5 x");
        var r = First(AttachVendorPdf.Run(Req(new[] { "CD-001897.ipt" }, pdf, dry: false), h, f));
        Assert.Equal("fixed", r["status"]!.GetValue<string>());
        Assert.Empty(f.Fetched);
        Assert.Empty(f.Discarded);                                   // the user's own file is never deleted
        Assert.Contains(h.Calls, c => c == "ensure CD-001897 McMaster-Carr 91290A115.pdf local=whatever.pdf existing=none");
        Assert.True(File.Exists(pdf));
    }

    [Fact]
    public void Local_pdf_source_that_is_not_a_pdf_or_is_missing_is_skipped()
    {
        var (h, f) = Rig();
        var bad = WriteFile("bad.pdf", "<html>");
        Assert.Contains("not a PDF", First(AttachVendorPdf.Run(Req(new[] { "CD-001897.ipt" }, bad), h, f))["reason"]!.GetValue<string>());
        Assert.Contains("not found", First(AttachVendorPdf.Run(Req(new[] { "CD-001897.ipt" }, Path.Combine(_dir, "none.pdf")), h, f))["reason"]!.GetValue<string>());
    }

    [Fact]
    public void Not_signed_in_marks_the_rest_as_errors()
    {
        var h = new NotSignedInHost();
        var o = AttachVendorPdf.Run(Req(new[] { "a.ipt", "b.ipt" }), h, null);
        Assert.Equal(2, o["counts"]!["error"]!.GetValue<int>());
        Assert.Equal("sign_in", o["results"]![0]!["step"]!.GetValue<string>());
    }

    private sealed class NotSignedInHost : IAttachPdfHost
    {
        public IReadOnlyList<DataCardFileInfo> Find(DataCardFileRef file) => throw new BridgeException("not_signed_in", "Sign in");
        public AttachFacts ReadFacts(DataCardFileInfo file) => throw new NotSupportedException();
        public AttachFileRef? FindInFolder(string folder, string name) => null;
        public PdfInVault EnsurePdf(string folder, string pdfName, string? localPath, AttachFileRef? existing, string comment) => throw new NotSupportedException();
        public DataCardApplied AttachToPart(DataCardFileInfo file, long pdfFileId, string comment) => throw new NotSupportedException();
        public IReadOnlyDictionary<PropRef, string?> Read(DataCardFileInfo file, IReadOnlyList<PropRef> props) => throw new NotSupportedException();
        public DataCardApplied Apply(DataCardFileInfo file, IReadOnlyList<PropChange> changes, string comment) => throw new NotSupportedException();
        public RevisePlan PlanRevise(DataCardFileInfo file) => throw new NotSupportedException();
        public DataCardStateChange ChangeState(DataCardFileInfo file, string toStateName, string comment) => throw new NotSupportedException();
        public bool? LinkedToItem(DataCardFileInfo file) => null;
        public string? ReadPropertyCompliance(DataCardFileInfo file) => null;
    }

    // ---- MCP preview and apply ----

    private IReadOnlyList<McpTool> Tools(FakeHost h, FakeFetcher f, PreviewStore<AttachPdfRequest>? store = null) =>
        AttachPdfTools.Build(h, f, new PathGuard(new[] { _dir }), store ?? new PreviewStore<AttachPdfRequest>(), null);

    [Fact]
    public void Tool_pair_is_a_preview_and_a_write_with_the_expected_schema()
    {
        var (h, f) = Rig();
        var tools = Tools(h, f);
        Assert.Equal(new[] { "attach_vendor_pdf_preview", "attach_vendor_pdf_apply" }, tools.Select(t => t.Name));
        Assert.Equal(McpToolKind.Preview, tools[0].Kind);
        Assert.Equal(McpToolKind.Write, tools[1].Kind);
        Assert.Equal("file", tools[0].InputSchema["required"]![0]!.GetValue<string>());
        Assert.Equal("preview_hash", tools[1].InputSchema["required"]![0]!.GetValue<string>());
        foreach (var t in tools) Assert.DoesNotContain("\u2014", t.Description);
        Assert.Contains("job_status", tools[1].Description);
    }

    [Fact]
    public void Apply_needs_the_matching_preview_hash_and_confirm_and_works_once()
    {
        var (h, f) = Rig();
        var tools = Tools(h, f);
        var args = new JsonObject { ["file"] = "CD-001897.ipt" };
        var preview = (JsonObject)tools[0].Handler(args)!;
        var hash = preview["preview_hash"]!.GetValue<string>();
        Assert.NotEmpty(hash);
        Assert.Empty(f.Fetched);                                       // a preview downloads nothing

        Assert.Equal("confirmation_required", Assert.Throws<BridgeException>(() => tools[1].Handler(new JsonObject { ["preview_hash"] = hash })).Code);
        Assert.Equal("no_matching_preview", Assert.Throws<BridgeException>(() => tools[1].Handler(new JsonObject { ["preview_hash"] = "nope", ["confirm"] = true })).Code);

        var applied = (JsonObject)tools[1].Handler(new JsonObject { ["preview_hash"] = hash, ["confirm"] = true })!;
        Assert.Equal("fixed", First(applied)["status"]!.GetValue<string>());
        Assert.Single(f.Fetched);
        Assert.Equal("no_matching_preview", Assert.Throws<BridgeException>(() => tools[1].Handler(new JsonObject { ["preview_hash"] = hash, ["confirm"] = true })).Code);
    }

    [Fact]
    public void Same_request_gives_the_same_hash_and_a_different_one_gives_another()
    {
        var (h, f) = Rig();
        var tools = Tools(h, f);
        var a = ((JsonObject)tools[0].Handler(new JsonObject { ["file"] = "CD-001897.ipt" })!)["preview_hash"]!.GetValue<string>();
        var b = ((JsonObject)tools[0].Handler(new JsonObject { ["file"] = "CD-001897.ipt" })!)["preview_hash"]!.GetValue<string>();
        var c = ((JsonObject)tools[0].Handler(new JsonObject { ["file"] = "CD-001897.ipt", ["comment"] = "x" })!)["preview_hash"]!.GetValue<string>();
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Preview_with_nothing_to_do_gives_no_hash()
    {
        var (h, f) = Rig(facts: McFacts("91290A115", "CD-001897 McMaster-Carr 91290A115.pdf"));
        var preview = (JsonObject)Tools(h, f)[0].Handler(new JsonObject { ["file"] = "CD-001897.ipt" })!;
        Assert.Null(preview["preview_hash"]);
        Assert.Equal(1, preview["counts"]!["skipped"]!.GetValue<int>());
    }

    [Fact]
    public void Tools_are_only_in_the_catalog_when_a_host_is_given()
    {
        var jobs = new JobManager(w => w());
        var without = McpCatalog.Build(new NoVault(), new NoInventor(), new NoSession(), new NoImporter(), "1", null, null, new PathGuard(new[] { _dir }), _dir, jobs);
        Assert.DoesNotContain(without, t => t.Name.StartsWith("attach_vendor_pdf"));
        var (h, f) = Rig();
        var with = McpCatalog.Build(new NoVault(), new NoInventor(), new NoSession(), new NoImporter(), "1", null, null, new PathGuard(new[] { _dir }), _dir, jobs, null, null, h, f);
        Assert.Contains(with, t => t.Name == "attach_vendor_pdf_preview");
        Assert.Contains(with, t => t.Name == "attach_vendor_pdf_apply");
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
        public string ImportStepAsPart(string stepPath, string outIpt, IReadOnlyDictionary<string, string> design, IReadOnlyDictionary<string, string> custom) => throw new NotSupportedException();
    }
}
