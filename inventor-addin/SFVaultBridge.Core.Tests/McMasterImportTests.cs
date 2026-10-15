using System.Text.Json.Nodes;
using SFVaultBridge.Core;
using SFVaultBridge.Core.McMaster;

namespace SFVaultBridge.Core.Tests;

public class McMasterImportTests
{
    // A typical Inventor 2026 active material library (subset).
    private static readonly string[] Library =
    {
        "ABS Plastic", "Aluminum 6061", "Aluminum-6061", "Brass, Soft Yellow", "Copper", "Nylon-6/6", "Rubber",
        "Stainless Steel", "Stainless Steel, 440C", "Stainless Steel, Austenitic", "Steel", "Steel, Alloy", "Steel, Mild",
    };

    [Theory]
    [InlineData(" 91251a054 ", "91251A054")]
    [InlineData("6100K123", "6100K123")]
    [InlineData("8975K421-48", "8975K421-48")]
    [InlineData("91251 A054", "91251A054")]
    [InlineData("", null)]
    [InlineData("ABC", null)]
    [InlineData("91251A054;rm", null)]
    [InlineData("../x1", null)]
    public void Part_number_normalizing(string input, string? expected) => Assert.Equal(expected, McMasterImport.NormalizePartNumber(input));

    [Theory]
    [InlineData("Black-Oxide Alloy Steel", "Steel", "Black-Oxide")]
    [InlineData("Zinc-Plated Steel", "Steel", "Zinc-Plated")]
    [InlineData("Steel", "Steel", "As Finished")]
    [InlineData("18-8 Stainless Steel", "Stainless Steel", "18-8")]
    [InlineData("Passivated 18-8 Stainless Steel", "Stainless Steel", "Passivated 18-8")]
    [InlineData("316 Stainless Steel", "Stainless Steel", "As Finished")]
    [InlineData("6061 Aluminum", "Aluminum 6061", "As Finished")]
    [InlineData("Black-Anodized 6061 Aluminum", "Aluminum 6061", "Black-Anodized")]
    [InlineData("7075 Aluminum", "Aluminum 6061", "As Finished")]
    [InlineData("Brass", "Brass, Soft Yellow", "As Finished")]
    [InlineData("Nylon", "Nylon-6/6", "As Finished")]
    public void Material_matches_the_library_and_finish_words_move_out(string spec, string material, string finish)
    {
        var m = McMasterImport.MatchMaterial(new[] { spec }, Library);
        Assert.Equal(material, m.Material);
        Assert.Equal(finish, m.Finish);
        Assert.Null(m.Hint);
    }

    [Fact]
    public void Stainless_grade_prefers_a_library_entry_with_that_grade()
    {
        var lib = Library.Append("Stainless Steel 316").ToArray();
        Assert.Equal("Stainless Steel 316", McMasterImport.MatchMaterial(new[] { "316 Stainless Steel" }, lib).Material);
        Assert.Equal("Stainless Steel, Austenitic", McMasterImport.MatchMaterial(new[] { "18-8 Stainless Steel" }, new[] { "Stainless Steel, Austenitic", "Steel" }).Material);
    }

    [Fact]
    public void Steel_without_an_exact_steel_uses_the_first_steel_name()
    {
        Assert.Equal("Steel, Mild", McMasterImport.MatchMaterial(new[] { "Alloy Steel" }, new[] { "Aluminum 6061", "Steel, Mild", "Steel, Alloy" }).Material);
        Assert.Equal("Aluminum-6061", McMasterImport.MatchMaterial(new[] { "Aluminum" }, new[] { "Aluminum-6061" }).Material);
    }

    [Fact]
    public void No_match_leaves_material_for_the_user_with_a_hint()
    {
        var m = McMasterImport.MatchMaterial(new[] { "PTFE" }, Library);
        Assert.Null(m.Material);
        Assert.Contains("PTFE", m.Hint);
        var none = McMasterImport.MatchMaterial(null, Library);
        Assert.Null(none.Material);
        Assert.Equal("As Finished", none.Finish);
        Assert.NotNull(none.Hint);
        Assert.Null(McMasterImport.MatchMaterial(new[] { "Black-Oxide" }, Library).Material);
    }

