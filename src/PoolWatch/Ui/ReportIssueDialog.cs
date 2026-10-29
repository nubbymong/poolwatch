using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using PoolWatch.Analysis;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Report;
using PoolWatch.Core.TokenGuard;
using PoolWatch.Core.Util;
using PoolWatch.Engine;

namespace PoolWatch.Ui;

/// <summary>
/// Report an issue: the user's own words and, while its boxes stay ticked, what PoolWatch saw and an incident's summary,
/// all redacted, in a preview that is exactly what goes into the issue. Open in browser copies that text and hands
/// GitHub's issue form, with its title, version and Windows filled in, to the user's browser, where the user pastes the
/// text; PoolWatch itself sends nothing, and writes nothing except on Save as file. The facts are read once, from what
/// PoolWatch already holds. Created when opened, disposed when closed.
/// </summary>
internal sealed class ReportIssueDialog : Form
{
    /// <summary>A report.json larger than this is not read for the incident's summary.</summary>
    private const int MaxReportJsonBytes = 16 << 20;

    private const string DefaultTitle = "Problem report from PoolWatch";

    private readonly MonitorEngine _engine;
    private readonly UiActions _actions;
    private readonly RedactionInfo _redaction = MainForm.CurrentRedaction();
    private readonly IssueFacts _facts;
    private readonly string? _incident;
    private readonly TextBox _what = new() { Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, MaxLength = 4000 };
    private readonly CheckBox _seen = new() { Text = "Include what PoolWatch saw (version, Windows, memory, kernel pool now)", AutoSize = true, Checked = true };
    private readonly CheckBox _withIncident = new() { Text = "Include the incident's summary", AutoSize = true, Checked = true };
    private readonly TextBox _preview = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly Label _status = new() { AutoSize = true, ForeColor = SystemColors.GrayText, UseMnemonic = false, Visible = false };
    private readonly System.Windows.Forms.Timer _typing = new() { Interval = 300 };
    private readonly Font _bold; // the labels never dispose the font they are given: the window does, after them

    // The redacted parts the preview shows and the form's short fields, rebuilt 300 ms after the last key and at once on a
    // box or a button.
    private string _title = DefaultTitle, _whatText = "", _details = "", _fullText = "", _version = "", _windows = "";

