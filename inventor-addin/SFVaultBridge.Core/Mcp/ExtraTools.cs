using System.Text.Json.Nodes;

namespace SFVaultBridge.Core;

/// <summary>What the PDF job apply needs to remember from its preview.</summary>
public sealed record PdfJobPlan(long FileVersionId, long MasterId, string Name, string Path, string JobType);

/// <summary>Release 1.1 tools: folders and advanced search, the job queue (with the preview-first PDF job), and Vault definitions, users and groups.
/// Handlers take JSON and return JSON; Vault work goes through <see cref="IVaultExtras"/>.</summary>
public static class ExtraTools
{
    public const string PdfFamily = "pdf_job";
    public const int PdfPriority = 10;
    public static readonly string[] EntityClasses = { "FILE", "FLDR", "ITEM", "CO", "CUSTENT" };

    public static IReadOnlyList<McpTool> Build(IVaultExtras v, PreviewStore<PdfJobPlan> pdfStore) => new[]
    {
        // ---- folders and search ----
        new McpTool("vault_list_folder", "List Vault folder",
            "List what is inside a Vault folder: its subfolders and, unless include_files is false, its files with state, revision, category and version (latest version of each). " +
            "Give the folder as 'path' (for example $/DESIGNS/Simplifyber Library/McMaster-Carr) or 'folder_id'; with neither it lists the root $. " +
            "Use it to browse the library or see what a project folder holds; use vault_search_files to find a file by name. Not recursive. Read-only.",
            Schema.Obj(Schema.Props(
                ("path", Schema.Str("Vault folder path starting with $/ (or $ for the root).")),
                ("folder_id", Schema.Int("Alternative to 'path': a folder id from an earlier listing or vault_find_folder.", min: 1)),
                ("include_files", Schema.Bool("List the files too (default true). Set false for a quick look at the folder tree.", true)),
                ("limit", Schema.Int($"Most subfolders and most files to return, each (default {ExtraRules.ListDefault}, max {ExtraRules.ListMax}).", 1, ExtraRules.ListMax, ExtraRules.ListDefault)))),
            McpToolKind.Read, a => ListFolder(v, a)),

        new McpTool("vault_find_folder", "Find Vault folder",
            "Check that a Vault folder exists and get its folder_id, exact path and number of children. Use it to confirm a path before listing it or searching inside it. Fails with not_found if there is no such folder. Read-only.",
            Schema.Obj(Schema.Props(("path", Schema.Str("Vault folder path starting with $/ (or $ for the root)."))), "path"),
            McpToolKind.Read, a => FindFolder(v, a)),

        new McpTool("vault_advanced_search", "Advanced Vault file search",
            "Search Vault files by property conditions, for example State equals Released, or Vendor contains McMASTER and Category equals Part - Purchased. " +
            "Each condition is {property, operator, value}: property is a Vault property display or system name (see vault_property_definitions; State, Category, Revision, Vendor, Part Number and so on); " +
            "operator is one of contains, equals, not_equals, starts_with, greater_than, less_than, is_empty, is_not_empty (the last two take no value; contains and starts_with are for text, greater_than and less_than for numbers and dates). " +
            "All conditions must match (AND) unless any is true (OR). Optionally limit to a folder (recursing by default); latest_only (default true) returns only the newest version of each file. " +
            "An unknown property name is rejected with the closest names. Returns the same file fields as vault_search_files. Read-only.",
            Schema.Obj(Schema.Props(
                ("conditions", ConditionsSchema()),
                ("any", Schema.Bool("Match any condition (OR) instead of all (AND). Default false.", false)),
                ("folder", Schema.Str("Only search this Vault folder path (starting with $/). Default the whole vault.")),
                ("recurse", Schema.Bool("Include subfolders of 'folder' (default true).", true)),
                ("latest_only", Schema.Bool("Only the latest version of each file (default true).", true)),
                ("limit", Schema.Int($"Most files to return (default {ExtraRules.SearchDefault}, max {ExtraRules.SearchMax}).", 1, ExtraRules.SearchMax, ExtraRules.SearchDefault))), "conditions"),
            McpToolKind.Read, a => AdvancedSearch(v, a)),

        // ---- job queue ----
        new McpTool("vault_job_queue", "Vault job queue",
            "Recent jobs in the Vault job queue (created in the last 30 days), newest first: job id, type (for example Autodesk.Vault.PDF.Create.idw), status (Ready, Running, Success, Failure), status message, submitter, created time and the file name when the job is for a file. " +
            "Use it to check whether a PDF or STEP publish finished, failed or is still waiting for the Job Processor. status filter: active (ready or running), ready, running, success, failure, all (default all). Read-only.",
            Schema.Obj(Schema.Props(
                ("status", StatusSchema()),
                ("limit", Schema.Int($"Most jobs to return (default {ExtraRules.JobsDefault}, max {ExtraRules.JobsMax}).", 1, ExtraRules.JobsMax, ExtraRules.JobsDefault)))),
            McpToolKind.Read, a => JobQueue(v, a)),

        new McpTool("vault_job_queue_enabled", "Is the Vault job queue enabled",
            "Whether the Vault job queue is switched on. If it is off, jobs (such as a PDF publish) wait until it is enabled again and a Job Processor picks them up. Read-only.",
            Schema.Obj(new JsonObject()), McpToolKind.Read, _ => new JsonObject { ["enabled"] = v.JobQueueEnabled() }),

        new McpTool("vault_submit_pdf_job_preview", "Preview PDF job",
            "Plan queuing the standard Vault PDF publish job for the latest version of a drawing (.idw or .dwg). Writes nothing. " +
            "Returns the file and version, the job type, whether the queue is enabled, and a preview_hash. Show it to the engineer; only after they approve call vault_submit_pdf_job_apply with that preview_hash and confirm true within 10 minutes. " +
            "If a PDF job for that exact version is already waiting or running it says so and unlocks nothing.",
            Schema.Obj(Schema.Props(
                ("file", Schema.Str("The drawing: a file name such as CD-001909.idw (extension may be left off if unambiguous) or a full $/ path.")),
                ("file_id", Schema.Int("Alternative to 'file': the numeric file_id from a search.", min: 1)))),
            McpToolKind.Preview, a => PdfPreview(v, a, pdfStore)),

        new McpTool("vault_submit_pdf_job_apply", "Queue PDF job",
            "Adds the previewed PDF publish job to the Vault job queue. A WRITE (it only queues a job; the Vault Job Processor makes the PDF later). " +
            "Needs the preview_hash from vault_submit_pdf_job_preview (valid 10 minutes, one use) and confirm true, only after the engineer approved the preview. " +
            "If the drawing got a newer version since the preview it refuses and you must preview again. Follow up with vault_job_queue using the returned job_id.",
            Schema.Obj(Schema.Props(
                ("preview_hash", Schema.Str("The preview_hash returned by vault_submit_pdf_job_preview.")),
                ("confirm", Schema.Bool("Must be true. Set it only after the engineer has seen the preview and said yes."))), "preview_hash", "confirm"),
            McpToolKind.Write, a => PdfApply(v, a, pdfStore), FixDataCardTools.WriteTimeout),

        // ---- definitions, users ----
        new McpTool("vault_property_definitions", "Vault property definitions",
            "The property definitions Vault knows (name, system name, data type, active, usage count). Use it to find the exact property name to pass to vault_advanced_search. " +
            "'filter' keeps definitions whose display or system name contains the text; 'entity' picks which kind of object (default FILE; also FLDR folders, ITEM items, CO change orders, CUSTENT custom objects). Read-only.",
            Schema.Obj(Schema.Props(
                ("filter", Schema.Str("Text the name must contain, for example vendor.")),
                ("entity", EntitySchema()))),
            McpToolKind.Read, a => PropertyDefinitions(v, a)),

        new McpTool("vault_categories", "Vault categories",
            "The categories files, folders, items and change orders can have (for example Part - Purchased, Documents), with the kinds of object each applies to. Read-only.",
            Schema.Obj(new JsonObject()), McpToolKind.Read, _ => Categories(v)),

        new McpTool("vault_lifecycles", "Vault lifecycles",
            "The lifecycle definitions with every state (flagged released and/or obsolete, and which is the default) and the allowed transitions between states by name. " +
            "Use it to know which states count as released before searching or releasing. Read-only.",
            Schema.Obj(new JsonObject()), McpToolKind.Read, _ => Lifecycles(v)),

        new McpTool("vault_revision_schemes", "Vault revision schemes",
            "The revision schemes (for example numeric, letter) with their primary, secondary and tertiary label sequences (first labels shown). Read-only.",
            Schema.Obj(new JsonObject()), McpToolKind.Read, _ => RevisionSchemes(v)),

        new McpTool("vault_users", "Vault users",
            "The Vault users: name, email, whether active and how they sign in (Vault or Windows). Active users only by default; pass active_only false to include deactivated accounts. Read-only.",
            Schema.Obj(Schema.Props(("active_only", Schema.Bool("Only active users (default true).", true)))),
            McpToolKind.Read, a => Users(v, a)),

        new McpTool("vault_groups", "Vault groups",
            "The Vault groups with their members (users and child groups), email and whether active. Read-only.",
            Schema.Obj(new JsonObject()), McpToolKind.Read, _ => Groups(v)),
    };

