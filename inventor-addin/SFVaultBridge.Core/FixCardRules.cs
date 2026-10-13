using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace SFVaultBridge.Core;

/// <summary>One editable data card field on the Fix Data Card form.</summary>
public sealed record FixCardField(string Label, PropRef Prop);

/// <summary>What kind of document is active in Inventor when Fix Data Card is clicked.</summary>
public enum FixCardDocKind { None, Part, Assembly, Other }

/// <summary>The parts the form will open with. Message is set (and Files empty) when the form must not open.</summary>
public sealed record FixCardPick(IReadOnlyList<string> Files, IReadOnlyList<string> Skipped, string? Message);

/// <summary>A part as read for the form: Vault facts and current values. Problem is set when it cannot be fixed or read (shown greyed).</summary>
public sealed record FixCardLoaded(string VaultPath, string Name, string Category, string State, bool Released,
                                   IReadOnlyDictionary<PropRef, string?> Current, string? Problem);

/// <summary>Per-row status after a preview or an apply.</summary>
public sealed record FixCardOutcome(string File, string Status, string Detail, string RevisionBefore = "", string RevisionAfter = "",
                                    string StateAfter = "", string Version = "")
{
    public const string WouldChange = "would change";
    public const string Fixed = "fixed";
    public const string Skipped = "skipped";
    public const string Error = "error";
    public const string NoChange = "no change";
}

/// <summary>Request built from the form values for one file. Request is null when nothing changed or an input is invalid (Errors says why).</summary>
public sealed record FixCardBuild(DataCardRequest? Request, IReadOnlyList<string> Errors);

/// <summary>Pure rules behind the Fix Data Card ribbon button: which parts, the proposed values, changed-fields-only requests, result text.</summary>
public static class FixCardRules
{
    public const string SelectPartsMessage = "Select the parts to fix in the browser, then click Fix Data Card.";
    public const string OpenPartMessage = "Open a part, or select parts in an assembly.";
    public const string SavePartMessage = "Save the part and check it in to Vault first.";
    public const string ReleasedSkipped = "Released: skipped (tick Revise and re-release Released parts to include it)";
    public const string BuySource = "Buy";
    public const string MakeSource = "Make";

    public static readonly PropRef PartNumberProp = new(PropSet.Design, DataCardFix.PartNumber);
    public static readonly PropRef TitleProp = new(PropSet.Summary, "Title");
    public static readonly PropRef DescriptionProp = new(PropSet.Design, "Description");
    public static readonly PropRef VendorProp = new(PropSet.Design, "Vendor");
    public static readonly PropRef VendorNumberProp = new(PropSet.Custom, "Vendor Number");
    public static readonly PropRef SourceProp = new(PropSet.Custom, "Source");
    public static readonly PropRef ProcessProp = new(PropSet.Custom, "Process");
    public static readonly PropRef FinishProp = new(PropSet.Custom, "Material Finish");
    public static readonly PropRef CostProp = new(PropSet.Design, "Cost");

    /// <summary>The form's fields, in column order.</summary>
    public static readonly IReadOnlyList<FixCardField> Fields = new[]
    {
        new FixCardField("Part Number", PartNumberProp),
        new FixCardField("Title", TitleProp),
        new FixCardField("Description", DescriptionProp),
        new FixCardField("Vendor", VendorProp),
        new FixCardField("Vendor Number", VendorNumberProp),
        new FixCardField("Source", SourceProp),
        new FixCardField("Process", ProcessProp),
        new FixCardField("Material Finish", FinishProp),
        new FixCardField("Estimated Cost", CostProp),
    };

    public static IReadOnlyList<PropRef> AllProps => Fields.Select(f => f.Prop).ToList();

    // ---- Which parts ----

    private static string Norm(string p) => p.Trim().Replace('/', '\\');