    /// <param name="incidentDir">An incident folder whose summary is offered (its report.json), or null.</param>
    public ReportIssueDialog(MonitorEngine engine, AnalysisPipeline analysis, UiActions actions, string? incidentDir)
    {
        _engine = engine;
        _actions = actions;
        _incident = Digest(incidentDir);
        _facts = Facts(engine, analysis);
        Text = "Report an issue";
        Icon = TrayIcons.App;
        Font = new Font("Segoe UI", 9f);
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        int width = S(600);
        var form = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Padding = Px(16, 12, 16, 12), Location = Point.Empty };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width));
        void Row(Control c, int bottom = 4)
        {
            c.Margin = new Padding(0, 0, 0, S(bottom));
            if (c is Label { AutoSize: true } l) l.MaximumSize = new Size(width, 0);
            form.Controls.Add(c);
        }
        var bold = _bold = new Font(Font, FontStyle.Bold);

        Row(new Label { Text = "What happened?", AutoSize = true, Font = bold });
        _what.Size = new Size(width, S(84));
        Row(_what, bottom: 6);
        Row(_seen, bottom: 2);
        _withIncident.Visible = _incident is not null;
        Row(_withIncident, bottom: 2);
        Row(new Label { Text = "Preview: exactly what goes into the issue, with names, paths and secrets replaced", AutoSize = true, Font = bold, Margin = Padding.Empty }, bottom: 4);
        _preview.Size = new Size(width, S(220));
        Row(_preview, bottom: 6);
        Row(new Label { Text = "Open in browser copies this text and opens GitHub's issue form with its title, version and Windows filled in: " +
                               "paste the text into \"What happened\". Nothing is public until you press Submit there.", AutoSize = true, UseMnemonic = false });
        Row(_status);
        var security = new LinkLabel { Text = "Security problem? Report it privately", AutoSize = true, UseMnemonic = false };
        security.LinkArea = new LinkArea(security.Text.IndexOf("Report", StringComparison.Ordinal), "Report it privately".Length);
        security.LinkClicked += (_, _) => actions.Guarded(this, "Open web page", () => UiActions.OpenWebPage(IssueReport.AdvisoryUrl));
        Row(security, bottom: 8);

        Button Action(string text, Action action, int left)
        {
            var b = new Button { Text = text, AutoSize = true, Margin = new Padding(S(left), 0, 0, 0) };
            b.Click += (_, _) => actions.Guarded(this, text, action);
            return b;
        }
        var close = new Button { Text = "Close", AutoSize = true, Margin = Padding.Empty };
        close.Click += (_, _) => Close();
        CancelButton = close;
        var buttons = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 5, RowCount = 1, Margin = Padding.Empty, Dock = DockStyle.Fill };
        for (int i = 0; i < 3; i++) buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttons.Controls.Add(Action("Open in browser", OpenInBrowser, 0), 0, 0);
        buttons.Controls.Add(Action("Copy all", CopyAll, 6), 1, 0);
        buttons.Controls.Add(Action("Save as file", SaveAsFile, 6), 2, 0);
        buttons.Controls.Add(close, 4, 0);
        form.Controls.Add(buttons);
        Controls.Add(form);

        _typing.Tick += (_, _) => Rebuild();
        _what.TextChanged += (_, _) => { _typing.Stop(); _typing.Start(); };
        _seen.CheckedChanged += (_, _) => Rebuild();
        _withIncident.CheckedChanged += (_, _) => Rebuild();
        Rebuild();
    }

    private int S(int logical) => LogicalToDeviceUnits(logical);
    private Padding Px(int l, int t, int r, int b) => new(S(l), S(t), S(r), S(b));

    // ------------------------------------------------------------------ the text

    /// <summary>Redacts the parts and shows them: the preview is built from exactly what the buttons send.</summary>
    private void Rebuild()
    {
        _typing.Stop();
        _whatText = IssueReport.Redact(_what.Text, _redaction);
        string? incident = _withIncident.Checked ? _incident : null;
        _details = IssueReport.Redact(_seen.Checked ? IssueReport.Details(_facts with { Incident = incident }) : incident ?? "", _redaction);
        _title = Title(_whatText);
        _fullText = IssueReport.FullText(_title, _whatText, _details);
        // The form's version and Windows fields are what PoolWatch saw: empty when that box is off, redacted like the rest.
        _version = _seen.Checked ? IssueReport.Redact(_facts.Version, _redaction) : "";
        _windows = _seen.Checked ? IssueReport.Redact(_facts.Windows, _redaction) : "";
        string shown = _fullText.ReplaceLineEndings("\r\n");
        if (_preview.Text != shown) _preview.Text = shown;
    }

    /// <summary>
    /// The first line of the user's words, at most 80 characters, cut at a space (never inside a surrogate pair); the
    /// default title without words.
    /// </summary>
    private static string Title(string what)
    {
        const int max = 80;
        string line = what.ReplaceLineEndings("\n").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        if (line.Length <= max) return line.Length > 0 ? line : DefaultTitle;
        int cut = line.LastIndexOf(' ', max - 1);
        if (cut <= 20) cut = char.IsHighSurrogate(line[max - 1]) ? max - 1 : max;
        return line[..cut].TrimEnd() + "...";
    }

    private string Address() => IssueReport.Url(_title, _version, _windows);

    private void OpenInBrowser()
    {
        Rebuild();
        _actions.Copy(_fullText.ReplaceLineEndings("\r\n"));
        UiActions.OpenWebPage(Address());
        ShowStatus("Copied. Paste it into \"What happened\" in the form your browser opened.");
    }

    private void CopyAll()
    {
        Rebuild();
        _actions.Copy(_fullText.ReplaceLineEndings("\r\n"));
        ShowStatus("Copied. You can paste it into the issue form or anywhere else.");
    }

    /// <summary>Writes the preview's text to exports\issue-&lt;time&gt;.txt (through the write policy) and shows it in Explorer.</summary>
    private void SaveAsFile()
    {
        Rebuild();
        var paths = _engine.Paths;
        string file = paths.Guard.CheckReal(Path.Combine(paths.EnsureDir(paths.ExportsDir), $"issue-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.txt"));
        File.WriteAllText(file, _fullText.ReplaceLineEndings("\r\n"), new UTF8Encoding(false));
        ShowStatus("Saved in PoolWatch's exports folder.");
        UiActions.ShowFile(file);
    }

    private void ShowStatus(string text)
    {
        _status.Text = text;
        _status.Visible = true;
    }

    // ------------------------------------------------------------------ the facts

    /// <summary>The facts from what PoolWatch already holds: nothing is collected or scanned for the report.</summary>
    private static IssueFacts Facts(MonitorEngine engine, AnalysisPipeline analysis)
    {
        var system = analysis.Host.GetSystemInfo();
        string kernel = KernelLine(UiData.Totals(analysis));
        var tags = UiData.Tags(analysis, growingOnly: false, max: int.MaxValue).OrderByDescending(t => t.Bytes).Take(8)
            .Select(t => $"{PlainWords.Tag(t.Tag, analysis.Knowledge.Lookup(t.Tag))}: {Fmt.Bytes(t.Bytes)}").ToList();
        // OSArchitecture is the machine's own (ARM64 on Windows on Arm, also for this x64 build under emulation, since .NET 7).
        string architecture = Arch(RuntimeInformation.OSArchitecture);
        if (RuntimeInformation.ProcessArchitecture != RuntimeInformation.OSArchitecture)
            architecture += $" (the {Arch(RuntimeInformation.ProcessArchitecture)} build under emulation)";
        return new IssueFacts(UiActions.AppVersion, InstallKind(), IssueReport.WindowsLine(system.OsDescription, system.OsBuild, ServicesController.InsiderEnrolled()),
            architecture, system.RamBytes, engine.Volumes.Count(v => v.IsDevDrive == true), kernel, AutoClear(engine, analysis), TokenGuard(engine, analysis),
            engine.Meter.OwnCostLine(), tags, null);
    }

    private static string Arch(Architecture a) => a switch { Architecture.X64 => "x64", Architecture.Arm64 => "ARM64", _ => a.ToString() };

    private static string Change(long bytes) => bytes > 0 ? "up " + Fmt.Bytes(bytes) : bytes < 0 ? "down " + Fmt.Bytes(-bytes) : "unchanged";

    /// <summary>
    /// The kernel pool now and its change over the last hour, as the status line measures it (a clear's drop added back, so
    /// growth before it still counts), with what PoolWatch's own cache clears of that hour freed said apart, as their result:
    /// "12.0 GB (paged and non-paged pool), up 150 MB in the last hour; PoolWatch's own cache clear at 03:56 freed 1.3 GB, not
    /// counted in that change". While a clear is still being measured no change is told.
    /// </summary>
    internal static string KernelLine(TotalsNow totals) => totals.Total is not { } total ? "not measured yet"
        : Fmt.Bytes(total) + " (paged and non-paged pool), " + (totals.ChangeHour is not { } h ? "watched for less than an hour"
            : totals.MeasuringUtc is { } m ? $"change in the last hour not known yet: PoolWatch's own cache clear at {m.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)} is being measured"
            : Change(h) + " in the last hour" + (totals.ClearedHour is { Bytes: > 0 } c ? $"; {Fmt.OwnClearsFreed(c.Bytes, c.OneAtUtc, seconds: false)}, not counted in that change" : ""));

    /// <summary>
    /// How this copy was installed, in the issue form's own words: the installer leaves unins000.exe next to the exe; a
    /// release is one PoolWatch.exe, so a PoolWatch.dll beside it is a build from source; a self-contained build loads the
    /// runtime from its own folder.
    /// </summary>
    private static string InstallKind()
    {
        string dir = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(dir, "unins000.exe"))) return "installer (setup.exe)";
        if (File.Exists(Path.Combine(dir, "PoolWatch.dll"))) return "built from source";
        return RuntimeEnvironment.GetRuntimeDirectory().StartsWith(dir, StringComparison.OrdinalIgnoreCase)
            ? "self-contained (win-x64-selfcontained.zip)"
            : "framework-dependent (win-x64.zip)";
    }

    /// <summary>"on, every 1 h or at 8 GB of kernel pool; last clear at 09:12 (standby list, automatic) freed 4.7 GB".</summary>
    private static string AutoClear(MonitorEngine engine, AnalysisPipeline analysis)
    {
        var s = engine.Settings;
        var rules = new List<string>();
        if (s.AutoClearEveryHours > 0) rules.Add($"every {s.AutoClearEveryHours.ToString("0.#", CultureInfo.InvariantCulture)} h");
        if (s.AutoClearPoolGb > 0) rules.Add($"at {s.AutoClearPoolGb.ToString("0.#", CultureInfo.InvariantCulture)} GB of kernel pool");
        string setting = s.AutoClearEnabled ? "on, " + string.Join(" or ", rules) : "off";
        var cache = analysis.Diagnostics.Cache;
        if (cache.HelperStatus?.LegacyTaskPresent == true) setting += "; your own clear task (RAMMap) is installed";
        string last = cache.History().LastOrDefault(r => r.Done) switch
        {
            null => "no clear yet",
            { Error: { } error } r => $"last clear at {Clock(r.TimeUtc)} failed: {error}",
            var r => $"last clear at {Clock(r.TimeUtc)} ({r.ModeText}, {r.TriggerText}) freed {Fmt.Bytes(Math.Max(0, r.Freed))}",
        };
        return $"{setting}; {last}";
    }

    /// <summary>"off; last test on build 26200.8655: leak (2026-10-27)". The saved result is the one this run's guard read.</summary>
    private static string TokenGuard(MonitorEngine engine, AnalysisPipeline analysis)
    {
        string verdict = analysis.Diagnostics.Guard.State is { } state
            ? $"last test on build {state.Build}: {GuardState.ResultText(state.Result)} ({state.TestedUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)})"
            : "no saved test result";
        return $"{(engine.Settings.TokenGuardEnabled ? "on" : "off")}; {verdict}";
    }

    private static string Clock(DateTime utc) => utc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>The incident's summary from its report.json, or null (no incident, a file too large, unreadable or not a report).</summary>
    private static string? Digest(string? incidentDir)
    {
        if (incidentDir is null) return null;
        try
        {
            var report = new FileInfo(Path.Combine(incidentDir, "report.json"));
            return report.Exists && report.Length <= MaxReportJsonBytes ? IssueReport.IncidentDigest(File.ReadAllText(report.FullName)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    // ------------------------------------------------------------------ CI

    /// <summary>
    /// CI: the preview, the address's length, and anything clipped. With "What happened?" empty it first types a sample
    /// that names this user's home folder and this PC, so the preview shows redaction working.
    /// </summary>
    internal object SmokeStats()
    {
        if (_what.TextLength == 0)
            _what.Text = "The kernel pool kept growing while I worked, and typing started to lag.\r\n" +
                         $"My project is in {Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}\\source\\app on {Environment.MachineName}.";
        Rebuild();
        var problems = new List<string>();
        MainForm.ClippedControls(this, problems);
        return new { preview = _fullText, title = _title, urlLength = Address().Length, layoutProblems = problems };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _typing.Dispose();
        base.Dispose(disposing);
        if (disposing) _bold.Dispose();
    }
}
