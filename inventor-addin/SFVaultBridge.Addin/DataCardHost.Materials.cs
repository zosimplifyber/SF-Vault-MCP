using Autodesk.Connectivity.WebServices;
using SFVaultBridge.Core;
using VDF = Autodesk.DataManagement.Client.Framework;
using Inv = Inventor;

namespace SFVaultBridge.Addin;

/// <summary>add_library_material and list_material_properties: the shared Inventor material library stored in Vault. Runs on Inventor's UI thread.</summary>
internal sealed partial class DataCardHost
{
    private static string BackupDir() =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Simplifyber", "VaultBridge", "backups");

    // ---- Preview ----

    public MaterialLibraryFacts Inspect(DataCardFileInfo file, string materialName)
    {
        var latest = Latest(file);
        var local = VaultFiles.LocalPathOf(latest);
        var exists = System.IO.File.Exists(local);
        var matches = true;
        if (exists)
        {
            var temp = NewTempDir();
            try { matches = SameBytes(local, DownloadBaseline(latest, temp)); }
            finally { DeleteQuietly(temp); }
        }
        var lib = MaterialAssets.FindLoaded(_app, local);
        if (lib is null) return new MaterialLibraryFacts(local, exists, matches, false, null, null, null);
        var count = MaterialAssets.MaterialCount(lib);
        bool? readOnly = null;
        try { readOnly = lib.IsReadOnly; } catch (System.Runtime.InteropServices.COMException) { }
        return new MaterialLibraryFacts(local, exists, matches, true, readOnly, MaterialAssets.FindMaterial(lib, materialName) is not null, count < 0 ? null : count);
    }

    // ---- Read-only dump ----

