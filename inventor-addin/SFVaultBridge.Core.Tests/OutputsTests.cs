using System.Text.Json.Nodes;
using SFVaultBridge.Core;

namespace SFVaultBridge.Core.Tests;

public class OutputsTests
{
    private static BomNode N(string pn, string structure = "Normal", string path = "") =>
        new(pn, "d", 1, "Each", structure, "", "", pn + ".ipt", path == "" ? @"C:\Vault Workspace\DESIGNS\" + pn + ".ipt" : path, Array.Empty<BomNode>());

    [Fact]
    public void Maps_local_file_to_vault_folder()
    {
        Assert.Equal("$/DESIGNS/PRODUCTION EQUIPMENT",
            VaultPaths.ToVaultFolder(@"C:\Vault Workspace\DESIGNS\PRODUCTION EQUIPMENT\CD-001887.iam", @"C:\Vault Workspace"));
        Assert.Equal("$", VaultPaths.ToVaultFolder(@"C:\Vault Workspace\a.iam", @"C:\Vault Workspace\"));
    }

    [Fact]
    public void Outside_workspace_asks_to_check_in()
    {
        var ex = Assert.Throws<BridgeException>(() => VaultPaths.ToVaultFolder(@"C:\Users\z\Desktop\a.iam", @"C:\Vault Workspace"));
        Assert.Equal("not_in_vault", ex.Code);
        Assert.Contains("Check the assembly in to Vault first", ex.Message);
        Assert.Throws<BridgeException>(() => VaultPaths.ToVaultFolder(@"C:\Vault WorkspaceX\a.iam", @"C:\Vault Workspace"));
    }

    [Fact]
    public void Output_names()
    {
        Assert.Equal("CD-001887 Purchasing.xlsx", VaultPaths.PurchasingName(@"C:\x\CD-001887.iam"));
        Assert.Equal("CD-001887 Design Review.pdf", VaultPaths.ReviewName(@"C:\x\CD-001887.iam"));
    }

    [Fact]
    public void Skip_rules()
    {
        Assert.True(ReviewRules.Skip(N("A", "Purchased"), null));
        Assert.True(ReviewRules.Skip(N("A"), "Part - Purchased"));
        Assert.True(ReviewRules.Skip(N("A"), "Part - Content Center"));
        Assert.True(ReviewRules.Skip(N("A", path: @"C:\Vault Workspace\Content Center Files\en-US\x.ipt"), null));
        Assert.False(ReviewRules.Skip(N("A"), "Part - Engineering"));
    }

    [Fact]
    public void Unique_components_keep_first_occurrence()
    {
        var a = N("A"); var b = N("B");
        var lines = new[] { new BomLine("1", a, 0), new BomLine("2", b, 0), new BomLine("2.1", a, 1) };
        Assert.Equal(new[] { "1", "2" }, ReviewRules.UniqueComponents(lines).Select(l => l.Item));
    }

    [Fact]
    public void Drawing_choice()
    {
        Assert.Equal(new[] { "CD-1.idw" }, ReviewRules.PickDrawings("CD-1.ipt", new[] { "CD-9.idw", "CD-1.dwg", "CD-1.idw" }));
        Assert.Equal(new[] { "CD-1.dwg" }, ReviewRules.PickDrawings("CD-1.ipt", new[] { "CD-1.dwg", "X.iam" }));
        Assert.Equal(new[] { "A.idw", "B.dwg" }, ReviewRules.PickDrawings("CD-1.ipt", new[] { "B.dwg", "A.idw", "A.IDW" }));
        Assert.Empty(ReviewRules.PickDrawings("CD-1.ipt", new[] { "CD-1.pdf" }));
        Assert.Equal("CD-1 - CD-1.idw", ReviewRules.Bookmark("CD-1", "CD-1.idw"));
    }

    [Fact]
    public void Manifest_json_matches_python_contract()
    {
        var json = ReviewManifest.ToJson("CD-001887", "desc", "zak", "Work in Progress", "5", new DateTime(2026, 10, 12, 17, 30, 0),
            new[] { new ReviewDrawing("CD-1", "CD-1.idw", @"C:\t\1.pdf") }, new[] { "CD-2" }, new[] { new ReviewFailure("CD-3.idw", "could not open") });
        var o = (JsonObject)JsonNode.Parse(json)!;
        Assert.Equal("2026-10-12 17:30", o["created"]!.GetValue<string>());
        Assert.Equal("CD-1.idw", o["drawings"]![0]!["drawing"]!.GetValue<string>());
        Assert.Equal(@"C:\t\1.pdf", o["drawings"]![0]!["pdf"]!.GetValue<string>());
        Assert.Equal("CD-1", o["drawings"]![0]!["part_number"]!.GetValue<string>());
        Assert.Equal("CD-2", o["missing"]![0]!.GetValue<string>());
        Assert.Equal("could not open", o["failed"]![0]!["reason"]!.GetValue<string>());
    }

    [Fact]
    public void Tool_result_parsing()
    {
        var ok = ToolResults.Parse("{\"ok\": true, \"workbook\": \"C:\\\\a.xlsx\", \"lines\": 12, \"unmatched\": [\"X\"]}", "workbook", "lines");
        Assert.True(ok.Ok); Assert.Equal(@"C:\a.xlsx", ok.Output); Assert.Equal(12, ok.Count); Assert.Equal(new[] { "X" }, ok.Unmatched);
        var bad = ToolResults.Parse("{\"ok\": false, \"error\": \"No part numbers\"}", "workbook", "lines");
        Assert.False(bad.Ok); Assert.Equal("No part numbers", bad.Error);
        Assert.False(ToolResults.Parse(null, "pdf", "pages").Ok);
        Assert.Contains("bridge.log", ToolResults.Parse("not json", "pdf", "pages").Error);
    }

    [Fact]
    public void Tool_arguments()
    {
        Assert.Equal(new[] { "purchasing", "--bom", "b", "--assembly", "A", "--out", "o", "--name", "n", "--result", "r" },
            ToolResults.PurchasingArgs("b", "A", "o", "n", "r"));
        Assert.Equal(new[] { "review", "--bom", "b", "--manifest", "m", "--out", "p", "--result", "r" },
            ToolResults.ReviewArgs("b", "m", "p", "r"));
    }
}
