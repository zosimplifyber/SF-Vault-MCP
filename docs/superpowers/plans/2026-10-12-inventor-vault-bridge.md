# SF Vault Bridge (Inventor add-in) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An Inventor 2026 add-in that runs Vault writes and the STEP import on the user's signed-in Inventor session, and a Python client so the McMaster intake commit uses it instead of the PowerShell SDK sign-in that Vault refuses.

**Architecture:** `SFVaultBridge.Core` (net8.0, no Inventor or Vault references) holds the HTTP host, router, token check, path guard and the JSON mapping of every operation onto two interfaces, so all of it is unit-tested with fakes. `SFVaultBridge.Addin` (net8.0-windows) implements those interfaces against the Vault add-in's connection and the Inventor API, and marshals every operation onto Inventor's UI thread. `scripts/vault_bridge.py` is the Python client with the same call shapes as the `vault_sdk` wrappers the intake already uses.

**Tech Stack:** C# 12 / .NET 8 SDK 8.0.425, xUnit, `System.Net.HttpListener`, `System.Text.Json.Nodes`, Inventor 2026 interop, Vault 2026 VDF + web services (from Inventor's Vault add-in bundle); Python 3.14 stdlib `urllib` + pytest.

**Spec:** `docs/superpowers/specs/2026-10-12-inventor-vault-bridge-design.md`

## Global Constraints

- Listener: `http://localhost:8790/` only (HttpListener needs no admin rights for a `localhost` prefix). Python calls `http://localhost:8790` (the Host header must be `localhost`).
- Token: random, created at first start in `%APPDATA%\Simplifyber\VaultBridge\token`; every request sends it in `X-Bridge-Token`; wrong or missing token returns 401 and runs nothing.
- Vault connection: `Connectivity.Application.VaultBase.ConnectionManager.Instance.Connection`, read per request. Null: `{"error": "not_signed_in", "message": "Sign in to Vault in Inventor (Vault tab, Log In)."}`.
- Requests are handled one at a time; every operation runs on Inventor's UI thread.
- Vault and Inventor DLLs are referenced, never copied to output: Inventor interop from `C:\Program Files\Autodesk\Inventor 2026\Bin`, Vault DLLs from `C:\ProgramData\Autodesk\ApplicationPlugins\VaultInventor2026.bundle\Contents`.
- Path guard: `add_file.localPath`, `import_step_as_part.stepPath` and `outIpt` must be under the Vault workspace root or the user's Downloads folder, else `{"error": "path_not_allowed"}`.
- Every failure reply is `{"error": code, "message": text}`. Add-in exceptions are caught per request and logged to `%APPDATA%\Simplifyber\VaultBridge\bridge.log`; Inventor never sees an unhandled exception from the add-in.
- Operation names and reply shapes exactly as the spec's Operations table (`status`, `get_numbering_schemes`, `generate_file_number`, `get_folder_by_path`, `add_folder`, `get_file_categories`, `add_file`, `update_file_categories`, `import_step_as_part`).
- Install: no admin rights; DLLs to `%APPDATA%\Simplifyber\VaultBridge\`, manifest to `%APPDATA%\Autodesk\Inventor 2026\Addins\`.
- Python: run with `py -3.14`; full suite `py -3.14 -m pytest -q` (814 passed, 5 skipped before this plan). C#: `dotnet test inventor-addin/SFVaultBridge.sln`.
- User-facing strings: no em dashes.

## Review Focus

1. **Request with no token or a wrong token**: 401, the operation never runs. Test in Task 1.
2. **A path that escapes the allowed roots** (`C:\Vault Workspace\..\Windows\x.dll`, another drive): `path_not_allowed`, nothing read. Test in Task 1 (guard) and Task 2 (applied to `add_file` and `import_step_as_part`).
3. **Vault not signed in inside Inventor**: every Vault operation returns `not_signed_in`; Python raises `BridgeUnavailable` with that message. Test in Task 2 and Task 5.
4. **Port 8790 already taken** (a second Inventor, a stale process): the add-in logs it and Inventor keeps running. Test in Task 3 (second host on the same prefix throws) and Task 4 (Activate catches).
5. **A long write that times out on the Python side**: reported as uncertain (`BridgeError("timeout")`), not as "Inventor is not running". Test in Task 5.

Ruling carried from the spec: the ribbon status button is left out of phase 1. Status comes from the `status` operation and `bridge.log`. Report this to Zak.

---

## File Structure

| File | Responsibility |
|---|---|
| `inventor-addin/SFVaultBridge.sln` | Solution with the three projects. |
| `inventor-addin/SFVaultBridge.Core/SFVaultBridge.Core.csproj` | net8.0 class library. |
| `inventor-addin/SFVaultBridge.Core/Protocol.cs` | `BridgeException`, `Operation`, `Router`. |
| `inventor-addin/SFVaultBridge.Core/Args.cs` | Typed reads from a JSON body, `bad_request` on bad input. |
| `inventor-addin/SFVaultBridge.Core/Support.cs` | `TokenStore`, `PathGuard`, `FileLog`. |
| `inventor-addin/SFVaultBridge.Core/Contracts.cs` | Records + `IVaultSession`, `IStepImporter`. |
| `inventor-addin/SFVaultBridge.Core/BridgeOps.cs` | Registers every operation: JSON in, interface call, JSON out. |
| `inventor-addin/SFVaultBridge.Core/HttpHost.cs` | HttpListener loop feeding the router. |
| `inventor-addin/SFVaultBridge.Core.Tests/*` | xUnit tests for all of the above. |
| `inventor-addin/SFVaultBridge.Addin/SFVaultBridge.Addin.csproj` | net8.0-windows add-in, WinForms for UI-thread marshalling. |
| `inventor-addin/SFVaultBridge.Addin/VaultSession.cs` | `IVaultSession` on the Vault add-in's connection. |
| `inventor-addin/SFVaultBridge.Addin/StepImporter.cs` | `IStepImporter` on the Inventor API. |
| `inventor-addin/SFVaultBridge.Addin/AddInServer.cs` | Inventor entry point; wires and starts everything. |
| `inventor-addin/SFVaultBridge.addin` | Manifest template (`{{ASSEMBLY}}` placeholder). |
| `inventor-addin/Install-VaultBridge.ps1` | Build, copy, write manifest. |
| `scripts/vault_bridge.py` | Python client. |
| `tests/test_vault_bridge.py` | Client tests against a fake HTTP server. |
| `mcmaster_intake.py`, `tests/test_mcmaster_intake_tools.py` (modify) | Intake writes go through the bridge. |

---

### Task 1: Core library: router, args, token, path guard, log

**Files:**
- Create: `inventor-addin/SFVaultBridge.sln`, `inventor-addin/SFVaultBridge.Core/SFVaultBridge.Core.csproj`, `inventor-addin/SFVaultBridge.Core/Protocol.cs`, `inventor-addin/SFVaultBridge.Core/Args.cs`, `inventor-addin/SFVaultBridge.Core/Support.cs`
- Create: `inventor-addin/SFVaultBridge.Core.Tests/SFVaultBridge.Core.Tests.csproj`, `inventor-addin/SFVaultBridge.Core.Tests/RouterTests.cs`, `inventor-addin/SFVaultBridge.Core.Tests/SupportTests.cs`
- Modify: `.gitignore` (add `inventor-addin/**/bin/` and `inventor-addin/**/obj/`)

**Interfaces:**
- Produces (namespace `SFVaultBridge.Core`):
  - `class BridgeException(string code, string message) : Exception` with `string Code`
  - `delegate JsonNode? Operation(JsonObject body)`
  - `class Router(string token)`: `void Register(string name, Operation op)`, `(int Status, string Json) Handle(string method, string path, string? token, string body)`
  - `static class Args`: `Str`, `OptStr`, `Long`, `Longs`, `StrMap`
  - `static class TokenStore`: `string LoadOrCreate(string path)`
  - `class PathGuard(IEnumerable<string> roots)`: `string Check(string? path, string field)`
  - `class FileLog(string path)`: `void Write(string line)`

- [ ] **Step 1: Scaffold the solution and projects**

```bash
cd inventor-addin
dotnet new sln -n SFVaultBridge
dotnet new classlib -n SFVaultBridge.Core -f net8.0 -o SFVaultBridge.Core
dotnet new xunit -n SFVaultBridge.Core.Tests -f net8.0 -o SFVaultBridge.Core.Tests
dotnet sln add SFVaultBridge.Core/SFVaultBridge.Core.csproj SFVaultBridge.Core.Tests/SFVaultBridge.Core.Tests.csproj
dotnet add SFVaultBridge.Core.Tests/SFVaultBridge.Core.Tests.csproj reference SFVaultBridge.Core/SFVaultBridge.Core.csproj
rm SFVaultBridge.Core/Class1.cs SFVaultBridge.Core.Tests/UnitTest1.cs
```

Keep the generated csproj files; make sure `SFVaultBridge.Core.csproj` has `<Nullable>enable</Nullable>` and `<ImplicitUsings>enable</ImplicitUsings>`. Add to the repo `.gitignore`:

```
inventor-addin/**/bin/
inventor-addin/**/obj/
```

- [ ] **Step 2: Write the failing tests**

`inventor-addin/SFVaultBridge.Core.Tests/RouterTests.cs`:

```csharp
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
```

`inventor-addin/SFVaultBridge.Core.Tests/SupportTests.cs`:

```csharp
using SFVaultBridge.Core;

