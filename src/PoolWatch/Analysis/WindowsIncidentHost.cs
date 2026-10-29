using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Win32;
using PoolWatch.Collectors;
using PoolWatch.Core;
using PoolWatch.Core.Aicc;
using PoolWatch.Core.Correlation;
using PoolWatch.Core.Model;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Tags;
using PoolWatch.Engine;

namespace PoolWatch.Analysis;

/// <summary>Windows services for incidents: file version + signature, system info, PNG charts, driver tag scans.</summary>
internal sealed class WindowsIncidentHost : IIncidentHost
{
    private readonly MonitorEngine _engine;
    private readonly TagKnowledge _knowledge;
    private readonly ConcurrentDictionary<string, FileDetails> _files = new(StringComparer.OrdinalIgnoreCase);
    private SystemInfo? _system;
    private readonly object _scanLock = new();
    private readonly HashSet<PoolTag> _scanQueue = new();
    private bool _scanRunning;

    private readonly System.Threading.Timer _systemTimer;

    public WindowsIncidentHost(MonitorEngine engine, TagKnowledge knowledge)
    {
        _engine = engine;
        _knowledge = knowledge;
        // fltmc and WMI can be slow (or hang on a struggling machine): gather them off the sampler thread, hourly.
        _systemTimer = new System.Threading.Timer(_ => RefreshSystemInfo(), null, TimeSpan.FromSeconds(5), TimeSpan.FromHours(1));
    }

    public string IncidentsDir => _engine.Paths.IncidentsDir;
    public string CheckWrite(string path) => _engine.Paths.Guard.CheckReal(path);
    public void RequestHandleScan() => _engine.RequestHandleScan();
    public string DisplayUser(string user) => ProcessDetails.DisplayUser(user);

    private readonly ConcurrentDictionary<ProcessKey, TranscriptHit?> _transcripts = new();
    private readonly ConcurrentDictionary<ProcessKey, byte> _searching = new();
    private readonly SemaphoreSlim _searchGate = new(1, 1);
    private int _searchesPending;

    /// <summary>Waits (bounded) for running transcript searches, so a final report does not say "in progress".</summary>
    public void WaitForSearches(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (Volatile.Read(ref _searchesPending) > 0 && DateTime.UtcNow < deadline) Thread.Sleep(100);
    }
    /// <summary>Raised (on a pool thread) when a transcript search for a suspect finished.</summary>
    public event Action<ProcessKey>? SessionResolved;

