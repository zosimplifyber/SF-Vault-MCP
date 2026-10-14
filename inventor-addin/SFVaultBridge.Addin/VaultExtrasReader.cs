using System.Globalization;
using Autodesk.Connectivity.WebServices;
using SFVaultBridge.Core;
using VDF = Autodesk.DataManagement.Client.Framework;
using WsFile = Autodesk.Connectivity.WebServices.File;

namespace SFVaultBridge.Addin;

/// <summary>IVaultExtras on Inventor's signed-in Vault connection. Reads only, except SubmitJob, which adds one job to the Vault job queue (the PDF tool calls it after a confirmed preview).
/// Nothing here leaks exception text: failures surface as the generic internal error and are logged by the host.</summary>
internal sealed class VaultExtrasReader : IVaultExtras
{
    private static VDF.Vault.Currency.Connections.Connection Conn => VaultSession.Connection;
    private static DocumentService Ds => Conn.WebServiceManager.DocumentService;

    private const int RawSearchCap = 1000;
    private static readonly string[] CategoryClasses = { "FILE", "FLDR", "ITEM", "CO" };

    // ---- folders ----

    private static VaultFolderInfo Info(Folder f) => new(f.Id, f.Name ?? "", f.FullName ?? "", f.NumClds);

    private static Folder ResolveFolder(FolderRef r)
    {
        if (r.Id is { } id)
        {
            var byId = Ds.FindFoldersByIds(new[] { id })?.FirstOrDefault();
            return byId is null || byId.Id <= 0 ? throw new BridgeException("not_found", $"No Vault folder with folder_id {id}.") : byId;
        }
        var path = r.Path ?? "$";
        if (path == "$") return Ds.GetFolderRoot();
        var f = Ds.FindFoldersByPaths(new[] { path })?.FirstOrDefault();
        return f is null || f.Id <= 0 ? throw new BridgeException("not_found", $"No Vault folder at {path}.") : f;
    }

    public VaultFolderInfo FindFolder(string path) => Info(ResolveFolder(new FolderRef(path, null)));

    public FolderListing ListFolder(FolderRef folder, bool includeFiles, int limit)
    {
        var f = ResolveFolder(folder);
        var l = new VaultReader.Lookups();
        l.Folders[f.Id] = f.FullName ?? "";
        var subsAll = (f.NumClds > 0 ? Ds.GetFoldersByParentId(f.Id, false) : null) ?? Array.Empty<Folder>();
        var subs = subsAll.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Take(limit + 1).Select(Info).ToList();
        var files = new List<VaultFileSummary>();
        var filesTruncated = false;
        if (includeFiles)
        {
            var all = Ds.GetLatestFilesByFolderId(f.Id, false) ?? Array.Empty<WsFile>();
            var picked = all.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Take(limit + 1).ToList();
            filesTruncated = all.Length > limit;
            files = picked.Select(x => VaultReader.Summarize(l, x)).ToList();
        }
        return new FolderListing(Info(f), subs, files, subsAll.Length > limit, filesTruncated);
    }

    // ---- search ----

    private static PropDefLite Lite(PropDef d) => new(d.Id, d.DispName ?? "", d.SysName ?? "", d.Typ.ToString(), d.IsAct, d.IsSys, d.UsageCount);

    public IReadOnlyList<PropDefLite> FileSearchProperties() => PropertyDefinitions("FILE");

    public IReadOnlyList<PropDefLite> PropertyDefinitions(string entityClass) =>
        (Conn.WebServiceManager.PropertyService.GetPropertyDefinitionsByEntityClassId(entityClass) ?? Array.Empty<PropDef>()).Select(Lite).ToList();

