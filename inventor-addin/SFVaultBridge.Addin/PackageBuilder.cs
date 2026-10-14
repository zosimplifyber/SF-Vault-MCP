using System.Windows.Forms;
using SFVaultBridge.Core;
using Inv = Inventor;
using WsFile = Autodesk.Connectivity.WebServices.File;

namespace SFVaultBridge.Addin;

/// <summary>
/// Document is the open assembly or, for a single part, the part (Stem, Folder, VaultFolder, AssemblyFile and Top then describe that part).
/// Pick is set for the Selected and Part scopes: the Make rows in scope and the picks that were skipped.
/// </summary>
internal sealed record PackageContext(Inv.Document Document, string Folder, string VaultFolder, string Stem,
                                      WsFile? AssemblyFile, BomNode Top, IReadOnlyList<BomNode> Roots,
                                      IReadOnlyDictionary<string, string?> Categories, string Source,
                                      BridgeSettings Settings, PrintMaterials Materials,
                                      PackageScopeKind Scope = PackageScopeKind.Assembly, string ScopeLine = "", ScopePick? Pick = null);

internal sealed record PackageOutcome(string ZipName, string? VaultPath, int? Version, string? DownloadsCopy,
                                      string Summary, IReadOnlyList<string> Warnings,
                                      string? DropboxPath = null, string? ShareUrl = null, string? Visibility = null, bool LinkCopied = false);

/// <summary>Builds the Partner Package and 3D Print Package zips: export loop, manifest, SFVaultTools.exe, Downloads copy, Vault check-in.</summary>
internal sealed class PackageBuilder
{
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromMinutes(10);
    private readonly Inv.Application _app;
    private readonly Action<string> _log;

    public PackageBuilder(Inv.Application app, Action<string> log)
    {
        _app = app; _log = log;
    }

    public PackageContext Prepare(Inv.AssemblyDocument asm)
    {
        var previous = StatusText();
        try
        {
            SetStatus("Partner Package: reading BOM");
            var (folder, vaultFolder, stem) = RibbonActions.Locate((Inv.Document)asm);
            var settings = BridgeSettings.Load(BridgeSettings.DefaultPath, _log);
            var categories = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var roots = BomEnricher.Apply(BomReader.Read(asm), VaultFiles.WorkingFolderRoot(), _log, categories);
            var asmFile = VaultFiles.FindLatest(vaultFolder + "/" + System.IO.Path.GetFileName(asm.FullFileName));
            var top = BomReader.TopNode(asm, VaultFiles.RevisionOf(asmFile), asmFile is null ? "" : VaultFiles.StateOf(asmFile));
            return new PackageContext((Inv.Document)asm, folder, vaultFolder, stem, asmFile, top, roots, categories, BomReader.SourceLine(asm),
                                      settings, new PrintMaterials(settings.PrintMaterials));
        }
        finally { SetStatus(previous); }
    }

    /// <summary>The open part: Vault location, its BOM row (Vault state, revision and category from the same enrichment as a BOM row) and the Part scope.</summary>
    public PackageContext PreparePart(Inv.PartDocument part)
    {
        var previous = StatusText();
        try
        {
            SetStatus("Partner Package: reading part");
            var (folder, vaultFolder, stem) = RibbonActions.Locate((Inv.Document)part);
            var settings = BridgeSettings.Load(BridgeSettings.DefaultPath, _log);
            var categories = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var roots = BomEnricher.Apply(new[] { BomReader.PartNode(part) }, VaultFiles.WorkingFolderRoot(), _log, categories);
            var file = VaultFiles.FindLatest(vaultFolder + "/" + System.IO.Path.GetFileName(part.FullFileName));
            var materials = new PrintMaterials(settings.PrintMaterials);
            var pick = PackageScope.PickPart(Candidates(roots, materials, categories));
            return new PackageContext((Inv.Document)part, folder, vaultFolder, stem, file, roots[0], roots, categories, "", settings, materials,
                                      PackageScopeKind.Part, PackageScope.ScopeLine(PackageScopeKind.Part, stem), pick);
        }
        finally { SetStatus(previous); }
    }

