using Autodesk.Connectivity.WebServices;
using SFVaultBridge.Core;
using VDF = Autodesk.DataManagement.Client.Framework;
using WsFile = Autodesk.Connectivity.WebServices.File;
using WsFolder = Autodesk.Connectivity.WebServices.Folder;

namespace SFVaultBridge.Addin;

/// <summary>IVaultSession on the Inventor Vault add-in's signed-in connection. No second sign-in, no extra licence seat.</summary>
internal sealed class VaultSession : IVaultSession
{
    private static VDF.Vault.Currency.Connections.Connection? Current =>
        Connectivity.Application.VaultBase.ConnectionManager.Instance.Connection;

    private static VDF.Vault.Currency.Connections.Connection Conn =>
        Current ?? throw new BridgeException("not_signed_in", BridgeOps.NotSignedInMessage);

    internal static VDF.Vault.Currency.Connections.Connection Connection => Conn;

    public StatusInfo Status()
    {
        var c = Current;
        return c is null ? new StatusInfo(false, null, null, null) : new StatusInfo(true, c.UserName, c.Vault, c.Server);
    }

    public IReadOnlyList<SchemeInfo> GetNumberingSchemes() =>
        (Conn.WebServiceManager.NumberingService.GetNumberingSchemes("FILE", NumSchmType.Activated) ?? Array.Empty<NumSchm>())
            .Select(s => new SchemeInfo(s.SchmID, s.Name, s.IsDflt)).ToList();

    public string GenerateFileNumber(long schemeId) =>
        Conn.WebServiceManager.DocumentService.GenerateFileNumber(schemeId, Array.Empty<string>());

    public FolderInfo? GetFolderByPath(string path)
    {
        var f = FindFolder(Conn, path);
        return f is null ? null : new FolderInfo(f.Id, f.FullName, f.IsLib);
    }

    public FolderInfo AddFolder(string parentPath, string name)
    {
        var conn = Conn;
        var parent = FindFolder(conn, parentPath) ?? throw new BridgeException("not_found", $"Vault folder {parentPath} does not exist.");
        var f = conn.WebServiceManager.DocumentService.AddFolder(name, parent.Id, parent.IsLib);
        return new FolderInfo(f.Id, f.FullName, f.IsLib);
    }

    public IReadOnlyList<CategoryInfo> GetFileCategories() =>
        Conn.WebServiceManager.CategoryService.GetCategoriesByEntityClassId("FILE", true)
            .Select(c => new CategoryInfo(c.Id, c.Name)).ToList();

    public VaultFile AddFile(string folderPath, string localPath, string fileName, long[] attachmentFileIds, string comment)
    {
        var conn = Conn;
        if (!System.IO.File.Exists(localPath)) throw new BridgeException("not_found", $"Local file not found: {localPath}");
        var wsFolder = FindFolder(conn, folderPath) ?? throw new BridgeException("not_found", $"Vault folder {folderPath} does not exist.");
        var folder = new VDF.Vault.Currency.Entities.Folder(conn, wsFolder);
        var assocs = attachmentFileIds
            .Select(id => new FileAssocParam { CldFileId = id, Typ = AssociationType.Attachment, RefId = null, Source = null, ExpectedVaultPath = null })
            .ToArray();
        using var stream = System.IO.File.OpenRead(localPath);
        var added = conn.FileManager.AddFile(folder, fileName, comment, System.IO.File.GetLastWriteTime(localPath),
                                             assocs, null, FileClassification.None, false, stream);
        return new VaultFile(added.EntityIterationId, added.EntityMasterId, added.EntityName, added.IsCheckedOut);
    }

    public IReadOnlyList<VaultFile> UpdateFileCategories(long[] masterIds, long categoryId, string comment) =>
        Conn.WebServiceManager.DocumentServiceExtensions
            .UpdateFileCategories(masterIds, masterIds.Select(_ => categoryId).ToArray(), comment)
            .Select(ToFile).ToList();

    private static WsFolder? FindFolder(VDF.Vault.Currency.Connections.Connection conn, string path)
    {
        var f = conn.WebServiceManager.DocumentService.FindFoldersByPaths(new[] { path })?.FirstOrDefault();
        return f is null || f.Id <= 0 ? null : f;
    }

    private static VaultFile ToFile(WsFile f) => new(f.Id, f.MasterId, f.Name, f.CheckedOut);
}