namespace SFVaultBridge.Core.Tests;

public class SupportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sfvb-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void Token_is_created_once_and_reused()
    {
        var path = Path.Combine(_dir, "sub", "token");
        var first = TokenStore.LoadOrCreate(path);
        Assert.Equal(64, first.Length);
        Assert.Equal(first, TokenStore.LoadOrCreate(path));
    }

    [Fact]
    public void Short_token_file_is_replaced()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "token");
        File.WriteAllText(path, "abc");
        Assert.Equal(64, TokenStore.LoadOrCreate(path).Length);
    }

    [Fact]
    public void PathGuard_allows_paths_under_roots()
    {
        var root = Path.Combine(_dir, "ws");
        var guard = new PathGuard(new[] { root });
        var inside = Path.Combine(root, "DESIGNS", "a.ipt");
        Assert.Equal(Path.GetFullPath(inside), guard.Check(inside, "outIpt"));
    }

    [Theory]
    [InlineData("..\\outside.dll")]
    [InlineData("..\\wsX\\a.ipt")]
    public void PathGuard_rejects_escapes(string relative)
    {
        var root = Path.Combine(_dir, "ws");
        var guard = new PathGuard(new[] { root });
        var ex = Assert.Throws<BridgeException>(() => guard.Check(Path.Combine(root, relative), "localPath"));
        Assert.Equal("path_not_allowed", ex.Code);
        Assert.Contains("localPath", ex.Message);
    }

    [Fact]
    public void PathGuard_requires_a_value()
    {
        var guard = new PathGuard(new[] { _dir });
        Assert.Equal("bad_request", Assert.Throws<BridgeException>(() => guard.Check(" ", "stepPath")).Code);
    }

    [Fact]
    public void FileLog_appends_lines()
    {
        var log = new FileLog(Path.Combine(_dir, "bridge.log"));
        log.Write("one");
        log.Write("two");
        var lines = File.ReadAllLines(Path.Combine(_dir, "bridge.log"));
        Assert.Equal(2, lines.Length);
        Assert.EndsWith("two", lines[1]);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test inventor-addin/SFVaultBridge.sln`
Expected: build FAIL with `The type or namespace name 'Router' could not be found`

- [ ] **Step 4: Implement**

`inventor-addin/SFVaultBridge.Core/Protocol.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SFVaultBridge.Core;

/// <summary>An expected failure with a stable code the Python client acts on.</summary>
public sealed class BridgeException : Exception
{
    public string Code { get; }

    public BridgeException(string code, string message) : base(message) => Code = code;
}

public delegate JsonNode? Operation(JsonObject body);

/// <summary>Token check, routing and error mapping for POST /op/&lt;name&gt;.</summary>
public sealed class Router
{
    private readonly Dictionary<string, Operation> _ops = new(StringComparer.Ordinal);
    private readonly byte[] _token;

    public Router(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("token is required", nameof(token));
        _token = Encoding.UTF8.GetBytes(token);
    }

    public void Register(string name, Operation op) => _ops[name] = op;

    public (int Status, string Json) Handle(string method, string path, string? token, string body)
    {
        if (!TokenMatches(token)) return Error(401, "unauthorized", "Missing or wrong X-Bridge-Token.");
        if (method != "POST" || !path.StartsWith("/op/", StringComparison.Ordinal))
            return Error(404, "not_found", "Use POST /op/<name>.");
        var name = path["/op/".Length..].TrimEnd('/');
        if (!_ops.TryGetValue(name, out var op)) return Error(404, "unknown_operation", $"No operation named '{name}'.");

        JsonObject args;
        try
        {
            args = string.IsNullOrWhiteSpace(body)
                ? new JsonObject()
                : JsonNode.Parse(body) as JsonObject ?? throw new BridgeException("bad_request", "Body must be a JSON object.");
        }
        catch (JsonException ex) { return Error(400, "bad_request", "Body is not valid JSON: " + ex.Message); }
        catch (BridgeException ex) { return Error(400, ex.Code, ex.Message); }

        try
        {
            var result = op(args) ?? new JsonObject();
            return (200, result.ToJsonString());
        }
        catch (BridgeException ex) { return Error(400, ex.Code, ex.Message); }
        catch (Exception ex) { return Error(500, "internal", ex.GetType().Name + ": " + ex.Message); }
    }

    private bool TokenMatches(string? token) =>
        !string.IsNullOrEmpty(token) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), _token);

    private static (int, string) Error(int status, string code, string message) =>
        (status, new JsonObject { ["error"] = code, ["message"] = message }.ToJsonString());
}
```

`inventor-addin/SFVaultBridge.Core/Args.cs`:

```csharp
using System.Text.Json.Nodes;

namespace SFVaultBridge.Core;

/// <summary>Typed reads from a request body; any problem is a bad_request naming the field.</summary>
public static class Args
{
    public static string Str(JsonObject b, string key) =>
        b[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)
            ? s
            : throw Bad($"'{key}' is required and must be a string.");

    public static string OptStr(JsonObject b, string key, string fallback = "") =>
        b[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : fallback;

    public static long Long(JsonObject b, string key) =>
        b[key] is JsonValue v && v.TryGetValue<long>(out var l) ? l : throw Bad($"'{key}' is required and must be an integer.");

    public static long[] Longs(JsonObject b, string key, bool required = true)
    {
        if (b[key] is null)
            return required ? throw Bad($"'{key}' is required and must be a list of integers.") : Array.Empty<long>();
        if (b[key] is not JsonArray a) throw Bad($"'{key}' must be a list of integers.");
        return a.Select((n, i) => n is JsonValue v && v.TryGetValue<long>(out var l) ? l : throw Bad($"'{key}[{i}]' must be an integer.")).ToArray();
    }

    public static Dictionary<string, string> StrMap(JsonObject b, string key)
    {
        if (b[key] is null) return new Dictionary<string, string>();
        if (b[key] is not JsonObject o) throw Bad($"'{key}' must be an object of strings.");
        return o.ToDictionary(
            p => p.Key,
            p => p.Value is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw Bad($"'{key}.{p.Key}' must be a string."));
    }

    private static BridgeException Bad(string message) => new("bad_request", message);
}
```

`inventor-addin/SFVaultBridge.Core/Support.cs`:

```csharp
using System.Security.Cryptography;

namespace SFVaultBridge.Core;

/// <summary>The shared secret between the add-in and the Python client.</summary>
public static class TokenStore
{
    public static string LoadOrCreate(string path)
    {
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length >= 32) return existing;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        File.WriteAllText(path, token);
        return token;
    }
}

/// <summary>Only files under the Vault workspace or Downloads may be read or written.</summary>
public sealed class PathGuard
{
    private readonly string[] _roots;

    public PathGuard(IEnumerable<string> roots) =>
        _roots = roots.Where(r => !string.IsNullOrWhiteSpace(r))
                      .Select(r => Path.GetFullPath(r).TrimEnd('\\', '/') + Path.DirectorySeparatorChar)
                      .ToArray();

    public string Check(string? path, string field)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new BridgeException("bad_request", $"'{field}' is required.");
        var full = Path.GetFullPath(path);
        if (!_roots.Any(r => full.StartsWith(r, StringComparison.OrdinalIgnoreCase)))
            throw new BridgeException("path_not_allowed", $"'{field}' must be under the Vault workspace or Downloads: {full}");
        return full;
    }
}

/// <summary>Append-only log; logging never throws.</summary>
public sealed class FileLog
{
    private readonly string _path;
    private readonly object _gate = new();

    public FileLog(string path) => _path = path;

