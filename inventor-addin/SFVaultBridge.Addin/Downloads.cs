using SFVaultBridge.Core;

namespace SFVaultBridge.Addin;

/// <summary>The per-user Downloads\SF Vault Bridge folder where every finished output is also kept.</summary>
internal static class Downloads
{
    public static string Folder { get; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "SF Vault Bridge");

    /// <summary>Copies the finished output to the folder (never overwrites; "(n)" suffix) before the temp folder goes away; null if even that fails.</summary>
    public static string? Keep(string tempOutput, string name, Action<string> log)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Folder);
            var target = System.IO.Path.Combine(Folder, VaultPaths.UniqueName(name, n => System.IO.File.Exists(System.IO.Path.Combine(Folder, n))));
            System.IO.File.Copy(tempOutput, target, false);
            return target;
        }
        catch (Exception ex) { log($"could not keep a copy of {name}: {ex.Message}"); return null; }
    }
}
