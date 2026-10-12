using System.Windows.Forms;
using SFVaultBridge.Core;
using Inv = Inventor;

namespace SFVaultBridge.Addin;

/// <summary>The Purchasing Sheet and Design Review buttons on the SF Vault Bridge panel.</summary>
internal sealed class RibbonActions
{
    private const string ClientId = "{6F1C2D9A-4B7E-4E2B-9C1A-5D3E8F7A2B10}";
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromMinutes(10);
    private readonly Inv.Application _app;
    private readonly IReadOnlyList<Inv.RibbonPanel> _panels;
    private readonly Action<string> _log;
    private readonly List<(Inv.ButtonDefinition Def, Inv.ButtonDefinitionSink_OnExecuteEventHandler Handler)> _buttons = new();
    private bool _running;

    public RibbonActions(Inv.Application app, IReadOnlyList<Inv.RibbonPanel> panels, Action<string> log)
    {
        _app = app; _panels = panels; _log = log;
    }

    public void Create()
    {
        Add("Purchasing Sheet", "SFVaultBridge.Purchasing", "Build the purchasing sheet for this assembly and check it into Vault.", Icons.Spreadsheet, Purchasing);
        Add("Design Review", "SFVaultBridge.Review", "Build the DRAFT design review PDF for this assembly and check it into Vault.", Icons.DraftPage, a => BuildPackage(a, PackageMode.Review));
        Add("Assembly Package", "SFVaultBridge.AssemblyPackage", "Build the assembly package PDF (assembly and sub-assembly drawings, NOT RELEASED stamped where needed) and check it into Vault.", Icons.ClipboardCheck, a => BuildPackage(a, PackageMode.Assembly));
        Add("Import New Part", "SFVaultBridge.ImportPart", "Pick a vendor STEP and PDF and file them in Vault as a numbered purchased part.", Icons.BoxDown, null, ImportNewPart);
    }

    private void Add(string label, string internalName, string tip, Func<int, System.Drawing.Bitmap> draw, Action<Inv.AssemblyDocument>? work, Action? custom = null)
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
            foreach (var panel in _panels)
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

    private static (string Folder, string VaultFolder, string Stem) Locate(Inv.AssemblyDocument asm)
    {
        var file = asm.FullFileName;
        if (string.IsNullOrWhiteSpace(file)) throw new BridgeException("not_in_vault", "Save the assembly and check it in to Vault first.");
        var vaultFolder = VaultPaths.ToVaultFolder(file, VaultFiles.WorkingFolderRoot());
        if (VaultFiles.FindLatest(vaultFolder + "/" + System.IO.Path.GetFileName(file)) is null)
            throw new BridgeException("not_in_vault", "Check the assembly in to Vault first.");
        return (System.IO.Path.GetDirectoryName(file)!, vaultFolder, System.IO.Path.GetFileNameWithoutExtension(file));
    }

    private void Purchasing(Inv.AssemblyDocument asm)
    {
        var (folder, vaultFolder, stem) = Locate(asm);
        var lines = BomOrdering.Flatten(BomReader.Read(asm));
        if (lines.Count == 0) throw new BridgeException("no_bom", "The assembly has no BOM rows.");
        var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SFVaultBridge", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(temp);
        try
        {
            var bom = System.IO.Path.Combine(temp, "bom.txt");
            var resultPath = System.IO.Path.Combine(temp, "result.json");
            BomFile.Write(bom, lines);
            var name = VaultPaths.PurchasingName(asm.FullFileName);
            var result = ToolResults.Parse(ToolRunner.Run(ToolResults.PurchasingArgs(bom, stem, temp, name, resultPath), resultPath, ToolTimeout), "workbook", "lines");
            if (!result.Ok) throw new BridgeException("tool_failed", result.Error ?? "The purchasing tool failed.");
            Publish(result.Output!, System.IO.Path.Combine(folder, name), vaultFolder, name, $"Purchasing sheet for {stem}",
                    $"{result.Count} BOM lines." + (result.Note is null ? "" : $"\n{result.Note}") + (result.Unmatched.Count > 0 ? $"\nNot matched: {string.Join(", ", result.Unmatched)}" : ""));
        }
        finally { try { System.IO.Directory.Delete(temp, true); } catch { } }
    }

