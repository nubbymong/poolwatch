using System.Drawing.Drawing2D;
using System.Text;
using System.Text.RegularExpressions;
using PoolWatch.Core.Services.Agent;

namespace PoolWatch.Ui;

/// <summary>The words of the "Decide with your AI agent" window, as the approved app design has them.</summary>
internal static class AgentPromptText
{
    public const string Title = "Decide with your AI agent";
    public const string Lead = "What is safe to turn off depends on how you use this PC. Your agent asks you, then suggests.";

    /// <summary>The three steps: what to do, then what it means.</summary>
    public static readonly IReadOnlyList<(string Step, string Text)> Steps =
    [
        ("Save the prompt", "A file with what runs on this PC and what to ask you."),
        ("Give it to your agent", $"Claude Code, Codex or any other: \"Read {AgentPrompt.FileName} and follow it.\""),
        ("Import its answer", "Your agent saves a file. PoolWatch shows every suggestion before it changes anything."),
    ];

    public const string SharedHeading = "What your agent will see";
    public const string SharedOnly = "Only what is in this file: the names and publishers of the services, scheduled tasks and startup apps below, " +
                                     "what each does, and your Windows version.";
    public const string NeverLabel = "Never in it:";
    public const string NeverText = "your name, user or PC name, folder paths, or anything from your files. Nothing is sent until you give the " +
                                    "file to your agent. Read every word on the right.";

    public static readonly string PreviewHeading = $"{AgentPrompt.FileName}: every word your agent will see";
    public const string PreviewHint = "scroll to read it all";

    public const string CopyButton = "Copy to clipboard", SaveButton = "Save the prompt…", CloseButton = "Close";

    /// <summary>The preview while PoolWatch reads what runs on this PC.</summary>
    public const string Reading = "Reading what runs on this PC…";

    public const string Copied = "Copied.";

    /// <summary>The bar's line once the prompt is saved: the file's name, never its folder.</summary>
    public static string Saved(string path) => $"Saved {Path.GetFileName(path)}.";

    /// <summary>The preview when a name on this PC could not be checked for private details in time (the Core's redaction gave up).</summary>
    public const string RedactionGaveUp = "PoolWatch could not check every name on this PC for private details in time, so it made no prompt. " +
                                          "Close this window and try again.";

    /// <summary>The preview when this PC could not be read.</summary>
    public static string ReadFailed(string why) =>
        $"PoolWatch could not read what runs on this PC, so it made no prompt: {why.Trim().TrimEnd('.')}. Close this window and try again.";
}

/// <summary>
/// What the "Decide with your AI agent" window shows and offers, without a window (so it is tested): the preview is the
/// whole prompt the Core built (<see cref="AgentPrompt.Build"/>), or why there is none, or that it is still being read;
/// Save and Copy are offered only once there is a prompt. The file is saved as <see cref="AgentPrompt.FileName"/> unless
/// the user names it otherwise, as the prompt's exact UTF-8 (no byte order mark), the bytes its size bound counts.
/// </summary>
internal sealed class AgentPromptModel
{
    public string? Prompt { get; private set; }
    public string? Error { get; private set; }

    /// <summary>There is a prompt to save or copy.</summary>
    public bool Ready => Prompt is not null;

    /// <summary>Every word of the prompt; or why there is none; or that PoolWatch is still reading this PC.</summary>
    public string Preview => Prompt ?? Error ?? AgentPromptText.Reading;

    /// <summary>The name the Save dialog offers.</summary>
    public string FileName => AgentPrompt.FileName;

    /// <summary>What Save writes: the prompt as built, in UTF-8 without a byte order mark.</summary>
    public byte[] FileBytes => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(Prompt ?? "");

    /// <summary>What Copy puts on the clipboard: the prompt with Windows line ends, so it pastes as lines anywhere.</summary>
    public string ClipboardText => (Prompt ?? "").ReplaceLineEndings("\r\n");

    public void Built(string prompt) => (Prompt, Error) = (prompt, null);

    /// <summary>
    /// No prompt: the list or the prompt could not be built. The Core's redaction giving up is said apart, as nothing is
    /// written rather than a word that was not checked.
    /// </summary>
    public void Failed(Exception ex) =>
        (Prompt, Error) = (null, ex is RegexMatchTimeoutException ? AgentPromptText.RedactionGaveUp : AgentPromptText.ReadFailed(ex.Message));
}

