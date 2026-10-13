using SFVaultBridge.Core;
using Inv = Inventor;

namespace SFVaultBridge.Addin;

internal sealed record ImportResult(bool Ok, string? Cd, string? VaultPath, string? FailedStep, string? Error,
                                    IReadOnlyList<string> AlreadyInVault, IReadOnlyList<string> Warnings, string? LocalIpt, string? State = null);

/// <summary>Runs the Import New Part steps in order on Inventor's UI thread. Never throws; a failure names the step and lists what already reached Vault.</summary>
internal static class ImportPartRunner
{
    private const string PartCategory = "Part - Purchased";
    private const string DocCategory = "Documents";

    private sealed class StepFailure : Exception
    {
        public string Step { get; }
        public StepFailure(string step, string message) : base(message) => Step = step;
    }

    public static ImportResult Run(Inv.Application app, ImportPlan plan, string stepPath, string pdfPath, Action<string>? log = null)
    {
        var inVault = new List<string>();
        var warnings = new List<string>();
        string? cd = null, vaultPath = null, localIpt = null, state = null;
        string? pdfLocalCopy = null;
        var partDone = false;
        var step = "validate";
        try
        {
            var problems = ImportRules.Validate(plan);
            if (problems.Count > 0) throw new StepFailure(step, string.Join(" ", problems));
            if (!System.IO.File.Exists(stepPath)) throw new StepFailure(step, $"STEP file not found: {stepPath}");
            if (!System.IO.File.Exists(pdfPath)) throw new StepFailure(step, $"PDF not found: {pdfPath}");
            plan = plan with { VendorNumber = ImportRules.CleanText(plan.VendorNumber), Description = ImportRules.CleanText(plan.Description) };

            step = "duplicate";
            var dup = VaultFiles.FindByVendorNumber(plan.Vendor, plan.VendorNumber, log);
            if (dup is not null)
                throw new StepFailure(step, $"Already in Vault as {System.IO.Path.GetFileNameWithoutExtension(dup.Name)} ({VaultFiles.FolderPathOf(dup)}). Nothing was written.");

            step = "number";
            cd = VaultFiles.NextPartNumber();

            var vaultFolder = ImportRules.VaultFolder(plan);
            step = "folders";
            foreach (var made in VaultFiles.EnsureFolder(vaultFolder)) inVault.Add(made);

            step = "part";
            var partName = ImportRules.PartFileName(cd);
            var pdfName = ImportRules.PdfFileName(cd, plan);
            var localDir = System.IO.Path.Combine(VaultFiles.WorkingFolderRoot(), vaultFolder[2..].Replace('/', '\\'));
            System.IO.Directory.CreateDirectory(localDir);
            localIpt = System.IO.Path.Combine(localDir, partName);
            var design = new Dictionary<string, string>
            {
                ["Part Number"] = cd,
                ["Description"] = plan.Description,
                ["Vendor"] = ImportRules.DataCardVendor(plan.Vendor),
                ["Catalog Web Link"] = plan.VendorNumber,
                ["Designer"] = plan.Designer ?? "",
            };
            var custom = new Dictionary<string, string>
            {
                ["Vendor Number"] = plan.VendorNumber,
                ["Source"] = "Buy",
                ["Engr Approved By"] = "NOT REVIEWED",
            };
            custom["Material Finish"] = ImportRules.NormalizeFinish(plan.MaterialFinish);
            new StepImporter(app).ImportStepAsPart(stepPath, localIpt, design, custom, plan.Material.Trim(), warnings);
            partDone = true;

            step = "pdf";
            var pdfLocal = System.IO.Path.Combine(localDir, pdfName);
            if (System.IO.File.Exists(pdfLocal)) throw new StepFailure(step, $"Refusing to overwrite existing file {pdfLocal}");
            System.IO.File.Copy(pdfPath, pdfLocal, false);
            pdfLocalCopy = pdfLocal;
            var (pdf, pdfCatError) = AddOrNote(pdfLocal, vaultFolder, pdfName, Array.Empty<long>(), DocCategory, $"{plan.Vendor} {plan.VendorNumber} drawing", step, inVault);
            if (pdfCatError is not null) throw new StepFailure("category", $"{pdfName} is in Vault but its category could not be set to {DocCategory}: {pdfCatError}");

            step = "add part";
            var (part, partCatError) = AddOrNote(localIpt, vaultFolder, partName, new[] { pdf.Id }, PartCategory, $"{plan.Vendor} {plan.VendorNumber} import", step, inVault);
            vaultPath = $"{vaultFolder}/{partName}";
            if (partCatError is not null) throw new StepFailure("category", $"{partName} is in Vault but its category could not be set to {PartCategory}: {partCatError}");

            step = "read back";
            try
            {
                var back = VaultFiles.FindLatest(vaultPath);
                if (back is null) warnings.Add($"Read back: {vaultPath} was not found in Vault.");
                else
                {
                    if (VaultFiles.CategoryName(back) != PartCategory) warnings.Add($"Read back: category is {VaultFiles.CategoryName(back) ?? "(none)"}, expected {PartCategory}.");
                    state = back.FileLfCyc?.LfCycStateName;
                    try
                    {
                        var reader = VaultFiles.CardValueReader(back, out var missingDefs);
                        foreach (var name in missingDefs)
                            warnings.Add($"Vault has no property named {name}.");
                        foreach (var name in ImportRules.EmptyCardProperties(reader).Where(n => !missingDefs.Contains(n)))
                            warnings.Add($"{name} is not visible on the Vault data card yet; check the part's iProperties.");
                    }
                    catch (Exception ex) { warnings.Add($"Could not read the data card back: {ex.Message}"); }
                }
            }
            catch (Exception ex) { warnings.Add($"Read back failed: {ex.Message}"); }

            step = "tidy";
            Tidy(new[] { stepPath, pdfPath }, warnings);

            return new ImportResult(true, cd, vaultPath, null, null, inVault, warnings, localIpt, state);
        }
        catch (StepFailure ex) { return Failed(ex.Step, ex.Message); }
        catch (BridgeException ex) { return Failed(step, ex.Message); }
        catch (Exception ex) { return Failed(step, ex.Message); }

        ImportResult Failed(string failedStep, string error)
        {
            if (partDone)
            {
                var left = new[] { localIpt, pdfLocalCopy }.Where(p => p is not null && System.IO.File.Exists(p)).ToList();
                if (left.Count > 0)
                    error += $"\nLocal files left in {System.IO.Path.GetDirectoryName(left[0])}: {string.Join(", ", left.Select(p => System.IO.Path.GetFileName(p)))}";
            }
            return new(false, cd, vaultPath, failedStep, error, inVault, warnings, localIpt, state);
        }
    }

