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

    private static void ClearReadOnly(string path)
    {
        if (!System.IO.File.Exists(path)) return;
        var attrs = System.IO.File.GetAttributes(path);
        if ((attrs & System.IO.FileAttributes.ReadOnly) != 0)
            System.IO.File.SetAttributes(path, attrs & ~System.IO.FileAttributes.ReadOnly);
    }

    /// <summary>
    /// Adds the file, or checks out and checks in a new version. tempOutput is the finished tool output; destLocal is where
    /// the file lives in the workspace. Nothing touches the workspace until the Vault checks pass. Sets category Documents
    /// (a failure there is returned as Warning, not thrown).
    /// </summary>
    public static (WsFile File, bool NewVersion, string? Warning) Publish(string tempOutput, string destLocal, string vaultFolder, string fileName, string comment)
    {
        var conn = Conn;
        var ds = conn.WebServiceManager.DocumentService;
        var vaultPath = vaultFolder.TrimEnd('/') + "/" + fileName;
        var existing = FindLatest(vaultPath);
        WsFile result;
        if (existing is null)
        {
            var wsFolder = ds.FindFoldersByPaths(new[] { vaultFolder })?.FirstOrDefault();
            if (wsFolder is null || wsFolder.Id <= 0) throw new BridgeException("not_found", $"Vault folder {vaultFolder} does not exist.");
            var folder = new VDF.Vault.Currency.Entities.Folder(conn, wsFolder);
            ClearReadOnly(destLocal);
            System.IO.File.Copy(tempOutput, destLocal, true);
            using var stream = System.IO.File.OpenRead(destLocal);
            var added = conn.FileManager.AddFile(folder, fileName, comment, System.IO.File.GetLastWriteTime(destLocal),
                                                 Array.Empty<FileAssocParam>(), null, FileClassification.None, false, stream);
            result = ds.GetLatestFileByMasterId(added.EntityMasterId);
        }
        else
        {
            if (existing.CheckedOut)
            {
                var mine = existing.CkOutUserId == conn.UserID;
                var who = mine ? "You have" : (conn.WebServiceManager.AdminService.GetUserByUserId(existing.CkOutUserId)?.Name ?? "Someone else") + " has";
                throw new BridgeException("checked_out", $"{who} {fileName} checked out. Check it in or undo the checkout, then try again.");
            }
            ds.CheckoutFile(existing.Id, CheckoutFileOptions.Master, Environment.MachineName, destLocal, comment, out _);
            try
            {
                var iteration = new VDF.Vault.Currency.Entities.FileIteration(conn, ds.GetLatestFileByMasterId(existing.MasterId));
                ClearReadOnly(destLocal);
                System.IO.File.Copy(tempOutput, destLocal, true);
                var checkedIn = conn.FileManager.CheckinFile(iteration, comment, false, null, null, false, null,
                    FileClassification.None, false, new VDF.Currency.FilePathAbsolute(destLocal));
                result = ds.GetLatestFileByMasterId(checkedIn.EntityMasterId);
            }
            catch (Exception ex)
            {
                string undone;
                try { ds.UndoCheckoutFile(existing.Id, out _); undone = "the checkout was undone"; }
                catch { undone = "the checkout could not be undone; undo it in Vault"; }
                throw new BridgeException("checkin_failed", $"Check-in failed; {undone}: {ex.Message}");
            }
        }
        string? warning = null;
        if (CategoryName(result) != "Documents")
        {
            try { SetCategory(result.MasterId, "Documents", comment); result = ds.GetLatestFileByMasterId(result.MasterId); }
            catch (Exception ex) { warning = $"Could not set the Documents category: {ex.Message}"; }
        }
        return (result, existing is not null, warning);
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
        var modelIds = (ds.GetFilesByMasterId(model.MasterId) ?? Array.Empty<WsFile>()).Select(f => f.Id).Append(model.Id).Distinct().ToArray();
        var assocs = ds.GetFileAssociationLitesByIds(modelIds, FileAssocAlg.LatestTip,
            FileAssociationTypeEnum.Dependency, false, FileAssociationTypeEnum.None, false, false, false, false) ?? Array.Empty<FileAssocLite>();
        var idSet = new HashSet<long>(modelIds);
        var parentIds = assocs.Where(a => idSet.Contains(a.CldFileId)).Select(a => a.ParFileId).Distinct().ToArray();
        if (parentIds.Length == 0) return Array.Empty<WsFile>();
        return ds.GetFilesByIds(parentIds)
            .Where(f => f.Name.EndsWith(".idw", StringComparison.OrdinalIgnoreCase) || f.Name.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase))
            .GroupBy(f => f.MasterId).Select(g => ds.GetLatestFileByMasterId(g.Key)).ToList();
    }

    /// <summary>Download the latest version of each drawing only (never its references, which may be open in Inventor); never over existing files.</summary>
    public static IReadOnlyList<string> AcquireLatest(IEnumerable<WsFile> files)
    {
        var conn = Conn;
        var settings = new VDF.Vault.Settings.AcquireFilesSettings(conn, false);
        settings.OptionsRelationshipGathering.FileRelationshipSettings.IncludeChildren = false;
        settings.OptionsRelationshipGathering.FileRelationshipSettings.RecurseChildren = false;
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
