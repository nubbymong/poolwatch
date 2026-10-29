using PoolWatch.Core.Cache;
using PoolWatch.Core.Recommendations;

namespace PoolWatch.Ui;

/// <summary>
/// "Set up PoolWatch": one scrollable window, not a wizard. It reads the PC first ("Looking at your PC..."), then shows
/// what it found and the first-run suggestions, each with one button that runs off the UI thread and becomes "Done" once it
/// worked (or shows the plain error), and the update notice's switch. It works on a new PC and when opened again from the
/// tray: what is already set up shows as done. On a Windows build that may have the token leak it runs the check by itself
/// (not in the CI UI test). Closing it (Done or X) marks the guide completed; open suggestions then live on in the Simple
/// view. The window sizes itself to its content (<see cref="GuidePage"/>) at any scaling and scrolls on a screen too small
/// for it. Created when opened, disposed when closed.
/// </summary>
internal sealed class SetupGuideForm : Form
{
    private const string Via = "the setup guide";
    private readonly SetupContext _context;
    private readonly GuidePage _page;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // What this opening of the guide knows besides the rules' inputs.
    private bool _scanned;
    private string? _leakNotChecked;                    // why the check could not run: its row says so, here only
    private readonly HashSet<string> _busy = [];        // rows whose button is running
    private readonly Dictionary<string, string> _errors = [];
    private List<(Recommendation Row, bool TestClear)> _rows = [];

    public SetupGuideForm(SetupContext context)
    {
        _context = context;
        Text = "Set up PoolWatch";
        Icon = TrayIcons.For(TrayState.Green);
        Font = new Font("Segoe UI", 9f);
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _page = new GuidePage(Font) { Location = Point.Empty };
        _page.Pressed += Press;
        _page.UpdatesSwitched += on =>
        {
            if (_context.Updates is { } updates) _context.Actions.Guarded(this, "Update notices", () => updates.Enabled = on);
        };
        _page.ActiveChangesClicked += _context.Shell.ShowActiveChanges;
        _page.DoneClicked += Close;
        Controls.Add(_page);
        Render();
    }

