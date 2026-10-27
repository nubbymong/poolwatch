using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using PoolWatch.Core;
using PoolWatch.Core.Incidents;

namespace PoolWatch.Capture;

/// <summary>
/// Reads a confirmation-trace .etl with TraceEvent's TraceLog: kernel pool events (PoolAlloc / PoolFree, read raw by
/// provider GUID and opcode, since TraceEvent has no typed parser for them) with their stacks, and
/// Microsoft-Windows-Kernel-File create / delete events for per-process file churn.
/// </summary>
internal static class EtlParser
{
    /// <summary>The kernel "PoolGuid" event class (NT Kernel Logger, PERF_POOL group).</summary>
    public static readonly Guid PoolGuid = new("0268a8b6-74fd-4302-9dd0-6e8f1795c0cf");
    public static readonly Guid KernelFile = new("EDD08927-9CC4-4E65-B970-C2560FB5C289");
    private const int OpPoolAlloc = 0x20, OpPoolAllocSession = 0x21, OpPoolFree = 0x22, OpPoolFreeSession = 0x23;
    private const int FileCreate = 12, FileDeletePath = 26;
    private const uint FILE_DIRECTORY_FILE = 0x1;
    private const int MaxEvents = 5_000_000;

    private sealed class PoolAgg
    {
        public long Alloc, Freed;
        public int Allocs, Frees;
        public DateTime FirstUtc;
        public readonly Dictionary<string, long> Frames = new();
    }