/// <summary>
/// What the two AI-agent windows share: the app's font and icon, a size from the approved design that never passes the
/// screen's working area (<see cref="FitTo"/>), and everything inside <see cref="Content"/>, so the tests lay it out at a
/// window's client size and scaling without the desktop cutting the window. <c>percent</c>: the display scaling the
/// tests stand in for (its text and its sizes); null uses the window's own.
/// </summary>
internal abstract class AgentWindow : Form
{
    private readonly int? _percent;
    private readonly Size _design, _least;
    private readonly Font _font;

    protected AgentWindow(Size design, Size least, int? percent)
    {
        _percent = percent;
        _design = design;
        _least = least;
        Icon = TrayIcons.App;
        _font = new Font("Segoe UI", Pt(9f));
        Font = _font;
        BackColor = SystemColors.Window;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        Size = S(design);
        Content = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = SystemColors.Window };
        Controls.Add(Content);
    }

    /// <summary>Every control of the window.</summary>
    internal Panel Content { get; }

    protected int S(int logical) => _percent is { } p ? logical * p / 100 : LogicalToDeviceUnits(logical);

    protected Size S(Size logical) => new(S(logical.Width), S(logical.Height));

    protected Padding Px(int l, int t, int r, int b) => new(S(l), S(t), S(r), S(b));

    /// <summary>A font size in points, at the scaling the tests stand in for.</summary>
    protected float Pt(float points) => points * (_percent ?? 100) / 100f;

    /// <summary>The width of a vertical scroll bar at this window's scaling.</summary>
    protected int ScrollBar => _percent is { } p ? SystemInformation.VerticalScrollBarWidth * p / 100 : SystemInformation.GetVerticalScrollBarWidthForDpi(DeviceDpi);

    /// <summary>The design's size, cut to <paramref name="area"/> (the screen's working area) where it is larger.</summary>
    internal static Size Within(Size want, Rectangle area) => new(Math.Min(want.Width, area.Width), Math.Min(want.Height, area.Height));

    /// <summary>Sizes the window as the design has it, never larger than <paramref name="area"/>, nor its least size larger than its size.</summary>
    internal void FitTo(Rectangle area)
    {
        var size = Within(S(_design), area);
        var least = S(_least);
        MinimumSize = new Size(Math.Min(least.Width, size.Width), Math.Min(least.Height, size.Height));
        Size = size;
    }

    /// <summary>A label that wraps at <paramref name="width"/> and shows its text as it is (an ampersand is not a shortcut key).</summary>
    protected static Label Words(string text, Font? font, Color colour, int width, Padding margin) => new()
    {
        Text = text, AutoSize = true, MaximumSize = new Size(Math.Max(1, width), 0), ForeColor = colour, UseMnemonic = false, Margin = margin,
        Font = font,
    };

    /// <summary>A button of the bar at the bottom, styled as the Services tab's.</summary>
    protected Button BarButton(string text, ListTabStyle.Kind kind) =>
        ListTabStyle.Style(new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = Px(12, 3, 12, 3), Margin = Px(8, 3, 0, 3) }, kind);

    /// <summary>The bar at the bottom: <paramref name="left"/>, then <paramref name="right"/> kept at the right end, a line above it.</summary>
    protected FlowLayoutPanel Bar(Control left, Control right)
    {
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = Px(24, 10, 24, 10), BackColor = SystemColors.Window };
        bar.Controls.AddRange([left, right]);
        ListTabStyle.KeepRight(bar, right, S(12));
        bar.Paint += (_, e) => { using var pen = new Pen(LineColour); e.Graphics.DrawLine(pen, 0, 0, bar.Width, 0); };
        return bar;
    }

    protected static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty };
        row.Controls.AddRange(controls);
        return row;
    }

    protected static FlowLayoutPanel Stack(Padding margin)
    {
        return new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = margin,
        };
    }

    protected static bool Contrast => SystemInformation.HighContrast;
    protected static Color InkColour => Contrast ? SystemColors.WindowText : ListTabStyle.Ink;
    protected static Color MutedColour => Contrast ? SystemColors.GrayText : ListTabStyle.Muted;
    protected static Color LineColour => Contrast ? SystemColors.WindowText : ListTabStyle.Line;

    /// <summary>The window's font goes with the window, after its controls.</summary>
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _font.Dispose();
    }
}

