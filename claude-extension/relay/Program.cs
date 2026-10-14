using System.Reflection;
using System.Text;
using SFVaultBridgeRelay;

var version = typeof(Relay).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "1.0.0";
void Log(string m) { try { Console.Error.WriteLine($"[sf-vault-bridge-relay] {m}"); } catch { } }

// SFVB_MCP_URL and SFVB_TOKEN_FILE exist for tests; production uses the defaults.
var url = Environment.GetEnvironmentVariable("SFVB_MCP_URL");
// The token goes with every request, so the override is honoured for loopback hosts only.
if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var parsed) || !parsed.IsLoopback) url = "http://localhost:8790/mcp";
var tokenFile = Environment.GetEnvironmentVariable("SFVB_TOKEN_FILE");
if (string.IsNullOrEmpty(tokenFile)) tokenFile = Relay.DefaultTokenFile();

var utf8 = new UTF8Encoding(false);
var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8, 4096) { AutoFlush = true, NewLine = "\n" };
var relay = new Relay(url, tokenFile, line => stdout.WriteLine(line), Log, version);
Log($"started version {version}, url {url}, token {(relay.HasToken ? "present" : "missing")}");

var stdin = new StreamReader(Console.OpenStandardInput(), utf8, false);
var pending = new List<Task>();
string? l;
while ((l = await stdin.ReadLineAsync()) != null)
{
    if (string.IsNullOrWhiteSpace(l)) continue;
    var captured = l;
    var t = Task.Run(async () =>
    {
        try { await relay.HandleAsync(captured); } catch (Exception e) { Log($"internal error: {e.Message}"); }
    });
    pending.RemoveAll(x => x.IsCompleted);
    pending.Add(t);
}
await Task.WhenAll(pending);
return 0;
