using PoolWatch.Core;
using PoolWatch.Core.History;
using PoolWatch.Core.Model;

namespace PoolWatch.Tests;

/// <summary>
/// The maintainer's PC under agentic coding, sampled every 10 s as the engine samples it (beta.2's profile.json after 4 hours,
/// 2026-11-09): about 600 long-running processes, 40 of them doing I/O in a sample; six Claude Code sessions whose shells
/// start about 9,400 commands an hour (26 a sample), each a bash.exe with its own long command line that runs two Git Bash
/// fork copies (the shell's image and exact command line, living milliseconds), each running a program and nine tools of a
/// few milliseconds (about 600 starts a sample, the maintainer's 910,000 in 4 hours). Most commands
/// end within the sample; about a third are seen in a snapshot, a tenth run for minutes. About 3,000 pool tags, 150 of them
/// over 256 KB, and the tags given to <see cref="Step"/> grow. Deterministic (fixed seed).
/// </summary>
internal sealed class AgentLoadWorld
{
    public static readonly DateTime T0 = new(2026, 11, 9, 20, 0, 0, DateTimeKind.Utc);
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
    public const int Sessions = 6, LongLived = 600, CommandsPerSample = 26, Tags = 3000, SignificantTags = 150;
    public const int SessionPid = 100, LongLivedPid = 1000;

    public readonly History History = History.ForInterval(Interval.TotalSeconds);
    /// <summary>The registry, its clock reading the world's time (how long exited records are kept is timed on it).</summary>
    public readonly ProcessRegistry Registry;
    public int Index;
    public DateTime Now => T0 + Interval * Index;
    /// <summary>ETW starts so far (commands, fork copies and what the copies ran).</summary>
    public long Starts;

    private readonly Random _rnd = new(20261109);
    private readonly Dictionary<int, ProcessSample> _live = new();
    private readonly List<Command> _running = [];
    private readonly Core.PoolTag[] _tags = new Core.PoolTag[Tags];
    private readonly long[] _tagBytes = new long[Tags];
    private int _nextPid = 20_000;
    private long _seq;

    private sealed class Command
    {
        public required int Pid;
        public required long Create;
        public required long ExitTicks;
        public int Ops;
    }

    public AgentLoadWorld()
    {
        Registry = new ProcessRegistry(clock: () => Clocks.At(Now));
        History.NameOf = key => Registry.Get(key)?.DisplayName; // as the pipeline names the since-boot report's processes
        long boot = T0.AddHours(-3).ToFileTimeUtc();
        _live[4] = new ProcessSample { Pid = 4, ImageName = "System", CreateTime = boot };
        for (int s = 0; s < Sessions; s++)
            _live[SessionPid + s] = new ProcessSample { Pid = SessionPid + s, ParentPid = 4, ImageName = "claude.exe", CreateTime = boot + s, HandleCount = 900 };
        string[] apps = ["chrome.exe", "msedge.exe", "Code.exe", "node.exe", "svchost.exe", "explorer.exe", "OneDrive.exe", "Teams.exe", "python.exe", "dotnet.exe",
            "MsMpEng.exe", "SearchHost.exe", "conhost.exe", "WindowsTerminal.exe", "devenv.exe", "docker.exe", "wsl.exe", "pwsh.exe", "RuntimeBroker.exe", "dllhost.exe"];
        for (int i = 0; i < LongLived; i++)
            _live[LongLivedPid + i] = new ProcessSample { Pid = LongLivedPid + i, ParentPid = 4, ImageName = apps[i % apps.Length], CreateTime = boot + 100 + i, HandleCount = 200 + i % 700 };
        foreach (var p in _live.Values)
            Registry.OnStart(p.Pid, p.ParentPid, p.CreateTime, p.ImageName, null, $"{p.ImageName} --type=service {p.Pid}", null, DateTime.FromFileTimeUtc(p.CreateTime));
        for (int t = 0; t < Tags; t++)
        {
            _tags[t] = Core.PoolTag.Parse($"{(char)('A' + t / 676 % 26)}{(char)('a' + t / 26 % 26)}{(char)('a' + t % 26)}{t % 10}");
            _tagBytes[t] = t < SignificantTags ? (1L + t % 40) << 20 : t < 400 ? 16_384 + t * 400 : 512 + t % 4000;
        }
        Registry.DrainEvents();
    }

