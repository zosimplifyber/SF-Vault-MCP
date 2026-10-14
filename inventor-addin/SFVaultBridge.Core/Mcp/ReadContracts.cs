namespace SFVaultBridge.Core;

/// <summary>How a tool names a Vault file: a bare name ("CD-001909.ipt" or "CD-001909"), a full "$/" path, or the numeric file_id a search returned.</summary>
public sealed record VaultFileRef(string? Text, long? FileId)
{
    public override string ToString() => FileId is { } id ? $"file_id {id}" : Text ?? "";
}

/// <summary>One Vault file version as the read tools report it.</summary>
public sealed record VaultFileSummary(long FileId, long MasterId, string Name, string Folder, string State, string Revision,
                                      string Category, int Version, int LatestVersion, bool CheckedOut = false, string CheckedOutBy = "")
{
    public string Path => Folder.Length == 0 ? Name : Folder.TrimEnd('/') + "/" + Name;
}

public sealed record VaultSearchResult(IReadOnlyList<VaultFileSummary> Files, bool Truncated);

public sealed record VaultFileDetail(VaultFileSummary File, bool? Released, long SizeBytes, string Modified, string Created, string CreatedBy,
                                     string Comment, IReadOnlyDictionary<string, string> Properties, string? Compliance, bool? LinkedToItem);

/// <summary>A file related to the subject. Type is Dependency or Attachment; Level 1 is the direct neighbour; Via is the neighbour one level closer to the subject.</summary>
public sealed record VaultRelation(VaultFileSummary File, string Type, int Level, string Via, int ReferencedVersion);

public sealed record VaultRelations(VaultFileSummary Subject, IReadOnlyList<VaultRelation> Rows, bool Truncated);

public sealed record VaultVersionInfo(long FileId, int Version, string Revision, string State, string Comment, string Modified, string CheckedInBy);

public sealed record VaultVersions(VaultFileSummary Subject, IReadOnlyList<VaultVersionInfo> Versions, bool Truncated);

/// <summary>Read-only Vault queries on the signed-in session. Every member throws BridgeException("not_signed_in") when Inventor's Vault add-in is not signed in,
/// "not_found" when a file reference matches nothing and "ambiguous" when a bare name matches several files.</summary>
public interface IVaultReader
{
    /// <summary>Latest versions whose file name or common data-card properties contain the text, at most limit.</summary>
    VaultSearchResult SearchFiles(string query, int limit);

    VaultFileDetail GetFile(VaultFileRef file);

    /// <summary>Files that use the subject, level by level up to levels, at most maxRows rows.</summary>
    VaultRelations WhereUsed(VaultFileRef file, int levels, int maxRows);

    /// <summary>Files the subject uses (children, with the version each parent references), level by level up to levels, at most maxRows rows.</summary>
    VaultRelations Uses(VaultFileRef file, int levels, int maxRows);

    VaultVersions Versions(VaultFileRef file, int limit);
}

public sealed record OpenBomResult(string Assembly, string FileName, string SourceLine, IReadOnlyList<BomNode> Roots);

/// <summary>What the tools need from the running Inventor.</summary>
public interface IInventorReader
{
    /// <summary>The active assembly's structured BOM with Vault facts applied. Throws BridgeException("no_assembly") when the active document is not an assembly.</summary>
    OpenBomResult OpenBom();

    /// <summary>Name and kind of the active document, or null when none is open.</summary>
    string? ActiveDocument();
}

/// <summary>Chooses the one file a bare name means.</summary>
public static class FileRefRules
{
    /// <summary>The "$/" path, bare name, or numeric id a tool argument carries. Exactly one of file and file_id.</summary>
    public static VaultFileRef Parse(System.Text.Json.Nodes.JsonObject args)
    {
        var text = args["file"] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null;
        long? id = null;
        if (args["file_id"] is System.Text.Json.Nodes.JsonValue iv)
        {
            if (!iv.TryGetValue<long>(out var l) || l <= 0) throw new BridgeException("bad_request", "'file_id' must be a positive integer from a search result.");
            id = l;
        }
        if (string.IsNullOrEmpty(text) && id is null) throw new BridgeException("bad_request", "Give 'file' (a file name or $/ path) or 'file_id'.");
        if (!string.IsNullOrEmpty(text) && id is not null) throw new BridgeException("bad_request", "Give 'file' or 'file_id', not both.");
        return new VaultFileRef(string.IsNullOrEmpty(text) ? null : text, id);
    }

    public static bool IsPath(string text) => text.StartsWith("$/", StringComparison.Ordinal);

    /// <summary>Picks from candidates (already one per file) by exact name, else by name without extension when the text has no extension.
    /// Throws not_found for none and ambiguous (naming up to 10 paths) for several.</summary>
    public static T Pick<T>(string text, IEnumerable<T> candidates, Func<T, string> nameOf, Func<T, string> pathOf)
    {
        var all = candidates.ToList();
        var exact = all.Where(c => string.Equals(nameOf(c), text, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 0 && Path.GetExtension(text).Length == 0)
            exact = all.Where(c => string.Equals(Path.GetFileNameWithoutExtension(nameOf(c)), text, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 0) throw new BridgeException("not_found", $"No file named {text} in Vault.");
        if (exact.Count == 1) return exact[0];
        var shown = string.Join("; ", exact.Take(10).Select(pathOf));
        throw new BridgeException("ambiguous", $"{text} matches {exact.Count} files: {shown}. Call again with the full $/ path or the file_id.");
    }
}