/// <summary>
/// "Decide with your AI agent", from the Services tab, as the approved app design has it: on the left the title, the three
/// steps (save the prompt, give it to your agent, import its answer) and the green "What your agent will see" panel; on the
/// right every word of the prompt (poolwatch-services.md), read-only and scrolling; at the bottom "Copy to clipboard", the
/// primary "Save the prompt…" and "Close". It reads this PC in the background when it opens. The prompt is written only
/// where the user picks in the Save dialog (<see cref="IAgentFiles.Write"/>, never through a link), and never anywhere on
/// its own. Created when opened and disposed when closed.
/// </summary>
internal sealed class AgentPromptForm : AgentWindow
{
    /// <summary>The preview's text: blue headings on a near-white page, as the design's.</summary>
    private static readonly Color PageColour = Color.FromArgb(0xfb, 0xfb, 0xfc), PageInk = Color.FromArgb(0x24, 0x29, 0x2f);

    private readonly Func<string> _build;
    private readonly IAgentFiles _files;
    private readonly IAgentWork _work;
    private readonly Font _title, _bold, _heading, _mono, _monoBold;

    /// <param name="build">Builds the prompt, off the UI thread (it reads this PC).</param>
    public AgentPromptForm(Func<string> build, IAgentFiles files, IAgentWork? work = null, int? percent = null)
        : base(new Size(900, 660), new Size(640, 440), percent)
    {
        _build = build;
        _files = files;
        _work = work ?? BackgroundWork.Instance;
        Text = AgentPromptText.Title;
        FormBorderStyle = FormBorderStyle.Sizable;
        _title = new Font("Segoe UI Semibold", Pt(14f));
        _bold = new Font("Segoe UI Semibold", Pt(9f));
        _heading = new Font("Segoe UI Semibold", Pt(9.5f));
        _mono = MonoFont(Pt(8.5f), FontStyle.Regular);
        _monoBold = MonoFont(Pt(8.5f), FontStyle.Bold);

        // The left column: 340 wide, as the design; it scrolls on a screen too short for it.
        int width = S(340);
        // Its padding, 22 on each side: wider than a scroll bar, so a column that scrolls never scrolls sideways too.
        int text = width - S(44);
        Column = new Panel { Dock = DockStyle.Left, Width = width, AutoScroll = true, BackColor = SystemColors.Window, AutoScrollMargin = new Size(0, S(20)) };
        Column.Paint += (_, e) => { using var pen = new Pen(LineColour); e.Graphics.DrawLine(pen, Column.ClientSize.Width - 1, 0, Column.ClientSize.Width - 1, Column.ClientSize.Height); };
        var column = Stack(Padding.Empty);
        column.Location = new Point(S(22), S(20));
        column.Controls.Add(Words(AgentPromptText.Title, _title, InkColour, text, Px(0, 0, 0, 10)));
        column.Controls.Add(Words(AgentPromptText.Lead, null, MutedColour, text, Px(0, 0, 0, 14)));
        int stepText = text - S(32);
        for (int i = 0; i < AgentPromptText.Steps.Count; i++)
        {
            var (step, words) = AgentPromptText.Steps[i];
            var badge = new StepBadge(i + 1, _bold) { Size = S(new Size(22, 22)), Margin = Px(0, 1, 10, 0) };
            var lines = Stack(Padding.Empty);
            lines.Controls.Add(Words(step, _bold, InkColour, stepText, Padding.Empty));
            lines.Controls.Add(Words(words, null, InkColour, stepText, Padding.Empty));
            var row = Row(badge, lines);
            row.Margin = Px(0, 0, 0, 10);
            StepRows.Add(row);
            column.Controls.Add(row);
        }
        Shared = new SoftPanel(S(8)) { Padding = Px(13, 11, 13, 11), Margin = Px(0, 4, 0, 0) };
        int inner = text - S(26);
        Shared.Controls.Add(new GreenNote(AgentPromptText.SharedHeading) { Font = _heading, Margin = Px(0, 0, 0, 6) });
        Shared.Controls.Add(Words(AgentPromptText.SharedOnly, null, InkColour, inner, Px(0, 0, 0, 6)));
        Shared.Controls.Add(Words(AgentPromptText.NeverLabel, _bold, InkColour, inner, Padding.Empty));
        Shared.Controls.Add(Words(AgentPromptText.NeverText, null, InkColour, inner, Padding.Empty));
        column.Controls.Add(Shared);
        Column.Controls.Add(column);

        // The preview: every word of the prompt, under a line that says so.
        var head = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 1, Padding = Px(18, 10, 18, 10) };
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        // One line: on a narrow window it ends in "…" and its tooltip has the whole of it.
        head.Controls.Add(new Label
        {
            Text = AgentPromptText.PreviewHeading, AutoSize = false, AutoEllipsis = true, Dock = DockStyle.Fill, Height = TextRenderer.MeasureText("Ag", Font).Height,
            ForeColor = MutedColour, UseMnemonic = false, Margin = Padding.Empty,
        }, 0, 0);
        head.Controls.Add(new Label { Text = AgentPromptText.PreviewHint, AutoSize = true, ForeColor = MutedColour, UseMnemonic = false, Margin = Px(12, 0, 0, 0) }, 1, 0);
        head.Paint += (_, e) => { using var pen = new Pen(LineColour); e.Graphics.DrawLine(pen, 0, head.Height - 1, head.Width, head.Height - 1); };
        Preview = new RichTextBox
        {
            Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, WordWrap = true, ScrollBars = RichTextBoxScrollBars.Vertical,
            DetectUrls = false, EnableAutoDragDrop = false, AutoWordSelection = false, Font = _mono,
            BackColor = Contrast ? SystemColors.Window : PageColour, ForeColor = Contrast ? SystemColors.WindowText : PageInk,
            AccessibleName = AgentPromptText.PreviewHeading,
        };
        var page = new Panel { Dock = DockStyle.Fill, Padding = Px(18, 14, 18, 14), BackColor = Preview.BackColor };
        page.Controls.Add(Preview);
        var right = new Panel { Dock = DockStyle.Fill, BackColor = Preview.BackColor };
        right.Controls.Add(page); // fill first, then the docked edge
        right.Controls.Add(head);

