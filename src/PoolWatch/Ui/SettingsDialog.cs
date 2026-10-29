using PoolWatch.Analysis;
using PoolWatch.Core;
using PoolWatch.Engine;

namespace PoolWatch.Ui;

/// <summary>
/// Settings, opened from the main window's Settings button: a short form in plain words with what most people change, in
/// three columns (Start at logon, update notices, and the Codex shortcut when it can be made again; automatic cache
/// clearing with its three rules by name; deeper capture and the token-leak guard) and an Advanced button for every
/// setting (the old grid). Start at logon is a scheduled task and changes at once. Save writes the rest to settings.json and puts
/// automatic clearing, the token-leak guard and update notices into effect at once; the other settings apply at
/// PoolWatch's next start. A change made elsewhere while the form is open shows here at once. The form sizes itself to its
/// content, so nothing is cut off at any scaling, and fits a 1024 x 600 screen; on a shorter one it scrolls. Created for
/// each opening and disposed when it closes, so it always starts from what is saved, with the settings in effect now.
/// </summary>
internal sealed class SettingsDialog : Form
{
    /// <summary>Each column's width (logical pixels).</summary>
    public const int ColumnWidth = 300;

    private readonly MonitorEngine? _engine;
    private readonly LiveSettings? _live;
    private readonly SettingsEditor? _editor;
    private readonly Action<Action<Settings>>? _onApplied;
    private readonly CheckBox _logon = new WrapCheckBox { Text = "Start PoolWatch when I sign in", AutoSize = true };
    private readonly SimpleSettingsFields _fields = new();
    private readonly Label _clearHint = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly Label _deeperHint = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly ToolTip _tips = new() { AutoPopDelay = 30_000, InitialDelay = 400 };
    private readonly Label _problems = new() { AutoSize = true, ForeColor = Color.Firebrick, Visible = false };
    private readonly List<Control> _clearFields = [];
    private readonly Control _every, _over;
    private readonly Font _bold;
    private readonly ShortcutOffer? _shortcut;
    private readonly Label _shortcutAnswer = new() { AutoSize = true, Visible = false };
    private readonly Button _shortcutButton = new() { Text = ShortcutOffer.Button, AutoSize = true };
    private AdvancedSettingsDialog? _advanced;

    /// <param name="shortcut">Offers to make the Codex shortcut again (ChatGPT desktop installed, the shortcut not on the desktop); null leaves it out.</param>
    public SettingsDialog(MonitorEngine engine, AnalysisPipeline analysis, UiActions actions, LiveSettings live, IShell shell, ShortcutOffer? shortcut = null)
        : this(new SettingsEditor(engine, analysis, actions, live, shell), shortcut)
    {
        _engine = engine;
        _live = live;
        LoadFrom();
        var editor = _editor!;
        _advancedButton.Click += (_, _) => actions.Guarded(this, "Advanced settings", OpenAdvanced);
        _saveButton.Click += (_, _) => actions.Guarded(this, "Save settings", Save);
        _shortcutButton.Click += (_, _) => actions.Guarded(this, "Create shortcut", CreateShortcut);

        // schtasks /query can take seconds: ask in the background, then wire the checkbox on this (the UI) thread. The
        // answer is posted to the context captured here, so it arrives whether or not the dialog has a window handle yet.
        _logon.Enabled = false;
        var ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        Task.Run(SafeExists).ContinueWith(t => ui.Post(_ => { if (!IsDisposed) WireLogon(t.Result, actions); }, null), TaskScheduler.Default);
        // A suggestion, the setup guide or Undo may change a setting while the form is open (Undo on a worker thread):
        // the form follows on this thread, so Save never puts an older value back.
        _onApplied = change => ui.Post(_ =>
        {
            if (IsDisposed) return;
            editor.Follow(change);
            LoadFrom();
        }, null);
        live.Applied += _onApplied;
    }

    /// <summary>
    /// The form as it lays out, showing <paramref name="shown"/>, with nothing behind it: no logon task is read and no
    /// button acts (tests, which check that it fits).
    /// </summary>
    internal SettingsDialog(Settings shown, ShortcutOffer? shortcut) : this(editor: null, shortcut)
    {
        _fields.Fill(SimpleSettings.From(shown));
        _clearHint.Text = SimpleSettings.AutoClearHint(shown);
        _deeperHint.Text = SimpleSettings.DeeperCaptureHint(shown);
        EnableClearFields();
    }