    /// <summary>The assembly narrowed to the selected components (their files; null for a component without one).</summary>
    public PackageContext PrepareSelected(Inv.AssemblyDocument asm, IEnumerable<string?> selectedFiles)
    {
        var ctx = Prepare(asm);
        var pick = PackageScope.PickSelected(Candidates(ctx.Roots, ctx.Materials, ctx.Categories), selectedFiles);
        return ctx with
        {
            Scope = PackageScopeKind.Selected,
            ScopeLine = PackageScope.ScopeLine(PackageScopeKind.Selected, ctx.Stem, pick.Candidates.Count, pick.Skipped.Count),
            Pick = pick,
        };
    }

    private static IReadOnlyList<ScopeCandidate> Candidates(IReadOnlyList<BomNode> roots, PrintMaterials materials, IReadOnlyDictionary<string, string?> categories) =>
        PackageRules.Candidates(roots, materials, n => categories.TryGetValue(n.FullPath, out var c) ? c : null);

    /// <summary>The McMaster-Carr parts in scope (every class, Purchased too): they are listed, never exported.</summary>
    private static IReadOnlyList<ScopeCandidate> McMasterCandidates(PackageContext ctx) =>
        ctx.Scope == PackageScopeKind.Assembly
            ? Candidates(ctx.Roots, ctx.Materials, ctx.Categories).Where(c => c.McMaster).ToList()
            : ctx.Pick?.McMasterParts ?? Array.Empty<ScopeCandidate>();

    /// <summary>The grid rows for a Selected or Part scope under the chosen Parts mode.</summary>
    public IReadOnlyList<ScopeRow> ScopeRows(PackageContext ctx, ManufacturingMode mode) =>
        PackageScope.Rows(ctx.Pick?.Candidates ?? Array.Empty<ScopeCandidate>(), mode, ctx.Scope == PackageScopeKind.Part);

    /// <summary>The parts to build for a Selected or Part scope, with the quantities entered in the form.</summary>
    public IReadOnlyList<PackagePart> ScopeParts(PackageContext ctx, ManufacturingMode mode, Func<ScopeRow, int?> quantity) =>
        PackageScope.ToParts(ScopeRows(ctx, mode), quantity);

    public IReadOnlyList<PackagePart> Parts(PackageContext ctx, ManufacturingMode mode) =>
        PackageRules.Select(ctx.Roots, mode, ctx.Materials, n => ctx.Categories.TryGetValue(n.FullPath, out var c) ? c : null,
                            mode == ManufacturingMode.AllMake ? ctx.Top : null);

    /// <summary>What the export loop and the release gate both need to know about one part's Vault files (looked up once).</summary>
    private sealed class Resolved
    {
        public WsFile? Model;
        public string? ModelLocal;
        public bool DrawingSearched;
        public WsFile? Drawing;
        public string? DrawingLocal;
    }

