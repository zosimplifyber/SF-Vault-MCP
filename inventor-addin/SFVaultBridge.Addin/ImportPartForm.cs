using System.Drawing;
using System.Windows.Forms;
using SFVaultBridge.Core;
using SFVaultBridge.Core.McMaster;
using Inv = Inventor;

namespace SFVaultBridge.Addin;

/// <summary>
/// The Import New Part form. The vendor STEP and PDF come from "Choose files..." or from a McMaster number (Fetch: get_product,
/// get_price, get_cad STEP and PDF, saved under Downloads\SF Vault Bridge\McMaster\&lt;PN&gt;, then the fields are prefilled).
/// Never throws into Inventor: failures show a message and are logged.
/// </summary>
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
    private readonly TextBox _comments = new() { Multiline = true, WordWrap = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };
    private readonly ComboBox _material = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _finish = new() { Text = ImportRules.DefaultFinish };
    private readonly TextBox _designer = new() { ReadOnly = true };
    private readonly TextBox _approved = new() { ReadOnly = true, Text = "NOT REVIEWED" };
    private readonly Label _will = new() { AutoSize = true };
    private readonly Label _error = new() { AutoSize = true, ForeColor = Color.Firebrick };
    private readonly TextBox _mcNumber = new() { Width = 160 };
    private readonly Button _fetch = new() { Text = "Fetch", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    private readonly Label _mcInfo = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly TextBox _files = new()
    {
        Multiline = true, ReadOnly = true, WordWrap = true, BorderStyle = BorderStyle.None, TabStop = false,
        ScrollBars = ScrollBars.None, BackColor = SystemColors.Control, ForeColor = SystemColors.GrayText,
    };
    private readonly Button _choose = new() { Text = "Choose files...", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    private readonly ToolTip _filesTip = new();
    private readonly IReadOnlyList<string> _materials;
    private readonly Func<(McMasterAuth Auth, IMcMasterTools Tools)>? _mcmaster;
    private readonly CancellationTokenSource _closing = new();
    private string? _stepFile;
    private string? _pdfFile;
    private (string PartNumber, double? Cost, int? PackSize)? _fetched;
    private bool _fetching;
    private int _fileLineHeight = 16;
    private readonly Button _import = new() { Text = "Import", DialogResult = DialogResult.None };
    private readonly Button _cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel };
    private string _vendorSel = "";
    private string _subSel = "";
    private bool _loading;

    public ImportPlan? Plan { get; private set; }

    /// <summary>The STEP and PDF the import uses (chosen or fetched).</summary>
    public string? StepFile => _stepFile;
    public string? PdfFile => _pdfFile;

    public ImportPartForm(Inv.Application app, string? stepFile, string? pdfFile, Action<string> log, string mcmasterServerUrl)
        : this(DesignerOf(app, log), MaterialsOf(app, log), VaultFiles.LibraryVendors, VaultFiles.Subfolders, stepFile, pdfFile, log,
               () => McMasterSession.For(mcmasterServerUrl, log)) { }

    private static string DesignerOf(Inv.Application app, Action<string> log)
    {
        try { return ImportRules.DesignerName(app.GeneralOptions.UserName); }
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
                                           IReadOnlyList<string> subfolders, string? stepFile, string? pdfFile) =>
        new(designer, materials, () => vendors, _ => subfolders, stepFile, pdfFile, _ => { }, null);

    private ImportPartForm(string designer, IReadOnlyList<string> materials, Func<IReadOnlyList<string>> vendorsFn,
                           Func<string, IReadOnlyList<string>> subfoldersFn, string? stepFile, string? pdfFile, Action<string> log,
                           Func<(McMasterAuth Auth, IMcMasterTools Tools)>? mcmaster)
    {
        _log = log;
        _materials = materials;
        _mcmaster = mcmaster;
        _stepFile = string.IsNullOrWhiteSpace(stepFile) ? null : stepFile;
        _pdfFile = string.IsNullOrWhiteSpace(pdfFile) ? null : pdfFile;
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

        var prefill = _stepFile is null ? new Prefill(null, "", "") : VendorFileNames.FromFiles(_stepFile, _pdfFile);
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

        Build();
        UpdateFiles();
        _vendor.SelectedIndexChanged += (_, _) => Safe(OnVendorChanged);
        _sub.SelectedIndexChanged += (_, _) => Safe(OnSubChanged);
        _number.TextChanged += (_, _) => Safe(UpdateWill);
        _import.Click += (_, _) => Safe(OnImport);
        _fetch.Click += (_, _) => Safe(OnFetch);
        _choose.Click += (_, _) => Safe(OnChooseFiles);
        _mcNumber.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Safe(OnFetch); } };
        if (_mcmaster is null) { _fetch.Enabled = false; _mcInfo.Text = "McMaster Fetch is not available here."; }
        FormClosing += (_, _) => { try { _closing.Cancel(); } catch (ObjectDisposedException) { } };
        UpdateWill();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _closing.Dispose(); _filesTip.Dispose(); }
        base.Dispose(disposing);
    }

    private void Safe(Action a)
    {
        try { a(); }
        catch (Exception ex) { _log($"import form: {ex}"); _error.Text = "Unexpected error: " + ex.Message; }
    }

    private void Build()
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
        var mcRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false };
        _mcNumber.Margin = new Padding(0, 0, 6, 0);
        _fetch.Margin = Padding.Empty;
        mcRow.Controls.Add(_mcNumber); mcRow.Controls.Add(_fetch);
        Row("McMaster number", mcRow, 0);
        _mcNumber.TabIndex = 0; _fetch.TabIndex = 1;
        new ToolTip().SetToolTip(_mcNumber, "Optional. Type a McMaster-Carr part number and press Fetch to download its STEP and PDF and fill in the fields.");
        _mcInfo.MaximumSize = new Size(InputWidth, 0);
        _mcInfo.Margin = new Padding(0, 0, 0, 6);
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label { AutoSize = true, Margin = Padding.Empty });
        table.Controls.Add(_mcInfo);
        var filesPanel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false };
        _files.Width = InputWidth;
        _files.Margin = Padding.Empty;
        _choose.Margin = new Padding(0, 2, 0, 0);
        filesPanel.Controls.Add(_files); filesPanel.Controls.Add(_choose);
        Row("Files", filesPanel, 2);
        _fileLineHeight = lineHeight;
        Row("Vendor", _vendor, 3);
        Row("Subfolder", _sub, 4);
        Row("Vendor Number", _number, 5);
        Row("Description", _desc, 6);
        _comments.Height = lineHeight * 3 + 8;
        Row("Comments", _comments, 7);
        new ToolTip().SetToolTip(_comments, "Optional. Extra description lines, for example McMaster's second line. Goes to the Comments iProperty.");
        Row("Material", _material, 8);
        Row("Material Finish", _finish, 9);
        Row("Designer", _designer, 10);
        Row("Engr Approved By", _approved, 11);
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
        _cancel.TabIndex = 13; _import.TabIndex = 12;
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

    /// <summary>The Estimated Cost and Pack Size from a Fetch go with the plan only while Vendor Number still names the fetched part.</summary>
    private ImportPlan CurrentPlan()
    {
        var keep = _fetched is { } x && ImportRules.NumberKey(_number.Text) == ImportRules.NumberKey(x.PartNumber);
        return new(_vendorSel, _subSel, _number.Text, _desc.Text, (_material.SelectedItem as string ?? "").Trim(), ImportRules.NormalizeFinish(_finish.Text),
                   _designer.Text, ImportRules.CleanComments(_comments.Text), keep ? _fetched!.Value.Cost : null, keep ? _fetched!.Value.PackSize : null);
    }

    /// <summary>Shows CD-&lt;next&gt; literally: asking Vault for the real number would consume it.</summary>
    private void UpdateWill()
    {
        if (_vendorSel.Length == 0) { _will.Text = "Choose a vendor."; return; }
        var plan = CurrentPlan();
        _will.Text = $"{ImportRules.VaultFolder(plan)}/{ImportRules.PartFileName("CD-<next>")}\n{ImportRules.PdfFileName("CD-<next>", plan)}";
    }

    private void OnImport()
    {
        if (_fetching) { _error.Text = "Wait for the McMaster Fetch to finish."; return; }
        if (_stepFile is null || _pdfFile is null) { _error.Text = "Choose the vendor STEP and PDF, or type a McMaster number and press Fetch."; return; }
        var plan = CurrentPlan();
        var errors = ImportRules.Validate(plan);
        if (errors.Count > 0) { _error.Text = string.Join(" ", errors); return; }
        Plan = plan with { VendorNumber = plan.VendorNumber.Trim() };
        _lastVendor = Plan.Vendor;
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>Shows the chosen file names (two lines, wrapped) with the full paths in a tooltip.</summary>
    private void UpdateFiles()
    {
        var text = _stepFile is null ? "No files yet: choose them, or fetch them by McMaster number."
            : System.IO.Path.GetFileName(_stepFile) + "\r\n" + System.IO.Path.GetFileName(_pdfFile);
        _files.Text = text;
        var toLogical = 96f / DeviceDpi;
        var wrapped = TextRenderer.MeasureText(text, Font, new Size(LogicalToDeviceUnits(InputWidth), 0),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl);
        _files.Height = Math.Max(_fileLineHeight, (int)Math.Ceiling(wrapped.Height * toLogical)) + 6;
        _filesTip.SetToolTip(_files, _stepFile is null ? "" : _stepFile + "\n" + _pdfFile);
    }

    /// <summary>The picker the ribbon used before: one STEP and one PDF from Downloads. Blank fields are prefilled from the names.</summary>
    private void OnChooseFiles()
    {
        var downloads = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        while (true)
        {
            using var dlg = new OpenFileDialog
            {
                Title = "Select the vendor STEP and PDF",
                InitialDirectory = System.IO.Directory.Exists(downloads) ? downloads : "",
                Filter = "Vendor files (*.stp;*.step;*.pdf)|*.stp;*.step;*.pdf",
                Multiselect = true,
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            var check = SelectionCheck.Check(dlg.FileNames);
            if (check.Error is not null) { MessageBox.Show(this, check.Error, Text); continue; }
            _stepFile = check.Step; _pdfFile = check.Pdf;
            break;
        }
        var prefill = VendorFileNames.FromFiles(_stepFile!, _pdfFile);
        if (_number.Text.Trim().Length == 0) _number.Text = prefill.VendorNumber;
        if (_desc.Text.Trim().Length == 0) _desc.Text = prefill.Description;
        if (_vendorSel.Length == 0 && prefill.Vendor is not null) SelectVendor(prefill.Vendor);
        UpdateFiles();
        _error.Text = "";
        UpdateWill();
    }

    /// <summary>Selects the vendor folder (adding it to the list when Vault has no such folder yet) without the New vendor prompt.</summary>
    private void SelectVendor(string name)
    {
        var existing = _vendor.Items.Cast<string>().FirstOrDefault(v => v != NewVendor && string.Equals(v, name, StringComparison.OrdinalIgnoreCase));
        _loading = true;
        if (existing is null) { _vendor.Items.Insert(_vendor.Items.Count - 1, name); existing = name; }
        _vendor.SelectedItem = existing;
        _loading = false;
        if (_vendorSel != existing) { _vendorSel = existing; FillSubfolders(); }
    }

    private void SetFetching(bool on)
    {
        _fetching = on;
        _fetch.Enabled = !on && _mcmaster is not null;
        _mcNumber.Enabled = !on;
        _choose.Enabled = !on;
        _import.Enabled = !on;
    }

    private sealed record FetchResult(McMasterPrefill Prefill, string Step, string Pdf, string? PriceError);

    private async void OnFetch()
    {
        if (_fetching || _mcmaster is null) return;
        var pn = McMasterImport.NormalizePartNumber(_mcNumber.Text);
        if (pn is null) { _error.Text = "Type a McMaster part number first, for example 91251A054."; return; }
        _error.Text = "";
        _mcNumber.Text = pn;
        SetFetching(true);
        _mcInfo.Text = $"Fetching {pn} from McMaster...";
        McMasterAuth? auth = null;
        void OnStatus(string text)
        {
            try { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => { if (!IsDisposed) _mcInfo.Text = text; }); }
            catch (InvalidOperationException) { /* the form is closing */ }
        }
        try
        {
            var session = _mcmaster();
            auth = session.Auth;
            auth.Status += OnStatus;
            var materials = _materials;
            var ct = _closing.Token;
            var result = await Task.Run(() => FetchAsync(session.Tools, pn, materials, ct));
            if (IsDisposed) return;
            ApplyFetch(pn, result);
        }
        catch (Exception ex)
        {
            _log($"import fetch {pn}: {ex.GetType().Name}: {McMasterSession.Describe(ex)}");
            if (IsDisposed) return;
            _mcInfo.Text = "";
            _error.Text = "McMaster Fetch failed: " + McMasterSession.Describe(ex);
        }
        finally
        {
            if (auth is not null) auth.Status -= OnStatus;
            if (!IsDisposed) SetFetching(false);
        }
    }

    /// <summary>get_product, get_price (optional), then the STEP and the PDF one after the other into Downloads\SF Vault Bridge\McMaster\&lt;PN&gt;.</summary>
    private static async Task<FetchResult> FetchAsync(IMcMasterTools tools, string pn, IReadOnlyList<string> materials, CancellationToken ct)
    {
        var product = await tools.GetProductAsync(pn, ct).ConfigureAwait(false);
        if (product.PartNumber.Length == 0) product = product with { PartNumber = pn };
        McMasterPrice? price = null;
        string? priceError = null;
        try { price = await tools.GetPriceAsync(pn, 1, ct).ConfigureAwait(false); }
        catch (McMasterToolException e) { priceError = e.Message; }
        var stepFormat = McMasterImport.PickFormat(product.CadFormats, McMasterImport.StepFormat, "STEP")
            ?? throw new McMasterToolException($"McMaster has no STEP model for {pn}. Download the files by hand and use Choose files.");
        var pdfFormat = McMasterImport.PickFormat(product.CadFormats, McMasterImport.PdfFormat, "PDF")
            ?? throw new McMasterToolException($"McMaster has no 2-D PDF for {pn}. Download the files by hand and use Choose files.");
        var folder = McMasterImport.DownloadFolder(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), pn);
        var stem = McMasterImport.FileStem(pn, product.Family);
        var step = System.IO.Path.Combine(folder, stem + ".step");
        var pdf = System.IO.Path.Combine(folder, stem + ".pdf");
        var stepLink = await tools.GetCadAsync(pn, stepFormat, ct).ConfigureAwait(false);
        await tools.DownloadAsync(stepLink.Url, step, ct).ConfigureAwait(false);
        var pdfLink = await tools.GetCadAsync(pn, pdfFormat, ct).ConfigureAwait(false);
        await tools.DownloadAsync(pdfLink.Url, pdf, ct).ConfigureAwait(false);
        return new FetchResult(McMasterImport.Prefill(product, price, materials), step, pdf, priceError);
    }

    private void ApplyFetch(string pn, FetchResult r)
    {
        var f = r.Prefill;
        SelectVendor(McMasterImport.VendorFolder);
        _number.Text = f.VendorNumber.Length > 0 ? f.VendorNumber : pn;
        _desc.Text = f.Description;
        _comments.Text = f.Comments;
        var hints = new List<string>();
        if (f.Material.Material is { } m && _material.Items.Contains(m)) _material.SelectedItem = m;
        else { _material.SelectedIndex = -1; hints.Add(f.Material.Hint ?? "Choose the material."); }
        _finish.Text = f.Material.Finish;
        _stepFile = r.Step; _pdfFile = r.Pdf;
        _fetched = (_number.Text, f.EstimatedCost, f.PackSize);
        UpdateFiles();
        var lines = new List<string> { f.Summary };
        if (f.EstimatedCost is { } cost)
            lines.Add("Estimated Cost " + cost.ToString("$0.00##", System.Globalization.CultureInfo.InvariantCulture) + " each" + (f.PackSize is > 1 ? $", Pack Size {f.PackSize}" : ""));
        if (r.PriceError is not null) hints.Add("No McMaster price: " + r.PriceError);
        else if (f.CostHint is not null) hints.Add(f.CostHint);
        lines.AddRange(hints);
        lines.Add("McMaster CAD is for Simplifyber's own use; supplier packages leave it out.");
        _mcInfo.Text = string.Join("\n", lines);
        UpdateWill();
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