    private readonly Button _advancedButton = new() { Text = "Advanced...", AutoSize = true };
    private readonly Button _saveButton = new() { Text = "Save", AutoSize = true };

    /// <summary>The layout, shared by both constructors.</summary>
    private SettingsDialog(SettingsEditor? editor, ShortcutOffer? shortcut)
    {
        _editor = editor;
        _shortcut = shortcut;
        Text = "PoolWatch settings";
        Icon = TrayIcons.App;
        Font = new Font("Segoe UI", 9f);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        int width = S(ColumnWidth), indent = S(22), gutter = S(24);
        _bold = new Font(Font, FontStyle.Bold);
        TableLayoutPanel Column() => new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
        void Row(TableLayoutPanel column, Control c, int indentPx = 0, int top = 0, int bottom = 4)
        {
            c.Margin = new Padding(indentPx, S(top), 0, S(bottom));
            // Texts wrap within the column (a check box's long label too).
            if (c is Label { AutoSize: true } l) l.MaximumSize = new Size(width - indentPx, 0);
            if (c is WrapCheckBox box) box.WrapWidth = width - indentPx;
            column.Controls.Add(c);
        }
        Label Heading(string text) => new() { Text = text, AutoSize = true, Font = _bold };
        Label Hint(string text) => new() { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText };
        FlowLayoutPanel Line(params Control[] parts)
        {
            var p = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty };
            foreach (var c in parts)
            {
                c.Margin = c is Label ? new Padding(0, S(5), S(6), 0) : c is RadioButton ? new Padding(0, S(2), S(14), 0) : new Padding(0, S(2), S(6), 0);
                p.Controls.Add(c);
            }
            return p;
        }