    [Fact]
    public void Pack_quantity_unit_and_piece_price()
    {
        var specs = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase) { ["Quantity per Package"] = new[] { "1,000" } };
        Assert.Equal(1000, McMasterImport.QuantityPerPackage(specs));
        Assert.Null(McMasterImport.QuantityPerPackage(new Dictionary<string, IReadOnlyList<string>>()));
        Assert.Null(McMasterImport.QuantityPerPackage(new Dictionary<string, IReadOnlyList<string>> { ["Quantity per Package"] = new[] { "0" } }));
        Assert.True(McMasterImport.IsPackUnit("Packs"));
        Assert.True(McMasterImport.IsPackUnit("pack"));
        Assert.False(McMasterImport.IsPackUnit("Each"));
        Assert.Equal(0.0936, McMasterImport.PiecePrice(4.68, "Packs", 50));
        Assert.Equal(12.5, McMasterImport.PiecePrice(12.5, "Each", 50));
        Assert.Null(McMasterImport.PiecePrice(4.68, "Packs", null));
        Assert.Equal(50, McMasterImport.PackSize("Packs", 50));
        Assert.Equal(1, McMasterImport.PackSize("Each", 50));
    }

    [Fact]
    public void Prefill_from_the_91251A054_example()
    {
        var product = McMasterShapes.Product(FakeMcMasterServer.Product());
        var price = McMasterShapes.Price(FakeMcMasterServer.Price());
        var f = McMasterImport.Prefill(product, price, Library);
        Assert.Equal("91251A054", f.VendorNumber);
        Assert.Equal("Black-Oxide Alloy Steel Socket Head Screw", f.Description);
        Assert.Equal("US Origin, 0-80 Thread Size, 3/16\" Long", f.Comments);
        Assert.Equal("Steel", f.Material.Material);
        Assert.Equal("Black-Oxide", f.Material.Finish);
        Assert.Equal(0.0936, f.EstimatedCost);
        Assert.Equal(50, f.PackSize);
        Assert.Null(f.CostHint);
        Assert.Equal("Black-Oxide Alloy Steel Socket Head Screw | US Origin, 0-80 Thread Size, 3/16\" Long | $4.68 per pack of 50", f.Summary);
    }

    [Fact]
    public void Prefill_without_price_or_pack_size_leaves_cost_blank()
    {
        var product = McMasterShapes.Product(FakeMcMasterServer.Product(qtyPerPack: null));
        var noPack = McMasterImport.Prefill(product, McMasterShapes.Price(FakeMcMasterServer.Price()), Library);
        Assert.Null(noPack.EstimatedCost);
        Assert.Null(noPack.PackSize);
        Assert.NotNull(noPack.CostHint);
        var each = McMasterImport.Prefill(product, McMasterShapes.Price(FakeMcMasterServer.Price(unit: 7.25, uom: "Each")), Library);
        Assert.Equal((7.25, 1), (each.EstimatedCost!.Value, each.PackSize!.Value));
        Assert.Contains("$7.25 (Each)", each.Summary);
        var feet = McMasterImport.Prefill(product, McMasterShapes.Price(FakeMcMasterServer.Price(unit: 12.0, uom: "Feet")), Library);
        Assert.Null(feet.EstimatedCost);
        Assert.Null(feet.PackSize);
        Assert.Contains("by the Feet", feet.CostHint);
        Assert.True(McMasterImport.IsEachUnit("Each"));
        Assert.True(McMasterImport.IsEachUnit(""));
        Assert.False(McMasterImport.IsEachUnit("Rolls"));
        var none = McMasterImport.Prefill(product, null, Library);
        Assert.Null(none.EstimatedCost);
        Assert.Contains("No McMaster price", none.CostHint);
    }

    [Fact]
    public void File_names_and_folder()
    {
        Assert.Equal("91251A054_Black-Oxide Alloy Steel Socket Head Screw", McMasterImport.FileStem("91251A054", "Black-Oxide Alloy Steel Socket Head Screw"));
        Assert.Equal("1234K5_Hose 1-2- - 3-4- Fitting", McMasterImport.FileStem("1234K5", "Hose 1/2\" - 3/4\" Fitting"));
        Assert.Equal("1234K5", McMasterImport.FileStem("1234K5", ""));
        Assert.True(McMasterImport.FileStem("1234K5", new string('x', 300)).Length <= 87);
        Assert.Equal(Path.Combine(@"C:\Users\z", "Downloads", "SF Vault Bridge", "McMaster", "91251A054"), McMasterImport.DownloadFolder(@"C:\Users\z", "91251A054"));
        // The fetched names read back through the file-name prefill the same way a McMaster download does.
        var prefill = VendorFileNames.FromFiles("91251A054_Black-Oxide Alloy Steel Socket Head Screw.step", null);
        Assert.Equal(("McMaster-Carr", "91251A054"), (prefill.Vendor, prefill.VendorNumber));
    }

    [Fact]
    public void Formats_prefer_exact_names()
    {
        var f = new[] { "2-D DWG", "2-D PDF", "3-D STEP", "3-D IGES" };
        Assert.Equal("3-D STEP", McMasterImport.PickFormat(f, "3-D STEP", "STEP"));
        Assert.Equal("2-D PDF", McMasterImport.PickFormat(f, "2-D PDF", "PDF"));
        Assert.Equal("3D STEP AP214", McMasterImport.PickFormat(new[] { "3D STEP AP214" }, "3-D STEP", "STEP"));
        Assert.Equal("2-D PDF (inch)", McMasterImport.PickFormat(new[] { "3-D PDF", "2-D PDF (inch)" }, "2-D PDF", "PDF"));
        Assert.Null(McMasterImport.PickFormat(new[] { "2-D DWG" }, "2-D PDF", "PDF"));
    }
}