    /// <summary>CI: the PC has been read and the rows are shown.</summary>
    internal Task Ready => _ready.Task;

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // Always read the PC again: the guide is opened on demand, and it must show what is set up now.
        try { await _context.Probe.ScanAsync(); }
        catch (Exception ex) { _context.Engine.Log.Info("setup guide: the PC could not be read: " + ex.Message); }
        if (IsDisposed) return;
        _scanned = true;
        Render();
        _ready.TrySetResult();
        if (!_context.Smoke && _rows.Any(r => r.Row.State == RecommendationState.Checking)) await CheckTokenLeak();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _stop.Cancel(); // a running check stops; a test clear still finishes and is measured
        _context.Store.MarkGuideCompleted(DateTime.UtcNow);
        _ready.TrySetResult();
        base.OnFormClosed(e);
    }

    /// <summary>
    /// The first-run rows: the rules' rows, with the hourly clear decided by file system and what clears freed here, and
    /// whether that row's button is the test clear. <paramref name="leakNotChecked"/>: why the token-leak check could not
    /// run; its row says that instead of "Checking...", in this window only (the Simple view can still offer the check).
    /// </summary>
    internal static List<(Recommendation Row, bool TestClear)> Rows(RecommendationInputs inputs, ClearEvidence evidence, string? leakNotChecked) =>
        RecommendationRules.FirstRun(inputs)
            .Select(r => leakNotChecked is not null && r is { Id: RecommendationId.TokenLeakGuard, State: RecommendationState.Checking }
                ? r with { State = RecommendationState.Info, Why = leakNotChecked } : r)
            .Select(r => ClearAdvice.ForGuide(r, inputs.Facts, evidence)).ToList();

    /// <summary>Shows the page again from what is known now (a handful of times per opening).</summary>
    private void Render()
    {
        if (IsDisposed) return;
        _rows = _scanned ? Rows(_context.Inputs(), _context.ClearEvidence(), _leakNotChecked) : [];
        IReadOnlyList<string>? facts = _scanned ? _context.Probe.Last?.PlainLines() ?? [] : null;
        SuspendLayout();
        _page.Fill(new GuideState(facts, _rows, _scanned ? _context.Updates : null, _busy, _errors));
        AcceptButton = _page.DoneButton;
        CancelButton = _page.DoneButton;
        ResumeLayout(true);
        FitToScreen();
    }

    /// <summary>A row's button: its change off the UI thread, then the rows again (a change that worked shows as done).</summary>
    private async void Press(Recommendation r, bool testClear)
    {
        // Leave the button's click first: the rows, this button with them, are built again below.
        await Task.Yield();
        _busy.Add(r.Key);
        _errors.Remove(r.Key);
        Render();
        try
        {
            var (ok, message) = testClear ? await _context.Recommendations.TestClear(_stop.Token) : await _context.Recommendations.Run(r, this, Via);
            if (!ok) _errors[r.Key] = message;
        }
        catch (OperationCanceledException) { return; } // the window closed meanwhile
        catch (Exception ex) { _errors[r.Key] = "That did not work: " + ex.Message; }
        finally { _busy.Remove(r.Key); }
        Render();
    }

    /// <summary>The token-leak check on its own (about 20 seconds); its row shows the verdict, or why it could not run.</summary>
    private async Task CheckTokenLeak()
    {
        var (ok, message) = await _context.Recommendations.CheckTokenLeak(_stop.Token);
        if (IsDisposed || _stop.IsCancellationRequested) return;
        if (!ok) _leakNotChecked = message;
        Render();
    }

    /// <summary>
    /// On a screen too short for the whole window (high scaling on a small display) it keeps its width and scrolls
    /// vertically instead of running off the screen, and a window that grew past the bottom moves up
    /// (<see cref="ScreenFit.IntoArea"/>).
    /// </summary>
    private void FitToScreen() => ScreenFit.IntoArea(this, Screen.FromControl(this).WorkingArea);

    /// <summary>CI: what the guide found and offers, each row's state and button, the update switch, its closing button, and anything clipped.</summary>
    internal object SmokeStats()
    {
        var problems = _page.LayoutProblems();
        if (_page.Width > ClientSize.Width + 1) problems.Add($"the guide's content ({_page.Width} px) is wider than its window ({ClientSize.Width} px)");
        return new
        {
            facts = _context.Probe.Last?.PlainLines() ?? [],
            rows = _rows.Select(r => new { title = r.Row.Title, state = r.Row.State.ToString(), button = r.Row.Button, why = r.Row.Why }).ToList(),
            updates = _context.Updates is { } u ? (bool?)u.Enabled : null,
            done = _page.DoneButton?.Text,
            layoutProblems = problems,
        };
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _stop.Dispose();
    }
}

/// <summary>What the setup guide shows at one moment.</summary>
/// <param name="Facts">What PoolWatch found on this PC, a line each; null while it reads the PC, empty when it could not.</param>
/// <param name="Rows">The first-run rows, and whether a row's button is the test clear.</param>
/// <param name="Updates">The update notice's switch; null leaves it out.</param>
/// <param name="Busy">The rows whose button is running, by key.</param>
/// <param name="Errors">The plain error a row's button gave, by key.</param>
internal sealed record GuideState(IReadOnlyList<string>? Facts, IReadOnlyList<(Recommendation Row, bool TestClear)> Rows, IUpdateCheck? Updates,
    IReadOnlySet<string> Busy, IReadOnlyDictionary<string, string> Errors);

/// <summary>
/// The setup guide's content: one column 620 logical pixels wide whose texts wrap within it, every font made from the one it
/// is given, so it lays out the same way at any scaling or text size. <see cref="Fill"/> builds it again from scratch;
/// <see cref="LayoutProblems"/> judges the layout itself, so it holds before the window is shown.
/// </summary>
internal sealed class GuidePage : TableLayoutPanel
{
    private static readonly Color DoneColor = Color.FromArgb(0, 110, 50), ErrorColor = Color.Firebrick;
    private readonly int _width;
    private readonly Font _titleFont, _headingFont, _boldFont; // the labels never dispose the fonts they are given: the page does

