using System.Windows.Forms;
using SFVaultBridge.Core;
using Inv = Inventor;

namespace SFVaultBridge.Addin;

/// <summary>The SF Vault Bridge panel buttons: Purchasing Sheet, Design Review, Assembly Package, Partner Package, 3D Print Package, Import New Part, Fix Data Card, I Approve! and the Status button (see RibbonStatus).</summary>
internal sealed class RibbonActions
{
    private const string ClientId = "{6F1C2D9A-4B7E-4E2B-9C1A-5D3E8F7A2B10}";
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromMinutes(10);
    private readonly Inv.Application _app;
    private readonly Func<string, IReadOnlyList<Inv.RibbonPanel>> _panels;
    private readonly Action<string> _log;
    private readonly List<(Inv.ButtonDefinition Def, Inv.ButtonDefinitionSink_OnExecuteEventHandler Handler)> _buttons = new();
    private bool _running;

    public RibbonActions(Inv.Application app, Func<string, IReadOnlyList<Inv.RibbonPanel>> panels, Action<string> log)
    {
        _app = app; _panels = panels; _log = log;
    }

    public void Create()
    {
        Add("Purchasing", "Purchasing Sheet", "SFVaultBridge.Purchasing", "Build the purchasing sheet for this assembly and check it into Vault.", Icons.Spreadsheet, Purchasing);
        Add("Review", "Design Review", "SFVaultBridge.Review", "Build the DRAFT design review PDF for this assembly and check it into Vault.", Icons.DraftPage, a => BuildPackage(a, PackageMode.Review));
        Add("Manufacturing", "Assembly Package", "SFVaultBridge.AssemblyPackage", "Build the assembly package PDF (assembly and sub-assembly drawings, NOT RELEASED stamped where needed) and check it into Vault.", Icons.ClipboardCheck, a => BuildPackage(a, PackageMode.Assembly));
        Add("Manufacturing", "Partner Package", "SFVaultBridge.PartnerPackage", "Build a zip of STEP models, PDF drawings, DXF flat patterns and a BOM for a manufacturing partner, and check it into Vault. Covers the whole assembly, the parts selected in it, or the open part.", Icons.BoxOut, null, PartnerPackage);
        Add("Manufacturing", "3D Print Package", "SFVaultBridge.PrintPackage", "Build a zip of 3MF (or STL) print files, one per 3D printed part, grouped by material, and check it into Vault.", Icons.PrintNozzle, PrintPackage);
        Add("Parts", "Import New Part", "SFVaultBridge.ImportPart", "Pick a vendor STEP and PDF and file them in Vault as a numbered purchased part.", Icons.BoxDown, null, ImportNewPart);
        Add("Parts", "Fix Data Card", "SFVaultBridge.FixDataCard", "Fix the data card (part number, title, description, vendor, source, process, finish, cost) of the open part or the parts selected in an assembly, with a preview first.", Icons.FixCard, null, FixDataCard);
        Add("Release", "I Approve!", "SFVaultBridge.Approve", "Sign off (Engr Approved By) and release this assembly's engineering parts, Make sub-assemblies, the assembly and their drawings, bottom up, with a preview first. Then builds the Purchasing Sheet and Assembly Package and opens Partner Package.", Icons.ApproveSeal, null, Approve);
    }

    private void Add(string group, string label, string internalName, string tip, Func<int, System.Drawing.Bitmap> draw, Action<Inv.AssemblyDocument>? work, Action? custom = null)
    {
        try
        {
            var defs = _app.CommandManager.ControlDefinitions;
            Inv.ButtonDefinition? def = null;
            try { def = defs[internalName] as Inv.ButtonDefinition; } catch { }
            var (small, large) = Icons.Pair(draw);
            def ??= defs.AddButtonDefinition(label, internalName, Inv.CommandTypesEnum.kNonShapeEditCmdType, ClientId, tip, tip,
                                             small, large, Inv.ButtonDisplayEnum.kAlwaysDisplayText);
            try { def.StandardIcon = small; def.LargeIcon = large; } catch (Exception ex) { _log($"{label} icon not set: {ex.Message}"); }
            Inv.ButtonDefinitionSink_OnExecuteEventHandler handler = _ => { if (custom is not null) ClickAny(label, custom); else Click(label, work!); };
            def.OnExecute += handler;
            _buttons.Add((def, handler));
            foreach (var panel in _panels(group))
            {
                try { panel.CommandControls.AddButton(def, true); }
                catch (Exception ex) { _log($"{label} button on a panel skipped: {ex.Message}"); }
            }
        }
        catch (Exception ex) { _log($"{label} button not created: {ex}"); }
    }