    public IReadOnlyList<MaterialPropertyRow> ListProperties(string materialName, string? library)
    {
        var all = MaterialAssets.Loaded(_app);
        var libs = all.Where(l => library is null || MaterialRules.MatchesLibrary(MaterialAssets.FullName(l), library)).ToList();
        if (libs.Count == 0)
            throw new BridgeException("not_found", library is null
                ? "Inventor has no material library loaded."
                : $"No loaded library matches '{library}'. Loaded: {string.Join(", ", all.Select(l => System.IO.Path.GetFileName(MaterialAssets.FullName(l))))}.");
        foreach (var lib in libs)
        {
            var asset = MaterialAssets.FindMaterial(lib, materialName);
            if (asset is null) continue;
            return MaterialAssets.Dump(asset, $"{System.IO.Path.GetFileName(MaterialAssets.FullName(lib))}: {materialName}");
        }
        var similar = libs.SelectMany(l => MaterialAssets.MaterialNames(l))
            .Where(n => n.Contains(materialName, StringComparison.OrdinalIgnoreCase) || materialName.Contains(n, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(15).ToList();
        throw new BridgeException("not_found", $"No material named '{materialName}' in {string.Join(", ", libs.Select(l => System.IO.Path.GetFileName(MaterialAssets.FullName(l))))}."
            + (similar.Count > 0 ? " Similar: " + string.Join("; ", similar) + "." : ""));
    }

    // ---- Apply ----

    public MaterialApplied AddMaterial(DataCardFileInfo file, MaterialSpec spec, string comment)
    {
        var conn = Conn;
        var ds = conn.WebServiceManager.DocumentService;
        var latest = Latest(file);
        var again = Describe(latest);
        var skip = MaterialRules.SkipReason(again, reviseReleased: false);
        if (skip is not null) throw new DataCardSkipException(skip);
        if (again.Version != file.Version)
            throw new DataCardSkipException($"{file.Name} changed in Vault since the preview (version {file.Version} to {again.Version}). Run it again.");
        var local = VaultFiles.LocalPathOf(latest);

        // 1. The local file must be Vault's latest; copy it aside before anything can change.
        string backup;
        var temp = NewTempDir();
        try
        {
            var baseline = DownloadBaseline(latest, temp);
            RequireLocalMatches(local, baseline);
            backup = MakeBackup(System.IO.File.Exists(local) ? local : baseline, file.Name);
        }
        finally { DeleteQuietly(temp); }

        FileAssocParam[]? assocs;
        try { assocs = ChildAssociations(conn, latest); }
        catch (Exception ex) { throw new DataCardStepException("children", "could not read the library's child associations: " + ex.Message); }

        // 2. Build the material in a hidden temporary document first: a name or value Inventor refuses stops here, with Vault untouched.
        using var built = MaterialAssets.Build(_app, spec);

        // 3. Close the library in Inventor, check it out, open it again writable and add the material.
        var state = new MaterialAssets.LibraryState(false, false, false);
        var step = "close";
        VDF.Vault.Currency.Entities.FileIteration after;
        try
        {
            state = MaterialAssets.Unload(_app, local);
            step = "checkout";
            var (checkedOut, _) = CheckOut(conn, latest, overwrite: true, comment);
            ClearReadOnly(local);
            step = "open";
            var lib = MaterialAssets.Open(_app, local);
            if (lib.IsReadOnly)
                throw new DataCardStepException("open", "Inventor opened the library read-only even though it is checked out to you.");
            step = "copy";
            if (MaterialAssets.FindMaterial(lib, spec.Name) is not null)
                throw new DataCardStepException("copy", $"The library already has a material named {spec.Name}.");
            MaterialAssets.CopyInto(built.Material, lib);
            step = "verify";
            var problem = MaterialAssets.Verify(lib, spec);
            if (problem is not null) throw new DataCardStepException("verify", "the copied material is wrong: " + problem);
            step = "close";
            MaterialAssets.Close(lib);
            step = "checkin";
            after = conn.FileManager.CheckinFile(checkedOut, comment, false, assocs, null, false, null,
                FileClassification.None, false, new VDF.Currency.FilePathAbsolute(local));
        }
        catch (Exception ex)
        {
            var failedStep = ex is DataCardStepException se ? se.Step : step;
            string closed;
            try
            {
                MaterialAssets.FindLoaded(_app, local)?.Remove();
                closed = "";
            }
            catch (Exception closeEx) { closed = "; the library could not be closed in Inventor (" + closeEx.Message + ")"; }
            var undone = UndoIfStillMine(conn, latest.MasterId, local);
            var restored = RestoreLocalFile(local, backup);
            var reloaded = MaterialAssets.ReloadAfterFailure(_app, local, state);
            throw new DataCardStepException(failedStep, $"{ex.Message} ({undone}; {restored}{closed}{reloaded}; backup {backup})");
        }

        // 4. Checked in. Nothing below may fail the run or undo anything: problems become a warning.
        var warnings = new List<string>(built.Warnings);
        long version = -1;
        try { version = ds.GetLatestFileByMasterId(after.EntityMasterId).VerNum; }
        catch (Exception ex) { warnings.Add("Checked in, but the new version number could not be read: " + ex.Message); }
        var verified = false;
        try
        {
            var lib = MaterialAssets.Open(_app, local);
            var problem = MaterialAssets.Verify(lib, spec);
            verified = problem is null;
            if (problem is not null) warnings.Add("Checked in, but reopening the library did not show the material correctly: " + problem);
            if (state.WasLoaded) MaterialAssets.Restore(_app, lib, state);
            else MaterialAssets.Close(lib);
        }
        catch (Exception ex) { warnings.Add("Checked in, but the library could not be reopened to verify it: " + ex.Message); }
        return new MaterialApplied(version, backup, warnings.Count == 0 ? null : string.Join(" ", warnings), verified);
    }

    private static string MakeBackup(string source, string libraryName)
    {
        try
        {
            var dir = BackupDir();
            System.IO.Directory.CreateDirectory(dir);
            var dest = System.IO.Path.Combine(dir, MaterialRules.BackupFileName(libraryName, DateTime.Now));
            System.IO.File.Copy(source, dest, overwrite: false);
            System.IO.File.SetAttributes(dest, System.IO.FileAttributes.Normal);
            return dest;
        }
        catch (Exception ex) { throw new DataCardStepException("backup", $"could not copy the library to the backups folder: {ex.Message}"); }
    }

    /// <summary>After a failed run the local file must be what Vault has: put the backup back when it differs. Returns a short note, never throws.</summary>
    private static string RestoreLocalFile(string local, string backup)
    {
        try
        {
            if (System.IO.File.Exists(local) && SameBytes(local, backup)) return "the local file is unchanged";
            if (System.IO.File.Exists(local)) System.IO.File.SetAttributes(local, System.IO.FileAttributes.Normal);
            System.IO.File.Copy(backup, local, overwrite: true);
            System.IO.File.SetAttributes(local, System.IO.FileAttributes.ReadOnly);
            return "the local file was restored from the backup";
        }
        catch (Exception ex) { return "the local file could NOT be restored (" + ex.Message + ")"; }
    }
}
