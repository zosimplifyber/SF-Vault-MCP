namespace SFVaultBridge.Core;

public sealed record StatusInfo(bool SignedIn, string? User, string? Vault, string? Server);
public sealed record SchemeInfo(long Id, string Name, bool IsDefault);
public sealed record FolderInfo(long Id, string FullName, bool IsLibrary);
public sealed record CategoryInfo(long Id, string Name);
public sealed record VaultFile(long Id, long MasterId, string Name, bool CheckedOut);

/// <summary>Vault work on the signed-in session. Every member except Status throws
/// BridgeException("not_signed_in") when Inventor's Vault add-in is not signed in.</summary>
public interface IVaultSession
{
    StatusInfo Status();
    IReadOnlyList<SchemeInfo> GetNumberingSchemes();
    string GenerateFileNumber(long schemeId);
    FolderInfo? GetFolderByPath(string path);
    FolderInfo AddFolder(string parentPath, string name);
    IReadOnlyList<CategoryInfo> GetFileCategories();
    VaultFile AddFile(string folderPath, string localPath, string fileName, long[] attachmentFileIds, string comment);
    IReadOnlyList<VaultFile> UpdateFileCategories(long[] masterIds, long categoryId, string comment);
}

/// <summary>Inventor work: turn a STEP into a saved .ipt with iProperties set.</summary>
public interface IStepImporter
{
    string InventorVersion { get; }
    string ImportStepAsPart(string stepPath, string outIpt, IReadOnlyDictionary<string, string> design, IReadOnlyDictionary<string, string> custom);
}
