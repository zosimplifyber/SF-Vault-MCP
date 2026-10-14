using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using SFVaultBridge.Core;
using SFVaultBridge.Core.McMaster;

namespace SFVaultBridge.Addin;

/// <summary>
/// The McMaster sign-in record (client id and refresh token) encrypted with DPAPI for the current Windows user at
/// %APPDATA%\Simplifyber\VaultBridge\mcmaster.token. An unreadable file (another user, corrupt) reads as signed out.
/// </summary>
internal sealed class DpapiSignInStore : ISignInStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SFVaultBridge.McMaster.v1");
    private readonly string _path;

    public DpapiSignInStore(string? path = null) => _path = path ?? DefaultPath;

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Simplifyber", "VaultBridge", "mcmaster.token");

    public string? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException) { return null; }
    }

    public void Save(string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllBytes(tmp, ProtectedData.Protect(Encoding.UTF8.GetBytes(text), Entropy, DataProtectionScope.CurrentUser));
        if (File.Exists(_path)) File.Replace(tmp, _path, null); else File.Move(tmp, _path);
    }

    public void Delete()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}

/// <summary>
/// One McMaster sign-in and MCP client per server URL for the Inventor session, so the in-memory access token is reused across
/// clicks. Every use may start the browser sign-in (Zak, 2026-10-14: sign in when Fetch or Purchasing needs McMaster).
/// </summary>
internal static class McMasterSession
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(100) };
    private static readonly object Gate = new();
    private static (string Url, McMasterAuth Auth, McMasterTools Tools)? _current;

    public static (McMasterAuth Auth, IMcMasterTools Tools) For(string serverUrl, Action<string> log)
    {
        lock (Gate)
        {
            if (_current is { } c && McMasterOAuth.SameServer(c.Url, serverUrl)) return (c.Auth, c.Tools);
            var auth = new McMasterAuth(serverUrl, Http, new DpapiSignInStore(), OpenBrowser, log);
            Task<string> TokenFor(bool force, CancellationToken ct) => auth.GetAccessTokenAsync(interactive: true, ct, forceRefresh: force);
            var tools = new McMasterTools(new McpHttpClient(serverUrl, Http, TokenFor, AppVersion.Current), Http, TokenFor);
            _current = (serverUrl, auth, tools);
            return (auth, tools);
        }
    }

    /// <summary>The server URL the stored sign-in belongs to; null when not signed in.</summary>
    public static string? SignedInServer() => McMasterOAuth.FromJson(new DpapiSignInStore().Load())?.Server;

    public static bool IsSignedIn(string serverUrl) => McMasterOAuth.IsFor(McMasterOAuth.FromJson(new DpapiSignInStore().Load()), serverUrl);

    /// <summary>Opens the URL in the default browser.</summary>
    public static bool OpenBrowser(string url)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    /// <summary>The message for an exception from a McMaster call, for a dialog or the log.</summary>
    public static string Describe(Exception e) => e switch
    {
        McMasterToolException or McMasterAuthException or McMasterSignInCancelled or McMasterNotSignedIn or McMasterUnauthorized => e.Message,
        HttpRequestException => "Could not reach the McMaster server (" + e.Message + "). Check the network and the server URL in Settings.",
        TaskCanceledException => "The McMaster server did not answer in time.",
        OperationCanceledException => "Cancelled.",
        _ => e.Message,
    };
}
