using System.Globalization;
using Autodesk.Connectivity.WebServices;
using SFVaultBridge.Core;
using VDF = Autodesk.DataManagement.Client.Framework;
using WsFile = Autodesk.Connectivity.WebServices.File;

namespace SFVaultBridge.Addin;

/// <summary>IVaultReader on Inventor's signed-in Vault connection. Read-only: nothing here adds, checks out, changes or downloads a file.</summary>
internal sealed class VaultReader : IVaultReader
{
    private static VDF.Vault.Currency.Connections.Connection Conn => VaultSession.Connection;
    private static DocumentService Ds => Conn.WebServiceManager.DocumentService;

    private static readonly string[] SearchProperties = { "Name", "Description", "Title", "Part Number", "Vendor Number" };

    // ---- summaries ----

    private sealed class Lookups
    {
        public readonly Dictionary<long, string> Folders = new();
        public readonly Dictionary<long, string> Users = new();
    }

    private static string FolderOf(Lookups l, long folderId)
    {
        if (l.Folders.TryGetValue(folderId, out var p)) return p;
        try { p = Ds.GetFolderById(folderId)?.FullName ?? ""; } catch (Exception) { p = ""; }
        return l.Folders[folderId] = p;
    }

    private static string UserOf(Lookups l, long userId)
    {
        if (l.Users.TryGetValue(userId, out var n)) return n;
        if (userId == Conn.UserID) n = "you";
        else
        {
            try { n = Conn.WebServiceManager.AdminService.GetUserByUserId(userId)?.Name ?? "someone else"; } catch (Exception) { n = "someone"; }
        }
        return l.Users[userId] = n;
    }

    private static VaultFileSummary Summarize(Lookups l, WsFile f) =>
        new(f.Id, f.MasterId, f.Name, FolderOf(l, f.FolderId), VaultFiles.StateOf(f), VaultFiles.RevisionOf(f), VaultFiles.CategoryName(f) ?? "",
            f.VerNum, f.MaxCkInVerNum, f.CheckedOut, f.CheckedOut ? UserOf(l, f.CkOutUserId) : "");

    // ---- resolving ----

    private static WsFile Resolve(Lookups l, VaultFileRef r)
    {
        if (r.FileId is { } id)
        {
            WsFile? byId;
            try { byId = Ds.GetFileById(id); }
            catch (Exception) { byId = null; }
            return byId is null || byId.Id <= 0 ? throw new BridgeException("not_found", $"No Vault file with file_id {id}.") : byId;
        }
        var text = r.Text!;
        if (FileRefRules.IsPath(text))
            return VaultFiles.FindLatest(text) ?? throw new BridgeException("not_found", $"No file at {text} in Vault.");
        var hits = VaultFiles.FindByNameContaining(text).GroupBy(f => f.MasterId).Select(g => g.First()).ToList();
        var items = hits.Select(f => (File: f, Path: FolderOf(l, f.FolderId).TrimEnd('/') + "/" + f.Name)).ToList();
        return FileRefRules.Pick(text, items, x => x.File.Name, x => x.Path).File;
    }

    // ---- search ----

    public VaultSearchResult SearchFiles(string query, int limit)
    {
        var l = new Lookups();
        var defs = Conn.WebServiceManager.PropertyService.GetPropertyDefinitionsByEntityClassId("FILE") ?? Array.Empty<PropDef>();
        var root = Ds.GetFolderRoot();
        var found = new Dictionary<long, WsFile>();
        var truncated = false;
        foreach (var name in SearchProperties)
        {
            var def = (name == "Name" ? defs.FirstOrDefault(d => d.SysName == "Name") ?? defs.FirstOrDefault(d => d.SysName == "ClientFileName") : null)
                      ?? defs.FirstOrDefault(d => string.Equals(d.DispName, name, StringComparison.OrdinalIgnoreCase));
            if (def is null) continue;
            var cond = new SrchCond { PropDefId = def.Id, PropTyp = PropertySearchType.SingleProperty, SrchOper = 1, SrchTxt = query, SrchRule = SearchRuleType.Must };
            try
            {
                var bookmark = string.Empty;
                var fetched = 0L;
                while (found.Count <= limit)
                {
                    var page = Ds.FindFilesBySearchConditions(new[] { cond }, null, new[] { root.Id }, true, true, ref bookmark, out var status);
                    if (page is null || page.Length == 0) break;
                    fetched += page.Length;
                    foreach (var f in page) found.TryAdd(f.MasterId, f);
                    if (status is null || fetched >= status.TotalHits) break;
                }
            }
            catch (Exception) when (name != "Name") { /* a property that cannot be searched as text is skipped */ }
            if (found.Count > limit) { truncated = true; break; }
        }
        var files = found.Values.Take(limit).Select(f => Summarize(l, f)).ToList();
        return new VaultSearchResult(files, truncated);
    }

    // ---- one file ----