    public void Write(string line)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test inventor-addin/SFVaultBridge.sln`
Expected: all passed, 0 warnings about nullability errors

- [ ] **Step 6: Commit**

```bash
git add .gitignore inventor-addin
git commit -m "SF Vault Bridge core: router, token, path guard, log"
```

---

### Task 2: Core operations: contracts and JSON mapping

**Files:**
- Create: `inventor-addin/SFVaultBridge.Core/Contracts.cs`, `inventor-addin/SFVaultBridge.Core/BridgeOps.cs`
- Create: `inventor-addin/SFVaultBridge.Core.Tests/BridgeOpsTests.cs`

**Interfaces:**
- Consumes: Task 1 `Router`, `Args`, `PathGuard`, `BridgeException`.
- Produces:
  - records `StatusInfo(bool SignedIn, string? User, string? Vault, string? Server)`, `SchemeInfo(long Id, string Name, bool IsDefault)`, `FolderInfo(long Id, string FullName, bool IsLibrary)`, `CategoryInfo(long Id, string Name)`, `VaultFile(long Id, long MasterId, string Name, bool CheckedOut)`
  - `interface IVaultSession`: `StatusInfo Status()`, `IReadOnlyList<SchemeInfo> GetNumberingSchemes()`, `string GenerateFileNumber(long schemeId)`, `FolderInfo? GetFolderByPath(string path)`, `FolderInfo AddFolder(string parentPath, string name)`, `IReadOnlyList<CategoryInfo> GetFileCategories()`, `VaultFile AddFile(string folderPath, string localPath, string fileName, long[] attachmentFileIds, string comment)`, `IReadOnlyList<VaultFile> UpdateFileCategories(long[] masterIds, long categoryId, string comment)`
  - `interface IStepImporter`: `string InventorVersion { get; }`, `string ImportStepAsPart(string stepPath, string outIpt, IReadOnlyDictionary<string, string> design, IReadOnlyDictionary<string, string> custom)`
  - `static class BridgeOps`: `const string NotSignedInMessage = "Sign in to Vault in Inventor (Vault tab, Log In)."`, `void RegisterAll(Router router, IVaultSession vault, IStepImporter inventor, PathGuard guard, string bridgeVersion, Func<Func<JsonNode?>, JsonNode?> onUi)`

- [ ] **Step 1: Write the failing tests**

`inventor-addin/SFVaultBridge.Core.Tests/BridgeOpsTests.cs`:

```csharp
using System.Text.Json.Nodes;
using SFVaultBridge.Core;

namespace SFVaultBridge.Core.Tests;

public class BridgeOpsTests : IDisposable
{
    private const string Token = "0123456789abcdef0123456789abcdef";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sfvb-ops-" + Guid.NewGuid().ToString("N"));

    private sealed class FakeVault : IVaultSession
    {
        public bool SignedIn = true;
        public readonly List<string> Calls = new();
        private void Need() { if (!SignedIn) throw new BridgeException("not_signed_in", BridgeOps.NotSignedInMessage); }

        public StatusInfo Status() => SignedIn ? new(true, "zak@simplifyber.com", "Simplifyber", "simplifyber1") : new(false, null, null, null);
        public IReadOnlyList<SchemeInfo> GetNumberingSchemes() { Need(); return new[] { new SchemeInfo(12, "CAD Data Autonumbering", true) }; }
        public string GenerateFileNumber(long schemeId) { Need(); Calls.Add($"gen {schemeId}"); return "CD-001900"; }
        public FolderInfo? GetFolderByPath(string path) { Need(); return path.EndsWith("/nuts") ? new FolderInfo(84835, path, false) : null; }
        public FolderInfo AddFolder(string parentPath, string name) { Need(); return new FolderInfo(2, parentPath + "/" + name, false); }
        public IReadOnlyList<CategoryInfo> GetFileCategories() { Need(); return new[] { new CategoryInfo(30, "Part - Purchased") }; }
        public VaultFile AddFile(string folderPath, string localPath, string fileName, long[] attachmentFileIds, string comment)
        {
            Need(); Calls.Add($"add {fileName} [{string.Join(",", attachmentFileIds)}] {comment}");
            return new VaultFile(510, 509, fileName, false);
        }
        public IReadOnlyList<VaultFile> UpdateFileCategories(long[] masterIds, long categoryId, string comment)
        {
            Need(); Calls.Add($"cat {string.Join(",", masterIds)} -> {categoryId}");
            return masterIds.Select(m => new VaultFile(m + 1, m, "f", false)).ToList();
        }
    }

    private sealed class FakeInventor : IStepImporter
    {
        public string? LastDesign;
        public string InventorVersion => "2026";
        public string ImportStepAsPart(string stepPath, string outIpt, IReadOnlyDictionary<string, string> design, IReadOnlyDictionary<string, string> custom)
        {
            LastDesign = string.Join(";", design.Select(kv => kv.Key + "=" + kv.Value)) + "|" + string.Join(";", custom.Select(kv => kv.Key + "=" + kv.Value));
            return outIpt;
        }
    }

    private (Router router, FakeVault vault, FakeInventor inv, List<string> uiCalls) Make()
    {
        var vault = new FakeVault();
        var inv = new FakeInventor();
        var ui = new List<string>();
        var router = new Router(Token);
        BridgeOps.RegisterAll(router, vault, inv, new PathGuard(new[] { _root }), "0.1.0", work => { ui.Add("ui"); return work(); });
        return (router, vault, inv, ui);
    }

    public void Dispose() { }

    private static JsonObject Call(Router r, string op, JsonObject body, out int status)
    {
        var (s, json) = r.Handle("POST", "/op/" + op, Token, body.ToJsonString());
        status = s;
        return (JsonObject)JsonNode.Parse(json)!;
    }

    [Fact]
    public void Status_reports_session_and_versions()
    {
        var (r, _, _, ui) = Make();
        var o = Call(r, "status", new JsonObject(), out var st);
        Assert.Equal(200, st);
        Assert.True(o["signed_in"]!.GetValue<bool>());
        Assert.Equal("zak@simplifyber.com", o["user"]!.GetValue<string>());
        Assert.Equal("2026", o["inventor_version"]!.GetValue<string>());
        Assert.Equal("0.1.0", o["bridge_version"]!.GetValue<string>());
        Assert.Single(ui);
    }

    [Fact]
    public void Status_works_when_not_signed_in()
    {
        var (r, vault, _, _) = Make();
        vault.SignedIn = false;
        var o = Call(r, "status", new JsonObject(), out var st);
        Assert.Equal(200, st);
        Assert.False(o["signed_in"]!.GetValue<bool>());
    }

    [Fact]
    public void Vault_ops_return_not_signed_in()
    {
        var (r, vault, _, _) = Make();
        vault.SignedIn = false;
        var o = Call(r, "generate_file_number", new JsonObject { ["schemeId"] = 12 }, out var st);
        Assert.Equal(400, st);
        Assert.Equal("not_signed_in", o["error"]!.GetValue<string>());
        Assert.Equal(BridgeOps.NotSignedInMessage, o["message"]!.GetValue<string>());
    }

    [Fact]
    public void Shapes_match_the_python_wrappers()
    {
        var (r, _, _, _) = Make();
        Assert.Equal("CAD Data Autonumbering", Call(r, "get_numbering_schemes", new(), out _)["schemes"]![0]!["name"]!.GetValue<string>());
        Assert.True(Call(r, "get_numbering_schemes", new(), out _)["schemes"]![0]!["isDefault"]!.GetValue<bool>());
        Assert.Equal("CD-001900", Call(r, "generate_file_number", new() { ["schemeId"] = 12 }, out _)["number"]!.GetValue<string>());
        var found = Call(r, "get_folder_by_path", new() { ["path"] = "$/X/nuts" }, out _);
        Assert.True(found["found"]!.GetValue<bool>());
        Assert.Equal(84835, found["id"]!.GetValue<long>());
        Assert.False(found["isLibrary"]!.GetValue<bool>());
        Assert.False(Call(r, "get_folder_by_path", new() { ["path"] = "$/X/none" }, out _)["found"]!.GetValue<bool>());
        Assert.Equal("$/X/new", Call(r, "add_folder", new() { ["parentPath"] = "$/X", ["name"] = "new" }, out _)["fullName"]!.GetValue<string>());
        Assert.Equal(30, Call(r, "get_file_categories", new(), out _)["categories"]![0]!["id"]!.GetValue<long>());
    }

    [Fact]
    public void Add_file_guards_path_and_passes_attachments()
    {
        var (r, vault, _, _) = Make();
        var local = Path.Combine(_root, "CD-001900.ipt");
        var o = Call(r, "add_file", new() { ["folderPath"] = "$/X", ["localPath"] = local, ["fileName"] = "CD-001900.ipt",
            ["attachmentFileIds"] = new JsonArray(510, 520), ["comment"] = "intake" }, out var st);
        Assert.Equal(200, st);
        Assert.Equal(509, o["masterId"]!.GetValue<long>());
        Assert.Equal("add CD-001900.ipt [510,520] intake", vault.Calls.Single());

        var bad = Call(r, "add_file", new() { ["folderPath"] = "$/X", ["localPath"] = @"C:\Windows\notepad.exe", ["fileName"] = "x" }, out st);
        Assert.Equal(400, st);
        Assert.Equal("path_not_allowed", bad["error"]!.GetValue<string>());
        Assert.Single(vault.Calls);
    }

    [Fact]
    public void Update_categories_requires_ids()
    {
        var (r, vault, _, _) = Make();
        var o = Call(r, "update_file_categories", new() { ["masterIds"] = new JsonArray(529), ["categoryId"] = 30 }, out var st);
        Assert.Equal(200, st);
        Assert.Equal(529, o["updated"]![0]!["masterId"]!.GetValue<long>());
        Assert.Equal("bad_request", Call(r, "update_file_categories", new() { ["masterIds"] = new JsonArray(), ["categoryId"] = 30 }, out _)["error"]!.GetValue<string>());
        Assert.Single(vault.Calls);
    }

