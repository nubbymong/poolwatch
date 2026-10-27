using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using PoolWatch.Core.Lag;
using PoolWatch.Core.Model;

namespace PoolWatch.Collectors;

/// <summary>
/// The responsiveness probe (item 9): every few seconds, on its own normal-priority thread, without starting any
/// process: how late a 15 ms sleep wakes up, a window-message round trip through win32k (PostThreadMessage to the probe
/// thread's own queue, then PeekMessage), creating and deleting a 16-byte file under PoolWatch's own folder, and the
/// compositor's missed / dropped frames. Probes are queued; the sampler thread consumes them.
/// <para>
/// The window-message round trip replaced an earlier OpenClipboard / CloseClipboard round trip. Both are two win32k calls that take the same system-wide USER lock every
/// window message and keystroke goes through, so both slow down with the same win32k contention that makes typing lag;
/// the message round trip also passes through a thread message queue, the path input takes. What the clipboard version
/// did in addition was hold the one system-wide clipboard open every 10 s, so another app's copy could fail in that
/// moment, and when another app held it, OpenClipboard returned at once and the measure meant nothing.
/// </para>
/// </summary>
internal sealed class LagProber : IDisposable
{
    private readonly TimeSpan _interval;
    private readonly string _probeFile;
    private readonly Func<PoolTotals> _pool;
    private readonly ManualResetEventSlim _stop = new();
    private readonly ConcurrentQueue<LagProbe> _queue = new();
    private Thread? _thread;
    private long? _lastDwm;
    private DateTime _dwmRetry = DateTime.MinValue;
    private bool _dwmEverWorked;
    private bool _queueReady;
    private const uint WmProbe = 0x8000 + 0x50; // WM_APP + 0x50, only ever posted to this thread
    private static readonly TimeSpan DwmRetry = TimeSpan.FromMinutes(5);

    private readonly Func<bool>? _paused;
    private readonly Func<string, string> _checkWrite;

    /// <param name="paused">C1: true while PoolWatch's own trace or cache clear runs and for 30 s after; no probe is taken then.</param>
    /// <param name="checkWrite">Review round 2 (S3): the write policy (PathGuard.CheckReal) before each probe file write and delete.</param>
    public LagProber(TimeSpan interval, string probeFile, Func<PoolTotals> pool, Func<bool>? paused = null, Func<string, string>? checkWrite = null)
    {
        _interval = interval;
        _probeFile = probeFile;
        _pool = pool;
        _paused = paused;
        _checkWrite = checkWrite ?? (p => p);
    }