    // ---- schemas ----

    private static JsonObject ConditionsSchema()
    {
        var item = Schema.Obj(Schema.Props(
            ("property", Schema.Str("Vault property display or system name, for example State, Category, Part Number, Vendor.")),
            ("operator", new JsonObject
            {
                ["type"] = "string", ["description"] = "How to compare.",
                ["enum"] = new JsonArray(ExtraRules.OperatorNames.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()),
            }),
            ("value", Schema.Str("Text, number or date (yyyy-MM-dd) to compare with. Omit for is_empty and is_not_empty."))), "property", "operator");
        return new JsonObject
        {
            ["type"] = "array", ["items"] = item, ["minItems"] = 1, ["maxItems"] = ExtraRules.MaxConditions,
            ["description"] = $"One or more conditions, at most {ExtraRules.MaxConditions}.",
        };
    }

    private static JsonObject StatusSchema() => new()
    {
        ["type"] = "string", ["description"] = "Which jobs to list (default all).",
        ["enum"] = new JsonArray(ExtraRules.JobStatuses.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()),
    };

    private static JsonObject EntitySchema() => new()
    {
        ["type"] = "string", ["description"] = "Kind of object (default FILE).",
        ["enum"] = new JsonArray(EntityClasses.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()),
    };

    // ---- folders and search ----

