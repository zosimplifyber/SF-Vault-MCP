using System.Drawing;
using System.Windows.Forms;
using SFVaultBridge.Core;

namespace SFVaultBridge.Addin;

/// <summary>
/// The Partner Package form: partner name, which parts, a live preview and the quote override. For a selected or single-part scope it also
/// shows the scope line and a grid (Part Number, Description, Mode files, editable Quantity). Never throws into Inventor.
/// </summary>
internal sealed class PartnerPackageForm : Form
{
    private const int InputWidth = 380;   // logical px; AutoScaleMode.Dpi scales it
    private static readonly (string Label, ManufacturingMode Mode)[] Modes =
    {
        ("All Make parts", ManufacturingMode.AllMake),
        ("CNC (machined)", ManufacturingMode.Cnc),
        ("Sheet metal", ManufacturingMode.SheetMetal),
    };

    private readonly Func<ManufacturingMode, IReadOnlyList<PackagePart>> _partsFor;
    private readonly Dictionary<ManufacturingMode, (string Text, int Count, string Error)> _preview = new();
    private readonly string _dropboxAppKey;
    private readonly ComboBox _partner = new() { DropDownStyle = ComboBoxStyle.DropDown };
    private readonly ComboBox _parts = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _quote = new() { Text = "For quote only (allow unreleased)", AutoSize = true, Checked = false };
    private readonly CheckBox _upload = new() { Text = "Upload to Dropbox /Suppliers/<Partner>/", AutoSize = true };
    private readonly Label _uploadHint = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly Button _signOut = new() { Text = "Sign out of Dropbox", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    private readonly Label _previewLabel = new() { AutoSize = true };
    private readonly Label _error = new() { AutoSize = true, ForeColor = Color.Firebrick };
    private readonly Button _build = new() { Text = "Build", DialogResult = DialogResult.None };
    private readonly Button _cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel };

    // Selected / single-part scope only.
    private readonly string _scopeLine;
    private readonly Func<ManufacturingMode, IReadOnlyList<ScopeRow>>? _rowsFor;
    private readonly IReadOnlyList<string> _skipped;
    private readonly Dictionary<ManufacturingMode, IReadOnlyList<ScopeRow>> _rowCache = new();
    private readonly Dictionary<string, string> _qtyText = new(StringComparer.OrdinalIgnoreCase);
    private readonly DataGridView _grid = new();
    private readonly Label _skippedLabel = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private bool _filling;
    private bool Scoped => _rowsFor is not null;

    public string? Partner { get; private set; }
    public ManufacturingMode Mode { get; private set; } = ManufacturingMode.AllMake;
    /// <summary>"For quote only" ticked. Never remembered between runs.</summary>
    public bool Quote { get; private set; }
    /// <summary>"Upload to Dropbox" ticked (only possible when a dropboxAppKey is set).</summary>
    public bool Upload { get; private set; }
    /// <summary>Selected / single-part scope: the entered quantity for each included part, keyed by the part's file path.</summary>
    public Dictionary<string, int> Quantities { get; } = new(StringComparer.OrdinalIgnoreCase);

    public PartnerPackageForm(IReadOnlyList<string> recentPartners, Func<ManufacturingMode, IReadOnlyList<PackagePart>> partsFor, string dropboxAppKey = "",
                                Func<ManufacturingMode, IReadOnlyList<ScopeRow>>? rowsFor = null, string scopeLine = "",
                                IReadOnlyList<string>? skipped = null)
    {
        _partsFor = partsFor;
        _rowsFor = rowsFor; _scopeLine = scopeLine; _skipped = skipped ?? Array.Empty<string>();
        _dropboxAppKey = (dropboxAppKey ?? "").Trim();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Partner Package";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        AcceptButton = _build; CancelButton = _cancel;
        Font = SystemFonts.MessageBoxFont ?? Font;

        foreach (var p in recentPartners) _partner.Items.Add(p);
        _partner.Text = "";
        foreach (var m in Modes) _parts.Items.Add(m.Label);
        _parts.SelectedIndex = 0;

        var keySet = _dropboxAppKey.Length > 0;
        _upload.Enabled = keySet;
        _upload.Checked = keySet;
        _uploadHint.Text = keySet ? "" : "Set dropboxAppKey in settings.json to turn this on.";
        _signOut.Enabled = keySet && SafeSignedIn();
        _signOut.Click += (_, _) => Safe(OnSignOut);
        BuildLayout();
        _parts.SelectedIndexChanged += (_, _) => Safe(UpdatePreview);
        _build.Click += (_, _) => Safe(OnBuild);
        Safe(UpdatePreview);
    }

