using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using SFVaultBridge.Core;

namespace SFVaultBridge.Addin;

/// <summary>
/// The Settings window: General (version, update check, log folder), Dropbox (app key, sign in and out), Purchasing and packages
/// (sales tax, Process values, print materials, recent partners) and Claude (MCP endpoint, setup info, extension folder).
/// Save validates, writes settings.json through BridgeSettings.Save (unknown keys stay, changed team keys are recorded in userOverrides).
/// Every action loads settings fresh, so saved values apply to the next click with no restart. Never throws into Inventor.
/// </summary>
internal sealed class SettingsForm : Form
{
    private const int TabWidth = 720;   // logical px; AutoScaleMode.Dpi scales these
    private const int TabHeight = 400;

    /// <summary>One list with add, remove and move buttons (or, for the recent list, remove and clear only).</summary>
    private sealed class ListEditor : TableLayoutPanel
    {
        private readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true };
        private readonly TextBox _entry = new() { Dock = DockStyle.Fill };
        private readonly Label _note = new() { AutoSize = true, ForeColor = Color.Firebrick, Margin = new Padding(0, 2, 0, 0) };

        public ListEditor(string title, IEnumerable<string> items, bool editable)
        {
            Dock = DockStyle.Fill; ColumnCount = 1; Margin = new Padding(6, 0, 6, 0);
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            RowStyles.Add(new RowStyle(SizeType.AutoSize));
            RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(new Label { Text = title, AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont ?? Control.DefaultFont, FontStyle.Bold), Margin = new Padding(0, 0, 0, 4) });
            foreach (var i in items) _list.Items.Add(i);
            Controls.Add(_list);
            if (editable)
            {
                RowStyles.Add(new RowStyle(SizeType.AutoSize));
                var add = Button("Add");
                var row = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
                row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                row.Controls.Add(_entry);
                row.Controls.Add(add);
                Controls.Add(row);
                add.Click += (_, _) => OnAdd();
                _entry.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; OnAdd(); } };
            }
            RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 4, 0, 0), Dock = DockStyle.Fill };
            var remove = Button(editable ? "Remove" : "Remove selected");
            remove.Click += (_, _) => OnRemove();
            buttons.Controls.Add(remove);
            if (editable)
            {
                var up = Button("Up"); var down = Button("Down");
                up.Click += (_, _) => OnMove(-1);
                down.Click += (_, _) => OnMove(1);
                buttons.Controls.Add(up); buttons.Controls.Add(down);
            }
            else
            {
                var clear = Button("Clear all");
                clear.Click += (_, _) => { _list.Items.Clear(); _note.Text = ""; };
                buttons.Controls.Add(clear);
            }
            Controls.Add(buttons);
            RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(_note);
        }

        private static Button Button(string text) =>
            new() { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 0, 6, 0) };

        public IReadOnlyList<string> Items => _list.Items.Cast<string>().ToList();

        private void OnAdd()
        {
            var items = Items.ToList();
            if (!SettingsRules.TryAdd(items, _entry.Text, out var error)) { _note.Text = error; return; }
            _list.Items.Add(items[^1]);
            _list.SelectedIndex = _list.Items.Count - 1;
            _entry.Clear(); _note.Text = "";
        }

        private void OnRemove()
        {
            var i = _list.SelectedIndex;
            if (i < 0) return;
            _list.Items.RemoveAt(i);
            if (_list.Items.Count > 0) _list.SelectedIndex = Math.Min(i, _list.Items.Count - 1);
            _note.Text = "";
        }

        private void OnMove(int delta)
        {
            var items = Items.ToList();
            var to = SettingsRules.Move(items, _list.SelectedIndex, delta);
            if (to == _list.SelectedIndex) return;
            var from = _list.SelectedIndex;
            (_list.Items[from], _list.Items[to]) = (_list.Items[to], _list.Items[from]);
            _list.SelectedIndex = to;
        }
    }

    private readonly string _settingsPath;
    private readonly string _endpoint;
    private readonly Func<string>? _checkUpdates;
    private readonly Action<string> _log;
    private readonly BridgeSettings _original;
    private readonly string _dataDir;
    private readonly CancellationTokenSource _closing = new();

    private readonly Label _updateResult = new() { AutoSize = true, MaximumSize = new Size(TabWidth - 60, 0) };
    private readonly Button _check = new() { Text = "Check for updates now", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    private readonly TextBox _key = new() { Width = 380 };
    private readonly Button _signIn = new() { Text = "Sign in", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    private readonly Button _signOut = new() { Text = "Sign out", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    private readonly Label _dropboxStatus = new() { AutoSize = true, MaximumSize = new Size(TabWidth - 60, 0) };
    private readonly TextBox _tax = new() { Width = 80 };
    private readonly ListEditor _process;
    private readonly ListEditor _materials;
    private readonly ListEditor _recent;
    private readonly Label _error = new() { AutoSize = true, ForeColor = Color.Firebrick, MaximumSize = new Size(TabWidth - 220, 0) };
    private readonly Button _save = new() { Text = "Save", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    private readonly Button _cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    private CancellationTokenSource? _signInCts;
    private string? _tokenKey;   // the app key a sign-in made in this window used

    /// <summary>True once Save wrote the file.</summary>
    public bool Saved { get; private set; }

    public SettingsForm(string settingsPath, string endpoint, Func<string>? checkUpdates, Action<string> log)
    {
        _settingsPath = settingsPath; _endpoint = endpoint; _checkUpdates = checkUpdates; _log = log;
        _dataDir = Path.GetDirectoryName(Path.GetFullPath(settingsPath)) ?? "";
        _original = BridgeSettings.Load(settingsPath, log);
        _process = new ListEditor("Process values", _original.ProcessValues, true);
        _materials = new ListEditor("Print materials", _original.PrintMaterials, true);
        _recent = new ListEditor("Recent partners", _original.RecentPartners, false);

        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "SF Vault Bridge Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        AcceptButton = null; CancelButton = _cancel;
        Font = SystemFonts.MessageBoxFont ?? Font;

        _key.Text = _original.DropboxAppKey;
        _tax.Text = SettingsRules.PercentText(_original.SalesTaxRate);
        BuildLayout();
        RefreshDropbox();

        _check.Click += (_, _) => Safe(OnCheckUpdates);
        _key.TextChanged += (_, _) => Safe(RefreshDropbox);
        _signIn.Click += (_, _) => Safe(OnSignIn);
        _signOut.Click += (_, _) => Safe(OnSignOut);
        _save.Click += (_, _) => Safe(OnSave);
        FormClosing += (_, _) => { try { _closing.Cancel(); } catch (ObjectDisposedException) { /* already disposed */ } };
    }

    private void Safe(Action a)
    {
        try { a(); }
        catch (Exception ex) { _log("settings window: " + ex); _error.Text = "Unexpected error: " + ex.Message; }
    }

    private static TableLayoutPanel Page(TabControl tabs, string title)
    {
        var page = new TabPage(title) { UseVisualStyleBackColor = true, Padding = new Padding(12) };
        var table = new TableLayoutPanel { ColumnCount = 1, Dock = DockStyle.Fill };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        page.Controls.Add(table);
        tabs.TabPages.Add(page);
        return table;
    }

    private static void Add(TableLayoutPanel t, Control c, Padding? margin = null)
    {
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        c.Margin = margin ?? new Padding(0, 0, 0, 8);
        c.Anchor = AnchorStyles.Left | AnchorStyles.Top;
        t.Controls.Add(c);
    }

    private static Label Note(string text, bool gray = false) =>
        new() { Text = text, AutoSize = true, MaximumSize = new Size(TabWidth - 60, 0), ForeColor = gray ? SystemColors.GrayText : SystemColors.ControlText };

    private static Button Btn(string text) => new() { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };

    private void BuildLayout()
    {
        var tabs = new TabControl { Size = new Size(TabWidth, TabHeight), Margin = new Padding(8, 8, 8, 0) };

        // General
        var general = Page(tabs, "General");
        Add(general, Note("Installed version: " + AppVersion.Current));
        var checkRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        checkRow.Controls.Add(_check);
        Add(general, checkRow, new Padding(0, 0, 0, 4));
        Add(general, _updateResult);
        var openLog = Btn("Open log folder");
        openLog.Click += (_, _) => Safe(() => OpenFolder(_dataDir, "the log folder"));
        Add(general, openLog);
        Add(general, Note("Logs and settings live in " + _dataDir, gray: true));

        // Dropbox
        var dropbox = Page(tabs, "Dropbox");
        Add(dropbox, Note("Dropbox app key"), new Padding(0, 0, 0, 2));
        Add(dropbox, _key);
        var dbButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        _signIn.Margin = new Padding(0, 0, 8, 0);
        dbButtons.Controls.Add(_signIn); dbButtons.Controls.Add(_signOut);
        Add(dropbox, dbButtons);
        Add(dropbox, _dropboxStatus);
        Add(dropbox, Note("Sign in opens your browser once. The Partner Package then uploads to Dropbox /Suppliers/<Partner>/. " +
                          "Changing the app key signs the old account out. See the README, Dropbox upload, for creating the app.", gray: true));

        // Purchasing and packages
        var purchasing = Page(tabs, "Purchasing and packages");
        var taxRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        taxRow.Controls.Add(new Label { Text = "Sales tax rate", AutoSize = true, Margin = new Padding(0, 6, 8, 0) });
        taxRow.Controls.Add(_tax);
        taxRow.Controls.Add(new Label { Text = "% (0 to 100)", AutoSize = true, Margin = new Padding(4, 6, 0, 0) });
        Add(purchasing, taxRow);
        var lists = new TableLayoutPanel { ColumnCount = 3, Dock = DockStyle.Fill };
        for (var i = 0; i < 3; i++) lists.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
        lists.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        lists.Controls.Add(_process); lists.Controls.Add(_materials); lists.Controls.Add(_recent);
        purchasing.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        purchasing.Controls.Add(lists);

        // Claude
        var claude = Page(tabs, "Claude");
        Add(claude, Note("MCP endpoint"), new Padding(0, 0, 0, 2));
        Add(claude, new TextBox { Text = _endpoint, ReadOnly = true, Width = 380 });
        Add(claude, Note("Token file"), new Padding(0, 0, 0, 2));
        Add(claude, new TextBox { Text = TokenPath, ReadOnly = true, Width = 640 });
        var cButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        var copy = Btn("Copy setup info"); var ext = Btn("Open Claude extension");
        copy.Margin = new Padding(0, 0, 8, 0);
        copy.Click += (_, _) => Safe(OnCopySetup);
        ext.Click += (_, _) => Safe(OnOpenExtension);
        cButtons.Controls.Add(copy); cButtons.Controls.Add(ext);
        Add(claude, cButtons);
        _claudeNote = Note("The bridge runs only while Inventor is open. Copy setup info never copies the token itself.", gray: true);
        Add(claude, _claudeNote);

        var buttons = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(8, 6, 8, 8) };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _save.MinimumSize = _cancel.MinimumSize = new Size(80, 0);
        _save.Margin = _cancel.Margin = new Padding(6, 3, 0, 3);
        _error.Anchor = AnchorStyles.Left;
        buttons.Controls.Add(_error); buttons.Controls.Add(_save); buttons.Controls.Add(_cancel);

        var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Location = Point.Empty };
        root.Controls.Add(tabs);
        root.Controls.Add(buttons);
        Controls.Add(root);
    }

    private Label? _claudeNote;

    private string TokenPath => Path.Combine(_dataDir, "token");

    private void OpenFolder(string folder, string what)
    {
        if (!Directory.Exists(folder)) { MessageBox.Show(this, $"Could not find {what}: {folder}", Text); return; }
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true })?.Dispose();
    }

    private async void OnCheckUpdates()
    {
        if (_checkUpdates is null) { _updateResult.Text = "Update checks are not available in this window."; return; }
        _check.Enabled = false;
        _updateResult.Text = "Checking...";
        try
        {
            var text = await Task.Run(() => _checkUpdates());
            if (!IsDisposed) _updateResult.Text = text;
        }
        catch (Exception ex)
        {
            _log("settings update check failed: " + ex.Message);
            if (!IsDisposed) _updateResult.Text = "The update check failed: " + ex.Message;
        }
        finally { if (!IsDisposed) _check.Enabled = true; }
    }

    private static bool TokenExists()
    {
        try { return File.Exists(DropboxAuth.DefaultTokenPath); } catch { return false; }
    }

    private void RefreshDropbox()
    {
        var hasKey = _key.Text.Trim().Length > 0;
        var signedIn = TokenExists();
        _signIn.Enabled = hasKey && _signInCts is null;
        _signOut.Enabled = signedIn && _signInCts is null;
        string status;
        if (_signInCts is not null) status = "Finish the sign-in in your browser (it times out after 3 minutes).";
        else if (!hasKey) status = signedIn ? "Signed in to Dropbox, but no app key is set." : "Enter the Dropbox app key to turn on sign-in.";
        else status = signedIn ? "Signed in to Dropbox." : "Not signed in to Dropbox.";
        _dropboxStatus.Text = status;
    }

    private async void OnSignIn()
    {
        var key = _key.Text.Trim();
        if (key.Length == 0) return;
        _signInCts = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        var cts = _signInCts;
        RefreshDropbox();
        try
        {
            using var http = DropboxClient.NewHttpClient();
            var auth = new DropboxAuth(key, http);
            await Task.Run(() => auth.SignInAsync(cts.Token));
            _tokenKey = key;
            _error.Text = "";
        }
        catch (OperationCanceledException) { /* cancelled by closing the window */ }
        catch (Exception ex)
        {
            _log("settings dropbox sign-in failed: " + ex.Message);
            if (!IsDisposed) _error.Text = "Dropbox sign-in failed: " + ex.Message;
        }
        finally
        {
            _signInCts = null;
            cts.Dispose();
            if (!IsDisposed) RefreshDropbox();
        }
    }

    private void OnSignOut()
    {
        var key = (_tokenKey ?? (_original.DropboxAppKey.Length > 0 ? _original.DropboxAppKey : _key.Text)).Trim();
        SignOutWith(key);
        RefreshDropbox();
    }

    private void SignOutWith(string key)
    {
        try
        {
            using var http = DropboxClient.NewHttpClient();
            new DropboxAuth(key, http).SignOut();
            _tokenKey = null;
            _error.Text = "";
        }
        catch (Exception ex)
        {
            _log("settings dropbox sign-out failed: " + ex.Message);
            _error.Text = "Could not sign out of Dropbox: " + ex.Message;
        }
    }

    private void OnCopySetup()
    {
        Clipboard.SetText(ClaudeExtension.SetupInfo(_endpoint, TokenPath));
        _claudeNote!.Text = "Copied the endpoint and the token file path (not the token).";
    }

    private void OnOpenExtension()
    {
        var install = new List<string>();
        try
        {
            var dir = Path.GetDirectoryName(typeof(SettingsForm).Assembly.Location);
            if (!string.IsNullOrEmpty(dir)) { install.Add(dir); var parent = Path.GetDirectoryName(dir); if (!string.IsNullOrEmpty(parent)) install.Add(parent); }
        }
        catch (Exception ex) { _log("settings: bundle folder unknown: " + ex.Message); }
        var hit = ClaudeExtension.Find(install, Downloads.Folder);
        if (hit is { } found)
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{found.File}\"") { UseShellExecute = true })?.Dispose();
            _claudeNote!.Text = "Double-click " + Path.GetFileName(found.File) + " to install it in Claude Desktop.";
            return;
        }
        if (Directory.Exists(Downloads.Folder))
        {
            OpenFolder(Downloads.Folder, "the release folder");
            _claudeNote!.Text = "No SF Vault Bridge.mcpb in that folder yet. It comes in the release zip next to Install.cmd.";
            return;
        }
        var how = "The Claude extension (SF Vault Bridge.mcpb) comes in the release zip next to Install.cmd. " +
                  "Download a release from Vault $/Tools/SF Vault Bridge/ (the ribbon status button downloads it to Downloads\\SF Vault Bridge), unzip it, then double-click the .mcpb.";
        _claudeNote!.Text = how;
        MessageBox.Show(this, how, Text);
    }

    private void OnSave()
    {
        _error.Text = "";
        if (!SettingsRules.TryParsePercent(_tax.Text, out var rate, out var taxError)) { _error.Text = taxError; return; }
        var process = SettingsRules.CleanList(_process.Items);
        var materials = SettingsRules.CleanList(_materials.Items);
        if (SettingsRules.ListError("Process values", process) is { } e1) { _error.Text = e1; return; }
        if (SettingsRules.ListError("Print materials", materials) is { } e2) { _error.Text = e2; return; }
        var key = _key.Text.Trim();
        var edited = _original with
        {
            ProcessValues = process, PrintMaterials = materials, RecentPartners = _recent.Items,
            DropboxAppKey = key, SalesTaxRate = rate,
        };
        if (_signInCts is not null) { _error.Text = "Finish or wait for the Dropbox sign-in first."; return; }
        // A different app key cannot use the old account's token: sign the old one out (a sign-in made here with the new key stays).
        if (key != _original.DropboxAppKey && TokenExists() && _tokenKey != key)
            SignOutWith(_original.DropboxAppKey.Length > 0 ? _original.DropboxAppKey : key);
        if (!BridgeSettings.Save(_settingsPath, _original, edited, _log))
        {
            _error.Text = "Could not write settings.json. Details are in bridge.log.";
            return;
        }
        Saved = true;
        _log("settings saved; changed: " + string.Join(", ", BridgeSettings.ChangedKeys(_original, edited)));
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _closing.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>Builds the form for the layout preview harness (a temp settings path, no Inventor, no Vault).</summary>
    internal static SettingsForm Preview(string settingsPath) => new(settingsPath, "http://localhost:8790/mcp", () => "You are up to date (preview).", _ => { });
}
