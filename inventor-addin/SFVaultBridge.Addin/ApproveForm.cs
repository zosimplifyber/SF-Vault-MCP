using System.Drawing;
using System.Windows.Forms;
using SFVaultBridge.Core;

namespace SFVaultBridge.Addin;

/// <summary>
/// The I Approve! form: a preview (no writes) with one row per file in release order, then a confirmed run that signs off and releases the
/// Ready rows bottom up. Same patterns as FixDataCardForm: DPI-safe layout, one file per message-pump turn, Stop between files, never throws
/// into Inventor. One run per form: after it the form is read-only.
/// </summary>
internal sealed class ApproveForm : Form
{
    private const int FileCol = 0, KindCol = 1, StateCol = 2, RevCol = 3, RevAfterCol = 4, ReadyCol = 5, NotesCol = 6, ResultCol = 7;

    private readonly ReleaseHost _host;
    private readonly IReadOnlyList<ReleaseModel> _models;
    private readonly string _approver;
    private readonly string _assembly;
    private readonly Action<string> _log;
    private readonly DataGridView _grid = new();
    private readonly Label _header = new() { AutoSize = true };
    private readonly Label _status = new() { AutoSize = true };
    private readonly TextBox _detail = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, TabStop = false };
    private readonly Button _preview = new() { Text = "Preview again", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    private readonly Button _approve = new() { Text = "I Approve!", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    private readonly Button _copy = new() { Text = "Copy results", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    private readonly Button _stop = new() { Text = "Stop", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Enabled = false };
    private readonly Button _close = new() { Text = "Close", DialogResult = DialogResult.Cancel, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };

    private IReadOnlyList<ReleaseRow> _rows = Array.Empty<ReleaseRow>();
    private IReadOnlyList<ReleaseResult>? _results;
    private ReleaseRun? _run;
    private bool _busy, _stopRequested, _previewed;

    public ApproveForm(ReleaseHost host, IReadOnlyList<ReleaseModel> models, string approver, string assembly, Action<string> log)
    {
        _host = host; _models = models; _approver = approver; _assembly = assembly; _log = log;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "I Approve! " + assembly;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        CancelButton = _close;
        Font = SystemFonts.MessageBoxFont ?? Font;
        BuildGrid();
        BuildLayout();
        _header.Text = $"Approver: {approver}";
        _grid.SelectionChanged += (_, _) => Safe(ShowDetail);
        _preview.Click += (_, _) => Safe(StartPreview);
        _approve.Click += (_, _) => Safe(OnApprove);
        _copy.Click += (_, _) => Safe(OnCopy);
        _stop.Click += (_, _) => { _stopRequested = true; _run?.RequestStop(); _stop.Enabled = false; _status.Text = "Stopping after the current file..."; };
        FormClosing += (_, e) => { if (_busy) e.Cancel = true; };
        UpdateButtons();
    }

    /// <summary>True once the release run has started (its results are in Results).</summary>
    public bool Ran => _results is not null;

    public IReadOnlyList<ReleaseResult> Results => _results ?? Array.Empty<ReleaseResult>();

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var wa = Screen.FromControl(this).WorkingArea;
        ClientSize = new Size(Math.Min(LogicalToDeviceUnits(1200), wa.Width - LogicalToDeviceUnits(60)),
                              Math.Min(LogicalToDeviceUnits(560), wa.Height - LogicalToDeviceUnits(120)));
        MinimumSize = new Size(Math.Min(LogicalToDeviceUnits(760), wa.Width), Math.Min(LogicalToDeviceUnits(400), wa.Height));
        CenterToParent();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        StartPreview();
    }

    private void Safe(Action a)
    {
        try { a(); }
        catch (Exception ex) { _log($"approve form: {ex}"); _status.Text = "Unexpected error: " + ex.Message; }
    }

    // ---- Layout ----

    // Logical (96 dpi) pixels: AutoScaleMode.Dpi scales every control, column width and padding exactly once when the form is shown.
    private static int Px(int logical) => logical;

    private void BuildGrid()
    {
        _grid.AllowUserToAddRows = false; _grid.AllowUserToDeleteRows = false; _grid.AllowUserToResizeRows = false;
        _grid.ReadOnly = true;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _grid.ColumnHeadersHeight = Px(28);
        _grid.BackgroundColor = SystemColors.Window;
        _grid.Dock = DockStyle.Fill;
        _grid.TabIndex = 0;
        void Col(string header, int width, bool frozen = false) =>
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = header, Width = Px(width), Frozen = frozen, SortMode = DataGridViewColumnSortMode.NotSortable });
        Col("File", 170, true);
        Col("Kind", 85);
        Col("State now", 110);
        Col("Revision now", 75);
        Col("Revision after", 95);
        Col("Ready / Blocker", 300);
        Col("Notes", 200);
        Col("Result", 260);
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(Px(12), Px(10), Px(12), Px(6)) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Fixed(Control c) { root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.Controls.Add(c); }
        _header.MaximumSize = new Size(Px(1150), 0);
        _header.Margin = new Padding(0, 0, 0, Px(6));
        Fixed(_header);
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(_grid);
        _detail.Height = Px(64); _detail.Margin = new Padding(0, Px(4), 0, Px(2));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(_detail);
        _detail.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _status.Margin = new Padding(0, Px(2), 0, Px(2));
        _status.MaximumSize = new Size(Px(1150), 0);
        Fixed(_status);
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(0, Px(2), 0, Px(6)),
        };
        foreach (var b in new[] { _close, _approve, _preview, _copy, _stop })
        {
            b.MinimumSize = new Size(Px(88), 0); b.Margin = new Padding(Px(6), Px(3), Px(0), Px(3));
        }
        _approve.TabIndex = 1; _preview.TabIndex = 2; _stop.TabIndex = 3; _copy.TabIndex = 4; _close.TabIndex = 5;
        buttons.Controls.Add(_close);
        buttons.Controls.Add(_approve);
        buttons.Controls.Add(_preview);
        buttons.Controls.Add(_copy);
        buttons.Controls.Add(_stop);
        Fixed(buttons);
        Controls.Add(root);
    }

    private void UpdateButtons()
    {
        var ran = _results is not null;
        _preview.Enabled = !_busy && !ran;
        _approve.Enabled = !_busy && !ran && _previewed && ReleasePlan.Counts(_rows).Release > 0;
        _copy.Enabled = !_busy && (ran || _previewed);
        _close.Enabled = !_busy;
        _stop.Enabled = _busy && !_stopRequested;
    }

    // ---- Pump ----

    /// <summary>Runs step() once per message-pump turn until it returns false, so painting and Inventor's messages are served between files.</summary>
    private void Pump(Func<bool> step, Action done)
    {
        _busy = true;
        _stopRequested = false;
        UpdateButtons();
        void Turn()
        {
            if (IsDisposed) return;
            bool more;
            try { more = step(); }
            catch (Exception ex)
            {
                _log($"approve form: {ex}");
                _status.Text = "Stopped: " + ex.Message;
                more = false;
            }
            if (more) { BeginInvoke((Action)Turn); return; }
            _busy = false;
            try { done(); }
            catch (Exception ex) { _log($"approve form: {ex}"); _status.Text = "Unexpected error: " + ex.Message; }
            UpdateButtons();
        }
        BeginInvoke((Action)Turn);
    }

    // ---- Preview ----

    private void StartPreview()
    {
        _previewed = false;
        _rows = Array.Empty<ReleaseRow>();
        _grid.Rows.Clear();
        var drawings = new Dictionary<string, ReleaseDrawingRef?>(StringComparer.OrdinalIgnoreCase);
        var problems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<ReleaseRowSpec>? specs = null;
        var facts = new List<ReleaseFacts>();
        Dictionary<string, string>? rowKeys = null;
        var m = 0;
        var aborted = (string?)null;
        Pump(() =>
        {
            if (_stopRequested) { aborted = "Preview stopped."; return false; }
            if (m < _models.Count)
            {
                var model = _models[m++];
                _status.Text = $"Finding drawings: {model.FileName} ({m} of {_models.Count})...";
                try { drawings[model.Key] = _host.FindDrawing(model.Key); }
                catch (BridgeException ex) when (ex.Code == "not_signed_in") { aborted = ex.Message; return false; }
                catch (Exception ex)
                {
                    _log($"approve: drawing lookup for {model.Key} failed: {ex}");
                    drawings[model.Key] = null;
                    problems[model.Key] = "could not look up the drawing: " + ex.Message;
                }
                return true;
            }
            if (specs is null)
            {
                specs = ReleasePlan.Order(_models, drawings);
                rowKeys = specs.GroupBy(s => ReleaseHost.Key(s.Key), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Key, StringComparer.OrdinalIgnoreCase);
                foreach (var s in specs)
                {
                    var i = _grid.Rows.Add();
                    var cells = _grid.Rows[i].Cells;
                    cells[FileCol].Value = s.FileName;
                    cells[FileCol].ToolTipText = s.Key;
                    cells[KindCol].Value = ReleasePlan.KindText(s.Kind);
                    cells[ReadyCol].Value = "reading...";
                }
                return true;
            }
            if (facts.Count < specs.Count)
            {
                var s = specs[facts.Count];
                _status.Text = $"Reading {s.FileName} from Vault ({facts.Count + 1} of {specs.Count})...";
                ReleaseFacts f;
                try { f = _host.Facts(s, rowKeys!); }
                catch (BridgeException ex) when (ex.Code == "not_signed_in") { aborted = ex.Message; return false; }
                if (problems.TryGetValue(s.Key, out var p) && f.Found && f.Problem is null) f = f with { Problem = p };
                var cells = _grid.Rows[facts.Count].Cells;
                cells[StateCol].Value = f.Found ? f.State : "";
                cells[RevCol].Value = f.Revision;
                facts.Add(f);
                return true;
            }
            return false;
        }, () =>
        {
            if (aborted is not null || specs is null || facts.Count < specs.Count)
            {
                _status.Text = (aborted ?? "Preview stopped.") + " Nothing was written. Press Preview again.";
                return;
            }
            var factsOf = new Dictionary<ReleaseRowSpec, ReleaseFacts>(ReferenceEqualityComparer.Instance);
            for (var k = 0; k < specs.Count; k++) factsOf[specs[k]] = facts[k];
            _rows = ReleasePlan.Evaluate(specs, s => factsOf[s], _approver);
            for (var i = 0; i < _rows.Count; i++) FillPreview(i, _rows[i]);
            var lines = new List<string> { $"Approver: {_approver}. Release order is bottom up: parts, their drawings, sub-assemblies, their drawings, the top assembly, its drawing. A part or sub-assembly counts as released for the assemblies above it only when its drawing is Released too." };
            lines.Add(ReleasePlan.ChildCheckLine(_rows.Select(r => (r.Facts.Lifecycle, r.Facts.ChildCheck))));
            try { var note = _host.RestrictionNote(); if (note is not null) lines.Add(note); }
            catch (BridgeException ex) { lines.Add(ex.Message); }
            _header.Text = string.Join("\n", lines);
            var c = ReleasePlan.Counts(_rows);
            _previewed = true;
            _status.Text = c.Release == 0
                ? $"Nothing can be released: {c.Skipped} skipped, {c.HeldBack} held back, {c.AlreadyReleased} already Released. Select a row to see why."
                : $"{c.Release} ready ({c.Revise} Released drawings to revise), {c.Skipped} skipped, {c.HeldBack} held back, {c.AlreadyReleased} already Released. Press I Approve! to sign off and release.";
            ShowDetail();
        });
    }

    private void FillPreview(int i, ReleaseRow r)
    {
        var cells = _grid.Rows[i].Cells;
        cells[StateCol].Value = r.Facts.Found ? r.Facts.State : "";
        cells[RevCol].Value = r.Facts.Revision;
        cells[RevAfterCol].Value = r.Status is ReleaseStatus.Ready or ReleaseStatus.Revise ? r.Facts.RevisionAfter : "";
        cells[ReadyCol].Value = ReleasePlan.PreviewText(r);
        cells[NotesCol].Value = string.Join("; ", r.Warnings);
        cells[ResultCol].Value = "";
        _grid.Rows[i].DefaultCellStyle.ForeColor = r.Status switch
        {
            ReleaseStatus.Blocked => Color.Firebrick,
            ReleaseStatus.HeldBack or ReleaseStatus.AlreadyReleased => SystemColors.GrayText,
            _ => SystemColors.ControlText,
        };
    }

    private void ShowDetail()
    {
        var i = _grid.CurrentCell?.RowIndex ?? -1;
        if (i < 0 || i >= _rows.Count) { _detail.Text = ""; return; }
        var r = _rows[i];
        var parts = new List<string> { $"{r.Spec.FileName} ({ReleasePlan.KindText(r.Spec.Kind)}) {r.Facts.VaultPath}" };
        if (r.Blockers.Count > 0) parts.Add("Blockers: " + string.Join("; ", r.Blockers));
        if (r.HeldBackBy is not null) parts.Add(ReleasePlan.HeldBackText(r.HeldBackBy));
        if (r.Warnings.Count > 0) parts.Add("Notes: " + string.Join("; ", r.Warnings));
        var result = _run?.ResultAt(i) ?? (_results is not null && i < _results.Count ? _results[i] : null);
        if (result is not null) parts.Add("Result: " + ReleasePlan.ResultLine(result));
        _detail.Text = string.Join("\r\n", parts);
    }

    // ---- Run ----

    private void OnApprove()
    {
        if (!_previewed || _busy || _results is not null || ReleasePlan.Counts(_rows).Release == 0) return;
        if (MessageBox.Show(this, ReleasePlan.ConfirmText(_rows, _approver), Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        var run = new ReleaseRun(_rows, _approver, _host);
        _run = run;
        _results = Array.Empty<ReleaseResult>();
        Pump(() =>
        {
            var next = NextLabel(run);
            if (next is not null) _status.Text = next;
            var i = run.Step();
            if (i is int row && row >= 0 && run.ResultAt(row) is { } res) ShowResult(row, res);
            if (i == -1 && run.GetLatestNotes.Count > 0) _log("approve: kept as they are: " + string.Join("; ", run.GetLatestNotes));
            return i is not null;
        }, () =>
        {
            _results = run.Results;
            for (var i = 0; i < _results.Count; i++) ShowResult(i, _results[i]);
            var after = ReleasePlan.AfterRelease(_results);
            _status.Text = ReleasePlan.Summary(_results)
                + (run.StoppedBecause is null ? "" : $" Stopped: {run.StoppedBecause}.")
                + (after.Build ? " Close this window to build the Purchasing Sheet and Assembly Package and open Partner Package." : " " + after.Message)
                + " Use Copy results to keep a record.";
            _log("approve: " + ReleasePlan.Summary(_results) + (run.StoppedBecause is null ? "" : " stopped: " + run.StoppedBecause));
            foreach (var r in _results) _log($"approve: {r.Row.Spec.Key}: {ReleasePlan.ResultLine(r)}");
            ShowDetail();
        });
    }

    private string? NextLabel(ReleaseRun run)
    {
        var done = Enumerable.Range(0, _rows.Count).Count(i => run.ResultAt(i) is not null);
        if (done == 0 && run.GetLatestNotes.Count == 0 && !_stopRequested) return "Getting the latest version of the tree from Vault...";
        if (done >= _rows.Count) return null;
        return $"{_rows[done].Spec.FileName} ({done + 1} of {_rows.Count})...";
    }

    private void ShowResult(int i, ReleaseResult r)
    {
        var cells = _grid.Rows[i].Cells;
        cells[ResultCol].Value = ReleasePlan.ResultLine(r);
        _grid.Rows[i].DefaultCellStyle.ForeColor = r.Status switch
        {
            ReleaseStatus.Released => Color.ForestGreen,
            ReleaseStatus.Failed => Color.Firebrick,
            ReleaseStatus.Blocked => Color.Firebrick,
            _ => SystemColors.GrayText,
        };
    }

    private void OnCopy()
    {
        var results = _results ?? _rows.Select(r => new ReleaseResult(r, r.Status,
            r.Status == ReleaseStatus.Blocked ? string.Join("; ", r.Blockers) : r.Status == ReleaseStatus.HeldBack ? ReleasePlan.HeldBackText(r.HeldBackBy ?? "a child") : "")).ToList();
        try
        {
            Clipboard.SetText(ReleasePlan.ResultsText(results));
            _status.Text = "Results copied to the clipboard.";
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            _log($"approve copy failed: {ex.Message}");
            _status.Text = "Could not copy to the clipboard: " + ex.Message;
        }
    }
}
