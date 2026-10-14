using System.Text;

namespace SFVaultBridge.Core;

/// <summary>Writes a text file so a crash or full disk never leaves a half-written file: write path.tmp beside it, then swap it in.</summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string text)
    {
        var full = Path.GetFullPath(path);
        var tmp = full + ".tmp";
        try
        {
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            if (File.Exists(full)) File.Replace(tmp, full, null);
            else File.Move(tmp, full);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { /* best effort */ }
            throw;
        }
    }
}
