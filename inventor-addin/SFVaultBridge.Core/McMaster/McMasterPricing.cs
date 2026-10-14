using System.Globalization;

namespace SFVaultBridge.Core.McMaster;

/// <summary>A McMaster part number to price, and the pack size its Pack Size iProperty already gives (null when unknown).</summary>
public sealed record McMasterPart(string PartNumber, int? KnownPackSize);

/// <summary>A live price: what one purchasing unit costs (a pack, or one piece) and how many pieces it holds.</summary>
public sealed record McMasterLivePrice(string PartNumber, double PackPrice, int PackSize, string Unit);

/// <summary>The outcome of one Purchasing Sheet price run. FellBack holds part numbers (and BOM rows with no usable number) priced from the list or file instead.</summary>
public sealed record McMasterPriceRun(IReadOnlyDictionary<string, McMasterLivePrice> Live, IReadOnlyList<string> FellBack, IReadOnlyList<string> Problems)
{
    public static McMasterPriceRun AllFellBack(IEnumerable<string> parts, string problem) =>
        new(new Dictionary<string, McMasterLivePrice>(StringComparer.OrdinalIgnoreCase), parts.ToList(), new[] { problem });
}

/// <summary>
/// Live McMaster-Carr prices for the Purchasing Sheet. McMaster rows follow McMasterRule (vendor spelling or the Library McMaster
/// folder). One enrich_bom call prices every unique part (500 per call); when the server refuses the batch (daily budget, or any
/// tool error) each part is priced on its own with get_price, so parts already subscribed still price. A pack unit needs a pack size:
/// the part's Pack Size iProperty, else get_product's Quantity per Package. Anything that fails falls back to the list or file price.
/// </summary>
public static class McMasterPricing
{
    public const int BatchSize = 500;

    /// <summary>The unique McMaster part numbers in the BOM, and the BOM rows that are McMaster but carry no usable Vendor Number.</summary>
    public static (IReadOnlyList<McMasterPart> Parts, IReadOnlyList<string> NoNumber) PartsToPrice(IEnumerable<BomLine> lines)
    {
        var parts = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        var noNumber = new List<string>();
        foreach (var l in lines)
        {
            var n = l.Node;
            if (!McMasterRule.IsMcMasterCad(n)) continue;
            var pn = McMasterImport.NormalizePartNumber(n.VendorNumber);
            if (pn is null)
            {
                if (!noNumber.Contains(n.PartNumber, StringComparer.OrdinalIgnoreCase)) noNumber.Add(n.PartNumber);
                continue;
            }
            if (!parts.ContainsKey(pn)) { parts[pn] = null; order.Add(pn); }
            if (parts[pn] is null && n.PackSize is > 0) parts[pn] = n.PackSize;
        }
        return (order.Select(p => new McMasterPart(p, parts[p])).ToList(), noNumber);
    }

