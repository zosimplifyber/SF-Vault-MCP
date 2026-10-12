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
        Add("Purchasing Sheet", "SFVaultBridge.Purchasing", "Build the purchasing sheet for this assembly and check it into Vault.", Purchasing);
        Add("Design Review", "SFVaultBridge.Review", "Build the DRAFT design review PDF for this assembly and check it into Vault.", Review);
    }

    private void Add(string label, string internalName, string tip, Action<Inv.AssemblyDocument> work)
    {
        try
        {
            var defs = _app.CommandManager.ControlDefinitions;
            Inv.ButtonDefinition? def = null;
            try { def = defs[internalName] as Inv.ButtonDefinition; } catch { }
            def ??= defs.AddButtonDefinition(label, internalName, Inv.CommandTypesEnum.kNonShapeEditCmdType, ClientId, tip, tip,
                                             Type.Missing, Type.Missing, Inv.ButtonDisplayEnum.kAlwaysDisplayText);
            Inv.ButtonDefinitionSink_OnExecuteEventHandler handler = _ => Click(label, work);
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

    private void Click(string label, Action<Inv.AssemblyDocument> work)
    {
        if (_running) { MessageBox.Show("Already running.", label); return; }
        if (_app.ActiveDocument is not Inv.AssemblyDocument asm) { MessageBox.Show("Open an assembly first.", label); return; }
        _running = true;
        var cursor = Cursor.Current;
        try
        {
            Cursor.Current = Cursors.WaitCursor;
            work(asm);
        }
        catch (BridgeException ex) { _log($"{label}: {ex.Code} {ex.Message}"); MessageBox.Show(ex.Message, label); }
        catch (Exception ex) { _log($"{label} failed: {ex}"); MessageBox.Show($"{label} failed: {ex.Message}\nDetails are in bridge.log.", label); }
        finally { Cursor.Current = cursor; _running = false; }
    }

    private static (string Folder, string VaultFolder, string Stem) Locate(Inv.AssemblyDocument asm)
    {
        var file = asm.FullFileName;
        if (string.IsNullOrWhiteSpace(file)) throw new BridgeException("not_in_vault", "Save the assembly and check it in to Vault first.");
        var vaultFolder = VaultPaths.ToVaultFolder(file, VaultFiles.WorkingFolderRoot());
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
            var result = ToolResults.Parse(ToolRunner.Run(ToolResults.PurchasingArgs(bom, stem, folder, name, resultPath), resultPath, ToolTimeout), "workbook", "lines");
            if (!result.Ok) throw new BridgeException("tool_failed", result.Error ?? "The purchasing tool failed.");
            Publish(result.Output!, vaultFolder, name, $"Purchasing sheet for {stem}",
                    $"{result.Count} BOM lines." + (result.Unmatched.Count > 0 ? $"\nNot matched: {string.Join(", ", result.Unmatched)}" : ""));
        }
        finally { try { System.IO.Directory.Delete(temp, true); } catch { } }
    }

    private void Review(Inv.AssemblyDocument asm)
    {
        var (_, vaultFolder, stem) = Locate(asm);
        var lines = BomOrdering.Flatten(BomReader.Read(asm));
        var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SFVaultBridge", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(temp);
        try
        {
            var drawings = new List<ReviewDrawing>();
            var missing = new List<string>();
            var failed = new List<ReviewFailure>();
            var root = VaultFiles.WorkingFolderRoot();
            var seen = new HashSet<long>();

            void Collect(string partNumber, string localModel)
            {
                try
                {
                    var vaultModel = VaultPaths.ToVaultFolder(localModel, root) + "/" + System.IO.Path.GetFileName(localModel);
                    var model = VaultFiles.FindLatest(vaultModel);
                    if (model is null) { missing.Add($"{partNumber} (not in Vault)"); return; }
                    var candidates = VaultFiles.WhereUsedDrawings(model);
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
                            var pdf = System.IO.Path.Combine(temp, $"{drawings.Count + failed.Count:D3}.pdf");
                            DrawingExporter.Export(_app, local, pdf);
                            drawings.Add(new ReviewDrawing(partNumber, file.Name, pdf));
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

            Collect(stem, asm.FullFileName);
            foreach (var line in ReviewRules.UniqueComponents(lines))
            {
                var n = line.Node;
                if (string.IsNullOrWhiteSpace(n.FullPath)) continue;
                string? category = null;
                try
                {
                    var f = VaultFiles.FindLatest(VaultPaths.ToVaultFolder(n.FullPath, root) + "/" + n.FileName);
                    category = f is null ? null : VaultFiles.CategoryName(f);
                }
                catch (Exception ex) { _log($"review category lookup for {n.PartNumber} failed: {ex.Message}"); }
                if (ReviewRules.Skip(n, category)) continue;
                Collect(n.PartNumber, n.FullPath);
            }

            var bom = System.IO.Path.Combine(temp, "bom.txt");
            BomFile.Write(bom, lines);
            var asmFile = VaultFiles.FindLatest(vaultFolder + "/" + System.IO.Path.GetFileName(asm.FullFileName));
            var conn = VaultSession.Connection;
            var manifest = System.IO.Path.Combine(temp, "manifest.json");
            System.IO.File.WriteAllText(manifest, ReviewManifest.ToJson(stem,
                Convert.ToString(asm.PropertySets["Design Tracking Properties"]["Description"].Value) ?? "",
                conn.UserName, asmFile?.FileLfCyc?.LfCycStateName ?? "", asmFile?.VerNum.ToString() ?? "",
                DateTime.Now, drawings, missing, failed));

            var name = VaultPaths.ReviewName(asm.FullFileName);
            var outPdf = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(asm.FullFileName)!, name);
            var resultPath = System.IO.Path.Combine(temp, "result.json");
            var result = ToolResults.Parse(ToolRunner.Run(ToolResults.ReviewArgs(bom, manifest, outPdf, resultPath), resultPath, ToolTimeout), "pdf", "pages");
            if (!result.Ok) throw new BridgeException("tool_failed", result.Error ?? "The review tool failed.");
            Publish(result.Output!, vaultFolder, name, $"Design review for {stem}",
                    $"{result.Count} pages, {drawings.Count} drawings, {missing.Count} missing, {failed.Count} could not export.");
        }
        finally { try { System.IO.Directory.Delete(temp, true); } catch { } }
    }

    private void Publish(string localPath, string vaultFolder, string name, string comment, string summary)
    {
        try
        {
            var (file, newVersion) = VaultFiles.Publish(localPath, vaultFolder, name, comment);
            _log($"published {vaultFolder}/{name} version {file.VerNum}");
            var answer = MessageBox.Show($"{(newVersion ? "Updated" : "Added")} {vaultFolder}/{name} (version {file.VerNum}).\n{summary}\n\nOpen it now?",
                                         "SF Vault Bridge", MessageBoxButtons.YesNo);
            if (answer == DialogResult.Yes) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(localPath) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is not BridgeException)
        {
            throw new BridgeException("vault_failed", $"Saved locally at {localPath} but not checked in to Vault: {ex.Message}");
        }
    }
}
