using SFVaultBridge.Core;
using SFVaultBridge.Core.McMaster;

namespace SFVaultBridge.Addin;

/// <summary>
/// Downloads a part's McMaster "2-D PDF" through the McMaster client Import New Part's Fetch uses (get_product, get_cad, then the download
/// with its %PDF content check). The sign-in is the shared McMasterSession one and starts the browser when needed. Blocks the calling thread
/// (Inventor's UI thread, like every other bridge operation); a stalled server is cut off after six minutes.
/// </summary>
internal sealed class McMasterPdfFetcher : IVendorPdfFetcher
{
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(6);
    private readonly Func<string> _serverUrl;
    private readonly Action<string> _log;

    public McMasterPdfFetcher(Func<string> serverUrl, Action<string> log) { _serverUrl = serverUrl; _log = log; }

    private static string Profile() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string Root() => System.IO.Path.GetDirectoryName(McMasterImport.DownloadFolder(Profile(), "x"))!;

    public string Fetch(string vendorNumber, string pdfName)
    {
        var pn = McMasterImport.NormalizePartNumber(vendorNumber) ?? throw new InvalidOperationException($"{vendorNumber} is not a McMaster part number.");
        try
        {
            var tools = McMasterSession.For(_serverUrl(), _log).Tools;
            using var cts = new CancellationTokenSource(Limit);
            var ct = cts.Token;
            return Task.Run(async () =>
            {
                var product = await tools.GetProductAsync(pn, ct).ConfigureAwait(false);
                var format = McMasterImport.PickFormat(product.CadFormats, McMasterImport.PdfFormat, "PDF")
                             ?? throw new McMasterToolException($"McMaster has no 2-D PDF for {pn}.");
                var link = await tools.GetCadAsync(pn, format, ct).ConfigureAwait(false);
                var path = System.IO.Path.Combine(McMasterImport.DownloadFolder(Profile(), pn), pdfName);
                await tools.DownloadAsync(link.Url, path, ct).ConfigureAwait(false);
                return path;
            }, ct).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not BridgeException)
        {
            _log($"attach pdf fetch {pn}: {ex.GetType().Name}: {McMasterSession.Describe(ex)}");
            throw new InvalidOperationException("McMaster download failed: " + McMasterSession.Describe(ex));
        }
    }

    public void Discard(string path)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.Equals(System.IO.Path.GetDirectoryName(dir), Root(), StringComparison.OrdinalIgnoreCase)) return;
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            if (dir is not null && !System.IO.Directory.EnumerateFileSystemEntries(dir).Any()) System.IO.Directory.Delete(dir);
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { }
    }
}
