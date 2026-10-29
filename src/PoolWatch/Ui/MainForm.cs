using PoolWatch.Analysis;
using PoolWatch.Core;
using PoolWatch.Core.Correlation;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Findings;
using PoolWatch.Core.Util;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Knowledge;
using PoolWatch.Core.Model;
using PoolWatch.Core.Recommendations;
using PoolWatch.Engine;
using PoolWatch.Engine.Changes;

namespace PoolWatch.Ui;

/// <summary>
/// The main window (DESIGN.md section 11), in one of two views. The Simple view, the default: the status line, the
/// Clear cache now button, the line about the next cache clear, the pool graph and up to three suggestions. The Advanced
/// view: the status line, three buttons (Clear cache now, Export report, Settings), the graph and two tabs (Pool causes,
/// Services). Both have Active changes, the update notice when a newer release is out, and the Advanced view switch at
/// the bottom; everything else is in the tray menu. The one graph moves between the two layouts. Refreshes on a 10 s
/// timer that runs only while the window is visible and not minimised, the Simple view's line and suggestions at most
/// once a minute; nothing is rendered or recomputed while it is hidden, and hiding it drops what only it shows.
/// </summary>
internal sealed class MainForm : Form
{
    private readonly MonitorEngine _engine;
    private readonly AnalysisPipeline _analysis;
    private readonly UiActions _actions;
    private readonly SetupContext _context;
    private readonly ViewState? _view;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 10_000 };
    private readonly ToolTip _tips = new() { AutoPopDelay = 30_000, InitialDelay = 400 };

    // The header: the status line on the left, the buttons on the right (only Clear cache now in the Simple view). The line
    // draws itself (a Label flashed blank each time a refresh changed its text).
    private readonly Panel _header = new() { Dock = DockStyle.Top };
    private readonly PaintedLabel _status = new(centerVertically: true) { Dock = DockStyle.Fill };
    private readonly FlowLayoutPanel _buttons = new() { Dock = DockStyle.Right, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false };
    private readonly List<Button> _headerButtons = [];
    private readonly List<Button> _advancedButtons = [];
    private PoolStatusKind _statusKind = PoolStatusKind.Collecting;

    // The two views and the footer they share.
    private readonly SimpleView _simple = new() { Dock = DockStyle.Fill };
    private readonly TableLayoutPanel _advanced = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = SystemColors.Window };
    private readonly TableLayoutPanel _footer = new() { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, RowCount = 1 };
    private readonly Button _appliedChanges = new() { Text = "Active changes", AutoSize = true, UseVisualStyleBackColor = true };
    private readonly CheckBox _advancedBox = new() { Text = "Advanced view", AutoSize = true };
    /// <summary>"PoolWatch 1.2.0 is available. See what's new", while a newer release is out and update notices are on.</summary>
    private readonly LinkLabel _updateNotice = new() { AutoSize = true, UseMnemonic = false, Visible = false };
    private readonly Func<UpdateRelease?> _update;
    private UpdateRelease? _shownUpdate;
    private bool _isAdvanced;
    /// <summary>When the Simple view's line and suggestions were last read (they are read at most once a minute).</summary>
    private DateTime _simpleReadUtc = DateTime.MinValue;
    private static readonly TimeSpan SimpleEvery = TimeSpan.FromMinutes(1);

    private readonly PoolChart _chart = new() { Dock = DockStyle.Fill };
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };

    // Pool causes: the merged list and the selected row's details. The list is drawn off screen and kept in step in place
    // (ListSync): its cells change while it shows.
    private readonly BufferedListView _causes = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false };
    private readonly SplitContainer _causesSplit = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
    private readonly WrapLabel _detailTitle = new() { Dock = DockStyle.Top };
    private readonly TextBox _detailText = new() { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, BackColor = SystemColors.Window, BorderStyle = BorderStyle.None };
    private readonly Button _openFolder = new() { Text = "Open report folder", AutoSize = true, Visible = false };
    private readonly TagRankings _rankings = new();
    private List<CauseRow> _causeRows = [];
    private Dictionary<string, CauseRow> _causeByKey = new();
    private bool _causeSelectPending;
    private long _causesSamples = -1;

    // Services (the former Background services page; same behaviour). Its list is drawn off screen: its Cost cells change
    // on every refresh.
    private readonly BufferedListView _services = new() { View = View.Details, FullRowSelect = true, CheckBoxes = true, Dock = DockStyle.Fill, MultiSelect = true, HideSelection = false };
    private readonly WrapLabel _servicesStatus = new() { Dock = DockStyle.Top };
    private readonly ListView _tasks = new() { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, MultiSelect = false, HideSelection = false };
    private readonly ComboBox _preset = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly SplitContainer _servicesSplit = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
    private readonly Engine.TasksAndStartup _tasksEngine;

    private bool _repopulating;

    /// <param name="view">The view to open in, and where the owner's switch is kept; null (the CI UI test): the Simple view, and a switch is not kept.</param>
    /// <param name="update">The newer release to tell the owner about now, or null (read at each refresh: it can change without notice).</param>
    public MainForm(SetupContext context, ViewState? view, Func<UpdateRelease?> update)
    {
        _context = context;
        _engine = context.Engine;
        _analysis = context.Analysis;
        _actions = context.Actions;
        _view = view;
        _update = update;
        _tasksEngine = new Engine.TasksAndStartup(_engine.Log);
        Text = "PoolWatch";
        Icon = TrayIcons.For(TrayState.Green);
        Font = new Font("Segoe UI", 9f);
        BackColor = SystemColors.Window;
        StartPosition = FormStartPosition.CenterScreen;
        // About 1100 x 750 at 100%, scaled with the display (and never larger than the screen's working area).
        var want = LogicalToDeviceUnits(new Size(1100, 750));
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(Point.Empty, want);
        Size = new Size(Math.Min(want.Width, area.Width), Math.Min(want.Height, area.Height));
        MinimumSize = LogicalToDeviceUnits(new Size(760, 540));

        // Header.
        _status.Font = new Font("Segoe UI Semibold", 13f);
        _status.Text = "Kernel pool: collecting the first sample…";
        var clear = HeaderButton("Clear cache now", () => _actions.Guarded(this, "Clear cache", () => _actions.ClearCacheNow(this)));
        clear.MouseEnter += (_, _) => _tips.SetToolTip(clear, _actions.ClearCacheTip());
        var export = HeaderButton("Export report", () => _actions.Guarded(this, "Export report", () => _actions.ExportReport(this, SelectedRow()?.Folder)), advancedOnly: true);
        _tips.SetToolTip(export, "Zip the selected row's report (or the newest incident's) to share it. Redacted by default.");
        var settings = HeaderButton("Settings", () => _actions.Guarded(this, "Settings", OpenSettings), advancedOnly: true);
        _tips.SetToolTip(settings, "Detection thresholds, the automatic cache clear, redaction, and Start at logon.");
        _header.Controls.Add(_status);  // fill first, then the docked edge
        _header.Controls.Add(_buttons);

        // The Advanced view's body: the graph above the two tabs. The Simple view lays itself out (SimpleView).
        _chart.HelpText = "Committed paged and non-paged pool: the figures Task Manager shows (Performance, Memory).";
        _tabs.TabPages.Add(CausesPage());
        _tabs.TabPages.Add(ServicesPage());
        _advanced.Padding = Px(12, 0, 12, 4);
        _advanced.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _advanced.RowStyles.Add(new RowStyle(SizeType.Percent, 36));
        _advanced.RowStyles.Add(new RowStyle(SizeType.Percent, 64));
        _tabs.Margin = Padding.Empty;
        _advanced.Controls.Add(_tabs, 0, 1);

        // The footer, the same in both views.
        _footer.Padding = Px(12, 6, 12, 10);
        _footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _appliedChanges.Padding = Px(6, 2, 6, 2);
        _appliedChanges.Margin = Padding.Empty;
        _appliedChanges.Click += (_, _) => _actions.Guarded(this, "Active changes", _context.Shell.ShowActiveChanges);
        _tips.SetToolTip(_appliedChanges, "Every change PoolWatch made on this PC, each with Undo.");
        _advancedBox.Margin = new Padding(0, S(4), 0, 0);
        _advancedBox.CheckedChanged += (_, _) =>
        {
            if (_advancedBox.Checked == _isAdvanced) return; // set by the window itself
            _view?.Remember(_advancedBox.Checked);
            SafeRefresh(() => SetView(_advancedBox.Checked));
        };
        _tips.SetToolTip(_advancedBox, "Pool causes, background services, exports and every setting.");
        _updateNotice.Margin = new Padding(S(16), S(5), S(12), 0);
        _updateNotice.LinkClicked += (_, _) => _actions.Guarded(this, "Open the release page", () => { if (_shownUpdate is { } r) UiActions.OpenWebPage(r.PageUrl); });
        _tips.SetToolTip(_updateNotice, "Opens the release's page on GitHub in your browser. PoolWatch never downloads or installs anything itself.");
        _footer.Controls.Add(_appliedChanges, 0, 0);
        _footer.Controls.Add(_updateNotice, 1, 0);
        _footer.Controls.Add(_advancedBox, 2, 0);

        Controls.Add(_simple); // fill first, then the docked edges
        Controls.Add(_advanced);
        Controls.Add(_footer);
        Controls.Add(_header);
        _simple.ActionClicked += RunSuggestion;
        _simple.HideClicked += r =>
        {
            _context.Store.Dismiss(r.Key, DateTime.UtcNow);
            ReadSimpleNow();
        };

        EnableCopy(_causes, CauseCopy);
        EnableTextCopy(_detailText);

        // Selection changes are debounced (a click deselects, then selects): the details are rebuilt once, after the burst.
        _causes.SelectedIndexChanged += (_, _) =>
        {
            if (_repopulating || _causeSelectPending) return;
            _causeSelectPending = true;
            BeginInvoke(() => { _causeSelectPending = false; SafeRefresh(ShowCauseDetails); SafeRefresh(PumpRankings); });
        };
        _causes.DoubleClick += (_, _) => { if (SelectedRow()?.Folder is { } d && Directory.Exists(d)) _actions.OpenIncidentFolder(d); };
        _causes.Resize += (_, _) => FitCauseColumns();
        _openFolder.Click += (_, _) => { if (SelectedRow()?.Folder is { } d) _actions.OpenIncidentFolder(d); };
        _tabs.SelectedIndexChanged += (_, _) => RefreshNow();
        _timer.Tick += (_, _) => RefreshNow();
        VisibleChanged += (_, _) =>
        {
            if (Disposing) return; // the app is exiting: nothing to read or release
            if (Visible) ReadFactsIfStale();
            else ReleaseWhileHidden();
            UpdateTimer();
        };
        Resize += (_, _) => { UpdateTimer(); LayoutHeader(); };
        Load += (_, _) => { LayoutHeader(); FitCauseColumns(); };
        SplitAt(_causesSplit, 0.58);
        SplitAt(_servicesSplit, 0.6);
        SetView(_view?.Advanced == true);
    }

    private int S(int logical) => LogicalToDeviceUnits(logical);
    private Padding Px(int l, int t, int r, int b) => new(S(l), S(t), S(r), S(b));

    private Button HeaderButton(string text, Action click, bool advancedOnly = false)
    {
        var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = Px(10, 4, 10, 4), Margin = Px(8, 0, 0, 0), UseVisualStyleBackColor = true };
        b.Click += (_, _) => click();
        _buttons.Controls.Add(b);
        _headerButtons.Add(b);
        if (advancedOnly) _advancedButtons.Add(b);
        return b;
    }

    /// <summary>The header buttons of the view shown now.</summary>
    private IEnumerable<Button> ShownButtons => _isAdvanced ? _headerButtons : _headerButtons.Except(_advancedButtons);

    /// <summary>
    /// Shows the Advanced view (the graph above the tabs, all three header buttons) or the Simple view (its line, the graph
    /// and the suggestions, Clear cache now only), moving the one graph between them. The view's contents are read at once.
    /// </summary>
    private void SetView(bool advanced)
    {
        SuspendLayout();
        _isAdvanced = advanced;
        if (advanced)
        {
            _chart.Margin = Px(0, 0, 0, 8);
            _advanced.Controls.Add(_chart, 0, 0);
        }
        else _simple.SetChart(_chart);
        _advanced.Visible = advanced;
        _simple.Visible = !advanced;
        foreach (var b in _advancedButtons) b.Visible = advanced;
        _advancedBox.Checked = advanced;
        ResumeLayout(true);
        LayoutHeader();
        _simpleReadUtc = DateTime.MinValue;
        RefreshNow(force: true);
    }

    /// <summary>Switches to the Advanced view on a tab (0 Pool causes, 1 Services), as the owner asked from a suggestion; kept like the switch.</summary>
    public void ShowAdvanced(int tab)
    {
        _view?.Remember(true);
        if (!_isAdvanced) SetView(true);
        _tabs.SelectedIndex = Math.Clamp(tab, 0, _tabs.TabCount - 1);
    }

    /// <summary>The header's height follows the status text (wrapped to the room left of the buttons) and the buttons.</summary>
    private void LayoutHeader()
    {
        if (_buttons.Controls.Count == 0) return;
        _header.Padding = Px(14, 10, 12, 8);
        var shown = ShownButtons.ToList();
        int btnH = shown.Max(b => b.GetPreferredSize(Size.Empty).Height + b.Margin.Vertical);
        int btnW = shown.Sum(b => b.GetPreferredSize(Size.Empty).Width + b.Margin.Horizontal) + S(12);
        int textW = Math.Max(S(200), ClientSize.Width - _header.Padding.Horizontal - btnW);
        // Measured a little narrower than the label: wrapping earlier can only add a line, never cut one off.
        int textH = PaintedLabel.Measure(_status.Text, _status.Font, textW - S(4));
        int inner = Math.Max(textH + S(2), btnH);
        _buttons.Padding = new Padding(S(12), Math.Max(0, (inner - btnH) / 2), 0, 0);
        int h = inner + _header.Padding.Vertical;
        if (_header.Height != h) _header.Height = h;
    }

    // The tray icon's red and amber (UiData.State: red only for a real risk), and green for a fall.
    private static readonly Color Risk = Color.FromArgb(178, 34, 34), Watched = Color.FromArgb(150, 90, 0), Fell = Color.FromArgb(0, 110, 50);
    private static readonly TimeSpan OwnClearSaidFor = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The status line, calm on purpose: a growth alone is plain text with when the next clear runs, its colour is the tray
    /// icon's, and PoolWatch's own clear of the last 10 minutes is said as its result, not as the pool shrinking.
    /// </summary>
    private void UpdateStatus(TrayState state)
    {
        var t = UiData.Totals(_analysis);
        var lead = PoolCauses.Lead(_analysis, _rankings, _engine.Services, t.Held, out _leadTag);
        string? ownClear = t.Clear is { } c && DateTime.UtcNow - c.TimeUtc <= OwnClearSaidFor ? c.Result : null;
        var s = PoolStatusLine.Describe(t.Total, t.Held, t.Change10Min, t.ChangeHour, lead, UiData.SaturatedBusiest(_analysis), ownClear, UiData.NextClearContext(_analysis));
        _statusKind = s.Kind;
        var color = state switch
        {
            TrayState.Red => Risk,
            TrayState.Amber => Watched,
            _ when s.Saturated => Watched,
            _ => s.Kind switch { PoolStatusKind.Dropping => Fell, PoolStatusKind.Collecting => SystemColors.GrayText, _ => SystemColors.ControlText },
        };
        if (_status.ForeColor != color) _status.ForeColor = color;
        if (_status.Text != s.Text) { _status.Text = s.Text; LayoutHeader(); }
    }

    /// <summary>Settings, created for this opening and disposed when it closes, so it always starts from what is saved, with the settings in effect now.</summary>
    private void OpenSettings()
    {
        using var settings = NewSettings();
        settings.ShowDialog(this);
    }

    /// <summary>Settings for one opening (the caller disposes it).</summary>
    private SettingsDialog NewSettings() => new(_engine, _analysis, _actions, _context.Live, _context.Shell);

    // ---------------------------------------------------------------- Simple view

    /// <summary>
    /// The Simple view's line about clearing and its suggestions, read at most once a minute while it shows (from memory
    /// and the cached facts: nothing is scanned), and at once after a suggestion's button or Hide.
    /// </summary>
    private void ReadSimple(bool force)
    {
        var now = DateTime.UtcNow;
        if (!force && now - _simpleReadUtc < SimpleEvery) return;
        _simpleReadUtc = now;
        _simple.ShowNextClear(UiData.NextClearLine(_analysis));
        _simple.ShowRecommendations(_context.Suggestions());
    }

    private void ReadSimpleNow() => RefreshNow(force: true);

    /// <summary>A suggestion's button: its change runs off the UI thread (RecommendationActions), then its answer shows on the card.</summary>
    private async void RunSuggestion(Recommendation r)
    {
        var (ok, message) = await _context.Recommendations.Run(r, this);
        if (IsDisposed) return;
        _simple.ShowOutcome(r, ok, message);
        ReadSimpleNow();
    }

    /// <summary>The window became visible: the PC's facts are read again when they are old or Windows was updated, in the background.</summary>
    private async void ReadFactsIfStale()
    {
        if (!_context.Probe.Stale(DateTime.UtcNow)) return;
        try { await _context.Probe.ScanAsync(); }
        catch (Exception ex) { _engine.Log.Info("the PC's facts were not read: " + ex.Message); return; }
        if (!IsDisposed && !_isAdvanced) ReadSimpleNow();
    }

    /// <summary>
    /// The window hid: what only it shows is dropped (the tag rankings, the Pool causes rows and their report texts) and
    /// PoolWatch's own memory is given back at the next sample. A released tag's row then has no summary to copy.
    /// </summary>
    private void ReleaseWhileHidden()
    {
        _rankings.Clear();
        _causeRows = [];
        _causeByKey = new();
        _rankTags = [];
        _knownIssues = null;
        _reportText.Clear();
        _detailText.Clear();
        _textItems.Clear();
        if (_syncs.TryGetValue(_causes, out var sync)) sync.Clear();
        _listSignature.Remove(_causes);
        _causesSamples = -1;
        _engine.ReleaseMemory();
    }

    /// <summary>What the Pool causes rankings hold now, for PoolWatch's own cost report (any thread).</summary>
    internal TagRankings Rankings => _rankings;

    // ---------------------------------------------------------------- Pool causes

    private TabPage CausesPage()
    {
        int[] widths = [300, 150, 100, 250, 130, 110];
        for (int i = 0; i < PoolCauses.Columns.Length; i++) _causes.Columns.Add(PoolCauses.Columns[i], S(widths[i]));
        _causesSplit.Panel1.Controls.Add(_causes); // fill first, then the legend above it
        _causesSplit.Panel1.Controls.Add(Legend());
        _detailTitle.Font = new Font("Segoe UI Semibold", 10f);
        _detailTitle.Padding = Px(2, 6, 2, 4);
        _detailText.Font = new Font("Segoe UI", 9.5f);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = Px(0, 4, 0, 0) };
        bar.Controls.Add(_openFolder);
        _causesSplit.Panel2.Padding = Px(4, 0, 4, 4);
        _causesSplit.Panel2.Controls.Add(_detailText); // fill first, then the docked edges
        _causesSplit.Panel2.Controls.Add(_detailTitle);
        _causesSplit.Panel2.Controls.Add(bar);
        _causesSplit.SplitterWidth = S(6);
        var page = new TabPage("Pool causes") { UseVisualStyleBackColor = true, Padding = Px(4, 4, 4, 4) };
        page.Controls.Add(_causesSplit);
        return page;
    }

    /// <summary>What the row colours mean (the tones PoolCauses gives each row).</summary>
    private FlowLayoutPanel Legend()
    {
        var legend = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Padding = Px(0, 0, 0, 4) };
        Label Chip(string text, Color back) => new() { Text = text, AutoSize = true, BackColor = back, Padding = Px(6, 2, 6, 2), Margin = Px(0, 0, 8, 2) };
        legend.Controls.AddRange([
            Chip("Pink: an open incident, or an alert", AlertBack),
            Chip("Yellow: growing or held now, or a warning", WarningBack),
            new Label { Text = "No colour: earlier, or for information", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = Px(0, 2, 0, 2) },
        ]);
        return legend;
    }

    /// <summary>The fixed columns keep their width; "What is growing" and "Likely cause" share the rest.</summary>
    private void FitCauseColumns()
    {
        if (_causes.Columns.Count < 6 || _causes.ClientSize.Width <= 0) return;
        int fixedW = _causes.Columns[1].Width + _causes.Columns[2].Width + _causes.Columns[4].Width + _causes.Columns[5].Width;
        int rest = _causes.ClientSize.Width - fixedW - S(4);
        int what = Math.Max(S(180), rest * 55 / 100), cause = Math.Max(S(150), rest - what);
        if (_causes.Columns[0].Width != what) _causes.Columns[0].Width = what;
        if (_causes.Columns[3].Width != cause) _causes.Columns[3].Width = cause;
    }

    /// <summary>Puts the splitter at <paramref name="fraction"/> of the container once it has its real size (then the panels keep the ratio).</summary>
    private static void SplitAt(SplitContainer s, double fraction)
    {
        bool done = false;
        void Apply()
        {
            int size = s.Orientation == Orientation.Horizontal ? s.Height : s.Width;
            if (done || size <= 120) return;
            try
            {
                s.SplitterDistance = Math.Clamp((int)(size * fraction), s.Panel1MinSize, size - s.Panel2MinSize - s.SplitterWidth);
                done = true;
            }
            catch (InvalidOperationException) { } // not laid out yet: tried again on the next size change
        }
        s.SizeChanged += (_, _) => Apply();
        Apply();
    }

    private CauseRow? SelectedRow() =>
        _causes.SelectedItems.Count > 0 && _causes.SelectedItems[0].Tag is string key ? _causeByKey.GetValueOrDefault(key) : null;

    private static readonly Color AlertBack = Color.MistyRose, WarningBack = Color.FromArgb(255, 248, 214);

    private void RefreshCauses(bool force)
    {
        if (!force && _engine.SampleCount == _causesSamples)
        {
            // The list only changes with a new sample; the selected tag's ranking has its own 30 s clock.
            if (SelectedRow() is { Kind: CauseKind.Tag }) ShowCauseDetails();
            return;
        }
        _causesSamples = _engine.SampleCount;
        _causeRows = PoolCauses.Build(_analysis, _engine, _rankings);
        _rankTags = PoolCauses.TagsToRank(_causeRows);
        _causeByKey = _causeRows.GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.First());
        var items = _causeRows.GroupBy(r => r.Key).Select(g => g.First()).Select(r =>
        {
            var it = new ListViewItem(r.Cells) { Tag = r.Key };
            if (r.Tone == RowTone.Alert) it.BackColor = AlertBack;
            else if (r.Tone == RowTone.Warning) it.BackColor = WarningBack;
            return it;
        }).ToList();
        Update(_causes, items);
        ShowCauseDetails();
    }

    private void ShowCauseDetails()
    {
        var row = SelectedRow();
        if (row is null)
        {
            _openFolder.Visible = false;
            if (_causeRows.Count == 0)
            {
                SetTitle("Nothing stands out right now");
                SetText(_detailText, "No pool tag is growing, no incident is open, and no process is piling up handles or pool. When something grows, " +
                    "it is listed here with how much, how fast, the likely cause and how sure PoolWatch is.", "empty");
            }
            else
            {
                SetTitle("Select a row to see its details");
                SetText(_detailText, "", "none");
            }
            return;
        }
        SetTitle(row.HowMuch.Length > 0 ? $"{row.What}: {row.HowMuch}" : row.What);
        string text = row.Kind switch
        {
            CauseKind.Incident => IncidentDetails(row),
            CauseKind.Tag => TagDetails(row),
            CauseKind.Process => ProcessDetails(row),
            _ => FindingDetails(row),
        };
        if (row.Kind is CauseKind.Incident or CauseKind.Tag && row.Tag is { } tag && KnownIssueNotes(tag) is { Length: > 0 } notes)
            text += "\r\n\r\n" + notes;
        SetText(_detailText, text, row.Key);
        bool folder = row.Folder is { } f && Directory.Exists(f);
        if (_openFolder.Visible != folder) _openFolder.Visible = folder;
    }

    private void SetTitle(string text) { if (_detailTitle.Text != text) _detailTitle.Text = text; }

    /// <summary>The known issues matched with the Simple view's cards (SetupContext), and when: read at most once a minute, only for a selected row.</summary>
    private (DateTime Utc, IReadOnlyList<Recommendation> Cards, IReadOnlyList<KnownIssueMatch> Matches)? _knownIssues;

    /// <summary>The notes of the known issues about <paramref name="tag"/> (what PoolWatch saw, what helps, who found it), or "".</summary>
    private string KnownIssueNotes(PoolTag tag)
    {
        var now = DateTime.UtcNow;
        if (_knownIssues is not { } known || now - known.Utc >= SimpleEvery)
        {
            var (cards, matches) = _context.SuggestionsAndKnownIssues();
            _knownIssues = known = (now, cards, matches);
        }
        return string.Join("\r\n\r\n", known.Matches.Where(m => m.Tags.Contains(tag)).Select(m => m.Note(known.Cards)));
    }

    private readonly Dictionary<string, (DateTime written, string text)> _reportText = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The incident's report summary (from report.json; re-read when the report is rewritten).</summary>
    private string IncidentDetails(CauseRow row)
    {
        if (row.Folder is not { } dir) return "";
        string json = Path.Combine(dir, "report.json");
        try
        {
            var info = new FileInfo(json);
            if (!info.Exists) return "The report is being written.";
            if (_reportText.TryGetValue(dir, out var c) && c.written == info.LastWriteTimeUtc) return c.text;
            string text = info.Length > 16 << 20 ? "The report is too large to show here: open the report folder."
                : UiActions.IncidentSummary(dir).TrimEnd() + "\r\n\r\nThe full report (report.md, with the timeline chart, every suspect's evidence and a draft bug report) is in the report folder.";
            _reportText[dir] = (info.LastWriteTimeUtc, text);
            return text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return "The report could not be read just now (" + ex.Message + ").";
        }
    }

    /// <summary>A growing tag: the plain answer, the top suspects with their evidence, and the recent notable events.</summary>
    private string TagDetails(CauseRow row)
    {
        if (row.Tag is not { } tag) return "";
        var sb = new System.Text.StringBuilder();
        // For the expert: the tag code, its driver and its technical description (the row shows the everyday name).
        var info = _analysis.Knowledge.Lookup(tag);
        string driver = PlainWords.Driver(info.Owner);
        sb.Append($"Pool tag {tag.ToDisplay()}{(driver.Length > 0 ? $", allocated by {driver}" : "")}{(info.Description.Length > 0 ? $": {info.Description}" : "")}.\r\n\r\n");
        if (row.Active is false) return sb.Append("Released: whatever exited or was deleted then was holding this memory.").ToString();
        // The ranking is built in the background (PumpRankings); this text is replaced when it arrives.
        if (_rankings.Get(tag) is not { } v)
            return sb.Append(_rankings.Tried(tag) && !_rankings.Building(tag) ? "Not enough samples yet to rank the suspects." : "Ranking the suspects…").ToString();
        sb.Append(v.Answer.Line).Append("\r\n");
        sb.Append(v.TraceBacked ? "The confirmation trace's owners come first; the rest are correlation only.\r\n" : "Suspects are ranked by correlation, not proof.\r\n");
        int n = 0;
        foreach (var o in v.Owners.Where(o => o.Meaningful).Take(3))
        {
            sb.Append($"\r\n{++n}. {o.Name}: {PlainWords.Confidence(o.Confidence)}{(o.CorrelationOnly ? " (correlation only)" : "")}\r\n");
            foreach (var p in o.Plain) sb.Append("   - ").Append(p).Append("\r\n");
            if (o.Members.Count > 0) sb.Append("   ").Append(ProcessesLine(o)).Append("\r\n");
            if (o.Members.FirstOrDefault(m => m.CommandLine is not null)?.CommandLine is { } cl) sb.Append("   Command line: ").Append(cl).Append("\r\n");
        }
        if (n == 0) sb.Append("\r\nNo suspect stood out.\r\n");
        var events = v.Events.Where(e => e.Notable).TakeLast(10).ToList();
        if (events.Count > 0)
        {
            sb.Append("\r\nRecent events:\r\n");
            foreach (var e in events) sb.Append("   ").Append(e.Text).Append("\r\n");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>An owner's first five processes, then how many more it has: all of them, not only the ones the row kept.</summary>
    internal static string ProcessesLine(OwnerSuspect o) =>
        "Processes: " + string.Join("; ", o.Members.Take(5).Select(m => PlainWords.WithPid(m.DisplayName, m.Key.Pid)))
        + (o.MemberCount > 5 ? $" and {o.MemberCount - 5} more" : "");

    private string ProcessDetails(CauseRow row)
    {
        if (row.Candidate is not { } p) return "";
        var rec = _engine.Registry.Get(p.Key);
        var sb = new System.Text.StringBuilder();
        sb.Append($"{p.Name}, PID {p.Key.Pid}{(p.StartUtc > DateTime.MinValue ? $", started {Fmt.Stamp(p.StartUtc)}" : "")}.\r\n");
        string over = p.Over > TimeSpan.Zero ? $" (over {Fmt.Duration(p.Over)})" : "";
        sb.Append($"Handles: {p.Handles:N0}, was {p.HandlesBefore:N0}{over}.\r\n");
        sb.Append($"Pool charged to it: {Fmt.Bytes(p.Quota)}, was {Fmt.Bytes(p.QuotaBefore)}{over}.\r\n");
        sb.Append($"I/O: {p.OpsPerSec:N0} operations a second.\r\n");
        if (rec is not null)
        {
            if (rec.OrphanedUtc is { } o) sb.Append($"Kept running after its parent exited (at {Fmt.Clock(o)}).\r\n");
            var chain = _engine.Registry.Ancestry(rec.Key);
            if (chain.Count > 1) sb.Append("Started by: ").Append(string.Join(" › ", chain.Select(r => r.DisplayName))).Append("\r\n");
            if (rec.CommandLine is { } cl) sb.Append("Command line: ").Append(cl).Append("\r\n");
            if (rec.ImagePath is { } path) sb.Append("Path: ").Append(path).Append("\r\n");
        }
        sb.Append("\r\nA process whose handles or pool charge keep rising holds kernel pool; if it is not meant to, closing it gives the memory back.");
        return sb.ToString();
    }

    private static string FindingDetails(CauseRow row)
    {
        if (row.Finding is not { } f) return "";
        var sb = new System.Text.StringBuilder();
        sb.Append(f.Title).Append("\r\n");
        foreach (var d in f.Details) sb.Append(d).Append("\r\n");
        if (f.Data.GetValueOrDefault("commandLine") is string cl) sb.Append("\r\nCommand line: ").Append(cl).Append("\r\n");
        sb.Append($"\r\n{Fmt.Stamp(f.TimeUtc)} ({f.Kind}, {f.Severity.ToString().ToLowerInvariant()})");
        return sb.ToString();
    }

    /// <summary>What the right-click menu can copy for a Pool causes row.</summary>
    private CopyInfo? CauseCopy(object? key)
    {
        if (key is not string k || _causeByKey.GetValueOrDefault(k) is not { } row) return null;
        switch (row.Kind)
        {
            case CauseKind.Incident when row.Folder is { } dir:
                return new CopyInfo(null, null, dir, null, () => UiActions.IncidentSummary(dir));
            case CauseKind.Tag when row.Tag is { } tag && _rankings.Get(tag) is { } ranked: // none after the window hid: nothing to copy
                return new CopyInfo(null, null, null, null, () => TagSummary(ranked));
            case CauseKind.Process when row.Process is { } pk && _engine.Registry.Get(pk) is { } r:
                return new CopyInfo(r.Pid.ToString(), r.CommandLine, r.ImagePath, null, null);
            case CauseKind.Finding when row.Finding is { } f:
                return new CopyInfo(f.Pid != 0 ? f.Pid.ToString() : null, f.Data.GetValueOrDefault("commandLine") as string, f.ReportDir,
                    string.Join("\r\n", new[] { f.Title }.Concat(f.Details)), null);
            default:
                return null;
        }
    }

    internal static string OwnerDetails(OwnerSuspect o, TagViewData v)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{o.Name}: {OwnerRanking.Strength(o.Confidence)}{(o.CorrelationOnly ? " (correlation only: the confirmation trace did not see it allocate this tag)" : "")} ({o.Owner.Kind})");
        foreach (var p in o.Plain) sb.AppendLine("• " + p);
        sb.AppendLine();
        // What the trace saw this owner's processes do, each with its identity, chain and command line.
        if (o.Trace is { } t)
        {
            sb.AppendLine($"Traced processes ({t.Processes.Count}, net {Fmt.Bytes(t.Net)}, {t.Allocs:N0} allocations, {t.Frees:N0} frees):");
            foreach (var p in t.Processes.OrderByDescending(p => p.Usage.NetBytes).Take(20))
            {
                sb.AppendLine($"{p.Name}  (PID {p.Usage.Pid}{(p.StartUtc is { } st ? $", started {Fmt.Clock(st)}" : "")}{(p.ExitUtc is { } ex ? $", exited {Fmt.Clock(ex)}" : "")}): {Fmt.SignedBytes(p.Usage.NetBytes)} net");
                if (p.Chain.Count > 1) sb.AppendLine("   chain: " + string.Join(" › ", p.Chain));
                if (p.CommandLine is { } tcl) sb.AppendLine("   command line: " + tcl);
            }
            if (t.Processes.Count > 20) sb.AppendLine($"… and {t.Processes.Count - 20} more traced processes");
            if (o.Members.Count > 0) sb.AppendLine().AppendLine("Seen by correlation:");
        }
        for (int i = 0; i < o.Members.Count && i < 20; i++)
        {
            var m = o.Members[i];
            var id = o.Identities[i];
            sb.AppendLine($"{id.Display}  (PID {m.Key.Pid}, started {Fmt.Clock(m.StartUtc)}{(m.ExitUtc is { } x ? $", exited {Fmt.Clock(x)}" : "")}{(m.IsOrphan ? ", kept running after its parent exited" : "")})");
            if (m.Ancestry.Count > 1) sb.AppendLine("   chain: " + string.Join(" › ", m.Ancestry));
            if (m.CommandLine is { } cl) sb.AppendLine("   command line: " + cl);
            if (m.ImagePath is { } path) sb.AppendLine("   path: " + path);
            foreach (var e in m.Evidence) sb.AppendLine("   raw: " + e);
        }
        if (o.MemberCount > 20) sb.AppendLine($"… and {o.MemberCount - 20} more processes");
        return sb.ToString().TrimEnd(); // AppendLine is \r\n on Windows: a TextBox needs that
    }

    /// <summary>A tag's answer, window and suspects with their evidence, as plain text (Copy summary on a tag row).</summary>
    private static string TagSummary(TagViewData v)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(v.Answer.Line).Append("\r\n");
        sb.Append($"{v.Title}\r\nWindow: {Fmt.Stamp(v.FromUtc)} to {Fmt.Stamp(v.ToUtc)}, growth from {Fmt.Clock(v.OnsetUtc)}\r\n\r\n");
        int n = 0;
        foreach (var o in v.Owners.Where(o => o.Meaningful).Take(5)) sb.Append($"{++n}. ").Append(OwnerDetails(o, v)).Append("\r\n\r\n");
        if (n == 0) sb.Append("No suspect stood out.\r\n");
        return sb.ToString().TrimEnd() + "\r\n";
    }

    // ---------------------------------------------------------------- Services

    private TabPage ServicesPage()
    {
        foreach (var (h, w) in new[] { ("Service", 200), ("Group", 80), ("Category", 140), ("State", 230), ("Cost", 190), ("You lose / publisher", 360) })
            _services.Columns.Add(h, S(w));
        _preset.Items.AddRange(["Custom", "Telemetry only", "Dev workstation"]);
        _preset.SelectedIndex = 0;
        _preset.Width = S(150);
        _preset.SelectedIndexChanged += (_, _) => _actions.Guarded(this, "Preset", ApplyPresetTicks);
        Button Btn(string text, string what, Action a) { var b = new Button { Text = text, AutoSize = true, UseVisualStyleBackColor = true }; b.Click += (_, _) => _actions.Guarded(this, what, a); return b; }
        var apply = Btn("Apply (turn off ticked)", "Apply services", ApplyServices);
        var restoreOne = Btn("Restore selected", "Restore service", RestoreSelected);
        var restoreDefault = Btn("Restore to Windows default", "Restore to Windows default", RestoreSelectedToDefault);
        var restore = Btn("Restore all", "Restore services", () => DoRestore(null));
        var refresh = Btn("Refresh", "Refresh services", () => { _servicesLoaded = false; RefreshServices(); });
        _tips.SetToolTip(restoreDefault, "For a selected service that is off outside PoolWatch: set it back to its Windows default start type (reversible with Restore).");
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = Px(0, 2, 0, 4) };
        bar.Controls.AddRange([new Label { Text = "Preset:", AutoSize = true, Margin = Px(3, 8, 0, 0) }, _preset, apply, restoreOne, restoreDefault, restore, refresh]);
        _servicesStatus.Padding = Px(2, 4, 2, 2);
        _servicesStatus.Text = "PoolWatch recommends; you choose; every change is reversible (Restore). Protected services (security, Windows Update, input/audio, core) are greyed out.";

        foreach (var (h, w) in new[] { ("Type", 70), ("Name", 230), ("State", 140), ("Cost", 210), ("Runs / command", 480) }) _tasks.Columns.Add(h, S(w));
        var taskDisable = Btn("Disable", "Disable", () => ToggleTaskOrStartup(false));
        var taskEnable = Btn("Enable", "Enable", () => ToggleTaskOrStartup(true));
        var appEnd = Btn("End process", "End process", EndSelectedApp);
        var appOpen = Btn("Open file location", "Open location", OpenSelectedAppLocation);
        var cBar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = Px(0, 4, 0, 0) };
        cBar.Controls.AddRange([taskDisable, taskEnable, appEnd, appOpen]);
        var cBox = new GroupBox { Text = "Scheduled tasks, startup apps and heavy background apps (non-Microsoft; you choose, reversible)", Dock = DockStyle.Fill, Padding = Px(6, 4, 6, 6) };
        cBox.Controls.Add(_tasks);
        cBox.Controls.Add(cBar);

        _servicesSplit.Panel1.Controls.Add(_services); // fill first, then the docked edges (the last added is the topmost)
        _servicesSplit.Panel1.Controls.Add(bar);
        _servicesSplit.Panel1.Controls.Add(_servicesStatus);
        _servicesSplit.Panel2.Controls.Add(cBox);
        _servicesSplit.SplitterWidth = S(6);
        var page = new TabPage("Services") { UseVisualStyleBackColor = true, Padding = Px(4, 4, 4, 4) };
        page.Controls.Add(_servicesSplit);
        _services.ItemChecked += (_, e) => { if (!_repopulating && e.Item.Tag is Core.Services.ServiceOption o && !o.CanTurnOff && e.Item.Checked) e.Item.Checked = false; };
        return page;
    }

    private bool _servicesLoaded;
    private List<Core.Services.ServiceOption> _serviceOptions = new();

    private void RefreshServices()
    {
        if (_servicesLoaded) { UpdateServiceCosts(); return; }
        _servicesLoaded = true;
        _servicesStatus.Text = "Reading services…";
        Task.Run(() => _analysis.Diagnostics.Services.Options()).ContinueWith(t =>
        {
            if (!IsHandleCreated || IsDisposed || t.IsFaulted) return;
            BeginInvoke(() => ShowServices(t.Result));
        });
        Task.Run(LoadTasksAndApps).ContinueWith(t =>
        {
            if (!IsHandleCreated || IsDisposed || t.IsFaulted) return;
            BeginInvoke(() => ShowTasks(t.Result));
        });
    }

    private List<(string type, string name, string state, string cost, string runs, object tag)> LoadTasksAndApps()
    {
        var rows = new List<(string, string, string, string, string, object)>();
        var d = _analysis.Diagnostics;
        // Cost per task / startup entry: the live processes running its program, matched by image.
        var live = _engine.Registry.LiveRecords().Select(r => new Core.Services.LiveImage(r.Key, r.Image, r.ImagePath)).ToList();
        string Cost(string command)
        {
            string? exe = Core.Services.EntryCost.ExeOf(command, Environment.ExpandEnvironmentVariables);
            var r = Core.Services.EntryCost.ViaLauncher(exe) ? null : Core.Services.EntryCost.Match(exe, live, d.CostOfProcess);
            return Core.Services.EntryCost.Text(exe, r);
        }
        foreach (var t in _tasksEngine.Tasks())
            rows.Add(("task", t.Name, t.Enabled ? "enabled" : "disabled", Cost(t.Runs), t.Runs, t));
        foreach (var su in _tasksEngine.Startup().Where(s => !s.Microsoft))
            rows.Add(("startup", su.Name, su.Enabled ? "enabled" : "disabled", Cost(su.Command), $"{su.Location}: {su.Command}", su));
        // Heavy background apps: over 0.5 core averaged over the last 30 minutes, never with a window in front (judged on
        // minute points, not one 10 s window).
        foreach (var h in d.HeavyBackground().Take(10))
        {
            var rec = _engine.Registry.Get(h.Key);
            string identity = rec is not null ? Core.Identity.ProcessIdentities.Of(_engine.Registry.Ancestry(rec.Key)).Display : h.Image;
            var app = new HeavyApp(h.Key, h.Image, identity, rec?.CommandLine, h.Cores);
            rows.Add(("app", identity, "running, no window in front", $"{Core.Services.ServicePlan.Cost(h.Cores, h.PageFaultsPerSec)} (average over {(int)h.Covered.TotalMinutes} min)",
                $"PID {h.Key.Pid}{(h.Key.CreateTime > 0 ? $", started {Fmt.Clock(DateTime.FromFileTimeUtc(h.Key.CreateTime))}" : "")}{(rec?.CommandLine is { } cl ? ": " + cl : "")}", app));
        }
        return rows;
    }

    /// <summary>A heavy background app, keyed by PID AND create time so End process never kills a reused PID.</summary>
    private sealed record HeavyApp(ProcessKey Key, string Image, string Identity, string? CommandLine, double Cores);

    private void ShowTasks(List<(string type, string name, string state, string cost, string runs, object tag)> rows) =>
        KeepPlace(_tasks, it => it.SubItems.Count > 1 ? it.SubItems[0].Text + "\u001f" + it.SubItems[1].Text : null, () =>
        {
            _tasks.BeginUpdate();
            try
            {
                _tasks.Items.Clear();
                foreach (var (type, name, state, cost, runs, tag) in rows)
                    _tasks.Items.Add(new ListViewItem([type, name, state, cost, runs]) { Tag = tag });
            }
            finally { _tasks.EndUpdate(); }
        });

    private void ToggleTaskOrStartup(bool enable)
    {
        if (_tasks.SelectedItems.Count == 0) return;
        var tag = _tasks.SelectedItems[0].Tag;
        string? err = tag switch
        {
            Engine.ScheduledTaskView t => _tasksEngine.SetTaskEnabled(t.Path, enable),
            Engine.StartupView su => _tasksEngine.SetStartupEnabled(su.Location, su.Name, enable),
            _ => "select a scheduled task or startup entry",
        };
        if (err is not null) MessageBox.Show(this, err, "PoolWatch");
        else
        {
            // Every change is on the timeline, like the service changes, and under Active changes.
            string what = tag switch
            {
                Engine.ScheduledTaskView t => $"Scheduled task {(enable ? "enabled" : "disabled")} by you: {t.Path}",
                Engine.StartupView su => $"Startup entry {(enable ? "enabled" : "disabled")} by you: {su.Location}\\{su.Name} (Task Manager shows the same switch)",
                _ => "",
            };
            if (what.Length > 0) _analysis.Diagnostics.RecordOwnerChange(what, tag is Engine.StartupView s ? s.Name : (tag as Engine.ScheduledTaskView)?.Name);
            if (tag is Engine.ScheduledTaskView task) _context.Ledger.Records.RecordToggle(ChangeKind.ScheduledTask, task.Path, task.Name, enable, "the Services tab");
            else if (tag is Engine.StartupView entry) _context.Ledger.Records.RecordToggle(ChangeKind.StartupEntry, entry.Location, entry.Name, enable, "the Services tab");
        }
        _servicesLoaded = false; RefreshServices();
    }

    private void EndSelectedApp()
    {
        if (_tasks.SelectedItems.Count == 0 || _tasks.SelectedItems[0].Tag is not HeavyApp app) { MessageBox.Show(this, "Select a background app.", "PoolWatch"); return; }
        // Never a core / security process, a service host or a VM's memory process.
        var hosted = _engine.Services.Where(s => s.ProcessId == app.Key.Pid).Select(s => s.Name).ToList();
        if (Core.Services.EndProcessPolicy.WhyNot(app.Key.Pid, app.Image, hosted) is { } why)
        {
            MessageBox.Show(this, $"PoolWatch will not end {app.Identity}: {why}.", "PoolWatch");
            return;
        }
        // The handle is opened first, the create time read from it and compared exactly, the owner asked,
        // and the process ended through that same handle (Engine.ProcessEnder). An unknown create time ends nothing.
        string detail = $"{app.Identity}\nPID {app.Key.Pid}" +
            (app.Key.CreateTime > 0 ? $", started {Fmt.Clock(DateTime.FromFileTimeUtc(app.Key.CreateTime))}" : "") +
            $"\n{app.Image}" + (app.CommandLine is { } cl ? $"\n{cl}" : "");
        var r = Engine.ProcessEnder.End(new Engine.WindowsProcessOpener(), app.Key, app.Identity, () =>
            MessageBox.Show(this, $"End this process?\n\n{detail}\n\nUnsaved work in it is lost. PoolWatch never uninstalls anything.",
                "PoolWatch: end process", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK);
        if (r.Outcome == Engine.ProcessEnder.Outcome.Cancelled) return;
        if (r.Outcome == Engine.ProcessEnder.Outcome.Ended) _analysis.Diagnostics.RecordOwnerChange($"Process ended by you: {app.Identity} (PID {app.Key.Pid})", app.Image);
        else MessageBox.Show(this, r.Message, "PoolWatch");
        _servicesLoaded = false; RefreshServices();
    }

    private void OpenSelectedAppLocation()
    {
        if (_tasks.SelectedItems.Count == 0 || _tasks.SelectedItems[0].Tag is not HeavyApp app) { MessageBox.Show(this, "Select a background app.", "PoolWatch"); return; }
        try
        {
            string? path = System.Diagnostics.Process.GetProcessById(app.Key.Pid).MainModule?.FileName;
            if (path is not null) UiActions.ShowFile(path);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "PoolWatch"); }
    }

    private void ShowServices(List<Core.Services.ServiceOption> options)
    {
        _serviceOptions = options.OrderBy(o => o.Group).ThenByDescending(o => o.Service.Cores).ThenBy(o => o.Service.Name).ToList();
        _repopulating = true;
        try { KeepPlace(_services, it => (it.Tag as Core.Services.ServiceOption)?.Service.Name, FillServices); }
        finally { _repopulating = false; }
        int off = _analysis.Diagnostics.Services.Changes.Count;
        _servicesStatus.Text = $"{_serviceOptions.Count} services; {_serviceOptions.Count(o => o.CanTurnOff)} you may turn off, {_serviceOptions.Count(o => o.Group == Core.Services.ServiceGroup.Protected)} protected." +
            (off > 0 ? $" {off} changed by PoolWatch (Restore all to undo)." : "") + " Tick, pick a preset or tick by hand, then Apply.";
    }

    /// <summary>
    /// The Cost cell: the live CPU and page faults of the service's process (a shared host says so), or, for a service
    /// PoolWatch turned off, what that saved over the hour after the change.
    /// </summary>
    private string CostCell(Core.Services.ServiceOption o)
    {
        if (o.PoolWatchChange is not null && _analysis.Diagnostics.Services.SavingsText(o.Service.Name) is { } saved) return saved;
        var live = o.Service.Running && o.Service.ProcessId > 0 && _analysis.Diagnostics.CostOfPid(o.Service.ProcessId) is { } c
            ? o.Service with { Cores = c.cores, PageFaultsPerSec = c.faultsPerSec } : o.Service;
        return Core.Services.ServicePlan.CostText(live);
    }

    /// <summary>Every refresh while the page shows: the Cost cells only (the list itself is re-read on Refresh / Apply).</summary>
    private void UpdateServiceCosts()
    {
        foreach (ListViewItem it in _services.Items)
            if (it.Tag is Core.Services.ServiceOption o && it.SubItems.Count > 4 && CostCell(o) is var cost && it.SubItems[4].Text != cost)
                it.SubItems[4].Text = cost;
    }

    private void FillServices()
    {
        _services.BeginUpdate();
        try
        {
            _services.Items.Clear();
            foreach (var o in _serviceOptions)
            {
                string group = o.Group switch { Core.Services.ServiceGroup.Optional => "optional", Core.Services.ServiceGroup.Unknown => "unknown", _ => "protected" };
                string state = Core.Services.ServicePlan.StateText(o);
                if (o.CanRestoreDefault) state += $"; Windows default: {o.WindowsDefault}";
                string cost = CostCell(o);
                string detail = o.Catalogue is { } c ? $"lose: {c.Lose}" + (c.Note is { } n ? $" — {n}" : "") : o.Service.Publisher ?? o.ProtectedReason ?? o.Service.Description ?? "";
                var it = new ListViewItem([o.Service.Name, group, o.Catalogue?.Category ?? "", state, cost, detail]) { Tag = o };
                if (o.Group == Core.Services.ServiceGroup.Protected) it.ForeColor = Color.Gray;
                else if (o.Catalogue is { Risk: Core.Services.ServiceRisk.CheckFirst }) it.ForeColor = Color.DarkGoldenrod;
                _services.Items.Add(it);
            }
        }
        finally { _services.EndUpdate(); }
    }

    private void ApplyPresetTicks()
    {
        var preset = _preset.SelectedIndex switch { 1 => Core.Services.Preset.TelemetryOnly, 2 => Core.Services.Preset.DevWorkstation, _ => Core.Services.Preset.Custom };
        if (preset == Core.Services.Preset.Custom) return;
        var ticked = Core.Services.ServicePlan.Tick(preset, _serviceOptions);
        _repopulating = true;
        foreach (ListViewItem it in _services.Items) it.Checked = it.Tag is Core.Services.ServiceOption o && ticked.Contains(o.Service.Name);
        _repopulating = false;
    }

    private void ApplyServices()
    {
        var ticked = _services.Items.Cast<ListViewItem>().Where(i => i.Checked && i.Tag is Core.Services.ServiceOption o && o.CanTurnOff)
            .Select(i => ((Core.Services.ServiceOption)i.Tag!).Service.Name).ToList();
        if (ticked.Count == 0) { MessageBox.Show(this, "Tick the services to turn off first.", "PoolWatch"); return; }
        bool anyUnknown = ticked.Any(n => _serviceOptions.First(o => o.Service.Name == n).Group == Core.Services.ServiceGroup.Unknown);
        string summary = $"{ticked.Count} service(s) will be stopped and set to not start:\n\n  {string.Join("\n  ", ticked)}\n\nThe original is saved first; Restore puts it back. Continue?";
        if (MessageBox.Show(this, summary, "PoolWatch: turn off services", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        if (anyUnknown && MessageBox.Show(this, "Some are third-party services PoolWatch does not recognise (\"you decide\"). Turn them off too?", "PoolWatch", MessageBoxButtons.YesNo) != DialogResult.Yes)
            ticked = ticked.Where(n => _serviceOptions.First(o => o.Service.Name == n).Group != Core.Services.ServiceGroup.Unknown).ToList();
        var preset = _preset.SelectedIndex switch { 1 => "Telemetry only", 2 => "Dev workstation", _ => "manual" };
        RunServiceOp(() => _analysis.Diagnostics.Services.Apply(ticked, preset));
    }

    private void DoRestore(string? service)
    {
        if (_analysis.Diagnostics.Services.Changes.Count == 0) { MessageBox.Show(this, "Nothing to restore: PoolWatch has not changed any service.", "PoolWatch"); return; }
        RunServiceOp(() => _analysis.Diagnostics.Services.Restore(service));
    }

    private Core.Services.ServiceOption? SelectedService() =>
        _services.SelectedItems.Count > 0 ? _services.SelectedItems[0].Tag as Core.Services.ServiceOption : null;

    /// <summary>Per-service Restore: puts back exactly what PoolWatch saved before changing it (and starts it if it was running).</summary>
    private void RestoreSelected()
    {
        if (SelectedService() is not { } o) { MessageBox.Show(this, "Select a service first.", "PoolWatch"); return; }
        if (o.PoolWatchChange is not { } c) { MessageBox.Show(this, $"PoolWatch has not changed {o.Service.Name}: nothing to restore.", "PoolWatch"); return; }
        string again = Core.Services.ServicePlan.StartAfterRestore(c) ? " and start it again (it was running before)" : "";
        if (MessageBox.Show(this, $"Restore {o.Service.Name} to {c.OriginalStart}{again}?", "PoolWatch: restore service", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        RunServiceOp(() => _analysis.Diagnostics.Services.Restore(o.Service.Name));
    }

    /// <summary>"Restore to Windows default" for a service that is off outside PoolWatch (through the same gate; reversible with Restore).</summary>
    private void RestoreSelectedToDefault()
    {
        if (SelectedService() is not { } o) { MessageBox.Show(this, "Select a service first.", "PoolWatch"); return; }
        if (!o.CanRestoreDefault)
        {
            string why = o.Group == Core.Services.ServiceGroup.Protected ? "it is a protected service"
                : o.PoolWatchChange is not null ? "PoolWatch changed it: use Restore"
                : o.OffOutsidePoolWatch != Core.Services.ServicePlan.OffOutside ? "it is not off outside PoolWatch"
                : "PoolWatch does not know its Windows default start type (set it in services.msc)";
            MessageBox.Show(this, $"{o.Service.Name}: not offered: {why}.", "PoolWatch");
            return;
        }
        if (MessageBox.Show(this, $"Set {o.Service.Name} back to its Windows default ({o.WindowsDefault})?\n\nIt starts at the next boot or when something asks for it. " +
                "Your current setting (Disabled) is saved first; Restore puts it back.", "PoolWatch: Windows default", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        RunServiceOp(() => _analysis.Diagnostics.Services.RestoreToDefault(o.Service.Name));
    }

    /// <summary>Runs a service change off the UI thread (it can take seconds), then re-reads the list.</summary>
    private void RunServiceOp(Func<List<(string service, string? error)>> op)
    {
        _servicesStatus.Text = "Applying…";
        Task.Run(op).ContinueWith(t =>
        {
            if (!IsHandleCreated || IsDisposed) return;
            BeginInvoke(() =>
            {
                string msg = t.IsFaulted ? t.Exception!.GetBaseException().Message
                    : string.Join("\n", t.Result.Select(r => r.error is null ? $"{r.service}: done" : $"{r.service}: {r.error}"));
                MessageBox.Show(this, msg, "PoolWatch: services");
                _servicesLoaded = false;
                RefreshServices();
            });
        });
    }

    // ---------------------------------------------------------------- refresh

    private void UpdateTimer()
    {
        bool active = Visible && WindowState != FormWindowState.Minimized;
        _timer.Enabled = active;
        if (active) RefreshNow();
    }

    private long _statusSamples = -1;

    public void RefreshNow() => RefreshNow(force: false);

    private void RefreshNow(bool force)
    {
        if (!Visible || WindowState == FormWindowState.Minimized) return;
        SafeRefresh(() => RefreshVisible(force));
    }

    private void RefreshVisible(bool force)
    {
        using var _ui = Core.Util.Profiler.Stage("ui.refresh");
        var state = UiData.State(_analysis);
        if (_engine.SampleCount != _statusSamples || force) // the chart and the status line change only with a new sample
        {
            _statusSamples = _engine.SampleCount;
            _chart.SetData(UiData.Chart(_analysis, PoolChart.Span));
            _chart.SetClears(UiData.Clears(_analysis, PoolChart.Span));
            UpdateStatus(state);
        }
        if (!_isAdvanced)
            using (Core.Util.Profiler.Stage("ui.simple")) ReadSimple(force);
        else
            using (Core.Util.Profiler.Stage("ui.page" + _tabs.SelectedIndex))
                switch (_tabs.SelectedIndex)
                {
                    case 0: RefreshCauses(force); break;
                    case 1: RefreshServices(); break;
                }
        Icon = TrayIcons.For(state);
        ShowUpdate(_update());
        PumpRankings();
    }

    /// <summary>The update notice at the bottom of the window, set only when the release to tell about changed.</summary>
    internal void ShowUpdate(UpdateRelease? release)
    {
        if (release == _shownUpdate) return;
        _shownUpdate = release;
        _updateNotice.Visible = release is not null;
        if (release is null) return;
        const string link = "See what's new";
        _updateNotice.Text = $"{release.Text}. {link}";
        _updateNotice.LinkArea = new LinkArea(_updateNotice.Text.Length - link.Length, link.Length);
    }

    /// <summary>The update notice as shown, or null (CI).</summary>
    internal string? UpdateNotice => _updateNotice.Visible ? _updateNotice.Text : null;

    private PoolTag? _leadTag;
    private List<PoolTag> _rankTags = [];

    /// <summary>
    /// Starts the next tag ranking in the background (TagRankings / RankPlan): the status line's lead tag first, then, in the
    /// Advanced view, the list's tag rows and the selected row's tag at once. When it is done the rows and the status line
    /// are refreshed.
    /// </summary>
    private void PumpRankings()
    {
        var wanted = new List<PoolTag>();
        if (_leadTag is { } lead) wanted.Add(lead);
        if (_isAdvanced) wanted.AddRange(_rankTags.Where(t => t != _leadTag));
        PoolTag? selected = _isAdvanced && SelectedRow() is { Kind: CauseKind.Tag, Active: true, Tag: { } sel } ? sel : null;
        _rankings.Pump(tag => TagViewBuilder.Build(_analysis, _engine.Registry, tag), wanted, selected, () =>
        {
            try { if (!IsDisposed && IsHandleCreated) BeginInvoke(OnRanked); }
            catch (InvalidOperationException) { } // the window is closing
        });
    }

    private void OnRanked()
    {
        _causesSamples = -1; // rebuild the rows (the new cause) and the status line (the lead's cause) on this refresh
        _statusSamples = -1;
        RefreshNow();
    }

    /// <summary>The item (row key) each read-only text box shows, so a refresh knows a new item from an update of the same one.</summary>
    private readonly Dictionary<TextBox, object?> _textItems = new();

    /// <summary>
    /// Updates a read-only text box without making it jump. A different <paramref name="item"/> always replaces the
    /// text, even while text is selected. For the same item the first visible line and the caret are kept, and a selection
    /// is kept when the selected text is unchanged; an update that would destroy the selection waits only while the box
    /// has focus (Core.Util.TextRefresh).
    /// </summary>
    private void SetText(TextBox tb, string text, object? item)
    {
        bool same = _textItems.TryGetValue(tb, out var shown) && Equals(shown, item);
        var action = TextRefresh.Plan(tb.Text, text, same, tb.SelectionStart, tb.SelectionLength, tb.ContainsFocus);
        if (action == TextRefreshAction.Hold) return;
        _textItems[tb] = item;
        if (action == TextRefreshAction.None) return;
        if (action == TextRefreshAction.Replace)
        {
            tb.Text = text; // a new item starts at the top with nothing selected
            tb.Select(0, 0);
            return;
        }
        int selStart = tb.SelectionStart, selLen = tb.SelectionLength;
        int firstLine = tb.IsHandleCreated ? (int)SendMessage(tb.Handle, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero) : 0;
        tb.Text = text;
        if (action == TextRefreshAction.UpdateKeepSelection) tb.Select(selStart, selLen);
        else tb.Select(Math.Min(selStart, text.Length), 0);
        // EM_SETSEL does not scroll; put the same line back at the top (setting the text scrolled to line 0).
        if (firstLine > 0 && tb.IsHandleCreated) SendMessage(tb.Handle, EM_LINESCROLL, IntPtr.Zero, (IntPtr)firstLine);
    }

    private const int EM_GETFIRSTVISIBLELINE = 0x00CE, EM_LINESCROLL = 0x00B6;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// A list rebuilt from scratch (the services and the tasks lists) keeps its top visible row and its selection, matched by
    /// <paramref name="key"/>.
    /// </summary>
    private static void KeepPlace(ListView lv, Func<ListViewItem, object?> key, Action rebuild)
    {
        int topIndex = 0;
        var fromTop = new List<object>();
        var selected = new List<object>();
        try
        {
            if (lv.IsHandleCreated && lv.Items.Count > 0 && lv.TopItem is { } top)
            {
                topIndex = top.Index;
                for (int i = top.Index; i < lv.Items.Count && fromTop.Count < 64; i++) if (key(lv.Items[i]) is { } k) fromTop.Add(k);
            }
            foreach (ListViewItem it in lv.SelectedItems) if (key(it) is { } k) selected.Add(k);
        }
        catch (Exception) { /* best effort */ }
        rebuild();
        try
        {
            var byKey = new Dictionary<object, ListViewItem>();
            foreach (ListViewItem it in lv.Items) if (key(it) is { } k) byKey.TryAdd(k, it);
            foreach (var k in selected) if (byKey.TryGetValue(k, out var it)) it.Selected = true;
            if (lv.IsHandleCreated && ScrollAnchor.Pick(topIndex, fromTop, byKey.ContainsKey) is { } a && byKey.TryGetValue(a, out var t)) lv.TopItem = t;
        }
        catch (Exception) { }
    }

    private string? _lastRefreshError;

    /// <summary>
    /// A refresh runs from the timer and from selection events, outside <see cref="UiActions.Guarded"/>; an exception
    /// there went to WinForms' unhandled-exception path. It is logged (once per distinct error) and the window keeps running.
    /// </summary>
    private void SafeRefresh(Action refresh)
    {
        try { refresh(); }
        catch (Exception ex)
        {
            string sig = ex.GetType().Name + ": " + ex.Message;
            if (sig == _lastRefreshError) return;
            _lastRefreshError = sig;
            try { _engine.Log.Info("window refresh failed (the window keeps running): " + ex); } catch { }
        }
    }

    /// <summary>Ctrl+C in a read-only text box goes through the copy redaction setting too.</summary>
    private void EnableTextCopy(TextBox tb) => tb.KeyDown += (_, e) =>
    {
        if (e.Control && e.KeyCode == Keys.C && tb.SelectionLength > 0) { _actions.Copy(tb.SelectedText); e.Handled = e.SuppressKeyPress = true; }
    };

    /// <summary>What the right-click menu can copy for a row (null entries are left out of the menu).</summary>
    private sealed record CopyInfo(string? Pid, string? CommandLine, string? Path, string? Evidence, Func<string>? Summary);

    private void EnableCopy(ListView lv, Func<object?, CopyInfo?> info)
    {
        lv.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.C) { _actions.Copy(RowsText(lv, selectedOnly: true)); e.Handled = true; }
        };
        var menu = new ContextMenuStrip();
        menu.Opening += (_, e) =>
        {
            menu.Items.Clear();
            var key = lv.SelectedItems.Count > 0 ? lv.SelectedItems[0].Tag : null;
            if (key is null) { e.Cancel = true; return; }
            menu.Items.Add("Copy row", null, (_, _) => _actions.Copy(RowsText(lv, selectedOnly: true)));
            menu.Items.Add("Copy all rows", null, (_, _) => _actions.Copy(RowsText(lv, selectedOnly: false)));
            if (info(key) is { } i)
            {
                if (i.CommandLine is { } cl) menu.Items.Add("Copy command line", null, (_, _) => _actions.Copy(cl));
                if (i.Path is { } p) menu.Items.Add("Copy path", null, (_, _) => _actions.Copy(p));
                if (i.Pid is { } pid) menu.Items.Add("Copy PID", null, (_, _) => _actions.Copy(pid));
                if (i.Evidence is { } ev) menu.Items.Add("Copy evidence", null, (_, _) => _actions.Copy(ev));
                if (i.Summary is { } sum) menu.Items.Add("Copy summary", null, (_, _) => _actions.Guarded(this, "Copy summary", () => _actions.Copy(sum())));
            }
        };
        lv.ContextMenuStrip = menu;
    }

    /// <summary>Tab-separated text with a header row: exactly the cells the list shows.</summary>
    internal static string RowsText(ListView lv, bool selectedOnly)
    {
        var headers = lv.Columns.Cast<ColumnHeader>().Select(c => c.Text).ToList();
        var items = selectedOnly ? lv.SelectedItems.Cast<ListViewItem>() : lv.Items.Cast<ListViewItem>();
        return CopyText.Tsv(headers, items.OrderBy(i => i.Index).Select(i => (IReadOnlyList<string>)Enumerable.Range(0, headers.Count)
            .Select(c => c < i.SubItems.Count ? i.SubItems[c].Text : "").ToList()));
    }

    private readonly Dictionary<ListView, ListSync> _syncs = new();
    private readonly Dictionary<ListView, string> _listSignature = new();

    /// <summary>
    /// Updates a list in place (no clear-and-rebuild: that made the window flash). Rows are matched by their Tag; the
    /// selection and scroll position stay, and the order is held while a row is selected or the mouse is over the list.
    /// </summary>
    /// <returns>False when the rows were unchanged (nothing was touched).</returns>
    private bool Update(ListView lv, List<ListViewItem> items)
    {
        // Unchanged rows: nothing to do (even an in-place sync walks every row).
        var sig = new System.Text.StringBuilder(items.Count * 64);
        foreach (var it in items)
        {
            sig.Append(it.Tag).Append('\u001e');
            foreach (ListViewItem.ListViewSubItem sub in it.SubItems) sig.Append(sub.Text).Append('\u001f');
            sig.Append(it.BackColor.ToArgb()).Append(it.ForeColor.ToArgb()).Append('\n');
        }
        string signature = sig.ToString();
        if (!_syncs.TryGetValue(lv, out var sync)) _syncs[lv] = sync = new ListSync(lv);
        if (!sync.OrderHeld && _listSignature.TryGetValue(lv, out var last) && last == signature) return false;
        _listSignature[lv] = signature;
        _repopulating = true;
        try
        {
            sync.Update(items.Select(i => new ListSync.Row(i.Tag!, i.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(s => s.Text).ToArray(),
                i.BackColor == Color.Empty || i.BackColor == SystemColors.Window ? null : i.BackColor,
                Fore: i.ForeColor == Color.Empty || i.ForeColor == SystemColors.WindowText ? null : i.ForeColor)).ToList());
        }
        catch (Exception) { /* the handle may not exist yet (hidden tab) */ }
        finally { _repopulating = false; }
        return true;
    }

    /// <summary>
    /// The elevated app's redaction for exports and copies: the home folder in its long and 8.3 forms (TEMP is often
    /// C:\Users\JOHNDO~1\AppData\Local\Temp) and the TEMP folders, so a short path in a command line cannot carry part of
    /// the user name out of a redacted export. TEMP is redacted as &lt;TEMP&gt;.
    /// </summary>
    internal static RedactionInfo CurrentRedaction()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var homes = new List<string> { home };
        if (ShortPath(home) is { } sh && !sh.Equals(home, StringComparison.OrdinalIgnoreCase)) homes.Add(sh);
        var temps = new List<string>();
        foreach (var v in new[] { "TEMP", "TMP" })
            if (Environment.GetEnvironmentVariable(v) is { Length: > 3 } t)
            {
                temps.Add(t);
                if (ShortPath(t) is { } st) temps.Add(st);
            }
        return new(Environment.UserName, Environment.MachineName, homes.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), Environment.UserDomainName,
            System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value, temps.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static string? ShortPath(string path)
    {
        var sb = new System.Text.StringBuilder(520);
        try { return GetShortPathName(path, sb, sb.Capacity) is > 0 and < 520 ? sb.ToString() : null; } catch { return null; }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint GetShortPathName(string longPath, System.Text.StringBuilder shortPath, int size);

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing the window hides it; the tray keeps monitoring. Exit is in the tray menu.
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        base.OnFormClosing(e);
    }

    // ---------------------------------------------------------------- CI

    /// <summary>
    /// CI soak: every minute show the next page, as a user would: the Simple view, then Pool causes with its top row
    /// selected, then Services (profiled as ui.simple, ui.page0 and ui.page1).
    /// </summary>
    public void StartSoakCycle()
    {
        var t = new System.Windows.Forms.Timer { Interval = 60_000 };
        int step = 0;
        t.Tick += (_, _) => SafeRefresh(() =>
        {
            int page = step++ % (_tabs.TabCount + 1) - 1; // -1: the Simple view
            if (page < 0) { SetView(false); return; }
            if (!_isAdvanced) SetView(true);
            _tabs.SelectedIndex = page;
            if (page == 0 && _causes.Items.Count > 0 && _causes.SelectedItems.Count == 0) _causes.Items[0].Selected = true;
            RefreshNow();
        });
        t.Start();
    }

    /// <summary>The selected tab's row count (CI).</summary>
    private static int Rows(ListView lv) => lv.Items.Count;

    /// <summary>The header buttons the window shows now (CI: what is on screen, not what the view should show).</summary>
    private List<string> VisibleButtons() => _headerButtons.Where(b => b.Visible).Select(b => b.Text).ToList();

    /// <summary>
    /// CI: the Simple view as the window opens (its buttons, footer, line, cards and anything clipped), then the Advanced
    /// view (status, rows, the selected row's details, whether a refresh keeps rows and the selection, whether anything is
    /// clipped or overlaps), Settings with its Advanced grid, whether hiding the window stops the refresh timer, and
    /// whether Pool causes lists its rows again once the window shows after a hide.
    /// </summary>
    public object SmokeStats()
    {
        SetView(false);
        Application.DoEvents();
        var simple = new
        {
            buttons = VisibleButtons(),
            footer = new[] { _appliedChanges.Text, _advancedBox.Text },
            tabsShown = _tabs.Visible,
            view = _simple.SmokeStats(),
            layoutProblems = LayoutProblems(),
        };
        SetView(true);
        Application.DoEvents();
        var advancedButtons = VisibleButtons();
        _tabs.SelectedIndex = 1;
        RefreshNow(force: true);
        int serviceRows = Rows(_services);
        _tabs.SelectedIndex = 0;
        RefreshNow(force: true);
        var kinds = _causeRows.GroupBy(r => r.Kind.ToString()).ToDictionary(g => g.Key, g => g.Count());
        // No flashing: one refresh keeps the row objects and the selection; Ctrl+C copies exactly what the row shows.
        var before = _causes.Items.Cast<ListViewItem>().ToDictionary(i => i.Tag!, i => i);
        _causes.SelectedItems.Clear();
        if (_causes.Items.Count > 0) _causes.Items[0].Selected = true;
        ShowCauseDetails();
        var selectedKey = _causes.SelectedItems.Count > 0 ? _causes.SelectedItems[0].Tag : null;
        RefreshNow(force: true);
        int kept = _causes.Items.Cast<ListViewItem>().Count(i => before.TryGetValue(i.Tag!, out var old) && ReferenceEquals(old, i));
        double identityKept = _causes.Items.Count == 0 ? 0 : kept / (double)_causes.Items.Count;
        bool selectionKept = selectedKey is not null && _causes.SelectedItems.Count == 1 && Equals(_causes.SelectedItems[0].Tag, selectedKey);
        string copied = RowsText(_causes, selectedOnly: true);
        var lines = copied.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        bool copyMatches = lines.Length == 2 && lines[0] == string.Join('\t', _causes.Columns.Cast<ColumnHeader>().Select(c => c.Text)) &&
                           _causes.SelectedItems.Count == 1 && lines[1] == string.Join('\t', _causes.SelectedItems[0].SubItems.Cast<ListViewItem.ListViewSubItem>().Select(s => CopyText.Cell(s.Text)));
        string detailsTitle = _detailTitle.Text, details = _detailText.Text;
        // Both tabs, each with the tab selected (a hidden page is not laid out for the user): Services once its list has
        // loaded, as the user sees it (its status text and wrapping button bar are what clipped before).
        _tabs.SelectedIndex = 1;
        RefreshNow(force: true);
        for (int i = 0; i < 200 && _serviceOptions.Count == 0; i++) { Application.DoEvents(); Thread.Sleep(100); }
        List<string> BothTabs()
        {
            _tabs.SelectedIndex = 0;
            Application.DoEvents();
            var p = LayoutProblems().Select(x => "Pool causes: " + x).ToList();
            _tabs.SelectedIndex = 1;
            Application.DoEvents();
            p.AddRange(LayoutProblems().Select(x => "Services: " + x));
            _tabs.SelectedIndex = 0;
            return p;
        }
        var layout = BothTabs();
        // The smallest size the window allows: reported (the CI asserts the default size).
        var size = Size;
        Size = MinimumSize;
        Application.DoEvents();
        var layoutAtMinimum = BothTabs();
        Size = size;
        Application.DoEvents();
        // The Settings form and its Advanced grid, each shown as it opens.
        var settingsLayout = new List<string>();
        var settingsForm = new List<string>();
        using (var settings = NewSettings())
        {
            settings.Show(this);
            Application.DoEvents();
            ClippedControls(settings, settingsLayout);
            // The form sizes itself to its content: it must still fit the screen it opens on.
            if (Screen.FromControl(settings).WorkingArea is var area && (settings.Height > area.Height || settings.Width > area.Width))
                settingsLayout.Add($"the Settings form ({settings.Size}) is larger than the screen's working area ({area.Size})");
            // Every text on the form, its grey hints too (CI checks the token-leak guard's line is there).
            void Texts(Control parent) { foreach (Control c in parent.Controls) { if (c is CheckBox or Button or Label) settingsForm.Add(c.Text); Texts(c); } }
            Texts(settings);
            settings.Hide();
            var advanced = settings.Advanced;
            advanced.Show(this);
            Application.DoEvents();
            var advancedLayout = new List<string>();
            ClippedControls(advanced, advancedLayout);
            settingsLayout.AddRange(advancedLayout.Select(x => "Advanced: " + x));
            advanced.Hide();
        }
        // Read before hiding: the window drops its rows when it hides.
        var rows = new Dictionary<string, int> { ["Pool causes"] = _causeRows.Count, ["Services"] = Math.Max(serviceRows, Rows(_services)) };
        var likelyCauses = _causeRows.Take(20).Select(r => r.Cause).ToList();
        int unrankedTagRows = _causeRows.Count(r => r.Kind == CauseKind.Tag && r.Active && r.Cause.Length == 0);
        SetView(false); // as the window opens again
        bool timerWhileVisible = _timer.Enabled;
        Hide();
        bool timerWhileHidden = _timer.Enabled;
        // The hide dropped the rows: shown again, Pool causes must list them again (the leak's incident at least).
        Show();
        SetView(true);
        _tabs.SelectedIndex = 0;
        RefreshNow(force: true);
        int rowsAfterHide = Rows(_causes);
        SetView(false);
        Hide();
        return new
        {
            simple,
            status = _status.Text, statusKind = _statusKind.ToString(), chartPoints = _chart.PointCount,
            tabs = _tabs.TabPages.Cast<TabPage>().Select(p => p.Text).ToList(),
            buttons = advancedButtons,
            rows, causeKinds = kinds, detailsTitle, details = details.Length > 600 ? details[..600] : details,
            likelyCauses, unrankedTagRows,
            identityKept, selectionKept, copyMatches, copiedSample = copied,
            layoutProblems = layout, layoutProblemsAtMinimumSize = layoutAtMinimum, settingsLayoutProblems = settingsLayout, settingsForm,
            size = new { width = size.Width, height = size.Height, dpi = DeviceDpi },
            timerWhileVisible, timerWhileHidden, rowsAfterHide,
        };
    }

    /// <summary>
    /// CI: text that does not fit, controls outside their parent, the status line cut off, and the chart's text overlapping
    /// (its title, legend and axis labels). Empty when the window is clean.
    /// </summary>
    internal List<string> LayoutProblems()
    {
        PerformLayout();
        _chart.Refresh(); // paint now, so the chart's layout is the current one
        var p = new List<string>();
        int need = PaintedLabel.Measure(_status.Text, _status.Font, Math.Max(1, _status.ClientSize.Width));
        if (need > _status.ClientSize.Height) p.Add($"the status line needs {need} px, has {_status.ClientSize.Height}");
        if (_header.Bottom > ClientSize.Height || _status.Width < S(150)) p.Add($"the header does not fit ({_header.Bounds}, status {_status.Bounds})");
        ClippedControls(this, p);
        if (_chart.LastLayout is { } l) p.AddRange(l.Problems().Select(x => "chart: " + x));
        return p;
    }

    /// <summary>
    /// Visible controls outside their parent, and buttons, check boxes and labels whose text does not fit (CI). A parent
    /// that scrolls (AutoScroll: About on a screen too short for it) may hold controls above or below what it shows, as
    /// the user scrolls to them; never beside it, as no PoolWatch window scrolls sideways.
    /// </summary>
    internal static void ClippedControls(Control parent, List<string> p)
    {
        bool scrolls = parent is ScrollableControl { AutoScroll: true };
        foreach (Control c in parent.Controls)
        {
            if (!c.Visible) continue;
            var area = parent.ClientRectangle;
            if (c.Left < -1 || c.Right > area.Width + 1 || (!scrolls && (c.Top < -1 || c.Bottom > area.Height + 1)))
                p.Add($"{Describe(c)} at {c.Bounds} is outside {Describe(parent)} ({area.Size})");
            // (A label with a maximum width wraps by design: its height follows its text.)
            if (c is Button or CheckBox || c is Label { AutoSize: true, MaximumSize.Width: 0 })
            {
                var pref = c.GetPreferredSize(Size.Empty);
                if (pref.Width > c.Width + 1 || pref.Height > c.Height + 1) p.Add($"{Describe(c)} needs {pref}, has {c.Size}");
            }
            if (c is WrapLabel w && w.NeededHeight > w.Height + 1) p.Add($"{Describe(c)} needs {w.NeededHeight} px, has {w.Height}");
            if (c is Panel or TabControl or GroupBox or SplitContainer) ClippedControls(c, p);
        }

        static string Describe(Control c) => $"{c.GetType().Name} \"{(c.Text.Length > 40 ? c.Text[..40] + "…" : c.Text)}\"";
    }

    /// <summary>
    /// CI/README screenshots: the Simple view as the window opens, the Advanced view with the top cause selected, Services,
    /// and Settings with its Advanced grid.
    /// </summary>
    public List<string> Screenshots(string dir)
    {
        var files = new List<string>();
        void Shot(Control c, string name) => files.Add(SmokeShots.Save(c, dir, name, _engine.Paths.Guard));
        SetView(true);
        _tabs.SelectedIndex = 0;
        _causes.SelectedItems.Clear();
        RefreshNow(force: true);
        // The background ranking spaces its work out (RankPlan); for the screenshots, rank every listed tag now.
        _rankings.RankNow(tag => TagViewBuilder.Build(_analysis, _engine.Registry, tag), _leadTag is { } lead ? _rankTags.Prepend(lead) : _rankTags);
        _statusSamples = -1;
        SetView(false);
        Shot(this, "1-simple-view.png");
        SetView(true);
        if (_causes.Items.Count > 0) { _causes.Items[0].Selected = true; ShowCauseDetails(); }
        Shot(this, "2-advanced-view.png");
        _tabs.SelectedIndex = 1;
        RefreshNow(force: true);
        // The services list loads in the background: wait for it (at most 20 s) so the shot shows it.
        for (int i = 0; i < 200 && _serviceOptions.Count == 0; i++) { Application.DoEvents(); Thread.Sleep(100); }
        Shot(this, "3-services.png");
        _tabs.SelectedIndex = 0;
        using (var settings = NewSettings())
        {
            settings.StartPosition = FormStartPosition.CenterParent;
            settings.Show(this);
            Shot(settings, "4-settings.png");
            settings.Hide();
            var advanced = settings.Advanced;
            advanced.Show(this);
            Shot(advanced, "5-settings-advanced.png");
            advanced.Hide();
        }
        return files;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _tips.Dispose();
        }
        base.Dispose(disposing);
    }
}
