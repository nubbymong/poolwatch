using System.Runtime.InteropServices;
using PoolWatch.Core.Services;

namespace PoolWatch.Native;

/// <summary>One reading of the system-wide load counters; a field is null when its counter has no reading.</summary>
internal readonly record struct SystemLoadReading(double? RunQueue, FaultSplit? Faults);

/// <summary>
/// System-wide load counters (addendum 4 A) from the in-box performance counters (PDH, English names, locale-independent;
/// no WMI), one query collected once per sample: the processor queue length (threads ready to run but waiting for a core)
/// and the page-fault split per second: demand-zero (fresh zeroed pages: what one vendor telemetry service did 887k times a second),
/// transition (pages taken back from the standby / modified lists) and pages input (read from disk to resolve hard faults).
/// The rate counters need two collections: the first sample has no fault split.
/// </summary>
internal static class SystemLoadNative
{
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhOpenQuery(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern int PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] private static extern int PdhGetFormattedCounterValue(IntPtr counter, int format, IntPtr type, out PDH_FMT_COUNTERVALUE value);

    [StructLayout(LayoutKind.Explicit)]
    private struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)] public int CStatus;
        [FieldOffset(8)] public long LargeValue;
        [FieldOffset(8)] public double DoubleValue;
    }

    private const int PDH_FMT_LARGE = 0x400, PDH_FMT_DOUBLE = 0x200, PDH_FMT_NOCAP100 = 0x8000;

    private static IntPtr _query, _queue, _demandZero, _transition, _pagesInput;
    private static bool _tried, _ok, _faultsOk, _primed;
    private static readonly object Lock = new();

    /// <summary>Current run queue and fault split (each null when unavailable or before its second collection).</summary>
    public static SystemLoadReading Read()
    {
        lock (Lock)
        {
            if (!_tried)
            {
                _tried = true;
                _ok = PdhOpenQuery(null, IntPtr.Zero, out _query) == 0
                      && PdhAddEnglishCounter(_query, @"\System\Processor Queue Length", IntPtr.Zero, out _queue) == 0;
                _faultsOk = _ok
                      && PdhAddEnglishCounter(_query, @"\Memory\Demand Zero Faults/sec", IntPtr.Zero, out _demandZero) == 0
                      && PdhAddEnglishCounter(_query, @"\Memory\Transition Faults/sec", IntPtr.Zero, out _transition) == 0
                      && PdhAddEnglishCounter(_query, @"\Memory\Pages Input/sec", IntPtr.Zero, out _pagesInput) == 0;
                if (_ok) PdhCollectQueryData(_query); // the first collect primes the counters (rates need two)
            }
            if (!_ok || PdhCollectQueryData(_query) != 0) return new SystemLoadReading(null, null);
            double? queue = PdhGetFormattedCounterValue(_queue, PDH_FMT_LARGE, IntPtr.Zero, out var q) == 0 ? q.LargeValue : null;
            FaultSplit? faults = null;
            // The first call collected twice within microseconds (prime, then read): its rates mean nothing. From the next
            // call on, a rate covers the time between two samples.
            bool rated = _primed;
            _primed = true;
            if (rated && _faultsOk && Rate(_demandZero) is { } dz && Rate(_transition) is { } tr && Rate(_pagesInput) is { } pi)
                faults = new FaultSplit(dz, tr, pi);
            return new SystemLoadReading(queue, faults);
        }
    }

    private static double? Rate(IntPtr counter) =>
        PdhGetFormattedCounterValue(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, IntPtr.Zero, out var v) == 0 ? Math.Max(0, v.DoubleValue) : null;
}
