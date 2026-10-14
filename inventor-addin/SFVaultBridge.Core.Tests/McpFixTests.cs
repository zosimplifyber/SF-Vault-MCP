using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using SFVaultBridge.Core;

namespace SFVaultBridge.Core.Tests;

// Fix round 1: concurrency, cancellation, jobs, peer and body checks.
public partial class McpTests
{
    private static (string Text, int Status) RawPost(int port, string head)
    {
        using var c = new TcpClient();
        c.Connect(IPAddress.Loopback, port);
        c.ReceiveTimeout = 5000;
        var s = c.GetStream();
        var bytes = Encoding.ASCII.GetBytes(head + "\r\n\r\n");
        s.Write(bytes, 0, bytes.Length);
        using var r = new StreamReader(s, Encoding.UTF8);
        var text = r.ReadToEnd();
        return (text, int.Parse(text.Split(' ')[1]));
    }

    private static string CallBody(string tool) =>
        "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool + "\"}}";

    [Fact]
    public void Null_request_id_is_an_error_not_silence()
    {
        using var rig = new Rig();
        var r = rig.Server.Handle(new McpHttpRequest("POST", Token, null, null, null, "{\"jsonrpc\":\"2.0\",\"id\":null,\"method\":\"ping\"}"));
        Assert.Equal(400, r.Status);
    }

    [Fact]
    public void Internal_errors_do_not_leak_exception_text_to_the_client()
    {
        using var rig = new Rig();
        rig.Vault.Throw = new InvalidOperationException("secret local path");
        var (_, err, text) = rig.Call("vault_search_files", new JsonObject { ["query"] = "nut" });
        Assert.True(err);
        Assert.DoesNotContain("secret", text);
        Assert.Contains(rig.Logs, l => l.Contains("secret"));
    }

    [Fact]
    public void Timed_out_call_that_never_reached_the_ui_thread_never_runs()
    {
        var ui = new object();
        var ran = 0;
        var tool = new McpTool("slowread", "t", "d", Schema.Obj(new JsonObject()), McpToolKind.Read, _ => { Interlocked.Increment(ref ran); return new JsonObject(); });
        var server = new McpServer(Token, "1", new[] { tool }, w => { lock (ui) return w(); }, TimeSpan.FromMilliseconds(80));
        string reply;
        lock (ui)
        {
            reply = server.Handle(new McpHttpRequest("POST", Token, null, null, null, CallBody("slowread"))).Body!;
        }
        Assert.Contains("timed out before starting; nothing was changed", reply);
        Thread.Sleep(250);
        Assert.Equal(0, ran);
    }

    [Fact]
    public void Timed_out_call_that_already_started_is_reported_as_still_running()
    {
        var tool = new McpTool("slowread", "t", "d", Schema.Obj(new JsonObject()), McpToolKind.Read, _ => { Thread.Sleep(500); return new JsonObject(); });
        var server = new McpServer(Token, "1", new[] { tool }, w => w(), TimeSpan.FromMilliseconds(100));
        var body = server.Handle(new McpHttpRequest("POST", Token, null, null, null, CallBody("slowread"))).Body!;
        Assert.Contains("still running in Inventor", body);
    }

    [Fact]
    public void Bridge_read_ops_time_out_too_and_do_not_run_late()
    {
        using var rig = new Rig();
        var ui = new object();
        var router = new Router(Token);
        McpCatalog.RegisterReadOps(router, rig.Tools, w => { lock (ui) return w(); }, TimeSpan.FromMilliseconds(80));
        (int, string) r;
        lock (ui) { r = router.Handle("POST", "/op/vault_search_files", Token, "{\"query\":\"nut\"}"); }
        Assert.Equal(400, r.Item1);
        Assert.Contains("timeout", r.Item2);
        Thread.Sleep(250);
        Assert.Null(rig.Vault.LastQuery);
    }