    /// <param name="quote">Partner packages only: skip the release gate, stamp every PDF FOR QUOTE and name the zip Quote.</param>
    public PackageOutcome Build(PackageContext ctx, ManufacturingMode mode, string partner, IReadOnlyList<PackagePart> parts, bool quote = false, bool upload = false)
    {
        var print = mode == ManufacturingMode.Print;
        if (print) quote = false;
        var prefix = print ? "3D Print Package" : "Partner Package";
        partner = (partner ?? "").Trim();
        if (!print)
        {
            var partnerError = PackageRules.PartnerError(partner);
            if (partnerError is not null) throw new BridgeException("bad_partner", partnerError);
        }
        var root = VaultFiles.WorkingFolderRoot();
        var resolved = new Dictionary<string, Resolved>(StringComparer.OrdinalIgnoreCase);

        Resolved Resolve(PackagePart part)
        {
            var node = part.Node;
            if (resolved.TryGetValue(node.FullPath, out var r)) return r;
            r = new Resolved { Model = VaultFiles.FindLatest(VaultPaths.ToVaultFolder(node.FullPath, root) + "/" + node.FileName) };
            if (r.Model is not null) r.ModelLocal = VaultFiles.LocalPathOf(r.Model);
            resolved[node.FullPath] = r;
            return r;
        }

        void FindDrawing(Resolved r)
        {
            if (r.DrawingSearched || r.Model is null) return;
            r.Drawing = DrawingFinder.PartDrawing(r.Model, _log);
            if (r.Drawing is not null) r.DrawingLocal = VaultFiles.LocalPathOf(r.Drawing);
            r.DrawingSearched = true;
        }

        if (!print && !quote) CheckReleased(parts, Resolve, FindDrawing);

        var previousStatus = StatusText();
        var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SFVaultBridge", Guid.NewGuid().ToString("N"));
        var exportDir = System.IO.Path.Combine(temp, "export");
        try
        {
            System.IO.Directory.CreateDirectory(exportDir);
            var files = new List<PackageFile>();
            var rows = new List<PackageRow>();
            var failed = new List<PackageFailure>();
            var missingDrawings = new List<string>();
            var noFlatPattern = new List<string>();
            var guessed = new List<string>();
            var usedDests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var printFormat = print && parts.Count > 0 ? ModelExporter.ResolvePrintFormat(_app, _log) : null;
            var counter = 0;
            var localCopies = new List<string>();
            var refreshFailed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // McMaster-Carr terms: McMaster CAD never leaves in a package. The parts are listed (BOM and cover), assembly STEPs are exported without them.
            var mcmaster = McMasterCandidates(ctx);
            var stepLeftOut = new List<string>();
            var mcNodes = PackageRules.McMasterNodes(ctx.Roots);
            var stepGuard = new ModelExporter.McMasterGuard(
                new HashSet<string>(mcNodes.Select(n => n.FullPath).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => System.IO.Path.GetFullPath(p)), StringComparer.OrdinalIgnoreCase),
                mcNodes.SelectMany(n => new[] { System.IO.Path.GetFileNameWithoutExtension(n.FileName ?? ""), n.PartNumber })
                       .Where(s => !string.IsNullOrWhiteSpace(s) && s.Trim().Length >= 4).ToList());

            // Make every closed, not checked-out model and drawing the latest Vault version, so the package matches what the gate checked.
            SetStatus($"{prefix}: getting latest from Vault");
            var refreshed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in parts)
            {
                var pn = part.Node.PartNumber;
                if (!refreshed.Add(part.Node.FullPath)) continue;
                try
                {
                    var r = Resolve(part);
                    if (r.Model is null) continue;
                    if (part.Files.HasFlag(PackageFiles.Pdf)) FindDrawing(r);
                    var toGet = new List<WsFile>();
                    void Consider(WsFile? f, string? local, string what)
                    {
                        if (f is null || local is null) return;
                        var open = DocumentOpener.FindOpen(_app, local) is not null;
                        var action = LocalCopy.Decide(open, f.CheckedOut);
                        if (action == LocalCopyAction.Refresh && HasLocalEdits(local, f)) localCopies.Add($"{pn} {what} (local edits, not overwritten)");
                        else if (action == LocalCopyAction.Refresh) toGet.Add(f);
                        else localCopies.Add($"{pn} {what}");
                    }
                    Consider(r.Model, r.ModelLocal, "model");
                    Consider(r.Drawing, r.DrawingLocal, "drawing");
                    if (toGet.Count > 0) VaultFiles.GetLatestOverwrite(toGet);
                }
                catch (Exception ex)
                {
                    _log($"package get latest for {pn} failed: {ex}");
                    failed.Add(new PackageFailure(pn, $"could not get the latest version from Vault: {ex.Message}"));
                    refreshFailed.Add(part.Node.FullPath);
                }
            }

            string Unique(string dest)
            {
                var slash = dest.LastIndexOf('/');
                var dir = slash < 0 ? "" : dest.Substring(0, slash + 1);
                var name = VaultPaths.UniqueName(dest.Substring(slash + 1), n => usedDests.Contains(dir + n));
                usedDests.Add(dir + name);
                return dir + name;
            }

