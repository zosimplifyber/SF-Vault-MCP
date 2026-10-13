using SFVaultBridge.Core;
using Inv = Inventor;

namespace SFVaultBridge.Addin;

internal sealed record PrintFormat(Inv.TranslatorAddIn Translator, string Extension);

/// <summary>STEP, flat pattern DXF and 3D print (3MF, else STL) exports through Inventor's translators, invisibly.</summary>
internal static class ModelExporter
{
    public const string StepTranslatorId = "{90AF7F40-0C01-11D5-8E83-0010B541CD80}";
    public const string StlTranslatorId = "{533E9A98-FC3B-11D4-8E7E-0010B541CD80}";

    private static readonly HashSet<string> LoggedOptionSets = new(StringComparer.Ordinal);
    private static readonly object LogLock = new();

    /// <summary>The translator with this id, or null when Inventor has none (ItemById can throw for an unknown id).</summary>
    private static Inv.TranslatorAddIn? FindTranslator(Inv.Application app, string id, Action<string> log)
    {
        try { return app.ApplicationAddIns.ItemById[id] as Inv.TranslatorAddIn; }
        catch (Exception ex)
        {
            log($"Translator {id} lookup failed: {ex.Message}");
            return null;
        }
    }

    public static void Step(Inv.Application app, string modelPath, string outStp, Action<string> log)
    {
        var translator = FindTranslator(app, StepTranslatorId, log);
        if (translator is null)
        {
            log($"STEP translator {StepTranslatorId} not found.");
            throw new BridgeException("no_step_translator", "Inventor's STEP translator add-in is not available.");
        }
        log($"STEP translator: {translator.DisplayName} {StepTranslatorId}");
        if (!translator.Activated) translator.Activate();
        using var d = DocumentOpener.Open(app, modelPath);
        var ctx = app.TransientObjects.CreateTranslationContext();
        ctx.Type = Inv.IOMechanismEnum.kFileBrowseIOMechanism;
        var options = app.TransientObjects.CreateNameValueMap();
        if (translator.HasSaveCopyAsOptions[d.Document, ctx, options])
        {
            options.Value["ApplicationProtocolType"] = 3; // AP214
            LogOptions(options, "STEP", log);
        }
        var medium = app.TransientObjects.CreateDataMedium();
        medium.FileName = outStp;
        translator.SaveCopyAs(d.Document, ctx, options, medium);
        if (!System.IO.File.Exists(outStp)) throw new BridgeException("export_failed", "Inventor did not write the STEP file.");
    }

    /// <summary>Writes the part's existing flat pattern; false when it has none. Never creates one.</summary>
    public static bool FlatPatternDxf(Inv.Application app, string partPath, string outDxf, Action<string> log)
    {
        using var d = DocumentOpener.Open(app, partPath);
        if (d.Document is not Inv.PartDocument p || p.ComponentDefinition is not Inv.SheetMetalComponentDefinition smcd) return false;
        if (!smcd.HasFlatPattern) return false;
        smcd.DataIO.WriteDataToFile("FLAT PATTERN DXF?AcadVersion=2018&OuterProfileLayer=Outer", outDxf);
        if (!System.IO.File.Exists(outDxf)) throw new BridgeException("export_failed", "Inventor did not write the DXF.");
        return true;
    }

    /// <summary>3MF when Inventor has a translator whose name says so (found by display name, never by a guessed id), else STL.</summary>
    public static PrintFormat ResolvePrintFormat(Inv.Application app, Action<string> log)
    {
        foreach (Inv.ApplicationAddIn a in app.ApplicationAddIns)
        {
            try
            {
                if (a is Inv.TranslatorAddIn t && a.DisplayName.Contains("3MF", StringComparison.OrdinalIgnoreCase))
                {
                    log($"3MF translator: {a.DisplayName} {a.ClassIdString}");
                    return new PrintFormat(t, ".3mf");
                }
            }
            catch { }
        }
        log("No 3MF translator found among Inventor's add-ins; falling back to STL.");
        var stl = FindTranslator(app, StlTranslatorId, log);
        if (stl is not null) return new PrintFormat(stl, ".stl");
        throw new BridgeException("no_print_translator", "Inventor has neither a 3MF nor an STL translator.");
    }

    public static void Print(Inv.Application app, PrintFormat format, string partPath, string outFile, Action<string> log)
    {
        var translator = format.Translator;
        if (!translator.Activated) translator.Activate();
        using var d = DocumentOpener.Open(app, partPath);
        var ctx = app.TransientObjects.CreateTranslationContext();
        ctx.Type = Inv.IOMechanismEnum.kFileBrowseIOMechanism;
        var options = app.TransientObjects.CreateNameValueMap();
        if (translator.HasSaveCopyAsOptions[d.Document, ctx, options])
        {
            if (format.Extension == ".stl")
            {
                options.Value["OutputFileType"] = 0; // binary
                options.Value["Resolution"] = 0;     // high
            }
            LogOptions(options, format.Extension.TrimStart('.').ToUpperInvariant(), log);
        }
        var medium = app.TransientObjects.CreateDataMedium();
        medium.FileName = outFile;
        translator.SaveCopyAs(d.Document, ctx, options, medium);
        if (!System.IO.File.Exists(outFile)) throw new BridgeException("export_failed", $"Inventor did not write the {format.Extension} file.");
    }

    /// <summary>Logs the translator's option names once per kind, so the live values (AP214, STL flags) can be confirmed.</summary>
    private static void LogOptions(Inv.NameValueMap options, string kind, Action<string> log)
    {
        lock (LogLock) { if (!LoggedOptionSets.Add(kind)) return; }
        try
        {
            var items = new List<string>();
            for (var i = 1; i <= options.Count; i++)
            {
                var name = options.Name[i];
                string value;
                try { value = Convert.ToString(options.Value[name]) ?? ""; } catch { value = "?"; }
                items.Add($"{name}={value}");
            }
            log($"{kind} translator options: {string.Join(", ", items)}");
        }
        catch (Exception ex) { log($"{kind} translator options unreadable: {ex.Message}"); }
    }
}
