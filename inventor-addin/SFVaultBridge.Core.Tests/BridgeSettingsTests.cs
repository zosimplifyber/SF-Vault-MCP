using System.Text.Json.Nodes;
using SFVaultBridge.Core;

namespace SFVaultBridge.Core.Tests;

public class BridgeSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sfvb-settings-" + Guid.NewGuid().ToString("N"));
    public BridgeSettingsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private string P => Path.Combine(_dir, "sub", "settings.json");

    [Fact]
    public void Defaults_values()
    {
        var d = BridgeSettings.Defaults;
        Assert.Equal(new[] { "CNC", "3D Print", "Sheet Metal", "Weldment", "In-house", "Other" }, d.ProcessValues);
        Assert.Equal(new[] { "PLA", "PETG", "ABS", "ASA", "PA11", "PA12", "Nylon", "TPU", "PC", "Resin", "CF" }, d.PrintMaterials);
        Assert.Empty(d.RecentPartners);
    }

    [Fact]
    public void Load_missing_writes_defaults()
    {
        var s = BridgeSettings.Load(P, _ => { });
        Assert.Equal(BridgeSettings.Defaults.PrintMaterials, s.PrintMaterials);
        var o = JsonNode.Parse(File.ReadAllText(P))!.AsObject();
        Assert.True(o.ContainsKey("processValues") && o.ContainsKey("printMaterials") && o.ContainsKey("recentPartners"));
    }

    [Fact]
    public void Load_malformed_uses_defaults_and_keeps_file()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(P)!);
        File.WriteAllText(P, "{ not json");
        var log = new List<string>();
        var s = BridgeSettings.Load(P, log.Add);
        Assert.Equal(BridgeSettings.Defaults.ProcessValues, s.ProcessValues);
        Assert.Equal("{ not json", File.ReadAllText(P));
        Assert.Contains("settings.json", Assert.Single(log));
    }

    [Fact]
    public void Load_partial_file_fills_missing_keys()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(P)!);
        File.WriteAllText(P, "{\"printMaterials\": [\"PLA\", 5, \" \", \"Wood \"]}");
        var s = BridgeSettings.Load(P, _ => { });
        Assert.Equal(new[] { "PLA", "Wood" }, s.PrintMaterials);
        Assert.Equal(BridgeSettings.Defaults.ProcessValues, s.ProcessValues);
    }

    [Fact]
    public void WithRecent_orders_dedupes_and_caps()
    {
        Assert.Equal(new[] { "A", "B", "C" }, BridgeSettings.WithRecent(new[] { "B", "a", "C" }, "A"));
        var ten = Enumerable.Range(1, 10).Select(i => "P" + i).ToArray();
        var r = BridgeSettings.WithRecent(ten, "New");
        Assert.Equal(10, r.Count);
        Assert.Equal("New", r[0]);
        var same = new[] { "X" };
        Assert.Same(same, BridgeSettings.WithRecent(same, "  "));
    }

    [Fact]
    public void RememberPartner_preserves_other_keys()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(P)!);
        File.WriteAllText(P, "{\"processValues\":[\"CNC\"],\"printMaterials\":[\"PLA\"],\"recentPartners\":[\"Old\"],\"extra\":1}");
        Assert.True(BridgeSettings.RememberPartner(P, "New", _ => { }));
        var o = JsonNode.Parse(File.ReadAllText(P))!.AsObject();
        Assert.Equal(new[] { "New", "Old" }, o["recentPartners"]!.AsArray().Select(x => (string?)x));
        Assert.Equal(new[] { "CNC" }, o["processValues"]!.AsArray().Select(x => (string?)x));
        Assert.Equal(1, (int)o["extra"]!);
    }

    [Fact]
    public void RememberPartner_missing_file_writes_defaults_with_partner()
    {
        Assert.True(BridgeSettings.RememberPartner(P, "Acme", _ => { }));
        Assert.Equal(new[] { "Acme" }, BridgeSettings.Load(P, _ => { }).RecentPartners);
    }

    [Fact]
    public void RememberPartner_malformed_returns_false_untouched()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(P)!);
        File.WriteAllText(P, "[1,2");
        var log = new List<string>();
        Assert.False(BridgeSettings.RememberPartner(P, "New", log.Add));
        Assert.Equal("[1,2", File.ReadAllText(P));
        Assert.Single(log);
    }
}
