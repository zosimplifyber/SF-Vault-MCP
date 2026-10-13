using System.Text.Json.Nodes;

namespace SFVaultBridge.Core;

public enum PropSet { Design, Custom }

public sealed record PropRef(PropSet Set, string Name);

public sealed record PropChange(PropRef Prop, string? From, string To);

/// <summary>What the Vault says about the latest version of one file.</summary>
public sealed record DataCardFileInfo(string VaultPath, string Name, string Category, string State, bool Released,
                                      bool CheckedOut, string CheckedOutBy, long Version);

/// <summary>A "files" entry: a bare file name or a full $/ Vault path.</summary>
public sealed record DataCardFileRef(string Text, bool IsPath);

/// <summary>Raised by the host when the file must be left alone (for example open and unsaved in Inventor).</summary>
public sealed class DataCardSkipException : Exception
{
    public DataCardSkipException(string reason) : base(reason) { }
}

/// <summary>Raised by the host when a step fails; Step names it (read, checkout, open, set, save, checkin).</summary>
public sealed class DataCardStepException : Exception
{
    public string Step { get; }

    public DataCardStepException(string step, string message) : base(message) => Step = step;
}

/// <summary>The Vault and Inventor side of the data card fix. Core only decides; the host touches the world.</summary>
public interface IDataCardHost
{
    /// <summary>Latest Vault files matching the reference: exact case-insensitive name for a bare name, the path for a $/ path. Empty when none.</summary>
    IReadOnlyList<DataCardFileInfo> Find(DataCardFileRef file);

    /// <summary>Current values (null when the property does not exist). Downloads the latest version without children, opens it invisibly, closes without saving.
    /// Throws DataCardSkipException when the file is open in Inventor with unsaved changes.</summary>
    IReadOnlyDictionary<PropRef, string?> Read(DataCardFileInfo file, IReadOnlyList<PropRef> props);

    /// <summary>Check out, set, save, check in with the comment (undoing the checkout on any failure). Returns the new version number.</summary>
    long Apply(DataCardFileInfo file, IReadOnlyList<PropChange> changes, string comment);
}

public sealed record DataCardRequest(IReadOnlyList<string> Files, IReadOnlyDictionary<string, string> Design,
                                     IReadOnlyDictionary<string, string> Custom, bool PartNumberFromFileName,
                                     bool DryRun, string? Comment);

/// <summary>Rules and orchestration for the fix_data_card bridge operation.</summary>
public static class DataCardFix
{
    public const string PartNumber = "Part Number";
    public const string ContentCenterCategory = "Part - Content Center";
    public const string ContentCenterRoot = "$/Content Center Files";

    public static DataCardFileRef ParseFile(string text)
    {
        var t = (text ?? "").Trim();
        return new DataCardFileRef(t, t.StartsWith("$/", StringComparison.Ordinal));
    }

