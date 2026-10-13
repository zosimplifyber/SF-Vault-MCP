using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SFVaultBridge.Core;

/// <summary>Dropbox path rules for Partner Packages: /Suppliers/&lt;Partner&gt;/&lt;zip&gt;.</summary>
public static class DropboxPaths
{
    public const string Root = "/Suppliers";
    private const string Illegal = "<>:\"/\\|?*";

    /// <summary>Replaces characters Dropbox or Windows reject, collapses spaces, trims leading and trailing dots and spaces.</summary>
    public static string CleanName(string? name)
    {
        var sb = new StringBuilder();
        foreach (var c in name ?? "")
            sb.Append(char.IsControl(c) || Illegal.IndexOf(c) >= 0 ? ' ' : c);
        var s = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim('.', ' ');
        return s.Length == 0 ? "Unnamed" : s;
    }

    public static string PartnerFolder(string partner) => Root + "/" + CleanName(partner);

    public static string FilePath(string partner, string zipName) => PartnerFolder(partner) + "/" + CleanName(zipName);

    /// <summary>The folders to create, outermost first.</summary>
    public static IReadOnlyList<string> FoldersFor(string partner) => new[] { Root, PartnerFolder(partner) };
}

/// <summary>PKCE per RFC 7636 (S256).</summary>
public static class Pkce
{
    public static string NewVerifier() =>
        // 64 random bytes -> 86 base64url chars, inside 43-128 and the unreserved set.
        Base64Url(RandomNumberGenerator.GetBytes(64));

    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Dropbox request and response JSON shapes. Output is pure ASCII so it is safe in an HTTP header.</summary>
public static class DropboxJson
{
    public static string UploadArg(string path) =>
        new JsonObject { ["path"] = path, ["mode"] = "overwrite", ["autorename"] = false, ["mute"] = true }.ToJsonString();

    public static string PathRoot(string rootNs) =>
        new JsonObject { [".tag"] = "root", ["root"] = rootNs }.ToJsonString();

    public static string SharedLinkArgs(string path, string visibility) =>
        new JsonObject
        {
            ["path"] = path,
            ["settings"] = new JsonObject { ["requested_visibility"] = visibility, ["access"] = "viewer" },
        }.ToJsonString();

    public static string CreateFolderArg(string path) => new JsonObject { ["path"] = path, ["autorename"] = false }.ToJsonString();

    public static string ListLinksArgs(string path) => new JsonObject { ["path"] = path, ["direct_only"] = true }.ToJsonString();

    public static string SessionAppendArg(string sessionId, long offset, bool close = false) =>
        new JsonObject { ["cursor"] = new JsonObject { ["session_id"] = sessionId, ["offset"] = offset }, ["close"] = close }.ToJsonString();

    public static string SessionFinishArg(string sessionId, long offset, string path) =>
        new JsonObject
        {
            ["cursor"] = new JsonObject { ["session_id"] = sessionId, ["offset"] = offset },
            ["commit"] = new JsonObject { ["path"] = path, ["mode"] = "overwrite", ["autorename"] = false, ["mute"] = true },
        }.ToJsonString();

    /// <summary>Pulls the url out of a shared_link_already_exists error body.</summary>
    public static bool TryExistingLinkUrl(string errorJson, out string url)
    {
        url = "";
        var err = Parse(errorJson)?["error"] as JsonObject;
        if (err is null || (string?)err[".tag"] != "shared_link_already_exists") return false;
        var u = (err["shared_link_already_exists"] as JsonObject)?["metadata"]?["url"]?.GetValue<string>();
        if (string.IsNullOrEmpty(u)) return false;
        url = u;
        return true;
    }

    /// <summary>True when the error body is a shared_link_already_exists, with or without a url.</summary>
    public static bool IsLinkExists(string errorJson) =>
        (Parse(errorJson)?["error"] as JsonObject)?[".tag"]?.GetValue<string>() == "shared_link_already_exists";

    /// <summary>First link url in a list_shared_links response.</summary>
    public static bool TryFirstListedUrl(string json, out string url)
    {
        url = "";
        var first = (Parse(json)?["links"] as JsonArray)?.FirstOrDefault()?["url"]?.GetValue<string>();
        if (string.IsNullOrEmpty(first)) return false;
        url = first;
        return true;
    }

    /// <summary>A team account whose root namespace is not the home namespace needs the path-root header.</summary>
    public static bool NeedsPathRoot(string accountJson, out string rootNs)
    {
        rootNs = "";
        var info = Parse(accountJson)?["root_info"] as JsonObject;
        var root = info?["root_namespace_id"]?.GetValue<string>();
        var home = info?["home_namespace_id"]?.GetValue<string>();
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(home) || root == home) return false;
        rootNs = root;
        return true;
    }

    /// <summary>The short error text Dropbox sends, or a trimmed body when there is none.</summary>
    public static string ErrorSummary(string body)
    {
        var s = Parse(body)?["error_summary"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(s)) return s;
        var t = (body ?? "").Trim();
        return t.Length > 200 ? t[..200] : t;
    }

    private static JsonNode? Parse(string json)
    {
        try { return JsonNode.Parse(json ?? ""); }
        catch (JsonException) { return null; }
    }
}

/// <summary>Splits an upload into session chunks; an empty plan means one files/upload call.</summary>
public static class UploadPlan
{
    public static IReadOnlyList<(long Offset, int Length)> Chunks(long length, long single = 150_000_000, int chunk = 8_388_608)
    {
        if (length <= single) return Array.Empty<(long, int)>();
        var list = new List<(long, int)>();
        for (long off = 0; off < length; off += chunk)
            list.Add((off, (int)Math.Min(chunk, length - off)));
        return list;
    }
}
