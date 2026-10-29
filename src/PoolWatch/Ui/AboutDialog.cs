using System.Globalization;
using PoolWatch.Analysis;
using PoolWatch.Core;
using PoolWatch.Core.Detection;
using PoolWatch.Engine;

namespace PoolWatch.Ui;

/// <summary>What About says about this PoolWatch: its version, what it monitors and what that costs, and its data folder.</summary>
internal sealed record AboutFacts(string Version, string Monitoring, string Overhead, string DataRoot);

/// <summary>
/// About PoolWatch, in three columns: the version, what it monitors and what that costs, and the data folder; what
/// PoolWatch changes and what goes over the network; and the credits and licences (<see cref="Credits.All"/>), each name
/// a link to its page, in a list that scrolls when it is longer than the other two columns. The form sizes itself to its
/// content, so nothing is cut off at any scaling, and fits a 1024 x 600 screen whole (it was 857 px tall in one column);
/// on a shorter one it scrolls vertically. Created when opened, disposed when closed.
/// </summary>
internal sealed class AboutDialog : Form
{
    /// <summary>Each column's width (logical pixels): the three side by side fit a 1024-wide screen.</summary>
    public const int ColumnWidth = 300;

    /// <summary>Start-up (JIT, first scans, tag scan) is not steady state: overhead is judged only after this long.</summary>
    private const int SteadySeconds = 300;

    /// <summary>What PoolWatch changes (SECURITY.md has every action).</summary>
    private const string Changes =
        "PoolWatch watches and reads. It changes your PC only where you choose to: clearing the cache (now or automatically), " +
        "background services, scheduled tasks, startup apps, ending a program, starting at sign-in, replacing your own clear task, " +
        "a desktop shortcut for Codex, and, when you turn it on, the token-leak guard's focus setting while you are signed in. " +
        "On its own it only secures its data folder and removes an older PoolWatch's unused clear-cache tasks. Active changes " +
        "lists every lasting change, with a way to put it back; SECURITY.md in the source repository lists every action.";

    /// <summary>What goes over the network: the update check, the one connection PoolWatch makes itself, and pages the browser opens.</summary>
    internal const string Network =
        "PoolWatch sends nothing about your PC and has no telemetry. The only connection it makes itself is the daily check for a " +
        "new version on GitHub, which you can turn off in Set up PoolWatch or in Settings; GitHub sees your IP address, as it does for any " +
        "web page. Report an issue and the links below open a page in your " +
        "browser; Report an issue copies its text for you to paste into GitHub's form, and nothing is public until you submit it.";

    private readonly ToolTip _tips = new();
    private readonly List<LinkLabel> _credits = [];
    /// <summary>The credits' list: it scrolls when it is longer than the other two columns.</summary>
    private readonly Panel _creditsList;
    private readonly Font _bold, _title; // the labels never dispose the fonts they are given: the window does, after them

    public AboutDialog(MonitorEngine engine, AnalysisPipeline analysis, UiActions actions) : this(FactsOf(engine, analysis), actions) { }

