using Autodesk.Connectivity.WebServices;
using SFVaultBridge.Core;
using VDF = Autodesk.DataManagement.Client.Framework;
using WsFile = Autodesk.Connectivity.WebServices.File;

namespace SFVaultBridge.Addin;

/// <summary>Vault file work for the ribbon buttons, on Inventor's signed-in session.</summary>
internal static class VaultFiles
{
    private static VDF.Vault.Currency.Connections.Connection Conn => VaultSession.Connection;

    public static string WorkingFolderRoot() => Conn.WorkingFoldersManager.GetWorkingFolder("$").FullPath;

    public static WsFile? FindLatest(string vaultPath)
    {
        var f = Conn.WebServiceManager.DocumentService.FindLatestFilesByPaths(new[] { vaultPath })?.FirstOrDefault();
        return f is null || f.Id <= 0 ? null : f;
    }

    public static string? CategoryName(WsFile f) => f.Cat?.CatName;

    /// <summary>Add the file, or check out and check in a new version. Sets category Documents.</summary>
    public static (WsFile File, bool NewVersion) Publish(string localPath, string vaultFolder, string fileName, string comment)
    {
        var conn = Conn;
        var vaultPath = vaultFolder.TrimEnd('/') + "/" + fileName;
        var existing = FindLatest(vaultPath);
        WsFile result;
        if (existing is null)
        {
            var wsFolder = conn.WebServiceManager.DocumentService.FindFoldersByPaths(new[] { vaultFolder })?.FirstOrDefault();
            if (wsFolder is null || wsFolder.Id <= 0) throw new BridgeException("not_found", $"Vault folder {vaultFolder} does not exist.");
            var folder = new VDF.Vault.Currency.Entities.Folder(conn, wsFolder);
            using var stream = System.IO.File.OpenRead(localPath);
            var added = conn.FileManager.AddFile(folder, fileName, comment, System.IO.File.GetLastWriteTime(localPath),
                                                 Array.Empty<FileAssocParam>(), null, FileClassification.None, false, stream);
            result = conn.WebServiceManager.DocumentService.GetLatestFileByMasterId(added.EntityMasterId);
        }
        else
        {
            if (existing.CheckedOut && existing.CkOutUserId != conn.UserID)
            {
                var who = conn.WebServiceManager.AdminService.GetUserByUserId(existing.CkOutUserId)?.Name ?? "someone else";
                throw new BridgeException("checked_out_by_other", $"{who} has {fileName} checked out.");
            }
            var iteration = new VDF.Vault.Currency.Entities.FileIteration(conn, existing);
            if (!existing.CheckedOut)
            {
                conn.WebServiceManager.DocumentService.CheckoutFile(existing.Id, CheckoutFileOptions.Master,
                    Environment.MachineName, localPath, comment, out _);
                iteration = new VDF.Vault.Currency.Entities.FileIteration(conn,
                    conn.WebServiceManager.DocumentService.GetLatestFileByMasterId(existing.MasterId));
            }
            var checkedIn = conn.FileManager.CheckinFile(iteration, comment, false, null, null, false, null,
                FileClassification.None, false, new VDF.Currency.FilePathAbsolute(localPath));
            result = conn.WebServiceManager.DocumentService.GetLatestFileByMasterId(checkedIn.EntityMasterId);
        }
        SetCategory(result.MasterId, "Documents", comment);
        return (conn.WebServiceManager.DocumentService.GetLatestFileByMasterId(result.MasterId), existing is not null);
    }

    private static void SetCategory(long masterId, string category, string comment)
    {
        var cat = Conn.WebServiceManager.CategoryService.GetCategoriesByEntityClassId("FILE", true)
            .FirstOrDefault(c => c.Name == category) ?? throw new BridgeException("not_found", $"Vault has no file category {category}.");
        Conn.WebServiceManager.DocumentServiceExtensions.UpdateFileCategories(new[] { masterId }, new[] { cat.Id }, comment);
    }

    /// <summary>Latest drawings (.idw/.dwg) that reference the given model file.</summary>
    public static IReadOnlyList<WsFile> WhereUsedDrawings(WsFile model)
    {
        var ds = Conn.WebServiceManager.DocumentService;
        var assocs = ds.GetFileAssociationLitesByIds(new[] { model.Id }, FileAssocAlg.LatestTip,
            FileAssociationTypeEnum.Dependency, false, FileAssociationTypeEnum.None, false, false, false, false) ?? Array.Empty<FileAssocLite>();
        var parentIds = assocs.Where(a => a.CldFileId == model.Id).Select(a => a.ParFileId).Distinct().ToArray();
        if (parentIds.Length == 0) return Array.Empty<WsFile>();
        return ds.GetFilesByIds(parentIds)
            .Where(f => f.Name.EndsWith(".idw", StringComparison.OrdinalIgnoreCase) || f.Name.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase))
            .GroupBy(f => f.MasterId).Select(g => ds.GetLatestFileByMasterId(g.Key)).ToList();
    }

    /// <summary>Download the latest version of each drawing and its references; never over files checked out to the user.</summary>
    public static IReadOnlyList<string> AcquireLatest(IEnumerable<WsFile> files)
    {
        var conn = Conn;
        var settings = new VDF.Vault.Settings.AcquireFilesSettings(conn, false);
        settings.OptionsRelationshipGathering.FileRelationshipSettings.IncludeChildren = true;
        settings.OptionsRelationshipGathering.FileRelationshipSettings.RecurseChildren = true;
        settings.OptionsRelationshipGathering.FileRelationshipSettings.VersionGatheringOption = VDF.Vault.Currency.VersionGatheringOption.Latest;
        settings.OptionsResolution.OverwriteOption = VDF.Vault.Settings.AcquireFilesSettings.AcquireFileResolutionOptions.OverwriteOptions.NoOverwrite;
        foreach (var f in files)
            settings.AddFileToAcquire(new VDF.Vault.Currency.Entities.FileIteration(conn, f), VDF.Vault.Settings.AcquireFilesSettings.AcquisitionOption.Download);
        var results = conn.FileManager.AcquireFiles(settings);
        return results.FileResults.Select(r => r.LocalPath.FullPath).ToList();
    }

    public static string LocalPathOf(WsFile f) =>
        Conn.WorkingFoldersManager.GetPathOfFileInWorkingFolder(new VDF.Vault.Currency.Entities.FileIteration(Conn, f)).FullPath;
}