public class McMasterPricingTests
{
    private static BomLine Line(string pn, string vendor, string vendorNumber, string path = @"C:\VaultWS\DESIGNS\x.ipt", int? pack = null, double qty = 2) =>
        new("1", new BomNode(pn, "d", qty, "Each", "Purchased", "", "", pn + ".ipt", path, Array.Empty<BomNode>(), vendor, vendorNumber, PackSize: pack), 0);

    private sealed class FakeTools : IMcMasterTools
    {
        public readonly List<string> Calls = new();
        public Func<IReadOnlyList<(string, int)>, IReadOnlyList<McMasterBomLine>>? Enrich;
        public Func<string, McMasterPrice>? Price;
        public Func<string, McMasterProduct>? Product;

        public Task<IReadOnlyList<McMasterBomLine>> EnrichBomAsync(IReadOnlyList<(string PartNumber, int Qty)> lines, CancellationToken ct)
        {
            Calls.Add("enrich_bom:" + lines.Count);
            return Task.FromResult(Enrich!(lines));
        }

        public Task<McMasterPrice> GetPriceAsync(string partNumber, int qty, CancellationToken ct) { Calls.Add("get_price:" + partNumber); return Task.FromResult(Price!(partNumber)); }

        public Task<McMasterProduct> GetProductAsync(string partNumber, CancellationToken ct) { Calls.Add("get_product:" + partNumber); return Task.FromResult(Product!(partNumber)); }

        public Task<McMasterFile> GetCadAsync(string partNumber, string cadFormat, CancellationToken ct) => throw new NotSupportedException();
        public Task DownloadAsync(string url, string path, CancellationToken ct) => throw new NotSupportedException();
    }

    private static McMasterProduct Product(string pn, string? qty = "50") => McMasterShapes.Product(FakeMcMasterServer.Product(pn, qty));

    [Fact]
    public void Parts_follow_the_mcmaster_rule_unique_with_known_pack_sizes()
    {
        var lines = new[]
        {
            Line("CD-1", "McMASTER-CARR", "91251a054"),
            Line("CD-2", "McMaster Carr", "91251A054", pack: 50),
            Line("CD-3", "", "6100K123", path: @"C:\VaultWS\DESIGNS\Simplifyber Library\McMaster-Carr\x.ipt"),
            Line("CD-4", "Acme", "A-1"),
            Line("CD-5", "McMaster", ""),
            Line("CD-6", "mcmaster-carr", "bad;pn"),
        };
        var (parts, noNumber) = McMasterPricing.PartsToPrice(lines);
        Assert.Equal(new[] { new McMasterPart("91251A054", 50), new McMasterPart("6100K123", null) }, parts);
        Assert.Equal(new[] { "CD-5", "CD-6" }, noNumber);
    }