    internal static JsonNode ListFolder(IVaultExtras v, JsonObject a)
    {
        var folder = ExtraRules.ParseFolderRef(a);
        var includeFiles = Args.Bool(a, "include_files", true);
        var limit = ReadTools.Clamp(a, "limit", ExtraRules.ListDefault, 1, ExtraRules.ListMax);
        var r = v.ListFolder(folder, includeFiles, limit);
        var subs = r.Subfolders.Take(limit).Select(Folder).ToList();
        var o = new JsonObject { ["folder"] = Folder(r.Folder), ["subfolder_count"] = subs.Count, ["subfolders"] = Arr(subs) };
        if (r.SubfoldersTruncated || r.Subfolders.Count > limit) o["subfolders_truncated"] = true;
        if (includeFiles)
        {
            var files = r.Files.Take(limit).Select(ReadTools.File).ToList();
            o["file_count"] = files.Count;
            o["files"] = Arr(files);
            if (r.FilesTruncated || r.Files.Count > limit) o["files_truncated"] = true;
        }
        return o;
    }

    internal static JsonNode FindFolder(IVaultExtras v, JsonObject a) =>
        Folder(v.FindFolder(ExtraRules.NormalizeFolderPath(Args.Str(a, "path"))));

    private static JsonObject Folder(VaultFolderInfo f) => new() { ["folder_id"] = f.Id, ["name"] = f.Name, ["path"] = f.Path, ["children"] = f.ChildCount };

