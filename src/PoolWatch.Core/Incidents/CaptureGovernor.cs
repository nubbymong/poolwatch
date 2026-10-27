using System.Globalization;
using PoolWatch.Core.Detection;

namespace PoolWatch.Core.Incidents;

/// <summary>What asked for a confirmation trace.</summary>
public enum CaptureSource
{
    /// <summary>The owner pressed Capture now (or ran with --capture-now).</summary>
    Manual,
    /// <summary>A pool-growth incident opened (<see cref="Settings.CaptureOnAlert"/>).</summary>
    PoolAlert,
    /// <summary>The lag probe tripped (a CPU trace).</summary>
    LagTrip,
}

/// <summary>What one trace records: kernel pool events with stacks, Kernel-File (with the pool events), and the separate CPU recording.</summary>
public readonly record struct CaptureScope(bool PoolEvents, bool KernelFile, bool Cpu)
{
    /// <summary>Whether there is anything to record (Kernel-File only rides along with the pool events).</summary>
    public bool Any => PoolEvents || Cpu;

    /// <summary>"pool events only", "pool events + Kernel-File + CPU", "CPU only".</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (PoolEvents) parts.Add("pool events");
        if (PoolEvents && KernelFile) parts.Add("Kernel-File");
        if (Cpu) parts.Add("CPU");
        return parts.Count switch { 0 => "nothing", 1 => parts[0] + " only", _ => string.Join(" + ", parts) };
    }
}

/// <summary>The moment's conditions, read by the caller (the governor is pure).</summary>
/// <param name="CaptureRunning">A trace is recording or being parsed.</param>
/// <param name="Cores">Logical processors.</param>
/// <param name="RunQueue">Threads waiting for a core; null when there is no reading.</param>
/// <param name="CpuFraction">Total CPU busy, 0..1 over all cores; null when there is no reading.</param>
/// <param name="SinceLastInput">Time since the last keyboard / mouse input in the session; null when unknown.</param>
public readonly record struct CaptureConditions(bool CaptureRunning, int Cores, double? RunQueue = null, double? CpuFraction = null, TimeSpan? SinceLastInput = null);

/// <summary>One request for a trace.</summary>
/// <param name="TimeUtc">When it was asked for (wall clock): for the owner only (timeline, "held back from 11:20:05").</param>
/// <param name="At">When it was asked for on the governor's monotonic clock: what every rule uses (C1 d, review round 2).</param>
/// <param name="Why">For the log and the timeline ("incident 20261026-112000-Reff: 200 MB in 10 min").</param>
public sealed record CaptureRequest(CaptureSource Source, DateTime TimeUtc, TimeSpan At, string Why)
{
    /// <summary>What the caller needs to start it later (a pool alert's incidents). Opaque to the governor.</summary>
    public object? Context { get; init; }
    /// <summary>Why it was held back, once it is the pending request.</summary>
    public string? HeldBack { get; init; }
    public bool Automatic => Source != CaptureSource.Manual;
}

public enum CaptureVerdict
{
    /// <summary>Start the trace now.</summary>
    Start,
    /// <summary>Not now; the owner is told why ("capture skipped: ...").</summary>
    Skipped,
    /// <summary>The settings do not ask for this capture (a reason only when the owner may expect one), or a pending one was dropped.</summary>
    Off,
}

/// <summary>The governor's answer for one request.</summary>
public sealed record CaptureDecision(CaptureVerdict Verdict, CaptureRequest Request, CaptureScope Scope, string? Reason = null)
{
    public bool Start => Verdict == CaptureVerdict.Start;
    /// <summary>A skipped request that is now the pending one (it starts when the PC allows).</summary>
    public bool Kept { get; init; }
    /// <summary>The older pending request this one replaced (the newest wins).</summary>
    public CaptureRequest? Replaced { get; init; }

    /// <summary>The line for the incident timeline / the Lag finding; null when there is nothing to tell.</summary>
    public string? Note => Verdict switch
    {
        CaptureVerdict.Skipped => $"capture skipped: {Reason}{(Kept ? "; kept as the next capture to run when the PC allows" : "")}",
        CaptureVerdict.Off when Reason is not null => $"no capture: {Reason}",
        _ => null,
    };

    /// <summary>What the trace records and why: "pool events only (automatic, on a pool alert; held back from 11:20:05: ...)".</summary>
    public string Summary
    {
        get
        {
            string source = Request.Source switch
            {
                CaptureSource.Manual => "Capture now",
                CaptureSource.PoolAlert => "automatic, on a pool alert",
                _ => "automatic, on a lag trip",
            };
            string held = Request.HeldBack is { } h ? $"; held back from {Fmt.Clock(Request.TimeUtc)}: {h}" : "";
            return $"{Scope.Describe()} ({source}{held})";
        }
    }
}

