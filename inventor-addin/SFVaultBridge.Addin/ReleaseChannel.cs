using Autodesk.Connectivity.WebServices;
using SFVaultBridge.Core;
using System.Diagnostics;
using WsFile = Autodesk.Connectivity.WebServices.File;

namespace SFVaultBridge.Addin;

/// <summary>VaultFiles members for the release folder ($/Tools/SF Vault Bridge), which sits outside the Library root.</summary>
internal static partial class VaultFiles
{
    public static IReadOnlyList<WsFile> FilesIn(string vaultFolder)
    {
        var folder = FolderAt(vaultFolder);
        if (folder is null) return Array.Empty<WsFile>();
        return Conn.WebServiceManager.DocumentService.GetLatestFilesByFolderId(folder.Id, false) ?? Array.Empty<WsFile>();
    }

    /// <summary>Creates every missing level of the path below the vault root (any folder, not just the Library). Used only for the release folder.</summary>
    public static void EnsureFolderAnywhere(string vaultPath)
    {
        var ds = Conn.WebServiceManager.DocumentService;
        var parent = FolderAt("$") ?? throw new BridgeException("not_found", "Vault has no root folder.");
        var current = "$";
        foreach (var level in vaultPath.Split('/', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            current += "/" + level;
            parent = FolderAt(current) ?? ds.AddFolder(level, parent.Id, false);
        }
    }
}

/// <summary>The Vault release folder: list versions, publish a zip.</summary>
internal sealed class ReleaseChannel : IReleaseChannel
{
    public IReadOnlyList<string> ListNames() => VaultFiles.FilesIn(AppVersion.ReleaseFolder).Select(f => f.Name).ToList();

    public string Publish(string localZip, string fileName, string comment)
    {
        VaultFiles.EnsureFolderAnywhere(AppVersion.ReleaseFolder);
        var destLocal = System.IO.Path.Combine(VaultFiles.WorkingFolderRoot(), "Tools", "SF Vault Bridge", fileName);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destLocal)!);
        var (file, _, warning) = VaultFiles.Publish(localZip, destLocal, AppVersion.ReleaseFolder, fileName, comment);
        if (file is null && warning is not null) throw new BridgeException("publish_warning", warning);
        return AppVersion.ReleaseFolder + "/" + fileName;
    }
}

/// <summary>Looks for a newer release in Vault. Never throws; a failure is logged once and the check tries again next time.</summary>
internal sealed class UpdateChecker
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private readonly Action<string> _log;
    private DateTime _lastCheckUtc = DateTime.MinValue;
    private bool _failureLogged;

    public UpdateChecker(Action<string> log) => _log = log;

    public (SemVer Version, string Name)? Available { get; private set; }

    public bool Due(DateTime nowUtc) => nowUtc - _lastCheckUtc >= Interval;

    /// <summary>Call only when signed in to Vault. Runs on the UI thread (a Vault round trip).</summary>
    public void Check(DateTime nowUtc)
    {
        _lastCheckUtc = nowUtc;
        try
        {
            if (!SemVer.TryParse(AppVersion.Current, out var current)) return;
            Available = AppVersion.Newest(VaultFiles.FilesIn(AppVersion.ReleaseFolder).Select(f => f.Name), current);
            if (Available is { } a) _log($"update available: {a.Version}");
            _failureLogged = false;
        }
        catch (Exception ex)
        {
            if (!_failureLogged) _log("update check failed: " + ex.Message);
            _failureLogged = true;
        }
    }

    /// <summary>Downloads the release zip through the Vault session to Downloads\SF Vault Bridge and opens that folder. Returns the saved path.</summary>
    public string Download(string name)
    {
        var file = VaultFiles.FindLatest(AppVersion.ReleaseFolder + "/" + name)
            ?? throw new BridgeException("not_found", $"{name} is no longer in {AppVersion.ReleaseFolder}.");
        VaultFiles.GetLatestOverwrite(new[] { file });
        var saved = Downloads.Keep(VaultFiles.LocalPathOf(file), name, _log)
            ?? throw new BridgeException("local_write_failed", $"Could not save {name} to {Downloads.Folder}.");
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{saved}\"") { UseShellExecute = true });
        return saved;
    }
}