    [Fact]
    public async Task One_batch_prices_everything_and_the_live_pack_size_wins_over_the_iproperty()
    {
        var log = new List<string>();
        var tools = new FakeTools
        {
            Enrich = l => l.Select((x, i) => new McMasterBomLine(i + 1, x.Item1, x.Item1 == "6100K123" ? 12.0 : 4.68, x.Item1 == "6100K123" ? "Each" : "Packs", null)).ToList(),
            Product = pn => Product(pn, "100"),
        };
        var parts = new[] { new McMasterPart("91251A054", 50), new McMasterPart("6100K123", null), new McMasterPart("92196A194", null) };
        var run = await McMasterPricing.FetchAsync(tools, parts, log.Add, CancellationToken.None);
        Assert.Equal(new[] { "enrich_bom:3", "get_product:91251A054", "get_product:92196A194" }, tools.Calls);
        Assert.Equal(new McMasterLivePrice("91251A054", 4.68, 100, "Packs"), run.Live["91251A054"]);
        Assert.Contains(log, l => l.Contains("pack size now 100"));
        Assert.Equal(1, run.Live["6100K123"].PackSize);
        Assert.Equal(100, run.Live["92196A194"].PackSize);
        Assert.Empty(run.FellBack);
        Assert.Empty(run.Problems);
    }

    [Fact]
    public async Task Pack_size_iproperty_is_the_fallback_when_the_product_lookup_fails()
    {
        var tools = new FakeTools
        {
            Enrich = _ => new[] { new McMasterBomLine(1, "A1", 4.68, "Packs", null), new McMasterBomLine(2, "B2", 4.68, "Packs", null) },
            Product = _ => throw new McMasterToolException("budget used up"),
        };
        var run = await McMasterPricing.FetchAsync(tools, new[] { new McMasterPart("A1", 50), new McMasterPart("B2", null) }, _ => { }, CancellationToken.None);
        Assert.Equal(50, run.Live["A1"].PackSize);
        Assert.Equal(new[] { "B2" }, run.FellBack);
    }

    [Fact]
    public async Task Units_that_are_neither_each_nor_pack_fall_back()
    {
        var log = new List<string>();
        var tools = new FakeTools { Enrich = _ => new[] { new McMasterBomLine(1, "A1", 12.0, "Feet", null), new McMasterBomLine(2, "B2", 3.0, "Each", null) } };
        var run = await McMasterPricing.FetchAsync(tools, new[] { new McMasterPart("A1", null), new McMasterPart("B2", null) }, log.Add, CancellationToken.None);
        Assert.Equal(new[] { "B2" }, run.Live.Keys);
        Assert.Equal(new[] { "A1" }, run.FellBack);
        Assert.Contains(log, l => l.Contains("Feet"));
    }

    [Fact]
    public async Task Batch_line_errors_and_missing_pack_quantity_fall_back()
    {
        var log = new List<string>();
        var tools = new FakeTools
        {
            Enrich = l => new[] { new McMasterBomLine(1, "A1", 4.0, "Packs", null), new McMasterBomLine(2, "B2", null, "", "discontinued") },
            Product = pn => Product(pn, null),
        };
        var run = await McMasterPricing.FetchAsync(tools, new[] { new McMasterPart("A1", null), new McMasterPart("B2", null) }, log.Add, CancellationToken.None);
        Assert.Empty(run.Live);
        Assert.Equal(new[] { "A1", "B2" }, run.FellBack);
        Assert.Contains(log, l => l.Contains("discontinued"));
        Assert.Contains(log, l => l.Contains("Quantity per Package"));
    }

    [Fact]
    public async Task Budget_refusal_of_the_batch_prices_part_by_part()
    {
        var tools = new FakeTools
        {
            Enrich = _ => throw new McMasterToolException("This request needs 2 new McMaster subscriptions but only 0 are left today (daily budget 50)."),
            Price = pn => pn == "A1" ? new McMasterPrice(pn, 9.0, "Each") : throw new McMasterToolException("McMaster daily budget used up"),
        };
        var log = new List<string>();
        var run = await McMasterPricing.FetchAsync(tools, new[] { new McMasterPart("A1", null), new McMasterPart("B2", null) }, log.Add, CancellationToken.None);
        Assert.Equal(new[] { "enrich_bom:2", "get_price:A1", "get_price:B2" }, tools.Calls);
        Assert.Equal(9.0, run.Live["A1"].PackPrice);
        Assert.Equal(new[] { "B2" }, run.FellBack);
        Assert.Contains(log, l => l.Contains("part by part"));
    }