    [Fact]
    public void Fast_write_returns_its_result_directly()
    {
        using var rig = new Rig();
        rig.CallJson("fix_data_card_preview", FixArgs());
        var j = rig.CallJson("fix_data_card_apply", FixArgs(confirm: true));
        Assert.Null(j["status"]);
        Assert.Equal(1, j["counts"]!["fixed"]!.GetValue<int>());
        Assert.Equal(1, rig.Jobs.Count);
    }

    [Fact]
    public void Slow_write_returns_a_job_id_and_job_status_reports_running_then_done()
    {
        using var rig = new Rig(jobWait: TimeSpan.FromMilliseconds(100));
        var gate = rig.Card.BlockApply = new ManualResetEventSlim(false);
        rig.CallJson("fix_data_card_preview", FixArgs());
        var j = rig.CallJson("fix_data_card_apply", FixArgs(confirm: true));
        Assert.Equal("running", j["status"]!.GetValue<string>());
        Assert.Contains("job_status", j["message"]!.GetValue<string>());
        var id = j["job_id"]!.GetValue<string>();

        Assert.Equal("running", rig.CallJson("job_status", new JsonObject { ["job_id"] = id })["status"]!.GetValue<string>());

        gate.Set();
        var done = new JsonObject();
        for (var i = 0; i < 60; i++)
        {
            done = rig.CallJson("job_status", new JsonObject { ["job_id"] = id });
            if (done["status"]!.GetValue<string>() != "running") break;
        }
        Assert.Equal("done", done["status"]!.GetValue<string>());
        Assert.Equal(1, done["result"]!["counts"]!["fixed"]!.GetValue<int>());
        Assert.Equal(1, rig.Card.Applies);
    }

    [Fact]
    public void A_second_write_is_refused_while_one_is_running_but_reads_still_work()
    {
        using var rig = new Rig(jobWait: TimeSpan.FromMilliseconds(100));
        var gate = rig.Card.BlockApply = new ManualResetEventSlim(false);
        rig.CallJson("fix_data_card_preview", FixArgs());
        var first = rig.CallJson("fix_data_card_apply", FixArgs(confirm: true));
        Assert.Equal("running", first["status"]!.GetValue<string>());
        Download(rig);
        var token = rig.CallJson("mcmaster_intake_preview", IntakeArgs())["token"]!.GetValue<string>();
        var (_, err, text) = rig.Call("mcmaster_intake_commit", new JsonObject { ["token"] = token, ["confirm"] = true });
        Assert.True(err);
        Assert.Equal("busy", JsonNode.Parse(text)!["error"]!.GetValue<string>());
        Assert.Contains(first["job_id"]!.GetValue<string>(), text);
        Assert.False(rig.Call("bridge_status").IsError);
        gate.Set();
    }