    public void Destroy()
    {
        foreach (var (def, handler) in _buttons)
        {
            try { def.OnExecute -= handler; } catch { }
            try { def.Delete(); } catch { }
        }
        _buttons.Clear();
    }

    private sealed class Owner : IWin32Window
    {
        public Owner(IntPtr h) { Handle = h; }
        public IntPtr Handle { get; }
    }

    private IWin32Window? OwnerWindow()
    {
        try { var h = _app.MainFrameHWND; return h == 0 ? null : new Owner(new IntPtr(h)); }
        catch { return null; }
    }

    private DialogResult Msg(string text, string caption, MessageBoxButtons buttons = MessageBoxButtons.OK)
    {
        var owner = OwnerWindow();
        return owner is null ? System.Windows.Forms.MessageBox.Show(text, caption, buttons) : System.Windows.Forms.MessageBox.Show(owner, text, caption, buttons);
    }

    private void Click(string label, Action<Inv.AssemblyDocument> work)
    {
        if (_running) { Msg("Already running.", label); return; }
        if (_app.ActiveDocument is not Inv.AssemblyDocument asm) { Msg("Open an assembly first.", label); return; }
        _running = true;
        var cursor = Cursor.Current;
        try
        {
            Cursor.Current = Cursors.WaitCursor;
            work(asm);
        }
        catch (BridgeException ex) { _log($"{label}: {ex.Code} {ex.Message}"); Msg(ex.Message, label); }
        catch (Exception ex) { _log($"{label} failed: {ex}"); Msg($"{label} failed: {ex.Message}\nDetails are in bridge.log.", label); }
        finally { Cursor.Current = cursor; _running = false; }
    }

    /// <summary>Click guard for buttons that need no open document.</summary>
    private void ClickAny(string label, Action work)
    {
        if (_running) { Msg("Already running.", label); return; }
        _running = true;
        try { work(); }
        catch (BridgeException ex) { _log($"{label}: {ex.Code} {ex.Message}"); Msg(ex.Message, label); }
        catch (Exception ex) { _log($"{label} failed: {ex}"); Msg($"{label} failed: {ex.Message}\nDetails are in bridge.log.", label); }
        finally { _running = false; }
    }

    private void ImportNewPart()
    {
        const string title = "Import New Part";
        string? step = null, pdf = null;
        var downloads = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        while (true)
        {
            using var dlg = new OpenFileDialog
            {
                Title = "Select the vendor STEP and PDF",
                InitialDirectory = System.IO.Directory.Exists(downloads) ? downloads : "",
                Filter = "Vendor files (*.stp;*.step;*.pdf)|*.stp;*.step;*.pdf",
                Multiselect = true,
            };
            var owner = OwnerWindow();
            var ok = owner is null ? dlg.ShowDialog() : dlg.ShowDialog(owner);
            if (ok != DialogResult.OK) return;
            var check = SelectionCheck.Check(dlg.FileNames);
            if (check.Error is not null) { Msg(check.Error, title); continue; }
            step = check.Step; pdf = check.Pdf;
            break;
        }
        if (step is null || pdf is null) return;

        ImportPlan? plan;
        using (var form = new ImportPartForm(_app, step, pdf, _log))
        {
            var owner = OwnerWindow();
            var r = owner is null ? form.ShowDialog() : form.ShowDialog(owner);
            plan = r == DialogResult.OK ? form.Plan : null;
        }
        if (plan is null) return;

        ImportResult result;
        var cursor = Cursor.Current;
        try
        {
            Cursor.Current = Cursors.WaitCursor;
            result = ImportPartRunner.Run(_app, plan, step, pdf, _log);
        }
        finally { Cursor.Current = cursor; }

        if (!result.Ok)
        {
            _log($"import failed at {result.FailedStep}: {result.Error}");
            var text = $"Import stopped at step: {result.FailedStep}\n{result.Error}";
            if (result.AlreadyInVault.Count > 0) text += "\n\nAlready in Vault: " + string.Join(", ", result.AlreadyInVault);
            if (result.Warnings.Count > 0) text += "\n\n" + string.Join("\n", result.Warnings);
            Msg(text, title);
            return;
        }
        _log($"import ok: {result.Cd} at {result.VaultPath}");
        var msg = $"{result.Cd} added to {result.VaultPath}. PDF attached." + (string.IsNullOrEmpty(result.State) ? "" : $"\nState:{result.State}");
        if (result.Warnings.Count > 0) msg += "\n\n" + string.Join("\n", result.Warnings);
        msg += "\n\nOpen the part now?";
        if (Msg(msg, title, MessageBoxButtons.YesNo) == DialogResult.Yes && result.LocalIpt is not null)
        {
            try { _app.Documents.Open(result.LocalIpt, true); }
            catch (Exception ex) { _log($"open {result.LocalIpt} failed: {ex}"); Msg($"Could not open the part: {ex.Message}", title); }
        }
    }

