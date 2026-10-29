using System.Globalization;
using System.Text;
using PoolWatch.Core.Changes;
using PoolWatch.Engine.Changes;

namespace PoolWatch.Ui;

/// <summary>
/// Active changes: every lasting change PoolWatch made on this PC that is still in effect, in three groups: the features
/// you turned on (each with Turn off, or Remove for a shortcut), what PoolWatch turned off (each with Put back), and any
/// other change (Put back). A row says what it is and how it was before; one PoolWatch cannot put back says so and why, and
/// one the uninstaller leaves says so too. Put all back first lists the changes and their caveats and then asks each
/// warning on its own. Both put back only what the window showed, as it showed it. The list is read off the UI thread when the window opens and again after each action. Opened
/// from the main window's footer, the setup guide and the tray menu; created when opened and disposed when closed, so it
/// costs nothing while it is not shown. The logic is in <see cref="ActiveChangesView"/>, which the tests drive with no window.
/// </summary>
internal sealed class ActiveChangesForm : Form
{
    private readonly ChangeLedger _ledger;
    private readonly UiActions _actions;
    private readonly SynchronizationContext _ui;
    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ToolTip _tips = new();
    private readonly Label _status = new() { AutoSize = true, UseMnemonic = false };
    private readonly Label _notListed = new() { Text = ChangeWords.NotListed, AutoSize = true, UseMnemonic = false };
    /// <summary>The footer's promise and its shield: shown only over a list with rows, so "all of these" always has something to point at.</summary>
    private readonly Label _uninstallNote = new() { Text = "", Visible = false, AutoSize = true, UseMnemonic = false, Anchor = AnchorStyles.Left };
    private readonly Label _shield;
    /// <summary>A UserControl, not a Panel, so the layout check does not report the scrolled part of a long list as
    /// outside the window; the rows are checked on their own (<see cref="SmokeStats"/>).</summary>
    private readonly UserControl _scroll = new() { AutoScroll = true, Dock = DockStyle.Fill, Margin = Padding.Empty };
    private readonly FlowLayoutPanel _list = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty };
    private readonly Button _putAllBack = new() { Text = ChangeWords.PutAllBack, AutoSize = true, Enabled = false, UseVisualStyleBackColor = true };
    private readonly Button _close = new() { Text = "Close", AutoSize = true };
    private readonly Font _bold, _heading, _small;
    private readonly Font? _glyphs;
    private readonly int _textWidth;
    private IReadOnlyList<ActiveChange> _changes = [];

    public ActiveChangesForm(ChangeLedger ledger, UiActions actions)
    {
        _ledger = ledger;
        _actions = actions;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        Text = ChangeWords.WindowTitle;
        Icon = TrayIcons.For(TrayState.Green);
        Font = new Font("Segoe UI", 9f);
        _bold = new Font(Font, FontStyle.Bold);
        _heading = new Font(Font.FontFamily, 14f, FontStyle.Bold);
        _small = new Font(Font.FontFamily, 8f);
        _glyphs = Glyphs.Font();
        BackColor = Palette.Window;
        ForeColor = Palette.Ink;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = new Size(S(ActiveChangesView.ClientWidth), S(ActiveChangesView.ClientHeight));

        int side = S(24), width = ClientSize.Width - 2 * side;
        // The text column leaves room for the row's glyph, its button and the list's scroll bar.
        _textWidth = width - SystemInformation.GetVerticalScrollBarWidthForDpi(DeviceDpi) - S(22 + 12) - S(104);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = Padding.Empty, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // heading
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // lead
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // status
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // the list
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // not listed
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));  // the footer's line
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // the footer
        var heading = new Label { Text = ChangeWords.WindowTitle, Font = _heading, AutoSize = true, UseMnemonic = false, Margin = new Padding(side, S(18), side, S(2)) };
        var lead = new Label
        {
            Text = ChangeWords.Lead, AutoSize = true, UseMnemonic = false, ForeColor = Palette.Muted, MaximumSize = new Size(width, 0),
            Margin = new Padding(side, 0, side, S(10)),
        };
        _status.ForeColor = Palette.Muted;
        _status.MaximumSize = _notListed.MaximumSize = new Size(width, 0);
        _status.Margin = new Padding(side, 0, side, S(8));
        _scroll.Margin = new Padding(side, 0, S(8), 0);
        _notListed.ForeColor = Palette.Faint;
        _notListed.Font = _small;
        _notListed.Margin = new Padding(side, S(8), side, S(8));
        _scroll.Controls.Add(_list);
        var line = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Palette.Line };

        _putAllBack.Click += (_, _) => _actions.Guarded(this, ChangeWords.PutAllBack, PutAllBack);
        _tips.SetToolTip(_putAllBack, ActiveChangesView.PutAllBackTip);
        _close.Click += (_, _) => Close();
        Palette.Primary(_close, S(8));
        CancelButton = _close;
        _uninstallNote.ForeColor = Palette.Good;
        _uninstallNote.MaximumSize = new Size(width - S(230), 0);
        var footer = new TableLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 4, RowCount = 1, Dock = DockStyle.Fill,
            Margin = Padding.Empty, Padding = new Padding(side, S(10), side, S(10)), BackColor = Palette.Window,
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _shield = Glyph(Glyphs.Shield, Palette.Good);
        _shield.Margin = new Padding(0, 0, S(6), 0);
        _shield.Anchor = AnchorStyles.Left;
        _shield.Visible = false;
        _uninstallNote.Margin = Padding.Empty;
        _putAllBack.Margin = new Padding(S(8), 0, 0, 0);
        _close.Margin = new Padding(S(8), 0, 0, 0);
        footer.Controls.Add(_shield, 0, 0);
        footer.Controls.Add(_uninstallNote, 1, 0);
        footer.Controls.Add(_putAllBack, 2, 0);
        footer.Controls.Add(_close, 3, 0);
        layout.Controls.Add(heading, 0, 0);
        layout.Controls.Add(lead, 0, 1);
        layout.Controls.Add(_status, 0, 2);
        layout.Controls.Add(_scroll, 0, 3);
        layout.Controls.Add(_notListed, 0, 4);
        layout.Controls.Add(line, 0, 5);
        layout.Controls.Add(footer, 0, 6);
        Controls.Add(layout);
    }

    private int S(int logical) => LogicalToDeviceUnits(logical);

    /// <summary>CI: completes once the list read on opening is shown.</summary>
    internal Task Loaded => _loaded.Task;

    /// <summary>Tests: the tips of the buttons, the rows' and Put all back's.</summary>
    internal ToolTip Tips => _tips;

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // On a working area shorter than the window (a small screen at a high scaling), the window becomes as tall as the
        // area and the list scrolls; it is never taller or wider than the screen.
        var area = Screen.FromControl(this).WorkingArea;
        var fitted = ActiveChangesView.Fit(Size, area.Size);
        if (fitted != Size)
        {
            Size = fitted;
            CenterToScreen();
        }
        Reload();
    }

    /// <summary>Reads the list on a worker thread (COM, the service control manager, the registry), then shows it here.</summary>
    private void Reload()
    {
        SetBusy("Reading the changes...");
        Task.Run(_ledger.List).ContinueWith(t => _ui.Post(_ =>
        {
            if (IsDisposed) return;
            if (t.IsFaulted) ShowList([], "PoolWatch could not read the changes: " + t.Exception!.GetBaseException().Message);
            else ShowList(t.Result, t.Result.Count == 0 ? ChangeWords.NothingActive : null);
            _loaded.TrySetResult();
        }, null), TaskScheduler.Default);
    }

    /// <summary>While a read or a put-back runs, nothing else can be started.</summary>
    private void SetBusy(string status)
    {
        foreach (var b in RowButtons()) b.Enabled = false;
        _putAllBack.Enabled = false;
        ShowStatus(status);
    }

    private IEnumerable<Button> RowButtons() => Descendants(_list).OfType<Button>();

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control c in parent.Controls)
        {
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }

    private void ShowStatus(string? status)
    {
        _status.Text = status ?? "";
        _status.Visible = status is not null;
    }

    /// <summary>Shows the rows in their groups (the tests call it with no window shown).</summary>
    internal void ShowList(IReadOnlyList<ActiveChange> changes, string? status)
    {
        _changes = changes;
        _list.SuspendLayout();
        while (_list.Controls.Count > 0) _list.Controls[0].Dispose();
        // The old rows' tips go with them; Put all back keeps its own.
        _tips.RemoveAll();
        _tips.SetToolTip(_putAllBack, ActiveChangesView.PutAllBackTip);
        bool first = true;
        foreach (var section in ActiveChangesView.Sections(changes))
        {
            _list.Controls.Add(SectionHeading(section.Heading, first));
            first = false;
            foreach (var c in section.Rows) _list.Controls.Add(Row(c));
        }
        _list.ResumeLayout();
        _putAllBack.Enabled = changes.Any(c => c.CanUndo);
        string? note = ActiveChangesView.UninstallNote(changes);
        _uninstallNote.Text = note ?? "";
        _uninstallNote.Visible = _shield.Visible = note is not null;
        ShowStatus(status);
    }

    private Label SectionHeading(string text, bool first) => new()
    {
        Text = text.ToUpper(CultureInfo.CurrentCulture), Font = _small, ForeColor = Palette.Faint, AutoSize = true, UseMnemonic = false,
        Margin = new Padding(0, first ? 0 : S(14), 0, S(4)),
    };

    /// <summary>One change: a glyph, its title (bold) and lines (what it is and how it was, its caveats), and its button on the right.</summary>
    private Control Row(ActiveChange c)
    {
        var row = new TableLayoutPanel
        {
            ColumnCount = 3, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty,
            Padding = new Padding(0, S(8), 0, S(8)),
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(22 + 12)));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, _textWidth));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var glyph = Glyph(Glyphs.For(c), Palette.Muted);
        glyph.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        var text = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty };
        text.Controls.Add(Line(c.Title, _bold, Palette.Ink));
        foreach (var (line, kind) in ActiveChangesView.Lines(c, CultureInfo.CurrentCulture))
            text.Controls.Add(Line(line, Font, kind switch { LineKind.Caveat => Palette.Warn, _ => Palette.Faint }));
        var button = new Button
        {
            Text = ActiveChangesView.ButtonText(c), AutoSize = true, Enabled = c.CanUndo, UseVisualStyleBackColor = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Right, Margin = new Padding(S(8), 0, 0, 0),
        };
        _tips.SetToolTip(button, ActiveChangesView.ButtonTip(c));
        button.Click += (_, _) => _actions.Guarded(this, ActiveChangesView.ButtonText(c), () => PutBackOne(c));
        row.Controls.Add(glyph, 0, 0);
        row.Controls.Add(text, 1, 0);
        row.Controls.Add(button, 2, 0);
        // The line under each row, as the design's list draws it.
        var framed = new TableLayoutPanel { ColumnCount = 1, RowCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty };
        framed.Controls.Add(row, 0, 0);
        framed.Controls.Add(new Panel { Height = 1, Width = S(22 + 12) + _textWidth + S(104), BackColor = Palette.RowLine, Margin = Padding.Empty }, 0, 1);
        return framed;
    }

    private Label Line(string text, Font font, Color color) => new()
    {
        Text = text, Font = font, ForeColor = color, AutoSize = true, UseMnemonic = false, MaximumSize = new Size(_textWidth, 0), Margin = new Padding(0, 0, 0, S(2)),
    };

    private Label Glyph(string glyph, Color color) => new()
    {
        Text = _glyphs is null ? "" : glyph, Font = _glyphs ?? Font, ForeColor = color, AutoSize = true, UseMnemonic = false, Margin = new Padding(0, S(1), S(12), 0),
    };

    private void PutBackOne(ActiveChange c)
    {
        if (c.Warning is { } warning && !PutBackAnyway(warning)) return;
        Run(() => ActiveChangesView.PutBackOne(_ledger, c));
    }

    private void PutAllBack()
    {
        var shown = _changes;
        if (MessageBox.Show(this, ActiveChangesView.PutAllBackQuestion(shown), "PoolWatch: put all back", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        var plan = ActiveChangesView.PutAllBackPlan(shown, PutBackAnyway);
        Run(() => ActiveChangesView.PutAllBack(_ledger, plan));
    }

    private bool PutBackAnyway(string warning) => MessageBox.Show(this, warning + "\n\nPut it back anyway?", "PoolWatch", MessageBoxButtons.YesNo,
        MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    /// <summary>Runs a put-back on a worker thread, shows what happened, then reads the list again.</summary>
    private void Run(Func<(string Text, bool Done)> putBack)
    {
        SetBusy("Putting it back...");
        Task.Run(putBack).ContinueWith(t => _ui.Post(_ =>
        {
            if (IsDisposed) return;
            var (text, done) = t.IsFaulted ? ("It did not work: " + t.Exception!.GetBaseException().Message, false) : t.Result;
            MessageBox.Show(this, text, "PoolWatch", MessageBoxButtons.OK, done ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            Reload();
        }, null), TaskScheduler.Default);
    }

    /// <summary>CI: the rows (what, whether it can be put back, the caveat, its group and button), the buttons, the notes, and layout problems.</summary>
    internal object SmokeStats()
    {
        var problems = new List<string>();
        MainForm.ClippedControls(this, problems);
        MainForm.ClippedControls(_list, problems);
        return new
        {
            title = Text,
            rows = _changes.Select(c => new
            {
                what = c.What, title = c.Title, detail = c.Detail, canUndo = c.CanUndo, note = c.Note, group = c.Group.ToString(),
                button = ActiveChangesView.ButtonText(c), uninstallLeaves = c.UninstallLeaves,
            }).ToList(),
            // Each row's own button (Turn off, Remove or Put back), enabled or not.
            undoButtons = RowButtons().Count(),
            buttons = new[] { _putAllBack.Text, _close.Text },
            notListed = _notListed.Text,
            uninstallNote = _uninstallNote.Text,
            layoutProblems = problems,
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tips.Dispose();
            _bold.Dispose();
            _heading.Dispose();
            _small.Dispose();
            _glyphs?.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>The window's colours, as the approved design has them; the system's own in high contrast.</summary>
    internal static class Palette
    {
        private static bool Contrast => SystemInformation.HighContrast;
        public static Color Window => Contrast ? SystemColors.Window : Color.White;
        public static Color Ink => Contrast ? SystemColors.WindowText : Color.FromArgb(0x1f, 0x23, 0x28);
        public static Color Muted => Contrast ? SystemColors.WindowText : Color.FromArgb(0x59, 0x63, 0x6e);
        public static Color Faint => Contrast ? SystemColors.GrayText : Color.FromArgb(0x66, 0x70, 0x7a);
        public static Color Good => Contrast ? SystemColors.WindowText : Color.FromArgb(0x1a, 0x7f, 0x37);
        public static Color Warn => Contrast ? SystemColors.WindowText : Color.FromArgb(0x9a, 0x67, 0x00);
        public static Color Line => Contrast ? SystemColors.WindowFrame : Color.FromArgb(0xe3, 0xe6, 0xea);
        public static Color RowLine => Contrast ? SystemColors.WindowFrame : Color.FromArgb(0xee, 0xf0, 0xf2);
        private static readonly Color Accent = Color.FromArgb(0x0f, 0x6c, 0xbd);

        /// <summary>The window's one primary button, filled with the accent colour (a standard button in high contrast).</summary>
        /// <param name="pad">The padding on each side, in the window's device pixels (8 logical).</param>
        public static void Primary(Button b, int pad)
        {
            if (Contrast) { b.UseVisualStyleBackColor = true; return; }
            b.FlatStyle = FlatStyle.Flat;
            b.BackColor = Accent;
            b.ForeColor = Color.White;
            b.FlatAppearance.BorderColor = Accent;
            b.Padding = new Padding(pad, 0, pad, 0);
        }
    }

    /// <summary>The row glyphs, from Windows' own icon font (Segoe MDL2 Assets, on every Windows 10 and 11); none without it.</summary>
    private static class Glyphs
    {
        // Segoe MDL2 Assets code points, written as escapes so they can be read: the shield, power, broom, key, link,
        // gear, clock and warning glyphs.
        public const string Shield = "\uEA18", Power = "\uE7E8", Sweep = "\uEA99", Key = "\uE8D7", Link = "\uE71B", Gear = "\uE713",
            Clock = "\uE823", Warning = "\uE7BA";

        public static Font? Font()
        {
            var f = new Font("Segoe MDL2 Assets", 11f);
            if (f.Name == "Segoe MDL2 Assets") return f;
            f.Dispose(); // Windows gave another font: its letters would show instead of the glyphs
            return null;
        }

        public static string For(ActiveChange c) => !c.CanUndo ? Warning : c.Kind switch
        {
            ChangeKind.LogonTask or ChangeKind.StartupEntry => Power,
            ChangeKind.AutoClear => Sweep,
            ChangeKind.TokenGuard => Key,
            ChangeKind.Shortcut => Link,
            ChangeKind.Service => Gear,
            _ => Clock,
        };
    }
}

/// <summary>How a line under a row's title is shown: a plain detail, or a caveat (what cannot be put back, and by what).</summary>
internal enum LineKind { Detail, Caveat }

/// <summary>
/// The Active changes window's logic with no window: its groups, each row's lines and button, the footer's note, and Put
/// back and Put all back over the ledger, with the questions answered through delegates (the tests answer them).
/// </summary>
internal static class ActiveChangesView
{
    /// <summary>The window's client area in logical pixels (the approved design's 680 x 560 window, title bar included).</summary>
    internal const int ClientWidth = 660, ClientHeight = 500;

    internal sealed record Section(string Heading, IReadOnlyList<ActiveChange> Rows);

    /// <summary>The groups in the window's order (features, turned off, other), each with its rows as listed (newest first); empty ones left out.</summary>
    internal static IReadOnlyList<Section> Sections(IReadOnlyList<ActiveChange> changes) =>
        new[] { (ChangeGroup.Feature, ChangeWords.FeaturesHeading), (ChangeGroup.TurnedOff, ChangeWords.TurnedOffHeading), (ChangeGroup.Other, ChangeWords.OtherHeading) }
            .Select(g => new Section(g.Item2, changes.Where(c => c.Group == g.Item1).ToList()))
            .Where(s => s.Rows.Count > 0)
            .ToList();

    /// <summary>A feature is turned off (a shortcut removed); anything else is put back.</summary>
    internal static string ButtonText(ActiveChange c) => ActiveChange.ButtonOf(c);

    /// <summary>What the row's button does, in plain words (its tooltip).</summary>
    internal static string ButtonTip(ActiveChange c)
    {
        if (!c.CanUndo) return ChangeWords.CannotPutBack;
        return c.Kind switch
        {
            ChangeKind.LogonTask => "Removes PoolWatch's sign-in task: PoolWatch no longer starts by itself when you sign in.",
            ChangeKind.AutoClear => "Stops the automatic cache clears. Clear cache now still works.",
            ChangeKind.TokenGuard => ChangeWords.TokenGuardTurnOffTip,
            ChangeKind.Shortcut => "Deletes the shortcut, if it is still as PoolWatch made it. A shortcut you changed stays.",
            ChangeKind.Service => "Puts the service back to how it was before PoolWatch changed it, and starts it again if it was running.",
            ChangeKind.ScheduledTask => "Turns the scheduled task back to how it was before PoolWatch changed it.",
            ChangeKind.StartupEntry => "Turns the startup app back to how it was before PoolWatch changed it.",
            ChangeKind.RemovedClearTask => "Puts your clear task back in Task Scheduler, and turns PoolWatch's automatic clear off so the two never both run.",
            _ => "Puts it back to how it was.",
        };
    }

    internal const string PutAllBackTip = "Turns off every feature listed here and puts back everything PoolWatch turned off. It asks first.";

    /// <summary>
    /// The lines under a row's title: what it is and how it was (with when, for a change with a time), its note, and its
    /// caveats: that PoolWatch cannot put it back, or that uninstalling does not.
    /// </summary>
    internal static IReadOnlyList<(string Text, LineKind Kind)> Lines(ActiveChange c, CultureInfo culture)
    {
        var lines = new List<(string, LineKind)>();
        string? detail = c.Detail;
        if (c.ChangedUtc is { } t) detail = (detail is null ? "" : detail + " · ") + ChangeWords.Since(t.ToLocalTime(), culture);
        if (detail is not null) lines.Add((detail, LineKind.Detail));
        if (!c.CanUndo)
        {
            lines.Add((ChangeWords.CannotPutBackLine(c.Note), LineKind.Caveat));
            return lines;
        }
        if (c.Note is { } note) lines.Add((note, LineKind.Detail));
        if (c.UninstallLeaves is { } leaves) lines.Add((ChangeWords.UninstallLeaves + " " + leaves, LineKind.Caveat));
        return lines;
    }

    /// <summary>
    /// The footer: uninstalling puts all of these back, unless a row says it does not; null with no rows (nothing listed,
    /// or the list could not be read), when the window makes no promise about "these".
    /// </summary>
    internal static string? UninstallNote(IReadOnlyList<ActiveChange> changes) =>
        changes.Count == 0 ? null
        : changes.Any(c => c.UninstallLeaves is not null) ? ChangeWords.UninstallPutsBackTheOthers
        : ChangeWords.UninstallPutsAllBack;

    /// <summary>
    /// The window's size on a working area of <paramref name="area"/>: as designed, never taller or wider than the area.
    /// The list inside scrolls, not the window, so no room is added for a scroll bar (ScreenFit's rule, with none).
    /// </summary>
    internal static Size Fit(Size window, Size area) => ScreenFit.Capped(window, area, scrollBar: 0);

    /// <summary>At most this many changes are listed in Put all back's question and in its results, so each box fits on the screen.</summary>
    private const int MaxListed = 10;

    /// <summary>
    /// Put all back's question: what it does, then each change with its caveat (a warning to accept, or why it cannot be
    /// put back), the first <see cref="MaxListed"/> of them.
    /// </summary>
    internal static string PutAllBackQuestion(IReadOnlyList<ActiveChange> changes)
    {
        var text = new StringBuilder("Put all of these back?\n\nPoolWatch turns off the features it turned on and puts back what it turned off.\n");
        foreach (var c in changes.Take(MaxListed))
        {
            text.Append("\n- ").Append(c.What);
            string? caveat = !c.CanUndo ? ChangeWords.CannotPutBackLine(c.Note) : c.Warning ?? c.Note;
            if (caveat is not null) text.Append("\n   ").Append(caveat);
        }
        if (changes.Count > MaxListed) text.Append($"\n\nAnd {changes.Count - MaxListed} more, listed in the window.");
        return text.ToString();
    }

    /// <summary>What Put all back runs: the changes to put back, and the ones you said No to with their result.</summary>
    internal sealed record Plan(Func<ActiveChange, bool> Which, IReadOnlyList<(ActiveChange Change, UndoResult Result)> Declined);

    /// <summary>
    /// Put all back after your yes: each warning is asked on its own (<paramref name="putBackAnyway"/>), as a single Put
    /// back asks it, and a change you say No to stays as it is, reported as not done (Declined).
    /// Which: the other changes, only as the window showed them (<see cref="AsShown"/>); every warning among them was
    /// accepted, so the run confirms.
    /// </summary>
    internal static Plan PutAllBackPlan(IReadOnlyList<ActiveChange> shown, Func<string, bool> putBackAnyway)
    {
        var declined = shown.Where(c => c.CanUndo && c.Warning is { } warning && !putBackAnyway(warning)).ToList();
        return new(AsShown(shown.Except(declined)), declined.Select(c => (c, new UndoResult(false, "You chose not to put it back."))).ToList());
    }

    /// <summary>
    /// Put all back's run on the ledger (a worker thread): every change the plan takes, in the ledger's order
    /// (<see cref="ChangeLedger.UndoOrder"/>), never stopping at one that fails, then what to show: each result, the ones
    /// not done first, and whether all were done.
    /// </summary>
    internal static (string Text, bool Done) PutAllBack(ChangeLedger ledger, Plan plan)
    {
        var results = ledger.UndoAll(plan.Which, confirmed: true).Concat(plan.Declined).ToList();
        return (PutAllBackResults(results), results.All(r => r.Result.Done));
    }

    /// <summary>
    /// One row's button (a worker thread), after its warning was accepted: only as the window showed it, so a change that
    /// changed since is left for the next look. Turn off, Remove and Put back all run this.
    /// </summary>
    internal static (string Text, bool Done) PutBackOne(ChangeLedger ledger, ActiveChange c) =>
        ledger.UndoAll(AsShown([c]), confirmed: c.Warning is not null) is [var only] ? (only.Result.Message, only.Result.Done) : (ChangedSinceShown, false);

    internal const string ChangedSinceShown = "This change is no longer as the window showed it, so PoolWatch left it as it is. Look at the list again.";

    /// <summary>
    /// The put-back filter: only the changes the window showed, each as it was shown (its warning, and whether it could be
    /// put back), so one made or changed since (with a warning you never saw, or one Put all back's question listed as
    /// not possible) waits for the next look. Every warning it lets through was accepted, so the put-back confirms.
    /// </summary>
    internal static Func<ActiveChange, bool> AsShown(IEnumerable<ActiveChange> shown)
    {
        var asShown = new Dictionary<string, (string? Warning, bool CanUndo)>();
        foreach (var c in shown) asShown.TryAdd(c.Id, (c.Warning, c.CanUndo));
        return c => asShown.TryGetValue(c.Id, out var was) && was == (c.Warning, c.CanUndo);
    }

    /// <summary>
    /// Each change Put all back went through, with what happened to it: the ones not done first, so they are the last to
    /// be cut off, at most <see cref="MaxListed"/> in all, then a count of the rest.
    /// </summary>
    internal static string PutAllBackResults(IReadOnlyList<(ActiveChange Change, UndoResult Result)> results)
    {
        if (results.Count == 0) return "There was nothing to put back.";
        var ordered = results.OrderBy(r => r.Result.Done).ToList(); // stable: otherwise in the order they ran
        string text = string.Join("\n\n", ordered.Take(MaxListed).Select(r => $"{(r.Result.Done ? "Done" : "Not done")}: {r.Change.What}\n{r.Result.Message}"));
        var rest = ordered.Skip(MaxListed).ToList();
        if (rest.Count == 0) return text;
        int notDone = rest.Count(r => !r.Result.Done);
        return text + (notDone == 0
            ? $"\n\nAnd {rest.Count} more, all done."
            : $"\n\nAnd {rest.Count} more: {notDone} not done, still listed in the window.");
    }
}
