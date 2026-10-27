using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32.SafeHandles;

// Test helper for the PoolWatch end-to-end scenarios (DESIGN.md section 12). Only touches the directory it is given.
//   leak-handles --dir D [--count 40000] [--ramp 40] [--hold 40]   open many File handles to one file and hold them
//   touch-files  --dir D [--count 30000] [--ramp 40] [--hold 30]   create + write many files and folders, then delete the tree
//   orphan       --dir D [--seconds 70] [--count 20000] --pid-file F  start a child that keeps doing I/O, then exit
//   child-io     --dir D [--seconds 70] [--count 20000]             (the orphan's child) steady file churn
//   shell-load   --dir D [--seconds 600] [--chains-per-minute 60] [--files-per-second 200] [--handles 200000]
//                a busy developer PC: Git Bash command chains like Claude Code runs (bash > ls/grep/head/sed/git/find),
//                file churn, and a large system handle count
//   token-spawn  --dir D [--segments 0,30,5,...] [--segment-seconds 6] [--hold 20] [--tokens-per-start 1]  per segment start
//                that many short-lived cmd.exe children, leaking duplicated tokens per start (held until the end)
//   cpu-burn     --dir D [--seconds 40]                              busy threads on every core at the highest priority (lag)
//   accumulate   --dir D [--count 15] [--every 6] [--hold 30]       start a long-running cmd.exe child every N s and never
//                end it until the hold (helpers piling up), then end its own children
//   crash        --dir D                                           an unhandled exception (Application Error 1000 for Leaker.exe)
//   spawn        --dir D --exe E [--args A] [--count 120] [--seconds 30]  start E (A with {dir} replaced) count times, each waited for

var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
for (int i = 1; i + 1 < args.Length; i += 2) opts[args[i].TrimStart('-')] = args[i + 1];
string mode = args.Length > 0 ? args[0] : "";
string dir = opts.GetValueOrDefault("dir") ?? throw new ArgumentException("--dir is required");
int Int(string name, int def) => opts.TryGetValue(name, out var v) ? int.Parse(v, CultureInfo.InvariantCulture) : def;
// The orphan's stdout is a pipe whose reader (the parent) is gone: logging must never fail the scenario.
void Log(string msg)
{
    try { Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} leaker[{Environment.ProcessId}] {msg}"); }
    catch (IOException) { }
}

Directory.CreateDirectory(dir);
switch (mode)
{
    case "leak-handles": LeakHandles(Int("count", 40000), Int("ramp", 40), Int("hold", 40)); break;
    case "touch-files": TouchFiles(Int("count", 30000), Int("ramp", 40), Int("hold", 30)); break;
    case "orphan": Orphan(Int("seconds", 70), Int("count", 20000), opts.GetValueOrDefault("pid-file")); break;
    case "child-io": ChildIo(Int("seconds", 70), Int("count", 20000)); break;
    case "shell-load": ShellLoad(Int("seconds", 600), Int("chains-per-minute", 60), Int("files-per-second", 200), Int("handles", 200000)); break;
    case "token-spawn": TokenSpawn(opts.GetValueOrDefault("segments") ?? "0,30,5,40,0,25,10,35,0,20,15,30,0,40", Int("segment-seconds", 6), Int("hold", 20), Int("tokens-per-start", 1)); break;
    case "cpu-burn": CpuBurn(Int("seconds", 40)); break;
    case "crash": Crash(); break;
    case "accumulate": Accumulate(Int("count", 15), Int("every", 6), Int("hold", 30)); break;
    case "spawn": Spawn(opts.GetValueOrDefault("exe") ?? "git", opts.GetValueOrDefault("args") ?? "-C {dir} status --porcelain", Int("count", 120), Int("seconds", 30)); break;
    default: Console.Error.WriteLine("usage: Leaker leak-handles|touch-files|orphan|child-io|spawn|token-spawn|shell-load --dir <dir> [...]"); return 2;
}
return 0;