    private void BuildPackage(Inv.AssemblyDocument asm, PackageMode mode)
    {
        var assemblyMode = mode == PackageMode.Assembly;
        var (folder, vaultFolder, stem) = Locate(asm);
        var lines = BomOrdering.Flatten(BomReader.Read(asm));
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

            // Where-used drawings plus any .idw/.dwg in Vault named like the component, de-duplicated by master id.
            IReadOnlyList<Autodesk.Connectivity.WebServices.File> Candidates(Autodesk.Connectivity.WebServices.File model)
            {
                var list = new List<Autodesk.Connectivity.WebServices.File>(VaultFiles.WhereUsedDrawings(model));
                try
                {
                    foreach (var f in VaultFiles.DrawingsNamed(System.IO.Path.GetFileNameWithoutExtension(model.Name)))
                        if (list.All(x => x.MasterId != f.MasterId)) list.Add(f);
                }
                catch (Exception ex) { _log($"review name search for {model.Name} failed: {ex.Message}"); }
                return list;
            }

            void Collect(string partNumber, string localModel)
            {
                try
                {
                    var vaultModel = VaultPaths.ToVaultFolder(localModel, root) + "/" + System.IO.Path.GetFileName(localModel);
                    var model = VaultFiles.FindLatest(vaultModel);
                    if (model is null) { missing.Add($"{partNumber} (not in Vault)"); return; }
                    var candidates = Candidates(model);
                    var picked = ReviewRules.PickDrawings(model.Name, candidates.Select(c => c.Name));
                    if (picked.Count == 0) { missing.Add(partNumber); return; }
                    foreach (var name in picked)
                    {
                        var file = candidates.First(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
                        if (!seen.Add(file.MasterId)) continue;
                        try
                        {
                            var local = VaultFiles.LocalPathOf(file);
                            if (DrawingExporter.FindOpen(_app, local) is null)
                                VaultFiles.AcquireLatest(new[] { file });
                            var pdf = System.IO.Path.Combine(temp, $"{pdfCount++:D3}.pdf");
                            DrawingExporter.Export(_app, local, pdf);
                            drawings.Add(new ReviewDrawing(partNumber, file.Name, pdf, assemblyMode ? VaultFiles.StateOf(file) : ""));
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
            var result = ToolResults.Parse(ToolRunner.Run(ToolResults.ReviewArgs(bom, manifest, outPdf, resultPath), resultPath, ToolTimeout), "pdf", "pages");
            if (!result.Ok) throw new BridgeException("tool_failed", result.Error ?? (assemblyMode ? "The assembly package tool failed." : "The review tool failed."));
            var summary = assemblyMode
                ? $"{result.Count} pages, {drawings.Count} drawings, {result.Unreleased} not released, {missing.Count} missing, {failed.Count} could not export."
                : $"{result.Count} pages, {drawings.Count} drawings, {purchased.Count} vendor PDFs, {missing.Count} missing, {noVendor.Count} without vendor drawing, {failed.Count} could not export.";
            Publish(result.Output!, System.IO.Path.Combine(folder, name), vaultFolder, name,
                    assemblyMode ? $"Assembly package for {stem}" : $"Design review for {stem}", summary);
        }
        finally { try { System.IO.Directory.Delete(temp, true); } catch { } }
    }

    /// <summary>Copies the finished output to Downloads\SF Vault Bridge before the temp folder goes away; null if even that fails.</summary>
    private string? KeepOutput(string tempOutput, string name)
    {
        try
        {
            var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "SF Vault Bridge");
            System.IO.Directory.CreateDirectory(dir);
            var target = System.IO.Path.Combine(dir, VaultPaths.UniqueName(name, n => System.IO.File.Exists(System.IO.Path.Combine(dir, n))));
            System.IO.File.Copy(tempOutput, target, false);
            return target;
        }
        catch (Exception ex) { _log($"could not keep a copy of {name}: {ex.Message}"); return null; }
    }

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
