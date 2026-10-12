using SFVaultBridge.Core;
using Inv = Inventor;

namespace SFVaultBridge.Addin;

/// <summary>Export every sheet of a drawing to PDF with Inventor's PDF translator, invisibly.</summary>
internal static class DrawingExporter
{
    private const string PdfTranslatorId = "{0AC6FD96-2F4D-42CE-8BE0-8AEA580399E4}";

    /// <summary>The document Inventor already has open at this path, or null.</summary>
    public static Inv.Document? FindOpen(Inv.Application app, string path)
    {
        var want = Normalize(path);
        foreach (Inv.Document d in app.Documents)
        {
            try { if (string.Equals(Normalize(d.FullFileName), want, StringComparison.OrdinalIgnoreCase)) return d; }
            catch { }
        }
        return null;
    }

    private static string Normalize(string p)
    {
        try { return System.IO.Path.GetFullPath(p); } catch { return p; }
    }

    private static HashSet<string> OpenIds(Inv.Application app)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Inv.Document d in app.Documents)
        {
            try { ids.Add(d.InternalName); } catch { }
        }
        return ids;
    }

    /// <summary>Reuses a drawing the user has open (never closed); closes only a document this call opened.</summary>
    public static void Export(Inv.Application app, string drawingPath, string outPdf)
    {
        var translator = app.ApplicationAddIns.ItemById[PdfTranslatorId] as Inv.TranslatorAddIn
                         ?? throw new BridgeException("no_pdf_translator", "Inventor's PDF translator add-in is not available.");
        if (!translator.Activated) translator.Activate();
        var silent = app.SilentOperation;
        app.SilentOperation = true;
        Inv.Document? doc = null;
        Inv.Document? opened = null;
        try
        {
            doc = FindOpen(app, drawingPath);
            if (doc is null)
            {
                var before = OpenIds(app);
                doc = app.Documents.Open(drawingPath, false);
                bool wasOpen;
                try { wasOpen = before.Contains(doc.InternalName); } catch { wasOpen = true; }
                if (!wasOpen) opened = doc;
            }
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
            try { opened?.Close(true); } catch { }
            try { app.SilentOperation = silent; } catch { }
        }
    }
}
