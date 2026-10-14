using SFVaultBridge.Core;
using Inv = Inventor;
using WsFile = Autodesk.Connectivity.WebServices.File;

namespace SFVaultBridge.Addin;

/// <summary>What Inventor and the workspace say about a Vault file's local copy (release gate, packages and I Approve! share these rules).</summary>
internal static class LocalState
{
    /// <summary>"you", the user's name, or "someone else"; null when the file is not checked out.</summary>
    public static string? CheckedOutBy(WsFile f, Action<string> log)
    {
        if (!f.CheckedOut) return null;
        try
        {
            var conn = VaultSession.Connection;
            if (f.CkOutUserId == conn.UserID) return "you";
            return conn.WebServiceManager.AdminService.GetUserByUserId(f.CkOutUserId)?.Name ?? "someone else";
        }
        catch (Exception ex)
        {
            log($"check-out user lookup failed: {ex.Message}");
            return "someone else";
        }
    }

    /// <summary>Open in Inventor, not dirty, but the local file predates the latest Vault version.</summary>
    public static bool OpenOlder(Inv.Application app, string? local, WsFile latest)
    {
        if (string.IsNullOrEmpty(local)) return false;
        try
        {
            var doc = DocumentOpener.FindOpen(app, local);
            if (doc is null || doc.Dirty) return false;
            return LocalCopy.IsOlderThanLatest(System.IO.File.GetLastWriteTimeUtc(local), latest.ModDate.ToUniversalTime());
        }
        catch { return false; }
    }

    /// <summary>A closed, not checked-out workspace file written after its Vault version: Get Latest would overwrite local edits.</summary>
    public static bool HasLocalEdits(Inv.Application app, string? local, WsFile latest)
    {
        if (string.IsNullOrEmpty(local) || latest.CheckedOut) return false;
        try
        {
            if (!System.IO.File.Exists(local) || DocumentOpener.FindOpen(app, local) is not null) return false;
            return LocalCopy.HasLocalEdits(System.IO.File.GetLastWriteTimeUtc(local), latest.ModDate.ToUniversalTime());
        }
        catch { return false; }
    }

    public static bool OpenDirty(Inv.Application app, string? local)
    {
        if (string.IsNullOrEmpty(local)) return false;
        try { return DocumentOpener.FindOpen(app, local)?.Dirty ?? false; }
        catch { return false; }
    }
}