    /// <summary>AddNew, but when it throws, checks whether the file reached Vault anyway and records it before rethrowing as a step failure.</summary>
    private static (Autodesk.Connectivity.WebServices.File File, string? CategoryError) AddOrNote(string local, string vaultFolder, string name, long[] attach,
        string category, string comment, string step, List<string> inVault)
    {
        try
        {
            var r = VaultFiles.AddNew(local, vaultFolder, name, attach, category, comment);
            inVault.Add(name);
            return r;
        }
        catch (Exception ex)
        {
            try { if (VaultFiles.FindLatest($"{vaultFolder}/{name}") is not null) inVault.Add(name); }
            catch { /* could not check; the failure message stands */ }
            throw new StepFailure(step, ex.Message);
        }
    }

    /// <summary>Moves the downloads to Downloads\Imported (adding " (n)" on a name clash); a failure is a warning.</summary>
    private static void Tidy(IEnumerable<string> files, List<string> warnings)
    {
        try
        {
            var done = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Imported");
            System.IO.Directory.CreateDirectory(done);
            foreach (var f in files)
            {
                try
                {
                    var stem = System.IO.Path.GetFileNameWithoutExtension(f);
                    var ext = System.IO.Path.GetExtension(f);
                    var target = System.IO.Path.Combine(done, stem + ext);
                    for (var n = 2; System.IO.File.Exists(target); n++) target = System.IO.Path.Combine(done, $"{stem} ({n}){ext}");
                    System.IO.File.Move(f, target);
                }
                catch (Exception ex) { warnings.Add($"Could not move {System.IO.Path.GetFileName(f)} to Downloads\\Imported: {ex.Message}"); }
            }
        }
        catch (Exception ex) { warnings.Add($"Could not tidy the downloads, move them by hand: {ex.Message}"); }
    }
}
