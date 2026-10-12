using System.Runtime.InteropServices;
using SFVaultBridge.Core;
using Inv = Inventor;

namespace SFVaultBridge.Addin;

/// <summary>Opens a STEP as a part, sets iProperties, saves it as .ipt. Same rules as the Python version it replaces.</summary>
internal sealed class StepImporter : IStepImporter
{
    private readonly Inv.Application _app;

    public StepImporter(Inv.Application app) => _app = app;

    public string InventorVersion => _app.SoftwareVersion.DisplayVersion;

    public string ImportStepAsPart(string stepPath, string outIpt, IReadOnlyDictionary<string, string> design, IReadOnlyDictionary<string, string> custom)
        => ImportStepAsPart(stepPath, outIpt, design, custom, null, null);

    /// <summary>As above; when material is non-empty it is looked up in the active material library and applied. A missing or unappliable material adds a line to warnings and never fails the import.</summary>
    public string ImportStepAsPart(string stepPath, string outIpt, IReadOnlyDictionary<string, string> design, IReadOnlyDictionary<string, string> custom,
                                   string? material, ICollection<string>? warnings)
    {
        if (!System.IO.File.Exists(stepPath)) throw new BridgeException("not_found", $"STEP file not found: {stepPath}");
        if (System.IO.File.Exists(outIpt)) throw new BridgeException("exists", $"Refusing to overwrite existing file {outIpt}");

        // Suppress modal dialogs (STEP import options, Vault Data Standard) for the whole open-to-save span.
        var previousSilent = _app.SilentOperation;
        _app.SilentOperation = true;
        Inv.Document? doc = null;
        try
        {
            doc = _app.Documents.Open(stepPath, false);
            if (doc.DocumentType != Inv.DocumentTypeEnum.kPartDocumentObject)
                throw new BridgeException("not_a_part", $"{System.IO.Path.GetFileName(stepPath)} did not open as a single part. Import it by hand.");
            if (doc.File.ReferencedFileDescriptors.Count > 0)
                throw new BridgeException("reference_model", "The STEP opened as a reference model. Set Inventor's STEP import to Convert Model, then retry.");

            var tracking = doc.PropertySets["Design Tracking Properties"];
            foreach (var (name, value) in design) tracking[name].Value = value;
            var user = doc.PropertySets["Inventor User Defined Properties"];
            foreach (var (name, value) in custom)
            {
                try { user[name].Value = value; }
                catch (Exception ex) when (ex is COMException or ArgumentException) { user.Add(value, name); }
            }

            if (!string.IsNullOrWhiteSpace(material))
            {
                var warning = TryApplyMaterial((Inv.PartDocument)doc, material.Trim());
                if (warning is not null) warnings?.Add(warning);
            }

            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outIpt)!);
            doc.SaveAs(outIpt, false);
            if (!System.IO.File.Exists(outIpt))
                throw new BridgeException("save_failed", $"Inventor did not save {outIpt}. Check for a Vault Data Standard or save dialog.");
            return outIpt;
        }
        finally
        {
            try { doc?.Close(true); } catch (COMException) { }
            try { _app.SilentOperation = previousSilent; } catch (COMException) { }
        }
    }

    private string? TryApplyMaterial(Inv.PartDocument part, string material)
    {
        try
        {
            Inv.Asset? found = null;
            foreach (Inv.Asset a in part.MaterialAssets)
                if (string.Equals(a.DisplayName, material, StringComparison.OrdinalIgnoreCase)) { found = a; break; }
            if (found is null)
            {
                Inv.Asset? source = null;
                foreach (Inv.Asset a in _app.ActiveMaterialLibrary.MaterialAssets)
                    if (string.Equals(a.DisplayName, material, StringComparison.OrdinalIgnoreCase)) { source = a; break; }
                if (source is null) return $"Material '{material}' is not in Inventor's active material library; the part keeps its default material.";
                found = source.CopyTo((Inv.Document)part, true);
            }
            part.ActiveMaterial = found;
            return null;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidCastException)
        {
            return $"Could not apply material '{material}': {ex.Message}";
        }
    }
}
