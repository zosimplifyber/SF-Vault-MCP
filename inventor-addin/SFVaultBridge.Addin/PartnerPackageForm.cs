using System.Drawing;
using System.Windows.Forms;
using SFVaultBridge.Core;

namespace SFVaultBridge.Addin;

/// <summary>The Partner Package form: partner name, which parts, a live preview and the quote override. Never throws into Inventor.</summary>
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
    private readonly Dictionary<ManufacturingMode, (string Text, int Count)> _preview = new();
    private readonly ComboBox _partner = new() { DropDownStyle = ComboBoxStyle.DropDown };
    private readonly ComboBox _parts = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _quote = new() { Text = "For quote only (allow unreleased)", AutoSize = true, Checked = false };
    private readonly Label _previewLabel = new() { AutoSize = true };
    private readonly Label _error = new() { AutoSize = true, ForeColor = Color.Firebrick };
    private readonly Button _build = new() { Text = "Build", DialogResult = DialogResult.None };
    private readonly Button _cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel };

    public string? Partner { get; private set; }
    public ManufacturingMode Mode { get; private set; } = ManufacturingMode.AllMake;
    /// <summary>"For quote only" ticked. Never remembered between runs.</summary>
    public bool Quote { get; private set; }

    public PartnerPackageForm(IReadOnlyList<string> recentPartners, Func<ManufacturingMode, IReadOnlyList<PackagePart>> partsFor)
    {
        _partsFor = partsFor;
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
        Row("Partner", _partner, 0);
        Row("Parts", _parts, 1);

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
        _cancel.TabIndex = 4; _build.TabIndex = 3;
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_build);
        var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Location = Point.Empty };
        root.Controls.Add(table);
        root.Controls.Add(buttons);
        Controls.Add(root);
    }

    private void UpdatePreview()
    {
        var mode = Selected;
        if (!_preview.TryGetValue(mode, out var p))
        {
            try
            {
                var parts = _partsFor(mode);
                p = (PackageRules.PreviewText(parts, mode), parts.Count);
                _error.Text = "";
            }
            catch (Exception ex)
            {
                p = ("", 0);
                _error.Text = "Could not read the parts: " + ex.Message;
            }
            _preview[mode] = p;
        }
        _previewLabel.Text = p.Text;
        _build.Enabled = p.Count > 0;
    }

    private void OnBuild()
    {
        var mode = Selected;
        if (!_preview.TryGetValue(mode, out var p) || p.Count == 0) return;
        var error = PackageRules.PartnerError(_partner.Text);
        if (error is not null) { _error.Text = error; return; }
        Partner = _partner.Text.Trim();
        Mode = mode;
        Quote = _quote.Checked;
        DialogResult = DialogResult.OK;
        Close();
    }
}