        // The first column: when PoolWatch starts, update notices, and the Codex shortcut when it can be made again.
        var first = Column();
        first.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width));
        Row(first, Heading("Start at logon"));
        Row(first, _logon, indent);
        Row(first, Hint("A scheduled task with the highest privileges, so there is no UAC prompt at each sign-in. It changes as soon as you " +
                        "tick or untick it, and is created only for a copy installed in Program Files."), indent, bottom: 10);
        Row(first, Heading("Update notices"));
        Row(first, _fields.Updates, indent);
        Row(first, Hint(SimpleSettings.UpdatesHint), indent, bottom: 10);
        if (shortcut is not null)
        {
            Row(first, Heading(ShortcutOffer.Heading));
            Row(first, Hint(ShortcutOffer.Text), indent);
            _shortcutButton.Margin = Padding.Empty;
            Row(first, _shortcutButton, indent, top: 2);
            Row(first, _shortcutAnswer, indent, bottom: 10);
        }

        // The second column: when PoolWatch clears the cache, its three rules by name, one under another.
        var second = Column();
        second.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width));
        Row(second, Heading("Automatic cache clearing"));
        Row(second, _fields.AutoClear, indent);
        _fields.Hours.Width = S(64);
        _fields.PoolGb.Width = S(64);
        var rules = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        foreach (var rule in new[] { _fields.Schedule, _fields.PoolSize, _fields.Either })
        {
            rule.Margin = new Padding(0, 0, 0, S(2));
            rules.Controls.Add(rule);
        }
        _every = Line(new Label { Text = "Every", AutoSize = true }, _fields.Hours, new Label { Text = "hours", AutoSize = true });
        _over = Line(new Label { Text = "When the kernel pool reaches", AutoSize = true }, _fields.PoolGb, new Label { Text = "GB", AutoSize = true });
        Row(second, rules, indent * 2);
        Row(second, _every, indent);
        Row(second, _over, indent);
        Row(second, _clearHint, indent, bottom: 10);
        _clearFields.AddRange([rules, _every, _over]);
        _tips.SetToolTip(_fields.Schedule, "Clears every so many hours, whatever the size of the kernel pool.");
        _tips.SetToolTip(_fields.PoolSize, "Clears when the kernel pool reaches the size you set, however long that takes.");
        _tips.SetToolTip(_fields.Either, "Clears on the schedule, or sooner when the kernel pool reaches the size you set. Recommended.");

        // The third column: what PoolWatch records, and the token-leak guard.
        var third = Column();
        third.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width));
        Row(third, Heading("Deeper capture"));
        Row(third, _fields.Deeper, indent);
        Row(third, _deeperHint, indent, bottom: 10);
        Row(third, Heading("Token-leak guard"));
        Row(third, _fields.TokenGuard, indent);
        Row(third, Hint(SimpleSettings.TokenGuardHint), indent, bottom: 10);
        _tips.SetToolTip(_fields.TokenGuard, SimpleSettings.TokenGuardTip);

        var columns = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        for (int i = 0; i < 3; i++) columns.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        first.Margin = second.Margin = new Padding(0, 0, gutter, 0);
        columns.Controls.Add(first, 0, 0);
        columns.Controls.Add(second, 1, 0);
        columns.Controls.Add(third, 2, 0);

        // Under them all: what is wrong, when changes apply, and the buttons.
        int full = 3 * width + 2 * gutter;
        var form = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Padding = Px(16, 12, 16, 12), Location = Point.Empty };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, full));
        form.Controls.Add(columns);
        _problems.MaximumSize = new Size(full, 0);
        _problems.Margin = new Padding(0, 0, 0, S(6));
        form.Controls.Add(_problems);
        var when = Hint(SimpleSettings.WhenChangesApply);
        when.MaximumSize = new Size(full, 0);
        when.Margin = new Padding(0, 0, 0, S(8));
        form.Controls.Add(when);

        _advancedButton.Margin = Padding.Empty;
        _saveButton.Margin = new Padding(S(6), 0, 0, 0);
        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel, Margin = new Padding(S(6), 0, 0, 0) };
        CancelButton = close;
        var buttons = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty, Dock = DockStyle.Fill };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttons.Controls.Add(_advancedButton, 0, 0);
        buttons.Controls.Add(_saveButton, 2, 0);
        buttons.Controls.Add(close, 3, 0);
        form.Controls.Add(buttons);
        Controls.Add(form);

        _fields.AutoClear.CheckedChanged += (_, _) => EnableClearFields();
        foreach (var rule in new[] { _fields.Schedule, _fields.PoolSize, _fields.Either }) rule.CheckedChanged += (_, _) => EnableClearFields();
    }

    private int S(int logical) => LogicalToDeviceUnits(logical);
    private Padding Px(int l, int t, int r, int b) => new(S(l), S(t), S(r), S(b));

    /// <summary>The Advanced grid (created on first use; CI renders it for the screenshots).</summary>
    public AdvancedSettingsDialog Advanced => _advanced ??= new AdvancedSettingsDialog(_editor ?? throw new InvalidOperationException("Settings has no editor"));

    /// <summary>The form's fields (tests).</summary>
    internal SimpleSettingsFields Fields => _fields;

    /// <summary>The form's fields from the edited settings (also after Advanced, which edits the same settings).</summary>
    private void LoadFrom()
    {
        if (_editor is null) return;
        _fields.Fill(SimpleSettings.From(_editor.Edit));
        _clearHint.Text = SimpleSettings.AutoClearHint(_editor.Edit);
        _deeperHint.Text = SimpleSettings.DeeperCaptureHint(_editor.Edit);
        EnableClearFields();
        ShowProblems([]);
    }

    /// <summary>The rule's choices and fields follow the box; each number field only while its rule uses it.</summary>
    private void EnableClearFields()
    {
        bool on = _fields.AutoClear.Checked;
        foreach (var c in _clearFields) c.Enabled = on;
        var rule = _fields.Rule;
        _every.Enabled = on && rule != ClearRule.PoolSize;
        _over.Enabled = on && rule != ClearRule.Schedule;
    }

    private void ShowProblems(List<string> problems)
    {
        _problems.Text = string.Join("\n", problems);
        _problems.Visible = problems.Count > 0;
    }

    private void Save()
    {
        if (_editor is null) return;
        if (!_fields.Values().ApplyTo(_editor.Edit, out var problems)) { ShowProblems(problems); return; }
        ShowProblems([]);
        _editor.Save(this);
        LoadFrom(); // Save may have kept the automatic clear off (your own clear task stays)
    }

    private void OpenAdvanced()
    {
        if (_editor is null) return;
        // The grid shows what this form holds now (it must be valid first), and this form shows what the grid saved.
        if (!_fields.Values().ApplyTo(_editor.Edit, out var problems)) { ShowProblems(problems); return; }
        if (!Advanced.Visible) Advanced.ShowDialog(this);
        LoadFrom();
    }

    /// <summary>Makes the Codex shortcut (off the UI thread) and says how it went under its button; the button stays disabled once it is made.</summary>
    private async void CreateShortcut()
    {
        if (_shortcut is null) return;
        _shortcutButton.Enabled = false;
        var (ok, message) = await _shortcut.Create();
        if (IsDisposed) return;
        _shortcutAnswer.Text = message;
        _shortcutAnswer.ForeColor = ok ? UiColors.Good : Color.Firebrick;
        _shortcutAnswer.Visible = message.Length > 0;
        _shortcutButton.Enabled = !ok;
    }

    private void WireLogon(bool exists, UiActions actions)
    {
        _logon.Checked = exists;
        _logon.Enabled = true;
        _logon.CheckedChanged += (_, _) => actions.Guarded(this, "Start at logon", () =>
        {
            var (ok, output) = LogonTask.Set(_logon.Checked, Path.Combine(_engine!.Paths.DataRoot, "logon-task.xml"), _engine.Paths.Guard.CheckReal);
            if (!ok) MessageBox.Show(this, "Could not change the logon task:\n" + output, "PoolWatch");
        });
    }

    private static bool SafeExists()
    {
        try { return LogonTask.Exists(); } catch { return false; }
    }

    /// <summary>
    /// On a screen too short for the whole form (high scaling on a small display) it keeps its width and scrolls
    /// vertically instead of running off the screen (<see cref="ScreenFit"/>).
    /// </summary>
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (ScreenFit.ToWorkingArea(this)) CenterToParent();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_live is not null && _onApplied is not null) _live.Applied -= _onApplied;
            _advanced?.Dispose();
            _tips.Dispose();
        }
        base.Dispose(disposing);
        if (disposing) _bold.Dispose();
    }
}

