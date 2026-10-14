namespace SFVaultBridge.Core;

/// <summary>A Vault folder as the folder tools report it.</summary>
public sealed record VaultFolderInfo(long Id, string Name, string Path, int ChildCount);

public sealed record FolderRef(string? Path, long? Id);

public sealed record FolderListing(VaultFolderInfo Folder, IReadOnlyList<VaultFolderInfo> Subfolders, IReadOnlyList<VaultFileSummary> Files,
                                   bool SubfoldersTruncated, bool FilesTruncated);

public enum SearchOp { Contains, Equals, NotEquals, StartsWith, GreaterThan, LessThan, IsEmpty, IsNotEmpty }

public sealed record SearchCondition(string Property, SearchOp Op, string Value);

/// <summary>The caller's search request after argument parsing (property names not yet resolved).</summary>
public sealed record AdvancedSearchSpec(IReadOnlyList<SearchCondition> Conditions, bool Any, string? FolderPath, bool Recurse, bool LatestOnly, int Limit);

/// <summary>A Vault property definition as the search mapping and the definitions tool need it. DataType is Vault's: String, Numeric, Bool, DateTime, Image.</summary>
public sealed record PropDefLite(long Id, string DispName, string SysName, string DataType, bool Active, bool IsSystem, int UsageCount);

/// <summary>One condition ready for the Vault SrchCond: property definition id, operator code, text, and whether starts_with must be checked after the search.</summary>
public sealed record MappedCondition(long PropDefId, string PropName, int OperatorCode, string Text, bool StartsWith);

public sealed record MappedSearch(IReadOnlyList<MappedCondition> Conditions, bool Any, string? FolderPath, bool Recurse, bool LatestOnly, int Limit);

public sealed record VaultJobInfo(long Id, string Type, string Status, string Message, string Submitter, string Created, string Description,
                                  string FileName, long FileVersionId, int Priority);

/// <summary>The latest version of a drawing a PDF job would be queued for.</summary>
public sealed record PdfTarget(long FileVersionId, long MasterId, string Name, string Path, int Version, string State, string Revision, bool CheckedOut, string CheckedOutBy);

public sealed record LifecycleStateInfo(long Id, string Name, string DisplayName, bool Released, bool Obsolete, bool Default);
public sealed record LifecycleTransitionInfo(long FromId, long ToId);
public sealed record LifecycleDefInfo(string Name, string Description, IReadOnlyList<LifecycleStateInfo> States, IReadOnlyList<LifecycleTransitionInfo> Transitions);

public sealed record CategoryInfoLite(string Name, string Description, IReadOnlyList<string> EntityClasses);

public sealed record RevisionSequenceInfo(string Level, string Name, IReadOnlyList<string> Labels);
public sealed record RevisionSchemeInfo(string Name, string Description, string Delimiter, IReadOnlyList<RevisionSequenceInfo> Sequences);

public sealed record VaultUserInfo(long Id, string Name, string Email, bool Active, string Auth);
public sealed record VaultGroupInfo(long Id, string Name, string Email, bool Active, IReadOnlyList<string> Users, IReadOnlyList<string> ChildGroups);

/// <summary>More read-only Vault queries (folders, advanced search, job queue, definitions, users) plus the one job submission the PDF tool makes.
/// Members throw BridgeException("not_signed_in") when Inventor's Vault add-in is not signed in and "not_found" for a folder or file that does not exist.</summary>
public interface IVaultExtras
{
    VaultFolderInfo FindFolder(string path);
    FolderListing ListFolder(FolderRef folder, bool includeFiles, int limit);

    /// <summary>The file property definitions a search can use.</summary>
    IReadOnlyList<PropDefLite> FileSearchProperties();

    /// <summary>Runs the mapped conditions (ANDed, or ORed when Any) and returns at most Limit files; sets Truncated when more matched.</summary>
    VaultSearchResult Search(MappedSearch search);

    bool JobQueueEnabled();

    /// <summary>Jobs created in the last 30 days, at most max.</summary>
    IReadOnlyList<VaultJobInfo> RecentJobs(int max);

    /// <summary>The latest version of the file, for a PDF job. Not-found and ambiguous work as in the other file tools.</summary>
    PdfTarget ResolvePdfTarget(VaultFileRef file);

    /// <summary>Adds one job to the Vault job queue and returns its id.</summary>
    long SubmitJob(string jobType, string description, IReadOnlyDictionary<string, string> parameters, int priority);

    IReadOnlyList<PropDefLite> PropertyDefinitions(string entityClass);
    IReadOnlyList<CategoryInfoLite> Categories();
    IReadOnlyList<LifecycleDefInfo> Lifecycles();
    IReadOnlyList<RevisionSchemeInfo> RevisionSchemes();
    IReadOnlyList<VaultUserInfo> Users();
    IReadOnlyList<VaultGroupInfo> Groups();
}
