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
}