    public bool DwmAvailable { get; private set; }
    /// <summary>Whether the last scheduled probe was skipped because PoolWatch's own work was running (C1).</summary>
    public bool Paused { get; private set; }
    /// <summary>Probes skipped for PoolWatch's own work since start.</summary>
    public long PausedProbes => Interlocked.Read(ref _pausedProbes);
    private long _pausedProbes;

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "PoolWatch lag probe", Priority = ThreadPriority.Normal };
        _thread.Start();
    }

    public List<LagProbe> Drain()
    {
        var list = new List<LagProbe>();
        while (_queue.TryDequeue(out var p)) list.Add(p);
        return list;
    }

    private void Loop()
    {
        // C3 (review): schedule and measure lateness on a monotonic clock (Stopwatch), not the wall clock. The wall
        // clock jumps on an NTP step, a DST change or (far larger) a sleep/resume, each of which looked like a huge
        // wake-up lateness and raised a false lag alert. The monotonic clock may pause with the machine, and the gap check
        // below (Core.Lag.ResumeGap: monotonic lateness or wall time) catches a resume either way.
        var clock = Stopwatch.StartNew();
        TimeSpan next = _interval;
        DateTime? lastWall = null;
        while (!_stop.Wait(Max(TimeSpan.Zero, next - clock.Elapsed)))
        {
            // How late this wake-up itself was: on a starved PC the probe thread does not run at all until the load
            // drops (the CI burn showed no probe for 40 s), so its own schedule is the first lag measure.
            TimeSpan nowElapsed = clock.Elapsed;
            double late = (nowElapsed - next).TotalMilliseconds;
            next = nowElapsed + _interval;
            var wallNow = DateTime.UtcNow;
            bool resumed = ResumeGap.Detected(TimeSpan.FromMilliseconds(late), wallNow - lastWall, _interval);
            lastWall = wallNow;
            if (resumed)
            {
                // Resumed from sleep: the gap is not lag, and (C3, review round 2) neither are the frames the compositor
                // counted as missed while the PC slept: the next DWM read re-bases its counter instead of adding them up.
                late = 0;
                _lastDwm = null;
            }
            // C1: PoolWatch's own trace or cache clear (and 30 s after): lag it causes itself is not a pool lag onset.
            bool paused = false;
            try { paused = _paused?.Invoke() == true; } catch { }
            Paused = paused;
            if (paused)
            {
                _lastDwm = null; // the next probe re-bases the compositor's counters: frames missed during the pause are not counted
                Interlocked.Increment(ref _pausedProbes);
                continue;
            }
            try { _queue.Enqueue(Probe(late)); }
            catch { /* one failed probe is skipped */ }
            while (_queue.Count > 100) _queue.TryDequeue(out _);
        }
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    /// <param name="lateMs">How late the probe's own wake-up was (one 15.6 ms timer tick of it is normal).</param>
    public LagProbe Probe(double lateMs = 0)
    {
        var sw = Stopwatch.StartNew();
        Thread.Sleep(15);
        double timer = sw.Elapsed.TotalMilliseconds + Math.Max(0, lateMs - 16);

        double? message = null;
        if (!_queueReady) { PeekMessage(out _, IntPtr.Zero, 0, 0, 0); _queueReady = true; } // creates this thread's message queue
        sw.Restart();
        if (PostThreadMessage(GetCurrentThreadId(), WmProbe, IntPtr.Zero, IntPtr.Zero) && PeekMessage(out _, IntPtr.Zero, WmProbe, WmProbe, 1 /* PM_REMOVE */))
            message = sw.Elapsed.TotalMilliseconds;

        // The link check runs before the timed part (it is not what is measured); a refused path skips the file measure.
        string? probeFile = null;
        try { probeFile = _checkWrite(_probeFile); } catch { }
        sw.Restart();
        try
        {
            if (probeFile is not null)
            {
                File.WriteAllBytes(probeFile, new byte[16]);
                File.Delete(probeFile);
            }
        }
        catch { }
        double file = probeFile is null ? 0 : sw.Elapsed.TotalMilliseconds;

        long? missed = null;
        var now = DateTime.UtcNow;
        if (now >= _dwmRetry)
        {
            var info = new DWM_TIMING_INFO { cbSize = (uint)Marshal.SizeOf<DWM_TIMING_INFO>() };
            if (DwmGetCompositionTimingInfo(IntPtr.Zero, ref info) == 0)
            {
                long total = (long)(info.cFramesMissed + info.cFramesDropped);
                if (_lastDwm is { } last) missed = Math.Max(0, total - last);
                _lastDwm = total;
                DwmAvailable = _dwmEverWorked = true;
            }
            else
            {
                // Locked workstation, RDP disconnect, DWM restart: skip the measure and try again in a few minutes.
                // No compositor at all (service session, Server Core) reports unavailable from the first probe on.
                DwmAvailable = _dwmEverWorked;
                _lastDwm = null;
                _dwmRetry = now + DwmRetry;
            }
        }
        return new LagProbe(now, timer, message, file, missed, _pool());
    }

    public void Dispose()
    {
        _stop.Set();
        _thread?.Join(2000);
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint filterMin, uint filterMax, uint remove);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public int ptX, ptY;
        public uint lPrivate;
    }
    [DllImport("dwmapi.dll")] private static extern int DwmGetCompositionTimingInfo(IntPtr hwnd, ref DWM_TIMING_INFO info);

    /// <summary>dwmapi.h DWM_TIMING_INFO (pack 1).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct DWM_TIMING_INFO
    {
        public uint cbSize;
        public uint rateRefreshNum, rateRefreshDen;
        public ulong qpcRefreshPeriod;
        public uint rateComposeNum, rateComposeDen;
        public ulong qpcVBlank, cRefresh;
        public uint cDXRefresh;
        public ulong qpcCompose, cFrame;
        public uint cDXPresent;
        public ulong cRefreshFrame, cFrameSubmitted;
        public uint cDXPresentSubmitted;
        public ulong cFrameConfirmed;
        public uint cDXPresentConfirmed;
        public ulong cRefreshConfirmed;
        public uint cDXRefreshConfirmed;
        public ulong cFramesLate;
        public uint cFramesOutstanding;
        public ulong cFrameDisplayed, qpcFrameDisplayed, cRefreshFrameDisplayed, cFrameComplete, qpcFrameComplete, cFramePending, qpcFramePending,
            cFramesDisplayed, cFramesComplete, cFramesPending, cFramesAvailable, cFramesDropped, cFramesMissed, cRefreshNextDisplayed,
            cRefreshNextPresented, cRefreshesDisplayed, cRefreshesPresented, cRefreshStarted, cPixelsReceived, cPixelsDrawn, cBuffersEmpty;
    }
}
