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
    /// (a failure there, or in reading the version back after a successful check-in, is returned as Warning, not thrown;
    /// File is null when the read-back failed).
    /// </summary>
    public static (WsFile? File, bool NewVersion, string? Warning) Publish(string tempOutput, string destLocal, string vaultFolder, string fileName, string comment)
    {
        var conn = Conn;
        var ds = conn.WebServiceManager.DocumentService;
        var vaultPath = vaultFolder.TrimEnd('/') + "/" + fileName;
        var existing = FindLatest(vaultPath);
        long masterId;
        if (existing is null)
        {
            var wsFolder = ds.FindFoldersByPaths(new[] { vaultFolder })?.FirstOrDefault();
            if (wsFolder is null || wsFolder.Id <= 0) throw new BridgeException("not_found", $"Vault folder {vaultFolder} does not exist.");
            var folder = new VDF.Vault.Currency.Entities.Folder(conn, wsFolder);
            try
            {
                ClearReadOnly(destLocal);
                System.IO.File.Copy(tempOutput, destLocal, true);
            }
            catch (Exception ex) { throw new BridgeException("local_write_failed", $"Could not write {destLocal}: {ex.Message}"); }
            using var stream = System.IO.File.OpenRead(destLocal);
            var added = conn.FileManager.AddFile(folder, fileName, comment, System.IO.File.GetLastWriteTime(destLocal),
                                                 Array.Empty<FileAssocParam>(), null, FileClassification.None, false, stream);
            masterId = added.EntityMasterId;
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
                masterId = checkedIn.EntityMasterId;
            }
            catch (Exception ex)
            {
                string undone;
                try { ds.UndoCheckoutFile(existing.Id, out _); undone = "the checkout was undone"; }
                catch { undone = "the checkout could not be undone; undo it in Vault"; }
                throw new BridgeException("checkin_failed", $"Check-in failed; {undone}: {ex.Message}");
            }
        }
        // The file is in Vault from here on; nothing below may report a failed check-in.
        WsFile result;
        try { result = ds.GetLatestFileByMasterId(masterId); }
        catch (Exception ex) { return (null, existing is not null, $"Checked in; could not read back the version: {ex.Message}"); }
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

    private static long? _fileNameDefId;

    private static long FileNameDefId()
    {
        if (_fileNameDefId is long id) return id;
        var defs = Conn.WebServiceManager.PropertyService.GetPropertyDefinitionsByEntityClassId("FILE") ?? Array.Empty<PropDef>();
        var def = defs.FirstOrDefault(d => d.SysName == "Name") ?? defs.FirstOrDefault(d => d.SysName == "ClientFileName")
                  ?? defs.FirstOrDefault(d => d.DispName is "Name" or "File Name")
                  ?? throw new BridgeException("not_found", "Vault has no file name property to search on.");
        _fileNameDefId = def.Id;
        return def.Id;
    }

    /// <summary>Latest versions of every file in Vault (all folders) whose name contains the text.</summary>
    public static IReadOnlyList<WsFile> FindByNameContaining(string text)
    {
        var ds = Conn.WebServiceManager.DocumentService;
        var cond = new SrchCond
        {
            PropDefId = FileNameDefId(), PropTyp = PropertySearchType.SingleProperty,
            SrchOper = 1, SrchTxt = text, SrchRule = SearchRuleType.Must,
        };
        var root = ds.GetFolderRoot();
        var all = new List<WsFile>();
        string bookmark = string.Empty;
        SrchStatus? status = null;
        do
        {
            var page = ds.FindFilesBySearchConditions(new[] { cond }, null, new[] { root.Id }, true, true, ref bookmark, out status);
            if (page is null || page.Length == 0) break;
            all.AddRange(page);
        } while (status is not null && all.Count < status.TotalHits);
        return all;
    }

    /// <summary>Latest .idw/.dwg files anywhere in Vault whose file name stem equals the given stem.</summary>
    public static IReadOnlyList<WsFile> DrawingsNamed(string stem)
    {
        if (string.IsNullOrWhiteSpace(stem)) return Array.Empty<WsFile>();
        return FindByNameContaining(stem)
            .Where(f => (f.Name.EndsWith(".idw", StringComparison.OrdinalIgnoreCase) || f.Name.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase))
                        && string.Equals(System.IO.Path.GetFileNameWithoutExtension(f.Name), stem, StringComparison.OrdinalIgnoreCase))
            .GroupBy(f => f.MasterId).Select(g => g.First()).ToList();
    }

    /// <summary>Vendor PDF of a purchased part: a .pdf attached to the part's latest version, else a .pdf in Vault named by the part stem or vendor number.</summary>
    public static WsFile? VendorPdf(WsFile part, string vendorNumber)
    {
        var ds = Conn.WebServiceManager.DocumentService;
        var assocs = ds.GetFileAssociationLitesByIds(new[] { part.Id }, FileAssocAlg.LatestTip,
            FileAssociationTypeEnum.None, false, FileAssociationTypeEnum.Attachment, false, false, false, false) ?? Array.Empty<FileAssocLite>();
        var childIds = assocs.Where(a => a.ParFileId == part.Id).Select(a => a.CldFileId).Distinct().ToArray();
        if (childIds.Length > 0)
        {
            var attached = (ds.GetFilesByIds(childIds) ?? Array.Empty<WsFile>()).ToList();
            var name = ReviewRules.FirstPdf(attached.Select(f => f.Name));
            var hit = attached.FirstOrDefault(f => f.Name == name);
            if (hit is not null) return ds.GetLatestFileByMasterId(hit.MasterId);
        }
        var stem = System.IO.Path.GetFileNameWithoutExtension(part.Name);
        var found = new List<WsFile>();
        foreach (var text in new[] { stem, vendorNumber.Trim() }.Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            found.AddRange(FindByNameContaining(text));
        var match = ReviewRules.MatchVendorPdf(stem, vendorNumber, found.Select(f => f.Name));
        return found.FirstOrDefault(f => f.Name == match);
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
