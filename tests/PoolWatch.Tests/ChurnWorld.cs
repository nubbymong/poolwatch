using PoolWatch.Core;
using PoolWatch.Core.History;
using PoolWatch.Core.Model;

namespace PoolWatch.Tests;

/// <summary>
/// A busy agentic-coding PC in miniature, for the memory and allocation tests: a few long-lived sessions whose shells
/// start many short commands (each with its own long command line, most seen in a single sample), a hundred long-lived
/// processes doing I/O, and one crawler whose I/O drives a growing tag. Deterministic (fixed seed), 10 s samples.
/// </summary>
internal sealed class ChurnWorld
{
    public static readonly DateTime T0 = new(2026, 10, 20, 6, 0, 0, DateTimeKind.Utc);
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
    public const int CrawlerPid = 900, Sessions = 6;
    public readonly History History = History.ForInterval(Interval.TotalSeconds);
    public readonly ProcessRegistry Registry = new();
    public long TagBytes = 200L << 20;
    public int Index;
    private readonly Random _rnd = new(42);
    private readonly Dictionary<int, ProcessSample> _live = new();
    private readonly List<(int pid, long create, int samplesLeft)> _short = [];
    private int _nextPid = 20_000;

    public DateTime Now => T0 + Interval * Index;
    public static PoolTag Tag => PoolTag.Parse("Reff");

    /// <summary>An hour of churn with a growth in its second half, and the window an incident would rank it over.</summary>
    public static (ChurnWorld w, Core.Correlation.CorrelationWindow window) GrowingHour()
    {
        var w = new ChurnWorld();
        for (int i = 0; i < 180; i++) w.Step(shortLived: 20, busy: 5, tagGrowth: 0);
        for (int i = 0; i < 180; i++) w.Step(shortLived: 20, busy: 5, tagGrowth: 2L << 20);
        return (w, new Core.Correlation.CorrelationWindow(T0, T0 + Interval * 180, null, w.Now));
    }

    public ChurnWorld(int longLived = 100)
    {
        long boot = T0.AddHours(-2).ToFileTimeUtc();
        _live[4] = new ProcessSample { Pid = 4, ImageName = "System", CreateTime = boot };
        for (int s = 0; s < Sessions; s++)
            _live[100 + s] = new ProcessSample { Pid = 100 + s, ParentPid = 4, ImageName = "claude.exe", CreateTime = boot + s };
        _live[CrawlerPid] = new ProcessSample { Pid = CrawlerPid, ParentPid = 4, ImageName = "crawler.exe", CreateTime = boot };
        for (int i = 0; i < longLived; i++)
            _live[1000 + i] = new ProcessSample { Pid = 1000 + i, ParentPid = 4, ImageName = $"app{i % 37}.exe", CreateTime = boot + 100 + i, HandleCount = 200 + i };
        // No image paths: Windows paths parse differently on the Linux test run, and the identities must not depend on it.
        foreach (var p in _live.Values)
            Registry.OnStart(p.Pid, p.ParentPid, p.CreateTime, p.ImageName, null, $"{p.ImageName} --service", null, DateTime.FromFileTimeUtc(p.CreateTime));
    }

    /// <summary>
    /// One sample. <paramref name="shortLived"/> new commands start under the sessions and are seen in this sample only;
    /// <paramref name="busy"/> more live for three samples doing I/O, those of the first session (PID 100) another
    /// <paramref name="firstSessionOps"/> each. The crawler's I/O follows <paramref name="tagGrowth"/>.
    /// </summary>
    public Sample Step(int shortLived, int busy, long tagGrowth, long firstSessionOps = 0)
    {
        foreach (var (pid, create, left) in _short.ToList())
        {
            if (left > 1)
            {
                _short[_short.FindIndex(x => x.pid == pid)] = (pid, create, left - 1);
                Io(pid, 50 + _rnd.Next(500) + (pid % Sessions == 0 ? firstSessionOps : 0));
                continue;
            }
            _short.RemoveAll(x => x.pid == pid);
            _live.Remove(pid);
            Registry.OnStop(pid, create, 0, Now.AddSeconds(-1));
        }
        for (int k = 0; k < shortLived + busy; k++) Start(k < busy ? 3 : 1);
        foreach (int pid in _live.Keys.Where(p => p >= 1000 && p < 20_000).ToList()) Io(pid, _rnd.Next(400));
        Io(CrawlerPid, tagGrowth > 0 ? tagGrowth / 1000 + _rnd.Next(2000) : _rnd.Next(50));
        TagBytes += tagGrowth;
        var tags = new List<PoolTagEntry> { new(Tag, 10, 1, (ulong)TagBytes, 0, 0, 0), new(PoolTag.Parse("File"), 5, 1, 64UL << 20, 0, 0, 0) };
        var s = new Sample(Now, new PoolTotals(4UL << 30, 2UL << 30, 4UL << 30, 2UL << 30), tags, _live.Values.ToList());
        Registry.ObserveSnapshot(s.Processes, Now);
        History.Add(s);
        Events = Registry.DrainEvents();
        Index++;
        return s;
    }

    /// <summary>The lifecycle events of the last sample (starts, exits).</summary>
    public List<LifecycleEvent> Events { get; private set; } = [];

    private void Start(int samples)
    {
        int pid = _nextPid++;
        int parent = 100 + pid % Sessions;
        long create = Now.AddSeconds(-2).ToFileTimeUtc();
        string image = (pid % 4) switch { 0 => "bash.exe", 1 => "git.exe", 2 => "grep.exe", _ => "node.exe" };
        Registry.OnStart(pid, parent, create, image, null, CommandLine(image), null, DateTime.FromFileTimeUtc(create));
        _live[pid] = new ProcessSample { Pid = pid, ParentPid = parent, ImageName = image, CreateTime = create, HandleCount = 40 };
        _short.Add((pid, create, samples));
    }

    /// <summary>A unique command line of 200 to 1,200 characters, like an agent's multi-line shell script.</summary>
    private string CommandLine(string image)
    {
        var hex = Convert.ToHexString(BitConverter.GetBytes(_rnd.NextInt64()));
        string body = string.Concat(Enumerable.Range(0, 2 + _rnd.Next(12)).Select(i => $"; echo step{i} {hex} >> /tmp/{_rnd.NextInt64():x16}/out.txt"));
        return $"{image} -c \"cd /d/work/{hex} && git -C /d/repos/{hex} status --porcelain{body}\"";
    }

    private void Io(int pid, long ops)
    {
        var p = _live[pid];
        _live[pid] = p with { OtherOperationCount = p.OtherOperationCount + ops, ReadOperationCount = p.ReadOperationCount + ops / 3 };
    }
}