    [Fact]
    public void Import_step_guards_both_paths_and_maps_iproperties()
    {
        var (r, _, inv, _) = Make();
        var body = new JsonObject
        {
            ["stepPath"] = Path.Combine(_root, "a.STEP"),
            ["outIpt"] = Path.Combine(_root, "CD-001900.ipt"),
            ["iproperties"] = new JsonObject
            {
                ["design"] = new JsonObject { ["Part Number"] = "CD-001900" },
                ["custom"] = new JsonObject { ["Source"] = "Buy" },
            },
        };
        var o = Call(r, "import_step_as_part", body, out var st);
        Assert.Equal(200, st);
        Assert.EndsWith("CD-001900.ipt", o["outIpt"]!.GetValue<string>());
        Assert.Equal("Part Number=CD-001900|Source=Buy", inv.LastDesign);

        body["outIpt"] = @"C:\Windows\x.ipt";
        Assert.Equal("path_not_allowed", Call(r, "import_step_as_part", body, out _)["error"]!.GetValue<string>());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test inventor-addin/SFVaultBridge.sln`
Expected: build FAIL with `The type or namespace name 'IVaultSession' could not be found`

- [ ] **Step 3: Implement**

`inventor-addin/SFVaultBridge.Core/Contracts.cs`:

```csharp
namespace SFVaultBridge.Core;

public sealed record StatusInfo(bool SignedIn, string? User, string? Vault, string? Server);
public sealed record SchemeInfo(long Id, string Name, bool IsDefault);
public sealed record FolderInfo(long Id, string FullName, bool IsLibrary);
public sealed record CategoryInfo(long Id, string Name);
public sealed record VaultFile(long Id, long MasterId, string Name, bool CheckedOut);

/// <summary>Vault work on the signed-in session. Every member except Status throws
/// BridgeException("not_signed_in") when Inventor's Vault add-in is not signed in.</summary>
public interface IVaultSession
{
    StatusInfo Status();
    IReadOnlyList<SchemeInfo> GetNumberingSchemes();
    string GenerateFileNumber(long schemeId);
    FolderInfo? GetFolderByPath(string path);
    FolderInfo AddFolder(string parentPath, string name);
    IReadOnlyList<CategoryInfo> GetFileCategories();
    VaultFile AddFile(string folderPath, string localPath, string fileName, long[] attachmentFileIds, string comment);
    IReadOnlyList<VaultFile> UpdateFileCategories(long[] masterIds, long categoryId, string comment);
}

/// <summary>Inventor work: turn a STEP into a saved .ipt with iProperties set.</summary>
public interface IStepImporter
{
    string InventorVersion { get; }
    string ImportStepAsPart(string stepPath, string outIpt, IReadOnlyDictionary<string, string> design, IReadOnlyDictionary<string, string> custom);
}
```

`inventor-addin/SFVaultBridge.Core/BridgeOps.cs`:

```csharp
using System.Text.Json.Nodes;

namespace SFVaultBridge.Core;

/// <summary>Every bridge operation: JSON body in, interface call, JSON reply out.
/// Names and shapes match scripts/vault_bridge.py and the older vault_sdk wrappers.</summary>
public static class BridgeOps
{
    public const string NotSignedInMessage = "Sign in to Vault in Inventor (Vault tab, Log In).";

    public static void RegisterAll(Router router, IVaultSession vault, IStepImporter inventor, PathGuard guard,
                                   string bridgeVersion, Func<Func<JsonNode?>, JsonNode?> onUi)
    {
        void Reg(string name, Func<JsonObject, JsonNode?> op) => router.Register(name, body => onUi(() => op(body)));

        Reg("status", _ =>
        {
            var s = vault.Status();
            return new JsonObject
            {
                ["signed_in"] = s.SignedIn, ["user"] = s.User, ["vault"] = s.Vault, ["server"] = s.Server,
                ["inventor_version"] = inventor.InventorVersion, ["bridge_version"] = bridgeVersion,
            };
        });
        Reg("get_numbering_schemes", _ => new JsonObject
        {
            ["schemes"] = Arr(vault.GetNumberingSchemes().Select(s => new JsonObject { ["id"] = s.Id, ["name"] = s.Name, ["isDefault"] = s.IsDefault })),
        });
        Reg("generate_file_number", b => new JsonObject { ["number"] = vault.GenerateFileNumber(Args.Long(b, "schemeId")) });
        Reg("get_folder_by_path", b =>
        {
            var f = vault.GetFolderByPath(Args.Str(b, "path"));
            return f is null
                ? new JsonObject { ["found"] = false }
                : new JsonObject { ["found"] = true, ["id"] = f.Id, ["fullName"] = f.FullName, ["isLibrary"] = f.IsLibrary };
        });
        Reg("add_folder", b =>
        {
            var f = vault.AddFolder(Args.Str(b, "parentPath"), Args.Str(b, "name"));
            return new JsonObject { ["id"] = f.Id, ["fullName"] = f.FullName };
        });
        Reg("get_file_categories", _ => new JsonObject
        {
            ["categories"] = Arr(vault.GetFileCategories().Select(c => new JsonObject { ["id"] = c.Id, ["name"] = c.Name })),
        });
        Reg("add_file", b => FileJson(vault.AddFile(
            Args.Str(b, "folderPath"),
            guard.Check(Args.Str(b, "localPath"), "localPath"),
            Args.Str(b, "fileName"),
            Args.Longs(b, "attachmentFileIds", required: false),
            Args.OptStr(b, "comment"))));
        Reg("update_file_categories", b =>
        {
            var ids = Args.Longs(b, "masterIds");
            if (ids.Length == 0) throw new BridgeException("bad_request", "'masterIds' must not be empty.");
            return new JsonObject
            {
                ["updated"] = Arr(vault.UpdateFileCategories(ids, Args.Long(b, "categoryId"), Args.OptStr(b, "comment")).Select(FileJson)),
            };
        });
        Reg("import_step_as_part", b =>
        {
            var props = b["iproperties"] as JsonObject ?? new JsonObject();
            var outIpt = inventor.ImportStepAsPart(
                guard.Check(Args.Str(b, "stepPath"), "stepPath"),
                guard.Check(Args.Str(b, "outIpt"), "outIpt"),
                Args.StrMap(props, "design"),
                Args.StrMap(props, "custom"));
            return new JsonObject { ["outIpt"] = outIpt };
        });
    }

    private static JsonArray Arr(IEnumerable<JsonObject> items) => new(items.Select(i => (JsonNode?)i).ToArray());

    private static JsonObject FileJson(VaultFile f) =>
        new() { ["id"] = f.Id, ["masterId"] = f.MasterId, ["name"] = f.Name, ["checkedOut"] = f.CheckedOut };
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test inventor-addin/SFVaultBridge.sln`
Expected: all passed

- [ ] **Step 5: Commit**

```bash
git add inventor-addin
git commit -m "SF Vault Bridge core: operation contracts and JSON mapping"
```

---

### Task 3: Core HTTP host

**Files:**
- Create: `inventor-addin/SFVaultBridge.Core/HttpHost.cs`
- Create: `inventor-addin/SFVaultBridge.Core.Tests/HttpHostTests.cs`

**Interfaces:**
- Consumes: Task 1 `Router`.
- Produces: `class HttpHost(string prefix, Router router, Action<string> log) : IDisposable` with `const string TokenHeader = "X-Bridge-Token"`, `void Start()` (throws `HttpListenerException` when the prefix is taken), `void Dispose()`.

- [ ] **Step 1: Write the failing tests**

`inventor-addin/SFVaultBridge.Core.Tests/HttpHostTests.cs`:

```csharp
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
    public void Dispose_before_start_is_safe()
    {
        var host = new HttpHost($"http://localhost:{FreePort()}/", EchoRouter(), _ => { });
        host.Dispose();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test inventor-addin/SFVaultBridge.sln`
Expected: build FAIL with `The type or namespace name 'HttpHost' could not be found`

- [ ] **Step 3: Implement**

`inventor-addin/SFVaultBridge.Core/HttpHost.cs`:

```csharp
using System.Net;
using System.Text;

namespace SFVaultBridge.Core;

/// <summary>Local HTTP listener. Requests are handled one at a time on one background thread.</summary>
public sealed class HttpHost : IDisposable
{
    public const string TokenHeader = "X-Bridge-Token";

    private readonly HttpListener _listener = new();
    private readonly Router _router;
    private readonly Action<string> _log;
    private Thread? _thread;

    public HttpHost(string prefix, Router router, Action<string> log)
    {
        _listener.Prefixes.Add(prefix);
        _router = router;
        _log = log;
    }

    public void Start()
    {
        _listener.Start();
        _thread = new Thread(Loop) { IsBackground = true, Name = "SFVaultBridge" };
        _thread.Start();
    }

    private void Loop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = _listener.GetContext(); }
            catch (Exception) when (!_listener.IsListening) { return; }
            catch (Exception ex) { _log("accept failed: " + ex.Message); continue; }

            try
            {
                string body;
                using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) body = reader.ReadToEnd();
                var path = ctx.Request.Url?.AbsolutePath ?? "/";
                var (status, json) = _router.Handle(ctx.Request.HttpMethod, path, ctx.Request.Headers[TokenHeader], body);
                var bytes = Encoding.UTF8.GetBytes(json);
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes);
                ctx.Response.Close();
                if (status >= 400) _log($"{path} -> {status} {json}");
            }
            catch (Exception ex)
            {
                _log("request failed: " + ex);
                try { ctx.Response.Abort(); } catch { }
            }
        }
    }

