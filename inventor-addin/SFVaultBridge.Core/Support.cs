using System.Security.Cryptography;

namespace SFVaultBridge.Core;

/// <summary>The shared secret between the add-in and the Python client.</summary>
public static class TokenStore
{
    public static string LoadOrCreate(string path)
    {
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length >= 32) return existing;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        File.WriteAllText(path, token);
        return token;
    }
}

/// <summary>Only files under the Vault workspace or Downloads may be read or written.</summary>
public sealed class PathGuard
{
    private readonly string[] _roots;
    private readonly string _allowed;

    /// <param name="allowed">How the allowed places are named in the error, default "the Vault workspace or Downloads".</param>
    public PathGuard(IEnumerable<string> roots, string? allowed = null)
    {
        _roots = roots.Where(r => !string.IsNullOrWhiteSpace(r))
                      .Select(r => Path.GetFullPath(r).TrimEnd('\\', '/') + Path.DirectorySeparatorChar)
                      .ToArray();
        _allowed = allowed ?? "the Vault workspace or Downloads";
    }

    public string Check(string? path, string field)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new BridgeException("bad_request", $"'{field}' is required.");
        var full = Path.GetFullPath(path);
        if (!_roots.Any(r => full.StartsWith(r, StringComparison.OrdinalIgnoreCase)))
            throw new BridgeException("path_not_allowed", $"'{field}' must be under {_allowed}: {full}");
        return full;
    }
}

/// <summary>Append-only log; logging never throws.</summary>
public sealed class FileLog
{
    private readonly string _path;
    private readonly object _gate = new();

    public FileLog(string path) => _path = path;

    public void Write(string line)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
