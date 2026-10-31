using PoolWatch.Core.Services;

namespace PoolWatch.Engine;

/// <summary>
/// The profile applied when the tray app starts (at sign-in, by the logon task): what was left out of the profiles when
/// settings.json was read goes to the log; then, after the chosen delay, the profile chosen to apply at start goes
/// through <see cref="ServicesController.ApplyProfile"/>, Apply's own path, on a worker thread. It asks nothing; a
/// profile that is not there or cannot be read is not applied, and the log says so. Nothing runs once PoolWatch exits.
/// </summary>
internal static class ProfileAtStart
{
    /// <summary>The <see cref="Core.Services.ServiceChange.Via"/> of a change a profile made at start.</summary>
    public static string Via(string profile) => $"profile {profile}, at start";

    /// <summary>The profile to apply at start, or null; with the line for the log when one was chosen but is not there.</summary>
    public static (ServiceProfile? Profile, string? Why) Pick(ServiceProfileSet set)
    {
        if (set.AtStart is not { Length: > 0 } name) return (null, null);
        return set.Find(name) is { } p ? (p, null)
            : (null, $"Service profile {ServiceProfileRules.Shown(name)} is chosen to apply at start, but settings.json holds no usable profile of that name: nothing is applied at start.");
    }

    /// <summary>
    /// The tray app's start (UiRunner): the profile chosen in the running settings, after its delay, through
    /// <paramref name="services"/>' own ApplyProfile. Not in the CI UI test (it changes no service); not with --config (a
    /// file read as written, which may be one anyone can write: only PoolWatch's own settings.json applies a profile at
    /// start); and not without administrator rights (no original could be recorded, so every service would fail). Each of
    /// those says so in the log when a profile is chosen.
    /// </summary>
    /// <param name="done">After a run: the profile and what it did (the tray's notice).</param>
    public static Task ForApp(LiveSettings live, ServicesController services, Action<string> log, bool smoke, bool explicitConfig, bool elevated,
        CancellationToken stop, Action<string, ServicesController.ProfileRun>? done = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        if (smoke) return Task.CompletedTask;
        var set = live.Running.ServiceProfiles;
        if (set.AtStart is { Length: > 0 } name && (explicitConfig || !elevated))
        {
            log(explicitConfig
                ? $"Service profile {ServiceProfileRules.Shown(name)} is not applied at start: settings given with --config never apply a profile at start; only PoolWatch's own settings.json does."
                : $"Service profile {ServiceProfileRules.Shown(name)} is not applied at start: PoolWatch is not running as administrator, so it could not record how the services were set first.");
            return Task.CompletedTask;
        }
        return Start(set, () => live.Running.ServiceProfiles, services.ApplyProfile, log, delay ?? Task.Delay, stop, done, services.OneChange,
            name => services.WaitingAtStart = name);
    }

    /// <summary>The longest notice the tray shows whole (a balloon's text is cut there).</summary>
    public const int MaxNotice = 250;

    /// <summary>
    /// What the tray says after a run that changed something or could not: "Build box turned off 2 services that were on:
    /// MapsBroker, DiagTrack. Click here to see them in Active changes." and "It could
    /// not turn off 1 service: C:\ProgramData\PoolWatch\poolwatch.log says why." (<paramref name="logFile"/>, where each
    /// failure's reason is). A service it set to Disabled but that is not known to have stopped is neither turned off nor
    /// "could not turn off": "It set 1 more service to Disabled, but it is not known to have stopped: ... says why." (with
    /// where to see it when nothing was turned off). Null when it found everything off already. When it would be longer
    /// than <see cref="MaxNotice"/>, fewer names first; then the profile is not named ("The profile applied at start"); then
    /// where to see them is left out (a click on the notice opens Active changes all the same). The log's place and "says
    /// why." always stay whole, and no word is cut.
    /// </summary>
    public static string? Notice(string profile, ServicesController.ProfileRun run, string logFile)
    {
        int off = run.TurnedOff.Count, set = run.NotStopped.Count, failed = run.Failed.Count;
        if (off == 0 && set == 0 && failed == 0) return null;
        var why = new List<string>();
        if (set > 0)
            why.Add($"set {set}{(off == 0 ? "" : " more")} service{(set == 1 ? "" : "s")} to Disabled, but {(set == 1 ? "it is" : "they are")} not known to have stopped");
        if (failed > 0) why.Add($"could not turn off {failed} service{(failed == 1 ? "" : "s")}");
        string text;
        for (int names = Math.Min(3, off); names >= 0; names--)
            if ((text = Text(ServiceProfileRules.Shown(profile), names, click: true)).Length <= MaxNotice) return text;
        if ((text = Text(UnnamedProfile, 0, click: true)).Length <= MaxNotice) return text;
        return Text(UnnamedProfile, 0, click: false);

        // The notice with the profile called <name>, the first <names> services it turned off named, and with <click>, where to see them.
        string Text(string name, int names, bool click)
        {
            string notTurned = why.Count == 0 ? ""
                : $"{(off == 0 ? name : " It")} {string.Join(", and ", why)}: {logFile} says why." +
                  (click && off == 0 && set > 0 ? $" Click here to see {(set == 1 ? "it" : "them")} in Active changes." : "");
            string listed = names == 0 ? "" : ": " + string.Join(", ", run.TurnedOff.Take(names).Select(ServiceProfileRules.Shown)) + (off > names ? $" and {off - names} more" : "");
            string turned = off == 0 ? "" : $"{name} turned off {off} service{(off == 1 ? "" : "s")} that {(off == 1 ? "was" : "were")} on{listed}." +
                                            (click ? $" Click here to see {(off == 1 ? "it" : "them")} in Active changes." : "");
            return (turned + notTurned).Trim();
        }
    }