        // The bar: Copy, the primary Save, Close.
        Status = new Label { AutoSize = true, ForeColor = MutedColour, UseMnemonic = false, Margin = Px(0, 9, 12, 0) };
        CopyButton = BarButton(AgentPromptText.CopyButton, ListTabStyle.Kind.Plain);
        SaveButton = BarButton(AgentPromptText.SaveButton, ListTabStyle.Kind.Primary);
        CloseButton = BarButton(AgentPromptText.CloseButton, ListTabStyle.Kind.Plain);
        CopyButton.Click += (_, _) => Copy();
        SaveButton.Click += (_, _) => Save();
        CloseButton.Click += (_, _) => Close();
        CancelButton = CloseButton;
        Buttons = Row(CopyButton, SaveButton, CloseButton);
        var bar = Bar(Status, Buttons);

        Content.Controls.Add(right); // fill first, then the docked edges (the last added is the outermost)
        Content.Controls.Add(Column);
        Content.Controls.Add(bar);
        (Column.TabIndex, right.TabIndex, bar.TabIndex) = (0, 1, 2);
        Display();
    }

    internal AgentPromptModel Model { get; } = new();
    internal Panel Column { get; }
    internal List<FlowLayoutPanel> StepRows { get; } = [];
    internal SoftPanel Shared { get; }
    internal RichTextBox Preview { get; }
    internal Label Status { get; }
    internal FlowLayoutPanel Buttons { get; }
    internal Button CopyButton { get; }
    internal Button SaveButton { get; }
    internal Button CloseButton { get; }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Start();
    }

    /// <summary>Reads this PC and builds the prompt in the background, then shows it (or why there is none).</summary>
    internal void Start() => _work.Run(this, _build, ShowPrompt, ShowFailure);

    internal void ShowPrompt(string prompt)
    {
        Model.Built(prompt);
        Display();
    }

    internal void ShowFailure(Exception ex)
    {
        Model.Failed(ex);
        Display();
    }

    /// <summary>The preview and the buttons as <see cref="Model"/> has them; the prompt's headings in the design's blue.</summary>
    private void Display()
    {
        string text = Model.Preview;
        if (Preview.Text != text) Preview.Text = text;
        CopyButton.Enabled = SaveButton.Enabled = Model.Ready;
        if (!Model.Ready || Contrast) return;
        // Only lines that start with "#" are headings: every name on this PC is inside a table row, which starts with "|".
        int at = 0;
        foreach (string line in text.Split('\n'))
        {
            if (line.StartsWith('#'))
            {
                Preview.Select(at, line.Length);
                Preview.SelectionColor = ListTabStyle.Accent;
                Preview.SelectionFont = _monoBold;
            }
            at += line.Length + 1;
        }
        Preview.Select(0, 0);
    }

    /// <summary>"Save the prompt…": a Save dialog offering poolwatch-services.md, then the file written there and nowhere else.</summary>
    private void Save()
    {
        if (!Model.Ready || _files.AskSavePath(this, Model.FileName) is not { } path) return;
        if (_files.Write(path, Model.FileBytes) is { } error)
        {
            Status.Text = "";
            _files.Tell(this, error, "PoolWatch");
            return;
        }
        Status.Text = AgentPromptText.Saved(path);
    }

    private void Copy()
    {
        if (!Model.Ready) return;
        Status.Text = _files.Copy(Model.ClipboardText) ?? AgentPromptText.Copied;
    }

    /// <summary>Cascadia Mono, as the design's preview; Consolas, which every Windows has, when it is not installed.</summary>
    private static Font MonoFont(float points, FontStyle style)
    {
        var font = new Font("Cascadia Mono", points, style);
        if (font.Name == "Cascadia Mono") return font;
        font.Dispose();
        return new Font("Consolas", points, style);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        _title.Dispose();
        _bold.Dispose();
        _heading.Dispose();
        _mono.Dispose();
        _monoBold.Dispose();
    }
}

