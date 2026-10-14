using System.Text.Json.Nodes;
using SFVaultBridge.Core;
using Xunit;

namespace SFVaultBridge.Core.Tests;

public class AppVersionTests
{
    [Theory]
    [InlineData("1.0.0", true)]
    [InlineData("v2.10.3", true)]
    [InlineData("1.2.3+abc", true)]
    [InlineData("1.2", false)]
    [InlineData("a.b.c", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Parse(string? text, bool ok) => Assert.Equal(ok, SemVer.TryParse(text, out _));

    [Fact]
    public void CompareIsNumeric()
    {
        SemVer.TryParse("1.10.0", out var a); SemVer.TryParse("1.9.0", out var b); SemVer.TryParse("1.9.0", out var c);
        Assert.True(a > b);
        Assert.True(b < a);
        Assert.Equal(0, b.CompareTo(c));
        SemVer.TryParse("2.0.0", out var d);
        Assert.True(d > a);
    }

    [Fact]
    public void FileNameRoundTrips()
    {
        Assert.True(AppVersion.TryParseFileName("SF Vault Bridge 1.2.3.zip", out var v));
        Assert.Equal("1.2.3", v.ToString());
        Assert.Equal("SF Vault Bridge 1.2.3.zip", AppVersion.FileName(v));
    }

    [Theory]
    [InlineData("SF Vault Bridge 1.2.zip")]
    [InlineData("SF Vault Bridge 1.2.3.pdf")]
    [InlineData("Bridge 1.2.3.zip")]
    [InlineData("SF Vault Bridge 1.2.3 (1).zip")]
    [InlineData("SF Vault Bridge 1.2.3.zip.bak")]
    public void FileNameRejects(string name) => Assert.False(AppVersion.TryParseFileName(name, out _));

    [Fact]
    public void NewestPicksHighestAboveCurrentAndIgnoresJunk()
    {
        SemVer.TryParse("1.0.0", out var cur);
        var names = new[] { "SF Vault Bridge 1.0.0.zip", "SF Vault Bridge 1.9.0.zip", "SF Vault Bridge 1.10.0.zip", "notes.txt", "SF Vault Bridge 0.9.0.zip" };
        var best = AppVersion.Newest(names, cur);
        Assert.Equal("1.10.0", best!.Value.Version.ToString());
        Assert.Equal("SF Vault Bridge 1.10.0.zip", best.Value.Name);
    }

    [Fact]
    public void NewestIsNullWhenCurrent()
    {
        SemVer.TryParse("1.10.0", out var cur);
        Assert.Null(AppVersion.Newest(new[] { "SF Vault Bridge 1.10.0.zip", "SF Vault Bridge 1.2.0.zip" }, cur));
        Assert.Null(AppVersion.Newest(Array.Empty<string>(), cur));
    }

    [Fact]
    public void CurrentIsStampedFromVersionFile()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "VERSION"))) d = d.Parent;
        Assert.NotNull(d);
        Assert.Equal(File.ReadAllText(Path.Combine(d!.FullName, "VERSION")).Trim(), AppVersion.Current);
    }

    [Fact]
    public void StatusViewShowsVersionAndUpdate()
    {
        var view = StatusModel.Evaluate(true, new StatusInfo(true, "z", "v", "s"), null, "http://localhost:8790/", "1.0.0", "1.1.0");
        Assert.Contains("Version: 1.0.0", view.Details);
        Assert.Contains("Update available: 1.1.0", view.Details);
        var plain = StatusModel.Evaluate(true, null, null, "p");
        Assert.DoesNotContain("Update available", plain.Details);
    }
}

public class SettingsMergeTests
{
    private const string Team = "{\"processValues\":[\"CNC\",\"Other\"],\"printMaterials\":[\"PLA\"],\"dropboxAppKey\":\"key\",\"salesTaxRate\":0.08,\"recentPartners\":[\"Nope\"]}";