    [Fact]
    public async Task Not_signed_in_or_cancelled_falls_back_for_everything()
    {
        foreach (var e in new Exception[] { new McMasterNotSignedIn(), new McMasterSignInCancelled("McMaster sign-in was cancelled."), new HttpRequestException("no route"), new McMasterUnauthorized() })
        {
            var tools = new FakeTools { Enrich = _ => throw e };
            var run = await McMasterPricing.FetchAsync(tools, new[] { new McMasterPart("A1", null), new McMasterPart("B2", null) }, _ => { }, CancellationToken.None);
            Assert.Empty(run.Live);
            Assert.Equal(new[] { "A1", "B2" }, run.FellBack);
            Assert.Single(run.Problems);
        }
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelled = await McMasterPricing.FetchAsync(new FakeTools { Enrich = _ => throw new OperationCanceledException(cts.Token) }, new[] { new McMasterPart("A1", null) }, _ => { }, cts.Token);
        Assert.Equal(new[] { "A1" }, cancelled.FellBack);
        Assert.Contains("cancelled", cancelled.Problems[0]);
    }

    [Fact]
    public async Task Stop_during_pack_sizes_keeps_only_complete_prices()
    {
        var tools = new FakeTools
        {
            Enrich = _ => new[] { new McMasterBomLine(1, "A1", 9.0, "Each", null), new McMasterBomLine(2, "B2", 4.0, "Packs", null) },
            Product = _ => throw new McMasterSignInCancelled("cancelled"),
        };
        var run = await McMasterPricing.FetchAsync(tools, new[] { new McMasterPart("A1", null), new McMasterPart("B2", null) }, _ => { }, CancellationToken.None);
        Assert.Equal(new[] { "A1" }, run.Live.Keys);
        Assert.Equal(new[] { "B2" }, run.FellBack);
    }

    [Fact]
    public void Apply_puts_prices_on_mcmaster_rows_only()
    {
        var lines = new[] { Line("CD-1", "McMASTER-CARR", "91251a054"), Line("CD-2", "Acme", "91251A054"), Line("CD-3", "McMaster", "6100K123") };
        var live = new Dictionary<string, McMasterLivePrice>(StringComparer.OrdinalIgnoreCase) { ["91251A054"] = new("91251A054", 4.68, 50, "Packs") };
        var applied = McMasterPricing.Apply(lines, live, new DateTime(2026, 10, 14));
        Assert.Equal((4.68, 50, "2026-10-14"), (applied[0].Node.McMasterPackPrice!.Value, applied[0].Node.PackSize!.Value, applied[0].Node.McMasterPriceDate));
        Assert.Null(applied[1].Node.McMasterPackPrice);
        Assert.Null(applied[2].Node.McMasterPackPrice);
    }

    [Fact]
    public void Note_counts_live_and_fallback()
    {
        var d = new DateTime(2026, 10, 14);
        Assert.Equal("", McMasterPricing.Note(0, 0, d));
        Assert.Equal("McMaster: 1 live price (2026-10-14)", McMasterPricing.Note(1, 0, d));
        Assert.Equal("McMaster: 3 live prices (2026-10-14), 1 fell back to list or file price", McMasterPricing.Note(3, 1, d));
        Assert.Equal("McMaster: 0 live prices, 2 fell back to list or file prices", McMasterPricing.Note(0, 2, d));
    }

    [Fact]
    public void Bom_file_carries_the_live_columns()
    {
        var path = Path.Combine(Path.GetTempPath(), "sfvb-bom-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var lines = McMasterPricing.Apply(new[] { Line("CD-1", "McMASTER-CARR", "91251A054", pack: 50), Line("CD-2", "Acme", "A-1", pack: 10) },
                new Dictionary<string, McMasterLivePrice> { ["91251A054"] = new("91251A054", 4.68, 50, "Packs") }, new DateTime(2026, 10, 14));
            BomFile.Write(path, lines);
            var rows = File.ReadAllLines(path).Select(l => l.Split('\t')).ToList();
            var h = rows[0].ToList();
            Assert.Equal(new[] { "McMaster Pack Price", "Pack Size", "McMaster Price Date" }, h.Skip(15).Take(3).ToArray());
            Assert.Equal(new[] { "4.68", "50", "2026-10-14" }, rows[1].Skip(15).Take(3).ToArray());
            Assert.Equal(new[] { "", "10", "" }, rows[2].Skip(15).Take(3).ToArray());   // the file's pack size is written; price and date only with a live price
        }
        finally { File.Delete(path); }
    }
}