    /// <summary>Builds the form without Inventor or Vault, for the layout preview harness.</summary>
    internal static PartnerPackageForm Preview(IReadOnlyList<string> recent, Func<ManufacturingMode, IReadOnlyList<PackagePart>> partsFor) =>
        new(recent, partsFor);

    private ManufacturingMode Selected => Modes[Math.Max(0, _parts.SelectedIndex)].Mode;

    private void Safe(Action a)
    {
        try { a(); }
        catch (Exception ex) { _error.Text = "Unexpected error: " + ex.Message; }
    }

    private void BuildLayout()
    {
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
        if (Scoped)
        {
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var scope = new Label { Text = _scopeLine, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 0, 0, 6) };
            table.Controls.Add(scope);
            table.SetColumnSpan(scope, 2);
        }
        Row("Partner", _partner, 0);
        Row("Parts", _parts, 1);

        if (Scoped)
        {
            ConfigureGrid();
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _grid.Margin = new Padding(0, 6, 0, 3);
            table.Controls.Add(_grid);
            table.SetColumnSpan(_grid, 2);
            _skippedLabel.MaximumSize = new Size(GridWidth, 0);
            _skippedLabel.Margin = new Padding(0, 0, 0, 3);
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(_skippedLabel);
            table.SetColumnSpan(_skippedLabel, 2);
            _skippedLabel.Text = string.Join(Environment.NewLine, _skipped);
            _skippedLabel.Visible = _skipped.Count > 0;
        }