    /// <summary>Part: that part. Assembly: the selected components, deduped by file, .iam and non-.ipt listed as skipped. Otherwise a message.
    /// selected holds each selected component's file path (null or blank for a component without a file, such as a virtual one).</summary>
    public static FixCardPick Pick(FixCardDocKind kind, string? activeFile, IEnumerable<string?> selected)
    {
        var none = Array.Empty<string>();
        switch (kind)
        {
            case FixCardDocKind.Part:
                return string.IsNullOrWhiteSpace(activeFile) ? new FixCardPick(none, none, SavePartMessage)
                                                             : new FixCardPick(new[] { activeFile.Trim() }, none, null);
            case FixCardDocKind.Assembly:
                break;
            default:
                return new FixCardPick(none, none, OpenPartMessage);
        }
        var files = new List<string>();
        var skipped = new List<string>();
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenSkipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var any = false;
        foreach (var raw in selected)
        {
            any = true;
            if (string.IsNullOrWhiteSpace(raw))
            {
                skipped.Add("a component without a file (virtual)");
                continue;
            }
            var path = raw.Trim();
            var key = Norm(path);
            var name = Path.GetFileName(key);
            var ext = Path.GetExtension(name);
            if (ext.Equals(".ipt", StringComparison.OrdinalIgnoreCase)) { if (seenFiles.Add(key)) files.Add(path); continue; }
            if (!seenSkipped.Add(key)) continue;
            skipped.Add(ext.Equals(".iam", StringComparison.OrdinalIgnoreCase) ? $"{name} (.iam not supported yet)" : $"{name} (not an .ipt file)");
        }
        if (!any) return new FixCardPick(none, none, SelectPartsMessage);
        if (files.Count == 0)
            return new FixCardPick(none, skipped, "No parts to fix in the selection. Skipped: " + string.Join("; ", skipped) + ".");
        return new FixCardPick(files, skipped, null);
    }

    // ---- Read ----

    /// <summary>Finds the file and reads every field through the same host the HTTP operation uses (Vault facts, then Inventor reads the
    /// downloaded baseline). Never changes anything. A file that could not be read or may not be fixed carries Problem.</summary>
    public static FixCardLoaded Load(IDataCardHost host, string vaultPath)
    {
        var empty = new Dictionary<PropRef, string?>();
        var name = Path.GetFileName(vaultPath.Replace('\\', '/'));
        try
        {
            var matches = host.Find(DataCardFix.ParseFile(vaultPath));
            if (matches.Count == 0) return new FixCardLoaded(vaultPath, name, "", "", false, empty, $"{name} was not found in Vault.");
            if (matches.Count > 1) return new FixCardLoaded(vaultPath, name, "", "", false, empty, $"{name} matches {matches.Count} files in Vault.");
            var info = matches[0];
            // reviseReleased is true here so a Released file is still read and shown; whether it is changed is decided at preview and apply.
            var problem = DataCardFix.SkipReason(info, true);
            if (problem is not null) return new FixCardLoaded(info.VaultPath, info.Name, info.Category, info.State, DataCardFix.IsReleased(info), empty, problem);
            try
            {
                var current = host.Read(info, AllProps);
                return new FixCardLoaded(info.VaultPath, info.Name, info.Category, info.State, DataCardFix.IsReleased(info), current, null);
            }
            catch (Exception ex) when (ex is not BridgeException { Code: "not_signed_in" })
            {
                return new FixCardLoaded(info.VaultPath, info.Name, info.Category, info.State, DataCardFix.IsReleased(info), empty, ex.Message);
            }
        }
        catch (BridgeException ex) when (ex.Code == "not_signed_in") { throw; }
        catch (Exception ex) { return new FixCardLoaded(vaultPath, name, "", "", false, empty, ex.Message); }
    }

    // ---- Proposed values ----

    public static bool IsPurchasedCategory(string? category) =>
        (category ?? "").Contains("purchased", StringComparison.OrdinalIgnoreCase);

    /// <summary>Estimated Cost as shown: invariant, 4 decimals. A value that is not a number is shown as it is.</summary>
    public static string FormatCost(string? text)
    {
        if (!DataCardFix.TryParseNumber(text, out var d)) return (text ?? "").Trim();
        return DataCardFix.RoundCurrency(d).ToString("F4", CultureInfo.InvariantCulture);
    }

    private static string Val(IReadOnlyDictionary<PropRef, string?> current, PropRef p) =>
        current.TryGetValue(p, out var v) ? v ?? "" : "";

    /// <summary>The proposed "new" value per field: Part Number from the file name; Title as PartTitle for Buy or purchased parts, else unchanged;
    /// Vendor in the data card spelling; everything else as it is now.</summary>
    public static IReadOnlyDictionary<PropRef, string> Propose(string fileName, string? category, IReadOnlyDictionary<PropRef, string?> current)
    {
        var o = new Dictionary<PropRef, string>();
        foreach (var f in Fields) o[f.Prop] = Val(current, f.Prop);
        var pn = Path.GetFileNameWithoutExtension(fileName ?? "");
        if (pn.Length > 0) o[PartNumberProp] = pn;
        var buy = string.Equals(Val(current, SourceProp).Trim(), BuySource, StringComparison.OrdinalIgnoreCase);
        if ((buy || IsPurchasedCategory(category)) && pn.Length > 0)
            o[TitleProp] = ImportRules.PartTitle(pn, Val(current, DescriptionProp));
        var vendor = Val(current, VendorProp).Trim();
        if (vendor.Length > 0) o[VendorProp] = ImportRules.DataCardVendor(vendor);
        o[CostProp] = FormatCost(Val(current, CostProp));
        return o;
    }