    /// <param name="actions">What the links and buttons do; null (tests) leaves them inert.</param>
    internal AboutDialog(AboutFacts facts, UiActions? actions)
    {
        Text = "About PoolWatch";
        Icon = TrayIcons.App;
        Font = new Font("Segoe UI", 9f);
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        int width = S(ColumnWidth), gutter = S(24);
        var bold = _bold = new Font(Font, FontStyle.Bold);
        _title = new Font(Font.FontFamily, 12f, FontStyle.Bold);
        TableLayoutPanel Column()
        {
            var c = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
            c.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width));
            return c;
        }
        void Row(TableLayoutPanel column, Control c, int bottom = 4)
        {
            c.Margin = new Padding(0, 0, 0, S(bottom));
            if (c is Label { AutoSize: true } l) l.MaximumSize = new Size(width, 0);
            column.Controls.Add(c);
        }
        Label Para(string text) => new() { Text = text, AutoSize = true, UseMnemonic = false };
        Label Heading(string text) => new() { Text = text, AutoSize = true, Font = bold };
        void Open(string what, Action a) { if (actions is not null) actions.Guarded(this, what, a); }

        // The first column: PoolWatch itself.
        var left = Column();
        Row(left, new Label { Text = $"PoolWatch {facts.Version}", AutoSize = true, Font = _title });
        Row(left, Para("Watches the Windows kernel pool, finds what makes it grow, and can clear the cache."), bottom: 2);
        // Where PoolWatch lives: its source, releases and issues.
        string home = Credits.RepoUrl["https://".Length..];
        var repo = new LinkLabel { Text = $"Source, releases and issues: {home}", AutoSize = true, UseMnemonic = false };
        repo.LinkArea = new LinkArea(repo.Text.Length - home.Length, home.Length);
        repo.LinkClicked += (_, _) => Open("Open web page", () => UiActions.OpenWebPage(Credits.RepoUrl));
        _tips.SetToolTip(repo, Credits.RepoUrl);
        Row(left, repo, bottom: 10);

        Row(left, Heading("Monitoring"));
        Row(left, Para(facts.Monitoring));
        Row(left, Para(facts.Overhead), bottom: 10);

        Row(left, Heading("Data folder"));
        var open = new Button { Text = "Open", AutoSize = true, Margin = new Padding(S(8), 0, 0, 0) };
        open.Click += (_, _) => { if (actions is not null) UiActions.OpenFolder(facts.DataRoot); };
        var folder = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false };
        folder.Controls.Add(new Label { Text = facts.DataRoot, AutoSize = true, UseMnemonic = false, Margin = new Padding(0, S(6), 0, 0), MaximumSize = new Size(width - S(80), 0) });
        folder.Controls.Add(open);
        Row(left, folder, bottom: 0);

        // The second column: what PoolWatch changes, and what goes over the network.
        var middle = Column();
        Row(middle, Heading("What PoolWatch changes"));
        Row(middle, Para(Changes), bottom: 6);
        Row(middle, Para(Network), bottom: 0);

        // The third column: the credits and licences, in a list as tall as the other two columns (it scrolls when longer).
        var right = Column();
        var heading = Heading("Credits and licences");
        Row(right, heading);
        int bar = SystemInformation.GetVerticalScrollBarWidthForDpi(DeviceDpi);
        var list = Column();
        list.ColumnStyles[0] = new ColumnStyle(SizeType.Absolute, width - bar - S(4));
        foreach (var c in Credits.All)
        {
            var link = new LinkLabel { Text = $"{c.Name} ({c.Licence}). {c.What}", AutoSize = true, UseMnemonic = false, LinkArea = new LinkArea(0, c.Name.Length) };
            link.LinkClicked += (_, _) => Open("Open web page", () => UiActions.OpenWebPage(c.Url));
            _tips.SetToolTip(link, c.Url);
            link.MaximumSize = new Size(width - bar - S(4), 0);
            link.Margin = new Padding(0, 0, 0, S(5));
            _credits.Add(link);
            list.Controls.Add(link);
        }
        int tallest = Math.Max(left.GetPreferredSize(Size.Empty).Height, middle.GetPreferredSize(Size.Empty).Height);
        int listHeight = Math.Max(S(200), tallest - heading.GetPreferredSize(Size.Empty).Height - S(4));
        _creditsList = new Panel { AutoScroll = true, Size = new Size(width, listHeight), Margin = Padding.Empty };
        _creditsList.Controls.Add(list);
        right.Controls.Add(_creditsList);

        var columns = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        for (int i = 0; i < 3; i++) columns.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        left.Margin = middle.Margin = new Padding(0, 0, gutter, 0);
        columns.Controls.Add(left, 0, 0);
        columns.Controls.Add(middle, 1, 0);
        columns.Controls.Add(right, 2, 0);

        var form = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Padding = Px(16, 12, 16, 12), Location = Point.Empty };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 3 * width + 2 * gutter));
        form.Controls.Add(columns);

        var licences = new Button { Text = "Licences", AutoSize = true, Margin = Padding.Empty };
        licences.Click += (_, _) => Open("Licences", ShowLicences);
        // The report window belongs to whoever opened About, so closing About leaves it open.
        var report = new Button { Text = "Report an issue...", AutoSize = true, Margin = new Padding(S(6), 0, 0, 0) };
        report.Click += (_, _) => { if (actions is not null) actions.Guarded(this, "Report an issue", () => actions.ReportIssue(Owner)); };
        var close = new Button { Text = "Close", AutoSize = true, Margin = Padding.Empty };
        close.Click += (_, _) => Close();
        CancelButton = close;
        var buttons = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 4, RowCount = 1, Margin = new Padding(0, S(10), 0, 0), Dock = DockStyle.Fill };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttons.Controls.Add(licences, 0, 0);
        buttons.Controls.Add(report, 1, 0);
        buttons.Controls.Add(close, 3, 0);
        form.Controls.Add(buttons);
        Controls.Add(form);
    }

    private int S(int logical) => LogicalToDeviceUnits(logical);
    private Padding Px(int l, int t, int r, int b) => new(S(l), S(t), S(r), S(b));

    /// <summary>About's facts from the running app.</summary>
    private static AboutFacts FactsOf(MonitorEngine engine, AnalysisPipeline analysis) => new(UiActions.AppVersion,
        $"{engine.SampleCount:N0} samples; process events (ETW) {(engine.EtwRunning ? engine.EtwMode : "not running")}; " +
        $"pooltag.txt {(analysis.Knowledge.PoolTagTxtLoaded ? "loaded" : "not found (built-in tag table and driver scan)")}.",
        Overhead(engine), engine.Paths.DataRoot);

    /// <summary>The overhead since start-up against its budget, then the last 10 minutes.</summary>
    private static string Overhead(MonitorEngine engine)
    {
        var o = engine.Meter.Report();
        string over = o.SteadyStateSeconds >= SteadySeconds
            ? $"steady state over {Fmt.Duration(TimeSpan.FromSeconds(o.SteadyStateSeconds))}"
            : "still warming up, so the numbers include start-up";
        return $"Overhead, {over}: CPU {o.CpuPercentOfOneCore:0.00}% of one core (budget {SelfMeter.CpuBudgetPercent.ToString(CultureInfo.InvariantCulture)}%), " +
               $"peak working set {Fmt.Bytes(o.PeakWorkingSetBytes)} (budget {SelfMeter.RamBudgetBytes >> 20} MB), disk writes " +
               $"{Fmt.Bytes(o.DiskWriteBytesPerHour)}/h (budget {SelfMeter.DiskBudgetBytesPerHour >> 20} MB/h).\n{engine.Meter.OwnCostLine()}";
    }

    /// <summary>THIRD-PARTY-NOTICES.md next to the exe (the installer puts it there), shown in Explorer; else its page on GitHub.</summary>
    private static void ShowLicences()
    {
        string notices = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.md");
        if (File.Exists(notices)) UiActions.ShowFile(notices);
        else UiActions.OpenWebPage(Credits.NoticesUrl);
    }

    /// <summary>
    /// On a screen too short for the whole window (high scaling on a small display) it keeps its width and scrolls
    /// vertically instead of running off the screen (<see cref="ScreenFit"/>).
    /// </summary>
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (ScreenFit.ToWorkingArea(this)) CenterToScreen();
    }

    /// <summary>CI: the credits as shown (each link's name) and anything clipped or outside its parent.</summary>
    internal object SmokeStats()
    {
        var problems = new List<string>();
        MainForm.ClippedControls(this, problems);
        return new { credits = _credits.Select(l => l.Text[..l.LinkArea.Length]).ToList(), layoutProblems = problems, size = new { width = Width, height = Height, dpi = DeviceDpi } };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tips.Dispose();
        base.Dispose(disposing);
        if (disposing)
        {
            _bold.Dispose();
            _title.Dispose();
        }
    }
}