        _previewLabel.MaximumSize = new Size(InputWidth, 0);
        _error.MaximumSize = new Size(InputWidth, 0);
        _previewLabel.Margin = new Padding(0, 6, 0, 6);
        _error.Margin = new Padding(0, 3, 0, 3);
        var toLogical = 96f / DeviceDpi;
        _error.MinimumSize = new Size(0, (int)Math.Ceiling(TextRenderer.MeasureText("Ag", Font).Height * toLogical));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label { Text = "Preview", AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(0, 6, 12, 6) });
        table.Controls.Add(_previewLabel);

        _quote.TabIndex = 2; _quote.Margin = new Padding(0, 3, 0, 3);
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label { AutoSize = true, Margin = Padding.Empty });
        table.Controls.Add(_quote);
        _upload.TabIndex = 3; _upload.Margin = new Padding(0, 3, 0, 3);
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label { AutoSize = true, Margin = Padding.Empty });
        table.Controls.Add(_upload);
        _uploadHint.MaximumSize = new Size(InputWidth, 0);
        if (_uploadHint.Text.Length > 0)
        {
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(new Label { AutoSize = true, Margin = Padding.Empty });
            table.Controls.Add(_uploadHint);
        }
        new ToolTip().SetToolTip(_quote, "Builds the package even when parts or drawings are not Released. Every PDF page is stamped FOR QUOTE - NOT FOR PRODUCTION and the zip is named Quote.");

        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label { AutoSize = true, Margin = Padding.Empty });
        table.Controls.Add(_error);

        var buttons = new FlowLayoutPanel
        {
            Anchor = AnchorStyles.Right, FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 4, 8, 8),
        };
        foreach (var b in new[] { _cancel, _build })
        {
            b.AutoSize = true; b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.MinimumSize = new Size(80, 0); b.Margin = new Padding(6, 3, 6, 3);
        }
        _signOut.MinimumSize = new Size(80, 0); _signOut.Margin = new Padding(6, 3, 6, 3); _signOut.TabIndex = 6;
        _cancel.TabIndex = 5; _build.TabIndex = 4;
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_build);
        buttons.Controls.Add(_signOut);
        var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Location = Point.Empty };
        root.Controls.Add(table);
        root.Controls.Add(buttons);
        Controls.Add(root);
    }

    private const int GridWidth = 600;
    private const int ColPart = 0, ColDescription = 1, ColFiles = 2, ColQty = 3, ColNote = 4;

    private void ConfigureGrid()
    {
        _grid.AllowUserToAddRows = false; _grid.AllowUserToDeleteRows = false; _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersVisible = false; _grid.MultiSelect = false; _grid.EditMode = DataGridViewEditMode.EditOnEnter;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        _grid.BackgroundColor = SystemColors.Window; _grid.BorderStyle = BorderStyle.FixedSingle;
        _grid.TabIndex = 7;
        void Col(string header, int width, bool readOnly)
        {
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = header, Width = width, ReadOnly = readOnly, SortMode = DataGridViewColumnSortMode.NotSortable });
        }
        Col("Part Number", 110, true); Col("Description", 180, true); Col("Mode files", 90, true); Col("Quantity", 70, false); Col("Note", 140, true);
        _grid.CellEndEdit += (_, e) => Safe(() => { if (!_filling && e.ColumnIndex == ColQty) RememberQty(e.RowIndex); });
        _grid.Size = new Size(GridWidth, 120);
    }

    private IReadOnlyList<ScopeRow> RowsFor(ManufacturingMode mode)
    {
        if (!_rowCache.TryGetValue(mode, out var rows)) _rowCache[mode] = rows = _rowsFor!(mode);
        return rows;
    }

    private static string Key(ScopeRow r) => r.Candidate.Node.FullPath ?? "";

    private static string DefaultText(ScopeRow r) => r.DefaultQty.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private void RememberQty(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= _grid.Rows.Count || _grid.Rows[rowIndex].Tag is not string key) return;
        var row = _grid.Rows[rowIndex];
        if (row.Cells[ColQty].ReadOnly) return;
        _qtyText[key] = Convert.ToString(row.Cells[ColQty].Value) ?? "";
    }

    private void FillGrid(IReadOnlyList<ScopeRow> rows)
    {
        _filling = true;
        try
        {
            _grid.Rows.Clear();
            foreach (var r in rows)
            {
                var qty = _qtyText.TryGetValue(Key(r), out var t) ? t : DefaultText(r);
                var i = _grid.Rows.Add(r.Candidate.Node.PartNumber, r.Candidate.Node.Description, PackageScope.FilesText(r.Files), r.Included ? qty : "", r.Note ?? "");
                var row = _grid.Rows[i];
                row.Tag = Key(r);
                if (!r.Included)
                {
                    row.Cells[ColQty].ReadOnly = true;
                    row.DefaultCellStyle.ForeColor = SystemColors.GrayText;
                    row.DefaultCellStyle.BackColor = SystemColors.Control;
                }
            }
            var visible = Math.Max(1, Math.Min(rows.Count, 8));
            _grid.Height = (int)((_grid.ColumnHeadersHeight + visible * 22 + 4) * 96f / DeviceDpi);
        }
        finally { _filling = false; }
    }

    private void UpdateScopedPreview()
    {
        var mode = Selected;
        IReadOnlyList<ScopeRow> rows;
        try { rows = RowsFor(mode); }
        catch (Exception ex)
        {
            _error.Text = "Could not read the parts: " + ex.Message;
            _previewLabel.Text = ""; _build.Enabled = false; _grid.Rows.Clear();
            return;
        }
        _error.Text = "";
        FillGrid(rows);
        var parts = PackageScope.ToParts(rows, _ => null);
        _previewLabel.Text = PackageRules.PreviewText(parts, mode);
        _build.Enabled = parts.Count > 0;
    }

    private void UpdatePreview()
    {
        if (Scoped) { UpdateScopedPreview(); return; }
        var mode = Selected;
        if (!_preview.TryGetValue(mode, out var p))
        {
            try
            {
                var parts = _partsFor(mode);
                p = (PackageRules.PreviewText(parts, mode), parts.Count, "");
            }
            catch (Exception ex)
            {
                p = ("", 0, "Could not read the parts: " + ex.Message);
            }
            _preview[mode] = p;
        }
        _error.Text = p.Error;
        _previewLabel.Text = p.Text;
        _build.Enabled = p.Count > 0;
    }

    private static bool SafeSignedIn()
    {
        try { return System.IO.File.Exists(DropboxAuth.DefaultTokenPath); } catch { return false; }
    }

    private void OnSignOut()
    {
        try
        {
            using var http = DropboxClient.NewHttpClient();
            new DropboxAuth(_dropboxAppKey, http).SignOut();
            _signOut.Enabled = false;
            _error.Text = "Signed out of Dropbox.";
        }
        catch (Exception ex) { _error.Text = "Could not sign out of Dropbox: " + ex.Message; }
    }

    private void OnBuild()
    {
        var mode = Selected;
        if (Scoped)
        {
            _grid.EndEdit();
            for (var i = 0; i < _grid.Rows.Count; i++) RememberQty(i);
        }
        else if (!_preview.TryGetValue(mode, out var p) || p.Count == 0) return;
        var error = PackageRules.PartnerError(_partner.Text);
        if (error is not null) { _error.Text = error; return; }
        Quantities.Clear();
        if (Scoped)
        {
            var included = RowsFor(mode).Where(r => r.Included).ToList();
            if (included.Count == 0) return;
            foreach (var r in included)
            {
                var (value, qtyError) = PackageScope.ParseQty(_qtyText.TryGetValue(Key(r), out var t) ? t : DefaultText(r));
                if (value is null) { _error.Text = $"{r.Candidate.Node.PartNumber}: {qtyError}"; Quantities.Clear(); return; }
                Quantities[Key(r)] = value.Value;
            }
        }
        Partner = _partner.Text.Trim();
        Mode = mode;
        Quote = _quote.Checked;
        Upload = _upload.Enabled && _upload.Checked;
        DialogResult = DialogResult.OK;
        Close();
    }
}
