using System.Text.Json.Nodes;

namespace SFVaultBridge.Core;

/// <summary>Generic Dropbox upload as MCP tools: status, a preview that lists exactly which files would leave the machine, and the
/// apply that uploads them and returns a shared link. Nothing here touches Inventor, so every tool runs off the UI thread.</summary>
public static class DropboxTools
{
    public const string Family = "dropbox_upload";
    public static readonly TimeSpan UploadTimeout = TimeSpan.FromMinutes(30);

    public static IReadOnlyList<McpTool> Build(IDropboxUploader? dropbox, PathGuard guard, PreviewStore<DropboxUploadPlan> store)
    {
        if (dropbox is null) return Array.Empty<McpTool>();
        return new[]
        {
            new McpTool("dropbox_status", "Dropbox status",
                "Whether the bridge can upload to the team Dropbox: configured (an app key is set in Settings) and signed_in (this Windows user has approved the app). " +
                "When not signed in, the first dropbox_upload_apply opens the browser for a one-time Dropbox sign-in (3 minutes). Read-only.",
                Schema.Obj(new JsonObject()), McpToolKind.Read, _ => Status(dropbox), UiThread: false),

            new McpTool("dropbox_upload_preview", "Preview Dropbox upload",
                "Plan uploading one local file or one local folder (with subfolders) to the team Dropbox. Writes and uploads nothing. " +
                "Give 'local_path' (under Downloads, Desktop or Documents) and either 'partner' (lands in /Suppliers/<partner>/) or an explicit 'dropbox_folder'. " +
                "A folder is placed under that Dropbox folder as a subfolder named after it (override with 'subfolder'); a single file goes straight in. " +
                "Returns every file that would be sent with its size and Dropbox path, the total, whether a sign-in will be needed, and a preview_hash. " +
                "Show the list to the engineer: it is what leaves the company. Only after they approve call dropbox_upload_apply with the preview_hash and confirm true within 10 minutes.",
                Schema.Obj(Schema.Props(
                    ("local_path", Schema.Str("Full path of the file or folder to upload, for example C:\\Users\\me\\Downloads\\Acme package.")),
                    ("partner", Schema.Str("Partner or vendor name; the upload goes to /Suppliers/<partner>/. Use this or dropbox_folder.")),
                    ("dropbox_folder", Schema.Str("Explicit Dropbox folder starting with '/', for example /Suppliers/Acme/2026. Use this or partner.")),
                    ("subfolder", Schema.Str("For a folder upload: the name to give it in Dropbox (default: the local folder's own name).")),
                    ("include_subfolders", Schema.Bool("Include files in subfolders (default true).", true)),
                    ("share", Schema.Bool("Make a view-only shared link for the uploaded folder or file (default true).", true))), "local_path"),
                McpToolKind.Preview, a => Preview(dropbox, guard, store, a), UiThread: false),

            new McpTool("dropbox_upload_apply", "Upload to Dropbox",
                "Uploads the previewed files to Dropbox and returns the shared link. A WRITE to Dropbox (existing files with the same name are overwritten; Dropbox keeps versions). " +
                "Needs the preview_hash from dropbox_upload_preview (valid 10 minutes, one use) and confirm true, only after the engineer approved the file list. " +
                "If a listed file changed or disappeared since the preview it refuses and you must preview again. Runs as a job: a long upload returns a job_id to poll with job_status.",
                Schema.Obj(Schema.Props(
                    ("preview_hash", Schema.Str("The preview_hash returned by dropbox_upload_preview.")),
                    ("confirm", Schema.Bool("Must be true. Set it only after the engineer has seen the file list and said yes."))), "preview_hash", "confirm"),
                McpToolKind.Write, a => Apply(dropbox, store, a), UploadTimeout, UiThread: false),
        };
    }

    internal static JsonNode Status(IDropboxUploader dropbox)
    {
        var s = dropbox.Status();
        var o = new JsonObject { ["configured"] = s.Configured, ["signed_in"] = s.SignedIn, ["suppliers_root"] = DropboxPaths.Root };
        if (!s.Configured) o["next"] = "Set the Dropbox app key in the add-in Settings (Dropbox tab) or settings.json dropboxAppKey. See the README, Dropbox upload.";
        else if (!s.SignedIn) o["next"] = "Not signed in yet: the first dropbox_upload_apply opens the browser for the Dropbox sign-in.";
        return o;
    }

