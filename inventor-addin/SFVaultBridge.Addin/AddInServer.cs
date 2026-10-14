using System.Reflection;
using System.Runtime.CompilerServices;
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
    public static string Version => AppVersion.Current;
    public const string Prefix = "http://localhost:8790/";

    private Inv.Application? _app;
    private System.Windows.Forms.Control? _ui;
    private HttpHost? _host;
    private FileLog? _log;
    private ResolveEventHandler? _resolver;
    private IVaultSession? _vault;
    private RibbonStatus? _ribbon;
    private RibbonActions? _actions;

    // Vault DLLs are Private=false: they must come from Inventor's own load of the Vault add-in. Hand back an
    // already-loaded assembly of the same simple name so we never load a second copy into another context.
    private static Assembly? ResolveLoaded(object? sender, ResolveEventArgs args)
    {
        var name = new AssemblyName(args.Name).Name;
        return AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string VaultAssemblyLocation() =>
        typeof(Connectivity.Application.VaultBase.ConnectionManager).Assembly.Location;

    private static string DescribeVaultAssembly()
    {
        try { return VaultAssemblyLocation(); }
        catch (Exception ex) { return "unavailable: " + ex.Message; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IVaultSession NewVaultSession() => new VaultSession();

    public void Activate(Inv.ApplicationAddInSite addInSiteObject, bool firstTime)
    {
        var dataDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Simplifyber", "VaultBridge");
        _log = new FileLog(System.IO.Path.Combine(dataDir, "bridge.log"));
        try
        {
            _resolver = ResolveLoaded;
            AppDomain.CurrentDomain.AssemblyResolve += _resolver;
            _app = addInSiteObject.Application;
            _ui = new System.Windows.Forms.Control();
            _ = _ui.Handle;
            if (!_ui.IsHandleCreated) throw new InvalidOperationException("UI marshalling handle could not be created.");
            var token = TokenStore.LoadOrCreate(System.IO.Path.Combine(dataDir, "token"));
            var router = new Router(token);
            var roots = AllowedRoots(_app).ToList();
            _vault = NewVaultSession();
            var importer = new StepImporter(_app);
            var dataCard = new DataCardHost(_app);
            var pdfFetcher = new McMasterPdfFetcher(() => BridgeSettings.Load(BridgeSettings.DefaultPath, _log.Write).McMasterServerUrl, _log.Write);
            BridgeOps.RegisterAll(router, _vault, importer, new PathGuard(roots), Version, RunOnUi, dataCard, _log.Write, dataCard, pdfFetcher, dataCard);
            ReleaseChannelOps.Register(router, new ReleaseChannel(), new PathGuard(roots), RunOnUi);
            var downloads = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            var jobs = new JobManager(RunOnUi, log: _log.Write);
            var tools = McpCatalog.Build(new VaultReader(), new InventorReader(_app, _log.Write), _vault, importer, Version, dataCard,
                new IntakeHost(_app, _log.Write), new PathGuard(new[] { downloads }), downloads, jobs, _log.Write, new VaultExtrasReader(), dataCard, pdfFetcher, dataCard);
            McpCatalog.RegisterReadOps(router, tools, RunOnUi);
            _host = new HttpHost(Prefix, router, _log.Write) { Mcp = new McpServer(token, Version, tools, RunOnUi, log: _log.Write, jobs: jobs) };
            _host.Start();
            _log.Write($"started {Version} on {Prefix}; vault assembly: {DescribeVaultAssembly()}; roots: {string.Join(" | ", roots)}");
        }
        catch (Exception ex)
        {
            _log.Write("start failed, bridge stopped: " + ex);
            _host?.Dispose();
            _host = null;
            DropResolver();
        }
        CreateRibbon(dataDir);
    }

    // Runs even when the host failed to start so the button shows Stopped. Never throws.
    private void CreateRibbon(string dataDir)
    {
        try
        {
            if (_app is null || _log is null) return;
            _ribbon = new RibbonStatus(_app, () => _host, SafeVaultStatus, Prefix,
                System.IO.Path.Combine(dataDir, "bridge.log"), _log.Write);
            _ribbon.Create();
            _actions = new RibbonActions(_app, _ribbon.PanelsFor, _log.Write, _ribbon.CheckUpdatesNow);
            _actions.Create();
        }
        catch (Exception ex) { _log?.Write("ribbon setup failed: " + ex); }
    }

    private StatusInfo? SafeVaultStatus()
    {
        try { return _vault?.Status(); }
        catch (Exception) { return null; }
    }

    public void Deactivate()
    {
        try { _actions?.Destroy(); }
        catch (Exception ex) { _log?.Write("actions destroy failed: " + ex); }
        _actions = null;
        try { _ribbon?.Destroy(); }
        catch (Exception ex) { _log?.Write("ribbon stop failed: " + ex); }
        _ribbon = null;
        try
        {
            _host?.Dispose();
            DropResolver();
            _ui?.Dispose();
            _log?.Write("stopped");
        }
        catch (Exception ex) { _log?.Write("stop failed: " + ex); }
        _host = null;
        _vault = null;
        _ui = null;
        _app = null;
    }

    private void DropResolver()
    {
        if (_resolver is null) return;
        AppDomain.CurrentDomain.AssemblyResolve -= _resolver;
        _resolver = null;
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
        try { workspace = app.DesignProjectManager.ActiveDesignProject.WorkspacePath; } catch (Exception) { }
        yield return !string.IsNullOrWhiteSpace(workspace) && System.IO.Path.IsPathRooted(workspace) ? workspace : @"C:\Vault Workspace";
    }
}