public class McMasterSettingsTests
{
    private static string Temp() => Path.Combine(Path.GetTempPath(), "sfvb-set-" + Guid.NewGuid().ToString("N"), "settings.json");

    [Fact]
    public void Server_url_defaults_loads_saves_and_counts_as_changed()
    {
        var p = Temp();
        try
        {
            Assert.Equal(BridgeSettings.DefaultMcMasterServerUrl, BridgeSettings.Load(p, _ => { }).McMasterServerUrl);
            Assert.Equal("https://mcmaster.simplifyber-app.com/mcp", JsonNode.Parse(File.ReadAllText(p))!["mcmasterServerUrl"]!.GetValue<string>());
            var original = BridgeSettings.Load(p, _ => { });
            var edited = original with { McMasterServerUrl = "https://mc2.example.test/mcp" };
            Assert.Equal(new[] { "mcmasterServerUrl" }, BridgeSettings.ChangedKeys(original, edited));
            Assert.True(BridgeSettings.Save(p, original, edited, _ => { }));
            var back = BridgeSettings.Load(p, _ => { });
            Assert.Equal("https://mc2.example.test/mcp", back.McMasterServerUrl);
            Assert.Contains("mcmasterServerUrl", back.Overrides);
        }
        finally { Directory.Delete(Path.GetDirectoryName(p)!, true); }
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"http://mcmaster.example.test/mcp\"")]
    [InlineData("\"not a url\"")]
    [InlineData("42")]
    public void Junk_server_url_loads_as_the_default(string value)
    {
        var p = Temp();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, "{\"mcmasterServerUrl\": " + value + "}");
            Assert.Equal(BridgeSettings.DefaultMcMasterServerUrl, BridgeSettings.Load(p, _ => { }).McMasterServerUrl);
        }
        finally { Directory.Delete(Path.GetDirectoryName(p)!, true); }
    }

    [Fact]
    public void Url_rules_for_the_settings_box()
    {
        Assert.True(SettingsRules.TryParseServerUrl(" https://mcmaster.simplifyber-app.com/mcp ", out var u, out _));
        Assert.Equal("https://mcmaster.simplifyber-app.com/mcp", u);
        Assert.True(SettingsRules.TryParseServerUrl("http://127.0.0.1:8080/mcp", out _, out _));
        foreach (var bad in new[] { "", "http://mcmaster.example.test/mcp", "https://x.test/mcp?a=1", "https://u:p@x.test/mcp", "mcmaster" })
        {
            Assert.False(SettingsRules.TryParseServerUrl(bad, out _, out var error));
            Assert.NotEmpty(error);
        }
    }

    [Fact]
    public void Team_settings_set_the_url_unless_the_user_changed_it()
    {
        Assert.Contains("mcmasterServerUrl", SettingsMerge.TeamKeys);
        var team = "{\"mcmasterServerUrl\":\"https://team.example.test/mcp\"}";
        Assert.Equal("https://team.example.test/mcp", (string)JsonNode.Parse(SettingsMerge.Merge("{\"mcmasterServerUrl\":\"https://old.example.test/mcp\"}", team))!["mcmasterServerUrl"]!);
        var mine = "{\"mcmasterServerUrl\":\"https://mine.example.test/mcp\",\"userOverrides\":[\"mcmasterServerUrl\"]}";
        Assert.Equal("https://mine.example.test/mcp", (string)JsonNode.Parse(SettingsMerge.Merge(mine, team))!["mcmasterServerUrl"]!);
    }

    [Fact]
    public void Shipped_team_settings_and_installer_carry_the_url()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "SFVaultBridge.sln"))) root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        var team = JsonNode.Parse(File.ReadAllText(Path.Combine(root!, "manifest", "team-settings.json")))!;
        Assert.Equal(BridgeSettings.DefaultMcMasterServerUrl, (string)team["mcmasterServerUrl"]!);
        Assert.Contains("'mcmasterServerUrl'", File.ReadAllText(Path.Combine(root!, "Install-Package.ps1")));
    }
}
