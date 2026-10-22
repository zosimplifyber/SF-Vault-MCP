using System.Text.Json.Nodes;
using SFVaultBridge.Core;

namespace SFVaultBridge.Core.Tests;

public class DropboxTests
{
    [Fact]
    public void Partner_folder_and_file_path()
    {
        Assert.Equal("/Suppliers/Acme Machining", DropboxPaths.PartnerFolder("Acme Machining"));
        Assert.Equal("/Suppliers/Acme/pkg.zip", DropboxPaths.FilePath("Acme", "pkg.zip"));
    }

    [Fact]
    public void Partner_with_illegal_chars_is_cleaned()
    {
        Assert.Equal("/Suppliers/A B C D E F G H", DropboxPaths.PartnerFolder("A/B\\C:D*E?F\"G<H>|"));
        Assert.Equal("/Suppliers/Acme Co", DropboxPaths.PartnerFolder("  Acme   Co. "));
        Assert.Equal("/Suppliers/Unnamed", DropboxPaths.PartnerFolder("///"));
        Assert.Equal("/Suppliers/Unnamed", DropboxPaths.PartnerFolder(""));
        Assert.DoesNotContain("/..", DropboxPaths.PartnerFolder("../x"));
    }

    [Fact]
    public void Folders_outermost_first()
        => Assert.Equal(new[] { "/Suppliers", "/Suppliers/Acme" }, DropboxPaths.FoldersFor("Acme"));

    [Fact]
    public void Pkce_challenge_matches_rfc7636_vector()
        => Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", Pkce.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));