    [Fact]
    public void TeamKeysOverwriteAndPersonalKeysStay()
    {
        var existing = "{\"processValues\":[\"Old\"],\"printMaterials\":[\"X\"],\"recentPartners\":[\"Acme\",\"Beta\"],\"dropboxAppKey\":\"\",\"salesTaxRate\":0.07,\"custom\":1}";
        var merged = JsonNode.Parse(SettingsMerge.Merge(existing, Team))!.AsObject();
        Assert.Equal(new[] { "CNC", "Other" }, merged["processValues"]!.AsArray().Select(x => (string)x!).ToArray());
        Assert.Equal("key", (string)merged["dropboxAppKey"]!);
        Assert.Equal(0.08, (double)merged["salesTaxRate"]!);
        Assert.Equal(new[] { "Acme", "Beta" }, merged["recentPartners"]!.AsArray().Select(x => (string)x!).ToArray());
        Assert.Equal(1, (int)merged["custom"]!);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1]")]
    public void MissingOrBrokenExistingGetsTeamKeysOnly(string? existing)
    {
        var merged = JsonNode.Parse(SettingsMerge.Merge(existing, Team))!.AsObject();
        Assert.Equal("key", (string)merged["dropboxAppKey"]!);
        Assert.Null(merged["recentPartners"]);
    }

    [Fact]
    public void MergedFileLoadsThroughBridgeSettings()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sfvb-merge-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "settings.json");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(path, SettingsMerge.Merge("{\"recentPartners\":[\"Acme\"]}", Team));
            var s = BridgeSettings.Load(path, _ => { });
            Assert.Equal(new[] { "CNC", "Other" }, s.ProcessValues);
            Assert.Equal(new[] { "Acme" }, s.RecentPartners);
            Assert.Equal(0.08, s.SalesTaxRate);
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class ReleaseChannelOpsTests
{
    private sealed class FakeChannel : IReleaseChannel
    {
        public List<string> Names = new();
        public List<(string Zip, string Name, string Comment)> Published = new();
        public IReadOnlyList<string> ListNames() => Names;
        public string Publish(string localZip, string fileName, string comment) { Published.Add((localZip, fileName, comment)); return "$/Tools/SF Vault Bridge/" + fileName; }
    }

    private static (string Dir, string Zip) Zip(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sfvb-rel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var zip = Path.Combine(dir, name);
        File.WriteAllText(zip, "x");
        return (dir, zip);
    }

    [Fact]
    public void PublishesUnderNameFromVersion()
    {
        var (dir, zip) = Zip("anything.zip");
        try
        {
            var ch = new FakeChannel();
            var r = ReleaseChannelOps.Publish(new JsonObject { ["zipPath"] = zip, ["version"] = "1.2.3" }, ch, new PathGuard(new[] { dir }));
            Assert.Equal("SF Vault Bridge 1.2.3.zip", (string)r["name"]!);
            Assert.Equal("$/Tools/SF Vault Bridge/SF Vault Bridge 1.2.3.zip", (string)r["vaultPath"]!);
            Assert.Single(ch.Published);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void VersionComesFromZipNameWhenNotGiven()
    {
        var (dir, zip) = Zip("SF Vault Bridge 2.0.1.zip");
        try
        {
            var r = ReleaseChannelOps.Publish(new JsonObject { ["zipPath"] = zip }, new FakeChannel(), new PathGuard(new[] { dir }));
            Assert.Equal("2.0.1", (string)r["version"]!);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void RefusesExistingSameVersion()
    {
        var (dir, zip) = Zip("SF Vault Bridge 1.0.0.zip");
        try
        {
            var ch = new FakeChannel { Names = { "sf vault bridge 1.0.0.zip" } };
            var ex = Assert.Throws<BridgeException>(() => ReleaseChannelOps.Publish(new JsonObject { ["zipPath"] = zip }, ch, new PathGuard(new[] { dir })));
            Assert.Equal("exists", ex.Code);
            Assert.Empty(ch.Published);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void RejectsBadVersionAndOutsidePath()
    {
        var (dir, zip) = Zip("x.zip");
        try
        {
            var guard = new PathGuard(new[] { dir });
            Assert.Equal("bad_request", Assert.Throws<BridgeException>(() => ReleaseChannelOps.Publish(new JsonObject { ["zipPath"] = zip, ["version"] = "1.2" }, new FakeChannel(), guard)).Code);
            Assert.Equal("bad_request", Assert.Throws<BridgeException>(() => ReleaseChannelOps.Publish(new JsonObject { ["zipPath"] = zip }, new FakeChannel(), guard)).Code);
            Assert.Equal("path_not_allowed", Assert.Throws<BridgeException>(() => ReleaseChannelOps.Publish(new JsonObject { ["zipPath"] = @"C:\Windows\x.zip", ["version"] = "1.0.0" }, new FakeChannel(), guard)).Code);
        }
        finally { Directory.Delete(dir, true); }
    }
}
