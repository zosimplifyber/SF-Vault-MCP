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
