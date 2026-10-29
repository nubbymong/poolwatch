using System.Diagnostics;
using PoolWatch.Analysis;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Util;
using PoolWatch.Engine;
using PoolWatch.Engine.Changes;

namespace PoolWatch.Ui;

/// <summary>
/// The actions the main window and the tray menu share (DESIGN.md section 11): Clear cache, the exports, the Since boot
/// report, copying a summary, the clear-task removals and About. Each takes the window to parent its dialogs on (null from
/// the tray). Nothing here changes what an action does; it only gives the tray and the window one copy of it.
/// </summary>
internal sealed class UiActions
{
    private readonly MonitorEngine _engine;
    private readonly AnalysisPipeline _analysis;
    private readonly SynchronizationContext _ui;

    /// <summary>Start-up (JIT, first scans, tag scan) is not steady state: overhead is shown only after this long.</summary>
    private const int SteadySeconds = 300;

    public UiActions(MonitorEngine engine, AnalysisPipeline analysis, SynchronizationContext ui)
    {
        _engine = engine;
        _analysis = analysis;
        _ui = ui;
    }

    /// <summary>Runs an action; a failure is logged and shown instead of reaching WinForms' unhandled-exception path.</summary>
    public void Guarded(IWin32Window? owner, string what, Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            _engine.Log.Info($"{what} failed: {ex}");
            MessageBox.Show(owner, $"{what} failed: {ex.Message}", "PoolWatch");
        }
    }

    // ---------------------------------------------------------------- Clear cache

    public const string NotElevated = "Clear cache needs PoolWatch to run as administrator (start PoolWatch normally: it asks for that).";

    public void ClearCacheNow(IWin32Window? owner)
    {
        var c = _analysis.Diagnostics.Cache;
        // S5 (review round 2): the purge is in-process; PoolWatch runs as administrator, so there is nothing to set up.
        if (!CacheHelper.IsElevated)
        {
            MessageBox.Show(owner, NotElevated, "PoolWatch");
            return;
        }
        MessageBox.Show(owner, c.Request(Core.Cache.ClearMode.Standby, Core.Cache.ClearTrigger.Manual)
            ? "Clearing the standby cache. PoolWatch measures the pool again in 2 minutes; the result is then a row in Pool causes and in this button's tooltip."
            : "A clear is already running (or no sample yet).", "PoolWatch");
    }

    /// <summary>The Clear cache button's tooltip: what it does, then the last clear's result and anything blocking the automatic clear.</summary>
    public string ClearCacheTip()
    {
        const string what = "Empty the Windows standby list (cached file pages); the per-file ReFS / filter-manager pool they keep alive is freed with them. " +
                            "Measured: pool before and 2 minutes after.";
        string? status = null;
        lock (_analysis.History.SyncRoot)
            if (_analysis.History.Count > 0) status = _analysis.Diagnostics.Cache.StatusLine(_analysis.History.Totals[^1]);
        return status is { Length: > 0 } s ? what + "\n\n" + s : what;
    }

    /// <summary>Your own clear task (RAMMap in a scheduled task) is installed: the tray offers to remove it.</summary>
    public bool LegacyTaskPresent => _analysis.Diagnostics.Cache.HelperStatus?.LegacyTaskPresent == true;

    /// <summary>An older PoolWatch's two SYSTEM clear-cache tasks are still installed: the tray offers to remove them.</summary>
    public bool OldTasksPresent => _analysis.Diagnostics.Cache.HelperStatus?.OldTasksPresent == true;

    public void RemoveLegacyTask(IWin32Window? owner)
    {
        if (!CacheHelper.IsElevated) { MessageBox.Show(owner, NotElevated, "PoolWatch"); return; }
        int code = LegacyInProcess(owner, "the tray menu", out string? note);
        _analysis.Diagnostics.Cache.RefreshStatus();
        if (code == 0) MessageBox.Show(owner, "Removed (RAMMap itself is left in place).", "PoolWatch");
        else if (note is not null) MessageBox.Show(owner, note, "PoolWatch");
    }

    /// <summary>
    /// The same step as <c>--remove-legacy-clear-task</c>, with its exit codes. 0: removed, or no clear task of your own is
    /// there; 4: one is still there (you kept it, or the note says why). Only 0 lets the automatic clear go on. Every task
    /// is recorded, with its definition, before it is removed, so Active changes can put it back.
    /// </summary>
    /// <param name="via">Where the removal was asked for, in plain words ("the tray menu", "Settings").</param>
    /// <param name="note">Why your tasks were left in place when it was not your No, for the caller to show; null otherwise.</param>
    public int LegacyInProcess(IWin32Window? owner, string via, out string? note)
    {
        (var outcome, _, note) = CacheHelper.ResolveLegacy(t =>
            MessageBox.Show(owner, CacheHelper.LegacyQuestion(t), "PoolWatch: your own clear task", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes,
            ChangeRecords.Open(_engine.Paths, _engine.Log), via);
        return outcome == CacheHelper.LegacyOutcome.Kept ? 4 : 0;
    }

    /// <summary><see cref="LegacyInProcess(IWin32Window?, string, out string?)"/> from Settings, for a caller that shows its own words.</summary>
    public int LegacyInProcess(IWin32Window? owner) => LegacyInProcess(owner, "Settings", out _);

    /// <summary>
    /// S5 (review round 2): removes the two \PoolWatch\ SYSTEM tasks and the helper files an older PoolWatch's Clear cache
    /// setup installed (offered only while they are there), after a yes. Recorded on the timeline.
    /// </summary>
    public void RemoveOldClearTasks(IWin32Window? owner)
    {
        if (!CacheHelper.IsElevated) { MessageBox.Show(owner, NotElevated, "PoolWatch"); return; }
        var old = _analysis.Diagnostics.Cache.HelperStatus?.OldTasks ?? [];
        string list = old.Count > 0 ? string.Join("\n", old.Select(n => $@"{CacheHelper.Folder}\{n}")) : "(none found just now)";
        if (MessageBox.Show(owner, "An older PoolWatch installed these SYSTEM tasks for Clear cache. PoolWatch clears the cache itself now and no longer " +
                $"uses them:\n\n{list}\n\nRemove them, and the helper files in {CacheHelper.OldHelperDir}? (Your own clear task, if any, is not touched.)",
                "PoolWatch: old clear-cache tasks", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        var removed = CacheHelper.RemoveOldTasks();
        _analysis.Diagnostics.Cache.RefreshStatus();
        if (removed.Count > 0) _analysis.Diagnostics.RecordOwnerChange("Removed the old Clear cache tasks and helper: " + string.Join("; ", removed), null);
        MessageBox.Show(owner, removed.Count == 0 ? "Nothing was found to remove." : "Removed:\n" + string.Join("\n", removed), "PoolWatch");
    }

    // ---------------------------------------------------------------- reports and exports

    /// <summary>The newest incident's folder (in memory or still on disk), or null.</summary>
    public string? LatestIncidentDir() => UiData.Incidents(_analysis, _engine.Paths.IncidentsDir).FirstOrDefault()?.Dir;

    /// <summary>Opens <paramref name="dir"/> (an incident or finding report folder) or, without one, the incidents folder.</summary>
    public void OpenIncidentFolder(string? dir = null) =>
        OpenFolder(dir is not null && Directory.Exists(dir) ? dir : _engine.Paths.EnsureDir(_engine.Paths.IncidentsDir));

    /// <summary>
    /// Zips <paramref name="dir"/> (the selected report folder; null: the newest incident), redacted by default, with the guide
    /// for AI assistants at its root.
    /// </summary>
    public void ExportReport(IWin32Window? owner, string? dir)
    {
        dir ??= LatestIncidentDir();
        if (dir is null || !Directory.Exists(dir)) { MessageBox.Show(owner, "No incident to export yet.", "PoolWatch"); return; }
        if (_analysis.Captures.Busy) { MessageBox.Show(owner, "A confirmation trace is being recorded. Export when it has finished.", "PoolWatch"); return; }
        // M1: redaction is the default (Yes, or Enter); the incident folder itself is never redacted.
        var answer = MessageBox.Show(owner, $"Export {Path.GetFileName(dir)}.\n\nRedact the user name, home and TEMP paths, machine name, SID and values that look like secrets " +
            "(passwords, tokens, API keys on a command line)? Recommended before you share it.\n(The .etl trace is left out of a redacted export. " +
            "Redaction is best-effort: read the export before you post it.)",
            "Export report", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1);
        if (answer == DialogResult.Cancel) return;
        var zip = Exporter.Export(dir, _engine.Paths.ExportsDir, answer == DialogResult.Yes ? MainForm.CurrentRedaction() : null, _engine.Paths.Guard.CheckReal,
            new Dictionary<string, string> { [AgentsGuide.FileName] = AgentsGuide.Text });
        OpenFolder(Path.GetDirectoryName(zip)!);
    }

    public void ExportVendorBundle(IWin32Window? owner)
    {
        // Redaction is the default: Yes (or Enter) redacts; No has to be chosen.
        var answer = MessageBox.Show(owner, "Redact the user name, home paths, machine name, SID and values that look like secrets? (Recommended before sending it to anyone.)",
            "Export vendor bundle", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1);
        if (answer == DialogResult.Cancel) return;
        var zip = BundleBuilder.Export(_engine, _analysis, answer == DialogResult.Yes ? MainForm.CurrentRedaction() : null);
        OpenFolder(Path.GetDirectoryName(zip)!);
    }

    /// <summary>What has grown since boot, and what drove it, hour by hour: written off the UI thread, then shown in its folder.</summary>
    public void SinceBoot(IWin32Window? owner)
    {
        // Builds the 25-hour report under the history lock: keep it off the UI thread.
        string dir = _engine.Paths.EnsureDir(Path.Combine(_engine.Paths.DataRoot, "reports"));
        Task.Run(() => _analysis.WriteSinceBoot(dir)).ContinueWith(t => _ui.Post(_ =>
        {
            if (owner is Control { IsDisposed: true }) owner = null;
            if (t.IsFaulted) MessageBox.Show(owner, "Since-boot report failed: " + t.Exception!.GetBaseException().Message, "PoolWatch");
            else ShowFile(t.Result); // t.Result is the report file; show it in its folder, never open it through an association
        }, null));
    }

    // ---------------------------------------------------------------- copying

    /// <summary>Copies text to the clipboard, through the copy redaction setting (<c>RedactCopies</c>).</summary>
    public void Copy(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        text = CopyText.Apply(text, _engine.Settings.RedactCopies ? MainForm.CurrentRedaction() : null);
        try { Clipboard.SetDataObject(text, copy: true, retryTimes: 5, retryDelay: 100); }
        catch (Exception ex) { _engine.Log.Info("clipboard: " + ex.Message); } // another app holds the clipboard
    }

    public static string IncidentSummary(string dir) => CopyText.IncidentSummary(File.ReadAllText(Path.Combine(dir, "report.json")));

    /// <summary>Tray "Copy latest incident summary": the newest incident's summary as plain text.</summary>
    public void CopyLatestIncidentSummary(IWin32Window? owner)
    {
        if (LatestIncidentDir() is { } dir && File.Exists(Path.Combine(dir, "report.json"))) { Copy(IncidentSummary(dir)); return; }
        MessageBox.Show(owner, "There is no incident yet.", "PoolWatch");
    }

    // ---------------------------------------------------------------- About

    public void About(IWin32Window? owner)
    {
        var o = _engine.Meter.Report();
        string monitoring = $"{_engine.SampleCount:N0} samples; process events (ETW) {(_engine.EtwRunning ? _engine.EtwMode : "not running")}; " +
                            $"pooltag.txt {(_analysis.Knowledge.PoolTagTxtLoaded ? "loaded" : "not found (built-in tag table and driver scan)")}.";
        MessageBox.Show(owner,
            $"PoolWatch {typeof(UiActions).Assembly.GetName().Version}\n\n{monitoring}\n\n" +
            $"Overhead (budget in brackets){(o.SteadyStateSeconds >= SteadySeconds ? $", steady state over {Fmt.Duration(TimeSpan.FromSeconds(o.SteadyStateSeconds))}" : " - still warming up, numbers include start-up")}:\n" +
            $"CPU {o.CpuPercentOfOneCore:0.00}% of one core (< {SelfMeter.CpuBudgetPercent.ToString(System.Globalization.CultureInfo.InvariantCulture)}%)\n" +
            $"Peak working set {Fmt.Bytes(o.PeakWorkingSetBytes)} (< {SelfMeter.RamBudgetBytes >> 20} MB)\n" +
            $"Disk writes {Fmt.Bytes(o.DiskWriteBytesPerHour)}/h (< {SelfMeter.DiskBudgetBytesPerHour >> 20} MB/h)\n" +
            $"{_engine.Meter.OwnCostLine()}\n\nData: {_engine.Paths.DataRoot}\n\n" +
            "PoolWatch watches and reads. It changes the system where you choose to (Clear cache, background " +
            "services, scheduled tasks, startup apps, End process, start at logon) and, when you turn it on, in the token-leak " +
            "guard's live focus setting at logon; and, on its own, it secures its data folder and removes an older PoolWatch's " +
            "leftover clear-cache tasks. SECURITY.md in the source repository (github.com/nubbymong/poolwatch) lists every such " +
            "action. No network calls, no telemetry.", "About PoolWatch");
    }

    // ---------------------------------------------------------------- Explorer

    /// <summary>The real explorer.exe, by full path, never a bare name resolved through App Paths or the current directory.</summary>
    private static readonly string ExplorerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    /// <summary>
    /// Opens a folder in Explorer. S1/S3: the elevated app never shell-executes a path (that would consult the user's
    /// shell associations and App Paths, both user-writable): it starts explorer.exe by full path with the folder as an
    /// argument and UseShellExecute = false.
    /// </summary>
    public static void OpenFolder(string folder)
    {
        try { Process.Start(new ProcessStartInfo(ExplorerPath, $"\"{Path.GetFullPath(folder)}\"") { UseShellExecute = false }); } catch { }
    }

    /// <summary>Shows a file in Explorer with it selected: the folder opens, never the file through its association.</summary>
    public static void ShowFile(string file)
    {
        try { Process.Start(new ProcessStartInfo(ExplorerPath, $"/select,\"{Path.GetFullPath(file)}\"") { UseShellExecute = false }); } catch { }
    }
}