    private sealed class FileAgg
    {
        public int Creates, Dirs, Deletes;
        public readonly Dictionary<string, int> Parents = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <param name="symbolPath">Local symbol folders for naming kernel functions in the CPU part (never a server); null: module + offset.</param>
    /// <param name="checkWrite">The write policy (PathGuard.CheckReal) for the .etlx TraceEvent writes next to the trace and
    /// PoolWatch deletes after (review round 2, S3); null only where nothing is elevated (none in the app).</param>
    public static CaptureResult Parse(string etlPath, IReadOnlyCollection<PoolTag> tags, DateTime startUtc, DateTime endUtc, Action<string> log, string? symbolPath = null,
        Func<string, string>? checkWrite = null)
    {
        checkWrite ??= p => p;
        var notes = new List<string>();
        var tagSet = new HashSet<uint>(tags.Select(t => t.Raw));
        // Keyed by the trace's process instance (index), not the PID: a PID is reused within minutes on a busy PC.
        var pool = new Dictionary<(int proc, uint tag), PoolAgg>();
        var images = new Dictionary<int, string>();
        var procs = new Dictionary<int, (int pid, long createTime, string? commandLine, int parentPid, string image, DateTime? exit)>();
        var live = new Dictionary<ulong, (int proc, uint tag, long bytes)>(); // block address -> allocation seen in the trace
        var files = new Dictionary<(int pid, string volume), FileAgg>();
        long poolEvents = 0, stacked = 0, fileEvents = 0, seen = 0;
        var cpu = new CpuAgg();
        CpuProfile? cpuProfile = null;

        string etlx = checkWrite(Path.ChangeExtension(etlPath, ".etlx")); // TraceEvent writes it: checked first
        try
        {
        using (var traceLog = TraceLog.OpenOrConvert(etlPath, new TraceLogOptions { ConversionLog = TextWriter.Null, ContinueOnError = true, MaxEventCount = MaxEvents }))
        {
            foreach (var p in traceLog.Processes)
            {
                string image = string.IsNullOrEmpty(p.Name) ? $"PID {p.ProcessID}" : p.ProcessID is 0 or 4 ? p.Name : p.Name + ".exe";
                images[p.ProcessID] = image;
                // Started during the trace: its start time is the create time. Started before: unknown (rundown).
                long ct = p.StartTimeRelativeMsec > 0 ? p.StartTime.ToUniversalTime().ToFileTimeUtc() : 0;
                // Exited during the trace: its exit time (short commands need it to count as short when PoolWatch missed them).
                DateTime? exit = p.EndTimeRelativeMsec > 0 && p.EndTimeRelativeMsec < traceLog.SessionEndTimeRelativeMSec - 1 ? p.EndTime.ToUniversalTime() : null;
                procs[(int)p.ProcessIndex] = (p.ProcessID, ct, string.IsNullOrEmpty(p.CommandLine) ? null : p.CommandLine, p.ParentID, image, exit);
            }
            foreach (var e in traceLog.Events)
            {
                seen++;
                if (cpu.Observe(e)) continue;
                if (e.ProviderGuid == PoolGuid || e.TaskGuid == PoolGuid)
                {
                    int op = (int)e.Opcode;
                    if (op is not (OpPoolAlloc or OpPoolAllocSession or OpPoolFree or OpPoolFreeSession)) continue;
                    poolEvents++;
                    var data = e.EventData();
                    int ptr = e.PointerSize;
                    if (data.Length < 8 + ptr) continue;
                    uint tag = BitConverter.ToUInt32(data, 4);
                    int pid = e.Process() is { } tp ? (int)tp.ProcessIndex : -1; // the process instance
                    if (op is OpPoolAlloc or OpPoolAllocSession)
                    {
                        if (data.Length < 8 + 2 * ptr) continue;
                        long bytes = ptr == 8 ? BitConverter.ToInt64(data, 8) : BitConverter.ToUInt32(data, 8);
                        ulong entry = ptr == 8 ? BitConverter.ToUInt64(data, 8 + ptr) : BitConverter.ToUInt32(data, 8 + ptr);
                        if (tagSet.Count > 0 && !tagSet.Contains(tag)) continue;
                        var agg = Agg(pool, pid, tag);
                        agg.Alloc += bytes; agg.Allocs++;
                        if (agg.FirstUtc == default) agg.FirstUtc = e.TimeStamp.ToUniversalTime();
                        live[entry] = (pid, tag, bytes);
                        if (e.CallStack() is { } cs)
                        {
                            stacked++;
                            string frame = DriverFrame(cs);
                            agg.Frames[frame] = agg.Frames.GetValueOrDefault(frame) + bytes;
                        }
                    }
                    else
                    {
                        ulong entry = ptr == 8 ? BitConverter.ToUInt64(data, 8) : BitConverter.ToUInt32(data, 8);
                        // Credit the free to whoever made the allocation (a free on another thread is common).
                        if (live.Remove(entry, out var a))
                        {
                            var agg = Agg(pool, a.proc, a.tag);
                            agg.Freed += a.bytes; agg.Frees++;
                        }
                    }
                }
                else if (e.ProviderGuid == KernelFile && (int)e.ID is FileCreate or FileDeletePath)
                {
                    fileEvents++;
                    // Create carries FileName; DeletePath carries FilePath.
                    string? name = (e.PayloadByName("FileName") ?? e.PayloadByName("FilePath")) as string;
                    if (string.IsNullOrEmpty(name)) continue;
                    string path = Collectors.DevicePaths.ToDosPath(name);
                    string volume = path.Length >= 2 && path[1] == ':' ? path[..2].ToUpperInvariant() : "?";
                    var key = (e.ProcessID, volume);
                    if (!files.TryGetValue(key, out var fa)) files[key] = fa = new FileAgg();
                    if ((int)e.ID == FileDeletePath) { fa.Deletes++; continue; }
                    fa.Creates++;
                    bool dir = e.PayloadByName("CreateOptions") is { } co && (Convert.ToUInt32(co) & FILE_DIRECTORY_FILE) != 0;
                    if (dir) fa.Dirs++;
                    string? parent = dir ? path : Path.GetDirectoryName(path);
                    if (parent is not null && fa.Parents.Count < 50_000) fa.Parents[parent] = fa.Parents.GetValueOrDefault(parent) + 1;
                }
            }
            // While the trace log is open: its code addresses name the kernel functions (addendum 2 C).
            cpuProfile = cpu.Result(endUtc - startUtc, traceLog, symbolPath);
        }
        }
        finally { try { File.Delete(checkWrite(etlx)); } catch { } } // can be large; never left behind (and never through a link)

        bool parsed = poolEvents > 0;
        bool truncated = seen >= MaxEvents;
        if (truncated) notes.Add($"The trace was cut at {MaxEvents:N0} events; later events were not parsed.");
        if (parsed && pool.Count == 0) notes.Add("Pool events were read, but none for the alerting tag(s) during the trace.");
        if (!parsed) notes.Add("No kernel pool events were found in the trace (the pool-tag filter or the PERF_POOL group may be unsupported here).");
        else if (stacked == 0) notes.Add("Pool events carried no stacks; driver frames are unavailable.");
        notes.Add("File-system allocations made on worker threads show as System (PID 4); the file activity below ties them back to the requesting process.");
        var usage = pool.Select(kv =>
            {
                // An event without a known process (index -1) is credited to System, as the kernel does.
                var pr = procs.TryGetValue(kv.Key.proc, out var x) ? x : (pid: 4, createTime: 0L, commandLine: (string?)null, parentPid: 0, image: "System", exit: (DateTime?)null);
                return new CapturePoolUsage(pr.pid, pr.image, new PoolTag(kv.Key.tag),
                    kv.Value.Alloc, kv.Value.Freed, kv.Value.Allocs, kv.Value.Frees,
                    kv.Value.Frames.OrderByDescending(f => f.Value).Take(5).Select(f => new CaptureFrame(f.Key, f.Value)).ToList(),
                    pr.createTime, pr.commandLine, pr.parentPid, kv.Value.FirstUtc, pr.exit);
            })
            // Every process instance (a few hundred short commands each holding ~700 KB is the finding), capped.
            .OrderByDescending(u => u.NetBytes).Take(2000).ToList();
        var activity = files.Select(kv => new CaptureFileActivity(kv.Key.pid, images.GetValueOrDefault(kv.Key.pid, $"PID {kv.Key.pid}"), kv.Key.volume,
                kv.Value.Creates, kv.Value.Dirs, kv.Value.Deletes,
                kv.Value.Parents.OrderByDescending(p => p.Value).Take(10).Select(p => new CaptureDirectory(p.Key, p.Value)).ToList()))
            .OrderByDescending(f => f.Creates + f.Deletes).Take(50).ToList();
        log($"capture parsed: {poolEvents} pool events ({stacked} with stacks), {fileEvents} file events");
        return new CaptureResult
        {
            StartUtc = startUtc, EndUtc = endUtc, EtlPath = etlPath, Tags = tags.ToList(), PoolEventsParsed = parsed,
            Library = "Microsoft.Diagnostics.Tracing.TraceEvent (TraceLog, raw PoolGuid events)",
            PoolEvents = poolEvents, StackedPoolEvents = stacked, FileEvents = fileEvents, Pool = usage, Files = activity, Notes = notes, Cpu = cpuProfile,
        };
    }