    private void FixDataCard()
    {
        const string title = "Fix Data Card";
        var doc = _app.ActiveDocument;
        var kind = doc is null ? FixCardDocKind.None : doc is Inv.PartDocument ? FixCardDocKind.Part : doc is Inv.AssemblyDocument ? FixCardDocKind.Assembly : FixCardDocKind.Other;
        var selected = new List<string?>();
        if (kind == FixCardDocKind.Assembly) selected.AddRange(ComponentSelection.Files(doc!, m => _log("fix data card: " + m)));
        var pick = FixCardRules.Pick(kind, kind == FixCardDocKind.None ? null : doc!.FullFileName, selected);
        if (pick.Message is not null) { Msg(pick.Message, title); return; }

        var root = VaultFiles.WorkingFolderRoot();
        var sources = pick.Files.Select(f =>
        {
            var name = System.IO.Path.GetFileName(f);
            try { return new FixCardSource(VaultPaths.ToVaultFolder(f, root) + "/" + name, name, null); }
            catch (BridgeException ex) { return new FixCardSource("", name, ex.Message); }
        }).ToList();
        var process = BridgeSettings.Load(BridgeSettings.DefaultPath, _log).ProcessValues;
        using var form = new FixDataCardForm(new DataCardHost(_app), sources, pick.Skipped, process, _log);
        var owner = OwnerWindow();
        if (owner is null) form.ShowDialog(); else form.ShowDialog(owner);
    }