    public VaultSearchResult Search(MappedSearch search)
    {
        var l = new VaultReader.Lookups();
        var conds = search.Conditions.Select(c => new SrchCond
        {
            PropDefId = c.PropDefId, PropTyp = PropertySearchType.SingleProperty, SrchOper = c.OperatorCode, SrchTxt = c.Text,
            SrchRule = search.Any ? SearchRuleType.May : SearchRuleType.Must,
        }).ToArray();
        long folderId = search.FolderPath is { } p ? ResolveFolder(new FolderRef(p, null)).Id : Ds.GetFolderRoot().Id;
        var postFilter = search.Conditions.Where(c => c.StartsWith).ToList();
        var want = postFilter.Count > 0 ? RawSearchCap : search.Limit + 1;

        var raw = new List<WsFile>();
        var seen = new HashSet<long>();
        var bookmark = string.Empty;
        var fetched = 0L;
        var more = false;
        while (raw.Count < want)
        {
            var page = Ds.FindFilesBySearchConditions(conds, null, new[] { folderId }, search.Recurse, search.LatestOnly, ref bookmark, out var status);
            if (page is null || page.Length == 0) break;
            fetched += page.Length;
            foreach (var f in page) if (seen.Add(f.Id)) raw.Add(f);
            if (status is null || fetched >= status.TotalHits) break;
            more = true;
        }
        var truncated = raw.Count > search.Limit && postFilter.Count == 0;
        if (postFilter.Count > 0)
        {
            raw = FilterStartsWith(raw, postFilter, search.Any, search.Conditions.Count);
            truncated = raw.Count > search.Limit;
        }
        else if (raw.Count >= want && more) truncated = true;
        var files = raw.Take(search.Limit).Select(f => VaultReader.Summarize(l, f)).ToList();
        return new VaultSearchResult(files, truncated);
    }

    // starts_with is searched as contains, then checked here against the files' own values (fetched 100 files at a time).
    private static List<WsFile> FilterStartsWith(List<WsFile> files, List<MappedCondition> starts, bool any, int totalConditions)
    {
        // With OR across other conditions a file that matched only those cannot be judged here, so everything stays.
        if (any && totalConditions > starts.Count) return files;
        var kept = new List<WsFile>();
        for (var i = 0; i < files.Count; i += 100)
        {
            var batch = files.Skip(i).Take(100).ToList();
            var values = new Dictionary<long, Dictionary<long, string>>();
            try
            {
                foreach (var p in Conn.WebServiceManager.PropertyService.GetPropertiesByEntityIds("FILE", batch.Select(f => f.Id).ToArray()) ?? Array.Empty<PropInst>())
                {
                    if (!values.TryGetValue(p.EntityId, out var m)) values[p.EntityId] = m = new Dictionary<long, string>();
                    m[p.PropDefId] = p.Val is DateTime dt ? dt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : p.Val?.ToString() ?? "";
                }
            }
            catch (Exception) { continue; /* unreadable: these files fail the check */ }
            foreach (var f in batch)
            {
                values.TryGetValue(f.Id, out var m);
                var ok = starts.Select(c => m is not null && m.TryGetValue(c.PropDefId, out var v) && v.StartsWith(c.Text, StringComparison.OrdinalIgnoreCase)).ToList();
                if (any ? ok.Any(x => x) : ok.All(x => x)) kept.Add(f);
            }
        }
        return kept;
    }

    // ---- job queue ----

    public bool JobQueueEnabled() => Conn.WebServiceManager.JobService.GetJobQueueEnabled();