    /// <summary>
    /// CPU sampling, DPC / ISR and context switches, by kernel module (addendum 2 C). Addresses are resolved to modules
    /// after the pass (the kernel's image rundown comes at the end of a file-mode trace). Bounded dictionaries.
    /// </summary>
    private sealed class CpuAgg
    {
        private readonly Dictionary<ulong, long> _ip = new();
        private readonly Dictionary<ulong, double> _dpc = new();
        private readonly Dictionary<int, double> _out = new();
        private readonly Dictionary<string, (double ms, long n)> _wait = new(StringComparer.OrdinalIgnoreCase);
        // Addendum 2 C, functions: kernel-mode samples and wait time by code address (named after the pass, with symbols when local ones exist).
        private readonly Dictionary<CodeAddressIndex, long> _kernelIp = new();
        private readonly Dictionary<CodeAddressIndex, double> _waitAt = new();
        private readonly List<(ulong start, ulong end, string name)> _modules = new();
        private long _samples, _switches;
        private int _cpus;

        public bool Observe(TraceEvent e)
        {
            switch (e)
            {
                case SampledProfileTraceData sp:
                    _samples++;
                    _cpus = Math.Max(_cpus, sp.ProcessorNumber + 1);
                    // Idle: the idle thread, unless it was running a DPC or an interrupt (those count against their routine's module).
                    if ((sp.ThreadID == 0 || sp.ProcessID == 0) && !sp.ExecutingDPC && !sp.ExecutingISR) { _ip[0] = _ip.GetValueOrDefault(0UL) + 1; return true; }
                    if (_ip.Count < 200_000 || _ip.ContainsKey(sp.InstructionPointer)) _ip[sp.InstructionPointer] = _ip.GetValueOrDefault(sp.InstructionPointer) + 1;
                    if (sp.InstructionPointer >= 0xFFFF800000000000UL)
                    {
                        var ci = sp.IntructionPointerCodeAddressIndex();
                        if (ci != CodeAddressIndex.Invalid && (_kernelIp.Count < 100_000 || _kernelIp.ContainsKey(ci))) _kernelIp[ci] = _kernelIp.GetValueOrDefault(ci) + 1;
                    }
                    return true;
                case DPCTraceData d:
                    if (_dpc.Count < 50_000 || _dpc.ContainsKey(d.Routine)) _dpc[d.Routine] = _dpc.GetValueOrDefault(d.Routine) + Math.Max(0, d.ElapsedTimeMSec);
                    return true;
                case ISRTraceData i:
                    if (_dpc.Count < 50_000 || _dpc.ContainsKey(i.Routine)) _dpc[i.Routine] = _dpc.GetValueOrDefault(i.Routine) + Math.Max(0, i.ElapsedTimeMSec);
                    return true;
                case CSwitchTraceData cs:
                    _switches++;
                    double t = cs.TimeStampRelativeMSec;
                    if (_out.Remove(cs.NewThreadID, out var since) && cs.NewThreadID != 0)
                    {
                        // The resuming thread's stack shows where it waited (WPA's wait analysis reads it the same way).
                        var (module, at) = cs.CallStack() is { } st ? WaitFrame(st) : ("?", CodeAddressIndex.Invalid);
                        var w = _wait.GetValueOrDefault(module);
                        // One wait counts at most 1 s: a thread parked for the whole trace says nothing about lag.
                        double ms = Math.Min(1000, t - since);
                        if (_wait.Count < 2000 || _wait.ContainsKey(module)) _wait[module] = (w.ms + ms, w.n + 1);
                        if (at != CodeAddressIndex.Invalid && (_waitAt.Count < 50_000 || _waitAt.ContainsKey(at))) _waitAt[at] = _waitAt.GetValueOrDefault(at) + ms;
                    }
                    if (cs.OldThreadID != 0 && _out.Count < 200_000) _out[cs.OldThreadID] = t;
                    return true;
                case ImageLoadTraceData il when il.ImageBase >= 0xFFFF800000000000UL:
                    _modules.Add((il.ImageBase, il.ImageBase + (ulong)il.ImageSize, Path.GetFileName(il.FileName)));
                    return false; // the Loader keyword's own use (if any) continues
                default:
                    return false;
            }
        }

