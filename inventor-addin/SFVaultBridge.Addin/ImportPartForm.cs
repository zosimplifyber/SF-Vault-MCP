using System.Drawing;
using System.Windows.Forms;
using SFVaultBridge.Core;
using Inv = Inventor;

namespace SFVaultBridge.Addin;

/// <summary>The Import New Part form. Never throws into Inventor: failures show a message and are logged.</summary>
internal sealed class ImportPartForm : Form
{
    private const string NewVendor = "New vendor...";
    private const string VendorFolder = "(vendor folder)";
    private const string NewSubfolder = "New subfolder...";

    /// <summary>Last vendor chosen in this Inventor session.</summary>
    private static string? _lastVendor;

    private readonly Action<string> _log;
    private readonly ComboBox _vendor = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _sub = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _number = new();
    private readonly TextBox _desc = new();
    private readonly ComboBox _material = new() { DropDownStyle = ComboBoxStyle.DropDown };
    private readonly TextBox _finish = new();
    private readonly TextBox _designer = new() { ReadOnly = true };
    private readonly TextBox _approved = new() { ReadOnly = true, Text = "NOT REVIEWED" };
    private readonly Label _will = new() { AutoSize = false };
    private readonly Label _error = new() { AutoSize = false, ForeColor = Color.Firebrick };
    private readonly Button _import = new() { Text = "Import", DialogResult = DialogResult.None };
    private readonly Button _cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel };
    private string _vendorSel = "";
    private string _subSel = "";
    private bool _loading;

    public ImportPlan? Plan { get; private set; }

    public ImportPartForm(Inv.Application app, string stepFile, string pdfFile, Action<string> log)
    {
        _log = log;
        Text = "Import New Part";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(560, 470);
        AcceptButton = _import; CancelButton = _cancel;
        Font = SystemFonts.MessageBoxFont ?? Font;

        var designer = "";
        try { designer = app.GeneralOptions.UserName ?? ""; } catch (Exception ex) { log($"import form: user name unavailable: {ex.Message}"); }
        _designer.Text = designer;

        var prefill = VendorFileNames.FromFiles(stepFile, pdfFile);
        _number.Text = prefill.VendorNumber;
        _desc.Text = prefill.Description;

        IReadOnlyList<string> vendors = Array.Empty<string>();
        try { vendors = VaultFiles.LibraryVendors(); }
        catch (Exception ex) { log($"import form: vendor list failed: {ex}"); _error.Text = "Could not read the vendor folders from Vault: " + ex.Message; }
        _loading = true;
        foreach (var v in vendors) _vendor.Items.Add(v);
        _vendor.Items.Add(NewVendor);
        var pre = prefill.Vendor is not null ? vendors.FirstOrDefault(v => string.Equals(v, prefill.Vendor, StringComparison.OrdinalIgnoreCase)) : null;
        pre ??= _lastVendor is not null ? vendors.FirstOrDefault(v => string.Equals(v, _lastVendor, StringComparison.OrdinalIgnoreCase)) : null;
        if (pre is not null) { _vendor.SelectedItem = pre; _vendorSel = pre; }
        _loading = false;
        FillSubfolders();

        try
        {
            foreach (Inv.Asset a in app.ActiveMaterialLibrary.MaterialAssets) _material.Items.Add(a.DisplayName);
        }
        catch (Exception ex) { log($"import form: material list unavailable: {ex.Message}"); }

        Build(stepFile, pdfFile);
        _vendor.SelectedIndexChanged += (_, _) => Safe(OnVendorChanged);
        _sub.SelectedIndexChanged += (_, _) => Safe(OnSubChanged);
        _number.TextChanged += (_, _) => Safe(UpdateWill);
        _import.Click += (_, _) => Safe(OnImport);
        UpdateWill();
    }

    private void Safe(Action a)
    {
        try { a(); }
        catch (Exception ex) { _log($"import form: {ex}"); _error.Text = "Unexpected error: " + ex.Message; }
    }

    private void Build(string stepFile, string pdfFile)
    {
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Row(string label, Control c, int tab)
        {
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 0, 6) });
            c.Dock = DockStyle.Fill; c.Margin = new Padding(0, 3, 0, 3); c.TabIndex = tab;
            table.Controls.Add(c);
        }
        var files = new Label { Text = $"{System.IO.Path.GetFileName(stepFile)}\n{System.IO.Path.GetFileName(pdfFile)}", AutoSize = true, ForeColor = SystemColors.GrayText, TabStop = false };
        Row("Files", files, 100);
        Row("Vendor", _vendor, 0);
        Row("Subfolder", _sub, 1);
        Row("Vendor Number", _number, 2);
        Row("Description", _desc, 3);
        Row("Material", _material, 4);
        Row("Material Finish", _finish, 5);
        Row("Designer", _designer, 6);
        Row("Engr Approved By", _approved, 7);
        _designer.TabStop = false; _approved.TabStop = false;

        _will.Height = 54; _error.Height = 40;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label { Text = "Will create", AutoSize = true, Margin = new Padding(0, 6, 0, 6) });
        table.Controls.Add(_will);
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label());
        table.Controls.Add(_error);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 44, Padding = new Padding(8) };
        _cancel.TabIndex = 9; _import.TabIndex = 8;
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_import);
        Controls.Add(table);
        Controls.Add(buttons);
    }

    private void OnVendorChanged()
    {
        if (_loading) return;
        if (_vendor.SelectedItem as string == NewVendor)
        {
            var name = InputDialog.Ask(this, "New vendor", "Vendor folder name:");
            var error = "";
            if (name is not null && !ImportRules.IsValidName(name))
            {
                error = "Vendor: the name cannot contain / \\ : * ? \" < > | or end with a dot or space.";
                name = null;
            }
            _loading = true;
            if (name is null)
            {
                if (_vendorSel.Length > 0 && _vendor.Items.Contains(_vendorSel)) _vendor.SelectedItem = _vendorSel; else _vendor.SelectedIndex = -1;
            }
            else
            {
                var existing = _vendor.Items.Cast<string>().FirstOrDefault(v => string.Equals(v, name, StringComparison.OrdinalIgnoreCase) && v != NewVendor);
                if (existing is null) { _vendor.Items.Insert(_vendor.Items.Count - 1, name); existing = name; }
                _vendor.SelectedItem = existing;
                _vendorSel = existing;
            }
            _loading = false;
            _error.Text = error;
        }
        else { _vendorSel = _vendor.SelectedItem as string ?? ""; _error.Text = ""; }
        FillSubfolders();
    }

    private void FillSubfolders()
    {
        _loading = true;
        _sub.Items.Clear();
        _sub.Items.Add(VendorFolder);
        if (_vendorSel.Length > 0)
        {
            try { foreach (var s in VaultFiles.Subfolders(_vendorSel)) _sub.Items.Add(s); }
            catch (Exception ex) { _log($"import form: subfolders of {_vendorSel} failed: {ex.Message}"); }
        }
        _sub.Items.Add(NewSubfolder);
        _sub.SelectedIndex = 0;
        _subSel = "";
        _loading = false;
        UpdateWill();
    }

    private void OnSubChanged()
    {
        if (_loading) return;
        var sel = _sub.SelectedItem as string ?? VendorFolder;
        if (sel == NewSubfolder)
        {
            var name = InputDialog.Ask(this, "New subfolder", "Subfolder name (use / for nesting, e.g. screws/shoulder screws):");
            _loading = true;
            if (name is not null && name.Split('/').All(s => ImportRules.IsValidName(s) && !string.Equals(s, VendorFolder, StringComparison.OrdinalIgnoreCase)))
            {
                if (!_sub.Items.Contains(name)) _sub.Items.Insert(_sub.Items.Count - 1, name);
                _sub.SelectedItem = name; _subSel = name; _error.Text = "";
            }
            else
            {
                if (name is not null) _error.Text = "Subfolder: each level needs a name without \\ : * ? \" < > | and no trailing dot or space, and not \"(vendor folder)\".";
                _sub.SelectedItem = _subSel.Length == 0 ? VendorFolder : _subSel;
            }
            _loading = false;
        }
        else _subSel = sel == VendorFolder ? "" : sel;
        UpdateWill();
    }

    private ImportPlan CurrentPlan() =>
        new(_vendorSel, _subSel, _number.Text, _desc.Text, _material.Text.Trim(), _finish.Text.Trim(), _designer.Text);

    /// <summary>Shows CD-&lt;next&gt; literally: asking Vault for the real number would consume it.</summary>
    private void UpdateWill()
    {
        if (_vendorSel.Length == 0) { _will.Text = "Choose a vendor."; return; }
        var plan = CurrentPlan();
        _will.Text = $"{ImportRules.VaultFolder(plan)}/{ImportRules.PartFileName("CD-<next>")}\n{ImportRules.PdfFileName("CD-<next>", plan)}";
    }

    private void OnImport()
    {
        var plan = CurrentPlan();
        var errors = ImportRules.Validate(plan);
        if (errors.Count > 0) { _error.Text = string.Join(" ", errors); return; }
        Plan = plan with { VendorNumber = plan.VendorNumber.Trim() };
        _lastVendor = Plan.Vendor;
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>Small single-line prompt.</summary>
    private sealed class InputDialog : Form
    {
        private readonly TextBox _box = new();
        private InputDialog(string title, string prompt)
        {
            Text = title; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent; ClientSize = new Size(380, 120);
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(210, 84) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(295, 84) };
            Controls.Add(new Label { Text = prompt, Location = new Point(12, 10), Size = new Size(356, 36) });
            _box.Location = new Point(12, 50); _box.Width = 356;
            Controls.Add(_box); Controls.Add(ok); Controls.Add(cancel);
            AcceptButton = ok; CancelButton = cancel;
        }

        public static string? Ask(IWin32Window owner, string title, string prompt)
        {
            using var d = new InputDialog(title, prompt);
            return d.ShowDialog(owner) == DialogResult.OK ? d._box.Text.Trim() : null;
        }
    }
}
