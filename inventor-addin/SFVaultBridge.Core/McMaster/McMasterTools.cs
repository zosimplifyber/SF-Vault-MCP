using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace SFVaultBridge.Core.McMaster;

public sealed record McMasterProduct(string PartNumber, string Family, string Description, IReadOnlyDictionary<string, IReadOnlyList<string>> Specifications,
                                     IReadOnlyList<string> CadFormats, string Status = "");

public sealed record McMasterPrice(string PartNumber, double UnitPrice, string UnitOfMeasure);

public sealed record McMasterFile(string Format, string FileName, string Url);

/// <summary>One enrich_bom line: a price, or the reason there is none.</summary>
public sealed record McMasterBomLine(int Line, string PartNumber, double? UnitPrice, string UnitOfMeasure, string? Error);

/// <summary>The McMaster MCP tools the add-in uses. McMasterTools talks to the server; tests use a fake.</summary>
public interface IMcMasterTools
{
    Task<McMasterProduct> GetProductAsync(string partNumber, CancellationToken ct);
    Task<McMasterPrice> GetPriceAsync(string partNumber, int qty, CancellationToken ct);
    Task<McMasterFile> GetCadAsync(string partNumber, string cadFormat, CancellationToken ct);
    Task<IReadOnlyList<McMasterBomLine>> EnrichBomAsync(IReadOnlyList<(string PartNumber, int Qty)> lines, CancellationToken ct);
    Task DownloadAsync(string url, string path, CancellationToken ct);
}

/// <summary>Shapes the McMaster tool results (server.py, service.py in SF-McMasterMCP). Pure.</summary>
public static class McMasterShapes
{
    public static McMasterProduct Product(JsonObject o)
    {
        var specs = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (o["specifications"] is JsonObject s)
            foreach (var (k, v) in s)
                specs[k] = v is JsonArray a ? a.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList() : v is null ? new List<string>() : new List<string> { v.ToString() };
        var cad = (o["cad_formats"] as JsonArray)?.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList() ?? new List<string>();
        return new McMasterProduct(Text(o, "part_number"), Text(o, "family"), Text(o, "description"), specs, cad, Text(o, "status"));
    }

    public static McMasterPrice Price(JsonObject o)
    {
        var unit = Number(o["unit_price"]) ?? throw new McMasterToolException("McMaster returned no unit price.");
        return new McMasterPrice(Text(o, "part_number"), unit, Text(o, "unit_of_measure"));
    }

    public static McMasterFile File(JsonObject o)
    {
        var url = Text(o, "url");
        if (url.Length == 0) throw new McMasterToolException("McMaster returned no download link.");
        return new McMasterFile(Text(o, "format"), Text(o, "filename"), url);
    }

    public static IReadOnlyList<McMasterBomLine> BomLines(JsonObject o) =>
        (o["lines"] as JsonArray)?.OfType<JsonObject>().Select(l => new McMasterBomLine(
            l["line"] is JsonValue lv && lv.TryGetValue<int>(out var n) ? n : 0,
            Text(l, "part_number"), Number(l["unit_price"]), Text(l, "unit_of_measure"),
            l["error"]?.ToString())).ToList() ?? new List<McMasterBomLine>();

    public static JsonObject BomArgs(IEnumerable<(string PartNumber, int Qty)> lines) =>
        new() { ["lines"] = new JsonArray(lines.Select(l => (JsonNode?)new JsonObject { ["part_number"] = l.PartNumber, ["qty"] = Math.Max(1, l.Qty) }).ToArray()) };