/// <summary>
/// Settings' offer to make the Codex shortcut again: shown when the ChatGPT desktop app (Codex) is installed, the launcher
/// the installer puts next to PoolWatch is there, and the shortcut is not on the desktop (deleted, or never made). The
/// shortcut only starts the app without its shader cache: PoolWatch never changes the app itself.
/// </summary>
/// <param name="Create">Makes the shortcut (off the UI thread) and says how it went.</param>
internal sealed record ShortcutOffer(Func<Task<(bool Ok, string Message)>> Create)
{
    public const string Heading = "Codex shortcut";
    public const string Text = "The shortcut that starts Codex without its shader cache is not on your desktop. PoolWatch doesn't change the Codex app; " +
                               "the shortcut just starts it without that cache.";
    public const string Button = "Create shortcut";

    /// <summary>Whether Settings offers it: the app installed, the launcher there, and no shortcut of that name on the desktop.</summary>
    public static bool Offered(bool chatGptInstalled, bool launcherInstalled, bool onDesktop) => chatGptInstalled && launcherInstalled && !onDesktop;

    /// <summary>
    /// Whether the shortcut is on the desktop (<paramref name="desktop"/> the desktop folder, <paramref name="exists"/> a
    /// file's check). A desktop that cannot be found or read counts as having it: nothing is offered on a guess.
    /// </summary>
    public static bool OnDesktop(Func<string> desktop, Func<string, bool> exists)
    {
        try { return desktop() is not { Length: > 0 } dir || exists(Path.Combine(dir, Engine.ChatGptShortcut.FileName)); }
        catch (Exception) { return true; }
    }
}

/// <summary>
/// The plain form's value fields and how they show and read <see cref="SimpleSettings"/>. The dialog lays them out;
/// they are kept apart from it so a test can bind them without an engine or a window.
/// </summary>
internal sealed class SimpleSettingsFields
{
    public CheckBox AutoClear { get; } = new WrapCheckBox { Text = "Clear the standby cache automatically", AutoSize = true };
    /// <summary>The automatic clear's three rules, by name (one is always picked).</summary>
    public RadioButton Schedule { get; } = new() { Text = SimpleSettings.ScheduleChoice, AutoSize = true };
    public RadioButton PoolSize { get; } = new() { Text = SimpleSettings.SizeChoice, AutoSize = true };
    public RadioButton Either { get; } = new() { Text = SimpleSettings.EitherChoice, AutoSize = true, Checked = true };
    public NumericUpDown Hours { get; } = new() { DecimalPlaces = 1, Increment = 0.5m, Minimum = 0, Maximum = SimpleSettings.MaxHours, TextAlign = HorizontalAlignment.Right };
    public NumericUpDown PoolGb { get; } = new() { DecimalPlaces = 1, Increment = 1, Minimum = 0, Maximum = SimpleSettings.MaxPoolGb, TextAlign = HorizontalAlignment.Right };
    public CheckBox Deeper { get; } = new WrapCheckBox { Text = "Deeper automatic captures", AutoSize = true };
    public CheckBox TokenGuard { get; } = new WrapCheckBox { Text = SimpleSettings.TokenGuardLabel, AutoSize = true };
    public CheckBox Updates { get; } = new WrapCheckBox { Text = SimpleSettings.UpdatesLabel, AutoSize = true };

