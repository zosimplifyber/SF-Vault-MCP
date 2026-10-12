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

    internal static void SetCategory(long masterId, string category, string comment)
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

    // ---- Import New Part support ----

    private static Folder? FolderAt(string vaultPath)
    {
        var f = Conn.WebServiceManager.DocumentService.FindFoldersByPaths(new[] { vaultPath })?.FirstOrDefault();
        return f is null || f.Id <= 0 ? null : f;
    }

    /// <summary>Immediate subfolder names of the Library root, sorted.</summary>
    public static IReadOnlyList<string> LibraryVendors()
    {
        var root = FolderAt(ImportRules.LibraryRoot);
        if (root is null) return Array.Empty<string>();
        var kids = Conn.WebServiceManager.DocumentService.GetFoldersByParentId(root.Id, false) ?? Array.Empty<Folder>();
        return kids.Select(k => k.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Folders below the vendor folder as relative paths ("a", "a/b"), sorted, at most 3 levels deep.</summary>
    public static IReadOnlyList<string> Subfolders(string vendor)
    {
        var start = FolderAt($"{ImportRules.LibraryRoot}/{vendor}");
        var result = new List<string>();
        if (start is null) return result;
        var ds = Conn.WebServiceManager.DocumentService;
        void Walk(long parentId, string prefix, int depth)
        {
            if (depth > 3) return;
            foreach (var k in ds.GetFoldersByParentId(parentId, false) ?? Array.Empty<Folder>())
            {
                var rel = prefix.Length == 0 ? k.Name : prefix + "/" + k.Name;
                result.Add(rel);
                Walk(k.Id, rel, depth + 1);
            }
        }
        Walk(start.Id, "", 1);
        return result.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static PropDef FilePropDef(string name)
    {
        var defs = Conn.WebServiceManager.PropertyService.GetPropertyDefinitionsByEntityClassId("FILE") ?? Array.Empty<PropDef>();
        return defs.FirstOrDefault(d => string.Equals(d.DispName, name, StringComparison.OrdinalIgnoreCase))
               ?? defs.FirstOrDefault(d => string.Equals(d.SysName, name, StringComparison.OrdinalIgnoreCase))
               ?? throw new BridgeException("not_found", $"Vault has no file property {name}.");
    }

    /// <summary>Latest file whose Vendor Number property matches and whose Vendor/Vendor Number give the same duplicate key as the plan.</summary>
    public static WsFile? FindByVendorNumber(string vendor, string vendorNumber)
    {
        var ds = Conn.WebServiceManager.DocumentService;
        var numDef = FilePropDef("Vendor Number");
        var vendorDef = FilePropDef("Vendor");
        var wanted = ImportRules.DuplicateKey(vendor, vendorNumber);
        // Contains on the whitespace-free number: a superset of equality, so stored case/spacing variants still turn up.
        var needle = System.Text.RegularExpressions.Regex.Replace(vendorNumber ?? "", @"\s+", "");
        if (needle.Length == 0) return null;
        var cond = new SrchCond { PropDefId = numDef.Id, PropTyp = PropertySearchType.SingleProperty, SrchOper = 1, SrchTxt = needle, SrchRule = SearchRuleType.Must };
        var hits = new List<WsFile>();
        string bookmark = string.Empty;
        SrchStatus? status = null;
        var root = ds.GetFolderRoot();
        do
        {
            var page = ds.FindFilesBySearchConditions(new[] { cond }, null, new[] { root.Id }, true, true, ref bookmark, out status);
            if (page is null || page.Length == 0) break;
            hits.AddRange(page);
        } while (status is not null && hits.Count < status.TotalHits);
        if (hits.Count == 0) return null;
        var ids = hits.Select(f => f.Id).ToArray();
        var props = Conn.WebServiceManager.PropertyService.GetProperties("FILE", ids, new[] { numDef.Id, vendorDef.Id }) ?? Array.Empty<PropInst>();
        string Val(long fileId, long defId) => props.FirstOrDefault(p => p.EntityId == fileId && p.PropDefId == defId)?.Val?.ToString() ?? "";
        return hits.FirstOrDefault(f => ImportRules.DuplicateKey(Val(f.Id, vendorDef.Id), Val(f.Id, numDef.Id)) == wanted);
    }

    /// <summary>Vault folder path of a file's folder.</summary>
    public static string FolderPathOf(WsFile f) => Conn.WebServiceManager.DocumentService.GetFolderById(f.FolderId).FullName;

    /// <summary>Next CD number from the default FILE numbering scheme; throws BridgeException("number") when it is unusable or taken.</summary>
    public static string NextPartNumber()
    {
        var ws = Conn.WebServiceManager;
        var schemes = ws.NumberingService.GetNumberingSchemes("FILE", NumSchmType.Activated) ?? Array.Empty<NumSchm>();
        var scheme = schemes.FirstOrDefault(s => s.IsDflt) ?? throw new BridgeException("number", "Vault has no default file numbering scheme.");
        var cd = ws.DocumentService.GenerateFileNumber(scheme.SchmID, Array.Empty<string>()) ?? "";
        if (!System.Text.RegularExpressions.Regex.IsMatch(cd, @"^CD-\d{6}$"))
            throw new BridgeException("number", $"Vault generated '{cd}', which is not a CD-###### number.");
        var clash = FindByNameContaining(cd).Where(f => f.Name.StartsWith(cd + ".", StringComparison.OrdinalIgnoreCase)).Select(f => f.Name).ToList();
        if (clash.Count > 0)
            throw new BridgeException("number", $"Vault generated {cd}, but Vault already has {string.Join(", ", clash)}. The numbering counter is behind. Nothing was written.");
        return cd;
    }

    /// <summary>Creates every missing level of vaultPath under the Library root; returns the folder paths it created.</summary>
    public static IReadOnlyList<string> EnsureFolder(string vaultPath)
    {
        var ds = Conn.WebServiceManager.DocumentService;
        if (!vaultPath.StartsWith(ImportRules.LibraryRoot, StringComparison.Ordinal))
            throw new BridgeException("folder", $"{vaultPath} is not under {ImportRules.LibraryRoot}.");
        var created = new List<string>();
        var parent = FolderAt(ImportRules.LibraryRoot) ?? throw new BridgeException("not_found", $"Vault folder {ImportRules.LibraryRoot} does not exist.");
        var current = ImportRules.LibraryRoot;
        foreach (var level in vaultPath[ImportRules.LibraryRoot.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + level;
            var existing = FolderAt(current);
            if (existing is null)
            {
                existing = ds.AddFolder(level, parent.Id, parent.IsLib);
                created.Add(current);
            }
            parent = existing;
        }
        return created;
    }

    /// <summary>Adds a new file with optional attachments and sets its category. The file is in Vault even when CategoryError is set.</summary>
    public static (WsFile File, string? CategoryError) AddNew(string localPath, string vaultFolder, string fileName, long[] attachmentFileIds, string category, string comment)
    {
        var conn = Conn;
        var ds = conn.WebServiceManager.DocumentService;
        var wsFolder = FolderAt(vaultFolder) ?? throw new BridgeException("not_found", $"Vault folder {vaultFolder} does not exist.");
        var folder = new VDF.Vault.Currency.Entities.Folder(conn, wsFolder);
        var assocs = (attachmentFileIds ?? Array.Empty<long>()).Select(id => new FileAssocParam
        {
            CldFileId = id, Typ = AssociationType.Attachment, RefId = null, Source = null, ExpectedVaultPath = null,
        }).ToArray();
        VDF.Vault.Currency.Entities.FileIteration added;
        using (var stream = System.IO.File.OpenRead(localPath))
            added = conn.FileManager.AddFile(folder, fileName, comment, System.IO.File.GetLastWriteTime(localPath), assocs, null, FileClassification.None, false, stream);
        var file = ds.GetLatestFileByMasterId(added.EntityMasterId);
        if (CategoryName(file) == category) return (file, null);
        try
        {
            SetCategory(file.MasterId, category, comment);
            return (ds.GetLatestFileByMasterId(file.MasterId), null);
        }
        catch (Exception ex) { return (file, ex.Message); }
    }

    public static string LocalPathOf(WsFile f) =>
        Conn.WorkingFoldersManager.GetPathOfFileInWorkingFolder(new VDF.Vault.Currency.Entities.FileIteration(Conn, f)).FullPath;
}