/// <summary>A step's number in a pale blue circle, as the design's.</summary>
internal sealed class StepBadge : Control
{
    private readonly Font _font;

    public StepBadge(int number, Font font)
    {
        _font = font;
        Text = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        BackColor = Color.Transparent;
        AccessibleRole = AccessibleRole.StaticText;
        AccessibleName = "Step " + Text;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        bool contrast = SystemInformation.HighContrast;
        var mode = e.Graphics.SmoothingMode;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var circle = new Rectangle(0, 0, Width - 1, Height - 1);
        if (contrast)
        {
            using var pen = new Pen(SystemColors.WindowText);
            e.Graphics.DrawEllipse(pen, circle);
        }
        else
        {
            using var fill = new SolidBrush(ListTabStyle.AccentSoft);
            e.Graphics.FillEllipse(fill, circle);
        }
        e.Graphics.SmoothingMode = mode;
        TextRenderer.DrawText(e.Graphics, Text, _font, ClientRectangle, contrast ? SystemColors.WindowText : ListTabStyle.Accent,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
    }
}

/// <summary>The design's pale green panel with round corners, sized to what it holds, one line under another.</summary>
internal sealed class SoftPanel : FlowLayoutPanel
{
    /// <summary>The design's pale green.</summary>
    public static readonly Color GoodSoft = UiColors.GoodSoft;

    private readonly int _radius;

    public SoftPanel(int radius)
    {
        _radius = radius;
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = SystemInformation.HighContrast ? SystemColors.Window : GoodSoft;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var outside = Parent?.BackColor ?? SystemColors.Window;
        using (var clear = new SolidBrush(outside)) e.Graphics.FillRectangle(clear, ClientRectangle);
        var mode = e.Graphics.SmoothingMode;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = new GraphicsPath();
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        int d = Math.Max(1, Math.Min(_radius * 2, Math.Min(r.Width, r.Height)));
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        using (var fill = new SolidBrush(BackColor)) e.Graphics.FillPath(fill, path);
        if (SystemInformation.HighContrast)
        {
            using var pen = new Pen(SystemColors.WindowText);
            e.Graphics.DrawPath(pen, path);
        }
        e.Graphics.SmoothingMode = mode;
    }
}
