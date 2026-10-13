using Inv = Inventor;

namespace SFVaultBridge.Addin;

/// <summary>A document opened (or reused) by <see cref="DocumentOpener"/>; closes it without saving only if this call opened it.</summary>
internal sealed class OpenedDocument : IDisposable
{
    private readonly Inv.Document? _toClose;

    public OpenedDocument(Inv.Document document, bool openedHere)
    {
        Document = document;
        OpenedHere = openedHere;
        _toClose = openedHere ? document : null;
    }

    public Inv.Document Document { get; }
    public bool OpenedHere { get; }

    public void Dispose()
    {
        try { _toClose?.Close(true); } catch { }
    }
}

/// <summary>Finds or invisibly opens Inventor documents; never closes one the user already had open.</summary>
internal static class DocumentOpener
{
    /// <summary>The document Inventor already has open at this path, or null.</summary>
    public static Inv.Document? FindOpen(Inv.Application app, string path)
    {
        var want = Normalize(path);
        foreach (Inv.Document d in app.Documents)
        {
            try { if (string.Equals(Normalize(d.FullFileName), want, StringComparison.OrdinalIgnoreCase)) return d; }
            catch { }
        }
        return null;
    }

    private static string Normalize(string p)
    {
        try { return System.IO.Path.GetFullPath(p); } catch { return p; }
    }

    /// <summary>InternalName of every open document, or null if any could not be read (then nothing may be closed).</summary>
    private static HashSet<string>? OpenIds(Inv.Application app)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Inv.Document d in app.Documents)
        {
            try { ids.Add(d.InternalName); } catch { return null; }
        }
        return ids;
    }

    /// <summary>Reuses an open document (never closed); otherwise opens it invisibly and closes it on Dispose.</summary>
    public static OpenedDocument Open(Inv.Application app, string path)
    {
        var silent = app.SilentOperation;
        app.SilentOperation = true;
        try
        {
            var doc = FindOpen(app, path);
            if (doc is not null) return new OpenedDocument(doc, false);
            var before = OpenIds(app);
            doc = app.Documents.Open(path, false);
            var wasOpen = true;
            if (before is not null)
            {
                try { wasOpen = before.Contains(doc.InternalName); } catch { wasOpen = true; }
            }
            return new OpenedDocument(doc, !wasOpen);
        }
        finally
        {
            try { app.SilentOperation = silent; } catch { }
        }
    }
}
