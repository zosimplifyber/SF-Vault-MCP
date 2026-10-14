using System.Text.Json;
using System.Text.Json.Nodes;
using SFVaultBridge.Core;

namespace SFVaultBridge.Core.Tests;

public class SettingsRulesTests
{
    [Theory]
    [InlineData("7", 0.07)]
    [InlineData("8.25", 0.0825)]
    [InlineData(" 7.5 % ", 0.075)]
    [InlineData("0", 0.0)]
    [InlineData("100", 1.0)]
    public void PercentParsesToFraction(string text, double expected)
    {
        Assert.True(SettingsRules.TryParsePercent(text, out var f, out var err), err);
        Assert.Equal(expected, f, 8);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("abc")]
    [InlineData("-0.1")]
    [InlineData("100.01")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void PercentRejectsBadInput(string text)
    {
        Assert.False(SettingsRules.TryParsePercent(text, out _, out var err));
        Assert.NotEmpty(err);
    }

    [Fact]
    public void PercentTextRoundTrips()
    {
        Assert.True(SettingsRules.TryParsePercent(SettingsRules.PercentText(0.0825), out var f, out _));
        Assert.Equal(0.0825, f, 8);
        Assert.True(SettingsRules.TryParsePercent(SettingsRules.PercentText(0.07), out var g, out _));
        Assert.Equal(0.07, g, 8);
    }

    [Fact]
    public void CleanListTrimsDropsBlanksAndDedupesIgnoringCase()
    {
        var clean = SettingsRules.CleanList(new string?[] { " CNC ", "cnc", "", "  ", "3D Print", null, "Cnc", "Other" });
        Assert.Equal(new[] { "CNC", "3D Print", "Other" }, clean);
    }

    [Fact]
    public void ListErrorNeedsOneValue()
    {
        Assert.NotNull(SettingsRules.ListError("Process values", new[] { "", "  " }));
        Assert.Null(SettingsRules.ListError("Process values", new[] { " ", "CNC" }));
    }

    [Fact]
    public void TryAddRejectsBlankAndDuplicates()
    {
        var list = new List<string> { "CNC" };
        Assert.False(SettingsRules.TryAdd(list, "  ", out _));
        Assert.False(SettingsRules.TryAdd(list, " cnc", out var err));
        Assert.Contains("already", err);
        Assert.True(SettingsRules.TryAdd(list, " PLA ", out _));
        Assert.Equal(new[] { "CNC", "PLA" }, list);
    }

    [Fact]
    public void MoveSwapsAndStopsAtEnds()
    {
        var list = new List<string> { "a", "b", "c" };
        Assert.Equal(0, SettingsRules.Move(list, 1, -1));
        Assert.Equal(new[] { "b", "a", "c" }, list);
        Assert.Equal(0, SettingsRules.Move(list, 0, -1));
        Assert.Equal(2, SettingsRules.Move(list, 2, 1));
        Assert.Equal(2, SettingsRules.Move(list, 1, 1));
        Assert.Equal(new[] { "b", "c", "a" }, list);
        Assert.Equal(-1, SettingsRules.Move(list, -1, 1));
    }

    [Fact]
    public void OverrideNamesKeepOnlyKnownTeamKeysOnce()
    {
        Assert.Equal(new[] { "processValues", "salesTaxRate" },
            SettingsRules.CleanOverrides(new string?[] { "salesTaxRate", " processValues ", "recentPartners", "processValues", "junk", null }));
        Assert.Empty(SettingsRules.CleanOverrides(null));
    }
}

public class SettingsSaveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sfvb-save-" + Guid.NewGuid().ToString("N"));
    public SettingsSaveTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
    private string P => Path.Combine(_dir, "settings.json");

    private static BridgeSettings Opened(string path) => BridgeSettings.Load(path, _ => { });

    [Fact]
    public void ChangedKeysListsOnlyWhatDiffers()
    {
        var a = BridgeSettings.Defaults;
        Assert.Empty(BridgeSettings.ChangedKeys(a, a with { RecentPartners = new[] { "Acme" } }));
        Assert.Equal(new[] { "dropboxAppKey", "salesTaxRate" },
            BridgeSettings.ChangedKeys(a, a with { DropboxAppKey = " k ", SalesTaxRate = 0.08 }));
        Assert.Equal(new[] { "processValues" }, BridgeSettings.ChangedKeys(a, a with { ProcessValues = a.ProcessValues.Reverse().ToList() }));
        Assert.Equal(new[] { "printMaterials" }, BridgeSettings.ChangedKeys(a, a with { PrintMaterials = new[] { "PLA" } }));
    }

    [Fact]
    public void SavePreservesUnknownKeysAndRecordsOnlyChangedOverrides()
    {
        File.WriteAllText(P, "{\"processValues\":[\"A\"],\"printMaterials\":[\"PLA\"],\"recentPartners\":[\"Acme\"],\"dropboxAppKey\":\"\",\"salesTaxRate\":0.07,\"custom\":{\"x\":1}}");
        var original = Opened(P);
        var edited = original with { SalesTaxRate = 0.0825, RecentPartners = Array.Empty<string>() };
        Assert.True(BridgeSettings.Save(P, original, edited, _ => { }));
        var root = JsonNode.Parse(File.ReadAllText(P))!.AsObject();
        Assert.Equal(1, (int)root["custom"]!["x"]!);
        Assert.Equal(0.0825, (double)root["salesTaxRate"]!);
        Assert.Empty(root["recentPartners"]!.AsArray());
        Assert.Equal(new[] { "salesTaxRate" }, root["userOverrides"]!.AsArray().Select(x => (string)x!).ToArray());
        var back = Opened(P);
        Assert.Equal(new[] { "salesTaxRate" }, back.Overrides);
        Assert.Equal(0.0825, back.SalesTaxRate);
    }

