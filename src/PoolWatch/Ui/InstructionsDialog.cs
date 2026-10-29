using PoolWatch.Core.Recommendations;

namespace PoolWatch.Ui;

/// <summary>
/// Steps to follow yourself (the Microsoft Defender advice), as read-only text: [Copy commands] copies only its command
/// lines, the ones indented with <see cref="DefenderAdvice.CommandIndent"/>, ready to paste into PowerShell. PoolWatch runs
/// none of them. Created when opened, disposed when closed.
/// </summary>
internal sealed class InstructionsDialog : Form
{
    private readonly Font _textFont = new("Segoe UI", 9.5f); // the box never disposes the font it is given: the window does

    public InstructionsDialog(string title, string text)
    {
        Text = title;
        Icon = TrayIcons.App;
        Font = new Font("Segoe UI", 9f);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        Size = LogicalToDeviceUnits(new Size(680, 560));
        MinimumSize = LogicalToDeviceUnits(new Size(420, 300));

        // A TextBox draws only CRLF as a line break.
        var box = new TextBox
        {
            Text = text.ReplaceLineEndings("\r\n"), ReadOnly = true, Multiline = true, WordWrap = true, ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = SystemColors.Window, Font = _textFont, TabStop = false,
        };
        var copied = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, S(7), S(8), 0) };
        var copy = new Button { Text = "Copy commands", AutoSize = true, Padding = new Padding(S(6), S(2), S(6), S(2)), Enabled = Commands(text).Length > 0 };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetDataObject(Commands(text), copy: true, retryTimes: 5, retryDelay: 100); copied.Text = "Copied."; }
            catch (Exception) { copied.Text = "Another program is using the clipboard. Try again."; }
        };
        var close = new Button { Text = "Close", AutoSize = true, Padding = new Padding(S(6), S(2), S(6), S(2)), Margin = new Padding(S(8), 0, 0, 0) };
        close.Click += (_, _) => Close();
        CancelButton = close;
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false, Padding = new Padding(S(12), S(8), S(12), S(12)),
        };
        buttons.Controls.AddRange([close, copy, copied]);
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(S(16), S(12), S(12), 0), BackColor = SystemColors.Window };
        body.Controls.Add(box);
        Controls.Add(body); // fill first, then the docked edge
        Controls.Add(buttons);
    }

    private int S(int logical) => LogicalToDeviceUnits(logical);

    /// <summary>The command lines of <paramref name="text"/>, without their indent, one per line.</summary>
    internal static string Commands(string text) => string.Join(Environment.NewLine,
        text.ReplaceLineEndings("\n").Split('\n').Where(l => l.StartsWith(DefenderAdvice.CommandIndent, StringComparison.Ordinal)).Select(l => l.Trim()));

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _textFont.Dispose();
    }
}
