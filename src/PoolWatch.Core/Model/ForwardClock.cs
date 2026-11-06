namespace PoolWatch.Core.Model;

/// <summary>
/// A value made by <c>make</c> and kept for <see cref="MaxAge"/>, its age timed on the clock that only goes forward
/// (<see cref="Moment.Ms"/>, read from <c>clock</c>; <see cref="Moment.Now"/> when none is given): setting the system clock
/// back or forward neither keeps it past its age nor makes it again early. One thread at a time: its users lock it, or use
/// it from one thread.
/// </summary>
public sealed class Kept<T>(TimeSpan maxAge, Func<T> make, Func<Moment>? clock = null)
{
    private readonly Func<Moment> _clock = clock ?? Moment.Now;
    private T _value = default!;
    private long? _madeMs;

    /// <summary>How long a value is kept before it is made again.</summary>
    public TimeSpan MaxAge { get; } = maxAge;

    /// <summary>The value, made first when there is none yet or the one kept is <see cref="MaxAge"/> old.</summary>
    public T Value
    {
        get
        {
            long now = _clock().Ms;
            if (_madeMs is not { } made || now - made >= (long)MaxAge.TotalMilliseconds)
            {
                _value = make();
                _madeMs = now;
            }
            return _value;
        }
    }
}

/// <summary>
/// Values made by key, each kept for <see cref="MaxAge"/>, their ages timed on the clock that only goes forward
/// (<see cref="Moment.Ms"/>, read from <c>clock</c>; <see cref="Moment.Now"/> when none is given): setting the system clock
/// back or forward neither keeps one past its age nor makes it again early. Past <c>max</c> values all are let go and made
/// again as they are asked for. One thread at a time.
/// </summary>
public sealed class KeptBy<TKey, TValue>(TimeSpan maxAge, int max, Func<Moment>? clock = null) where TKey : notnull
{
    private readonly Func<Moment> _clock = clock ?? Moment.Now;
    private readonly Dictionary<TKey, (TValue value, long untilMs)> _kept = new();

    /// <summary>How long a value is kept before it is made again.</summary>
    public TimeSpan MaxAge { get; } = maxAge;

    /// <summary>The value of <paramref name="key"/>, made first (<paramref name="make"/>) when none is kept or the one kept is <see cref="MaxAge"/> old.</summary>
    public TValue Get(TKey key, Func<TKey, TValue> make)
    {
        long now = _clock().Ms;
        if (_kept.TryGetValue(key, out var kept) && kept.untilMs > now) return kept.value;
        var value = make(key);
        if (_kept.Count > max) _kept.Clear();
        _kept[key] = (value, now + (long)MaxAge.TotalMilliseconds);
        return value;
    }
}

/// <summary>
/// Something done at most once every <see cref="Interval"/>, timed on the clock that only goes forward
/// (<see cref="Moment.Ms"/> of the moment it is asked at): setting the system clock back never holds it back until the
/// clock catches up, nor does setting it forward bring it early. One thread at a time.
/// </summary>
public sealed class Every(TimeSpan interval)
{
    private long? _nextMs;

    /// <summary>How long after it is done it is due again (a caller may change it; it applies from the next time it is done).</summary>
    public TimeSpan Interval { get; set; } = interval;

    /// <summary>When it is next due (<see cref="Moment.Ms"/>); null: at the next time it is asked.</summary>
    public long? NextMs => _nextMs;

    /// <summary>Whether it is due at <paramref name="now"/>, without doing it.</summary>
    public bool IsDue(Moment now) => _nextMs is not { } next || now.Ms >= next;

    /// <summary>Whether it is due at <paramref name="now"/>; when it is, it is done now and next due <see cref="Interval"/> later.</summary>
    public bool Due(Moment now)
    {
        if (!IsDue(now)) return false;
        _nextMs = now.Ms + (long)Interval.TotalMilliseconds;
        return true;
    }

    /// <summary><see cref="Due"/> at sample <paramref name="s"/>, on its own moment (<see cref="Moment.Of(Sample)"/>).</summary>
    public bool DueAt(Sample s) => Due(Moment.Of(s));

    /// <summary>Makes it next due <paramref name="after"/> from <paramref name="now"/>, sooner or later than its interval.</summary>
    public void NextIn(Moment now, TimeSpan after) => _nextMs = now.Ms + (long)after.TotalMilliseconds;
}

/// <summary>The end of a wait, on the clock that only goes forward (<see cref="Moment.Ms"/>).</summary>
public readonly record struct Deadline(long Ms)
{
    /// <summary>The deadline <paramref name="wait"/> after <paramref name="now"/>; <see cref="TimeSpan.MaxValue"/>: none.</summary>
    public static Deadline After(Moment now, TimeSpan wait) =>
        new(wait == TimeSpan.MaxValue ? long.MaxValue : now.Ms + (long)wait.TotalMilliseconds);

    /// <summary>Whether the wait is over at <paramref name="now"/>.</summary>
    public bool Passed(Moment now) => now.Ms >= Ms;
}

/// <summary>Bounded waits, timed on the clock that only goes forward.</summary>
public static class Waits
{
    /// <summary>
    /// Waits while <paramref name="busy"/> holds, for at most <paramref name="timeout"/> by the clock that only goes forward
    /// (<see cref="Moment.Ms"/> read from <paramref name="clock"/>), sleeping <paramref name="poll"/> between looks
    /// (<paramref name="sleep"/>; Thread.Sleep when none is given). True when it ended because <paramref name="busy"/> no
    /// longer held.
    /// </summary>
    public static bool While(Func<bool> busy, TimeSpan timeout, TimeSpan poll, Func<Moment>? clock = null, Action<TimeSpan>? sleep = null)
    {
        clock ??= Moment.Now;
        sleep ??= Thread.Sleep;
        var end = Deadline.After(clock(), timeout);
        while (busy())
        {
            if (end.Passed(clock())) return false;
            sleep(poll);
        }
        return true;
    }
}