    [Fact]
    public void SaveAccumulatesOverridesAcrossSaves()
    {
        File.WriteAllText(P, "{\"processValues\":[\"A\"],\"userOverrides\":[\"processValues\"]}");
        var original = Opened(P);
        Assert.Equal(new[] { "processValues" }, original.Overrides);
        Assert.True(BridgeSettings.Save(P, original, original with { DropboxAppKey = "key" }, _ => { }));
        Assert.Equal(new[] { "processValues", "dropboxAppKey" }, Opened(P).Overrides);
    }

    [Fact]
    public void SaveWithNoChangesAddsNoOverrides()
    {
        var original = Opened(P); // creates the defaults file
        Assert.True(BridgeSettings.Save(P, original, original, _ => { }));
        Assert.Empty(Opened(P).Overrides);
        Assert.Equal(JsonValueKind.Array, JsonDocument.Parse(File.ReadAllText(P)).RootElement.GetProperty("userOverrides").ValueKind);
    }

    [Fact]
    public void SaveBacksUpAMalformedFileAndWritesFresh()
    {
        File.WriteAllText(P, "{ bad");
        var s = BridgeSettings.Defaults;
        Assert.True(BridgeSettings.Save(P, s, s with { SalesTaxRate = 0.1 }, _ => { }, new DateTime(2026, 10, 14, 1, 2, 3)));
        Assert.Equal("{ bad", File.ReadAllText(P + ".bad-20261014-010203"));
        Assert.Equal(0.1, Opened(P).SalesTaxRate);
    }

    [Fact]
    public void SaveCreatesTheFolderAndAllowsOneHundredPercent()
    {
        var path = Path.Combine(_dir, "new", "settings.json");
        var s = BridgeSettings.Defaults;
        Assert.True(BridgeSettings.Save(path, s, s with { SalesTaxRate = 1.0 }, _ => { }));
        Assert.Equal(1.0, Opened(path).SalesTaxRate);
    }
}

public class SettingsMergeOverrideTests
{
    private const string Team = "{\"processValues\":[\"CNC\"],\"printMaterials\":[\"PLA\"],\"dropboxAppKey\":\"teamkey\",\"salesTaxRate\":0.08,\"userOverrides\":[\"processValues\"]}";

    [Fact]
    public void OverriddenKeysKeepTheUserValueAndOthersStillFill()
    {
        var existing = "{\"processValues\":[\"Mine\"],\"printMaterials\":[\"X\"],\"dropboxAppKey\":\"\",\"salesTaxRate\":0.05,\"userOverrides\":[\"salesTaxRate\",\"processValues\"]}";
        var m = JsonNode.Parse(SettingsMerge.Merge(existing, Team))!.AsObject();
        Assert.Equal(new[] { "Mine" }, m["processValues"]!.AsArray().Select(x => (string)x!).ToArray());
        Assert.Equal(0.05, (double)m["salesTaxRate"]!);
        Assert.Equal(new[] { "PLA" }, m["printMaterials"]!.AsArray().Select(x => (string)x!).ToArray());
        Assert.Equal("teamkey", (string)m["dropboxAppKey"]!);
        Assert.Equal(new[] { "salesTaxRate", "processValues" }, m["userOverrides"]!.AsArray().Select(x => (string)x!).ToArray());
    }

    [Fact]
    public void OverriddenKeyMissingFromTheFileStaysMissing()
    {
        var m = JsonNode.Parse(SettingsMerge.Merge("{\"userOverrides\":[\"dropboxAppKey\"]}", Team))!.AsObject();
        Assert.Null(m["dropboxAppKey"]);
        Assert.Equal(0.08, (double)m["salesTaxRate"]!);
    }

    [Fact]
    public void TeamFileCannotSetUserOverrides()
    {
        var m = JsonNode.Parse(SettingsMerge.Merge("{}", Team))!.AsObject();
        Assert.Null(m["userOverrides"]);
        Assert.Equal(new[] { "CNC" }, m["processValues"]!.AsArray().Select(x => (string)x!).ToArray());
    }

    [Fact]
    public void BadOverridesValueIsIgnored()
    {
        var m = JsonNode.Parse(SettingsMerge.Merge("{\"userOverrides\":\"processValues\",\"processValues\":[\"Mine\"]}", Team))!.AsObject();
        Assert.Equal(new[] { "CNC" }, m["processValues"]!.AsArray().Select(x => (string)x!).ToArray());
    }

    [Fact]
    public void SavedOverridesSurviveAMergeFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sfvb-ovr-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "settings.json");
        try
        {
            var original = BridgeSettings.Load(path, _ => { });
            Assert.True(BridgeSettings.Save(path, original, original with { SalesTaxRate = 0.0825 }, _ => { }));
            Assert.Null(SettingsMerge.MergeFile(path, Team, DateTime.Now));
            var s = BridgeSettings.Load(path, _ => { });
            Assert.Equal(0.0825, s.SalesTaxRate);
            Assert.Equal("teamkey", s.DropboxAppKey);
            Assert.Equal(new[] { "CNC" }, s.ProcessValues);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