    internal static JsonNode AdvancedSearch(IVaultExtras v, JsonObject a)
    {
        var spec = ExtraRules.ParseSearch(a);
        var mapped = ExtraRules.Map(spec, v.FileSearchProperties());
        var r = v.Search(mapped);
        var files = r.Files.Take(spec.Limit).ToList();
        var o = new JsonObject
        {
            ["match"] = spec.Any ? "any" : "all",
            ["conditions"] = Arr(mapped.Conditions.Zip(spec.Conditions, (m, c) => new JsonObject
            {
                ["property"] = m.PropName, ["operator"] = ExtraRules.OpName(c.Op), ["value"] = c.Value,
            })),
            ["count"] = files.Count,
            ["files"] = Arr(files.Select(ReadTools.File)),
        };
        if (spec.FolderPath is not null) o["folder"] = spec.FolderPath;
        if (r.Truncated || r.Files.Count > spec.Limit) o["truncated"] = true;
        if (r.Partial) o["partial"] = true;
        if (r.RawCapHit) o["raw_count"] = r.RawCount;
        if (!string.IsNullOrEmpty(r.Warning)) o["warning"] = r.Warning;
        if (files.Count == 0 && !r.Partial && !r.Truncated) o["hint"] = "No file matches. Check the property names with vault_property_definitions, or loosen a condition.";
        return o;
    }

    // ---- job queue ----

    internal static JsonNode JobQueue(IVaultExtras v, JsonObject a)
    {
        var status = Args.OptStr(a, "status", "all").Trim().ToLowerInvariant();
        if (status.Length == 0) status = "all";
        if (!ExtraRules.JobStatuses.Contains(status))
            throw new BridgeException("bad_request", $"'status' must be one of {string.Join(", ", ExtraRules.JobStatuses)}.");
        var limit = ReadTools.Clamp(a, "limit", ExtraRules.JobsDefault, 1, ExtraRules.JobsMax);
        var all = v.RecentJobs(ExtraRules.JobsMax * 5);
        var matches = all.Where(j => ExtraRules.JobMatches(j, status)).OrderByDescending(j => j.Created, StringComparer.Ordinal).ThenByDescending(j => j.Id).ToList();
        var rows = matches.Take(limit).Select(j =>
        {
            var o = new JsonObject { ["job_id"] = j.Id, ["type"] = j.Type, ["status"] = j.Status };
            Put(o, "message", j.Message); Put(o, "submitter", j.Submitter); Put(o, "created", j.Created);
            Put(o, "description", j.Description); Put(o, "file", j.FileName);
            if (j.FileVersionId > 0) o["file_version_id"] = j.FileVersionId;
            return o;
        }).ToList();
        var res = new JsonObject { ["status_filter"] = status, ["count"] = rows.Count, ["jobs"] = Arr(rows) };
        if (matches.Count > rows.Count) { res["truncated"] = true; res["total_matching"] = matches.Count; }
        return res;
    }

    private static JsonObject PdfFile(PdfTarget t)
    {
        var o = new JsonObject { ["name"] = t.Name, ["path"] = t.Path, ["version"] = t.Version, ["file_version_id"] = t.FileVersionId };
        Put(o, "state", t.State); Put(o, "revision", t.Revision);
        return o;
    }

    internal static string PdfHash(PdfJobPlan p) => PreviewGate.Hash(PdfFamily, new JsonObject
    {
        ["fileVersionId"] = p.FileVersionId, ["jobType"] = p.JobType, ["name"] = p.Name,
    });