            var index = 0;
            foreach (var part in parts)
            {
                index++;
                var node = part.Node;
                SetStatus($"{prefix}: exporting {index} of {parts.Count}: {node.PartNumber}");
                var partFiles = new List<string>();
                if (part.Class.Guessed) guessed.Add(PackageRules.GuessedEntry(part));
                if (refreshFailed.Contains(node.FullPath)) { AddRow(); continue; }

                Resolved r;
                try
                {
                    r = Resolve(part);
                    if (r.Model is null) { failed.Add(new PackageFailure(node.PartNumber, "not in Vault")); AddRow(); continue; }
                }
                catch (Exception ex)
                {
                    _log($"package lookup for {node.PartNumber} failed: {ex}");
                    failed.Add(new PackageFailure(node.PartNumber, $"Vault lookup failed: {ex.Message}"));
                    AddRow();
                    continue;
                }

                if (part.Files.HasFlag(PackageFiles.Step))
                    Step("STEP", () =>
                    {
                        var src = System.IO.Path.Combine(exportDir, $"{counter++:D3}.stp");
                        try { ModelExporter.Step(_app, r.ModelLocal!, src, _log, stepGuard); }
                        catch (ModelExporter.McMasterStepBlocked blocked)
                        {
                            _log($"STEP of {node.PartNumber} left out: {blocked.Message}");
                            stepLeftOut.Add($"{node.PartNumber}: {McMasterRule.AssemblyStepLeftOut} ({blocked.Message})");
                            return;
                        }
                        var dest = Unique(PackageRules.Dest(PackageFiles.Step, part));
                        files.Add(new PackageFile(src, dest, false)); partFiles.Add(dest);
                    });
                if (part.Files.HasFlag(PackageFiles.Dxf))
                    Step("DXF", () =>
                    {
                        var src = System.IO.Path.Combine(exportDir, $"{counter++:D3}.dxf");
                        if (!ModelExporter.FlatPatternDxf(_app, r.ModelLocal!, src, _log)) { noFlatPattern.Add(node.PartNumber); return; }
                        var dest = Unique(PackageRules.Dest(PackageFiles.Dxf, part));
                        files.Add(new PackageFile(src, dest, false)); partFiles.Add(dest);
                    });
                if (part.Files.HasFlag(PackageFiles.Pdf))
                    Step("drawing", () =>
                    {
                        FindDrawing(r);
                        var drawing = r.Drawing;
                        if (drawing is null) { missingDrawings.Add(node.PartNumber); return; }
                        var local = r.DrawingLocal!;
                        var open = DocumentOpener.FindOpen(_app, local) is not null;
                        var src = System.IO.Path.Combine(exportDir, $"{counter++:D3}.pdf");
                        DrawingExporter.Export(_app, local, src);
                        var stamp = PackageRules.StampDrawing(quote, VaultFiles.StateOf(drawing), drawing.CheckedOut,
                                                              open && OpenDirty(local), open && OpenOlder(local, drawing));
                        var dest = Unique(PackageRules.Dest(PackageFiles.Pdf, part));
                        files.Add(new PackageFile(src, dest, stamp)); partFiles.Add(dest);
                    });
                if (part.Files.HasFlag(PackageFiles.Print) && printFormat is not null)
                    Step("print", () =>
                    {
                        var dest = Unique(PackageRules.PrintDest(part, printFormat.Extension));
                        var src = System.IO.Path.Combine(exportDir, $"{counter++:D3}{printFormat.Extension}");
                        ModelExporter.Print(_app, printFormat, r.ModelLocal!, src, _log);
                        files.Add(new PackageFile(src, dest, false)); partFiles.Add(dest);
                    });
                AddRow();

                void Step(string label, Action work)
                {
                    try { work(); }
                    catch (Exception ex)
                    {
                        _log($"package {label} export of {node.PartNumber} failed: {ex}");
                        failed.Add(new PackageFailure(node.PartNumber, $"{(label == "drawing" ? "drawing" : label)} export failed: {ex.Message}"));
                    }
                }

                void AddRow() =>
                    rows.Add(new PackageRow(part.Item, node.PartNumber, node.Description, part.TotalQty, node.Material, node.MaterialFinish,
                                            PackageRules.ProcessText(part), node.Revision, node.State, partFiles));
            }

            var leftOutParts = mcmaster.Select(c => c.Node.PartNumber).ToList();
            var leftOut = leftOutParts.Concat(stepLeftOut).ToList();
            if (!print) rows.AddRange(PackageRules.McMasterRows(mcmaster));