    internal static JsonNode Preview(IDropboxUploader dropbox, PathGuard guard, PreviewStore<DropboxUploadPlan> store, JsonObject a)
    {
        var status = dropbox.Status();
        if (!status.Configured)
            throw new BridgeException("not_configured", "No Dropbox app key is set. Put it in the add-in Settings (Dropbox tab) or settings.json as dropboxAppKey, then try again.");

        var local = guard.Check(Args.OptStr(a, "local_path"), "local_path");
        var partner = Args.OptStr(a, "partner").Trim();
        var explicitFolder = Args.OptStr(a, "dropbox_folder").Trim();
        if (partner.Length == 0 && explicitFolder.Length == 0)
            throw new BridgeException("bad_request", "Give 'partner' (upload to /Suppliers/<partner>/) or 'dropbox_folder'.");
        var folder = explicitFolder.Length > 0 ? DropboxUploadRules.CleanFolderPath(explicitFolder) : DropboxPaths.PartnerFolder(partner);
        var sub = Args.OptStr(a, "subfolder").Trim();
        var plan = DropboxUploadRules.Plan(local, folder, sub.Length > 0 ? sub : null, Args.Bool(a, "include_subfolders", true), Args.Bool(a, "share", true));

        var hash = Hash(plan);
        store.PutAs(hash, plan);
        var listed = plan.Items.Take(DropboxUploadRules.ListedFiles).Select(i => new JsonObject
        {
            ["local"] = i.LocalPath, ["dropbox"] = i.DropboxPath, ["bytes"] = i.Bytes,
        }).ToList();
        var o = new JsonObject
        {
            ["preview"] = true, ["local_path"] = plan.LocalPath, ["dropbox_folder"] = plan.DropboxFolder,
            ["file_count"] = plan.Items.Count, ["total_bytes"] = plan.TotalBytes, ["total_mb"] = Math.Round(plan.TotalBytes / 1048576.0, 1),
            ["share"] = plan.Share, ["share_target"] = plan.ShareTarget,
            ["files"] = new JsonArray(listed.Select(x => (JsonNode?)x).ToArray()),
        };
        if (plan.Items.Count > listed.Count) { o["files_truncated"] = true; o["files_not_listed"] = plan.Items.Count - listed.Count; }
        var warnings = new JsonArray();
        if (!status.SignedIn) warnings.Add("Not signed in to Dropbox: apply opens the browser for a one-time sign-in and waits up to 3 minutes.");
        if (warnings.Count > 0) o["warnings"] = warnings;
        o["preview_hash"] = hash;
        o["expires_in_minutes"] = (int)store.Ttl.TotalMinutes;
        o["next"] = $"Show the engineer the file list. If they approve, call dropbox_upload_apply with this preview_hash and confirm true within {(int)store.Ttl.TotalMinutes} minutes.";
        return o;
    }

    internal static JsonNode Apply(IDropboxUploader dropbox, PreviewStore<DropboxUploadPlan> store, JsonObject a)
    {
        if (!Args.Bool(a, "confirm", false))
            throw new BridgeException("confirmation_required", "Nothing uploaded. Show the engineer the dropbox_upload_preview result, and only after they say yes call this again with confirm true.");
        var plan = store.Take(Args.Str(a, "preview_hash").Trim())
                   ?? throw new BridgeException("no_matching_preview", $"Nothing uploaded. The preview_hash is unknown, already used, or older than {(int)store.Ttl.TotalMinutes} minutes. Run dropbox_upload_preview again, show it to the engineer, then apply.");
        var drift = DropboxUploadRules.Drift(plan);
        if (drift.Count > 0)
            throw new BridgeException("changed_since_preview", "Nothing uploaded. Files changed since the preview: " + string.Join("; ", drift.Take(5)) + ". Run dropbox_upload_preview again.");

        var r = dropbox.Upload(plan, CancellationToken.None);
        var failed = r.Files.Where(f => !f.Ok).ToList();
        var o = new JsonObject
        {
            ["ok"] = failed.Count == 0,
            ["dropbox_folder"] = plan.DropboxFolder,
            ["uploaded"] = r.Files.Count(f => f.Ok),
            ["failed"] = failed.Count,
            ["total_bytes"] = plan.TotalBytes,
        };
        if (failed.Count > 0)
            o["failures"] = new JsonArray(failed.Take(20).Select(f => (JsonNode?)new JsonObject { ["dropbox"] = f.DropboxPath, ["error"] = f.Error }).ToArray());
        if (r.ShareUrl is not null) { o["share_url"] = r.ShareUrl; o["visibility"] = r.Visibility; }
        if (r.Warnings.Count > 0) o["warnings"] = new JsonArray(r.Warnings.Select(w => (JsonNode?)w).ToArray());
        o["note"] = failed.Count == 0
            ? (r.ShareUrl is not null ? "Uploaded. Send the share_url to the partner." : "Uploaded.")
            : "Some files failed; fix the cause and run the preview and apply again (a rerun overwrites, so nothing is duplicated).";
        return o;
    }

    private static string Hash(DropboxUploadPlan plan)
    {
        var req = new JsonObject
        {
            ["folder"] = plan.DropboxFolder, ["share"] = plan.Share, ["target"] = plan.ShareTarget,
            ["items"] = new JsonArray(plan.Items.Select(i => (JsonNode?)new JsonObject { ["l"] = i.LocalPath, ["d"] = i.DropboxPath, ["b"] = i.Bytes }).ToArray()),
        };
        return PreviewGate.Hash(Family, req);
    }
}