    public void Dispose()
    {
        try { if (_listener.IsListening) _listener.Stop(); } catch (ObjectDisposedException) { }
        _listener.Close();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test inventor-addin/SFVaultBridge.sln`
Expected: all passed

- [ ] **Step 5: Commit**

```bash
git add inventor-addin
git commit -m "SF Vault Bridge core: local HTTP host"
```

---

### Task 4: The Inventor add-in, manifest and installer

**Files:**
- Create: `inventor-addin/SFVaultBridge.Addin/SFVaultBridge.Addin.csproj`, `inventor-addin/SFVaultBridge.Addin/VaultSession.cs`, `inventor-addin/SFVaultBridge.Addin/StepImporter.cs`, `inventor-addin/SFVaultBridge.Addin/AddInServer.cs`
- Create: `inventor-addin/SFVaultBridge.addin`, `inventor-addin/Install-VaultBridge.ps1`, `inventor-addin/README.md`

**Interfaces:**
- Consumes: Task 1-3 `Router`, `PathGuard`, `TokenStore`, `FileLog`, `HttpHost`, `BridgeOps`, `IVaultSession`, `IStepImporter`, records.
- Produces: `SFVaultBridge.Addin.dll` with `AddInServer` (GUID `6F1C2D9A-4B7E-4E2B-9C1A-5D3E8F7A2B10`), installed by `Install-VaultBridge.ps1`.

No unit tests in this task: every class wraps the live Inventor or Vault API. The gate is a clean build of the whole solution with the Core tests still green, plus Task 7's live smoke test.

- [ ] **Step 1: Create the project**

```bash
cd inventor-addin
dotnet new classlib -n SFVaultBridge.Addin -f net8.0 -o SFVaultBridge.Addin
rm SFVaultBridge.Addin/Class1.cs
dotnet sln add SFVaultBridge.Addin/SFVaultBridge.Addin.csproj
```

Replace `SFVaultBridge.Addin/SFVaultBridge.Addin.csproj` with:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <PlatformTarget>x64</PlatformTarget>
    <EnableDynamicLoading>true</EnableDynamicLoading>
    <AssemblyName>SFVaultBridge.Addin</AssemblyName>
    <RootNamespace>SFVaultBridge.Addin</RootNamespace>
    <InventorBin>C:\Program Files\Autodesk\Inventor 2026\Bin</InventorBin>
    <VaultBundle>C:\ProgramData\Autodesk\ApplicationPlugins\VaultInventor2026.bundle\Contents</VaultBundle>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\SFVaultBridge.Core\SFVaultBridge.Core.csproj" />
  </ItemGroup>
  <ItemGroup>
    <Reference Include="Autodesk.Inventor.Interop">
      <HintPath>$(InventorBin)\Autodesk.Inventor.Interop.dll</HintPath>
      <Private>false</Private>
      <EmbedInteropTypes>false</EmbedInteropTypes>
    </Reference>
    <Reference Include="Autodesk.Connectivity.WebServices">
      <HintPath>$(VaultBundle)\Autodesk.Connectivity.WebServices.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="Autodesk.DataManagement.Client.Framework">
      <HintPath>$(VaultBundle)\Autodesk.DataManagement.Client.Framework.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="Autodesk.DataManagement.Client.Framework.Vault">
      <HintPath>$(VaultBundle)\Autodesk.DataManagement.Client.Framework.Vault.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="Connectivity.Application.VaultBase">
      <HintPath>$(VaultBundle)\Connectivity.Application.VaultBase.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Write `VaultSession.cs`**

```csharp
using Autodesk.Connectivity.WebServices;
using SFVaultBridge.Core;
using VDF = Autodesk.DataManagement.Client.Framework;
using WsFile = Autodesk.Connectivity.WebServices.File;
using WsFolder = Autodesk.Connectivity.WebServices.Folder;

namespace SFVaultBridge.Addin;

/// <summary>IVaultSession on the Inventor Vault add-in's signed-in connection. No second sign-in, no extra licence seat.</summary>
internal sealed class VaultSession : IVaultSession
{
    private static VDF.Vault.Currency.Connections.Connection? Current =>
        Connectivity.Application.VaultBase.ConnectionManager.Instance.Connection;

    private static VDF.Vault.Currency.Connections.Connection Conn =>
        Current ?? throw new BridgeException("not_signed_in", BridgeOps.NotSignedInMessage);

    public StatusInfo Status()
    {
        var c = Current;
        return c is null ? new StatusInfo(false, null, null, null) : new StatusInfo(true, c.UserName, c.Vault, c.Server);
    }

    public IReadOnlyList<SchemeInfo> GetNumberingSchemes() =>
        (Conn.WebServiceManager.NumberingService.GetNumberingSchemes("FILE", NumSchmType.Activated) ?? Array.Empty<NumSchm>())
            .Select(s => new SchemeInfo(s.SchmID, s.Name, s.IsDflt)).ToList();

    public string GenerateFileNumber(long schemeId) =>
        Conn.WebServiceManager.DocumentService.GenerateFileNumber(schemeId, Array.Empty<string>());

    public FolderInfo? GetFolderByPath(string path)
    {
        var f = FindFolder(Conn, path);
        return f is null ? null : new FolderInfo(f.Id, f.FullName, f.IsLib);
    }

    public FolderInfo AddFolder(string parentPath, string name)
    {
        var conn = Conn;
        var parent = FindFolder(conn, parentPath) ?? throw new BridgeException("not_found", $"Vault folder {parentPath} does not exist.");
        var f = conn.WebServiceManager.DocumentService.AddFolder(name, parent.Id, parent.IsLib);
        return new FolderInfo(f.Id, f.FullName, f.IsLib);
    }

    public IReadOnlyList<CategoryInfo> GetFileCategories() =>
        Conn.WebServiceManager.CategoryService.GetCategoriesByEntityClassId("FILE", true)
            .Select(c => new CategoryInfo(c.Id, c.Name)).ToList();

    public VaultFile AddFile(string folderPath, string localPath, string fileName, long[] attachmentFileIds, string comment)
    {
        var conn = Conn;
        if (!System.IO.File.Exists(localPath)) throw new BridgeException("not_found", $"Local file not found: {localPath}");
        var wsFolder = FindFolder(conn, folderPath) ?? throw new BridgeException("not_found", $"Vault folder {folderPath} does not exist.");
        var folder = new VDF.Vault.Currency.Entities.Folder(conn, wsFolder);
        var assocs = attachmentFileIds
            .Select(id => new FileAssocParam { CldFileId = id, Typ = AssociationType.Attachment, RefId = null, Source = null, ExpectedVaultPath = null })
            .ToArray();
        using var stream = System.IO.File.OpenRead(localPath);
        var added = conn.FileManager.AddFile(folder, fileName, comment, System.IO.File.GetLastWriteTime(localPath),
                                             assocs, null, FileClassification.None, false, stream);
        return ToFile(conn.WebServiceManager.DocumentService.GetLatestFileByMasterId(added.EntityMasterId));
    }

    public IReadOnlyList<VaultFile> UpdateFileCategories(long[] masterIds, long categoryId, string comment) =>
        Conn.WebServiceManager.DocumentServiceExtensions
            .UpdateFileCategories(masterIds, masterIds.Select(_ => categoryId).ToArray(), comment)
            .Select(ToFile).ToList();

    private static WsFolder? FindFolder(VDF.Vault.Currency.Connections.Connection conn, string path)
    {
        var f = conn.WebServiceManager.DocumentService.FindFoldersByPaths(new[] { path })?.FirstOrDefault();
        return f is null || f.Id <= 0 ? null : f;
    }

    private static VaultFile ToFile(WsFile f) => new(f.Id, f.MasterId, f.Name, f.CheckedOut);
}
```

- [ ] **Step 3: Write `StepImporter.cs`**

```csharp
using System.Runtime.InteropServices;
using SFVaultBridge.Core;
using Inv = Inventor;

namespace SFVaultBridge.Addin;

/// <summary>Opens a STEP as a part, sets iProperties, saves it as .ipt. Same rules as the Python version it replaces.</summary>
internal sealed class StepImporter : IStepImporter
{
    private readonly Inv.Application _app;

    public StepImporter(Inv.Application app) => _app = app;

    public string InventorVersion => _app.SoftwareVersion.DisplayVersion;

    public string ImportStepAsPart(string stepPath, string outIpt, IReadOnlyDictionary<string, string> design, IReadOnlyDictionary<string, string> custom)
    {
        if (!System.IO.File.Exists(stepPath)) throw new BridgeException("not_found", $"STEP file not found: {stepPath}");
        if (System.IO.File.Exists(outIpt)) throw new BridgeException("exists", $"Refusing to overwrite existing file {outIpt}");

        var doc = _app.Documents.Open(stepPath, false);
        try
        {
            if (doc.DocumentType != Inv.DocumentTypeEnum.kPartDocumentObject)
                throw new BridgeException("not_a_part", $"{System.IO.Path.GetFileName(stepPath)} did not open as a single part. Import it by hand.");
            if (doc.File.ReferencedFileDescriptors.Count > 0)
                throw new BridgeException("reference_model", "The STEP opened as a reference model. Set Inventor's STEP import to Convert Model, then retry.");

            var tracking = doc.PropertySets["Design Tracking Properties"];
            foreach (var (name, value) in design) tracking[name].Value = value;
            var user = doc.PropertySets["Inventor User Defined Properties"];
            foreach (var (name, value) in custom)
            {
                try { user[name].Value = value; }
                catch (COMException) { user.Add(value, name); }
            }

            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outIpt)!);
            doc.SaveAs(outIpt, false);
            return outIpt;
        }
        finally
        {
            try { doc.Close(true); } catch (COMException) { }
        }
    }
}
```

If the interop exposes `PropertySets.Item` / `PropertySet.Item` instead of indexers, or `PropertySet.Add` needs a third `PropId` argument, use `Item(...)` and pass `Type.Missing`; keep the behavior identical and say so in the report.

- [ ] **Step 4: Write `AddInServer.cs`**

```csharp
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using SFVaultBridge.Core;
using Inv = Inventor;