    /// <param name="font">The text's font; the title, headings and bold lines are sized from it (as 12, 10.5 and 9.5 to its 9 points).</param>
    public GuidePage(Font font)
    {
        Font = font;
        float Points(float atNine) => atNine * font.SizeInPoints / 9f;
        _titleFont = new Font(font.FontFamily, Points(12f), FontStyle.Bold);
        _headingFont = new Font("Segoe UI Semibold", Points(10.5f));
        _boldFont = new Font("Segoe UI Semibold", Points(9.5f));
        _width = S(620);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        ColumnCount = 1;
        Padding = Px(18, 14, 18, 14);
        ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, _width));
    }

    /// <summary>A row's button was pressed, and whether it is the test clear.</summary>
    public event Action<Recommendation, bool>? Pressed;

    /// <summary>The update notice's switch was turned; the switch then shows what is in effect, also when the change did not work.</summary>
    public event Action<bool>? UpdatesSwitched;

    public event Action? ActiveChangesClicked;
    public event Action? DoneClicked;

    /// <summary>The [Done] button, for the window's Enter and Esc.</summary>
    public Button? DoneButton { get; private set; }

    /// <summary>Builds the content again for <paramref name="state"/>.</summary>
    public void Fill(GuideState state)
    {
        SuspendLayout();
        foreach (var old in Controls.Cast<Control>().ToList())
        {
            Controls.Remove(old);
            old.Dispose();
        }

        Add(Para("Set up PoolWatch", _titleFont), bottom: 4);
        Add(Para("What PoolWatch found on this PC, and what it suggests. Each suggestion is one click, and you can undo every change later."), bottom: 12);

        Add(Para("Your PC", _headingFont), bottom: 4);
        if (state.Facts is not { } facts) Add(Para("Looking at your PC..."), bottom: 12);
        else if (facts.Count == 0) Add(Para("PoolWatch could not read this PC's details. Open the guide again from the tray to try again."), bottom: 12);
        else for (int i = 0; i < facts.Count; i++) Add(Para(facts[i]), bottom: i == facts.Count - 1 ? 12 : 2);

        var suggested = state.Rows.Where(r => r.Row.Id != RecommendationId.ReviewServices).ToList();
        if (suggested.Count > 0 || state.Updates is not null)
        {
            Add(Para("Suggested for this PC", _headingFont), bottom: 6);
            foreach (var (row, testClear) in suggested) Add(Row(row, testClear, state), bottom: 10);
            if (state.Updates is { } updates) Add(UpdateSwitch(updates), bottom: 12);
        }
        if (state.Rows.FirstOrDefault(r => r.Row.Id == RecommendationId.ReviewServices).Row is { } services)
        {
            Add(Para("Worth a look later", _headingFont), bottom: 6);
            Add(Row(services, testClear: false, state), bottom: 12);
        }

        Add(Footer(), bottom: 0);
        ResumeLayout(true);
    }

    /// <summary>
    /// A control outside the one around it, or a text, button or switch bigger than the space it has. Judged from the layout
    /// itself, so it holds before the window is shown (the unit tests, the CI UI test).
    /// </summary>
    internal List<string> LayoutProblems()
    {
        PerformLayout();
        var problems = new List<string>();
        Check(this, problems);
        return problems;

        static void Check(Control parent, List<string> problems)
        {
            foreach (Control c in parent.Controls)
            {
                string name = $"{c.GetType().Name} \"{(c.Text.Length > 40 ? c.Text[..40] + "..." : c.Text)}\"";
                if (!parent.ClientRectangle.Contains(c.Bounds)) problems.Add($"{name} at {c.Bounds} is outside its {parent.GetType().Name} ({parent.ClientSize})");
                // A label with a maximum width wraps: its preferred size is the wrapped one.
                if (c is Label or ButtonBase && c.GetPreferredSize(Size.Empty) is var need && (need.Width > c.Width + 1 || need.Height > c.Height + 1))
                    problems.Add($"{name} needs {need}, has {c.Size}");
                Check(c, problems);
            }
        }
    }

    private void Add(Control c, int bottom)
    {
        c.Margin = new Padding(0, 0, 0, S(bottom));
        Controls.Add(c);
    }

    /// <summary>A wrapped text (a label with a maximum width wraps; its height follows its text).</summary>
    private Label Para(string text, Font? font = null, Color? color = null, int? width = null) => new()
    {
        Text = text, AutoSize = true, UseMnemonic = false, MaximumSize = new Size(width ?? _width, 0), Margin = Padding.Empty,
        Font = font ?? Font, ForeColor = color ?? SystemColors.ControlText,
    };

    /// <summary>One suggestion: title and why on the left (with what you will notice for the token-leak guard), its button, "Done" or nothing on the right.</summary>
    private Control Row(Recommendation r, bool testClear, GuideState state)
    {
        bool busy = state.Busy.Contains(r.Key);
        Control? right = r.State switch
        {
            RecommendationState.Offer => ActionButton(r, testClear, busy),
            RecommendationState.Done => Para("Done", _boldFont, DoneColor),
            _ => null,
        };
        int rightWidth = right?.GetPreferredSize(Size.Empty).Width ?? 0;
        int textWidth = _width - (right is null ? 0 : rightWidth + S(16));
        var texts = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty };
        void Line(string text, Font? font = null, Color? color = null)
        {
            var l = Para(text, font, color, textWidth);
            l.Margin = new Padding(0, 0, 0, S(2));
            texts.Controls.Add(l);
        }
        Line(r.Title, _boldFont);
        bool leakOffer = r is { Id: RecommendationId.TokenLeakGuard, State: RecommendationState.Offer };
        Line(leakOffer ? TokenGuardWords.Why : r.Why);
        if (leakOffer) Line(TokenGuardWords.Details, color: SystemColors.GrayText);
        if (busy) Line(testClear ? "Clearing the cache once and measuring what it frees (about 2 minutes)..." : "Working...", color: SystemColors.GrayText);
        if (state.Errors.TryGetValue(r.Key, out var error)) Line(error, color: ErrorColor);

        var row = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 1 };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, textWidth + (right is null ? 0 : S(16))));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.Controls.Add(texts, 0, 0);
        if (right is not null)
        {
            right.Margin = Padding.Empty;
            row.Controls.Add(right, 1, 0);
        }
        return row;
    }

    /// <summary>A row's button. It carries its font itself: it is measured before it joins its row.</summary>
    private Button ActionButton(Recommendation r, bool testClear, bool busy)
    {
        var b = new Button { Text = r.Button, Font = Font, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = Px(6, 2, 6, 2), Enabled = !busy, UseVisualStyleBackColor = true };
        b.Click += (_, _) => Pressed?.Invoke(r, testClear);
        return b;
    }

    /// <summary>The update notice's switch: on by default, applied at once (it is a setting, not a change to the PC).</summary>
    private Control UpdateSwitch(IUpdateCheck updates)
    {
        var box = new CheckBox { Text = "Tell me when a new version is out.", AutoSize = true, Checked = updates.Enabled, Margin = Padding.Empty };
        box.CheckedChanged += (_, _) =>
        {
            if (box.Checked == updates.Enabled) return; // put back below
            UpdatesSwitched?.Invoke(box.Checked);
            box.Checked = updates.Enabled; // what is in effect: unchanged when the change did not work
        };
        var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        panel.Controls.Add(box);
        var note = Para("Checks GitHub once a day; information only, nothing is downloaded or installed.", color: SystemColors.GrayText, width: _width - S(18));
        note.Margin = new Padding(S(18), 0, 0, 0);
        panel.Controls.Add(note);
        return panel;
    }

    private Control Footer()
    {
        // Measured before it joins the footer: it carries its font itself.
        var done = new Button { Text = "Done", Font = Font, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = Px(14, 2, 14, 2), UseVisualStyleBackColor = true };
        var line = new LinkLabel
        {
            Text = "Every change is listed under Active changes, where you can undo it.", AutoSize = true, UseMnemonic = false,
            MaximumSize = new Size(_width - done.GetPreferredSize(Size.Empty).Width - S(12), 0),
        };
        line.LinkArea = new LinkArea(line.Text.IndexOf("Active changes", StringComparison.Ordinal), "Active changes".Length);
        line.LinkClicked += (_, _) => ActiveChangesClicked?.Invoke();
        done.Click += (_, _) => DoneClicked?.Invoke();
        DoneButton = done;
        var footer = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 1, Dock = DockStyle.Fill, Padding = Px(0, 6, 0, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        line.Margin = new Padding(0, S(6), S(12), 0);
        done.Margin = Padding.Empty;
        footer.Controls.Add(line, 0, 0);
        footer.Controls.Add(done, 1, 0);
        return footer;
    }

    private int S(int logical) => LogicalToDeviceUnits(logical);
    private Padding Px(int l, int t, int r, int b) => new(S(l), S(t), S(r), S(b));

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _titleFont.Dispose();
            _headingFont.Dispose();
            _boldFont.Dispose();
        }
    }
}
