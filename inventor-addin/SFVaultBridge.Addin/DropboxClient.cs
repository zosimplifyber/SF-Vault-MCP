using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using SFVaultBridge.Core;

namespace SFVaultBridge.Addin;

public sealed record DropboxUploadResult(string DropboxPath, string? ShareUrl, string? Visibility, IReadOnlyList<string> Warnings);

/// <summary>Uploads a zip under /Suppliers/&lt;Partner&gt;/ and makes a view-only shared link. Failures become warnings.</summary>
public sealed class DropboxClient
{
    private const string Api = "https://api.dropboxapi.com/2/";
    private const string Content = "https://content.dropboxapi.com/2/";

    private readonly DropboxAuth _auth;
    private readonly HttpClient _http;
    private string? _rootNs;
    private bool _rootChecked;

    public DropboxClient(DropboxAuth auth, HttpClient http)
    {
        _auth = auth;
        _http = http;
        _auth.AccountChanged += ResetAccount;
    }

    /// <summary>Forget the cached team path root; call after a sign-in or sign-out.</summary>
    public void ResetAccount() { _rootNs = null; _rootChecked = false; }

    public static HttpClient NewHttpClient() => new() { Timeout = Timeout.InfiniteTimeSpan };

    private sealed class DropboxException : Exception
    {
        public DropboxException(int status, string body) : base("Dropbox " + status + ": " + DropboxJson.ErrorSummary(body)) { Status = status; Body = body; }
        public int Status { get; }
        public string Body { get; }
    }

