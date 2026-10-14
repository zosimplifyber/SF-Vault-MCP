using System.Drawing;
using System.Windows.Forms;

namespace SFVaultBridge.Addin;

/// <summary>A small modal "working" window with a Cancel button, for work that may wait on a browser sign-in. Never throws.</summary>
internal sealed class BusyDialog : Form
{
    private readonly Label _text = new() { AutoSize = true, MaximumSize = new Size(380, 0), Margin = new Padding(0, 0, 0, 10) };
    private readonly Button _cancel = new() { Text = "Cancel", AutoSize = true, MinimumSize = new Size(80, 0) };
    private readonly CancellationTokenSource _cts = new();

    private BusyDialog(string title, string text)
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont ?? Font;
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false; ControlBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _text.Text = text;
        var table = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(14), MinimumSize = new Size(320, 0) };
        table.Controls.Add(_text);
        _cancel.Anchor = AnchorStyles.Right;
        table.Controls.Add(_cancel);
        Controls.Add(table);
        CancelButton = _cancel;
        _cancel.Click += (_, _) => { _cts.Cancel(); _text.Text = "Cancelling..."; _cancel.Enabled = false; };
    }

    /// <summary>
    /// Runs work while the window shows; the window closes when the work ends (Cancel only asks it to stop). Completed is false when
    /// the user pressed Cancel or the work threw; Result is whatever the work returned, even after Cancel. Progress text replaces the window's text.
    /// </summary>
    public static (bool Completed, T? Result, Exception? Error) Run<T>(IWin32Window? owner, string title, string text,
                                                                    Func<IProgress<string>, CancellationToken, Task<T>> work)
    {
        using var d = new BusyDialog(title, text);
        T? result = default;
        Exception? error = null;
        d.Shown += async (_, _) =>
        {
            var progress = new Progress<string>(t => { if (!d.IsDisposed && !d._cts.IsCancellationRequested) d._text.Text = t; });
            try
            {
                result = await Task.Run(() => work(progress, d._cts.Token));
            }
            catch (Exception e) { error = e; }
            if (!d.IsDisposed) { d.DialogResult = DialogResult.OK; d.Close(); }
        };
        if (owner is null) d.ShowDialog(); else d.ShowDialog(owner);
        var cancelled = d._cts.IsCancellationRequested;
        return (!cancelled && error is null, result, error);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _cts.Dispose();
        base.Dispose(disposing);
    }
}