            SetStatus($"{prefix}: building BOM and cover");
            var user = "";
            try { user = VaultSession.Connection.UserName ?? ""; } catch (Exception ex) { _log($"user name unavailable: {ex.Message}"); }
            var quoteLocal = quote && localCopies.Count > 0;
            var localText = "Local copy (open in Inventor, checked out or edited locally): " + string.Join(", ", localCopies);
            var source = quoteLocal ? (string.IsNullOrWhiteSpace(ctx.Source) ? localText : ctx.Source + " | " + localText) : ctx.Source;
            var manifest = System.IO.Path.Combine(temp, "manifest.json");
            System.IO.File.WriteAllText(manifest, PackageManifest.ToJson(new PackageManifestData(
                print ? "print" : "partner", ctx.Stem, ctx.Top.Description, VaultFiles.RevisionOf(ctx.AssemblyFile),
                ctx.AssemblyFile is null ? "" : VaultFiles.StateOf(ctx.AssemblyFile), partner, PackageRules.ModeName(mode), source,
                user, DateTime.Now, rows, files, guessed, missingDrawings, noFlatPattern, failed, quote,
                ctx.Scope == PackageScopeKind.Assembly ? "" : ctx.ScopeLine, leftOut)));

            var zipName = print ? PackageRules.PrintZipName(ctx.Stem) : PackageScope.ZipName(ctx.Scope, ctx.Stem, partner, mode, quote);
            var tempZip = System.IO.Path.Combine(temp, zipName);
            var resultPath = System.IO.Path.Combine(temp, "result.json");
            var json = ToolRunner.Run(ToolResults.PackageArgs(manifest, tempZip, resultPath), resultPath, ToolTimeout);
            var result = ToolResults.Parse(json, "zip", "files");
            if (!result.Ok)
            {
                if (result.Error == ToolResults.NoResultMessage)
                    throw new BridgeException("tool_failed", "SFVaultTools.exe is out of date or failed. Close Inventor and rerun Install-VaultBridge.ps1. Details are in bridge.log.");
                throw new BridgeException("tool_failed", result.Error ?? ToolResults.NoResultMessage);
            }
            if (!System.IO.File.Exists(tempZip)) tempZip = result.Output ?? tempZip;
            var counts = ToolResults.Counts(json);
            int C(string k) => counts.TryGetValue(k, out var v) ? v : 0;

            var warnings = new List<string>();
            var downloadsCopy = Downloads.Keep(tempZip, zipName, _log);

            var leftOutText = McMasterRule.LeftOutText(leftOutParts);
            if (leftOutText.Length > 0) warnings.Add(leftOutText);
            foreach (var s in stepLeftOut) warnings.Add("Left out: " + s);

            if (quoteLocal || (print && localCopies.Count > 0)) warnings.Add("Exported from the local copy (open in Inventor, checked out or edited locally): " + string.Join(", ", localCopies));

            string? dropboxPath = null, shareUrl = null, visibility = null;
            var linkCopied = false;
            if (upload && !print)
            {
                try
                {
                    var up = UploadStep(ctx, tempZip, partner);
                    if (up is not null)
                    {
                        warnings.AddRange(up.Warnings);
                        if (!string.IsNullOrEmpty(up.DropboxPath)) dropboxPath = up.DropboxPath;
                        shareUrl = up.ShareUrl; visibility = up.Visibility;
                        if (shareUrl is not null)
                        {
                            try { Clipboard.SetText(shareUrl); linkCopied = true; }
                            catch (Exception ex) { _log($"clipboard failed: {ex.Message}"); warnings.Add("Could not copy the Dropbox link to the clipboard."); }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log($"dropbox step failed: {ex}");
                    warnings.Add("Dropbox upload failed: " + ex.Message);
                }
            }

            SetStatus($"{prefix}: checking in");
            string? vaultPath = null;
            int? version = null;
            var comment = print ? $"3D print package for {ctx.Stem}"
                                : $"{partner} {PackageRules.ModeName(mode)} {(quote ? "quote " : "")}package for {(ctx.Scope == PackageScopeKind.Assembly ? ctx.Stem : ctx.ScopeLine)}";
            try
            {
                var pub = VaultFiles.Publish(tempZip, System.IO.Path.Combine(ctx.Folder, zipName), ctx.VaultFolder, zipName, comment);
                vaultPath = ctx.VaultFolder.TrimEnd('/') + "/" + zipName;
                version = pub.File is null ? null : (int)pub.File.VerNum;
                if (pub.Warning is not null) warnings.Add(pub.Warning);
                _log($"published {vaultPath} version {(version?.ToString() ?? "?")}");
            }
            catch (Exception ex)
            {
                _log($"package check-in failed: {ex}");
                if (downloadsCopy is null)
                {
                    if (ex is BridgeException be) throw new BridgeException("vault_failed", $"{zipName} was not kept anywhere. {be.Message}");
                    throw new BridgeException("vault_failed", $"{zipName} was not kept anywhere. Not checked in to Vault: {ex.Message}");
                }
                warnings.Add($"Not checked in to Vault: {ex.Message}");
            }

            if (!print) BridgeSettings.RememberPartner(BridgeSettings.DefaultPath, partner, _log);

            var summary = print
                ? $"{C("rows")} parts: {C("print")} print files ({(printFormat?.Extension ?? ".3mf").TrimStart('.').ToUpperInvariant()}). {C("guessed")} guessed, {C("failed")} could not export."
                : $"{C("rows")} parts: {C("step")} STEP, {C("pdf")} PDF, {C("dxf")} DXF. {C("guessed")} guessed, {C("missing")} missing drawings, {C("no_flat_pattern")} no flat pattern, {C("failed")} could not export.";
            return new PackageOutcome(zipName, vaultPath, version, downloadsCopy, summary, warnings, dropboxPath, shareUrl, visibility, linkCopied);
        }
        finally
        {
            try { System.IO.Directory.Delete(temp, true); } catch { }
            SetStatus(previousStatus);
        }
    }

