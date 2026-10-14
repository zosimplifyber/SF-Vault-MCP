using System.Drawing;
using System.Windows.Forms;
using System.Text.Json.Nodes;
using SFVaultBridge.Core;

namespace SFVaultBridge.Addin;

/// <summary>One part the Fix Data Card form was opened with. Problem is set when its Vault path could not be worked out.</summary>
internal sealed record FixCardSource(string VaultPath, string Name, string? Problem);

/// <summary>
/// The Fix Data Card form: one grid row per part, a "now" and a "new" column per data card field, Preview, then Apply.
/// Runs on Inventor's UI thread through the same host the HTTP operation uses; each part is one message-pump turn so Inventor stays responsive.
/// Never throws into Inventor: failures show in the row or the status line and are logged.
/// </summary>
internal sealed class FixDataCardForm : Form
{
    private const int FileCol = 0;
    private const int StatusCol = 1;
    private const int AttachCol = 2;
    private const int FirstFieldCol = 3;

    private static readonly Color ChangedBack = Color.FromArgb(255, 249, 196);

    private sealed class RowData
    {
        public FixCardSource Source = null!;
        public FixCardLoaded? Loaded;
        public FixCardOutcome? Outcome;
        public string? Problem;
    }

    private readonly IDataCardHost _host;
    private readonly IAttachPdfHost? _attach;
    private readonly IVendorPdfFetcher? _fetcher;
    private readonly IReadOnlyList<string> _processValues;
    private readonly Action<string> _log;
    private readonly List<RowData> _rows = new();
    private readonly DataGridView _grid = new();
    private readonly CheckBox _revise = new() { Text = "Revise and re-release Released parts", AutoSize = true };
    private readonly Label _status = new() { AutoSize = true };
    private readonly TextBox _detail = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, TabStop = false };
    private readonly Button _preview = new() { Text = "Preview", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    private readonly Button _apply = new() { Text = "Apply", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    private readonly Button _attachPdf = new() { Text = "Attach McMaster PDF", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Enabled = false };
    private readonly Button _copy = new() { Text = "Copy results", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    private readonly Button _stop = new() { Text = "Stop", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Enabled = false };
    private readonly Button _close = new() { Text = "Close", DialogResult = DialogResult.Cancel, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    private bool _busy;
    private bool _loaded;
    private bool _previewed;
    private bool _applied;
    private bool _filling;
    private bool _stopRequested;
    private IReadOnlyList<IReadOnlyDictionary<PropRef, string>>? _snapshot;
    private bool _snapshotRevise;
    private readonly Dictionary<int, string> _autoTitle = new();

    public FixDataCardForm(IDataCardHost host, IReadOnlyList<FixCardSource> sources, IReadOnlyList<string> skippedNotes,
                           IReadOnlyList<string> processValues, Action<string> log, IAttachPdfHost? attach = null, IVendorPdfFetcher? fetcher = null)
    {
        _host = host; _processValues = processValues; _log = log; _attach = attach; _fetcher = fetcher;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Fix Data Card";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        CancelButton = _close;
        Font = SystemFonts.MessageBoxFont ?? Font;

        BuildGrid();
        foreach (var s in sources) AddRow(s);
        BuildLayout(skippedNotes);
        _preview.Enabled = _apply.Enabled = _copy.Enabled = false;
        _status.Text = "Reading " + _rows.Count + (_rows.Count == 1 ? " part" : " parts") + " from Vault...";

        _grid.CellValueChanged += (_, e) => Safe(() => OnCellChanged(e.RowIndex, e.ColumnIndex));
        _grid.CurrentCellDirtyStateChanged += (_, _) => { if (_grid.IsCurrentCellDirty && _grid.CurrentCell is DataGridViewComboBoxCell or DataGridViewCheckBoxCell) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        _grid.SelectionChanged += (_, _) => Safe(ShowDetail);
        _grid.DataError += (_, e) => { e.ThrowException = false; };
        _revise.CheckedChanged += (_, _) => Safe(OnReviseChanged);
        _preview.Click += (_, _) => Safe(OnPreview);
        _attachPdf.Click += (_, _) => Safe(OnAttachPdf);
        _apply.Click += (_, _) => Safe(OnApply);
        _copy.Click += (_, _) => Safe(OnCopy);
        _stop.Click += (_, _) => { _stopRequested = true; _stop.Enabled = false; _status.Text = "Stopping after the current part..."; };
        FormClosing += (_, e) => { if (_busy) e.Cancel = true; };
    }

    /// <summary>Builds the form with fake rows and no Vault, for a layout check.</summary>
    internal static FixDataCardForm Preview(IDataCardHost host, IReadOnlyList<FixCardSource> sources, IReadOnlyList<string> process) =>
        new(host, sources, Array.Empty<string>(), process, _ => { });

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var wa = Screen.FromControl(this).WorkingArea;
        ClientSize = new Size(Math.Min(LogicalToDeviceUnits(1180), wa.Width - LogicalToDeviceUnits(60)),
                              Math.Min(LogicalToDeviceUnits(520), wa.Height - LogicalToDeviceUnits(120)));
        MinimumSize = new Size(Math.Min(LogicalToDeviceUnits(720), wa.Width), Math.Min(LogicalToDeviceUnits(380), wa.Height));
        CenterToParent();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        StartLoad();
    }

    private void Safe(Action a)
    {
        try { a(); }
        catch (Exception ex) { _log($"fix data card form: {ex}"); _status.Text = "Unexpected error: " + ex.Message; }
    }

    // ---- Layout ----

    // Logical (96 dpi) pixels: AutoScaleMode.Dpi scales every control, column width and padding exactly once when the form is shown.
    private static int Px(int logical) => logical;

    private void BuildGrid()
    {
        _grid.AllowUserToAddRows = false; _grid.AllowUserToDeleteRows = false; _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        _grid.MultiSelect = false;
        _grid.EditMode = DataGridViewEditMode.EditOnEnter;
        _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _grid.ColumnHeadersHeight = Px(40);
        _grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _grid.BackgroundColor = SystemColors.Window;
        _grid.Dock = DockStyle.Fill;
        _grid.TabIndex = 0;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "File", ReadOnly = true, Width = Px(150), Frozen = true, SortMode = DataGridViewColumnSortMode.NotSortable });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Status", ReadOnly = true, Width = Px(120), Frozen = true, SortMode = DataGridViewColumnSortMode.NotSortable });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Attach\nMcMaster PDF", Width = Px(80), Frozen = true, SortMode = DataGridViewColumnSortMode.NotSortable });
        var widths = new Dictionary<string, int>
        {
            ["Part Number"] = 140, ["Title"] = 220, ["Description"] = 200, ["Vendor"] = 120, ["Vendor Number"] = 110, ["Source"] = 70,
            ["Process"] = 90, ["Material Finish"] = 110, ["Estimated Cost"] = 85,
        };
        foreach (var f in FixCardRules.Fields)
        {
            var w = widths.TryGetValue(f.Label, out var lw) ? lw : 110;
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = f.Label + "\nnow", ReadOnly = true, Width = Px(w), SortMode = DataGridViewColumnSortMode.NotSortable, DefaultCellStyle = { ForeColor = SystemColors.GrayText, BackColor = SystemColors.ControlLight } });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = f.Label + "\nnew", Width = Px(w), SortMode = DataGridViewColumnSortMode.NotSortable });
        }
    }

    private static int NowCol(int field) => FirstFieldCol + field * 2;
    private static int NewCol(int field) => FirstFieldCol + field * 2 + 1;

    private void BuildLayout(IReadOnlyList<string> skippedNotes)
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(Px(12), Px(10), Px(12), Px(6)) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Fixed(Control c) { root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.Controls.Add(c); }

        var help = new Label
        {
            AutoSize = true, MaximumSize = new Size(Px(1100), 0), Margin = new Padding(0, 0, 0, Px(6)),
            Text = "Edit the \"new\" cells, press Preview to see what would change, then Apply. Only cells that differ from \"now\" are sent."
                   + (skippedNotes.Count > 0 ? "\nNot included: " + string.Join("; ", skippedNotes) + "." : ""),
        };
        Fixed(help);
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(_grid);
        _revise.Margin = new Padding(0, Px(6), 0, Px(2)); _revise.TabIndex = 1;
        new ToolTip().SetToolTip(_revise, "Released parts are moved to Work in Progress, edited and returned to Released. Off by default and never remembered.");
        Fixed(_revise);
        _detail.Height = Px(56); _detail.Margin = new Padding(0, Px(4), 0, Px(2));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(_detail);
        _detail.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _status.Margin = new Padding(0, Px(2), 0, Px(2));
        Fixed(_status);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(0, Px(2), 0, Px(6)),
        };
        foreach (var b in new[] { _close, _apply, _preview, _attachPdf, _copy, _stop })
        {
            b.MinimumSize = new Size(Px(88), 0); b.Margin = new Padding(Px(6), Px(3), Px(0), Px(3));
        }
        _preview.TabIndex = 2; _apply.TabIndex = 3; _stop.TabIndex = 4; _copy.TabIndex = 5; _close.TabIndex = 6; _attachPdf.TabIndex = 7;
        buttons.Controls.Add(_close);
        buttons.Controls.Add(_apply);
        buttons.Controls.Add(_preview);
        buttons.Controls.Add(_attachPdf);
        buttons.Controls.Add(_copy);
        buttons.Controls.Add(_stop);
        Fixed(buttons);
        Controls.Add(root);
    }

    // ---- Rows ----

    private void AddRow(FixCardSource s)
    {
        var row = new RowData { Source = s, Problem = s.Problem };
        _rows.Add(row);
        var i = _grid.Rows.Add();
        _grid.Rows[i].Cells[FileCol].Value = s.Name;
        _grid.Rows[i].Cells[StatusCol].Value = s.Problem is null ? "reading..." : "skipped";
        if (s.Problem is not null) GreyOut(i, s.Problem);
    }

    private void GreyOut(int i, string detail)
    {
        _grid.Rows[i].DefaultCellStyle.ForeColor = SystemColors.GrayText;
        for (var c = AttachCol; c < _grid.ColumnCount; c++) _grid.Rows[i].Cells[c].ReadOnly = true;
        _grid.Rows[i].Cells[AttachCol].Value = false;
        _grid.Rows[i].Cells[StatusCol].ToolTipText = detail;
        _rows[i].Problem = detail;
    }

    private void StartLoad()
    {
        var order = Enumerable.Range(0, _rows.Count).Where(i => _rows[i].Problem is null).ToList();
        var aborted = false;
        RunQueue(order, i =>
        {
            if (aborted) return;
            var r = _rows[i];
            _status.Text = $"Reading {r.Source.Name} ({order.IndexOf(i) + 1} of {order.Count})...";
            try
            {
                var loaded = FixCardRules.Load(_host, r.Source.VaultPath);
                r.Loaded = loaded;
                FillRow(i, loaded);
            }
            catch (BridgeException ex) when (ex.Code == "not_signed_in")
            {
                aborted = true;
                foreach (var j in order.Where(j => _rows[j].Loaded is null))
                {
                    _grid.Rows[j].Cells[StatusCol].Value = "skipped";
                    GreyOut(j, ex.Message);
                }
            }
        }, () =>
        {
            _loaded = true;
            var ok = _rows.Count(r => r.Loaded is { Problem: null });
            _status.Text = ok == 0 ? "Nothing can be fixed here. Select a row to see why."
                                   : $"{ok} of {_rows.Count} {(_rows.Count == 1 ? "part" : "parts")} ready. Edit the new values, then press Preview.";
            ShowDetail();
        });
    }

    private void FillRow(int i, FixCardLoaded loaded)
    {
        _filling = true;
        try
        {
            var row = _grid.Rows[i];
            row.Cells[FileCol].Value = loaded.Name;
            row.Cells[FileCol].ToolTipText = loaded.VaultPath + (loaded.State.Length > 0 ? $"\nState: {loaded.State}" : "");
            if (loaded.Problem is not null)
            {
                row.Cells[StatusCol].Value = "skipped";
                GreyOut(i, loaded.Problem);
                return;
            }
            var proposed = FixCardRules.Propose(loaded.Name, loaded.Category, loaded.Current);
            for (var f = 0; f < FixCardRules.Fields.Count; f++)
            {
                var prop = FixCardRules.Fields[f].Prop;
                loaded.Current.TryGetValue(prop, out var cur);
                var nowText = prop == FixCardRules.CostProp ? FixCardRules.FormatCost(cur) : cur ?? "";
                row.Cells[NowCol(f)].Value = nowText;
                var newText = proposed[prop];
                if (prop == FixCardRules.SourceProp || prop == FixCardRules.ProcessProp)
                {
                    var items = prop == FixCardRules.SourceProp ? FixCardRules.SourceChoices(cur) : FixCardRules.ProcessChoices(_processValues, cur);
                    var cell = new DataGridViewComboBoxCell { FlatStyle = FlatStyle.Flat, DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox };
                    foreach (var it in items) cell.Items.Add(it);
                    if (!cell.Items.Contains(newText)) cell.Items.Add(newText);
                    row.Cells[NewCol(f)] = cell;
                }
                row.Cells[NewCol(f)].Value = newText;
            }
            _autoTitle[i] = proposed[FixCardRules.TitleProp];
        }
        finally { _filling = false; }
        RefreshRow(i);
    }

    private IReadOnlyDictionary<PropRef, string> ReadEdited(int i)
    {
        var o = new Dictionary<PropRef, string>();
        for (var f = 0; f < FixCardRules.Fields.Count; f++)
            o[FixCardRules.Fields[f].Prop] = Convert.ToString(_grid.Rows[i].Cells[NewCol(f)].Value) ?? "";
        return o;
    }

    /// <summary>Status before a preview: no change, ready, Released skipped, or the input error. Also highlights the cells that differ.</summary>
    private void RefreshRow(int i)
    {
        var r = _rows[i];
        if (r.Loaded is not { Problem: null } loaded) return;
        var edited = ReadEdited(i);
        _filling = true;
        try
        {
            for (var f = 0; f < FixCardRules.Fields.Count; f++)
            {
                var prop = FixCardRules.Fields[f].Prop;
                loaded.Current.TryGetValue(prop, out var cur);
                var differs = FixCardRules.Differs(prop, cur, edited[prop]);
                _grid.Rows[i].Cells[NewCol(f)].Style.BackColor = differs ? ChangedBack : Color.Empty;
            }
        }
        finally { _filling = false; }
        var build = FixCardRules.BuildRequest(loaded.VaultPath, loaded.Current, edited, _revise.Checked, true);
        string status, detail;
        if (build.Errors.Count > 0) { status = "error"; detail = string.Join(" ", build.Errors); }
        else if (build.Request is null) { status = FixCardOutcome.NoChange; detail = ""; }
        else if (loaded.Released && !_revise.Checked) { status = FixCardOutcome.Skipped; detail = FixCardRules.ReleasedSkipped; }
        else { status = "ready"; detail = ""; }
        r.Outcome = null;
        _grid.Rows[i].Cells[StatusCol].Value = status == FixCardOutcome.Skipped ? "Released: skipped" : status;
        _grid.Rows[i].Cells[StatusCol].ToolTipText = detail;
        _grid.Rows[i].DefaultCellStyle.ForeColor = status is FixCardOutcome.NoChange or FixCardOutcome.Skipped ? SystemColors.GrayText : SystemColors.ControlText;
        _detailByRow[i] = detail;
    }

    private readonly Dictionary<int, string> _detailByRow = new();

    private void OnCellChanged(int row, int col)
    {
        if (col == AttachCol) { if (!_filling) UpdateButtons(); return; }
        if (_filling || row < 0 || col < FirstFieldCol || (col - FirstFieldCol) % 2 == 0) return;
        FollowTitle(row, (col - FirstFieldCol) / 2);
        RefreshRow(row);
        Invalidated();
    }

    private static int FieldIndex(PropRef p) => FixCardRules.Fields.ToList().FindIndex(f => f.Prop == p);

    /// <summary>Part Number, Description or Source edited: Title follows unless it was edited by hand.</summary>
    private void FollowTitle(int row, int field)
    {
        var prop = FixCardRules.Fields[field].Prop;
        if (prop != FixCardRules.PartNumberProp && prop != FixCardRules.DescriptionProp && prop != FixCardRules.SourceProp) return;
        if (_rows[row].Loaded is not { Problem: null } loaded || !_autoTitle.TryGetValue(row, out var prev)) return;
        var e = ReadEdited(row);
        loaded.Current.TryGetValue(FixCardRules.TitleProp, out var currentTitle);
        var next = FixCardRules.ProposeTitle(e[FixCardRules.PartNumberProp], e[FixCardRules.DescriptionProp], e[FixCardRules.SourceProp], loaded.Category, currentTitle ?? "");
        var cell = _grid.Rows[row].Cells[NewCol(FieldIndex(FixCardRules.TitleProp))];
        var follow = FixCardRules.FollowTitle(Convert.ToString(cell.Value), prev, next);
        _autoTitle[row] = next;
        if (follow == Convert.ToString(cell.Value)) return;
        _filling = true;
        try { cell.Value = follow; }
        finally { _filling = false; }
    }

    private void OnReviseChanged()
    {
        if (!_loaded) return;
        for (var i = 0; i < _rows.Count; i++) RefreshRow(i);
        Invalidated();
    }

    /// <summary>Any edit makes the last preview stale: Apply is off until the next Preview.</summary>
    private void Invalidated()
    {
        _previewed = false;
        UpdateButtons();
        ShowDetail();
    }

    private void ShowDetail()
    {
        var i = _grid.CurrentCell?.RowIndex ?? -1;
        if (i < 0 || i >= _rows.Count) { _detail.Text = ""; return; }
        var r = _rows[i];
        if (r.Outcome is not null) _detail.Text = r.Outcome.Status + (r.Outcome.Detail.Length > 0 ? ": " + r.Outcome.Detail : "");
        else if (r.Problem is not null) _detail.Text = "skipped: " + r.Problem;
        else _detail.Text = _detailByRow.TryGetValue(i, out var d) ? d : "";
    }

    private void UpdateButtons()
    {
        var canWork = _loaded && !_busy && !_applied && _rows.Any(r => r.Loaded is { Problem: null });
        _preview.Enabled = canWork;
        _apply.Enabled = canWork && _previewed && _rows.Any(r => r.Outcome?.Status == FixCardOutcome.WouldChange);
        _copy.Enabled = !_busy && _rows.Any(r => r.Outcome is not null);
        _attachPdf.Enabled = _attach is not null && _loaded && !_busy && TickedRows().Count > 0;
        _revise.Enabled = !_busy && !_applied;
        _grid.Enabled = !_busy;
        _close.Enabled = !_busy;
        _stop.Enabled = _busy && !_stopRequested;
    }

    // ---- Preview and Apply ----

    private void SetOutcome(int i, FixCardOutcome o)
    {
        _rows[i].Outcome = o;
        var label = o.Status == FixCardOutcome.Skipped && o.Detail == FixCardRules.ReleasedSkipped ? "Released: skipped" : o.Status;
        _grid.Rows[i].Cells[StatusCol].Value = label;
        _grid.Rows[i].Cells[StatusCol].ToolTipText = o.Detail;
        _grid.Rows[i].DefaultCellStyle.ForeColor = o.Status is FixCardOutcome.NoChange or FixCardOutcome.Skipped ? SystemColors.GrayText
            : o.Status == FixCardOutcome.Error ? Color.Firebrick : SystemColors.ControlText;
    }

    private List<int> TickedRows() => Enumerable.Range(0, _rows.Count)
        .Where(i => _rows[i].Loaded is { Problem: null } && _grid.Rows[i].Cells[AttachCol].Value is true).ToList();

    private List<int> WorkRows() => Enumerable.Range(0, _rows.Count).Where(i => _rows[i].Loaded is { Problem: null }).ToList();

    private IReadOnlyList<IReadOnlyDictionary<PropRef, string>> TakeSnapshot() =>
        Enumerable.Range(0, _rows.Count).Select(i => _rows[i].Loaded is { Problem: null } ? ReadEdited(i) : new Dictionary<PropRef, string>()).ToList();

    private void OnPreview()
    {
        _grid.EndEdit();
        _snapshot = TakeSnapshot();
        _snapshotRevise = _revise.Checked;
        var batch = new FixCardRules.Batch(_host, _revise.Checked, _log);
        var rows = WorkRows();
        RunQueue(rows, i =>
        {
            _status.Text = $"Previewing {_rows[i].Source.Name} ({rows.IndexOf(i) + 1} of {rows.Count})...";
            SetOutcome(i, Guarded(_rows[i], () => batch.Run(_rows[i].Loaded!, ReadEdited(i), true)));
        }, () =>
        {
            _previewed = !_stoppedEarly;
            var n = _rows.Count(r => r.Outcome?.Status == FixCardOutcome.WouldChange);
            _status.Text = _stoppedEarly ? "Stopped. Click Preview again before applying."
                : n == 0 ? "Nothing would change." : $"{n} {(n == 1 ? "part" : "parts")} would change. Press Apply to check out, edit and check in.";
            UpdateButtons();
            ShowDetail();
        }, i => SetOutcome(i, FixCardRules.NotAttemptedOutcome(_rows[i].Source.VaultPath)));
    }

    private void OnApply()
    {
        _grid.EndEdit();
        var targets = Enumerable.Range(0, _rows.Count).Where(i => _rows[i].Outcome?.Status == FixCardOutcome.WouldChange).ToList();
        if (!_previewed || targets.Count == 0) return;
        if (_snapshot is null || !FixCardRules.SameAsPreview(_snapshot, _snapshotRevise, TakeSnapshot(), _revise.Checked))
        {
            _status.Text = FixCardRules.ChangedSincePreview;
            Invalidated();
            return;
        }
        var revised = targets.Count(i => _revise.Checked && _rows[i].Loaded!.Released);
        if (MessageBox.Show(this, FixCardRules.ConfirmText(targets.Count, revised), Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        var batch = new FixCardRules.Batch(_host, _revise.Checked, _log);
        RunQueue(targets, i =>
        {
            _status.Text = $"Fixing {_rows[i].Source.Name} ({targets.IndexOf(i) + 1} of {targets.Count})...";
            SetOutcome(i, Guarded(_rows[i], () => batch.Run(_rows[i].Loaded!, ReadEdited(i), false)));
        }, () =>
        {
            _applied = true;
            _previewed = false;
            foreach (DataGridViewRow gr in _grid.Rows) for (var c = FirstFieldCol; c < _grid.ColumnCount; c++) gr.Cells[c].ReadOnly = true;
            var fixedCount = targets.Count(i => _rows[i].Outcome?.Status == FixCardOutcome.Fixed);
            var errors = targets.Count(i => _rows[i].Outcome?.Status == FixCardOutcome.Error);
            var notTried = targets.Count(i => _rows[i].Outcome?.Status == FixCardOutcome.NotAttempted);
            _status.Text = $"{fixedCount} fixed, {errors} with errors, {notTried} not attempted, {targets.Count - fixedCount - errors - notTried} skipped. Use Copy results to keep a record.";
            UpdateButtons();
            ShowDetail();
        }, i => SetOutcome(i, FixCardRules.NotAttemptedOutcome(_rows[i].Source.VaultPath)));
    }

    private FixCardOutcome Guarded(RowData row, Func<FixCardOutcome> run)
    {
        try { return run(); }
        catch (Exception ex)
        {
            _log($"fix data card {row.Source.VaultPath}: {ex}");
            return new FixCardOutcome(row.Source.VaultPath, FixCardOutcome.Error, "unexpected: " + ex.Message);
        }
    }

    // ---- Attach McMaster PDF ----

    /// <summary>The ticked parts, one at a time: a preview pass (reads Vault only), a confirmation that says what will be downloaded and
    /// added, then the same checkout / check-in path the HTTP operation uses. Released parts need "Revise and re-release" ticked.</summary>
    private void OnAttachPdf()
    {
        if (_attach is null) return;
        _grid.EndEdit();
        var rows = TickedRows();
        if (rows.Count == 0) return;
        var revise = _revise.Checked;
        var previews = new Dictionary<int, JsonObject>();
        RunQueue(rows, i =>
        {
            _status.Text = $"Checking {_rows[i].Source.Name} ({rows.IndexOf(i) + 1} of {rows.Count})...";
            var req = new AttachPdfRequest(new[] { _rows[i].Loaded!.VaultPath }, AttachRules.McMasterSource, true, revise, null);
            previews[i] = AttachVendorPdf.Run(req, _attach, _fetcher, _log)["results"]![0]!.AsObject();
            SetOutcome(i, AttachOutcome(_rows[i], previews[i]));
        }, () =>
        {
            var go = rows.Where(i => previews.TryGetValue(i, out var p) && p["status"]!.GetValue<string>() == "preview").ToList();
            if (go.Count == 0)
            {
                _status.Text = "No part needs the PDF. Select a row to see why.";
                UpdateButtons();
                ShowDetail();
                return;
            }
            var released = go.Count(i => previews[i]["would_revise"]?.GetValue<bool>() == true);
            var text = $"Download the McMaster drawing PDF and attach it to {go.Count} {(go.Count == 1 ? "part" : "parts")}?\n\n"
                       + string.Join("\n", go.Select(i => $"{_rows[i].Source.Name}: {previews[i]["pdf_name"]} ({(previews[i]["action"]!.GetValue<string>() == "attach_existing" ? "attach the file already in Vault" : "download and add")})"))
                       + (released > 0 ? $"\n\n{released} released {(released == 1 ? "part is" : "parts are")} moved to the edit state and back." : "")
                       + "\n\nMcMaster may open your browser to sign in.";
            if (MessageBox.Show(this, text, Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            {
                _status.Text = "Cancelled. Nothing was changed.";
                UpdateButtons();
                return;
            }
            var run = new DataCardFix.RunState();
            RunQueue(go, i =>
            {
                _status.Text = $"Attaching the PDF to {_rows[i].Source.Name} ({go.IndexOf(i) + 1} of {go.Count})...";
                var req = new AttachPdfRequest(new[] { _rows[i].Loaded!.VaultPath }, AttachRules.McMasterSource, false, revise, null);
                var result = AttachVendorPdf.Run(req, _attach, _fetcher, _log, run)["results"]![0]!.AsObject();
                SetOutcome(i, AttachOutcome(_rows[i], result));
                if (result["status"]!.GetValue<string>() == "fixed") _grid.Rows[i].Cells[AttachCol].Value = false;
            }, () =>
            {
                var done = go.Count(i => _rows[i].Outcome?.Status == FixCardOutcome.Fixed);
                _status.Text = $"{done} of {go.Count} attached. Use Copy results to keep a record.";
                Invalidated();
            }, i => SetOutcome(i, FixCardRules.NotAttemptedOutcome(_rows[i].Source.VaultPath)));
        }, i => SetOutcome(i, FixCardRules.NotAttemptedOutcome(_rows[i].Source.VaultPath)));
    }

    private static FixCardOutcome AttachOutcome(RowData row, JsonObject r)
    {
        var path = row.Loaded?.VaultPath ?? row.Source.VaultPath;
        var detail = (r["reason"] ?? r["message"])?.GetValue<string>() ?? "";
        switch (r["status"]!.GetValue<string>())
        {
            case "preview":
                return new FixCardOutcome(path, FixCardOutcome.WouldChange, $"would attach {r["pdf_name"]}");
            case "fixed":
                return new FixCardOutcome(path, FixCardOutcome.Fixed, $"attached {r["pdf_name"]}" + (r["warning"] is { } w ? " (" + w + ")" : ""),
                    Version: r["version"]?.ToString() ?? "");
            case "skipped":
                return new FixCardOutcome(path, FixCardOutcome.Skipped, detail);
            default:
                return new FixCardOutcome(path, FixCardOutcome.Error, detail);
        }
    }

    private void OnCopy()
    {
        var rows = _rows.Select((r, i) => r.Outcome ?? new FixCardOutcome(r.Source.VaultPath, Convert.ToString(_grid.Rows[i].Cells[StatusCol].Value) ?? "", r.Problem ?? ""))
                        .Where((_, i) => _rows[i].Outcome is not null);
        try
        {
            Clipboard.SetText(FixCardRules.ResultsText(rows));
            _status.Text = "Results copied to the clipboard.";
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            _log($"fix data card copy failed: {ex.Message}");
            _status.Text = "Could not copy to the clipboard: " + ex.Message;
        }
    }

    private bool _stoppedEarly;

    /// <summary>Runs work(row) for each row, one per message-pump turn, so painting and Inventor's own messages are served between rows.
    /// Stop is checked between rows; rows not reached go to notAttempted.</summary>
    private void RunQueue(IReadOnlyList<int> rows, Action<int> work, Action done, Action<int>? notAttempted = null)
    {
        _busy = true;
        _stopRequested = false;
        _stoppedEarly = false;
        UpdateButtons();
        var next = 0;
        void Step()
        {
            if (IsDisposed) return;
            if (_stopRequested && next < rows.Count)
            {
                _stoppedEarly = true;
                for (; next < rows.Count; next++)
                {
                    var skipped = rows[next];
                    if (notAttempted is not null) notAttempted(skipped);
                    else
                    {
                        _grid.Rows[skipped].Cells[StatusCol].Value = FixCardOutcome.NotAttempted;
                        _grid.Rows[skipped].DefaultCellStyle.ForeColor = SystemColors.GrayText;
                        _rows[skipped].Problem ??= "stopped before this part";
                    }
                }
            }
            if (next >= rows.Count)
            {
                _busy = false;
                try { done(); }
                catch (Exception ex) { _log($"fix data card form: {ex}"); _status.Text = "Unexpected error: " + ex.Message; }
                UpdateButtons();
                return;
            }
            var i = rows[next++];
            try { work(i); }
            catch (Exception ex)
            {
                _log($"fix data card form: {ex}");
                _grid.Rows[i].Cells[StatusCol].Value = "error";
                _grid.Rows[i].Cells[StatusCol].ToolTipText = ex.Message;
                _rows[i].Problem ??= ex.Message;
            }
            BeginInvoke((Action)Step);
        }
        BeginInvoke((Action)Step);
    }
}