    /// <summary>Never throws for server, sign-in or network trouble: what could not be priced is in FellBack, and why in Problems.</summary>
    public static async Task<McMasterPriceRun> FetchAsync(IMcMasterTools tools, IReadOnlyList<McMasterPart> parts, Action<string> log, CancellationToken ct)
    {
        var prices = new Dictionary<string, (double Price, string Unit)>(StringComparer.OrdinalIgnoreCase);
        var sizes = parts.ToDictionary(p => p.PartNumber, p => p.KnownPackSize, StringComparer.OrdinalIgnoreCase);
        var reasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();
        try
        {
            try
            {
                foreach (var chunk in parts.Chunk(BatchSize))
                {
                    var lines = await tools.EnrichBomAsync(chunk.Select(p => (p.PartNumber, 1)).ToList(), ct).ConfigureAwait(false);
                    foreach (var line in lines)
                    {
                        var part = line.Line >= 1 && line.Line <= chunk.Length ? chunk[line.Line - 1].PartNumber
                                 : chunk.FirstOrDefault(p => string.Equals(p.PartNumber, line.PartNumber, StringComparison.OrdinalIgnoreCase))?.PartNumber;
                        if (part is null) continue;
                        if (line.UnitPrice is > 0) prices[part] = (line.UnitPrice.Value, line.UnitOfMeasure);
                        else reasons[part] = line.Error ?? "no price returned";
                    }
                }
            }
            catch (McMasterToolException e)
            {
                log("McMaster enrich_bom refused (" + e.Message + "); pricing part by part.");
                foreach (var p in parts.Where(p => !prices.ContainsKey(p.PartNumber)))
                {
                    try
                    {
                        var price = await tools.GetPriceAsync(p.PartNumber, 1, ct).ConfigureAwait(false);
                        if (price.UnitPrice > 0) { prices[p.PartNumber] = (price.UnitPrice, price.UnitOfMeasure); reasons.Remove(p.PartNumber); }
                        else reasons[p.PartNumber] = "no price returned";
                    }
                    catch (McMasterToolException pe) { reasons[p.PartNumber] = pe.Message; }
                }
            }
            foreach (var (pn, (_, unit)) in prices.ToList())
            {
                if (!McMasterImport.IsPackUnit(unit) || sizes[pn] is > 0) continue;
                try
                {
                    var product = await tools.GetProductAsync(pn, ct).ConfigureAwait(false);
                    sizes[pn] = McMasterImport.QuantityPerPackage(product.Specifications);
                    if (sizes[pn] is null) reasons[pn] = "sold by the pack but no Quantity per Package";
                }
                catch (McMasterToolException e) { reasons[pn] = "pack size unknown: " + e.Message; }
            }
        }
        catch (Exception e) when (IsStop(e, ct))
        {
            problems.Add(Describe(e));
            log("McMaster prices stopped: " + Describe(e));
        }

        var live = new Dictionary<string, McMasterLivePrice>(StringComparer.OrdinalIgnoreCase);
        var fellBack = new List<string>();
        foreach (var p in parts)
        {
            if (prices.TryGetValue(p.PartNumber, out var pr))
            {
                var size = McMasterImport.IsPackUnit(pr.Unit) ? sizes[p.PartNumber] : 1;
                if (size is > 0) { live[p.PartNumber] = new McMasterLivePrice(p.PartNumber, pr.Price, size.Value, pr.Unit); continue; }
            }
            fellBack.Add(p.PartNumber);
            if (reasons.TryGetValue(p.PartNumber, out var why)) log($"McMaster {p.PartNumber}: {why}");
        }
        return new McMasterPriceRun(live, fellBack, problems);
    }

    /// <summary>Stops the run (everything left falls back) rather than failing one part.</summary>
    private static bool IsStop(Exception e, CancellationToken ct) =>
        e is McMasterNotSignedIn or McMasterSignInCancelled or McMasterAuthException or McMasterUnauthorized or HttpRequestException
        || (e is OperationCanceledException && (ct.IsCancellationRequested || e is TaskCanceledException));

    public static string Describe(Exception e) => e switch
    {
        McMasterSignInCancelled or McMasterNotSignedIn or McMasterAuthException or McMasterUnauthorized => e.Message,
        HttpRequestException => "Could not reach the McMaster server (" + e.Message + ").",
        OperationCanceledException => "Live McMaster prices were cancelled or timed out.",
        _ => e.Message,
    };

    /// <summary>Puts each live price on its McMaster rows (pack price, pack size, date). Other rows are returned as they are.</summary>
    public static IReadOnlyList<BomLine> Apply(IReadOnlyList<BomLine> lines, IReadOnlyDictionary<string, McMasterLivePrice> live, DateTime day)
    {
        var date = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return lines.Select(l =>
        {
            if (!McMasterRule.IsMcMasterCad(l.Node)) return l;
            var pn = McMasterImport.NormalizePartNumber(l.Node.VendorNumber);
            if (pn is null || !live.TryGetValue(pn, out var p)) return l;
            return l with { Node = l.Node with { McMasterPackPrice = p.PackPrice, PackSize = p.PackSize, McMasterPriceDate = date } };
        }).ToList();
    }

    /// <summary>"McMaster: 4 live prices (2026-10-14), 1 fell back to list or file prices"; empty when the BOM has no McMaster parts.</summary>
    public static string Note(int live, int fellBack, DateTime day)
    {
        if (live == 0 && fellBack == 0) return "";
        var text = $"McMaster: {live} live price{(live == 1 ? "" : "s")}";
        if (live > 0) text += " (" + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ")";
        if (fellBack > 0) text += $", {fellBack} fell back to list or file price{(fellBack == 1 ? "" : "s")}";
        return text;
    }
}