/// <summary>
/// C1 (review): the one gate for every confirmation trace. A busy many-core PC lagged while
/// traces ran back to back (a queue of alert captures, Capture now always adding a CPU trace, a 30 s CPU trace with stacks
/// on every lag trip). Rules:
/// <list type="bullet">
/// <item>Automatic captures (pool alerts, lag trips): at most one per <see cref="Settings.AutoCaptureMinMinutes"/>, counted
/// from the start AND the end of the last capture of any kind, so one never starts right after another.</item>
/// <item>No queue: a request that cannot start now is dropped, but the newest one is remembered and started when the PC
/// allows (<see cref="TryPending"/>); the caller records "capture skipped: reason" where the owner sees it.</item>
/// <item>Never while the PC is saturated (run queue above the core count, or total CPU above <see cref="BusyCpuFraction"/>)
/// or in use (keyboard / mouse input within <see cref="TypingWindow"/>).</item>
/// <item>Automatic captures record pool events only; Kernel-File and the lag trip's CPU trace need
/// <see cref="Settings.DeeperCapture"/> (and the CPU trace <see cref="Settings.CpuTraceEnabled"/>).</item>
/// <item>Capture now (the owner pressed it) skips the cap, the load and the input checks, but never starts while another
/// capture runs.</item>
/// </list>
/// Pure: the clock and the conditions are the caller's. Not thread-safe: the caller serialises (the history lock).
/// C1 d (review round 2): the clock is monotonic (the app passes Environment.TickCount64), never the wall clock: an NTP
/// step, a DST change or the owner setting the time must neither lift the hourly cap early nor hold a capture back for
/// hours. Requests carry their monotonic time (<see cref="CaptureRequest.At"/>) for ordering and the lag trip's 15 min.
/// </summary>
public sealed class CaptureGovernor
{
    /// <summary>Total CPU above this is "saturated".</summary>
    public const double BusyCpuFraction = 0.85;
    /// <summary>Keyboard / mouse input within this is "the owner is typing".</summary>
    public static readonly TimeSpan TypingWindow = TimeSpan.FromSeconds(10);
    /// <summary>A lag trip's CPU trace is about that lag: a pending one is dropped after this.</summary>
    public static readonly TimeSpan LagTripMaxWait = TimeSpan.FromMinutes(15);

    private readonly Settings _settings;
    private readonly Func<TimeSpan> _clock;

    /// <param name="clock">A monotonic clock (the app: Environment.TickCount64); the requests' <see cref="CaptureRequest.At"/> use it too.</param>
    public CaptureGovernor(Settings settings, Func<TimeSpan> clock)
    {
        _settings = settings;
        _clock = clock;
    }

    /// <summary>The governor's clock now (for the caller's requests).</summary>
    public TimeSpan Now => _clock();
    /// <summary>When the last capture started / ended, on the monotonic clock.</summary>
    public TimeSpan? LastStart { get; private set; }
    public TimeSpan? LastEnd { get; private set; }
    /// <summary>The newest request that was held back, started by <see cref="TryPending"/> when the PC allows.</summary>
    public CaptureRequest? Pending { get; private set; }
    /// <summary>Requests skipped so far (summary.json).</summary>
    public int SkippedCount { get; private set; }
    public TimeSpan MinInterval => TimeSpan.FromMinutes(_settings.AutoCaptureMinMinutes);

    /// <summary>What a capture of this kind records under these settings.</summary>
    public static CaptureScope ScopeFor(CaptureSource source, Settings s) => source switch
    {
        CaptureSource.Manual => new(PoolEvents: true, KernelFile: true, Cpu: s.CpuTraceEnabled),
        CaptureSource.PoolAlert => new(PoolEvents: true, KernelFile: s.DeeperCapture, Cpu: false),
        _ => new(PoolEvents: false, KernelFile: false, Cpu: s.DeeperCapture && s.CpuTraceEnabled),
    };

    /// <summary>A new request: start it, skip it (remembering the newest automatic one), or not at all (settings).</summary>
    public CaptureDecision Decide(CaptureRequest r, CaptureConditions c)
    {
        var scope = ScopeFor(r.Source, _settings);
        if (OffReason(r.Source) is { } off) return new(CaptureVerdict.Off, r, scope, off.Length > 0 ? off : null);
        if (c.CaptureRunning) return r.Automatic ? Hold(r, scope, "another capture is running") : Skip(r, scope, "another capture is running");
        if (r.Automatic && Blocker(c) is { } why) return Hold(r, scope, why);
        return new(CaptureVerdict.Start, r, scope);
    }

