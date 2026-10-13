using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;
using SFVaultBridge.Core;
using Inv = Inventor;

namespace SFVaultBridge.Addin;

/// <summary>Ribbon status button. Every member is exception-safe: a ribbon failure is logged and never reaches Inventor.</summary>
internal sealed class RibbonStatus
{
    private const string ClientId = "{6F1C2D9A-4B7E-4E2B-9C1A-5D3E8F7A2B10}";
    private const string DefName = "SFVaultBridge.Status";
    private static readonly string[] Ribbons = { "ZeroDoc", "Part", "Assembly", "Drawing" };

    private readonly Inv.Application _app;
    private readonly Func<HttpHost?> _host;
    private readonly Func<StatusInfo?> _vaultStatus;
    private readonly string _prefix;
    private readonly string _logPath;
    private readonly Action<string> _log;
    private readonly Dictionary<BridgeState, (stdole.IPictureDisp Small, stdole.IPictureDisp Large)> _icons = new();
    private readonly List<Inv.RibbonPanel> _panels = new();
    private readonly Dictionary<string, List<Inv.RibbonPanel>> _groups = new(StringComparer.Ordinal);
    private readonly List<Inv.RibbonTab> _tabs = new();

    /// <summary>Panels on the SF Vault Bridge tab, in order: Status first, then the button groups.</summary>
    public static readonly string[] Groups = { "Status", "Parts", "Purchasing", "Review", "Manufacturing" };
    public const string TabName = "SF Vault Bridge";
    private const string TabId = "SFVaultBridge.Tab";
    private readonly HashSet<string> _loggedErrors = new();
    private Inv.ButtonDefinition? _def;
    private Inv.ButtonDefinitionSink_OnExecuteEventHandler? _onExecute;
    private Timer? _timer;
    private BridgeStatusView? _last;

    public IReadOnlyList<Inv.RibbonPanel> Panels => _panels;

    /// <summary>The panels (one per ribbon) for a button group; empty when the tab could not be made.</summary>
    public IReadOnlyList<Inv.RibbonPanel> PanelsFor(string group) =>
        _groups.TryGetValue(group, out var list) ? list : (IReadOnlyList<Inv.RibbonPanel>)Array.Empty<Inv.RibbonPanel>();

    public RibbonStatus(Inv.Application app, Func<HttpHost?> host, Func<StatusInfo?> vaultStatus, string prefix, string logPath, Action<string> log)
    {
        _app = app; _host = host; _vaultStatus = vaultStatus; _prefix = prefix; _logPath = logPath; _log = log;
    }

    public void Create()
    {
        try
        {
            var view = Current();
            _def = FindExisting() ?? _app.CommandManager.ControlDefinitions.AddButtonDefinition(
                "Vault Bridge", DefName, Inv.CommandTypesEnum.kQueryOnlyCmdType, ClientId,
                view.Details, view.Details, Icon(view.State).Small, Icon(view.State).Large,
                Inv.ButtonDisplayEnum.kAlwaysDisplayText);
            Apply(view);
            _onExecute = OnExecute;
            _def.OnExecute += _onExecute;
            foreach (var name in Ribbons) AddPanel(name);
            _timer = new Timer { Interval = 5000 };
            _timer.Tick += (_, _) => Refresh();
            _timer.Start();
        }
        catch (Exception ex) { _log("ribbon failed: " + ex); }
    }

    public void Destroy()
    {
        try { _timer?.Stop(); _timer?.Dispose(); } catch (Exception ex) { _log("ribbon timer stop failed: " + ex.Message); }
        _timer = null;
        try { if (_def is not null && _onExecute is not null) _def.OnExecute -= _onExecute; } catch (Exception ex) { _log("ribbon unhook failed: " + ex.Message); }
        foreach (var p in _panels)
        {
            try { p.Delete(); } catch (Exception ex) { _log("ribbon panel delete failed: " + ex.Message); }
        }
        _panels.Clear();
        _groups.Clear();
        foreach (var t in _tabs)
        {
            try { t.Delete(); } catch (Exception ex) { _log("ribbon tab delete failed: " + ex.Message); }
        }
        _tabs.Clear();
        try { _def?.Delete(); } catch (Exception ex) { _log("ribbon button delete failed: " + ex.Message); }
        _def = null;
    }

    private Inv.ButtonDefinition? FindExisting()
    {
        try { return _app.CommandManager.ControlDefinitions[DefName] as Inv.ButtonDefinition; }
        catch (Exception) { return null; }
    }

    private void AddPanel(string ribbonName)
    {
        try
        {
            var tabs = _app.UserInterfaceManager.Ribbons[ribbonName].RibbonTabs;
            Inv.RibbonTab? tab = null;
            try { tab = tabs[TabId]; } catch (Exception) { }
            if (tab is null) { tab = tabs.Add(TabName, TabId, ClientId, "", false, false); _tabs.Add(tab); }
            foreach (var group in Groups)
            {
                var id = $"SFVaultBridge.Panel.{group}.{ribbonName}";
                Inv.RibbonPanel? panel = null;
                try { panel = tab.RibbonPanels[id]; } catch (Exception) { }
                panel ??= tab.RibbonPanels.Add(group, id, ClientId);
                _panels.Add(panel);
                if (!_groups.TryGetValue(group, out var list)) _groups[group] = list = new List<Inv.RibbonPanel>();
                list.Add(panel);
            }
            PanelsFor("Status")[^1].CommandControls.AddButton(_def!, true);
        }
        catch (Exception ex) { _log($"ribbon panel on {ribbonName} skipped: {ex.Message}"); }
    }

    private BridgeStatusView Current()
    {
        StatusInfo? status = null;
        try { status = _vaultStatus(); } catch (Exception) { }
        var host = _host();
        return StatusModel.Evaluate(host?.IsListening ?? false, status, host?.LastRequestUtc, _prefix);
    }

    private void Refresh()
    {
        try
        {
            var view = Current();
            if (_last is not null && _last.State == view.State && _last.Details == view.Details) return;
            Apply(view);
        }
        catch (Exception ex)
        {
            if (_loggedErrors.Add(ex.GetType().Name + ex.Message)) _log("ribbon refresh failed: " + ex);
        }
    }

    private void Apply(BridgeStatusView view)
    {
        var def = _def;
        if (def is null) return;
        var (small, large) = Icon(view.State);
        def.StandardIcon = small;
        def.LargeIcon = large;
        def.DescriptionText = view.Details;
        def.ToolTipText = view.Details;
        _last = view;
    }

    private void OnExecute(Inv.NameValueMap context)
    {
        try
        {
            var view = Current();
            var answer = MessageBox.Show(view.Details + Environment.NewLine + Environment.NewLine + "Open bridge.log?",
                "SF Vault Bridge: " + view.Title, MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (answer == DialogResult.Yes) Process.Start(new ProcessStartInfo(_logPath) { UseShellExecute = true });
        }
        catch (Exception ex) { _log("ribbon click failed: " + ex.Message); }
    }

    private (stdole.IPictureDisp Small, stdole.IPictureDisp Large) Icon(BridgeState state)
    {
        if (_icons.TryGetValue(state, out var cached)) return cached;
        var color = state switch
        {
            BridgeState.Ready => ColorTranslator.FromHtml("#2E9E44"),
            BridgeState.SignInNeeded => ColorTranslator.FromHtml("#E0A100"),
            _ => ColorTranslator.FromHtml("#C62828"),
        };
        var pair = Icons.Pair(size => Icons.Dot(size, color));
        _icons[state] = pair;
        return pair;
    }
}
