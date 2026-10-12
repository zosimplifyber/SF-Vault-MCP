using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using SFVaultBridge.Core;
using Inv = Inventor;

namespace SFVaultBridge.Addin;

/// <summary>Inventor entry point. Starts the local bridge; never lets an exception reach Inventor.</summary>
[Guid("6F1C2D9A-4B7E-4E2B-9C1A-5D3E8F7A2B10")]
[ComVisible(true)]
public sealed class AddInServer : Inv.ApplicationAddInServer
{
    public const string Version = "0.1.0";
    public const string Prefix = "http://localhost:8790/";

    private Inv.Application? _app;
    private System.Windows.Forms.Control? _ui;
    private HttpHost? _host;
    private FileLog? _log;

    public void Activate(Inv.ApplicationAddInSite addInSiteObject, bool firstTime)
    {
        var dataDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Simplifyber", "VaultBridge");
        _log = new FileLog(System.IO.Path.Combine(dataDir, "bridge.log"));
        try
        {
            _app = addInSiteObject.Application;
            _ui = new System.Windows.Forms.Control();
            _ = _ui.Handle;
            if (!_ui.IsHandleCreated) throw new InvalidOperationException("UI marshalling handle could not be created.");
            var token = TokenStore.LoadOrCreate(System.IO.Path.Combine(dataDir, "token"));
            var router = new Router(token);
            BridgeOps.RegisterAll(router, new VaultSession(), new StepImporter(_app), new PathGuard(AllowedRoots(_app)), Version, RunOnUi);
            _host = new HttpHost(Prefix, router, _log.Write);
            _host.Start();
            _log.Write($"started {Version} on {Prefix}");
        }
        catch (Exception ex)
        {
            _log.Write("start failed, bridge stopped: " + ex);
            _host?.Dispose();
            _host = null;
        }
    }

    public void Deactivate()
    {
        try
        {
            _host?.Dispose();
            _ui?.Dispose();
            _log?.Write("stopped");
        }
        catch (Exception ex) { _log?.Write("stop failed: " + ex); }
        _host = null;
        _ui = null;
        _app = null;
    }

    public void ExecuteCommand(int commandID) { }

    public object Automation => null!;

    private JsonNode? RunOnUi(Func<JsonNode?> work)
    {
        var ui = _ui ?? throw new BridgeException("stopped", "The bridge is shutting down.");
        if (!ui.IsHandleCreated) throw new BridgeException("stopped", "The bridge is not ready. Restart Inventor.");
        if (!ui.InvokeRequired) return work();
        JsonNode? result = null;
        ExceptionDispatchInfo? error = null;
        ui.Invoke(new Action(() =>
        {
            try { result = work(); }
            catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
        }));
        error?.Throw();
        return result;
    }

    private static IEnumerable<string> AllowedRoots(Inv.Application app)
    {
        yield return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        string? workspace = null;
        try { workspace = app.DesignProjectManager.ActiveDesignProject.WorkspacePath; } catch (COMException) { }
        yield return string.IsNullOrWhiteSpace(workspace) ? @"C:\Vault Workspace" : workspace;
    }
}