    /// <summary>
    /// The pending request when it may start now (a Start decision); null while it has to wait or there is none. A pending
    /// request that is no longer wanted (<paramref name="stillWanted"/> false, a lag trip older than <see cref="LagTripMaxWait"/>,
    /// or its kind turned off) is dropped: an Off decision with the reason.
    /// </summary>
    public CaptureDecision? TryPending(CaptureConditions c, Func<CaptureRequest, bool>? stillWanted = null)
    {
        if (Pending is not { } p) return null;
        var scope = ScopeFor(p.Source, _settings);
        string? dropped = OffReason(p.Source) is not null ? "that kind of capture is now turned off"
            : p.Source == CaptureSource.LagTrip && _clock() - p.At > LagTripMaxWait ? $"the lag trip is more than {LagTripMaxWait.TotalMinutes:0} min old"
            : stillWanted?.Invoke(p) == false ? "no longer needed"
            : null;
        if (dropped is not null)
        {
            Pending = null;
            return new(CaptureVerdict.Off, p, scope, dropped);
        }
        if (c.CaptureRunning || Blocker(c) is not null) return null;
        return new(CaptureVerdict.Start, p, scope);
    }

    /// <summary>
    /// The trace started (the runner accepted it). An automatic start supersedes a pending request that is not newer: the
    /// superseded one is returned when it was a different request (the caller tells its incident), else null.
    /// </summary>
    public CaptureRequest? Started(CaptureRequest r)
    {
        LastStart = _clock();
        if (!r.Automatic || Pending is not { } p || p.At > r.At) return null;
        Pending = null;
        return p.Source == r.Source && p.At == r.At && ReferenceEquals(p.Context, r.Context) ? null : p;
    }

    /// <summary>The trace ended (recorded and parsed, or failed: C1 a, the caller calls this whatever happened).</summary>
    public void Finished() => LastEnd = _clock();

    /// <summary>Why an automatic capture may not start now (cap, back-to-back, load, input); null when it may.</summary>
    public string? Blocker(CaptureConditions c)
    {
        var now = _clock();
        // The later of the last start and the last end: the cap counts from the end too, so a long trace never runs back to back.
        bool ended = LastEnd is { } end && (LastStart is not { } start || end >= start);
        if ((ended ? LastEnd : LastStart) is { } last && now - last < MinInterval)
            return $"at most one automatic capture per {MinInterval.TotalMinutes.ToString("0.#", CultureInfo.InvariantCulture)} min " +
                   $"(the last capture {(ended ? "ended" : "started")} {Fmt.Duration(now - last)} ago)";
        if (Saturation(c) is { } sat) return sat;
        if (c.SinceLastInput is { } idle && idle < TypingWindow)
            return $"the PC is in use (keyboard / mouse input {Math.Max(0, idle.TotalSeconds).ToString("0", CultureInfo.InvariantCulture)} s ago)";
        return null;
    }

    /// <summary>"the PC is saturated (...)" when the run queue is above the core count or total CPU above 85%; else null.</summary>
    public static string? Saturation(CaptureConditions c)
    {
        if (c.RunQueue is { } q && q > c.Cores)
            return $"the PC is saturated ({q.ToString("0", CultureInfo.InvariantCulture)} threads waiting for {c.Cores} cores)";
        if (c.CpuFraction is { } f && f > BusyCpuFraction)
            return $"the PC is saturated (CPU {(f * 100).ToString("0", CultureInfo.InvariantCulture)}%, over {BusyCpuFraction * 100:0}%)";
        return null;
    }

    /// <summary>Null when the settings ask for this capture; "" when they do not (silently); a reason when the owner may expect one.</summary>
    private string? OffReason(CaptureSource source) => source switch
    {
        CaptureSource.PoolAlert when !_settings.CaptureOnAlert => "",
        CaptureSource.LagTrip when !_settings.CpuTraceEnabled => "",
        CaptureSource.LagTrip when !_settings.DeeperCapture => "the CPU trace on a lag trip is a deeper capture (Settings: DeeperCapture is off)",
        _ => null,
    };

    private CaptureDecision Skip(CaptureRequest r, CaptureScope scope, string why)
    {
        SkippedCount++;
        return new(CaptureVerdict.Skipped, r, scope, why);
    }

    /// <summary>Skipped now; kept as the pending request when it is the newest.</summary>
    private CaptureDecision Hold(CaptureRequest r, CaptureScope scope, string why)
    {
        SkippedCount++;
        if (Pending is { } older && older.At > r.At) return new(CaptureVerdict.Skipped, r, scope, why);
        var replaced = Pending;
        Pending = r with { HeldBack = why };
        return new(CaptureVerdict.Skipped, r, scope, why) { Kept = true, Replaced = replaced };
    }
}