    public SessionDetails? DescribeSession(Suspect s)
    {
        var chain = _engine.Registry.Ancestry(s.Key);
        bool aicc = chain.Any(r => r.Image.Equals(ProcessLabels.AiccExe, StringComparison.OrdinalIgnoreCase));
        // claude.exe, or Claude Code run as `node …claude-code…/cli.js`.
        bool claude = chain.Take(chain.Count - 1).Any(r => r.Image.Equals("claude.exe", StringComparison.OrdinalIgnoreCase) ||
            (r.Image.Equals("node.exe", StringComparison.OrdinalIgnoreCase) && r.CommandLine is { } cl && (cl.Contains("claude-code", StringComparison.OrdinalIgnoreCase) || cl.Contains("@anthropic-ai", StringComparison.OrdinalIgnoreCase))));
        if (!aicc && !claude) return null;
        // On demand: the live session processes in the chain (the suspect, its shells, claude.exe) are read now.
        foreach (var r in chain)
            if (r.IsAlive && (ProcessLabels.SessionClis.Contains(r.Image) || r.Image.Equals("claude.exe", StringComparison.OrdinalIgnoreCase) || r == chain[^1]))
                ProcessDetails.ReadSessionContext(r);
        var ctx = SessionContext.ForChain(chain);
        string desc = SessionContext.Describe(chain, ctx);
        if (_transcripts.TryGetValue(s.Key, out var hit)) return new SessionDetails(desc, ctx, hit, false);
        if (!claude || string.IsNullOrEmpty(s.CommandLine) || s.StartUtc == DateTime.MinValue) return new SessionDetails(desc, ctx, null, false);
        if (_searching.TryAdd(s.Key, 0))
        {
            var dirs = ConfigDirs(ctx);
            string cmd = s.CommandLine;
            var start = s.StartUtc;
            Interlocked.Increment(ref _searchesPending);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                TranscriptHit? found = null;
                _searchGate.Wait(); // one search at a time: never a burst of file reads
                try
                {
                    found = TranscriptSearch.Find(dirs, cmd, start, _engine.Settings.TranscriptWindowMinutes,
                        new SearchBudget(MaxFiles: _engine.Settings.TranscriptMaxFiles));
                }
                catch (Exception ex) { Log("transcript search failed: " + ex.Message); }
                finally { _searchGate.Release(); Interlocked.Decrement(ref _searchesPending); }
                _transcripts[s.Key] = found;
                Log($"transcript search for PID {s.Key.Pid}: {(found is null ? "no match" : $"session {found.SessionId} ({found.Project})")}");
                SessionResolved?.Invoke(s.Key);
            });
        }
        return new SessionDetails(desc, ctx, null, true);
    }

    /// <summary>
    /// Claude config dirs to search: the session's own, every AICC account profile (of the AiccResourcesRoot setting, or of
    /// the AICC install the session's own config dir is in), and %USERPROFILE%\.claude.
    /// </summary>
    private List<string> ConfigDirs(SessionContext? ctx)
    {
        var dirs = new List<string>();
        if (ctx?.ConfigDir is { } c) dirs.Add(c); // the session's own first; the search stops at its first hit
        foreach (var root in SessionContext.AiccRoots(_engine.Settings.AiccResourcesRoot, ctx?.ConfigDir))
        {
            try
            {
                var profiles = Path.Combine(root, "account-profiles");
                if (!Directory.Exists(profiles)) continue;
                // N11: the root comes from another process's environment, so an elevated enumeration here skips reparse
                // points (a planted junction could redirect the walk), as TranscriptSearch does for the transcripts below.
                var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = FileAttributes.ReparsePoint };
                foreach (var p in Directory.EnumerateDirectories(profiles, "*", opts).Take(50))
                {
                    string d = Path.Combine(p, ".claude");
                    if (!dirs.Contains(d, StringComparer.OrdinalIgnoreCase)) dirs.Add(d);
                }
            }
            catch { }
        }
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"));
        return dirs;
    }
    public void Log(string message) => _engine.Log.Info(message);

    public FileDetails? DescribeFile(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        return _files.GetOrAdd(path, p =>
        {
            string? version = null, company = null, product = null, description = null;
            try
            {
                var v = FileVersionInfo.GetVersionInfo(p);
                version = Blank(v.FileVersion); company = Blank(v.CompanyName); product = Blank(v.ProductName); description = Blank(v.FileDescription);
            }
            catch { }
            var (status, signer) = FileSignature.Check(p);
            return new FileDetails(version, company, product, description, signer, status);
        });
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>The last gathered system info (never blocks: the slow parts are collected on a timer thread).</summary>
    public SystemInfo GetSystemInfo()
    {
        var s = Volatile.Read(ref _system) ?? BasicSystemInfo() with { AntiVirus = "not gathered yet", FilterDrivers = ["(not gathered yet)"] };
        return s with { Volumes = _engine.Volumes.ToList(), PoolWatchCost = _engine.Meter.OwnCostLine() };
    }

    private void RefreshSystemInfo()
    {
        try { Volatile.Write(ref _system, BasicSystemInfo() with { FilterDrivers = FilterDrivers(), AntiVirus = AntiVirus() }); }
        catch (Exception ex) { Log("system info failed: " + ex.Message); }
    }

    private SystemInfo BasicSystemInfo()
    {
        string product = "", display = "", build = Environment.OSVersion.Version.Build.ToString();
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            product = k?.GetValue("ProductName") as string ?? "";
            display = k?.GetValue("DisplayVersion") as string ?? "";
            if (k?.GetValue("CurrentBuild") is string cb) build = cb + (k.GetValue("UBR") is int ubr ? "." + ubr : "");
        }
        catch { }
        return new SystemInfo
        {
            Machine = Environment.MachineName,
            OsDescription = string.Join(" ", new[] { product, display }.Where(s => s.Length > 0)) is { Length: > 0 } d ? d : Environment.OSVersion.VersionString,
            OsBuild = build,
            RamBytes = Native.MemoryNative.TotalPhysical(),
            LogicalCpus = Environment.ProcessorCount,
            BootUtc = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64),
            Volumes = _engine.Volumes.ToList(),
        };
    }

    /// <summary>`fltmc filters` (in-box, read-only), bounded to 10 s.</summary>
    private static List<string> FilterDrivers()
    {
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "fltmc.exe"), "filters")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(10_000)) return ["(fltmc did not answer within 10 s)"];
            return output.Result.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
        }
        catch (Exception ex) { return [$"(fltmc unavailable: {ex.Message})"]; }
    }

    /// <summary>Registered antivirus products (Security Center; absent on Windows Server). Not in the hot path.</summary>
    private static string AntiVirus()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(@"root\SecurityCenter2", "SELECT displayName FROM AntiVirusProduct",
                new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(10), ReturnImmediately = true });
            var names = searcher.Get().Cast<System.Management.ManagementBaseObject>().Select(o => o["displayName"]?.ToString()).Where(n => !string.IsNullOrEmpty(n)).ToList();
            return names.Count > 0 ? string.Join(", ", names) : "none registered";
        }
        catch { return "unknown (Security Center not available, e.g. Windows Server)"; }
    }

    public bool RenderTimeline(TimelineChart chart, string pngPath)
    {
        TimelineRenderer.Render(chart, pngPath);
        return true;
    }

    /// <summary>Queues tags for the bounded driver-binary scan; one scan runs at a time on a low-priority thread.</summary>
    public void RequestTagScan(IReadOnlyCollection<PoolTag> tags)
    {
        lock (_scanLock)
        {
            foreach (var t in tags) _scanQueue.Add(t);
            if (_scanRunning) return;
            _scanRunning = true;
        }
        var thread = new Thread(ScanLoop) { IsBackground = true, Priority = ThreadPriority.Lowest, Name = "PoolWatch tag scan" };
        thread.Start();
    }

    private void ScanLoop()
    {
        while (true)
        {
            List<PoolTag> batch;
            lock (_scanLock)
            {
                batch = _scanQueue.Where(_knowledge.NeedsDriverScan).ToList();
                _scanQueue.Clear();
                if (batch.Count == 0) { _scanRunning = false; return; }
            }
            try
            {
                var sw = Stopwatch.StartNew();
                var files = DriverFiles.List();
                var hits = DriverTagScanner.Scan(batch, files);
                _knowledge.AddDriverHits(hits);
                Log($"tag scan: {batch.Count} tag(s) over {files.Count} driver files in {sw.ElapsedMilliseconds} ms: " +
                    string.Join(", ", hits.Select(kv => $"{kv.Key.ToDisplay()}={(kv.Value.Length > 0 ? string.Join("/", kv.Value) : "?")}")));
            }
            catch (Exception ex) { Log("tag scan failed: " + ex.Message); }
        }
    }
}
