using SFVaultBridge.Core;
using WsFile = Autodesk.Connectivity.WebServices.File;

namespace SFVaultBridge.Addin;

/// <summary>Finds the Vault drawings that belong to a model file.</summary>
internal static class DrawingFinder
{
    /// <summary>Where-used drawings plus any .idw/.dwg in Vault named like the model, de-duplicated by master id.</summary>
    public static IReadOnlyList<WsFile> Candidates(WsFile model, Action<string> log)
    {
        var list = new List<WsFile>(VaultFiles.WhereUsedDrawings(model));
        try
        {
            foreach (var f in VaultFiles.DrawingsNamed(System.IO.Path.GetFileNameWithoutExtension(model.Name)))
                if (list.All(x => x.MasterId != f.MasterId)) list.Add(f);
        }
        catch (Exception ex) { log($"review name search for {model.Name} failed: {ex.Message}"); }
        return list;
    }

    /// <summary>The single drawing the review rules pick for a part, or null.</summary>
    public static WsFile? PartDrawing(WsFile model, Action<string> log)
    {
        var candidates = Candidates(model, log);
        var name = ReviewRules.PickDrawings(model.Name, candidates.Select(c => c.Name)).FirstOrDefault();
        return name is null ? null : candidates.First(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}
