using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using SFVaultBridge.Core;

namespace SFVaultBridge.Core.Tests;

public class HttpHostTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static Router EchoRouter()
    {
        var r = new Router(Token);
        r.Register("echo", b => new JsonObject { ["got"] = b["x"]?.DeepClone() });
        return r;
    }

    [Fact]
    public async Task Serves_requests_with_token()
    {
        var port = FreePort();
        var logs = new List<string>();
        using var host = new HttpHost($"http://localhost:{port}/", EchoRouter(), logs.Add);
        host.Start();
        using var client = new HttpClient();

        var ok = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{port}/op/echo")
        { Content = new StringContent("{\"x\": \"hi\"}", Encoding.UTF8, "application/json") };
        ok.Headers.Add(HttpHost.TokenHeader, Token);
        var resp = await client.SendAsync(ok);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("hi", JsonNode.Parse(await resp.Content.ReadAsStringAsync())!["got"]!.GetValue<string>());

        var noToken = await client.PostAsync($"http://localhost:{port}/op/echo", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);
        Assert.Contains(logs, l => l.Contains("401"));
    }

    [Fact]
    public void Second_host_on_same_prefix_fails_to_start()
    {
        var port = FreePort();
        using var first = new HttpHost($"http://localhost:{port}/", EchoRouter(), _ => { });
        first.Start();
        using var second = new HttpHost($"http://localhost:{port}/", EchoRouter(), _ => { });
        Assert.Throws<HttpListenerException>(() => second.Start());
    }

    [Fact]
    public async Task Throwing_log_does_not_kill_the_listener()
    {
        var port = FreePort();
        using var host = new HttpHost($"http://localhost:{port}/", EchoRouter(), _ => throw new InvalidOperationException("log sink down"));
        host.Start();
        using var client = new HttpClient();

        var first = await client.PostAsync($"http://localhost:{port}/op/echo", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);

        var second = await client.PostAsync($"http://localhost:{port}/op/echo", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
    }

    [Fact]
    public void Dispose_before_start_is_safe()
    {
        var host = new HttpHost($"http://localhost:{FreePort()}/", EchoRouter(), _ => { });
        host.Dispose();
    }
}