    internal static JsonNode PdfPreview(IVaultExtras v, JsonObject a, PreviewStore<PdfJobPlan> store)
    {
        var t = v.ResolvePdfTarget(FileRefRules.Parse(a));
        var (type, parms) = ExtraRules.PdfJobFor(t.Name, t.FileVersionId);
        var enabled = v.JobQueueEnabled();
        var o = new JsonObject
        {
            ["preview"] = true, ["file"] = PdfFile(t), ["job_type"] = type, ["queue_enabled"] = enabled,
            ["params"] = new JsonObject(parms.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, p.Value))),
        };
        var warnings = new JsonArray();
        if (!enabled) warnings.Add("The Vault job queue is disabled: the job will wait until it is enabled again.");
        if (t.CheckedOut) warnings.Add($"The drawing is checked out by {(t.CheckedOutBy.Length > 0 ? t.CheckedOutBy : "someone")}; the PDF is made from the last checked-in version ({t.Version}).");
        var pending = v.RecentJobs(ExtraRules.JobsMax * 5).FirstOrDefault(j =>
            j.FileVersionId == t.FileVersionId && j.Type == type && j.Status is "Ready" or "Running");
        if (pending is not null)
        {
            o["already_queued"] = true;
            o["existing_job_id"] = pending.Id;
            o["next"] = $"A PDF job for this version is already {pending.Status.ToLowerInvariant()} (job {pending.Id}). Nothing to submit; check it with vault_job_queue.";
            if (warnings.Count > 0) o["warnings"] = warnings;
            return o;
        }
        var plan = new PdfJobPlan(t.FileVersionId, t.MasterId, t.Name, t.Path, type);
        var hash = PdfHash(plan);
        store.PutAs(hash, plan);
        o["preview_hash"] = hash;
        o["expires_in_minutes"] = (int)store.Ttl.TotalMinutes;
        if (warnings.Count > 0) o["warnings"] = warnings;
        o["next"] = $"Show this to the engineer. If they approve, call vault_submit_pdf_job_apply with this preview_hash and confirm true within {(int)store.Ttl.TotalMinutes} minutes.";
        return o;
    }

    internal static JsonNode PdfApply(IVaultExtras v, JsonObject a, PreviewStore<PdfJobPlan> store)
    {
        if (!Args.Bool(a, "confirm", false))
            throw new BridgeException("confirmation_required", "Not queued. Show the engineer the vault_submit_pdf_job_preview result, and only after they say yes call this again with confirm true.");
        var plan = store.Take(Args.Str(a, "preview_hash").Trim())
                   ?? throw new BridgeException("no_matching_preview", $"Not queued. The preview_hash is unknown, already used, or older than {(int)store.Ttl.TotalMinutes} minutes. Run vault_submit_pdf_job_preview again, show it to the engineer, then apply.");
        var now = v.ResolvePdfTarget(new VaultFileRef(null, plan.FileVersionId));
        if (now.FileVersionId != plan.FileVersionId)
            throw new BridgeException("changed_since_preview", $"{plan.Name} has a newer version (now version {now.Version}) since the preview. Run vault_submit_pdf_job_preview again.");
        var (type, parms) = ExtraRules.PdfJobFor(plan.Name, plan.FileVersionId);
        var id = v.SubmitJob(type, $"PDF Create: {plan.Name}", parms, PdfPriority);
        var o = new JsonObject { ["ok"] = true, ["job_id"] = id, ["job_type"] = type, ["file"] = PdfFile(now), ["queue_enabled"] = v.JobQueueEnabled() };
        o["note"] = "Queued. The Vault Job Processor makes the PDF; check progress with vault_job_queue.";
        return o;
    }

    // ---- definitions ----

    internal static JsonNode PropertyDefinitions(IVaultExtras v, JsonObject a)
    {
        var entity = Args.OptStr(a, "entity", "FILE").Trim().ToUpperInvariant();
        if (entity.Length == 0) entity = "FILE";
        if (!EntityClasses.Contains(entity)) throw new BridgeException("bad_request", $"'entity' must be one of {string.Join(", ", EntityClasses)}.");
        var filter = Args.OptStr(a, "filter").Trim();
        var defs = v.PropertyDefinitions(entity)
            .Where(d => filter.Length == 0 || d.DispName.Contains(filter, StringComparison.OrdinalIgnoreCase) || d.SysName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.DispName, StringComparer.OrdinalIgnoreCase).ToList();
        var shown = defs.Take(ExtraRules.ListMax).Select(d =>
        {
            var o = new JsonObject { ["name"] = d.DispName, ["system_name"] = d.SysName, ["type"] = d.DataType, ["active"] = d.Active };
            if (d.IsSystem) o["system"] = true;
            o["usage_count"] = d.UsageCount;
            return o;
        }).ToList();
        var res = new JsonObject { ["entity"] = entity, ["count"] = shown.Count, ["definitions"] = Arr(shown) };
        if (defs.Count > shown.Count) { res["truncated"] = true; res["total_matching"] = defs.Count; res["hint"] = "Narrow with 'filter'."; }
        return res;
    }

    internal static JsonNode Categories(IVaultExtras v)
    {
        var src = v.Categories().OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var rows = src.Take(ExtraRules.ListCap).Select(c =>
        {
            var o = new JsonObject { ["name"] = c.Name };
            Put(o, "description", c.Description);
            o["applies_to"] = new JsonArray(c.EntityClasses.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray());
            return o;
        }).ToList();
        return Capped(new JsonObject { ["count"] = rows.Count, ["categories"] = Arr(rows) }, src.Count, rows.Count);
    }

    internal static JsonNode Lifecycles(IVaultExtras v)
    {
        var src = v.Lifecycles();
        var rows = src.Take(ExtraRules.ListCap).Select(d =>
        {
            var names = d.States.ToDictionary(s => s.Id, s => s.Name);
            var o = new JsonObject { ["name"] = d.Name };
            Put(o, "description", d.Description);
            o["states"] = Arr(d.States.Select(s =>
            {
                var so = new JsonObject { ["name"] = s.Name, ["released"] = s.Released, ["obsolete"] = s.Obsolete };
                if (s.Default) so["default"] = true;
                if (!string.IsNullOrWhiteSpace(s.DisplayName) && s.DisplayName != s.Name) so["display_name"] = s.DisplayName;
                return so;
            }));
            o["transitions"] = Arr(d.Transitions.Where(t => names.ContainsKey(t.FromId) && names.ContainsKey(t.ToId))
                .Select(t => new JsonObject { ["from"] = names[t.FromId], ["to"] = names[t.ToId] }));
            return o;
        }).ToList();
        return Capped(new JsonObject { ["count"] = rows.Count, ["lifecycles"] = Arr(rows) }, src.Count, rows.Count);
    }

    internal static JsonNode RevisionSchemes(IVaultExtras v)
    {
        var src = v.RevisionSchemes();
        var rows = src.Take(ExtraRules.ListCap).Select(s =>
        {
            var o = new JsonObject { ["name"] = s.Name };
            Put(o, "description", s.Description); Put(o, "delimiter", s.Delimiter);
            o["sequences"] = Arr(s.Sequences.Select(q =>
            {
                var qo = new JsonObject { ["level"] = q.Level, ["name"] = q.Name, ["label_count"] = q.Labels.Count };
                qo["first_labels"] = new JsonArray(q.Labels.Take(12).Select(l => (JsonNode?)JsonValue.Create(l)).ToArray());
                return qo;
            }));
            return o;
        }).ToList();
        return Capped(new JsonObject { ["count"] = rows.Count, ["revision_schemes"] = Arr(rows) }, src.Count, rows.Count);
    }

    internal static JsonNode Users(IVaultExtras v, JsonObject a)
    {
        var activeOnly = Args.Bool(a, "active_only", true);
        var src = v.Users().Where(u => !activeOnly || u.Active).OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var rows = src.Take(ExtraRules.ListCap).Select(u =>
        {
            var o = new JsonObject { ["name"] = u.Name };
            Put(o, "email", u.Email); Put(o, "sign_in", u.Auth);
            o["active"] = u.Active;
            return o;
        }).ToList();
        return Capped(new JsonObject { ["count"] = rows.Count, ["users"] = Arr(rows) }, src.Count, rows.Count);
    }

    internal static JsonNode Groups(IVaultExtras v)
    {
        var src = v.Groups().OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var rows = src.Take(ExtraRules.ListCap).Select(g =>
        {
            var o = new JsonObject { ["name"] = g.Name };
            Put(o, "email", g.Email);
            o["active"] = g.Active;
            o["member_count"] = g.Users.Count;
            o["users"] = new JsonArray(g.Users.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(ExtraRules.MemberListMax).Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
            if (g.Users.Count > ExtraRules.MemberListMax) o["users_truncated"] = true;
            if (g.ChildGroups.Count > 0) o["groups"] = new JsonArray(g.ChildGroups.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
            return o;
        }).ToList();
        return Capped(new JsonObject { ["count"] = rows.Count, ["groups"] = Arr(rows) }, src.Count, rows.Count);
    }

    // ---- helpers ----

    private static JsonObject Capped(JsonObject o, int total, int shown)
    {
        if (total > shown) { o["truncated"] = true; o["total"] = total; }
        return o;
    }

    private static void Put(JsonObject o, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) o[key] = value;
    }

    private static JsonArray Arr(IEnumerable<JsonObject> items) => new(items.Select(i => (JsonNode?)i).ToArray());
}