    public static DataCardRequest ParseRequest(JsonObject body)
    {
        if (body["files"] is not JsonArray arr || arr.Count == 0)
            throw new BridgeException("bad_request", "'files' is required and must be a non-empty list of file names or $/ paths.");
        var files = arr.Select((n, i) => n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)
            ? s.Trim() : throw new BridgeException("bad_request", $"'files[{i}]' must be a non-empty string.")).ToList();
        var comment = Args.OptStr(body, "comment", "").Trim();
        return new DataCardRequest(files, Args.StrMap(body, "design"), Args.StrMap(body, "custom"),
            Args.Bool(body, "partNumberFromFileName", true), Args.Bool(body, "dryRun", true), comment.Length > 0 ? comment : null);
    }

    /// <summary>Why the file may not be fixed, or null when it may.</summary>
    public static string? SkipReason(DataCardFileInfo f)
    {
        var ext = Path.GetExtension(f.Name);
        if (!ext.Equals(".ipt", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".iam", StringComparison.OrdinalIgnoreCase))
            return $"{f.Name} is not an .ipt or .iam file.";
        if (f.CheckedOut)
            return $"{f.Name} is checked out by {(string.IsNullOrWhiteSpace(f.CheckedOutBy) ? "someone" : f.CheckedOutBy)}. Check it in or undo the checkout first.";
        if (f.Released || string.Equals(f.State, "Released", StringComparison.OrdinalIgnoreCase))
            return $"{f.Name} is Released. Change its state before fixing it.";
        if (string.Equals(f.Category, ContentCenterCategory, StringComparison.OrdinalIgnoreCase)
            || f.VaultPath.StartsWith(ContentCenterRoot, StringComparison.OrdinalIgnoreCase))
            return $"{f.Name} is a Content Center file.";
        return null;
    }

    public static IReadOnlyList<PropRef> Targets(DataCardRequest r, string fileName, out IReadOnlyDictionary<PropRef, string> wanted)
    {
        var w = new Dictionary<PropRef, string>();
        foreach (var (k, v) in r.Design) w[new PropRef(PropSet.Design, k)] = v;
        foreach (var (k, v) in r.Custom) w[new PropRef(PropSet.Custom, k)] = v;
        var pn = new PropRef(PropSet.Design, PartNumber);
        if (r.PartNumberFromFileName && !w.ContainsKey(pn)) w[pn] = Path.GetFileNameWithoutExtension(fileName);
        wanted = w;
        return w.Keys.ToList();
    }

    /// <summary>Target values that differ from the current ones; a missing property reads as empty. Equal values are omitted.</summary>
    public static IReadOnlyList<PropChange> ComputeChanges(IReadOnlyDictionary<PropRef, string> wanted, IReadOnlyDictionary<PropRef, string?> current)
    {
        var changes = new List<PropChange>();
        foreach (var (p, to) in wanted)
        {
            current.TryGetValue(p, out var from);
            if (string.Equals((from ?? "").Trim(), to.Trim(), StringComparison.Ordinal)) continue;
            changes.Add(new PropChange(p, from, to));
        }
        return changes;
    }

    public static string DefaultComment(IEnumerable<PropChange> changes) =>
        "Data card fix: " + string.Join(", ", changes.Select(c => c.Prop.Name).Distinct());

    public static JsonObject Run(DataCardRequest req, IDataCardHost host, Action<string>? log = null)
    {
        var results = new JsonArray();
        var counts = new Dictionary<string, int> { ["preview"] = 0, ["fixed"] = 0, ["no_change"] = 0, ["skipped"] = 0, ["error"] = 0 };
        foreach (var arg in req.Files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var r = RunOne(arg, req, host);
            var status = r["status"]!.GetValue<string>();
            counts[status]++;
            var detail = r["reason"] ?? r["message"] ?? r["version"];
            log?.Invoke($"fix_data_card {r["file"]}: {status}" + (detail is null ? "" : " " + detail));
            results.Add(r);
        }
        return new JsonObject
        {
            ["results"] = results,
            ["counts"] = new JsonObject(counts.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value))),
        };
    }

    private static JsonObject RunOne(string arg, DataCardRequest req, IDataCardHost host)
    {
        DataCardFileInfo? info = null;
        try
        {
            var matches = host.Find(ParseFile(arg));
            if (matches.Count == 0) return Skipped(arg, $"{arg} was not found in Vault.");
            if (matches.Count > 1)
                return Error(arg, "resolve", $"{arg} matches {matches.Count} files ({string.Join(", ", matches.Select(m => m.VaultPath))}). Pass the full $/ path.");
            info = matches[0];
            var skip = SkipReason(info);
            if (skip is not null) return Skipped(info.VaultPath, skip);

            var props = Targets(req, info.Name, out var wanted);
            IReadOnlyDictionary<PropRef, string?> current;
            try { current = host.Read(info, props); }
            catch (Exception ex) when (ex is not DataCardSkipException and not DataCardStepException and not BridgeException)
            { throw new DataCardStepException("read", ex.Message); }
            var changes = ComputeChanges(wanted, current);
            if (changes.Count == 0) return new JsonObject { ["file"] = info.VaultPath, ["status"] = "no_change", ["changes"] = new JsonObject() };
            if (req.DryRun)
                return new JsonObject { ["file"] = info.VaultPath, ["status"] = "preview", ["changes"] = ChangesJson(changes) };

            var comment = req.Comment ?? DefaultComment(changes);
            var version = host.Apply(info, changes, comment);
            return new JsonObject { ["file"] = info.VaultPath, ["status"] = "fixed", ["version"] = version, ["changes"] = ChangesJson(changes) };
        }
        catch (DataCardSkipException ex) { return Skipped(info?.VaultPath ?? arg, ex.Message); }
        catch (DataCardStepException ex) { return Error(info?.VaultPath ?? arg, ex.Step, ex.Message); }
        catch (BridgeException ex) when (ex.Code == "not_signed_in") { throw; }
        catch (Exception ex) { return Error(info?.VaultPath ?? arg, "unexpected", ex.Message); }
    }

    private static JsonObject ChangesJson(IEnumerable<PropChange> changes)
    {
        var o = new JsonObject();
        foreach (var c in changes) o[c.Prop.Name] = new JsonObject { ["from"] = c.From, ["to"] = c.To };
        return o;
    }

    private static JsonObject Skipped(string file, string reason) => new() { ["file"] = file, ["status"] = "skipped", ["reason"] = reason };

    private static JsonObject Error(string file, string step, string message) =>
        new() { ["file"] = file, ["status"] = "error", ["step"] = step, ["message"] = message };
}