        /// <summary>
        /// Kernel functions by CPU and by wait (addendum 2 C). With a local symbol folder the top modules' symbols are
        /// looked up (cache only: the reader never contacts a server) and functions are named; otherwise, or for a module
        /// with no symbols there, a function is module + offset and the note says so.
        /// </summary>
        private (List<CpuFunction> cpu, List<CpuFunction> wait, string note) Functions(TraceLog log, string? symbolPath)
        {
            var addrs = log.CodeAddresses;
            string? error = null;
            bool any = false;
            if (symbolPath is not null && (_kernelIp.Count > 0 || _waitAt.Count > 0))
            {
                try
                {
                    using var reader = new Microsoft.Diagnostics.Symbols.SymbolReader(TextWriter.Null, symbolPath, null)
                    {
                        Options = Microsoft.Diagnostics.Symbols.SymbolReaderOptions.CacheOnly,
                        // Only PDBs inside the admin-only folders on the path are accepted: one found by probing elsewhere
                        // (next to the image, or at the build path recorded in it) is refused.
                        SecurityCheck = p => InsideAny(p, symbolPath),
                    };
                    // Only the modules that matter: the eight that hold most of the kernel samples and waits.
                    var modules = _kernelIp.Select(kv => (idx: kv.Key, w: (double)kv.Value)).Concat(_waitAt.Select(kv => (idx: kv.Key, w: kv.Value)))
                        .GroupBy(x => addrs.ModuleFileIndex(x.idx)).Where(g => g.Key != ModuleFileIndex.Invalid)
                        .OrderByDescending(g => g.Sum(x => x.w)).Take(8).Select(g => g.Key).ToList();
                    foreach (var mi in modules) addrs.LookupSymbolsForModule(reader, log.ModuleFiles[mi]);
                }
                catch (Exception ex) { error = ex.Message; }
            }
            var names = new Dictionary<CodeAddressIndex, string>();
            string Name(CodeAddressIndex i)
            {
                if (names.TryGetValue(i, out var n)) return n;
                var mf = addrs.ModuleFile(i);
                string module = mf is null ? "kernel (unknown module)" : Path.GetFileName(mf.FilePath) is { Length: > 0 } f ? f : mf.Name;
                var m = addrs.MethodIndex(i);
                string? fn = m != MethodIndex.Invalid ? addrs.Methods.FullMethodName(m) : null;
                if (!string.IsNullOrEmpty(fn)) any = true;
                ulong address = addrs.Address(i);
                return names[i] = CpuProfile.FrameName(module, fn, mf is not null && address >= mf.ImageBase ? address - mf.ImageBase : address);
            }
            long all = Math.Max(1, _samples);
            var cpu = _kernelIp.GroupBy(kv => Name(kv.Key)).Select(g => (name: g.Key, n: g.Sum(x => x.Value))).OrderByDescending(x => x.n).Take(15)
                .Select(x => new CpuFunction(x.name, x.n, x.n / (double)all, x.name.Contains('!'))).ToList();
            double waitAll = Math.Max(1, _waitAt.Values.Sum());
            var wait = _waitAt.GroupBy(kv => Name(kv.Key)).Select(g => (name: g.Key, ms: g.Sum(x => x.Value))).OrderByDescending(x => x.ms).Take(15)
                .Select(x => new CpuFunction(x.name, x.ms, x.ms / waitAll, x.name.Contains('!'))).ToList();
            return (cpu, wait, CpuProfile.NoteFor(symbolPath, any, error));
        }

