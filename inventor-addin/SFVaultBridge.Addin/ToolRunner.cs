using System.Diagnostics;
using SFVaultBridge.Core;

namespace SFVaultBridge.Addin;

/// <summary>Runs SFVaultTools.exe (installed next to this add-in) and returns its result file text.</summary>
internal static class ToolRunner
{
    /// <summary>Bundle layout: Contents/tools/SFVaultTools.exe. Falls back to next to the DLL (older installs).</summary>
    public static string ToolPath
    {
        get
        {
            var dir = System.IO.Path.GetDirectoryName(typeof(ToolRunner).Assembly.Location)!;
            var inTools = System.IO.Path.Combine(dir, "tools", "SFVaultTools.exe");
            return System.IO.File.Exists(inTools) ? inTools : System.IO.Path.Combine(dir, "SFVaultTools.exe");
        }
    }

    public static string? Run(IReadOnlyList<string> args, string resultPath, TimeSpan timeout)
    {
        if (!System.IO.File.Exists(ToolPath))
            throw new BridgeException("tool_missing", $"SFVaultTools.exe is not installed next to the add-in ({ToolPath}). Reinstall the SF Vault Bridge package.");
        var psi = new ProcessStartInfo(ToolPath) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new BridgeException("tool_failed", "SFVaultTools.exe did not start.");
        if (!p.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { p.Kill(true); } catch { }
            throw new BridgeException("tool_timeout", $"SFVaultTools.exe did not finish within {timeout.TotalMinutes:0} minutes.");
        }
        return System.IO.File.Exists(resultPath) ? System.IO.File.ReadAllText(resultPath) : null;
    }
}
