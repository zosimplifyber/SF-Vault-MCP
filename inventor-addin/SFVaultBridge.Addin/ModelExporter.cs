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

    /// <summary>The McMaster-Carr CAD an assembly STEP must not contain: the component files and the names a leaked one would carry in the STEP.</summary>
    public sealed record McMasterGuard(IReadOnlySet<string> Files, IReadOnlyList<string> Names);

    /// <summary>The assembly cannot be exported without McMaster-Carr CAD; the message says why. The caller lists it on the cover.</summary>
    public sealed class McMasterStepBlocked : Exception
    {
        public McMasterStepBlocked(string reason) : base(reason) { }
    }

    private static string Norm(string? p)
    {
        try { return System.IO.Path.GetFullPath(p ?? ""); } catch { return p ?? ""; }
    }

    /// <summary>The leaf occurrences (at any depth) whose file is McMaster-Carr CAD. Read only.</summary>
    private static List<Inv.ComponentOccurrence> McMasterOccurrences(Inv.AssemblyDocument asm, McMasterGuard guard, Action<string> log)
    {
        var found = new List<Inv.ComponentOccurrence>();
        var def = (Inv.AssemblyComponentDefinition)asm.ComponentDefinition;
        bool Is(Inv.ComponentOccurrence o)
        {
            try
            {
                var file = o.ReferencedDocumentDescriptor.FullDocumentName;
                return McMasterRule.IsMcMasterPath(file) || guard.Files.Contains(Norm(file));
            }
            catch (Exception ex) { log($"STEP guard could not read an occurrence's file: {ex.Message}"); return false; }
        }
        foreach (Inv.ComponentOccurrence o in def.Occurrences.AllLeafOccurrences)
            if (Is(o)) found.Add(o);
        foreach (Inv.ComponentOccurrence o in def.Occurrences)
            if (!found.Contains(o) && Is(o)) found.Add(o);
        return found;
    }

    /// <summary>
    /// Assembly STEP without McMaster-Carr CAD (supplier terms). Approach: when the assembly contains McMaster components, they are suppressed
    /// in an invisible in-memory copy of the document that this call opened (never saved, closed with SkipSave), the STEP is written, and the
    /// STEP's product names are checked for any McMaster part. An assembly the user has open in Inventor is never touched: it is left out instead.
    /// Any doubt (suppress failed, name found in the STEP) deletes the STEP and throws <see cref="McMasterStepBlocked"/>.
    /// </summary>
    public static void Step(Inv.Application app, string modelPath, string outStp, Action<string> log, McMasterGuard? guard)
    {
        if (guard is null || (guard.Files.Count == 0 && guard.Names.Count == 0)) { Step(app, modelPath, outStp, log); return; }
        var translator = FindTranslator(app, StepTranslatorId, log);
        if (translator is null) throw new BridgeException("no_step_translator", "Inventor's STEP translator add-in is not available.");
        if (!translator.Activated) translator.Activate();
        using var d = DocumentOpener.Open(app, modelPath);
        if (d.Document is not Inv.AssemblyDocument asm) { WriteStep(app, translator, d.Document, outStp, log); return; }
        var hits = McMasterOccurrences(asm, guard, log);
        if (hits.Count == 0) { WriteStep(app, translator, d.Document, outStp, log); return; }
        if (!d.OpenedHere)
            throw new McMasterStepBlocked("it is open in Inventor and its McMaster-Carr components cannot be removed without changing it; close it and rebuild");
        log($"STEP guard: suppressing {hits.Count} McMaster-Carr occurrence(s) in an invisible copy of {System.IO.Path.GetFileName(modelPath)} (not saved)");
        foreach (var o in hits)
        {
            try { o.Suppress(); }
            catch (Exception ex) { throw new McMasterStepBlocked("a McMaster-Carr component could not be suppressed: " + ex.Message); }
            bool suppressed;
            try { suppressed = o.Suppressed; } catch { suppressed = false; }
            if (!suppressed) throw new McMasterStepBlocked("a McMaster-Carr component could not be suppressed");
        }
        WriteStep(app, translator, d.Document, outStp, log);
        IReadOnlyList<string> leaks;
        try { leaks = McMasterRule.FindLeaks(System.IO.File.ReadLines(outStp), guard.Names); }
        catch (Exception ex)
        {
            try { System.IO.File.Delete(outStp); } catch { }
            throw new McMasterStepBlocked("the STEP could not be checked for McMaster-Carr parts: " + ex.Message);
        }
        if (leaks.Count > 0)
        {
            try { System.IO.File.Delete(outStp); } catch { }
            throw new McMasterStepBlocked("the STEP still named McMaster-Carr parts: " + string.Join(", ", leaks));
        }
    }

    private static void WriteStep(Inv.Application app, Inv.TranslatorAddIn translator, Inv.Document doc, string outStp, Action<string> log)
    {
        var ctx = app.TransientObjects.CreateTranslationContext();
        ctx.Type = Inv.IOMechanismEnum.kFileBrowseIOMechanism;
        var options = app.TransientObjects.CreateNameValueMap();
        if (translator.HasSaveCopyAsOptions[doc, ctx, options])
        {
            options.Value["ApplicationProtocolType"] = 3; // AP214
            LogOptions(options, "STEP", log);
        }
        var medium = app.TransientObjects.CreateDataMedium();
        medium.FileName = outStp;
        translator.SaveCopyAs(doc, ctx, options, medium);
        if (!System.IO.File.Exists(outStp)) throw new BridgeException("export_failed", "Inventor did not write the STEP file.");
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