    /// <summary>The tag at index <paramref name="i"/> (0 to 149 are the significant ones).</summary>
    public Core.PoolTag Tag(int i) => _tags[i];

    /// <summary>The tags (0 to 29) whose growth has a long-running process doing I/O in step with it (<see cref="LongLivedPid"/> + tag).</summary>
    public const int Growers = 30;
    /// <summary>Sub-second tools each fork copy runs: 26 commands of 23 processes, about 600 starts a sample.</summary>
    public const int ToolsPerCopy = 9;
    private static readonly string[] Tools = ["sed.exe", "cut.exe", "head.exe", "tr.exe", "wc.exe", "cat.exe", "uname.exe", "date.exe", "dirname.exe"];

    /// <summary>
    /// One sample at <see cref="Now"/>: the last 10 s of commands (their starts and exits, as ETW delivers them), the processes
    /// alive at the sample with their I/O, and the tags, each significant tag t up <paramref name="growth"/>(t) bytes. The
    /// long-running process <see cref="LongLivedPid"/> + t does I/O in step with a growing tag t under <see cref="Growers"/>.
    /// </summary>
    public Sample Step(Func<int, long> growth)
    {
        var now = Now;
        long nowFt = now.ToFileTimeUtc();
        // The commands of the last 10 s, spread over it.
        for (int k = 0; k < CommandsPerSample; k++)
        {
            int session = SessionPid + _rnd.Next(Sessions);
            long create = now.AddSeconds(-10 + k * 10.0 / CommandsPerSample).ToFileTimeUtc() + ++_seq;
            int roll = _rnd.Next(100);
            // Most end within the sample; a third are seen in a snapshot (1 to 3 samples); a tenth run for minutes.
            long life = roll < 66 ? _rnd.Next(2, 60) * TimeSpan.TicksPerSecond / 10
                : roll < 90 ? _rnd.Next(10, 30) * TimeSpan.TicksPerSecond
                : _rnd.Next(40, 600) * TimeSpan.TicksPerSecond;
            string cl = BashCommandLine();
            int bash = StartProcess(session, create, "bash.exe", cl);
            for (int c = 0; c < 2; c++)
            {
                // A fork copy of the shell (its image and exact command line, read from its own start event: a string of
                // its own), and the program the copy runs.
                long cc = create + (c + 1) * TimeSpan.TicksPerMillisecond * 20;
                int copy = StartProcess(bash, cc, "bash.exe", new string(cl.AsSpan()));
                string image = (_rnd.Next(4)) switch { 0 => "git.exe", 1 => "grep.exe", 2 => "node.exe", _ => "sed.exe" };
                long pc = cc + TimeSpan.TicksPerMillisecond * 5;
                int program = StartProcess(copy, pc, image, image == "git.exe" ? "git status --porcelain" : $"{image} -n {_rnd.Next(1000)}");
                long programLife = c == 1 && roll >= 66 ? life - TimeSpan.TicksPerSecond / 2 : TimeSpan.TicksPerMillisecond * _rnd.Next(10, 300);
                // Half of the programs seen in a snapshot do I/O there (a test run, a build); the rest wait.
                Exit(program, pc, pc + programLife, image, copy, nowFt, ops: _rnd.Next(2) == 0 ? 40 + _rnd.Next(400) : 0);
                // The pipeline's tools under the copy, each a few milliseconds (the maintainer's PC: 630 starts a sample).
                for (int t = 0; t < ToolsPerCopy; t++)
                {
                    long tc = pc + TimeSpan.TicksPerMillisecond * (t + 1);
                    string tool = Tools[(t + c) % Tools.Length];
                    int tp = StartProcess(copy, tc, tool, $"{tool[..^4]} -n {t}");
                    Registry.OnStop(tp, tc, 0, DateTime.FromFileTimeUtc(tc + TimeSpan.TicksPerMillisecond * 3));
                }
                Exit(copy, cc, pc + programLife + TimeSpan.TicksPerMillisecond, "bash.exe", bash, nowFt, ops: 0);
            }
            Exit(bash, create, create + life, "bash.exe", session, nowFt, ops: 0);
        }
        // Commands that end by this sample are gone from it.
        for (int i = _running.Count - 1; i >= 0; i--)
        {
            var c = _running[i];
            if (c.ExitTicks > nowFt) { Io(c.Pid, c.Ops); continue; }
            _running.RemoveAt(i);
            _live.Remove(c.Pid);
            Registry.OnStop(c.Pid, c.Create, 0, DateTime.FromFileTimeUtc(c.ExitTicks));
        }
        // 40 long-running processes do I/O in a sample; the one beside each growing tag in step with it.
        for (int i = 0; i < 40; i++) Io(LongLivedPid + Growers + _rnd.Next(LongLived - Growers), _rnd.Next(300));
        for (int t = 0; t < Tags; t++)
        {
            long grow = t < SignificantTags ? growth(t) : 0;
            if (grow > 0)
            {
                _tagBytes[t] += grow + _rnd.Next(1 << 16);
                if (t < Growers) Io(LongLivedPid + t, 2000 + _rnd.Next(500));
            }
            else if (t < SignificantTags) _tagBytes[t] = Math.Max(1 << 18, _tagBytes[t] + _rnd.Next(-1 << 16, 1 << 16));
        }
        var tags = new List<PoolTagEntry>(Tags);
        for (int t = 0; t < Tags; t++)
            tags.Add(new PoolTagEntry(_tags[t], (uint)(1000 + Index * 7 + t), (uint)(900 + Index * 7 + t), (ulong)(t % 3 == 0 ? _tagBytes[t] : 0), (uint)(500 + Index), (uint)(400 + Index), (ulong)(t % 3 == 0 ? 0 : _tagBytes[t])));
        var s = new Sample(now, new PoolTotals(6UL << 30, 3UL << 30, 6UL << 30, 3UL << 30), tags, _live.Values.ToList()) { TickMs = Clocks.At(now).Ms };
        Index++;
        return s;
    }