    // ---- Changed fields only ----

    /// <summary>True when the edited text means a change from the current value (Estimated Cost is compared as a number, rounded to 4 decimals).</summary>
    public static bool Differs(PropRef prop, string? current, string? edited)
    {
        var cur = current ?? "";
        var ed = edited ?? "";
        if (prop == CostProp)
        {
            var hasCur = DataCardFix.TryParseNumber(cur, out var c);
            var hasEd = DataCardFix.TryParseNumber(ed, out var e);
            if (!hasCur && !hasEd) return !string.Equals(cur.Trim(), ed.Trim(), StringComparison.Ordinal);
            if (hasCur && hasEd) return DataCardFix.RoundCurrency(c) != DataCardFix.RoundCurrency(e);
            return true;
        }
        if (string.Equals(cur, ed, StringComparison.Ordinal)) return false;
        return !string.Equals(cur, ImportRules.CleanText(ed), StringComparison.Ordinal);
    }

    /// <summary>The fix request for one file with only the fields whose new value differs from the current one. Vendor goes through the data
    /// card spelling. Invalid input (blank Part Number, Estimated Cost not a number, blank or negative) is reported, never sent.</summary>
    public static FixCardBuild BuildRequest(string vaultPath, IReadOnlyDictionary<PropRef, string?> current,
                                            IReadOnlyDictionary<PropRef, string> edited, bool reviseReleased, bool dryRun, string? comment = null)
    {
        var design = new Dictionary<string, string>();
        var custom = new Dictionary<string, string>();
        var summary = new Dictionary<string, string>();
        var errors = new List<string>();
        foreach (var f in Fields)
        {
            if (!edited.TryGetValue(f.Prop, out var raw)) continue;
            var cur = current.TryGetValue(f.Prop, out var c) ? c : null;
            string send;
            if (f.Prop == CostProp)
            {
                if (!Differs(f.Prop, cur, raw)) continue;
                if (string.IsNullOrWhiteSpace(raw)) { errors.Add("Estimated Cost cannot be blank."); continue; }
                if (!DataCardFix.TryParseNumber(raw, out var d)) { errors.Add("Estimated Cost must be a number."); continue; }
                if (d < 0) { errors.Add("Estimated Cost cannot be negative."); continue; }
                send = DataCardFix.FormatNumber(DataCardFix.RoundCurrency(d));
            }
            else
            {
                var text = f.Prop == VendorProp && !string.Equals(raw, cur ?? "", StringComparison.Ordinal) && ImportRules.CleanText(raw).Length > 0 ? ImportRules.DataCardVendor(ImportRules.CleanText(raw)) : raw;
                if (!Differs(f.Prop, cur, text)) continue;
                send = ImportRules.CleanText(text);
                if (f.Prop == PartNumberProp && send.Length == 0) { errors.Add("Part Number cannot be blank."); continue; }
            }
            var target = f.Prop.Set switch { PropSet.Design => design, PropSet.Summary => summary, _ => custom };
            target[f.Prop.Name] = send;
        }
        if (errors.Count > 0) return new FixCardBuild(null, errors);
        if (design.Count + custom.Count + summary.Count == 0) return new FixCardBuild(null, Array.Empty<string>());
        var req = new DataCardRequest(new[] { vaultPath }, design, custom, false, dryRun, comment, reviseReleased, summary);
        return new FixCardBuild(req, Array.Empty<string>());
    }

    // ---- Run ----

    /// <summary>Runs rows one at a time through DataCardFix.Run (the same code as the HTTP operation), sharing the revise stop and the sign-in stop
    /// across rows. Use one instance per Preview or Apply click.</summary>
    public sealed class Batch
    {
        private readonly IDataCardHost _host;
        private readonly Action<string>? _log;
        private readonly bool _revise;
        private readonly DataCardFix.RunState _state = new();
        private FixCardOutcome? _signInStop;

        public Batch(IDataCardHost host, bool reviseReleased, Action<string>? log = null)
        {
            _host = host; _revise = reviseReleased; _log = log;
        }

