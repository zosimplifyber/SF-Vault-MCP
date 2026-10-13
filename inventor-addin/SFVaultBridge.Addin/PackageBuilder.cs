using SFVaultBridge.Core;
using Inv = Inventor;
using WsFile = Autodesk.Connectivity.WebServices.File;

namespace SFVaultBridge.Addin;

internal sealed record PackageContext(Inv.AssemblyDocument Assembly, string Folder, string VaultFolder, string Stem,
                                      WsFile? AssemblyFile, BomNode Top, IReadOnlyList<BomNode> Roots,
                                      IReadOnlyDictionary<string, string?> Categories, string Source,
                                      BridgeSettings Settings, PrintMaterials Materials);

internal sealed record PackageOutcome(string ZipName, string? VaultPath, int? Version, string? DownloadsCopy,
                                      string Summary, IReadOnlyList<string> Warnings);

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
            var (folder, vaultFolder, stem) = RibbonActions.Locate(asm);
            var settings = BridgeSettings.Load(BridgeSettings.DefaultPath, _log);
            var categories = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var roots = BomEnricher.Apply(BomReader.Read(asm), VaultFiles.WorkingFolderRoot(), _log, categories);
            var asmFile = VaultFiles.FindLatest(vaultFolder + "/" + System.IO.Path.GetFileName(asm.FullFileName));
            var top = BomReader.TopNode(asm, VaultFiles.RevisionOf(asmFile), asmFile is null ? "" : VaultFiles.StateOf(asmFile));
            return new PackageContext(asm, folder, vaultFolder, stem, asmFile, top, roots, categories, BomReader.SourceLine(asm),
                                      settings, new PrintMaterials(settings.PrintMaterials));
        }
        finally { SetStatus(previous); }
    }

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
        public bool ModelAcquired;
    }

    /// <param name="quote">Partner packages only: skip the release gate, stamp every PDF FOR QUOTE and name the zip Quote.</param>
    public PackageOutcome Build(PackageContext ctx, ManufacturingMode mode, string partner, IReadOnlyList<PackagePart> parts, bool quote = false)
    {
        var print = mode == ManufacturingMode.Print;
        if (print) quote = false;
        var prefix = print ? "3D Print Package" : "Partner Package";
        partner = (partner ?? "").Trim();
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
        System.IO.Directory.CreateDirectory(exportDir);
        try
        {
            var files = new List<PackageFile>();
            var rows = new List<PackageRow>();
            var failed = new List<PackageFailure>();
            var missingDrawings = new List<string>();
            var noFlatPattern = new List<string>();
            var guessed = new List<string>();
            var usedDests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var printFormat = print && parts.Count > 0 ? ModelExporter.ResolvePrintFormat(_app, _log) : null;
            var counter = 0;

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

                void AcquireModel()
                {
                    if (r.ModelAcquired) return;
                    if (DocumentOpener.FindOpen(_app, r.ModelLocal!) is null) VaultFiles.AcquireLatest(new[] { r.Model! });
                    r.ModelAcquired = true;
                }

                if (part.Files.HasFlag(PackageFiles.Step))
                    Step("STEP", () =>
                    {
                        AcquireModel();
                        var dest = Unique(PackageRules.Dest(PackageFiles.Step, part));
                        var src = System.IO.Path.Combine(exportDir, $"{counter++:D3}.stp");
                        ModelExporter.Step(_app, r.ModelLocal!, src, _log);
                        files.Add(new PackageFile(src, dest, false)); partFiles.Add(dest);
                    });
                if (part.Files.HasFlag(PackageFiles.Dxf))
                    Step("DXF", () =>
                    {
                        AcquireModel();
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
                        if (!open) VaultFiles.AcquireLatest(new[] { drawing });
                        var src = System.IO.Path.Combine(exportDir, $"{counter++:D3}.pdf");
                        DrawingExporter.Export(_app, local, src);
                        var stamp = open || drawing.CheckedOut || !PackageRules.IsReleased(VaultFiles.StateOf(drawing));
                        var dest = Unique(PackageRules.Dest(PackageFiles.Pdf, part));
                        files.Add(new PackageFile(src, dest, stamp)); partFiles.Add(dest);
                    });
                if (part.Files.HasFlag(PackageFiles.Print) && printFormat is not null)
                    Step("print", () =>
                    {
                        AcquireModel();
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

            SetStatus($"{prefix}: building BOM and cover");
            var user = "";
            try { user = VaultSession.Connection.UserName ?? ""; } catch (Exception ex) { _log($"user name unavailable: {ex.Message}"); }
            var manifest = System.IO.Path.Combine(temp, "manifest.json");
            System.IO.File.WriteAllText(manifest, PackageManifest.ToJson(new PackageManifestData(
                print ? "print" : "partner", ctx.Stem, ctx.Top.Description, VaultFiles.RevisionOf(ctx.AssemblyFile),
                ctx.AssemblyFile is null ? "" : VaultFiles.StateOf(ctx.AssemblyFile), partner, PackageRules.ModeName(mode), ctx.Source,
                user, DateTime.Now, rows, files, guessed, missingDrawings, noFlatPattern, failed, quote)));

            var zipName = print ? PackageRules.PrintZipName(ctx.Stem) : PackageRules.PartnerZipName(ctx.Stem, partner, mode, quote);
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

            var upload = UploadStep(tempZip, partner, _log);
            if (upload is not null) warnings.Add(upload);

            SetStatus($"{prefix}: checking in");
            string? vaultPath = null;
            int? version = null;
            var comment = print ? $"3D print package for {ctx.Stem}"
                                : $"{partner} {PackageRules.ModeName(mode)} {(quote ? "quote " : "")}package for {ctx.Stem}";
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
            return new PackageOutcome(zipName, vaultPath, version, downloadsCopy, summary, warnings);
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
                                          d?.CheckedOut ?? false, d is null ? null : CheckedOutBy(d), d is not null && OpenDirty(r.DrawingLocal)));
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
                throw new BridgeException("not_released", ReleaseGate.Message(blockers));
            }
        }
        finally { SetStatus(previous); }
    }

    private string? CheckedOutBy(WsFile f)
    {
        if (!f.CheckedOut) return null;
        try
        {
            var conn = VaultSession.Connection;
            if (f.CkOutUserId == conn.UserID) return "you";
            return conn.WebServiceManager.AdminService.GetUserByUserId(f.CkOutUserId)?.Name ?? "someone else";
        }
        catch (Exception ex)
        {
            _log($"check-out user lookup failed: {ex.Message}");
            return "someone else";
        }
    }

    private bool OpenDirty(string? local)
    {
        if (string.IsNullOrEmpty(local)) return false;
        try { return DocumentOpener.FindOpen(_app, local)?.Dirty ?? false; }
        catch { return false; }
    }

    /// <summary>
    /// Hook for the Dropbox upload (task 7): after the Downloads copy, independent of the Vault check-in. Will call
    /// DropboxClient.UploadAndShareAsync(localZip, partner, ct) and return a note for the result dialog (share link, warnings).
    /// Not implemented yet: does nothing.
    /// </summary>
    private static string? UploadStep(string localZip, string partner, Action<string> log) => null;

    private string StatusText()
    {
        try { return _app.StatusBarText ?? ""; } catch { return ""; }
    }

    private void SetStatus(string text)
    {
        try { _app.StatusBarText = text; } catch { }
    }
}