namespace SFVaultBridge.Addin;

/// <summary>Inventor entry point. Starts the local bridge; never lets an exception reach Inventor.</summary>
[Guid("6F1C2D9A-4B7E-4E2B-9C1A-5D3E8F7A2B10")]
[ComVisible(true)]
public sealed class AddInServer : Inv.ApplicationAddInServer
{
    public const string Version = "0.1.0";
    public const string Prefix = "http://localhost:8790/";

    private Inv.Application? _app;
    private System.Windows.Forms.Control? _ui;
    private HttpHost? _host;
    private FileLog? _log;

    public void Activate(Inv.ApplicationAddInSite addInSiteObject, bool firstTime)
    {
        var dataDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Simplifyber", "VaultBridge");
        _log = new FileLog(System.IO.Path.Combine(dataDir, "bridge.log"));
        try
        {
            _app = addInSiteObject.Application;
            _ui = new System.Windows.Forms.Control();
            _ui.CreateControl();
            var token = TokenStore.LoadOrCreate(System.IO.Path.Combine(dataDir, "token"));
            var router = new Router(token);
            BridgeOps.RegisterAll(router, new VaultSession(), new StepImporter(_app), new PathGuard(AllowedRoots(_app)), Version, RunOnUi);
            _host = new HttpHost(Prefix, router, _log.Write);
            _host.Start();
            _log.Write($"started {Version} on {Prefix}");
        }
        catch (Exception ex)
        {
            _log.Write("start failed, bridge stopped: " + ex);
            _host?.Dispose();
            _host = null;
        }
    }

    public void Deactivate()
    {
        try
        {
            _host?.Dispose();
            _ui?.Dispose();
            _log?.Write("stopped");
        }
        catch (Exception ex) { _log?.Write("stop failed: " + ex); }
        _host = null;
        _ui = null;
        _app = null;
    }

    public void ExecuteCommand(int commandID) { }

    public object Automation => null!;

    private JsonNode? RunOnUi(Func<JsonNode?> work)
    {
        var ui = _ui ?? throw new BridgeException("stopped", "The bridge is shutting down.");
        if (!ui.InvokeRequired) return work();
        JsonNode? result = null;
        ExceptionDispatchInfo? error = null;
        ui.Invoke(new Action(() =>
        {
            try { result = work(); }
            catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
        }));
        error?.Throw();
        return result;
    }

    private static IEnumerable<string> AllowedRoots(Inv.Application app)
    {
        yield return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        string? workspace = null;
        try { workspace = app.DesignProjectManager.ActiveDesignProject.WorkspacePath; } catch (COMException) { }
        yield return string.IsNullOrWhiteSpace(workspace) ? @"C:\Vault Workspace" : workspace;
    }
}
```

- [ ] **Step 5: Write the manifest template `inventor-addin/SFVaultBridge.addin`**

```xml
<?xml version="1.0" encoding="utf-8"?>
<Addin Type="Standard">
  <ClassId>{6F1C2D9A-4B7E-4E2B-9C1A-5D3E8F7A2B10}</ClassId>
  <ClientId>{6F1C2D9A-4B7E-4E2B-9C1A-5D3E8F7A2B10}</ClientId>
  <DisplayName>SF Vault Bridge</DisplayName>
  <Description>Lets the Simplifyber Vault MCP server work in Vault through this Inventor's signed-in session.</Description>
  <Assembly>{{ASSEMBLY}}</Assembly>
  <LoadOnStartUp>1</LoadOnStartUp>
  <UserUnloadable>1</UserUnloadable>
  <Hidden>0</Hidden>
  <SupportedSoftwareVersionEqualTo>30..</SupportedSoftwareVersionEqualTo>
  <DataVersion>1</DataVersion>
</Addin>
```

- [ ] **Step 6: Write `inventor-addin/Install-VaultBridge.ps1`**

```powershell
<#
Builds the SF Vault Bridge and installs it for the current Windows user. No admin rights.
Close Inventor first; start it again afterwards and sign in to Vault.
#>
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'

if (Get-Process Inventor -ErrorAction SilentlyContinue) {
    throw 'Close Inventor before installing the SF Vault Bridge.'
}
$root = $PSScriptRoot
$dest = Join-Path $env:APPDATA 'Simplifyber\VaultBridge'
$addins = Join-Path $env:APPDATA 'Autodesk\Inventor 2026\Addins'

dotnet build (Join-Path $root 'SFVaultBridge.Addin\SFVaultBridge.Addin.csproj') -c $Configuration --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

$out = Join-Path $root "SFVaultBridge.Addin\bin\$Configuration\net8.0-windows"
New-Item -ItemType Directory -Force -Path $dest, $addins | Out-Null
Copy-Item (Join-Path $out 'SFVaultBridge.*') $dest -Force

$assembly = Join-Path $dest 'SFVaultBridge.Addin.dll'
$manifest = (Get-Content (Join-Path $root 'SFVaultBridge.addin') -Raw).Replace('{{ASSEMBLY}}', $assembly)
Set-Content -Path (Join-Path $addins 'SFVaultBridge.addin') -Value $manifest -Encoding UTF8

Write-Host "Installed SF Vault Bridge to $dest"
Write-Host 'Start Inventor and sign in to Vault (Vault tab, Log In).'
```

- [ ] **Step 7: Write `inventor-addin/README.md`**

```markdown
# SF Vault Bridge

Inventor 2026 add-in that lets the Vault MCP server work in Vault through
Inventor's signed-in session, so no second Vault licence seat is needed.
Spec: `docs/superpowers/specs/2026-10-12-inventor-vault-bridge-design.md`.

## Install (per Windows user, no admin)

1. Close Inventor.
2. `pwsh -File inventor-addin/Install-VaultBridge.ps1` (needs the .NET 8 SDK on the build PC).
3. Start Inventor and sign in to Vault.

## Check it

- Log: `%APPDATA%\Simplifyber\VaultBridge\bridge.log` should say `started 0.1.0 on http://localhost:8790/`.
- `py -3.14 -c "import sys; sys.path.insert(0, 'scripts'); import vault_bridge; print(vault_bridge.status())"`

## Uninstall

Delete `%APPDATA%\Autodesk\Inventor 2026\Addins\SFVaultBridge.addin` and `%APPDATA%\Simplifyber\VaultBridge`.
```

- [ ] **Step 8: Build and run the Core tests**

Run: `dotnet build inventor-addin/SFVaultBridge.sln -c Release` then `dotnet test inventor-addin/SFVaultBridge.sln`
Expected: build succeeds with no errors (warnings about nullable interop members are acceptable; list them in the report), all Core tests pass. `inventor-addin/SFVaultBridge.Addin/bin/Release/net8.0-windows/` contains `SFVaultBridge.Addin.dll` and `SFVaultBridge.Core.dll` and no Autodesk DLLs.

Do NOT run the installer: Inventor is open on Zak's machine and the install is part of Task 7.

- [ ] **Step 9: Commit**

```bash
git add inventor-addin
git commit -m "SF Vault Bridge Inventor add-in, manifest and installer"
```

---

### Task 5: Python client `vault_bridge.py`

**Files:**
- Create: `scripts/vault_bridge.py`
- Test: `tests/test_vault_bridge.py`

**Interfaces:**
- Produces (module `vault_bridge`, importable when `scripts/` is on `sys.path`):
  - `BASE_URL = "http://localhost:8790"`, `TOKEN_HEADER = "X-Bridge-Token"`, `DEFAULT_TIMEOUT_SECONDS = 300`
  - `token_path() -> Path`
  - `class BridgeUnavailable(RuntimeError)`, `class BridgeError(RuntimeError)` with `.code`
  - `status() -> dict`, `check_inventor_running() -> dict` (raises `BridgeUnavailable` when not signed in)
  - `get_numbering_schemes()`, `generate_file_number(scheme_id)`, `get_folder_by_path(path)`, `add_folder(parent_path, name)`, `get_file_categories()`, `add_file(folder_path, local_path, file_name, *, attachment_file_ids=(), comment="")`, `update_file_categories(master_ids, category_id, *, comment="")`, `import_step_as_part(step_path, out_ipt, iproperties)` with the same returns as the `vault_sdk` wrappers.

- [ ] **Step 1: Write the failing tests**

```python
# tests/test_vault_bridge.py
import json
import sys
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "scripts"))
import vault_bridge as vb  # noqa: E402