    public async Task<DropboxUploadResult> UploadAndShareAsync(string localZip, string partner, CancellationToken ct)
    {
        var warnings = new List<string>();
        var dest = DropboxPaths.FilePath(partner, Path.GetFileName(localZip));
        try
        {
            await EnsureRootAsync(ct).ConfigureAwait(false);
            foreach (var folder in DropboxPaths.FoldersFor(partner))
                await CreateFolderAsync(folder, ct).ConfigureAwait(false);
            await UploadFileAsync(localZip, dest, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            warnings.Add("Dropbox upload failed: " + e.Message);
            return new DropboxUploadResult("", null, null, warnings);
        }

        try
        {
            var (url, vis) = await ShareAsync(dest, ct).ConfigureAwait(false);
            if (vis == "team_only") warnings.Add("Team policy does not allow public links; the link works for Dropbox team members only.");
            return new DropboxUploadResult(dest, url, vis, warnings);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            warnings.Add("Dropbox share link failed (the file is uploaded): " + e.Message);
            return new DropboxUploadResult(dest, null, null, warnings);
        }
    }

    private async Task EnsureRootAsync(CancellationToken ct)
    {
        if (_rootChecked) return;
        var body = await SendAsync(() => Json(Api + "users/get_current_account", null), usePathRoot: false, ct).ConfigureAwait(false);
        _rootNs = DropboxJson.NeedsPathRoot(body, out var ns) ? ns : null;
        _rootChecked = true;
    }

    private async Task CreateFolderAsync(string path, CancellationToken ct)
    {
        try
        {
            await SendAsync(() => Json(Api + "files/create_folder_v2", DropboxJson.CreateFolderArg(path)), true, ct).ConfigureAwait(false);
        }
        catch (DropboxException e) when (e.Status == 409 && e.Body.Contains("conflict", StringComparison.Ordinal))
        {
            // folder already exists
        }
    }

    private async Task UploadFileAsync(string localZip, string dest, CancellationToken ct)
    {
        var length = new FileInfo(localZip).Length;
        var chunks = UploadPlan.Chunks(length);
        if (chunks.Count == 0)
        {
            await SendAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, Content + "files/upload");
                req.Headers.TryAddWithoutValidation("Dropbox-API-Arg", DropboxJson.UploadArg(dest));
                req.Content = new StreamContent(new FileStream(localZip, FileMode.Open, FileAccess.Read, FileShare.Read));
                req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                return req;
            }, true, ct, ScaledTimeout(length)).ConfigureAwait(false);
            return;
        }

        await using var fs = new FileStream(localZip, FileMode.Open, FileAccess.Read, FileShare.Read);
        string? session = null;
        for (var i = 0; i < chunks.Count; i++)
        {
            var (offset, len) = chunks[i];
            var buf = new byte[len];
            fs.Position = offset;
            await fs.ReadExactlyAsync(buf, ct).ConfigureAwait(false);
            string url, arg;
            if (i == 0) { url = "files/upload_session/start"; arg = new JsonObject { ["close"] = false }.ToJsonString(); }
            else if (i < chunks.Count - 1) { url = "files/upload_session/append_v2"; arg = DropboxJson.SessionAppendArg(session!, offset); }
            else { url = "files/upload_session/finish"; arg = DropboxJson.SessionFinishArg(session!, offset, dest); }
            var body = await SendAsync(() => Binary(Content + url, arg, buf), true, ct, ScaledTimeout(len)).ConfigureAwait(false);
            if (i == 0)
                session = JsonNode.Parse(body)?["session_id"]?.GetValue<string>() ?? throw new InvalidOperationException("Dropbox did not return an upload session id.");
        }
    }

    private async Task<(string Url, string? Visibility)> ShareAsync(string path, CancellationToken ct)
    {
        foreach (var vis in new[] { "public", "team_only" })
        {
            try
            {
                var body = await SendAsync(() => Json(Api + "sharing/create_shared_link_with_settings", DropboxJson.SharedLinkArgs(path, vis)), true, ct).ConfigureAwait(false);
                var node = JsonNode.Parse(body);
                var url = node?["url"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(url)) return (url, ResolvedVisibility(node) ?? vis);
            }
            catch (DropboxException e) when (DropboxJson.IsLinkExists(e.Body))
            {
                return await ExistingLinkAsync(path, e.Body, ct).ConfigureAwait(false);
            }
            catch (DropboxException e) when (vis == "public" && e.Status is 400 or 403 or 409)
            {
                // team policy may forbid public links; try team_only
            }
        }
        throw new InvalidOperationException("Dropbox would not create a shared link.");
    }

    private async Task<(string Url, string? Visibility)> ExistingLinkAsync(string path, string errorBody, CancellationToken ct)
    {
        if (DropboxJson.TryExistingLinkUrl(errorBody, out var url))
            return (url, ResolvedVisibility(JsonNode.Parse(errorBody)?["error"]?["shared_link_already_exists"]?["metadata"]));
        var body = await SendAsync(() => Json(Api + "sharing/list_shared_links", DropboxJson.ListLinksArgs(path)), true, ct).ConfigureAwait(false);
        if (DropboxJson.TryFirstListedUrl(body, out var listed))
            return (listed, ResolvedVisibility(JsonNode.Parse(body)?["links"]?[0]));
        throw new InvalidOperationException("A shared link exists but Dropbox did not return it.");
    }

    private static string? ResolvedVisibility(JsonNode? meta) =>
        meta?["link_permissions"]?["resolved_visibility"]?[".tag"]?.GetValue<string>();

    private static HttpRequestMessage Json(string url, string? body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url);
        if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return req;
    }

    private static HttpRequestMessage Binary(string url, string arg, byte[] data)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.TryAddWithoutValidation("Dropbox-API-Arg", arg);
        req.Content = new ByteArrayContent(data);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return req;
    }

    /// <summary>
    /// Sends with the bearer token. On 401: one refresh, then one browser sign-in, then give up.
    /// On 429 and 5xx: up to 3 retries with backoff, honouring Retry-After. Each try has its own timeout.
    /// </summary>
    private async Task<string> SendAsync(Func<HttpRequestMessage> make, bool usePathRoot, CancellationToken ct, TimeSpan? timeout = null)
    {
        var authStep = 0;
        var retries = 0;
        while (true)
        {
            var token = authStep switch
            {
                0 => await _auth.GetAccessTokenAsync(ct).ConfigureAwait(false),
                1 => await _auth.GetAccessTokenAsync(ct, forceRefresh: true).ConfigureAwait(false),
                _ => await SignInAsync(ct).ConfigureAwait(false),
            };
            using var req = make();
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (usePathRoot && _rootNs is not null)
                req.Headers.TryAddWithoutValidation("Dropbox-API-Path-Root", DropboxJson.PathRoot(_rootNs));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout ?? TimeSpan.FromMinutes(2));
            HttpResponseMessage resp;
            string text;
            try
            {
                resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
                text = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("Dropbox did not answer in time.");
            }
            using (resp)
            {
                if (resp.IsSuccessStatusCode) return text;
                if (resp.StatusCode == HttpStatusCode.Unauthorized)
                {
                    if (DropboxJson.TryMissingScope(text, out var scope))
                        throw new InvalidOperationException("The Dropbox app is missing the '" + scope + "' permission. Tick it on the app's Permissions tab, Submit, then sign out and sign in again.");
                    if (authStep < 2) { authStep++; continue; }
                }
                else if ((resp.StatusCode == (HttpStatusCode)429 || (int)resp.StatusCode >= 500) && retries < 3)
                {
                    int? after = resp.Headers.RetryAfter?.Delta is { } d ? (int)Math.Ceiling(d.TotalSeconds) : null;
                    await Task.Delay(DropboxJson.RetryDelay(retries++, after), ct).ConfigureAwait(false);
                    continue;
                }
                throw new DropboxException((int)resp.StatusCode, text);
            }
        }
    }

    private async Task<string> SignInAsync(CancellationToken ct)
    {
        var t = await _auth.SignInAsync(ct).ConfigureAwait(false);
        ResetAccount();
        return t;
    }

    private static TimeSpan ScaledTimeout(long bytes) => TimeSpan.FromSeconds(60 + bytes / 200_000.0);
}