    /// <summary>
    /// I Approve!: preview, then sign off and release the open assembly's engineering files bottom up (ApproveForm). When the top assembly ends
    /// Released, the Purchasing Sheet and Assembly Package are built through their usual code paths and the Partner Package form opens.
    /// </summary>
    private void Approve()
    {
        const string title = "I Approve!";
        if (_app.ActiveDocument is not Inv.AssemblyDocument asm) { Msg("Open an assembly first.", title); return; }
        IReadOnlyList<ReleaseModel> models;
        ReleaseHost host;
        string approver;
        var cursor = Cursor.Current;
        try
        {
            Cursor.Current = Cursors.WaitCursor;
            var (_, vaultFolder, _) = Locate((Inv.Document)asm);
            var root = VaultFiles.WorkingFolderRoot();
            var categories = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var roots = BomEnricher.Apply(BomReader.Read(asm), root, _log, categories);
            var asmFile = VaultFiles.FindLatest(vaultFolder + "/" + System.IO.Path.GetFileName(asm.FullFileName));
            var top = BomReader.TopNode(asm, VaultFiles.RevisionOf(asmFile), asmFile is null ? "" : VaultFiles.StateOf(asmFile));
            models = ReleasePlan.Models(top, asmFile is null ? null : VaultFiles.CategoryName(asmFile), roots,
                                        n => categories.TryGetValue(n.FullPath, out var c) ? c : null);
            var tree = BomOrdering.Flatten(roots).Select(l => l.Node.FullPath).Where(p => !string.IsNullOrWhiteSpace(p))
                                  .Append(asm.FullFileName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var user = "";
            try { user = _app.GeneralOptions.UserName ?? ""; } catch (Exception ex) { _log($"approve: Inventor user name unavailable: {ex.Message}"); }
            approver = ReleasePlan.ApproverName(user);
            host = new ReleaseHost(_app, new DataCardHost(_app), _log, root, tree);
        }
        finally { Cursor.Current = cursor; }
        if (approver.Length == 0)
        {
            Msg("Inventor has no user name, so there is no sign-off name. Set it in Tools, Application Options, General, User name, then try again.", title);
            return;
        }

        IReadOnlyList<ReleaseResult> results;
        using (var form = new ApproveForm(host, models, approver, System.IO.Path.GetFileName(asm.FullFileName), _log))
        {
            var owner = OwnerWindow();
            if (owner is null) form.ShowDialog(); else form.ShowDialog(owner);
            if (!form.Ran) return;
            results = form.Results;
        }
        var after = ReleasePlan.AfterRelease(results);
        if (!after.Build) { Msg(after.Message, title); return; }
        AfterStep("Purchasing Sheet", () => Purchasing(asm));
        AfterStep("Assembly Package", () => BuildPackage(asm, PackageMode.Assembly));
        AfterStep("Partner Package", () => PartnerPackage(true));
    }

    /// <summary>One after-release step with the same messages as its own button; a failure is shown and the next step still runs.</summary>
    private void AfterStep(string label, Action work)
    {
        var cursor = Cursor.Current;
        try
        {
            Cursor.Current = Cursors.WaitCursor;
            work();
        }
        catch (BridgeException ex) { _log($"{label}: {ex.Code} {ex.Message}"); Msg(ex.Message, label); }
        catch (Exception ex) { _log($"{label} failed: {ex}"); Msg($"{label} failed: {ex.Message}\nDetails are in bridge.log.", label); }
        finally { Cursor.Current = cursor; }
    }

    internal static (string Folder, string VaultFolder, string Stem) Locate(Inv.Document doc)
    {
        var noun = doc is Inv.PartDocument ? "part" : "assembly";
        var file = doc.FullFileName;
        if (string.IsNullOrWhiteSpace(file)) throw new BridgeException("not_in_vault", $"Save the {noun} and check it in to Vault first.");
        var vaultFolder = VaultPaths.ToVaultFolder(file, VaultFiles.WorkingFolderRoot());
        if (VaultFiles.FindLatest(vaultFolder + "/" + System.IO.Path.GetFileName(file)) is null)
            throw new BridgeException("not_in_vault", $"Check the {noun} in to Vault first.");
        return (System.IO.Path.GetDirectoryName(file)!, vaultFolder, System.IO.Path.GetFileNameWithoutExtension(file));
    }

    private void Purchasing(Inv.AssemblyDocument asm)
    {
        var (folder, vaultFolder, stem) = Locate((Inv.Document)asm);
        var lines = BomOrdering.Flatten(BomEnricher.Apply(BomReader.Read(asm), VaultFiles.WorkingFolderRoot(), _log));
        if (lines.Count == 0) throw new BridgeException("no_bom", "The assembly has no BOM rows.");
        var source = BomReader.SourceLine(asm);
        var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SFVaultBridge", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(temp);
        try
        {
            var bom = System.IO.Path.Combine(temp, "bom.txt");
            var resultPath = System.IO.Path.Combine(temp, "result.json");
            BomFile.Write(bom, lines);
            var name = VaultPaths.PurchasingName(asm.FullFileName);
            var result = ToolResults.Parse(ToolRunner.Run(ToolResults.PurchasingArgs(bom, stem, temp, name, resultPath, source, BridgeSettings.Load(BridgeSettings.DefaultPath, _log).SalesTaxRate), resultPath, ToolTimeout), "workbook", "lines");
            if (!result.Ok) throw new BridgeException("tool_failed", result.Error ?? "The purchasing tool failed.");
            Publish(result.Output!, System.IO.Path.Combine(folder, name), vaultFolder, name, $"Purchasing sheet for {stem}",
                    $"{result.Count} BOM lines." + (result.Note is null ? "" : $"\n{result.Note}") + (result.Unmatched.Count > 0 ? $"\nNot matched: {string.Join(", ", result.Unmatched)}" : ""));
        }
        finally { try { System.IO.Directory.Delete(temp, true); } catch { } }
    }

    /// <summary>Whole assembly, the components selected in it, or the open part (see PackageScope.Decide).</summary>
    private void PartnerPackage() => PartnerPackage(false);

    /// <param name="wholeAssembly">Ignore the selection (after I Approve!, the package is for the whole assembly).</param>
    private void PartnerPackage(bool wholeAssembly)
    {
        const string title = "Partner Package";
        var doc = _app.ActiveDocument;
        var kind = doc is Inv.AssemblyDocument ? ScopeDoc.Assembly : doc is Inv.PartDocument ? ScopeDoc.Part : doc is null ? ScopeDoc.None : ScopeDoc.Other;
        var selected = kind == ScopeDoc.Assembly && !wholeAssembly ? ComponentSelection.Files(doc!, m => _log("partner package: " + m)) : Array.Empty<string?>();
        var decision = PackageScope.Decide(kind, selected.Count);
        if (decision.Kind is null) { Msg(decision.Message ?? PackageScope.OpenMessage, title); return; }

        var cursor = Cursor.Current;
        PackageContext ctx;
        var builder = new PackageBuilder(_app, _log);
        try
        {
            Cursor.Current = Cursors.WaitCursor;
            ctx = decision.Kind switch
            {
                PackageScopeKind.Part => builder.PreparePart((Inv.PartDocument)doc!),
                PackageScopeKind.Selected => builder.PrepareSelected((Inv.AssemblyDocument)doc!, selected),
                _ => builder.Prepare((Inv.AssemblyDocument)doc!),
            };
        }
        finally { Cursor.Current = cursor; }
        var scoped = ctx.Scope != PackageScopeKind.Assembly;

        string partner;
        ManufacturingMode mode;
        bool quote, upload;
        IReadOnlyList<PackagePart> parts;
        using (var form = new PartnerPackageForm(ctx.Settings.RecentPartners, m =>
        {
            try { return scoped ? Array.Empty<PackagePart>() : builder.Parts(ctx, m); }
            catch (Exception ex) { _log($"partner package parts for {m} failed: {ex}"); throw; }
        }, ctx.Settings.DropboxAppKey,
        scoped ? m =>
        {
            try { return builder.ScopeRows(ctx, m); }
            catch (Exception ex) { _log($"partner package rows for {m} failed: {ex}"); throw; }
        } : null,
        scoped ? ctx.ScopeLine : "", scoped ? ctx.Pick?.Skipped : null))
        {
            var owner = OwnerWindow();
            var r = owner is null ? form.ShowDialog() : form.ShowDialog(owner);
            if (r != DialogResult.OK || form.Partner is null) return;
            partner = form.Partner; mode = form.Mode; quote = form.Quote; upload = form.Upload;
            var entered = form.Quantities;
            parts = scoped ? builder.ScopeParts(ctx, mode, row => entered.TryGetValue(row.Candidate.Node.FullPath ?? "", out var q) ? q : null)
                           : builder.Parts(ctx, mode);
        }
        if (upload)
        {
            var signInProblem = builder.EnsureDropboxSignIn(ctx);
            if (signInProblem is not null)
            {
                if (Msg($"Dropbox sign-in did not finish ({signInProblem}).\n\nBuild the package without uploading it to Dropbox?", "SF Vault Bridge", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                upload = false;
            }
        }
        ShowOutcome(builder.Build(ctx, mode, partner, parts, quote, upload));
    }

    private void PrintPackage(Inv.AssemblyDocument asm)
    {
        var builder = new PackageBuilder(_app, _log);
        var ctx = builder.Prepare(asm);
        var parts = builder.Parts(ctx, ManufacturingMode.Print);
        if (parts.Count == 0)
        {
            Msg("No 3D print parts found (Process = 3D Print or a print material).", "3D Print Package");
            return;
        }
        ShowOutcome(builder.Build(ctx, ManufacturingMode.Print, "", parts));
    }

    private void ShowOutcome(PackageOutcome o)
    {
        const string nl = "\n";
        var text = o.ZipName + nl
                   + (o.VaultPath is null ? "" : $"Vault: {o.VaultPath}{(o.Version is null ? "" : $" (version {o.Version})")}{nl}")
                   + (o.DownloadsCopy is null ? "" : $"Copy: {o.DownloadsCopy}{nl}")
                   + (o.DropboxPath is null ? "" : $"Dropbox: {o.DropboxPath}{nl}")
                   + (o.ShareUrl is null ? "" : $"Link ({o.Visibility ?? "view only"}{(o.LinkCopied ? ", copied to the clipboard" : "")}): {o.ShareUrl}{nl}")
                   + o.Summary + (o.Warnings.Count > 0 ? nl + string.Join(nl, o.Warnings) : "")
                   + nl + nl + "Open folder?";
        if (Msg(text, "SF Vault Bridge", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        try
        {
            var args = o.DownloadsCopy is null ? $"\"{Downloads.Folder}\"" : $"/select,\"{o.DownloadsCopy}\"";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log($"open folder failed: {ex}");
            Msg("Could not open the folder.", "SF Vault Bridge");
        }
    }

    private void BuildPackage(Inv.AssemblyDocument asm, PackageMode mode)
    {
        var assemblyMode = mode == PackageMode.Assembly;
        var (folder, vaultFolder, stem) = Locate((Inv.Document)asm);
        var lines = BomOrdering.Flatten(BomEnricher.Apply(BomReader.Read(asm), VaultFiles.WorkingFolderRoot(), _log));
        var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SFVaultBridge", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(temp);
        try
        {
            var drawings = new List<ReviewDrawing>();
            var purchased = new List<ReviewPurchased>();
            var missing = new List<string>();
            var noVendor = new List<string>();
            var failed = new List<ReviewFailure>();
            var root = VaultFiles.WorkingFolderRoot();
            var seen = new HashSet<long>();
            var pdfCount = 0;

            void Collect(string partNumber, string localModel)
            {
                try
                {
                    var vaultModel = VaultPaths.ToVaultFolder(localModel, root) + "/" + System.IO.Path.GetFileName(localModel);
                    var model = VaultFiles.FindLatest(vaultModel);
                    if (model is null) { missing.Add($"{partNumber} (not in Vault)"); return; }
                    var candidates = DrawingFinder.Candidates(model, _log);
                    var picked = ReviewRules.PickDrawings(model.Name, candidates.Select(c => c.Name));
                    if (picked.Count == 0) { missing.Add(partNumber); return; }
                    foreach (var name in picked)
                    {
                        var file = candidates.First(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
                        if (!seen.Add(file.MasterId)) continue;
                        try
                        {
                            var local = VaultFiles.LocalPathOf(file);
                            var openDoc = DrawingExporter.FindOpen(_app, local);
                            if (openDoc is null)
                                VaultFiles.AcquireLatest(new[] { file });
                            var pdf = System.IO.Path.Combine(temp, $"{pdfCount++:D3}.pdf");
                            DrawingExporter.Export(_app, local, pdf);
                            if (!assemblyMode) drawings.Add(new ReviewDrawing(partNumber, file.Name, pdf));
                            else if (openDoc is not null)
                                drawings.Add(new ReviewDrawing(partNumber, file.Name, pdf, "", "printed from the copy open in Inventor"));
                            else if (file.CheckedOut)
                                drawings.Add(new ReviewDrawing(partNumber, file.Name, pdf, "", "checked out; printed from the local copy"));
                            else drawings.Add(new ReviewDrawing(partNumber, file.Name, pdf, VaultFiles.StateOf(file)));
                        }
                        catch (Exception ex)
                        {
                            _log($"review export {file.Name} failed: {ex}");
                            failed.Add(new ReviewFailure(file.Name, ex.Message));
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log($"review lookup for {partNumber} failed: {ex}");
                    failed.Add(new ReviewFailure(partNumber, ex.Message));
                }
            }

            void CollectPurchased(BomNode n, Autodesk.Connectivity.WebServices.File? part)
            {
                try
                {
                    var pdf = part is null ? null : VaultFiles.VendorPdf(part, n.VendorNumber);
                    if (pdf is null) { noVendor.Add(n.PartNumber); return; }
                    var local = VaultFiles.LocalPathOf(pdf);
                    if (!System.IO.File.Exists(local)) VaultFiles.AcquireLatest(new[] { pdf });
                    if (!System.IO.File.Exists(local)) throw new BridgeException("download_failed", $"{pdf.Name} could not be downloaded.");
                    purchased.Add(new ReviewPurchased(n.PartNumber, n.Vendor, n.VendorNumber, local));
                }
                catch (Exception ex)
                {
                    _log($"review vendor PDF for {n.PartNumber} failed: {ex}");
                    failed.Add(new ReviewFailure("Purchased: " + n.PartNumber, ex.Message));
                }
            }

            Collect(stem, asm.FullFileName);
            foreach (var line in ReviewRules.UniqueComponents(lines))
            {
                var n = line.Node;
                if (string.IsNullOrWhiteSpace(n.FullPath)) continue;
                Autodesk.Connectivity.WebServices.File? f = null;
                string? category = null;
                try
                {
                    f = VaultFiles.FindLatest(VaultPaths.ToVaultFolder(n.FullPath, root) + "/" + n.FileName);
                    category = f is null ? null : VaultFiles.CategoryName(f);
                }
                catch (Exception ex) { _log($"review category lookup for {n.PartNumber} failed: {ex.Message}"); }
                if (assemblyMode)
                {
                    if (ReviewRules.IncludeInAssemblyPackage(n, category)) Collect(n.PartNumber, n.FullPath);
                    continue;
                }
                switch (ReviewRules.Classify(n, category))
                {
                    case PartKind.Make: Collect(n.PartNumber, n.FullPath); break;
                    case PartKind.Purchased: CollectPurchased(n, f); break;
                }
            }

            var bom = System.IO.Path.Combine(temp, "bom.txt");
            BomFile.Write(bom, lines);
            var asmFile = VaultFiles.FindLatest(vaultFolder + "/" + System.IO.Path.GetFileName(asm.FullFileName));
            var conn = VaultSession.Connection;
            var manifest = System.IO.Path.Combine(temp, "manifest.json");
            System.IO.File.WriteAllText(manifest, ReviewManifest.ToJson(stem,
                Convert.ToString(asm.PropertySets["Design Tracking Properties"]["Description"].Value) ?? "",
                conn.UserName, asmFile?.FileLfCyc?.LfCycStateName ?? "", asmFile?.VerNum.ToString() ?? "",
                DateTime.Now, drawings, missing, failed, purchased, noVendor, mode, assemblyMode ? VaultFiles.RevisionOf(asmFile) : ""));

            var name = assemblyMode ? VaultPaths.AssemblyPackageName(asm.FullFileName) : VaultPaths.ReviewName(asm.FullFileName);
            var outPdf = System.IO.Path.Combine(temp, name);
            var resultPath = System.IO.Path.Combine(temp, "result.json");
            var result = ToolResults.Parse(ToolRunner.Run(ToolResults.ReviewArgs(bom, manifest, outPdf, resultPath, BridgeSettings.Load(BridgeSettings.DefaultPath, _log).SalesTaxRate), resultPath, ToolTimeout), "pdf", "pages");
            if (!result.Ok) throw new BridgeException("tool_failed", result.Error ?? (assemblyMode ? "The assembly package tool failed." : "The review tool failed."));
            if (assemblyMode && result.Mode != "assembly")
                throw new BridgeException("tool_failed", "SFVaultTools.exe is out of date. Close Inventor and rerun Install-VaultBridge.ps1.");
            var summary = assemblyMode
                ? $"{result.Count} pages, {drawings.Count} drawings, {result.Unreleased} not released, {missing.Count} missing, {failed.Count} could not export."
                : $"{result.Count} pages, {drawings.Count} drawings, {purchased.Count} vendor PDFs, {missing.Count} missing, {noVendor.Count} without vendor drawing, {failed.Count} could not export.";
            Publish(result.Output!, System.IO.Path.Combine(folder, name), vaultFolder, name,
                    assemblyMode ? $"Assembly package for {stem}" : $"Design review for {stem}", summary);
        }
        finally { try { System.IO.Directory.Delete(temp, true); } catch { } }
    }

    private string? KeepOutput(string tempOutput, string name) => Downloads.Keep(tempOutput, name, _log);

    private void Publish(string tempOutput, string destLocal, string vaultFolder, string name, string comment, string summary)
    {
        (Autodesk.Connectivity.WebServices.File? File, bool NewVersion, string? Warning) r;
        try { r = VaultFiles.Publish(tempOutput, destLocal, vaultFolder, name, comment); }
        catch (Exception ex)
        {
            var kept = KeepOutput(tempOutput, name);
            var note = kept is null ? "" : $"\nYour file was saved to {kept}.";
            if (ex is BridgeException be) throw new BridgeException(be.Code, be.Message + note);
            throw new BridgeException("vault_failed", $"{name} was not checked in to Vault: {ex.Message}{note}");
        }
        _log($"published {vaultFolder}/{name} version {(r.File is null ? "?" : r.File.VerNum.ToString())}");
        var version = r.File is null ? "" : $" (version {r.File.VerNum})";
        var text = $"{(r.NewVersion ? "Updated" : "Added")} {vaultFolder}/{name}{version}.\n{summary}"
                   + (r.Warning is null ? "" : $"\n{r.Warning}") + "\n\nOpen it now?";
        try
        {
            if (Msg(text, "SF Vault Bridge", MessageBoxButtons.YesNo) == DialogResult.Yes)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(destLocal) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log($"open {destLocal} failed: {ex}");
            Msg($"Could not open {name}.", "SF Vault Bridge");
        }
    }
}