    private static string Text(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";

    private static double? Number(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return double.IsFinite(d) ? d : null;
        if (v.TryGetValue<string>(out var s) && double.TryParse(s.Trim().TrimStart('$'), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
        return null;
    }
}

/// <summary>The live tools over MCP. Downloads send the bearer token only to the MCP server's own host.</summary>
public sealed class McMasterTools : IMcMasterTools
{
    public const long MaxDownloadBytes = 200L * 1024 * 1024;
    private const int SniffBytes = 1024;

    /// <summary>
    /// True when the first bytes fit the extension: STEP starts "ISO-10303-21" and PDF "%PDF" (a UTF-8 BOM and leading white space are
    /// allowed). Other extensions are not checked.
    /// </summary>
    public static bool ContentMatches(byte[] head, string extension)
    {
        var text = System.Text.Encoding.Latin1.GetString(head);
        if (text.StartsWith("\u00EF\u00BB\u00BF", StringComparison.Ordinal)) text = text[3..];
        text = text.TrimStart();
        return extension.ToLowerInvariant() switch
        {
            ".step" or ".stp" => text.StartsWith("ISO-10303-21", StringComparison.Ordinal),
            ".pdf" => text.StartsWith("%PDF", StringComparison.Ordinal),
            _ => true,
        };
    }

    private readonly McpHttpClient _mcp;
    private readonly HttpClient _http;
    private readonly Func<bool, CancellationToken, Task<string>> _tokenFor;

    public McMasterTools(McpHttpClient mcp, HttpClient http, Func<bool, CancellationToken, Task<string>> tokenFor)
    {
        _mcp = mcp; _http = http; _tokenFor = tokenFor;
    }

    public async Task<McMasterProduct> GetProductAsync(string partNumber, CancellationToken ct) =>
        McMasterShapes.Product(await _mcp.CallToolAsync("get_product", new JsonObject { ["part_number"] = partNumber }, ct).ConfigureAwait(false));

    public async Task<McMasterPrice> GetPriceAsync(string partNumber, int qty, CancellationToken ct) =>
        McMasterShapes.Price(await _mcp.CallToolAsync("get_price", new JsonObject { ["part_number"] = partNumber, ["qty"] = Math.Max(1, qty) }, ct).ConfigureAwait(false));

    public async Task<McMasterFile> GetCadAsync(string partNumber, string cadFormat, CancellationToken ct) =>
        McMasterShapes.File(await _mcp.CallToolAsync("get_cad", new JsonObject { ["part_number"] = partNumber, ["cad_format"] = cadFormat }, ct).ConfigureAwait(false));

    public async Task<IReadOnlyList<McMasterBomLine>> EnrichBomAsync(IReadOnlyList<(string PartNumber, int Qty)> lines, CancellationToken ct) =>
        McMasterShapes.BomLines(await _mcp.CallToolAsync("enrich_bom", McMasterShapes.BomArgs(lines), ct).ConfigureAwait(false));

    /// <summary>
    /// Downloads to a .part file, checks the content matches the extension (.step/.stp must start "ISO-10303-21", .pdf "%PDF", so an
    /// HTML error page is never imported), then moves it over path. A failed or cancelled download leaves no .part file behind.
    /// Throws McMasterToolException with the server's status on failure. Redirects are not followed.
    /// </summary>
    public async Task DownloadAsync(string url, string path, CancellationToken ct)
    {
        if (!McMasterOAuth.IsAllowedUrl(url)) throw new McMasterToolException("McMaster returned a download link that is not https.");
        var ours = McMasterOAuth.SameOrigin(url, _mcp.ServerUrl);
        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (ours) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _tokenFor(attempt > 0, ct).ConfigureAwait(false));
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.Unauthorized && ours && attempt == 0) continue;
            if (McMasterHttp.IsRedirect(resp))
                throw new McMasterToolException($"The McMaster download answered with a redirect ({(int)resp.StatusCode}); it was not followed.");
            if (!resp.IsSuccessStatusCode)
                throw new McMasterToolException($"The McMaster download failed ({(int)resp.StatusCode}). The link lasts one hour; press Fetch again.");
            if (resp.Content.Headers.ContentLength is > MaxDownloadBytes) throw new McMasterToolException("The McMaster file is too large to download.");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temp = path + ".part";
            try
            {
                var head = new List<byte>();
                await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                await using (var dst = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[81920];
                    long total = 0;
                    int n;
                    while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        total += n;
                        if (total > MaxDownloadBytes) throw new McMasterToolException("The McMaster file is too large to download.");
                        if (head.Count < SniffBytes) head.AddRange(buffer.Take(Math.Min(n, SniffBytes - head.Count)));
                        await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    }
                }
                if (!ContentMatches(head.ToArray(), Path.GetExtension(path)))
                    throw new McMasterToolException($"McMaster sent something that is not a {Path.GetExtension(path).TrimStart('.').ToUpperInvariant()} file (often an error page). Nothing was saved; press Fetch again.");
                File.Move(temp, path, true);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            return;
        }
    }
}
