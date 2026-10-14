using SFVaultBridge.Core;
using Inv = Inventor;

namespace SFVaultBridge.Addin;

/// <summary>IInventorReader on the running Inventor. Reads only; the BOM read restores the BOM view settings it touches.</summary>
internal sealed class InventorReader : IInventorReader
{
    private readonly Inv.Application _app;
    private readonly Action<string> _log;

    public InventorReader(Inv.Application app, Action<string> log) { _app = app; _log = log; }

    public OpenBomResult OpenBom()
    {
        if (_app.ActiveDocument is not Inv.AssemblyDocument asm)
            throw new BridgeException("no_assembly", "The active Inventor document is not an assembly. Open the assembly and make it the active window.");
        var roots = BomReader.Read(asm);
        if (VaultSession.IsSignedIn) roots = BomEnricher.Apply(roots, VaultFiles.WorkingFolderRoot(), _log);
        var file = "";
        try { file = System.IO.Path.GetFileName(asm.FullFileName ?? ""); } catch { }
        return new OpenBomResult(asm.DisplayName, file, BomReader.SourceLine(asm), roots);
    }

    public string? ActiveDocument()
    {
        var d = _app.ActiveDocument;
        if (d is null) return null;
        var kind = d is Inv.AssemblyDocument ? "assembly" : d is Inv.PartDocument ? "part" : d is Inv.DrawingDocument ? "drawing" : "document";
        return $"{d.DisplayName} ({kind})";
    }
}

/// <summary>IIntakeHost: the Import New Part path (ImportPartRunner) for the MCP intake tools.</summary>
internal sealed class IntakeHost : IIntakeHost
{
    private readonly Inv.Application _app;
    private readonly Action<string> _log;

    public IntakeHost(Inv.Application app, Action<string> log) { _app = app; _log = log; }

    public string Designer()
    {
        try { return ImportRules.DesignerName(_app.GeneralOptions.UserName); } catch { return ""; }
    }

    public IReadOnlyList<string> Materials()
    {
        var list = new List<string>();
        try { foreach (Inv.Asset a in _app.ActiveMaterialLibrary.MaterialAssets) list.Add(a.DisplayName); }
        catch (Exception ex) { _log($"intake: material list unavailable: {ex.Message}"); }
        return list;
    }

    public IReadOnlyList<string> Vendors() => VaultFiles.LibraryVendors();

    public IReadOnlyList<string> Subfolders(string vendor) => VaultFiles.Subfolders(vendor);

    public (string Name, string Folder)? FindDuplicate(string vendor, string vendorNumber)
    {
        var f = VaultFiles.FindByVendorNumber(vendor, vendorNumber, _log);
        return f is null ? null : (f.Name, VaultFiles.FolderPathOf(f));
    }

    public string NextPartNumber() => VaultFiles.NextPartNumber();

    public IntakeOutcome Run(ImportPlan plan, string stepPath, string pdfPath)
    {
        var r = ImportPartRunner.Run(_app, plan, stepPath, pdfPath, _log);
        return new IntakeOutcome(r.Ok, r.Cd, r.VaultPath, r.FailedStep, r.Error, r.AlreadyInVault, r.Warnings, r.State);
    }
}