TOKEN = "a" * 64


class Handler(BaseHTTPRequestHandler):
    replies = {}
    seen = []
    delay = 0.0

    def do_POST(self):
        import time
        length = int(self.headers.get("Content-Length") or 0)
        body = json.loads(self.rfile.read(length) or b"{}")
        op = self.path.rsplit("/", 1)[-1]
        type(self).seen.append((op, body, self.headers.get(vb.TOKEN_HEADER)))
        if type(self).delay:
            time.sleep(type(self).delay)
        status, payload = type(self).replies.get(op, (200, {}))
        data = json.dumps(payload).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def log_message(self, *args):
        pass


@pytest.fixture
def server(monkeypatch, tmp_path):
    Handler.replies, Handler.seen, Handler.delay = {}, [], 0.0
    httpd = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    threading.Thread(target=httpd.serve_forever, daemon=True).start()
    monkeypatch.setattr(vb, "BASE_URL", f"http://127.0.0.1:{httpd.server_port}")
    token_file = tmp_path / "token"
    token_file.write_text(TOKEN, encoding="utf-8")
    monkeypatch.setattr(vb, "token_path", lambda: token_file)
    yield Handler
    httpd.shutdown()


def test_sends_token_and_body(server):
    server.replies["add_file"] = (200, {"id": 510, "masterId": 509, "name": "x.pdf", "checkedOut": False})
    out = vb.add_file("$/X", r"C:\Vault Workspace\x.pdf", "x.pdf", attachment_file_ids=["7"], comment="c")
    assert out["masterId"] == 509
    op, body, token = server.seen[0]
    assert (op, token) == ("add_file", TOKEN)
    assert body == {"folderPath": "$/X", "localPath": r"C:\Vault Workspace\x.pdf", "fileName": "x.pdf",
                    "attachmentFileIds": [7], "comment": "c"}


def test_call_shapes(server):
    vb.generate_file_number("12")
    vb.update_file_categories(["5", 6], "30", comment="intake")
    vb.import_step_as_part(r"C:\a.STEP", r"C:\b.ipt", {"design": {"Part Number": "CD-1"}, "custom": {}})
    vb.get_folder_by_path("$/X")
    vb.add_folder("$/X", "nuts")
    assert [(o, b) for o, b, _ in server.seen] == [
        ("generate_file_number", {"schemeId": 12}),
        ("update_file_categories", {"masterIds": [5, 6], "categoryId": 30, "comment": "intake"}),
        ("import_step_as_part", {"stepPath": r"C:\a.STEP", "outIpt": r"C:\b.ipt",
                                 "iproperties": {"design": {"Part Number": "CD-1"}, "custom": {}}}),
        ("get_folder_by_path", {"path": "$/X"}),
        ("add_folder", {"parentPath": "$/X", "name": "nuts"}),
    ]


def test_update_categories_rejects_empty(server):
    with pytest.raises(ValueError):
        vb.update_file_categories([], 30)
    assert server.seen == []


def test_not_signed_in_is_unavailable(server):
    server.replies["generate_file_number"] = (400, {"error": "not_signed_in", "message": "Sign in to Vault in Inventor (Vault tab, Log In)."})
    with pytest.raises(vb.BridgeUnavailable, match="Sign in to Vault"):
        vb.generate_file_number(12)


def test_other_error_keeps_code(server):
    server.replies["add_file"] = (400, {"error": "path_not_allowed", "message": "nope"})
    with pytest.raises(vb.BridgeError) as info:
        vb.add_file("$/X", r"C:\Windows\x", "x")
    assert info.value.code == "path_not_allowed"


def test_check_inventor_running(server):
    server.replies["status"] = (200, {"signed_in": False})
    with pytest.raises(vb.BridgeUnavailable, match="Sign in to Vault"):
        vb.check_inventor_running()
    server.replies["status"] = (200, {"signed_in": True, "user": "zak"})
    assert vb.check_inventor_running()["user"] == "zak"


def test_no_server_is_unavailable(monkeypatch, tmp_path):
    token_file = tmp_path / "token"
    token_file.write_text(TOKEN, encoding="utf-8")
    monkeypatch.setattr(vb, "token_path", lambda: token_file)
    monkeypatch.setattr(vb, "BASE_URL", "http://127.0.0.1:9")
    with pytest.raises(vb.BridgeUnavailable, match="Inventor is not running"):
        vb.status()


def test_no_token_file_is_unavailable(monkeypatch, tmp_path):
    monkeypatch.setattr(vb, "token_path", lambda: tmp_path / "missing")
    with pytest.raises(vb.BridgeUnavailable, match="never started"):
        vb.status()


def test_timeout_is_uncertain_not_unavailable(server):
    server.delay = 1.0
    with pytest.raises(vb.BridgeError) as info:
        vb._call("status", {}, timeout=0.2)
    assert info.value.code == "timeout"
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `py -3.14 -m pytest tests/test_vault_bridge.py -v`
Expected: FAIL with `ModuleNotFoundError: No module named 'vault_bridge'`

- [ ] **Step 3: Implement `scripts/vault_bridge.py`**

