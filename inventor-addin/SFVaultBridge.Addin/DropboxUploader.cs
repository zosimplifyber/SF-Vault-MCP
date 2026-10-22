using SFVaultBridge.Core;

namespace SFVaultBridge.Addin;

/// <summary>The MCP side of Dropbox: reads the app key from settings at call time (so a key pasted in Settings works without a restart),
/// shares one HttpClient, and runs the generic upload. Never runs on the UI thread.</summary>
public sealed class DropboxUploader : IDropboxUploader
{
    private readonly Action<string> _log;
    private readonly HttpClient _http = DropboxClient.NewHttpClient();

    public DropboxUploader(Action<string> log) => _log = log;

    private string AppKey => BridgeSettings.Load(BridgeSettings.DefaultPath, _log).DropboxAppKey;

    public DropboxAccountStatus Status()
    {
        var path = DropboxAuth.DefaultTokenPath;
        return new DropboxAccountStatus(AppKey.Length > 0, File.Exists(path), path);
    }

    public DropboxUploadOutcome Upload(DropboxUploadPlan plan, CancellationToken ct)
    {
        var key = AppKey;
        if (key.Length == 0) throw new BridgeException("not_configured", "No Dropbox app key is set. Put it in the add-in Settings (Dropbox tab) or settings.json as dropboxAppKey.");
        var client = new DropboxClient(new DropboxAuth(key, _http), _http);
        try
        {
            return client.UploadManyAsync(plan, _log, ct).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { throw new BridgeException("cancelled", "The Dropbox upload was cancelled."); }
        catch (Exception e) when (e is not BridgeException)
        {
            // Any Dropbox refusal (including the client's own DropboxException) is reported as dropbox_failed with its message.
            _log("dropbox upload failed: " + e);
            throw new BridgeException("dropbox_failed", e.Message);
        }
    }
}
