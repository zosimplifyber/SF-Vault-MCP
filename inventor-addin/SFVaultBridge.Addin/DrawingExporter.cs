using SFVaultBridge.Core;
using Inv = Inventor;

namespace SFVaultBridge.Addin;

/// <summary>Export every sheet of a drawing to PDF with Inventor's PDF translator, invisibly.</summary>
internal static class DrawingExporter
{
    private const string PdfTranslatorId = "{0AC6FD96-2F4D-42CE-8BE0-8AEA580399E4}";

    public static void Export(Inv.Application app, string drawingPath, string outPdf)
    {
        var translator = app.ApplicationAddIns.ItemById[PdfTranslatorId] as Inv.TranslatorAddIn
                         ?? throw new BridgeException("no_pdf_translator", "Inventor's PDF translator add-in is not available.");
        if (!translator.Activated) translator.Activate();
        var silent = app.SilentOperation;
        app.SilentOperation = true;
        Inv.Document? doc = null;
        try
        {
            doc = app.Documents.Open(drawingPath, false);
            var ctx = app.TransientObjects.CreateTranslationContext();
            ctx.Type = Inv.IOMechanismEnum.kFileBrowseIOMechanism;
            var options = app.TransientObjects.CreateNameValueMap();
            if (translator.HasSaveCopyAsOptions[doc, ctx, options])
            {
                options.Value["All_Color_AS_Black"] = 0;
                options.Value["Remove_Line_Weights"] = 0;
                options.Value["Vector_Resolution"] = 400;
                options.Value["Sheet_Range"] = Inv.PrintRangeEnum.kPrintAllSheets;
            }
            var medium = app.TransientObjects.CreateDataMedium();
            medium.FileName = outPdf;
            translator.SaveCopyAs(doc, ctx, options, medium);
            if (!System.IO.File.Exists(outPdf)) throw new BridgeException("export_failed", "Inventor did not write the PDF.");
        }
        finally
        {
            try { doc?.Close(true); } catch { }
            try { app.SilentOperation = silent; } catch { }
        }
    }
}