    /// <summary>A handle census of the live processes (every 5 minutes in the engine), at the last sample's tick.</summary>
    public TypedHandles Handles(Sample s) =>
        new(s.TimeUtc, s.Processes.ToDictionary(p => p.Key, p => new Dictionary<string, int> { ["File"] = p.HandleCount / 3, ["Event"] = p.HandleCount / 3, ["Key"] = p.HandleCount / 3 })) { Ms = s.TickMs };

    private int StartProcess(int parent, long create, string image, string commandLine)
    {
        int pid = _nextPid++;
        if (_nextPid > 900_000) _nextPid = 20_000;
        Starts++;
        Registry.OnStart(pid, parent, create, image, image == "bash.exe" ? @"C:\Program Files\Git\usr\bin\bash.exe" : null, commandLine, @"PC\me", DateTime.FromFileTimeUtc(create));
        return pid;
    }

    /// <summary>A process ends at <paramref name="exit"/>: at once when that is before the sample, else it is seen in the snapshots until then.</summary>
    private void Exit(int pid, long create, long exit, string image, int parent, long nowFt, int ops)
    {
        if (exit <= nowFt)
        {
            Registry.OnStop(pid, create, 0, DateTime.FromFileTimeUtc(exit));
            return;
        }
        _live[pid] = new ProcessSample { Pid = pid, ParentPid = parent, ImageName = image, CreateTime = create, HandleCount = 60 };
        _running.Add(new Command { Pid = pid, Create = create, ExitTicks = exit, Ops = ops });
        Io(pid, ops);
    }

    private void Io(int pid, long ops)
    {
        var p = _live[pid];
        _live[pid] = p with { OtherOperationCount = p.OtherOperationCount + ops, ReadOperationCount = p.ReadOperationCount + ops / 3, KernelTime = p.KernelTime + ops * 100 };
    }

    /// <summary>A unique command line of 200 to 1,200 characters, like an agent's multi-line shell script.</summary>
    private string BashCommandLine()
    {
        var hex = _rnd.NextInt64().ToString("x16");
        string body = string.Concat(Enumerable.Range(0, 2 + _rnd.Next(14)).Select(i => $"; echo step{i} {hex} >> /tmp/{_rnd.NextInt64():x16}/out.txt"));
        return $"\"C:\\Program Files\\Git\\usr\\bin\\bash.exe\" -c \"cd /d/work/{hex} && git -C /d/repos/{hex} status --porcelain{body}\"";
    }
}