    public SimpleSettingsFields()
    {
        // One rule at a time, whatever holds the choices (radio buttons only clear each other inside one container).
        foreach (var choice in new[] { Schedule, PoolSize, Either })
            choice.CheckedChanged += (_, _) =>
            {
                if (!choice.Checked) return;
                foreach (var other in new[] { Schedule, PoolSize, Either }) if (other != choice) other.Checked = false;
            };
    }

    /// <summary>The rule picked now.</summary>
    public ClearRule Rule => Schedule.Checked ? ClearRule.Schedule : PoolSize.Checked ? ClearRule.PoolSize : ClearRule.Either;

    /// <summary>The schedule and the size the fields show when the rule in effect does not use one (the defaults).</summary>
    private const decimal ShownHours = 1, ShownPoolGb = 8;

    /// <summary>Sets the fields to <paramref name="v"/>: the rule its two values make, and each number (a rule's unused one shows the default).</summary>
    public void Fill(SimpleSettings v)
    {
        AutoClear.Checked = v.AutoClear;
        var rule = SimpleSettings.RuleOf(v.EveryHours, v.PoolGb);
        Schedule.Checked = rule == ClearRule.Schedule;
        PoolSize.Checked = rule == ClearRule.PoolSize;
        Either.Checked = rule == ClearRule.Either;
        Hours.Value = v.EveryHours > 0 ? v.EveryHours : rule == ClearRule.PoolSize ? ShownHours : 0;
        PoolGb.Value = v.PoolGb > 0 ? v.PoolGb : rule == ClearRule.Schedule ? ShownPoolGb : 0;
        Deeper.Checked = v.DeeperCapture;
        TokenGuard.Checked = v.TokenGuard;
        Updates.Checked = v.CheckForUpdates;
    }

    /// <summary>What the form holds: the rule keeps the numbers it uses, and turns the other rule off (0).</summary>
    public SimpleSettings Values()
    {
        var (hours, gb) = SimpleSettings.ForRule(Rule, Hours.Value, PoolGb.Value);
        return new(AutoClear.Checked, hours, gb, Deeper.Checked, TokenGuard.Checked, Updates.Checked);
    }
}

/// <summary>
/// A check box that wraps its label at <see cref="WrapWidth"/>, growing taller (a check box that sizes itself keeps its
/// label on one line, and a narrow column cuts it off). It draws the wrapped label itself; only its size is worked out here.
/// </summary>
internal sealed class WrapCheckBox : CheckBox
{
    private int _wrapWidth;

    public WrapCheckBox()
    {
        // The box beside the label's first line, as one-line check boxes have it.
        CheckAlign = ContentAlignment.TopLeft;
        TextAlign = ContentAlignment.TopLeft;
    }

    /// <summary>The widest it gets, in pixels (0: one line, as a plain check box).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int WrapWidth
    {
        get => _wrapWidth;
        set { if (_wrapWidth == value) return; _wrapWidth = value; PerformLayout(); Parent?.PerformLayout(); }
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var one = base.GetPreferredSize(Size.Empty); // its label on one line (a layout's narrower proposal only clips it)
        if (_wrapWidth <= 0 || one.Width <= _wrapWidth || Text.Length == 0) return base.GetPreferredSize(proposedSize);
        int width = proposedSize.Width is > 1 and < int.MaxValue ? Math.Min(_wrapWidth, proposedSize.Width) : _wrapWidth;
        const TextFormatFlags flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;
        var line = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        int glyph = Math.Max(0, one.Width - line.Width); // the box, its gap and the padding beside the label
        var wrapped = TextRenderer.MeasureText(Text, Font, new Size(Math.Max(1, width - glyph), int.MaxValue), flags);
        return new Size(width, one.Height + Math.Max(0, wrapped.Height - line.Height));
    }
}