    /// <summary>Stops before anything is exported when any included part or drawing is not released (see ReleaseGate).</summary>
    private void CheckReleased(IReadOnlyList<PackagePart> parts, Func<PackagePart, Resolved> resolve, Action<Resolved> findDrawing)
    {
        var previous = StatusText();
        try
        {
            var gate = new List<GatePart>();
            var index = 0;
            foreach (var part in parts)
            {
                index++;
                var pn = part.Node.PartNumber;
                SetStatus($"Partner Package: checking release state {index} of {parts.Count}: {pn}");
                try
                {
                    var r = resolve(part);
                    if (r.Model is null) { gate.Add(new GatePart(pn, false, "", false, null, false, false, false, "", false, null, false)); continue; }
                    var needDrawing = part.Files.HasFlag(PackageFiles.Pdf);
                    if (needDrawing) findDrawing(r);
                    var d = r.Drawing;
                    gate.Add(new GatePart(pn, true, VaultFiles.StateOf(r.Model), r.Model.CheckedOut, CheckedOutBy(r.Model), OpenDirty(r.ModelLocal),
                                          needDrawing, d is not null, d is null ? "" : VaultFiles.StateOf(d),
                                          d?.CheckedOut ?? false, d is null ? null : CheckedOutBy(d), d is not null && OpenDirty(r.DrawingLocal),
                                          OpenOlder(r.ModelLocal, r.Model), d is not null && OpenOlder(r.DrawingLocal, d),
                                          HasLocalEdits(r.ModelLocal, r.Model), d is not null && HasLocalEdits(r.DrawingLocal, d)));
                }
                catch (Exception ex)
                {
                    _log($"release check for {pn} failed: {ex}");
                    throw new BridgeException("vault_failed", $"Could not check the release state of {pn}: {ex.Message}");
                }
            }
            var blockers = ReleaseGate.Check(gate);
            if (blockers.Count > 0)
            {
                _log("release gate blocked: " + string.Join(" | ", blockers));
                var copied = false;
                if (blockers.Count > ReleaseGate.MaxListed)
                {
                    try { Clipboard.SetText(ReleaseGate.FullList(blockers)); copied = true; }
                    catch (Exception ex) { _log($"clipboard failed: {ex.Message}"); }
                }
                throw new BridgeException("not_released", ReleaseGate.Message(blockers, copied));
            }
        }
        finally { SetStatus(previous); }
    }

    private string? CheckedOutBy(WsFile f) => LocalState.CheckedOutBy(f, _log);

    private bool OpenOlder(string? local, WsFile latest) => LocalState.OpenOlder(_app, local, latest);

    private bool HasLocalEdits(string? local, WsFile latest) => LocalState.HasLocalEdits(_app, local, latest);

    private bool OpenDirty(string? local) => LocalState.OpenDirty(_app, local);

    private sealed class Owner : IWin32Window
    {
        public Owner(IntPtr h) { Handle = h; }
        public IntPtr Handle { get; }
    }

