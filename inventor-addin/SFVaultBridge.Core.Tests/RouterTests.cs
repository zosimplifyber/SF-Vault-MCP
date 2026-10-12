using System.Text.Json.Nodes;
using SFVaultBridge.Core;

namespace SFVaultBridge.Core.Tests;

public class RouterTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";

    private static Router MakeRouter(Action? onRun = null)
    {
        var r = new Router(Token);
        r.Register("echo", b => { onRun?.Invoke(); return new JsonObject { ["got"] = b["x"]?.DeepClone() }; });
        r.Register("boom", _ => throw new BridgeException("not_signed_in", "Sign in."));
        r.Register("crash", _ => throw new InvalidOperationException("bad state"));
        r.Register("nothing", _ => null);
        return r;
    }

    private static JsonObject Parse(string json) => (JsonObject)JsonNode.Parse(json)!;

    [Fact]
    public void Runs_operation_and_returns_its_json()
    {
        var (status, json) = MakeRouter().Handle("POST", "/op/echo", Token, "{\"x\": 5}");
        Assert.Equal(200, status);
        Assert.Equal(5, Parse(json)["got"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong-token-wrong-token-wrong-tok")]
    public void Missing_or_wrong_token_is_401_and_never_runs(string? token)
    {
        var ran = false;
        var (status, json) = MakeRouter(() => ran = true).Handle("POST", "/op/echo", token, "{}");
        Assert.Equal(401, status);
        Assert.Equal("unauthorized", Parse(json)["error"]!.GetValue<string>());
        Assert.False(ran);
    }

    [Fact]
    public void Unknown_operation_is_404()
    {
        var (status, json) = MakeRouter().Handle("POST", "/op/nope", Token, "{}");
        Assert.Equal(404, status);
        Assert.Equal("unknown_operation", Parse(json)["error"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("GET", "/op/echo")]
    [InlineData("POST", "/status")]
    public void Wrong_method_or_path_is_404(string method, string path)
    {
        var (status, _) = MakeRouter().Handle(method, path, Token, "{}");
        Assert.Equal(404, status);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("[1, 2]")]
    public void Bad_body_is_400(string body)
    {
        var (status, json) = MakeRouter().Handle("POST", "/op/echo", Token, body);
        Assert.Equal(400, status);
        Assert.Equal("bad_request", Parse(json)["error"]!.GetValue<string>());
    }

    [Fact]
    public void Empty_body_means_empty_object()
    {
        var (status, _) = MakeRouter().Handle("POST", "/op/echo", Token, "");
        Assert.Equal(200, status);
    }

    [Fact]
    public void BridgeException_becomes_400_with_its_code()
    {
        var (status, json) = MakeRouter().Handle("POST", "/op/boom", Token, "{}");
        Assert.Equal(400, status);
        var o = Parse(json);
        Assert.Equal("not_signed_in", o["error"]!.GetValue<string>());
        Assert.Equal("Sign in.", o["message"]!.GetValue<string>());
    }

    [Fact]
    public void Other_exception_becomes_500_internal()
    {
        var (status, json) = MakeRouter().Handle("POST", "/op/crash", Token, "{}");
        Assert.Equal(500, status);
        Assert.Contains("bad state", Parse(json)["message"]!.GetValue<string>());
    }

    [Fact]
    public void Null_result_is_empty_object()
    {
        var (status, json) = MakeRouter().Handle("POST", "/op/nothing", Token, "{}");
        Assert.Equal(200, status);
        Assert.Equal("{}", json);
    }

    [Fact]
    public void Args_reject_wrong_types_by_name()
    {
        var body = Parse("{\"path\": 3, \"ids\": [1, \"x\"], \"m\": {\"a\": 1}}");
        Assert.Equal("bad_request", Assert.Throws<BridgeException>(() => Args.Str(body, "path")).Code);
        Assert.Contains("ids[1]", Assert.Throws<BridgeException>(() => Args.Longs(body, "ids")).Message);
        Assert.Contains("m.a", Assert.Throws<BridgeException>(() => Args.StrMap(body, "m")).Message);
        Assert.Empty(Args.Longs(body, "absent", required: false));
        Assert.Equal("dflt", Args.OptStr(body, "absent", "dflt"));
        Assert.Equal(12L, Args.Long(Parse("{\"n\": 12}"), "n"));
    }
}
