namespace SFVaultBridge.Core;

/// <summary>One local file and where it lands in Dropbox.</summary>
public sealed record DropboxUploadItem(string LocalPath, string DropboxPath, long Bytes);

/// <summary>Everything an upload needs, fixed at preview time. ShareTarget is the Dropbox path the shared link is made for
/// (the uploaded folder, or the file itself for a single file).</summary>
public sealed record DropboxUploadPlan(string LocalPath, string DropboxFolder, IReadOnlyList<DropboxUploadItem> Items, bool Share, string ShareTarget, long TotalBytes);

public sealed record DropboxFileResult(string DropboxPath, long Bytes, bool Ok, string? Error = null);

public sealed record DropboxUploadOutcome(IReadOnlyList<DropboxFileResult> Files, string? ShareUrl, string? Visibility, IReadOnlyList<string> Warnings);

/// <summary>Configured: an app key is set. SignedIn: a refresh token is stored for this Windows user.</summary>
public sealed record DropboxAccountStatus(bool Configured, bool SignedIn, string TokenPath);

/// <summary>What the Dropbox tools need from the add-in. Upload creates the folders, uploads every item (a failed file is recorded,
/// not thrown) and makes the shared link when asked. Throws BridgeException("not_configured") without an app key and
/// BridgeException("dropbox_failed") when Dropbox itself refuses (sign-in cancelled, folder creation failed, and so on).</summary>
public interface IDropboxUploader
{
    DropboxAccountStatus Status();
    DropboxUploadOutcome Upload(DropboxUploadPlan plan, CancellationToken ct);
}

/// <summary>Pure rules for the generic upload: Dropbox folder paths, which local files go, and the size limits.</summary>
public static class DropboxUploadRules
{
    public const int MaxFiles = 500;
    public const long MaxBytes = 2L * 1024 * 1024 * 1024;
    public const int ListedFiles = 50;

    private static readonly string[] SkipNames = { "thumbs.db", ".ds_store", "desktop.ini" };

    /// <summary>Office lock files and desktop metadata never go to a vendor.</summary>
    public static bool Skip(string fileName) =>
        fileName.StartsWith("~$", StringComparison.Ordinal) || SkipNames.Contains(fileName.ToLowerInvariant());

    /// <summary>A Dropbox folder path, normalised: starts with '/', every segment cleaned like a partner name, no '.' or '..' segments,
    /// no empty segments. '/' alone is the root.</summary>
    public static string CleanFolderPath(string? path)
    {
        var raw = (path ?? "").Trim().Replace('\\', '/');
        if (raw.Length == 0 || raw[0] != '/') throw new BridgeException("bad_request", "'dropbox_folder' must be an absolute Dropbox path starting with '/', for example /Suppliers/Acme.");
        var segments = raw.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var clean = new List<string>();
        foreach (var s in segments)
        {
            var t = s.Trim();
            if (t == "." || t == "..") throw new BridgeException("bad_request", "'dropbox_folder' must not contain '.' or '..' segments.");
            clean.Add(DropboxPaths.CleanName(t));
        }
        return clean.Count == 0 ? "/" : "/" + string.Join('/', clean);
    }

    public static string Join(string folder, string name) => (folder == "/" ? "" : folder) + "/" + DropboxPaths.CleanName(name);

    /// <summary>Plans the upload of one file or one folder tree. A folder is placed under dropboxFolder as subfolder (default: its own name);
    /// a single file goes straight into dropboxFolder. Returns the plan, or throws bad_request / not_found / too_large.</summary>
    public static DropboxUploadPlan Plan(string localFull, string dropboxFolder, string? subfolder, bool includeSubfolders, bool share)
    {
        var folder = CleanFolderPath(dropboxFolder);
        var items = new List<DropboxUploadItem>();
        long total = 0;
        string shareTarget;

        if (File.Exists(localFull))
        {
            var fi = new FileInfo(localFull);
            var dest = Join(string.IsNullOrWhiteSpace(subfolder) ? folder : Join(folder, subfolder!), fi.Name);
            items.Add(new DropboxUploadItem(fi.FullName, dest, fi.Length));
            total = fi.Length;
            shareTarget = dest;
        }
        else if (Directory.Exists(localFull))
        {
            var di = new DirectoryInfo(localFull);
            var target = Join(folder, string.IsNullOrWhiteSpace(subfolder) ? di.Name : subfolder!);
            var files = di.EnumerateFiles("*", includeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .Where(f => !Skip(f.Name) && (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                .OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var f in files)
            {
                var rel = Path.GetRelativePath(di.FullName, f.FullName).Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                var dest = target;
                foreach (var seg in rel) dest = Join(dest, seg);
                items.Add(new DropboxUploadItem(f.FullName, dest, f.Length));
                total += f.Length;
                if (items.Count > MaxFiles) throw new BridgeException("too_large", $"More than {MaxFiles} files under '{localFull}'. Upload a smaller folder, or zip it first.");
            }
            if (items.Count == 0) throw new BridgeException("bad_request", $"No files to upload under '{localFull}'.");
            shareTarget = target;
        }
        else throw new BridgeException("not_found", $"'local_path' does not exist: {localFull}");

        if (total > MaxBytes) throw new BridgeException("too_large", $"{total / (1024 * 1024)} MB is over the {MaxBytes / (1024 * 1024 * 1024)} GB limit for one upload.");
        return new DropboxUploadPlan(localFull, folder, items, share, shareTarget, total);
    }

    /// <summary>Every Dropbox folder the plan needs, outermost first, without duplicates.</summary>
    public static IReadOnlyList<string> FoldersToCreate(DropboxUploadPlan plan)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        void Add(string folder)
        {
            if (folder == "/" || folder.Length == 0) return;
            var segs = folder.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var cur = "";
            foreach (var s in segs)
            {
                cur += "/" + s;
                if (set.Add(cur)) list.Add(cur);
            }
        }
        Add(plan.DropboxFolder);
        foreach (var i in plan.Items)
        {
            var slash = i.DropboxPath.LastIndexOf('/');
            if (slash > 0) Add(i.DropboxPath[..slash]);
        }
        return list;
    }

    /// <summary>The changes since the plan was made: files gone or resized. Empty means the plan still matches the disk.</summary>
    public static IReadOnlyList<string> Drift(DropboxUploadPlan plan)
    {
        var drift = new List<string>();
        foreach (var i in plan.Items)
        {
            if (!File.Exists(i.LocalPath)) drift.Add($"{i.LocalPath} is gone");
            else if (new FileInfo(i.LocalPath).Length != i.Bytes) drift.Add($"{i.LocalPath} changed size");
        }
        return drift;
    }
}