    /// <summary>What a notice too long with the profile's name calls the profile (<see cref="Notice"/>).</summary>
    private const string UnnamedProfile = "The profile applied at start";

    /// <param name="atStart">The profiles as read when PoolWatch started (their problems are logged, the delay is theirs, and the
    /// run never turns off a service the chosen profile did not list then).</param>
    /// <param name="current">The profiles when the run's turn comes (<see cref="Narrowed"/>): an edit made since only narrows the run.</param>
    /// <param name="apply">Applies a profile through Apply's path (<see cref="ServicesController.ApplyProfile"/>).</param>
    /// <param name="delay">Waits (Task.Delay; tests pass their own).</param>
    /// <param name="stop">Cancelled when PoolWatch exits.</param>
    /// <param name="done">After the run: the profile and what it did.</param>
    /// <param name="oneChange">Runs the read of <paramref name="current"/> and the apply as one change of services
    /// (<see cref="ServicesController.OneChange"/>), so an edit or a restore made before the run's turn is seen; null: as
    /// it is.</param>
    /// <param name="waiting">Told the chosen profile's name while the run waits, and null once it ends
    /// (<see cref="ServicesController.WaitingAtStart"/>, for "Turn them off now?").</param>
    /// <returns>The run (done at once when there is nothing to apply).</returns>
    public static Task Start(ServiceProfileSet atStart, Func<ServiceProfileSet> current, Func<ServiceProfile, string, ServicesController.ProfileRun> apply,
        Action<string> log, Func<TimeSpan, CancellationToken, Task> delay, CancellationToken stop, Action<string, ServicesController.ProfileRun>? done = null,
        Action<Action>? oneChange = null, Action<string?>? waiting = null)
    {
        foreach (var p in atStart.Problems) log(p);
        var (chosen, why) = Pick(atStart);
        if (why is not null) log(why);
        if (chosen is null) return Task.CompletedTask;
        int minutes = Math.Clamp(atStart.DelayMinutes, ServiceProfileRules.MinDelayMinutes, ServiceProfileRules.MaxDelayMinutes);
        log($"Service profile {ServiceProfileRules.Shown(chosen.Name)} is applied at start in {minutes} minute{(minutes == 1 ? "" : "s")}.");
        waiting?.Invoke(chosen.Name);
        return Task.Run(async () =>
        {
            try
            {
                try { await delay(TimeSpan.FromMinutes(minutes), stop).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                if (stop.IsCancellationRequested) return;
                (string Name, ServicesController.ProfileRun Run)? ran = null;
                (oneChange ?? (op => op()))(() =>
                {
                    ServiceProfileSet now;
                    try { now = current(); }
                    catch (Exception ex) { log("Service profile not applied at start: the profiles could not be read: " + ex.Message); return; }
                    if (Narrowed(chosen, now, log) is not { } profile) return;
                    try { ran = (profile.Name, apply(profile, Via(profile.Name))); }
                    catch (Exception ex) { log($"Service profile {ServiceProfileRules.Shown(profile.Name)} not applied at start: {ex.Message}"); }
                });
                if (ran is not { } r) return;
                // The notice is apart: the profile was applied whatever happens to it (the tray going away as PoolWatch exits).
                try { done?.Invoke(r.Name, r.Run); }
                catch (Exception ex) { log($"Service profile {ServiceProfileRules.Shown(r.Name)} was applied at start, but its notice could not be shown: {ex.Message}"); }
            }
            finally { waiting?.Invoke(null); }
        });
    }

    /// <summary>
    /// What the run applies when its turn comes: the profile chosen when PoolWatch started (<paramref name="chosen"/>), as
    /// <paramref name="now"/> holds it, never more. An edit made meanwhile only narrows it: a profile deleted or no longer
    /// chosen is not applied; another profile chosen (or the chosen one renamed) is applied from the next start; and of the
    /// chosen one, only the services it listed at start and still lists are turned off (one added meanwhile waits for the
    /// next start). Null when nothing is applied; each case says so in the log.
    /// </summary>
    public static ServiceProfile? Narrowed(ServiceProfile chosen, ServiceProfileSet now, Action<string> log)
    {
        var (profile, gone) = Pick(now);
        if (profile is null)
        {
            log(gone ?? $"Service profile {ServiceProfileRules.Shown(chosen.Name)} is no longer chosen to apply at start: nothing is applied.");
            return null;
        }
        if (!ServiceProfileRules.SameName(profile.Name, chosen.Name))
        {
            log($"Service profile {ServiceProfileRules.Shown(profile.Name)} is the one applied at start now, but {ServiceProfileRules.Shown(chosen.Name)} was when " +
                $"PoolWatch started: nothing is applied now; {ServiceProfileRules.Shown(profile.Name)} is applied from the next start.");
            return null;
        }
        var listed = chosen.Services.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = profile.Services.Where(s => !listed.Contains(s)).ToList();
        if (added.Count > 0)
            log($"Service profile {ServiceProfileRules.Shown(profile.Name)}: {string.Join(", ", added.Select(ServiceProfileRules.Shown))} added after PoolWatch " +
                "started: turned off from the next start.");
        return profile with { Services = profile.Services.Where(listed.Contains).ToList() };
    }
}