void LeakHandles(int count, int ramp, int hold)
{
    string file = Path.Combine(dir, "held.bin");
    File.WriteAllBytes(file, new byte[16]);
    var handles = new List<SafeFileHandle>(count);
    Log($"leak-handles: opening {count} handles over {ramp} s");
    Paced(count, ramp, i => handles.Add(File.OpenHandle(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)));
    Log($"leak-handles: holding {handles.Count} handles for {hold} s");
    Thread.Sleep(TimeSpan.FromSeconds(hold));
    foreach (var h in handles) h.Dispose();
    Log("leak-handles: released");
}

void TouchFiles(int count, int ramp, int hold)
{
    string root = Path.Combine(dir, "tree");
    var data = new byte[4096];
    Log($"touch-files: creating {count} files over {ramp} s");
    Paced(count, ramp, i =>
    {
        string sub = Path.Combine(root, $"d{i / 100:D4}");
        if (i % 100 == 0) Directory.CreateDirectory(sub);
        File.WriteAllBytes(Path.Combine(sub, $"f{i % 100:D3}.dat"), data);
    });
    Log($"touch-files: holding for {hold} s");
    Thread.Sleep(TimeSpan.FromSeconds(hold));
    var sw = Stopwatch.StartNew();
    Directory.Delete(root, recursive: true);
    Log($"touch-files: deleted the tree in {sw.ElapsedMilliseconds} ms");
}

void Orphan(int seconds, int count, string? pidFile)
{
    // Redirected so the child does not inherit (and hold open) the caller's console pipe after the parent exits.
    var psi = new ProcessStartInfo(Environment.ProcessPath!)
    {
        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
    };
    foreach (var a in new[] { "child-io", "--dir", Path.Combine(dir, "child"), "--seconds", seconds.ToString(CultureInfo.InvariantCulture), "--count", count.ToString(CultureInfo.InvariantCulture) })
        psi.ArgumentList.Add(a);
    using var child = Process.Start(psi)!;
    if (pidFile is not null) File.WriteAllText(pidFile, child.Id.ToString(CultureInfo.InvariantCulture));
    Log($"orphan: started child {child.Id}; parent exits in 3 s");
    Thread.Sleep(3000);
}

void ChildIo(int seconds, int count)
{
    var data = new byte[4096];
    Log($"child-io: {count} files over {seconds} s");
    Paced(count, seconds, i =>
    {
        string sub = Path.Combine(dir, $"d{i / 100:D4}");
        if (i % 100 == 0) Directory.CreateDirectory(sub);
        string f = Path.Combine(sub, $"f{i % 100:D3}.dat");
        File.WriteAllBytes(f, data);
        File.ReadAllBytes(f);
    });
    Log("child-io: done");
}

void Spawn(string exe, string argTemplate, int count, int seconds)
{
    string args = argTemplate.Replace("{dir}", dir);
    Log($"spawn: {exe} {args} x{count} over {seconds} s");
    int failed = 0;
    Paced(count, seconds, i =>
    {
        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = dir };
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) failed++;
    });
    Log($"spawn: done ({failed} non-zero exits)");
}