    /// <summary>
    /// Uploads the finished zip to Dropbox and makes a share link. Runs the client on a worker thread and keeps the UI pumping
    /// behind a small Cancel dialog, so Inventor stays responsive (the first run opens a browser for sign-in).
    /// Returns null when no app key is set. Callers wrap it so it can never block the Vault check-in.
    /// </summary>
    private DropboxUploadResult? UploadStep(PackageContext ctx, string localZip, string partner)
    {
        var key = ctx.Settings.DropboxAppKey;
        if (string.IsNullOrWhiteSpace(key)) return null;
        SetStatus("Partner Package: uploading to Dropbox");
        using var http = DropboxClient.NewHttpClient();
        var client = new DropboxClient(new DropboxAuth(key, http), http);
        try
        {
            return RunWithCancelDialog("Uploading to Dropbox...\nIf this is your first time, finish the sign-in in your browser.",
                                       ct => client.UploadAndShareAsync(localZip, partner, ct));
        }
        catch (OperationCanceledException) { return new DropboxUploadResult("", null, null, new[] { "Dropbox upload cancelled." }); }
    }

    /// <summary>
    /// Signs in to Dropbox before a build starts when the upload box is ticked and no token exists. Returns null on success (or when
    /// already signed in), otherwise the reason it failed or was cancelled so the caller can ask whether to build without the upload.
    /// </summary>
    public string? EnsureDropboxSignIn(PackageContext ctx)
    {
        var key = ctx.Settings.DropboxAppKey;
        if (string.IsNullOrWhiteSpace(key)) return "no Dropbox app key is set";
        try
        {
            using var http = DropboxClient.NewHttpClient();
            var auth = new DropboxAuth(key, http);
            if (auth.IsSignedIn) return null;
            SetStatus("Partner Package: signing in to Dropbox");
            RunWithCancelDialog("Signing in to Dropbox...\nFinish the sign-in in your browser.", async ct => { await auth.SignInAsync(ct); return true; });
            return null;
        }
        catch (OperationCanceledException) { return "the sign-in was cancelled"; }
        catch (Exception ex)
        {
            _log($"dropbox sign-in failed: {ex}");
            return ex.Message;
        }
        finally { SetStatus(""); }
    }

    /// <summary>Runs work on a worker thread behind a small Cancel dialog while the UI keeps pumping. Cancel surfaces as OperationCanceledException.</summary>
    private T RunWithCancelDialog<T>(string text, Func<CancellationToken, Task<T>> work)
    {
        using var cts = new CancellationTokenSource();
        var task = Task.Run(() => work(cts.Token));
        IWin32Window? owner = null;
        try { var h = _app.MainFrameHWND; if (h != 0) owner = new Owner(new IntPtr(h)); } catch { }
        using var dlg = new Form
        {
            Text = "Dropbox", FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            ShowInTaskbar = false, StartPosition = FormStartPosition.CenterParent, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            AutoScaleMode = AutoScaleMode.Dpi, AutoScaleDimensions = new System.Drawing.SizeF(96F, 96F),
            Font = System.Drawing.SystemFonts.MessageBoxFont ?? System.Drawing.SystemFonts.DefaultFont,
        };
        var cancel = new Button { Text = "Cancel", AutoSize = true, MinimumSize = new System.Drawing.Size(80, 0), Anchor = AnchorStyles.Right };
        cancel.Click += (_, _) => { try { cts.Cancel(); } catch { } cancel.Enabled = false; };
        var panel = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12) };
        panel.Controls.Add(new Label { Text = text, AutoSize = true, Margin = new Padding(0, 0, 0, 8) });
        panel.Controls.Add(cancel);
        dlg.Controls.Add(panel);
        dlg.CancelButton = cancel;
        if (owner is null) dlg.Show(); else dlg.Show(owner);
        try
        {
            // Task.Wait would wrap a cancel in AggregateException; poll IsCompleted and unwrap with GetResult instead.
            while (!task.IsCompleted) { Application.DoEvents(); Thread.Sleep(30); }
        }
        finally { dlg.Close(); }
        return task.GetAwaiter().GetResult();
    }

    private string StatusText()
    {
        try { return _app.StatusBarText ?? ""; } catch { return ""; }
    }

    private void SetStatus(string text)
    {
        try { _app.StatusBarText = text; } catch { }
    }
}