        public FixCardOutcome Run(FixCardLoaded row, IReadOnlyDictionary<PropRef, string> edited, bool dryRun, string? comment = null)
        {
            if (row.Problem is not null) return new FixCardOutcome(row.VaultPath, FixCardOutcome.Skipped, row.Problem);
            var build = BuildRequest(row.VaultPath, row.Current, edited, _revise, dryRun, comment);
            if (build.Errors.Count > 0) return new FixCardOutcome(row.VaultPath, FixCardOutcome.Error, "input: " + string.Join(" ", build.Errors));
            if (build.Request is null) return new FixCardOutcome(row.VaultPath, FixCardOutcome.NoChange, "");
            if (row.Released && !_revise) return new FixCardOutcome(row.VaultPath, FixCardOutcome.Skipped, ReleasedSkipped);
            if (_signInStop is not null) return _signInStop with { File = row.VaultPath };
            var result = DataCardFix.Run(build.Request, _host, _log, _state);
            var first = result["results"] is JsonArray a && a.Count > 0 ? a[0] as JsonObject : null;
            var outcome = first is null ? new FixCardOutcome(row.VaultPath, FixCardOutcome.Error, "unexpected: no result") : Describe(first);
            if (outcome.Status == FixCardOutcome.Error && first?["step"]?.GetValue<string>() == "sign_in") _signInStop = outcome;
            return outcome;
        }
    }

    // ---- Result text ----

    private static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?.ToString() ?? "";

    /// <summary>A Run result entry as a row status and one line of detail.</summary>
    public static FixCardOutcome Describe(JsonObject r)
    {
        var file = Str(r["file"]);
        var status = Str(r["status"]);
        var before = Str(r["revision_before"]);
        var after = Str(r["revision_after"]);
        var stateAfter = Str(r["state_after"]);
        var version = Str(r["version"]);
        var changes = r["changes"] is JsonObject co
            ? string.Join("; ", co.Select(kv => $"{kv.Key}: {Str(kv.Value?["from"])} -> {Str(kv.Value?["to"])}"))
            : "";
        var detail = new StringBuilder();
        string label;
        switch (status)
        {
            case "preview":
                label = FixCardOutcome.WouldChange;
                detail.Append(changes);
                if (r["would_revise"]?.GetValue<bool>() == true) detail.Append(" (Released: would move to Work in Progress, edit, and return to Released)");
                if (r["linked_to_item"]?.GetValue<bool>() == true) detail.Append(" (linked to an item)");
                break;
            case "fixed":
                label = FixCardOutcome.Fixed;
                detail.Append(version.Length > 0 ? $"new version {version}" : "checked in");
                if (before.Length > 0 || after.Length > 0) detail.Append($"; revision {before} -> {after}");
                if (stateAfter.Length > 0) detail.Append($"; state {stateAfter}");
                if (Str(r["warning"]).Length > 0) detail.Append("; " + Str(r["warning"]));
                break;
            case "skipped":
                label = FixCardOutcome.Skipped;
                detail.Append(Str(r["reason"]));
                break;
            case "no_change":
                label = FixCardOutcome.NoChange;
                break;
            default:
                label = FixCardOutcome.Error;
                var step = Str(r["step"]);
                detail.Append(step.Length > 0 ? $"{step}: {Str(r["message"])}" : Str(r["message"]));
                if (stateAfter.Length > 0) detail.Append($" (state now {stateAfter})");
                break;
        }
        return new FixCardOutcome(file, label, detail.ToString().Trim(), before, after, stateAfter, version);
    }

    private static string Cell(string? s) => (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    /// <summary>Tab-separated summary for the clipboard.</summary>
    public static string ResultsText(IEnumerable<FixCardOutcome> rows)
    {
        var sb = new StringBuilder();
        sb.Append("File\tStatus\tDetail\tRevision before\tRevision after\tState after\tVersion\r\n");
        foreach (var r in rows)
            sb.Append(string.Join("\t", new[] { r.File, r.Status, r.Detail, r.RevisionBefore, r.RevisionAfter, r.StateAfter, r.Version }.Select(Cell))).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>The Source dropdown items: Buy, Make, plus the current value when it is something else.</summary>
    public static IReadOnlyList<string> SourceChoices(string? current) => WithCurrent(new[] { BuySource, MakeSource }, current);

    /// <summary>The Process dropdown items: blank, the settings values, plus the current value when it is not among them.</summary>
    public static IReadOnlyList<string> ProcessChoices(IEnumerable<string> settingsValues, string? current) =>
        WithCurrent(new[] { "" }.Concat(settingsValues.Where(v => !string.IsNullOrWhiteSpace(v))), current);

    private static IReadOnlyList<string> WithCurrent(IEnumerable<string> items, string? current)
    {
        var list = items.ToList();
        var cur = current ?? "";
        if (!list.Contains(cur, StringComparer.Ordinal)) list.Add(cur);
        return list;
    }
}