void ShellLoad(int seconds, int chainsPerMinute, int filesPerSecond, int handleCount)
{
    var stop = DateTime.UtcNow.AddSeconds(seconds);
    // Held handles: event objects (cheap), so the system-wide handle table is large like on a busy PC.
    var held = new List<IntPtr>(handleCount);
    for (int i = 0; i < handleCount; i++) { var h = Native.CreateEventW(IntPtr.Zero, true, false, null); if (h == IntPtr.Zero) break; held.Add(h); }
    string repo = Path.Combine(dir, "repo");
    Directory.CreateDirectory(repo);
    File.WriteAllText(Path.Combine(repo, "a.txt"), "line one\nline two\nline three\n");
    string bash = new[] { @"C:\Program Files\Git\bin\bash.exe", @"C:\Program Files\Git\usr\bin\bash.exe" }.FirstOrDefault(File.Exists) ?? "";
    Run(bash.Length > 0 ? bash : "cmd.exe", bash.Length > 0 ? $"-c \"git init -q '{ToPosix(repo)}' && git -C '{ToPosix(repo)}' add a.txt\"" : "/c exit 0");
    string r = ToPosix(repo);
    string[] chains = bash.Length > 0
        ? [
            "ls -la /c/Windows/System32 | grep -i dll | head -5",
            $"git -C '{r}' status --porcelain | wc -l",
            $"cat '{r}/a.txt' | sed -n '1,2p' | head -1",
            $"find '{r}' -maxdepth 2 -name '*.txt' | head -3",
            "echo hello | grep h | tr a-z A-Z",
            $"git -C '{r}' rev-parse --show-toplevel && git -C '{r}' log --oneline -1 2>/dev/null | head -1",
          ]
        : ["/c dir C:\\Windows | findstr /i dll", "/c echo hello | findstr h"];
    Log($"shell-load: {seconds} s, {chainsPerMinute} chains/min via {(bash.Length > 0 ? bash : "cmd.exe")}, {filesPerSecond} files/s, {held.Count} handles held");
    long chainsRun = 0, files = 0;
    var churn = new Thread(() =>
    {
        string root = Path.Combine(dir, "churn");
        var data = new byte[2048];
        var sw = Stopwatch.StartNew();
        for (long i = 0; DateTime.UtcNow < stop; i++)
        {
            string sub = Path.Combine(root, $"d{i / 200 % 50:D2}");
            if (i % 200 == 0) Directory.CreateDirectory(sub);
            string f = Path.Combine(sub, $"f{i % 200:D3}.tmp");
            try { File.WriteAllBytes(f, data); File.ReadAllBytes(f); if (i % 3 == 0) File.Delete(f); } catch (IOException) { }
            Interlocked.Increment(ref files);
            double ahead = (i + 1) * 1000.0 / Math.Max(1, filesPerSecond) - sw.Elapsed.TotalMilliseconds;
            if (ahead > 15) Thread.Sleep((int)ahead);
        }
    }) { IsBackground = true };
    churn.Start();
    int workers = 4;
    var threads = Enumerable.Range(0, workers).Select(w => new Thread(() =>
    {
        var sw = Stopwatch.StartNew();
        double perChain = 60_000.0 * workers / Math.Max(1, chainsPerMinute);
        for (long i = 0; DateTime.UtcNow < stop; i++)
        {
            string c = chains[(i * workers + w) % chains.Length];
            Run(bash.Length > 0 ? bash : "cmd.exe", bash.Length > 0 ? "-c \"" + c.Replace("\"", "\\\"") + "\"" : c);
            Interlocked.Increment(ref chainsRun);
            double ahead = (i + 1) * perChain - sw.Elapsed.TotalMilliseconds;
            if (ahead > 15) Thread.Sleep((int)Math.Min(ahead, 60_000));
        }
    }) { IsBackground = true }).ToList();
    threads.ForEach(t => t.Start());
    threads.ForEach(t => t.Join());
    churn.Join();
    foreach (var h in held) Native.CloseHandle(h);
    Log($"shell-load: done, {chainsRun} chains, {files} file operations");

    static string ToPosix(string p) => "/" + char.ToLowerInvariant(p[0]) + p[2..].Replace('\\', '/');
    void Run(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = dir };
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
        }
        catch (Exception ex) { Log("shell-load: " + ex.Message); }
    }
}

void TokenSpawn(string segments, int segmentSeconds, int hold, int perStart)
{
    var counts = segments.Split(',').Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToList();
    var leaked = new List<IntPtr>();
    if (!Native.OpenProcessToken(Process.GetCurrentProcess().Handle, 0x0008 | 0x0002 /* QUERY | DUPLICATE */, out var self)) throw new IOException("OpenProcessToken failed");
    Log($"token-spawn: {counts.Sum()} starts in {counts.Count} segments of {segmentSeconds} s, one leaked token per start");
    foreach (int n in counts)
    {
        var sw = Stopwatch.StartNew();
        if (n > 0)
            Paced(n, segmentSeconds, _ =>
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c exit 0") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = dir };
                using (var p = Process.Start(psi)!) p.WaitForExit();
                // The simulated defect: each start leaves a token behind (a new token object, held by a handle).
                for (int k = 0; k < perStart; k++)
                    if (Native.DuplicateTokenEx(self, 0x02000000 /* MAXIMUM_ALLOWED */, IntPtr.Zero, 2, 1, out var dup)) leaked.Add(dup);
            });
        var rest = TimeSpan.FromSeconds(segmentSeconds) - sw.Elapsed;
        if (rest > TimeSpan.Zero) Thread.Sleep(rest);
    }
    Log($"token-spawn: {leaked.Count} tokens leaked; holding for {hold} s");
    Thread.Sleep(TimeSpan.FromSeconds(hold));
    foreach (var h in leaked) Native.CloseHandle(h);
    Native.CloseHandle(self);
}