        /// <summary>The file is inside one of the ';'-separated (already admin-only) folders.</summary>
        private static bool InsideAny(string? file, string folders)
        {
            if (string.IsNullOrEmpty(file)) return false;
            string full;
            try { full = Path.GetFullPath(file); } catch { return false; }
            foreach (var f in folders.Split(';', StringSplitOptions.RemoveEmptyEntries))
                if (full.StartsWith(Path.GetFullPath(f).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public CpuProfile? Result(TimeSpan span, TraceLog log, string? symbolPath)
        {
            if (_samples == 0 && _switches == 0) return null;
            List<CpuFunction> functions = [], waitFunctions = [];
            string note;
            try { (functions, waitFunctions, note) = Functions(log, symbolPath); }
            catch (Exception ex) { note = CpuProfile.NoteFor(symbolPath, false, ex.Message); }
            var mods = _modules.DistinctBy(m => m.start).OrderBy(m => m.start).ToList();
            string ModuleOf(ulong a)
            {
                if (a == 0) return "idle";
                if (a < 0xFFFF800000000000UL) return "user mode";
                int lo = 0, hi = mods.Count - 1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) / 2;
                    if (a < mods[mid].start) hi = mid - 1;
                    else if (a >= mods[mid].end) lo = mid + 1;
                    else return mods[mid].name;
                }
                return "kernel (unknown module)";
            }
            var cpu = _ip.GroupBy(kv => ModuleOf(kv.Key)).Select(g => (m: g.Key, n: g.Sum(x => x.Value))).OrderByDescending(x => x.n).ToList();
            long all = Math.Max(1, _samples);
            // WPR samples once per millisecond per processor by default.
            var top = cpu.Take(15).Select(x => new CpuModule(x.m, x.n, x.n / (double)all)).ToList();
            var dpc = _dpc.GroupBy(kv => ModuleOf(kv.Key)).Select(g => (m: g.Key, ms: g.Sum(x => x.Value))).OrderByDescending(x => x.ms).Take(10)
                .Select(x => new CpuModule(x.m, x.ms, 0)).ToList();
            var wait = _wait.OrderByDescending(kv => kv.Value.ms).Take(15).Select(kv => new WaitModule(kv.Key, kv.Value.ms, kv.Value.n)).ToList();
            return new CpuProfile
            {
                Seconds = span.TotalSeconds, Samples = _samples, Processors = _cpus, TopCpu = top, TopDpcIsr = dpc, TopWait = wait, ContextSwitches = _switches,
                TopFunctions = functions, TopWaitFunctions = waitFunctions, FunctionsNote = note,
            };
        }
    }

    private static PoolAgg Agg(Dictionary<(int, uint), PoolAgg> d, int pid, uint tag)
    {
        if (!d.TryGetValue((pid, tag), out var a)) d[(pid, tag)] = a = new PoolAgg();
        return a;
    }

    /// <summary>
    /// The first module outside the kernel's pool allocator that asked for the memory. Symbols are not loaded for pool
    /// stacks, so frames are module names; nt itself counts when nothing else is on the stack.
    /// </summary>
    private static string DriverFrame(TraceCallStack stack) => WaitFrame(stack).module;

    /// <summary>The first kernel frame outside nt (else the first nt frame): its module and its code address (for the function).</summary>
    private static (string module, CodeAddressIndex at) WaitFrame(TraceCallStack stack)
    {
        string? nt = null;
        var ntAt = CodeAddressIndex.Invalid;
        for (var s = stack; s is not null; s = s.Caller)
        {
            if (s.CodeAddress.Address < 0xFFFF800000000000UL) break; // user mode: the kernel part of the stack is over
            string module = s.CodeAddress.ModuleName ?? "?";
            if (module.Equals("ntoskrnl", StringComparison.OrdinalIgnoreCase) || module.Equals("ntkrnlmp", StringComparison.OrdinalIgnoreCase))
            {
                if (nt is null) { nt = module; ntAt = s.CodeAddress.CodeAddressIndex; }
                continue;
            }
            return (module, s.CodeAddress.CodeAddressIndex);
        }
        return (nt ?? "?", ntAt);
    }
}
