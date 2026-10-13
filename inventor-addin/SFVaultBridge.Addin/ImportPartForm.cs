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
    private const int InputWidth = 380;   // logical px; AutoScaleMode.Dpi scales it

    /// <summary>Last vendor chosen in this Inventor session.</summary>
    private static string? _lastVendor;

    private readonly Action<string> _log;
    private readonly Func<IReadOnlyList<string>> _vendorsFn;
    private readonly Func<string, IReadOnlyList<string>> _subfoldersFn;
    private readonly ComboBox _vendor = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _sub = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _number = new();
    private readonly TextBox _desc = new();
    private readonly ComboBox _material = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _finish = new() { Text = ImportRules.DefaultFinish };
    private readonly TextBox _designer = new() { ReadOnly = true };
    private readonly TextBox _approved = new() { ReadOnly = true, Text = "NOT REVIEWED" };
    private readonly Label _will = new() { AutoSize = true };
    private readonly Label _error = new() { AutoSize = true, ForeColor = Color.Firebrick };
    private readonly Button _import = new() { Text = "Import", DialogResult = DialogResult.None };
    private readonly Button _cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel };
    private string _vendorSel = "";
    private string _subSel = "";
    private bool _loading;

    public ImportPlan? Plan { get; private set; }

    public ImportPartForm(Inv.Application app, string stepFile, string pdfFile, Action<string> log)
        : this(DesignerOf(app, log), MaterialsOf(app, log), VaultFiles.LibraryVendors, VaultFiles.Subfolders, stepFile, pdfFile, log) { }

    private static string DesignerOf(Inv.Application app, Action<string> log)
    {
        try { return app.GeneralOptions.UserName ?? ""; }
        catch (Exception ex) { log($"import form: user name unavailable: {ex.Message}"); return ""; }
    }

    private static IReadOnlyList<string> MaterialsOf(Inv.Application app, Action<string> log)
    {
        var list = new List<string>();
        try { foreach (Inv.Asset a in app.ActiveMaterialLibrary.MaterialAssets) list.Add(a.DisplayName); }
        catch (Exception ex) { log($"import form: material list unavailable: {ex.Message}"); }
        return list;
    }

    /// <summary>Builds the form without Inventor or Vault (fake lists), for the layout preview harness.</summary>
    internal static ImportPartForm Preview(string designer, IReadOnlyList<string> materials, IReadOnlyList<string> vendors,
                                           IReadOnlyList<string> subfolders, string stepFile, string pdfFile) =>
        new(designer, materials, () => vendors, _ => subfolders, stepFile, pdfFile, _ => { });

    private ImportPartForm(string designer, IReadOnlyList<string> materials, Func<IReadOnlyList<string>> vendorsFn,
                           Func<string, IReadOnlyList<string>> subfoldersFn, string stepFile, string pdfFile, Action<string> log)
    {
        _log = log;
        _vendorsFn = vendorsFn;
        _subfoldersFn = subfoldersFn;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Import New Part";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        AcceptButton = _import; CancelButton = _cancel;
        Font = SystemFonts.MessageBoxFont ?? Font;
        _designer.Text = designer;

        var prefill = VendorFileNames.FromFiles(stepFile, pdfFile);
        _number.Text = prefill.VendorNumber;
        _desc.Text = prefill.Description;

        IReadOnlyList<string> vendors = Array.Empty<string>();
        try { vendors = _vendorsFn(); }
        catch (Exception ex) { log($"import form: vendor list failed: {ex}"); _error.Text = "Could not read the vendor folders from Vault: " + ex.Message; }
        _loading = true;
        foreach (var v in vendors) _vendor.Items.Add(v);
        _vendor.Items.Add(NewVendor);
        var pre = prefill.Vendor is not null ? vendors.FirstOrDefault(v => string.Equals(v, prefill.Vendor, StringComparison.OrdinalIgnoreCase)) : null;
        pre ??= _lastVendor is not null ? vendors.FirstOrDefault(v => string.Equals(v, _lastVendor, StringComparison.OrdinalIgnoreCase)) : null;
        if (pre is not null) { _vendor.SelectedItem = pre; _vendorSel = pre; }
        _loading = false;
        FillSubfolders();

        foreach (var m in materials) _material.Items.Add(m);
        var preferred = ImportRules.PreferredMaterial(materials);
        if (preferred is not null) _material.SelectedItem = preferred;
        else _error.Text = "No materials found in Inventor's active library; Material is required.";

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
        // Sizes are logical (96 dpi) pixels scaled by AutoScaleMode.Dpi; every label sizes itself from its text.
        var table = new TableLayoutPanel
        {
            ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Left | AnchorStyles.Right, Padding = new Padding(12, 12, 12, 4),
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        void Row(string label, Control c, int tab)
        {
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(0, 6, 12, 6) });
            c.Anchor = AnchorStyles.Left | AnchorStyles.Right; c.Margin = new Padding(0, 3, 0, 3); c.TabIndex = tab;
            c.Width = InputWidth;
            table.Controls.Add(c);
        }
        // Measured in device px, converted back to logical px: AutoScaleMode.Dpi then scales each control exactly once.
        var toLogical = 96f / DeviceDpi;
        var lineHeight = (int)Math.Ceiling(TextRenderer.MeasureText("Ag", Font).Height * toLogical);
        var filesText = System.IO.Path.GetFileName(stepFile) + "\r\n" + System.IO.Path.GetFileName(pdfFile);
        var wrapped = TextRenderer.MeasureText(filesText, Font, new Size(LogicalToDeviceUnits(InputWidth), 0),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl);
        var files = new TextBox
        {
            Text = filesText,
            Multiline = true, ReadOnly = true, WordWrap = true, BorderStyle = BorderStyle.None, TabStop = false,
            ScrollBars = ScrollBars.None, BackColor = SystemColors.Control, ForeColor = SystemColors.GrayText,
            Height = (int)Math.Ceiling(wrapped.Height * toLogical) + 6,
        };
        new ToolTip().SetToolTip(files, stepFile + "\n" + pdfFile);
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

        _will.MaximumSize = new Size(InputWidth, 0);
        _error.MaximumSize = new Size(InputWidth, 0);
        _will.Margin = new Padding(0, 6, 0, 6);
        _error.Margin = new Padding(0, 3, 0, 3);
        _error.MinimumSize = new Size(0, lineHeight);
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label { Text = "Will create", AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(0, 6, 12, 6) });
        table.Controls.Add(_will);
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label { AutoSize = true, Margin = Padding.Empty });
        table.Controls.Add(_error);

        var buttons = new FlowLayoutPanel
        {
            Anchor = AnchorStyles.Right, FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 4, 8, 8),
        };
        foreach (var b in new[] { _cancel, _import })
        {
            b.AutoSize = true; b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.MinimumSize = new Size(80, 0); b.Margin = new Padding(6, 3, 6, 3);
        }
        _cancel.TabIndex = 9; _import.TabIndex = 8;
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_import);
        var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Location = Point.Empty };
        root.Controls.Add(table);
        root.Controls.Add(buttons);
        Controls.Add(root);
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
            try { foreach (var s in _subfoldersFn(_vendorSel)) _sub.Items.Add(s); }
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
        new(_vendorSel, _subSel, _number.Text, _desc.Text, (_material.SelectedItem as string ?? "").Trim(), ImportRules.NormalizeFinish(_finish.Text), _designer.Text);

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
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = SystemFonts.MessageBoxFont ?? Font;
            Text = title; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(80, 0) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(80, 0) };
            var table = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Padding = new Padding(12) };
            table.Controls.Add(new Label { Text = prompt, AutoSize = true, MaximumSize = new Size(InputWidth, 0), Margin = new Padding(0, 0, 0, 6) });
            _box.MinimumSize = new Size(InputWidth, 0); _box.Dock = DockStyle.Fill;
            table.Controls.Add(_box);
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
            buttons.Controls.Add(cancel); buttons.Controls.Add(ok);
            table.Controls.Add(buttons);
            Controls.Add(table);
            AcceptButton = ok; CancelButton = cancel;
        }

        public static string? Ask(IWin32Window owner, string title, string prompt)
        {
            using var d = new InputDialog(title, prompt);
            return d.ShowDialog(owner) == DialogResult.OK ? d._box.Text.Trim() : null;
        }
    }
}