```python
"""Client for the SF Vault Bridge Inventor add-in (inventor-addin/).

The add-in runs inside Inventor and does Vault writes on the user's own
signed-in session, so no second Vault licence seat is needed. Function names
and returns match the scripts/vault_sdk.py wrappers the McMaster intake used
before. Spec: docs/superpowers/specs/2026-10-12-inventor-vault-bridge-design.md
"""
from __future__ import annotations

import json
import os
import socket
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any, Iterable

BASE_URL = "http://localhost:8790"
TOKEN_HEADER = "X-Bridge-Token"
DEFAULT_TIMEOUT_SECONDS = 300
NOT_RUNNING = ("Inventor is not running, or the SF Vault Bridge add-in is not loaded. "
               "Start Inventor and sign in to Vault.")


class BridgeUnavailable(RuntimeError):
    """Inventor, the add-in or the Vault sign-in is missing. Nothing was done."""


class BridgeError(RuntimeError):
    """The bridge answered with an error code."""

    def __init__(self, code: str, message: str):
        super().__init__(f"{code}: {message}")
        self.code = code


def token_path() -> Path:
    return Path(os.environ.get("APPDATA", "")) / "Simplifyber" / "VaultBridge" / "token"


def _call(op: str, body: dict[str, Any] | None = None, *, timeout: float = DEFAULT_TIMEOUT_SECONDS) -> dict[str, Any]:
    try:
        token = token_path().read_text(encoding="utf-8").strip()
    except OSError:
        raise BridgeUnavailable("The SF Vault Bridge has never started on this PC. Install it and start Inventor.") from None
    req = urllib.request.Request(
        f"{BASE_URL}/op/{op}", data=json.dumps(body or {}).encode("utf-8"), method="POST",
        headers={"Content-Type": "application/json", TOKEN_HEADER: token})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return json.loads(resp.read() or b"{}")
    except urllib.error.HTTPError as exc:
        try:
            payload = json.loads(exc.read() or b"{}")
        except ValueError:
            payload = {}
        code = payload.get("error") or f"http_{exc.code}"
        message = payload.get("message") or str(exc)
        if code == "not_signed_in":
            raise BridgeUnavailable(message) from None
        raise BridgeError(code, message) from None
    except (TimeoutError, socket.timeout):
        raise BridgeError("timeout", f"The bridge did not answer {op} within {timeout:g} s. It may still have finished; check Vault.") from None
    except urllib.error.URLError as exc:
        if isinstance(exc.reason, (TimeoutError, socket.timeout)):
            raise BridgeError("timeout", f"The bridge did not answer {op} within {timeout:g} s. It may still have finished; check Vault.") from None
        raise BridgeUnavailable(NOT_RUNNING) from exc
    except ConnectionError as exc:
        raise BridgeUnavailable(NOT_RUNNING) from exc


def status() -> dict[str, Any]:
    return _call("status", timeout=10)


def check_inventor_running() -> dict[str, Any]:
    """Status, or BridgeUnavailable when Inventor's Vault add-in is not signed in."""
    s = status()
    if not s.get("signed_in"):
        raise BridgeUnavailable("Sign in to Vault in Inventor (Vault tab, Log In).")
    return s


def get_numbering_schemes() -> dict[str, Any]:
    return _call("get_numbering_schemes")


def generate_file_number(scheme_id: int | str) -> dict[str, Any]:
    return _call("generate_file_number", {"schemeId": int(scheme_id)})


def get_folder_by_path(path: str) -> dict[str, Any]:
    return _call("get_folder_by_path", {"path": path})


def add_folder(parent_path: str, name: str) -> dict[str, Any]:
    return _call("add_folder", {"parentPath": parent_path, "name": name})


def get_file_categories() -> dict[str, Any]:
    return _call("get_file_categories")


def add_file(folder_path: str, local_path: str, file_name: str, *,
             attachment_file_ids: Iterable[int | str] = (), comment: str = "") -> dict[str, Any]:
    return _call("add_file", {
        "folderPath": folder_path, "localPath": str(local_path), "fileName": file_name,
        "attachmentFileIds": [int(i) for i in attachment_file_ids], "comment": comment,
    })


def update_file_categories(master_ids: Iterable[int | str], category_id: int | str, *, comment: str = "") -> dict[str, Any]:
    ids = [int(i) for i in master_ids]
    if not ids:
        raise ValueError("master_ids must not be empty")
    return _call("update_file_categories", {"masterIds": ids, "categoryId": int(category_id), "comment": comment})


def import_step_as_part(step_path: str | Path, out_ipt: str | Path, iproperties: dict) -> Path:
    out = _call("import_step_as_part", {"stepPath": str(step_path), "outIpt": str(out_ipt), "iproperties": iproperties})
    return Path(out.get("outIpt") or out_ipt)
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `py -3.14 -m pytest tests/test_vault_bridge.py -v`
Expected: 9 passed

- [ ] **Step 5: Commit**

```bash
git add scripts/vault_bridge.py tests/test_vault_bridge.py
git commit -m "vault_bridge: Python client for the SF Vault Bridge add-in"
```

---

### Task 6: McMaster intake writes through the bridge

**Files:**
- Modify: `mcmaster_intake.py` (every use of `_sdk()` and the default `inventor` object)
- Modify: `tests/test_mcmaster_intake_tools.py`, `tests/test_part_specs_tool.py` (the fake module key)
- Modify: `README.md` (McMaster intake section)

**Interfaces:**
- Consumes: Task 5 `vault_bridge` functions (same names and returns as the `vault_sdk` wrappers the intake calls today, plus `check_inventor_running` and `import_step_as_part`).
- Produces: `register(...)` unchanged in signature. Writes go to `vault_bridge`; the default `inventor` object is `vault_bridge`.

- [ ] **Step 1: Point the tests at the bridge module first (they fail)**

In `tests/test_mcmaster_intake_tools.py` and `tests/test_part_specs_tool.py`, change every
`monkeypatch.setitem(sys.modules, "vault_sdk", <fake>)` to
`monkeypatch.setitem(sys.modules, "vault_bridge", <fake>)` (the fake classes stay; rename the `super().__init__("vault_sdk")` argument to `"vault_bridge"`). Add one new test to `tests/test_mcmaster_intake_tools.py`:

```python
def test_commit_defaults_to_bridge_for_inventor(monkeypatch, tmp_path):
    """With no inventor injected, the STEP import and running check go to vault_bridge."""
    dl = tmp_path / "Downloads"
    dl.mkdir()
    (dl / "90510A313_Nut.STEP").write_bytes(b"step")
    (dl / "90510A313.pdf").write_bytes(b"%PDF")
    sdk = FakeSDK()
    calls = []
    sdk.check_inventor_running = lambda: calls.append("check") or {"signed_in": True}

    def fake_import(step, out, iprops):
        calls.append("import")
        Path(out).parent.mkdir(parents=True, exist_ok=True)
        Path(out).write_bytes(b"ipt")
        return Path(out)

    sdk.import_step_as_part = fake_import
    monkeypatch.setitem(sys.modules, "vault_bridge", sdk)

    async def fake_fetch(api, vault_id, file_name):
        return {"properties": dict(GOOD_PROPS), "note": ""}

    import check_file_properties
    monkeypatch.setattr(check_file_properties, "fetch_file", fake_fetch)
    mcp = FakeMCP()

    async def no_sleep(_):
        return None

    mi.register(mcp, FakeAPI(), lambda v: "1",
                config={"downloads_dir": str(dl), "workspace_root": str(tmp_path / "ws"), "numbering_scheme": "CD Parts"},
                sleep=no_sleep)
    token = json.loads(asyncio.run(mcp.tools["vault_mcmaster_intake_preview"](
        mcmaster_pn="90510A313", spec_card_json=json.dumps(CARD),
        description="Fully Threaded T-Slot Nut", finish="Black Oxide")))["token"]
    out = json.loads(asyncio.run(mcp.tools["vault_mcmaster_intake_commit"](token=token)))
    assert out["applied"] is True, out
    assert calls == ["check", "import"]
```

If `FakeAPI` in that file needs constructor arguments, or the CD clash check needs `search_files`, pass/extend them the same way the existing happy-path test does; keep the assertion on `calls`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `py -3.14 -m pytest tests/test_mcmaster_intake_tools.py tests/test_part_specs_tool.py -q`
Expected: FAIL (the code still imports `vault_sdk`, which is now the real module, and the default inventor is `inventor_automation`)

- [ ] **Step 3: Switch the intake to the bridge**

In `mcmaster_intake.py`:
- Add next to the other lazy helpers inside `register` (or at module level, matching where `_sdk` is used today):

```python
def _bridge():
    """The Vault/Inventor writer: the SF Vault Bridge add-in (scripts/vault_bridge.py)."""
    import vault_bridge
    return vault_bridge
```

- Replace every `_sdk()` call used for intake writes and reads (`get_folder_by_path`, `get_numbering_schemes`, `generate_file_number`, `add_folder`, `add_file`, `get_file_categories`, `update_file_categories`) with `_bridge()`. Keep the import of `_dump` and `_sdk_error` from `vault_write_tools`.
- Change the default inventor object: where the code does `import inventor_automation` / `return inventor_automation` for `inventor is None`, return `_bridge()` instead.
- In `failed()` (or wherever a step exception is turned into the reply), keep using `str(exc)` as the error text. A `vault_bridge.BridgeUnavailable` already carries the plain-language message.

- [ ] **Step 4: Run tests to verify they pass**

Run: `py -3.14 -m pytest tests/test_mcmaster_intake_tools.py tests/test_part_specs_tool.py tests/test_vault_bridge.py -q`
Expected: all passed

- [ ] **Step 5: Update the README McMaster intake section**

Replace the prerequisites that mention closing Vault Explorer, the SDK licence seat and signing out of Inventor's Vault add-in with:

```markdown
**Before the first intake:** install the SF Vault Bridge (`inventor-addin/README.md`),
start Inventor and **sign in to Vault there**. All intake writes run on that session,
so Vault Explorer can stay open and no extra licence seat is used. Set Inventor's
STEP import option to Convert Model.
```

Keep the rest of the section (download, preview, approve).

- [ ] **Step 6: Run the full suite**

Run: `py -3.14 -m pytest -q`
Expected: all passed (814 + 9 new bridge tests + 1 new intake test), 5 skipped

- [ ] **Step 7: Commit**

```bash
git add mcmaster_intake.py tests/test_mcmaster_intake_tools.py tests/test_part_specs_tool.py README.md
git commit -m "McMaster intake writes through the SF Vault Bridge"
```

---

### Task 7: Install and live smoke test (with Zak)

**Files:** none (live run). Record results in the session, not in the repo.

- [ ] **Step 1: Zak closes Inventor.** Then run: `pwsh -File inventor-addin/Install-VaultBridge.ps1`
Expected: `Installed SF Vault Bridge to C:\Users\ZakOlech\AppData\Roaming\Simplifyber\VaultBridge`

- [ ] **Step 2: Zak starts Inventor and signs in to Vault.** Then check the log:
Run: `Get-Content "$env:APPDATA\Simplifyber\VaultBridge\bridge.log" -Tail 5`
Expected: a line `started 0.1.0 on http://localhost:8790/`. If it says `start failed`, stop and report the logged exception.

- [ ] **Step 3: Status**
Run: `py -3.14 -c "import sys; sys.path.insert(0, 'scripts'); import vault_bridge as b; print(b.status())"`
Expected: `signed_in: True`, `user: zak@simplifyber.com`, `vault: Simplifyber`.

- [ ] **Step 4: Write test in the playground** (folder `$/DESIGNS/PLAYGROUND/ZOlech/intake-probe` already exists, id 127802)
Write a one-line text file under `%USERPROFILE%\Downloads\bridge-probe.txt`, then run:

```bash
py -3.14 -c "import sys,os; sys.path.insert(0,'scripts'); import vault_bridge as b; f=b.add_file('$/DESIGNS/PLAYGROUND/ZOlech/intake-probe', os.path.expandvars(r'%USERPROFILE%\Downloads\bridge-probe.txt'), 'bridge-probe.txt', comment='bridge smoke test'); print(f); cats={c['name']:c['id'] for c in b.get_file_categories()['categories']}; print(b.update_file_categories([f['masterId']], cats['Documents'], comment='bridge smoke test'))"
```

Expected: a file id and master id, then one updated row. Confirm over REST (`vault_get_folder_contents` on folder 127802) that `bridge-probe.txt` is there with category Documents.

- [ ] **Step 5: First real intake.** With Zak: download the McMaster STEP + PDF for the DIN 6796 M8 conical washer, run the intake preview, Zak approves, run commit. Check the new part in Vault (number, folder, Part - Purchased, two attachments, properties). Report the result; do not claim success without the REST check.