void CpuBurn(int seconds)
{
    // Starves normal-priority threads, so a 15 ms sleep wakes up late: the lag the probe must notice. Real-time priority
    // (the runner is elevated; otherwise Windows grants High), pulsed: busy 700 ms, idle 60 ms (a 760 ms period: it does not divide the probe interval, so probes cannot lock onto the gaps). Windows' starvation boost
    // lifts a waiting normal thread only to priority 15, below the real-time range, so most probes wake up to 700 ms late,
    // on probe after probe; the idle gaps keep the rest of the runner alive.
    using var self = Process.GetCurrentProcess();
    try { self.PriorityClass = ProcessPriorityClass.RealTime; } catch { self.PriorityClass = ProcessPriorityClass.High; }
    var until = DateTime.UtcNow.AddSeconds(seconds);
    // Every logical processor the machine has, not just the ones .NET reports for this process (on the CI runner
    // ProcessorCount said 2 while the probe kept getting a free core): one burner per processor, at least 4.
    int cpus = Math.Max(Environment.ProcessorCount, (int)Native.GetActiveProcessorCount(0xFFFF));
    int n = Math.Max(4, cpus);
    try { if (cpus < 64 && (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())) self.ProcessorAffinity = (nint)((1L << cpus) - 1); } catch { /* a job may forbid it */ }
    Log($"cpu-burn: {n} threads ({Environment.ProcessorCount} reported, {cpus} active), {self.PriorityClass}, pulsed 700/60 ms, for {seconds} s");
    var threads = Enumerable.Range(0, n).Select(_ => new Thread(() =>
    {
        double x = 1;
        while (DateTime.UtcNow < until)
        {
            var busy = Stopwatch.StartNew();
            while (busy.ElapsedMilliseconds < 700) x = Math.Sqrt(x + 1);
            Thread.Sleep(60);
        }
    }) { Priority = ThreadPriority.Highest, IsBackground = true }).ToList();
    threads.ForEach(t => t.Start());
    threads.ForEach(t => t.Join());
    Log("cpu-burn: done");
}

// Spreads `count` operations evenly over `seconds`, so the activity series is a steady ramp.
static void Paced(int count, int seconds, Action<int> op)
{
    var sw = Stopwatch.StartNew();
    double perOp = seconds * 1000.0 / Math.Max(1, count);
    for (int i = 0; i < count; i++)
    {
        op(i);
        double due = (i + 1) * perOp;
        double ahead = due - sw.Elapsed.TotalMilliseconds;
        if (ahead > 15) Thread.Sleep((int)ahead);
    }
}

// An unhandled exception: Windows logs Application Error 1000 (and usually a WER APPCRASH report) for Leaker.exe.
void Accumulate(int count, int every, int hold)
{
    var children = new List<Process>();
    for (int i = 0; i < count; i++)
    {
        var psi = new ProcessStartInfo("cmd.exe", "/c ping -n 600 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        var p = Process.Start(psi)!;
        p.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        children.Add(p);
        Log($"accumulate: {children.Count} children");
        Thread.Sleep(every * 1000);
    }
    Thread.Sleep(hold * 1000);
    foreach (var p in children) { try { p.Kill(entireProcessTree: true); } catch { } } // its own children only
    Log("accumulate: done");
}

static void Crash()
{
    Console.WriteLine("crash: unhandled exception");
    throw new InvalidOperationException("Leaker crash mode");
}

static class Native
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    public static extern uint GetActiveProcessorCount(ushort groupNumber);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr handle);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool DuplicateTokenEx(IntPtr existing, uint access, IntPtr attributes, int impersonationLevel, int tokenType, out IntPtr newToken);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    public static extern IntPtr CreateEventW(IntPtr attributes, bool manualReset, bool initialState, string? name);
}