    public VaultFileDetail GetFile(VaultFileRef file)
    {
        var l = new Lookups();
        var f = Resolve(l, file);
        var ps = Conn.WebServiceManager.PropertyService;
        var defs = (ps.GetPropertyDefinitionsByEntityClassId("FILE") ?? Array.Empty<PropDef>()).ToDictionary(d => d.Id, d => d.DispName);
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in ps.GetPropertiesByEntityIds("FILE", new[] { f.Id }) ?? Array.Empty<PropInst>())
            {
                var text = p.Val is DateTime dt ? dt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : p.Val?.ToString();
                if (!string.IsNullOrWhiteSpace(text) && defs.TryGetValue(p.PropDefId, out var name) && !string.IsNullOrWhiteSpace(name)) props[name] = text.Trim();
            }
        }
        catch (Exception) { /* properties unreadable: the rest is still useful */ }
        bool? released = null;
        try
        {
            var stateId = f.FileLfCyc?.LfCycStateId ?? 0;
            if (stateId > 0) released = Conn.WebServiceManager.LifeCycleService.GetLifeCycleStatesByIds(new[] { stateId })?.FirstOrDefault()?.ReleasedState;
        }
        catch (Exception) { }
        bool? linked = null;
        try { linked = Conn.WebServiceManager.ItemService.GetItemsByFileId(f.Id) is { Length: > 0 }; } catch (Exception) { }
        props.TryGetValue("Property Compliance", out var compliance);
        return new VaultFileDetail(Summarize(l, f), released, f.FileSize, ReadTools.FormatDate(f.ModDate), ReadTools.FormatDate(f.CreateDate),
            f.CreateUserName ?? "", f.Comm ?? "", props, compliance, linked);
    }

    // ---- relations ----

    public VaultRelations WhereUsed(VaultFileRef file, int levels, int maxRows)
    {
        var l = new Lookups();
        var subject = Resolve(l, file);
        var rows = new List<VaultRelation>();
        var seen = new HashSet<long> { subject.MasterId };
        var frontier = new List<WsFile> { subject };
        var truncated = false;
        for (var level = 1; level <= levels && frontier.Count > 0 && !truncated; level++)
        {
            // Associations point at the exact child iteration, so ask about every version of each file we climb from.
            var owner = new Dictionary<long, WsFile>();
            foreach (var f in frontier)
            {
                owner[f.Id] = f;
                foreach (var v in Ds.GetFilesByMasterId(f.MasterId) ?? Array.Empty<WsFile>()) owner[v.Id] = f;
            }
            var assocs = Ds.GetFileAssociationLitesByIds(owner.Keys.ToArray(), FileAssocAlg.LatestTip,
                FileAssociationTypeEnum.All, false, FileAssociationTypeEnum.None, false, true, false, false) ?? Array.Empty<FileAssocLite>();
            var rel = assocs.Where(a => owner.ContainsKey(a.CldFileId)).ToList();
            if (rel.Count == 0) break;
            var parents = (Ds.GetFilesByIds(rel.Select(a => a.ParFileId).Distinct().ToArray()) ?? Array.Empty<WsFile>()).ToDictionary(p => p.Id);
            var next = new List<WsFile>();
            foreach (var a in rel)
            {
                if (!parents.TryGetValue(a.ParFileId, out var parent) || !seen.Add(parent.MasterId)) continue;
                if (rows.Count >= maxRows) { truncated = true; break; }
                rows.Add(new VaultRelation(Summarize(l, parent), a.Typ.ToString(), level, owner[a.CldFileId].Name, 0));
                next.Add(parent);
            }
            frontier = next;
        }
        return new VaultRelations(Summarize(l, subject), rows, truncated);
    }

    public VaultRelations Uses(VaultFileRef file, int levels, int maxRows)
    {
        var l = new Lookups();
        var subject = Resolve(l, file);
        var rows = new List<VaultRelation>();
        var seen = new HashSet<long> { subject.MasterId };
        var frontier = new List<WsFile> { subject };
        var truncated = false;
        for (var level = 1; level <= levels && frontier.Count > 0 && !truncated; level++)
        {
            var byId = frontier.ToDictionary(f => f.Id);
            var assocs = Ds.GetFileAssociationLitesByIds(byId.Keys.ToArray(), FileAssocAlg.Actual,
                FileAssociationTypeEnum.None, false, FileAssociationTypeEnum.All, false, true, false, false) ?? Array.Empty<FileAssocLite>();
            var rel = assocs.Where(a => byId.ContainsKey(a.ParFileId)).ToList();
            if (rel.Count == 0) break;
            var children = (Ds.GetFilesByIds(rel.Select(a => a.CldFileId).Distinct().ToArray()) ?? Array.Empty<WsFile>()).ToDictionary(c => c.Id);
            var next = new List<WsFile>();
            foreach (var a in rel)
            {
                if (!children.TryGetValue(a.CldFileId, out var child) || !seen.Add(child.MasterId)) continue;
                if (rows.Count >= maxRows) { truncated = true; break; }
                rows.Add(new VaultRelation(Summarize(l, child), a.Typ.ToString(), level, byId[a.ParFileId].Name, child.VerNum));
                next.Add(child);
            }
            frontier = next;
        }
        return new VaultRelations(Summarize(l, subject), rows, truncated);
    }

    // ---- versions ----

    public VaultVersions Versions(VaultFileRef file, int limit)
    {
        var l = new Lookups();
        var f = Resolve(l, file);
        var all = (Ds.GetFilesByMasterId(f.MasterId) ?? Array.Empty<WsFile>()).OrderByDescending(v => v.VerNum).ToList();
        var shown = all.Take(limit).Select(v => new VaultVersionInfo(v.Id, v.VerNum, v.FileRev?.Label ?? "", v.FileLfCyc?.LfCycStateName ?? "",
            v.Comm ?? "", ReadTools.FormatDate(v.CkInDate), v.CreateUserName ?? "")).ToList();
        return new VaultVersions(Summarize(l, f), shown, all.Count > limit);
    }
}
