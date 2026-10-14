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

    /// <summary>As above; when material is non-empty it is looked up in the active material library and applied. A missing or unappliable material throws BridgeException("material") before the part is saved.</summary>
    public string ImportStepAsPart(string stepPath, string outIpt, IReadOnlyDictionary<string, string> design, IReadOnlyDictionary<string, string> custom,
                                   string? material, ICollection<string>? warnings, string? comments = null, string? titleDescription = null)
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
            if (!string.IsNullOrWhiteSpace(comments))
                doc.PropertySets["Inventor Summary Information"]["Comments"].Value = comments;
            if (titleDescription is not null && design.TryGetValue("Part Number", out var pn))
                doc.PropertySets["Inventor Summary Information"]["Title"].Value = ImportRules.PartTitle(pn, titleDescription);
            var user = doc.PropertySets["Inventor User Defined Properties"];
            foreach (var (name, value) in custom)
            {
                try { user[name].Value = value; }
                catch (Exception ex) when (ex is COMException or ArgumentException) { user.Add(value, name); }
            }

            if (!string.IsNullOrWhiteSpace(material))
            {
                var problem = TryApplyMaterial((Inv.PartDocument)doc, material.Trim());
                if (problem is not null) throw new BridgeException("material", problem);
            }

            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outIpt)!);
            try { doc.SaveAs(outIpt, false); }
            catch (COMException ex) { throw new BridgeException("save_failed", $"Inventor could not save {outIpt}: {ex.Message}"); }
            if (System.IO.File.Exists(outIpt)) return outIpt;

            // Vault's Inventor add-in can apply its own file numbering on save and write the next CD number instead of ours.
            var saved = doc.FullFileName;
            if (string.IsNullOrEmpty(saved) || !System.IO.File.Exists(saved))
                throw new BridgeException("save_failed", $"Inventor did not save {outIpt}. Check for a Vault Data Standard or save dialog.");
            if (!ImportRules.IsVaultRenumberedSave(outIpt, saved))
                throw new BridgeException("save_failed", $"Inventor saved the part as {saved} instead of {outIpt}. It is not in Vault; delete it and retry.");
            var cd = System.IO.Path.GetFileNameWithoutExtension(saved);
            tracking = doc.PropertySets["Design Tracking Properties"];
            tracking["Part Number"].Value = cd;
            if (titleDescription is not null)
                doc.PropertySets["Inventor Summary Information"]["Title"].Value = ImportRules.PartTitle(cd, titleDescription);
            doc.Save();
            warnings?.Add($"Vault numbered the part {cd} on save (the tool had reserved {System.IO.Path.GetFileNameWithoutExtension(outIpt)}).");
            return saved;
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
                if (source is null) return $"Material '{material}' is not in Inventor's active material library; choose another material.";
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