    [Fact]
    public void Pkce_verifier_length_and_charset_and_uniqueness()
    {
        var v = Pkce.NewVerifier();
        Assert.InRange(v.Length, 43, 128);
        Assert.All(v, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~'));
        Assert.NotEqual(v, Pkce.NewVerifier());
    }

    [Fact]
    public void Upload_arg_json()
    {
        var o = JsonNode.Parse(DropboxJson.UploadArg("/Suppliers/Acme/p.zip"))!.AsObject();
        Assert.Equal("/Suppliers/Acme/p.zip", (string?)o["path"]);
        Assert.Equal("overwrite", (string?)o["mode"]);
        Assert.False((bool)o["autorename"]!);
        Assert.True((bool)o["mute"]!);
    }

    [Fact]
    public void Upload_arg_is_ascii_for_header_use()
        => Assert.All(DropboxJson.UploadArg("/Suppliers/Müller/é.zip"), c => Assert.True(c < 128));

    [Fact]
    public void Path_root_and_shared_link_args()
    {
        var r = JsonNode.Parse(DropboxJson.PathRoot("123"))!.AsObject();
        Assert.Equal("root", (string?)r[".tag"]);
        Assert.Equal("123", (string?)r["root"]);
        var s = JsonNode.Parse(DropboxJson.SharedLinkArgs("/a/b.zip", "team_only"))!.AsObject();
        Assert.Equal("/a/b.zip", (string?)s["path"]);
        Assert.Equal("team_only", (string?)s["settings"]!["requested_visibility"]);
        Assert.Equal("viewer", (string?)s["settings"]!["access"]);
    }

    [Fact]
    public void Path_root_needed_only_when_namespaces_differ()
    {
        Assert.True(DropboxJson.NeedsPathRoot("{\"root_info\":{\".tag\":\"team\",\"root_namespace_id\":\"900\",\"home_namespace_id\":\"100\"}}", out var ns));
        Assert.Equal("900", ns);
        Assert.False(DropboxJson.NeedsPathRoot("{\"root_info\":{\"root_namespace_id\":\"100\",\"home_namespace_id\":\"100\"}}", out var ns2));
        Assert.Equal("", ns2);
        Assert.False(DropboxJson.NeedsPathRoot("{}", out _));
        Assert.False(DropboxJson.NeedsPathRoot("not json", out _));
    }

    [Fact]
    public void Existing_link_parsing()
    {
        var body = "{\"error_summary\":\"shared_link_already_exists/..\",\"error\":{\".tag\":\"shared_link_already_exists\",\"shared_link_already_exists\":{\".tag\":\"metadata\",\"metadata\":{\"url\":\"https://www.dropbox.com/s/x/p.zip?dl=0\"}}}}";
        Assert.True(DropboxJson.TryExistingLinkUrl(body, out var url));
        Assert.Equal("https://www.dropbox.com/s/x/p.zip?dl=0", url);
        Assert.True(DropboxJson.IsLinkExists(body));
    }

    [Fact]
    public void Existing_link_parsing_rejects_other_errors_and_missing_url()
    {
        Assert.False(DropboxJson.TryExistingLinkUrl("{\"error\":{\".tag\":\"settings_error\"}}", out _));
        Assert.False(DropboxJson.TryExistingLinkUrl("garbage", out _));
        var noUrl = "{\"error\":{\".tag\":\"shared_link_already_exists\"}}";
        Assert.False(DropboxJson.TryExistingLinkUrl(noUrl, out _));
        Assert.True(DropboxJson.IsLinkExists(noUrl));
        Assert.False(DropboxJson.IsLinkExists("garbage"));
    }

    [Fact]
    public void Listed_url_and_error_summary()
    {
        Assert.True(DropboxJson.TryFirstListedUrl("{\"links\":[{\"url\":\"https://d/1\"}]}", out var u));
        Assert.Equal("https://d/1", u);
        Assert.False(DropboxJson.TryFirstListedUrl("{\"links\":[]}", out _));
        Assert.Equal("path/not_found/..", DropboxJson.ErrorSummary("{\"error_summary\":\"path/not_found/..\"}"));
        Assert.Equal("plain", DropboxJson.ErrorSummary("plain"));
    }

    [Fact]
    public void Chunk_plan_boundaries()
    {
        Assert.Empty(UploadPlan.Chunks(0));
        Assert.Empty(UploadPlan.Chunks(150_000_000));
        var c = UploadPlan.Chunks(150_000_001);
        Assert.NotEmpty(c);
        var twenty = UploadPlan.Chunks(20_000_000, single: 10_000_000);
        Assert.Equal(new[] { (0L, 8_388_608), (8_388_608L, 8_388_608), (16_777_216L, 3_222_784) }, twenty);
    }

    [Fact]
    public void Chunk_plan_is_contiguous()
    {
        var c = UploadPlan.Chunks(150_000_001);
        long next = 0;
        foreach (var (o, l) in c) { Assert.Equal(next, o); next += l; }
        Assert.Equal(150_000_001, next);
    }

    [Fact]
    public void Session_args()
    {
        var a = JsonNode.Parse(DropboxJson.SessionAppendArg("sid", 16, false))!;
        Assert.Equal("sid", (string?)a["cursor"]!["session_id"]);
        var f = JsonNode.Parse(DropboxJson.SessionFinishArg("sid", 99, "/x/y.zip"))!;
        Assert.Equal("overwrite", (string?)f["commit"]!["mode"]);
    }

    [Fact]
    public void Missing_scope_parsing()
    {
        Assert.True(DropboxJson.TryMissingScope("{\"error\":{\".tag\":\"missing_scope\",\"required_scope\":\"sharing.write\"}}", out var sc));
        Assert.Equal("sharing.write", sc);
        Assert.False(DropboxJson.TryMissingScope("{\"error\":{\".tag\":\"expired_access_token\"}}", out _));
        Assert.False(DropboxJson.TryMissingScope("garbage", out _));
    }

    [Fact]
    public void Missing_scope_plain_text_parsing()
    {
        const string body = "Error in call to API function \"files/create_folder_v2\": Your app (ID: 8842465) is not permitted to access this endpoint because it does not have the required scope 'files.content.write'. The owner of the app can enable the scope.";
        Assert.True(DropboxJson.TryMissingScopeText(body, out var sc));
        Assert.Equal("files.content.write", sc);
        Assert.False(DropboxJson.TryMissingScopeText("Error in call to API function: something else", out _));
    }

    [Fact]
    public void Team_token_error_and_admin_member_id()
    {
        Assert.True(DropboxJson.IsTeamTokenError("Error in call to API function \"users/get_current_account\": This API function operates on a single Dropbox account, but the OAuth 2 access token you provided is for an entire Dropbox Business team.  Since your API app key has team member file access permissions, you can operate on a team member's Dropbox by providing the \"Dropbox-API-Select-User\" HTTP header"));
        Assert.False(DropboxJson.IsTeamTokenError("{\"error_summary\":\"path/not_found/\"}"));
        Assert.True(DropboxJson.TryAdminMemberId("{\"admin_profile\":{\"team_member_id\":\"dbmid:AAA\",\"email\":\"z@x\"}}", out var id));
        Assert.Equal("dbmid:AAA", id);
        Assert.False(DropboxJson.TryAdminMemberId("{}", out _));
    }

    [Fact]
    public void Retry_delay_backoff_and_retry_after()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), DropboxJson.RetryDelay(0, null));
        Assert.Equal(TimeSpan.FromSeconds(4), DropboxJson.RetryDelay(2, null));
        Assert.Equal(TimeSpan.FromSeconds(7), DropboxJson.RetryDelay(0, 7));
        Assert.Equal(TimeSpan.FromSeconds(60), DropboxJson.RetryDelay(0, 600));
    }
}