    [Fact]
    public void Job_that_fails_after_the_wait_shows_failed_with_ok_false_and_the_error()
    {
        var release = new ManualResetEventSlim(false);
        var jobs = new JobManager(w => w(), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50));
        var r = (JsonObject)jobs.RunOrDetach("w", () => { release.Wait(); throw new BridgeException("checkin_failed", "boom"); })!;
        var id = r["job_id"]!.GetValue<string>();
        release.Set();
        var s = new JsonObject();
        for (var i = 0; i < 60; i++) { s = (JsonObject)jobs.Status(id); if (s["status"]!.GetValue<string>() != "running") break; }
        Assert.Equal("failed", s["status"]!.GetValue<string>());
        Assert.False(s["ok"]!.GetValue<bool>());
        Assert.Equal("checkin_failed", s["error"]!["error"]!.GetValue<string>());
    }

    [Fact]
    public void Job_whose_result_says_ok_false_is_failed_but_keeps_its_result()
    {
        var jobs = new JobManager(w => w(), TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(10));
        var res = jobs.RunOrDetach("w", () => new JsonObject { ["ok"] = false, ["failed_step"] = "pdf" });
        Assert.Equal("pdf", res!["failed_step"]!.GetValue<string>());
        var id = ((JsonObject)jobs.Status(jobs.GetType().Name == "" ? "" : FirstId(jobs)))["status"]!.GetValue<string>();
        Assert.Equal("failed", id);
    }

    private static string FirstId(JobManager jobs)
    {
        var f = typeof(JobManager).GetField("_jobs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return ((System.Collections.IDictionary)f.GetValue(jobs)!).Keys.Cast<string>().First();
    }

    [Fact]
    public void Unknown_and_expired_jobs_are_not_found()
    {
        var now = DateTime.UtcNow;
        var jobs = new JobManager(w => w(), TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(10), () => now);
        Assert.Equal("not_found", Assert.Throws<BridgeException>(() => jobs.Status("nope")).Code);
        var res = jobs.RunOrDetach("w", () => new JsonObject { ["x"] = 1 });
        Assert.Equal(1, res!["x"]!.GetValue<int>());
        Assert.Equal(1, jobs.Count);
        now = now.AddMinutes(59);
        Assert.Equal(1, jobs.Count);
        now = now.AddMinutes(2);
        Assert.Equal(0, jobs.Count);
        Assert.Equal("not_found", Assert.Throws<BridgeException>(() => jobs.Status(FirstIdOrNope(jobs))).Code);
    }

    private static string FirstIdOrNope(JobManager jobs) => "gone";

    [Fact]
    public void Queued_job_can_be_cancelled_before_it_starts_and_never_runs()
    {
        var ui = new object();
        var ran = 0;
        var jobs = new JobManager(w => { lock (ui) return w(); }, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(10));
        string id;
        lock (ui)
        {
            var r = (JsonObject)jobs.RunOrDetach("w", () => { Interlocked.Increment(ref ran); return new JsonObject(); })!;
            id = r["job_id"]!.GetValue<string>();
            Assert.Equal("cancelled", ((JsonObject)jobs.Status(id, cancel: true))["status"]!.GetValue<string>());
        }
        Thread.Sleep(250);
        Assert.Equal(0, ran);
        Assert.Equal("cancelled", ((JsonObject)jobs.Status(id))["status"]!.GetValue<string>());
    }

    [Fact]
    public void Started_job_cannot_be_cancelled()
    {
        var release = new ManualResetEventSlim(false);
        var started = new ManualResetEventSlim(false);
        var jobs = new JobManager(w => w(), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(10));
        var r = (JsonObject)jobs.RunOrDetach("w", () => { started.Set(); release.Wait(); return new JsonObject(); })!;
        started.Wait();
        var c = (JsonObject)jobs.Status(r["job_id"]!.GetValue<string>(), cancel: true);
        Assert.Equal("running", c["status"]!.GetValue<string>());
        Assert.Contains("Too late", c["message"]!.GetValue<string>());
        release.Set();
    }

    [Fact]
    public void Job_status_runs_off_the_ui_thread()
    {
        var uiCalls = 0;
        using var rig = new Rig(onUi: w => { Interlocked.Increment(ref uiCalls); return w(); });
        var before = uiCalls;
        var (_, err, _) = rig.Call("job_status", new JsonObject { ["job_id"] = "nope" });
        Assert.True(err);
        Assert.Equal(before, uiCalls);
    }

    [Fact]
    public void Commit_log_line_hides_the_preview_token()
    {
        using var rig = new Rig();
        Download(rig);
        var token = rig.CallJson("mcmaster_intake_preview", IntakeArgs())["token"]!.GetValue<string>();
        rig.CallJson("mcmaster_intake_commit", new JsonObject { ["token"] = token, ["confirm"] = true });
        Assert.DoesNotContain(rig.Logs, l => l.Contains(token));
    }

    [Fact]
    public void Peer_check_requires_loopback_and_a_localhost_host_with_the_port()
    {
        var port = FreePort();
        using var host = new HttpHost($"http://localhost:{port}/", new Router(Token), _ => { });
        var lo = new IPEndPoint(IPAddress.Loopback, 5000);
        Assert.Null(host.CheckPeer(lo, $"localhost:{port}"));
        Assert.Null(host.CheckPeer(lo, $"127.0.0.1:{port}"));
        Assert.Null(host.CheckPeer(lo, $"[::1]:{port}"));
        Assert.Null(host.CheckPeer(new IPEndPoint(IPAddress.IPv6Loopback, 5000), $"LOCALHOST:{port}"));
        Assert.Null(host.CheckPeer(new IPEndPoint(IPAddress.Parse("::ffff:127.0.0.1"), 5000), $"localhost:{port}"));
        Assert.NotNull(host.CheckPeer(new IPEndPoint(IPAddress.Parse("10.1.2.3"), 5000), $"localhost:{port}"));
        Assert.NotNull(host.CheckPeer(new IPEndPoint(IPAddress.Parse("192.168.1.5"), 5000), $"127.0.0.1:{port}"));
        Assert.NotNull(host.CheckPeer(lo, "evil.example"));
        Assert.NotNull(host.CheckPeer(lo, "localhost"));
        Assert.NotNull(host.CheckPeer(lo, $"localhost:{port + 1}"));
        Assert.NotNull(host.CheckPeer(lo, $"localhost.evil.example:{port}"));
        Assert.NotNull(host.CheckPeer(lo, null));
        Assert.NotNull(host.CheckPeer(null, $"localhost:{port}"));
    }

    [Fact]
    public async Task Slow_call_does_not_block_other_requests()
    {
        var release = new ManualResetEventSlim(false);
        var slow = new McpTool("slow", "t", "d", Schema.Obj(new JsonObject()), McpToolKind.Read, _ => { release.Wait(); return new JsonObject(); });
        var server = new McpServer(Token, "1", new[] { slow }, w => w(), TimeSpan.FromSeconds(30));
        var port = FreePort();
        using var host = new HttpHost($"http://localhost:{port}/", new Router(Token), _ => { }) { Mcp = server };
        host.Start();
        using var client = new HttpClient();
        HttpRequestMessage Post(string body)
        {
            var m = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{port}/mcp") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            m.Headers.Add(HttpHost.TokenHeader, Token);
            return m;
        }
        var slowCall = client.SendAsync(Post(CallBody("slow")));
        await Task.Delay(300);
        var ping = await client.SendAsync(Post("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"ping\"}")).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.OK, ping.StatusCode);
        Assert.False(slowCall.IsCompleted);
        release.Set();
        Assert.Equal(HttpStatusCode.OK, (await slowCall).StatusCode);
    }

    [Fact]
    public async Task Auth_is_judged_before_the_body_and_big_bodies_are_413()
    {
        using var rig = new Rig();
        var port = FreePort();
        var router = new Router(Token);
        router.Register("status", _ => new JsonObject());
        using var host = new HttpHost($"http://localhost:{port}/", router, _ => { }) { Mcp = rig.Server };
        host.Start();
        // A body that is announced but never sent: answered at once, not after the read deadline.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (_, status) = await Task.Run(() => RawPost(port, $"POST /mcp HTTP/1.1\r\nHost: localhost:{port}\r\nContent-Length: 100\r\nConnection: close\r\nX-Bridge-Token: wrong"));
        Assert.Equal(401, status);
        var (_, op) = await Task.Run(() => RawPost(port, $"POST /op/status HTTP/1.1\r\nHost: localhost:{port}\r\nContent-Length: 100\r\nConnection: close\r\nX-Bridge-Token: wrong"));
        Assert.Equal(401, op);
        var (_, bad) = await Task.Run(() => RawPost(port, $"POST /mcp HTTP/1.1\r\nHost: localhost:{port}\r\nContent-Length: 100\r\nConnection: close\r\nOrigin: https://evil.example\r\nX-Bridge-Token: {Token}"));
        Assert.Equal(403, bad);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));

        using var client = new HttpClient();
        var big = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{port}/mcp") { Content = new ByteArrayContent(new byte[HttpHost.MaxBodyBytes + 10]) };
        big.Headers.Add(HttpHost.TokenHeader, Token);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.SendAsync(big)).StatusCode);
    }
}