    public IReadOnlyList<VaultJobInfo> RecentJobs(int max)
    {
        var jobs = Conn.WebServiceManager.JobService.GetJobsByDate(Math.Clamp(max, 1, 1000), DateTime.Now.AddDays(-30)) ?? Array.Empty<Job>();
        static long VersionOf(Job j)
        {
            var p = j.ParamArray?.FirstOrDefault(x => string.Equals(x.Name, "FileVersionId", StringComparison.OrdinalIgnoreCase));
            return p is not null && long.TryParse(p.Val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0;
        }
        var names = new Dictionary<long, string>();
        var ids = jobs.Select(VersionOf).Where(i => i > 0).Distinct().Take(200).ToArray();
        for (var i = 0; i < ids.Length; i += 100)
        {
            try
            {
                foreach (var f in Ds.GetFilesByIds(ids.Skip(i).Take(100).ToArray()) ?? Array.Empty<WsFile>()) names[f.Id] = f.Name;
            }
            catch (Exception) { /* names are a nicety */ }
        }
        return jobs.Select(j =>
        {
            var vid = VersionOf(j);
            return new VaultJobInfo(j.Id, j.Typ ?? "", j.StatusCode.ToString(), j.StatusMsg ?? "", j.CreateUserName ?? "", ReadTools.FormatDate(j.CreateDate),
                j.Descr ?? "", names.TryGetValue(vid, out var n) ? n : "", vid, j.Priority);
        }).ToList();
    }

    public PdfTarget ResolvePdfTarget(VaultFileRef file)
    {
        var l = new VaultReader.Lookups();
        var f = VaultReader.Resolve(l, file);
        var latest = Ds.GetLatestFileByMasterId(f.MasterId) ?? f;
        var s = VaultReader.Summarize(l, latest);
        return new PdfTarget(latest.Id, latest.MasterId, latest.Name, s.Path, latest.VerNum, s.State, s.Revision, latest.CheckedOut, s.CheckedOutBy);
    }

    public long SubmitJob(string jobType, string description, IReadOnlyDictionary<string, string> parameters, int priority)
    {
        var ps = parameters.Select(p => new JobParam { Name = p.Key, Val = p.Value }).ToArray();
        return Conn.WebServiceManager.JobService.AddJob(jobType, description, ps, priority).Id;
    }

    // ---- definitions ----

    public IReadOnlyList<CategoryInfoLite> Categories()
    {
        var byId = new Dictionary<long, (Cat Cat, List<string> Classes)>();
        foreach (var cls in CategoryClasses)
        {
            Cat[] cats;
            try { cats = Conn.WebServiceManager.CategoryService.GetCategoriesByEntityClassId(cls, true) ?? Array.Empty<Cat>(); }
            catch (Exception) { continue; }
            foreach (var c in cats)
            {
                if (!byId.TryGetValue(c.Id, out var e)) byId[c.Id] = e = (c, new List<string>());
                e.Classes.Add(cls);
            }
        }
        return byId.Values.Select(e => new CategoryInfoLite(e.Cat.Name ?? "", e.Cat.Descr ?? "", e.Classes)).ToList();
    }

    public IReadOnlyList<LifecycleDefInfo> Lifecycles() =>
        (Conn.WebServiceManager.LifeCycleService.GetAllLifeCycleDefinitions() ?? Array.Empty<LfCycDef>()).Select(d => new LifecycleDefInfo(
            d.DispName ?? d.Name ?? "", d.Descr ?? "",
            (d.StateArray ?? Array.Empty<LfCycState>()).OrderBy(s => s.DispOrder).Select(s => new LifecycleStateInfo(s.Id, s.Name ?? "", s.DispName ?? "", s.ReleasedState, s.ObsoleteState, s.IsDflt)).ToList(),
            (d.TransArray ?? Array.Empty<LfCycTrans>()).Select(t => new LifecycleTransitionInfo(t.FromId, t.ToId)).ToList())).ToList();

    public IReadOnlyList<RevisionSchemeInfo> RevisionSchemes()
    {
        var info = Conn.WebServiceManager.RevisionService.GetAllRevisionDefinitionInfo();
        var seqs = (info?.RevSeqArray ?? Array.Empty<RevSeq>()).ToDictionary(s => s.Id);
        RevisionSequenceInfo? Seq(string level, long id)
        {
            if (id <= 0 || !seqs.TryGetValue(id, out var s)) return null;
            return new RevisionSequenceInfo(level, s.DispName ?? s.Name ?? "", (s.Label?.LabelArray ?? Array.Empty<string>()).ToList());
        }
        return (info?.RevDefArray ?? Array.Empty<RevDef>()).Select(d => new RevisionSchemeInfo(
            d.DispName ?? d.SysName ?? "", d.Descr ?? "", d.Delim == '\0' ? "" : d.Delim.ToString(),
            new[] { Seq("primary", d.PriSchmId), Seq("secondary", d.SecSchmId), Seq("tertiary", d.TerSchmId) }.Where(x => x is not null).Select(x => x!).ToList())).ToList();
    }

    public IReadOnlyList<VaultUserInfo> Users() =>
        (Conn.WebServiceManager.AdminService.GetAllUsers() ?? Array.Empty<User>()).Where(u => !u.IsSys)
            .Select(u => new VaultUserInfo(u.Id, u.Name ?? "", u.Email ?? "", u.IsAct, u.Auth.ToString())).ToList();

    public IReadOnlyList<VaultGroupInfo> Groups()
    {
        var admin = Conn.WebServiceManager.AdminService;
        var groups = (admin.GetAllGroups() ?? Array.Empty<Group>()).ToList();
        var result = new List<VaultGroupInfo>();
        for (var i = 0; i < groups.Count; i += 50)
        {
            var batch = groups.Skip(i).Take(50).ToList();
            var infos = admin.GetGroupInfosByGroupIds(batch.Select(g => g.Id).ToArray()) ?? Array.Empty<GroupInfo>();
            foreach (var gi in infos)
            {
                if (gi?.Group is null) continue;
                result.Add(new VaultGroupInfo(gi.Group.Id, gi.Group.Name ?? "", gi.Group.EmailDL ?? "", gi.Group.IsAct,
                    (gi.Users ?? Array.Empty<User>()).Select(u => u.Name ?? "").ToList(),
                    (gi.Groups ?? Array.Empty<Group>()).Select(g => g.Name ?? "").ToList()));
            }
        }
        return result;
    }
}
