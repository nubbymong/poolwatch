using PoolWatch.Core.Model;

namespace PoolWatch.Analysis;

/// <summary>
/// When the sampler's checks run (<see cref="Diagnostics"/>): the version check every
/// <see cref="Core.Settings.VersionCheckMinutes"/> (the first at the first sample); the crash check at the first sample, 5
/// minutes later, 10 minutes after that and then every <see cref="Core.Settings.VersionCheckMinutes"/> (a log that filled
/// its page has its next page read a minute later, <see cref="CrashPageFull"/>); the stray .git check every
/// <see cref="Core.Settings.StrayGitCheckMinutes"/>; the filter manager's services every
/// <see cref="Core.Settings.ServiceIntervalSeconds"/> and its instances every hour or when an incident asks. Each is timed on
/// the clock that only goes forward, read from the sample itself (<see cref="Sample.TickMs"/>), so setting the system clock
/// back never holds a check back until it catches up, nor does setting it forward bring one early. Sampler thread only.
/// </summary>
internal sealed class CheckSchedule
{
    private readonly Every _version = new(TimeSpan.FromMinutes(60)), _crash = new(TimeSpan.FromMinutes(60)), _stray = new(TimeSpan.FromMinutes(60)),
        _filterServices = new(TimeSpan.FromSeconds(60)), _filterInstances = new(TimeSpan.FromMinutes(60));
    private int _crashChecks;

    /// <summary>The checks due at one sample.</summary>
    internal readonly record struct Due(bool Version, bool Crash, bool StrayGit, bool FilterServices, bool FilterInstances);

    /// <summary>
    /// The checks due at <paramref name="s"/>. The version, crash and stray .git checks due are counted as done at it; the
    /// filter manager's services and instances only when they are read (<see cref="ReadFilters"/>, the only way to count
    /// them as read), so a sample that fails before it reads them leaves them due at the next.
    /// </summary>
    /// <param name="instancesAsked">An incident opened and its instances were not read since: they are read at this sample.</param>
    public Due At(Sample s, Core.Settings settings, bool instancesAsked)
    {
        var at = Moment.Of(s);
        _version.Interval = TimeSpan.FromMinutes(settings.VersionCheckMinutes);
        bool version = _version.Due(at);
        bool crash = _crash.IsDue(at);
        if (crash)
        {
            _crashChecks++;
            _crash.NextIn(at, _crashChecks == 1 ? TimeSpan.FromMinutes(5) : _crashChecks == 2 ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(settings.VersionCheckMinutes));
        }
        _stray.Interval = TimeSpan.FromMinutes(settings.StrayGitCheckMinutes);
        bool stray = _stray.Due(at);
        _filterServices.Interval = TimeSpan.FromSeconds(settings.ServiceIntervalSeconds);
        bool services = _filterServices.IsDue(at);
        bool instances = _filterInstances.IsDue(at) || instancesAsked;
        return new Due(version, crash, stray, services, instances);
    }

    /// <summary>
    /// The filter manager's services are read at <paramref name="s"/>: next due their interval later. Private, so they are
    /// counted as read only where <see cref="ReadFilters"/> reads them.
    /// </summary>
    private void FilterServicesRead(Sample s) => _filterServices.NextIn(Moment.Of(s), _filterServices.Interval);

    /// <summary>
    /// The filter instances are read at <paramref name="s"/>: next due an hour later (or when an incident asks). Private, so
    /// they are counted as read only where <see cref="ReadFilters"/> reads them.
    /// </summary>
    private void FilterInstancesRead(Sample s) => _filterInstances.NextIn(Moment.Of(s), TimeSpan.FromMinutes(60));

    /// <summary>
    /// The filter timeline's part of sample <paramref name="s"/>, in its order: first <paramref name="timeline"/> (the
    /// event-log records and the lifecycle events), then the services and the instances <paramref name="due"/> says are due,
    /// each counted as read (<see cref="FilterServicesRead"/>, <see cref="FilterInstancesRead"/>) just before
    /// <paramref name="services"/> or <paramref name="instances"/> reads it. So a timeline that throws leaves both due at the
    /// next sample, and a query that throws waits for its next time.
    /// </summary>
    public void ReadFilters(Sample s, Due due, Action timeline, Action services, Action instances)
    {
        timeline();
        if (due.FilterServices)
        {
            FilterServicesRead(s);
            services();
        }
        if (due.FilterInstances)
        {
            FilterInstancesRead(s);
            instances();
        }
    }

    /// <summary>The crash check at <paramref name="s"/> read a full page: the next page is read a minute later, not at the next check.</summary>
    public void CrashPageFull(Sample s)
    {
        var at = Moment.Of(s);
        if (_crash.NextMs > at.Ms + 60_000) _crash.NextIn(at, TimeSpan.FromMinutes(1));
    }
}
