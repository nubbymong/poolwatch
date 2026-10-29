using PoolWatch.Core.Setup;
using PoolWatch.Core.TokenGuard;

namespace PoolWatch.Core.Recommendations;

/// <summary>
/// The one recommendations model: the setup guide's rows (every first-run item with its state) and the Simple view's
/// suggestions (offers only, each backed by evidence PoolWatch saw, never one the owner hid in the last 30 days). Pure.
/// </summary>
public static class RecommendationRules
{
    public static readonly TimeSpan DismissFor = TimeSpan.FromDays(30);
    public const int MaxTitle = 60, MaxWhy = 240, MaxButton = 16;

    /// <summary>Also the order of the Simple view's suggestions (lowest first).</summary>
    private const int TokenGuardPriority = 10, ReplaceTaskPriority = 20, HourlyPriority = 30, CheckLeakPriority = 35, SignInPriority = 40,
        ReportPriority = 50, ServicesPriority = 60, DefenderPriority = 70, ShortcutPriority = 80;

    private const string TokenGuardTitle = "Protect this PC from the Windows token leak";
    private const string TradeOff = "With it on, any app may bring its window to the front.";

    /// <summary>
    /// The setup guide's rows, in order: start at sign-in; your RAMMap clear task replaced, or else the hourly clear; the
    /// token-leak guard (offered only when this build leaks); the optional services that are running. Empty until the first
    /// scan has finished (the guide shows its facts first).
    /// </summary>
    public static IReadOnlyList<Recommendation> FirstRun(RecommendationInputs i)
    {
        if (i.Facts is not { } f) return [];
        var rows = new List<Recommendation>
        {
            f.LogonTaskExists ? Done(SignIn(), "PoolWatch starts when you sign in.")
                : f.InstalledInProgramFiles ? SignIn()
                : Info(SignIn(), "This needs PoolWatch installed with its installer; it runs from another folder now."),
            // The two must never both run: with a RAMMap task of your own, replacing it IS the hourly clear.
            f.OwnClearTasks.Count > 0 ? ReplaceTask()
                : i.AutoClearOn ? Done(Hourly(), "PoolWatch clears the cache automatically.")
                : Hourly(),
            TokenGuardRow(i, f),
        };
        if (f.OptionalServicesRunning > 0) rows.Add(ServicesInfo(f));
        return rows;
    }

    /// <summary>
    /// The Simple view: up to <paramref name="max"/> offers, each only on its evidence (in memory or in the cached facts),
    /// never one hidden in the last 30 days, by priority.
    /// </summary>
    public static IReadOnlyList<Recommendation> Current(RecommendationInputs i, int max = 3) =>
        Candidates(i).Where(r => !Hidden(i.Dismissed, r.Key, i.NowUtc)).OrderBy(r => r.Priority).Take(max).ToList();

    /// <summary>True while a key hidden at its time is still within <see cref="DismissFor"/>.</summary>
    public static bool Hidden(IReadOnlyDictionary<string, DateTime> dismissed, string key, DateTime nowUtc) =>
        dismissed.TryGetValue(key, out var at) && nowUtc - at < DismissFor;

    private static IEnumerable<Recommendation> Candidates(RecommendationInputs i)
    {
        var f = i.Facts;
        bool leaksHere = i.LeakTestNow == LeakVerdict.Leak || (i.SavedVerdict == GuardResult.Leak && f is not null && i.SavedVerdictBuild == f.Build);
        if (!i.TokenGuardOn && (i.TokenSpawnLast24h || leaksHere))
            yield return TokenGuardOffer(i.TokenSpawnLast24h
                ? "Security tokens pile up in the kernel pool each time a program starts another: PoolWatch saw it in the last day. " + TradeOff
                : "PoolWatch's check found that this Windows build leaks a little of the kernel pool each time a program starts another. " + TradeOff);
        if (f is not null)
        {
            if (f.OwnClearTasks.Count > 0) yield return ReplaceTask();
            else if (!i.AutoClearOn) yield return Hourly();
            if (!i.TokenGuardOn && i.LeakTestNow is null && f.TokenLeak == TokenLeakStatus.NeedsTest && i.SavedVerdictBuild != f.Build)
                yield return new Recommendation(RecommendationId.CheckTokenLeak, "Check this Windows build for the token leak",
                    (i.SavedVerdictBuild is null ? "Some Windows 11 builds leak a little of the kernel pool each time a program starts another. "
                                                 : "Windows was updated since PoolWatch last checked for the token leak. ") +
                    "The check takes about 20 seconds and changes nothing.", "Check", RecommendationAction.CheckTokenLeak, CheckLeakPriority);
            if (!f.LogonTaskExists && f.InstalledInProgramFiles) yield return SignIn();
        }
        if (i.ReportableIncident is { } inc)
            yield return new Recommendation(RecommendationId.ReportIncident, "Report what PoolWatch found",
                Clip(inc.Headline, 130) + " A report can help get it fixed, and you read every word before anything is sent.",
                "Report", RecommendationAction.ReportIssue, ReportPriority) { Subject = inc.Id };
        if (i.CostlyOptionalServices.Count > 0 || i.SaturationOfferedTurnOffLast24h)
            yield return new Recommendation(RecommendationId.ReviewServices, "Review the background services",
                i.CostlyOptionalServices.Count > 0
                    ? $"{Clip(i.CostlyOptionalServices[0], 60)} {(i.CostlyOptionalServices.Count > 1 ? $"and {i.CostlyOptionalServices.Count - 1} more " : "")}" +
                      "used noticeable processor time in the background over the last half hour. You can turn off the ones you do not need."
                    : "Your PC was overloaded in the last day, and a background service you may not need was among the busiest. You can turn it off.",
                "Review", RecommendationAction.OpenServices, ServicesPriority);
        // Performance mode only lets programs open files without waiting for the scan; Microsoft says it is not a remedy for
        // Defender's own processor or memory use, so the reason never promises that.
        if (f is not null && DefenderRoots(i, f) is { Count: > 0 } roots)
            yield return new Recommendation(RecommendationId.DefenderDevDrive, "Less waiting for Microsoft Defender on your Dev Drive",
                (i.FileSystemMetadataGrewLast24h ? "File activity made the kernel pool grow in the last day, and Microsoft Defender checks"
                                                 : "Microsoft Defender was busy in the last half hour, and it checks") +
                " each file on your Dev Drive before it opens. On a trusted Dev Drive it can check just after, so programs wait less. " +
                "Here is how to set it up yourself.",
                "Show me how", RecommendationAction.ShowInstructions, DefenderPriority) { Instructions = DefenderAdvice.Text(roots) };
        if (f is { ChatGptInstalled: true } && i.ChatGptLauncherInstalled && !i.ChatGptShortcutMade && i.ChatGptNamedInGrowth)
            yield return new Recommendation(RecommendationId.ChatGptShortcut, "Start Codex without its shader cache",
                "PoolWatch linked the Codex app to growth of the kernel pool. Some users found the Codex app uses less of the kernel pool when it " +
                "starts without its GPU shader disk cache (found by Faarfarin on GitHub). The shortcut starts it that way.",
                "Create shortcut", RecommendationAction.Apply, ShortcutPriority);
    }

    /// <summary>
    /// The Dev Drives the Defender advice is for: Defender's real-time protection is on, a Dev Drive is not trusted or
    /// performance mode is off, and there is evidence it costs (file-system metadata grew in the last day, or Defender's
    /// process averaged a quarter of a core). The untrusted drives, or every Dev Drive when performance mode is off.
    /// </summary>
    private static List<string> DefenderRoots(RecommendationInputs i, EnvironmentFacts f)
    {
        var devDrives = f.Volumes.Where(v => v.IsDevDrive).ToList();
        if (devDrives.Count == 0 || f.DefenderRealTimeOn != true) return [];
        if (!i.FileSystemMetadataGrewLast24h && i.DefenderCoresAvg30Min is not >= 0.25) return [];
        if (f.DefenderPerformanceModeOn == false) return devDrives.Select(v => v.Root).ToList();
        return devDrives.Where(v => v.Trusted == false).Select(v => v.Root).ToList();
    }

    // ------------------------------------------------------------------ the rows

    private static Recommendation SignIn() => new(RecommendationId.StartAtSignIn, "Start PoolWatch when you sign in",
        "So it watches from the moment you sign in, without asking for permission each time.", "Turn on", RecommendationAction.Apply, SignInPriority);

    private static Recommendation Hourly() => new(RecommendationId.HourlyCacheClear, "Clear the cache every hour",
        "Windows keeps details of recently used files in the kernel pool, and on some PCs that grows by gigabytes. An hourly clear " +
        "frees it; your files are not touched.", "Turn on", RecommendationAction.Apply, HourlyPriority);

    private static Recommendation ReplaceTask() => new(RecommendationId.ReplaceClearTask, "Let PoolWatch replace your RAMMap clear task",
        "A task of your own clears the cache with RAMMap. PoolWatch can do it every hour instead and show what each clear freed. " +
        "Your task is saved, so you can put it back.", "Replace", RecommendationAction.Apply, ReplaceTaskPriority);

    private static Recommendation TokenGuardOffer(string why) =>
        new(RecommendationId.TokenLeakGuard, TokenGuardTitle, why, "Turn on", RecommendationAction.Apply, TokenGuardPriority);

    /// <summary>The guide's token row: done when the guard is on; offered only when this build leaks; else what is known.</summary>
    private static Recommendation TokenGuardRow(RecommendationInputs i, EnvironmentFacts f)
    {
        if (i.TokenGuardOn) return Done(TokenGuardOffer(""), "The token-leak guard is on.");
        var status = i.LeakTestNow switch
        {
            LeakVerdict.Leak => TokenLeakStatus.Leaks,
            LeakVerdict.Clean => TokenLeakStatus.Clean,
            LeakVerdict.Unclear => (TokenLeakStatus?)null,
            _ => f.TokenLeak,
        };
        return status switch
        {
            TokenLeakStatus.Leaks => TokenGuardOffer("This Windows build leaks a little of the kernel pool each time a program starts another. The guard turns on the " +
                                                     "Windows setting that stops it, each time PoolWatch starts. " + TradeOff),
            TokenLeakStatus.NeedsTest => TokenGuardOffer("Checking whether this Windows build has the token leak (about 20 seconds)...") with
            {
                Button = "", Action = RecommendationAction.CheckTokenLeak, State = RecommendationState.Checking,
            },
            TokenLeakStatus.NotAffected => Info(TokenGuardOffer(""), "This Windows build does not have the token leak."),
            // One clean check is not proof (the guard tests again at each logon), so the line says what was found, no more.
            TokenLeakStatus.Clean => Info(TokenGuardOffer(""), "PoolWatch's check found no token leak on this Windows build."),
            TokenLeakStatus.WorkedAroundAlready => Info(TokenGuardOffer(""), "Something on this PC already works around the token leak."),
            TokenLeakStatus.Unknown => Info(TokenGuardOffer(""), "PoolWatch could not check for the token leak."),
            _ => Info(TokenGuardOffer(""), "The token-leak check gave no clear answer. PoolWatch suggests the guard if it sees the leak later."),
        };
    }

    private static Recommendation ServicesInfo(EnvironmentFacts f)
    {
        var names = f.OptionalServiceExamples.Take(2).Select(n => Clip(n, 50)).ToList();
        string examples = names.Count > 0 ? $" (for example {string.Join(", ", names)})" : "";
        string count = f.OptionalServicesRunning == 1 ? "1 background service you may not need is" : $"{f.OptionalServicesRunning} background services you may not need are";
        return new Recommendation(RecommendationId.ReviewServices, "Background services", $"{count} running{examples}. You can review them any time " +
            "in Advanced view, Services.", "", RecommendationAction.OpenServices, ServicesPriority) { State = RecommendationState.Info };
    }

    private static Recommendation Done(Recommendation r, string why) => r with { Why = why, Button = "", State = RecommendationState.Done };

    private static Recommendation Info(Recommendation r, string why) => r with { Why = why, Button = "", State = RecommendationState.Info };

    /// <summary>A text cut at a word to at most <paramref name="max"/> characters, marked with "...".</summary>
    private static string Clip(string s, int max)
    {
        if (s.Length <= max) return s;
        int cut = s.LastIndexOf(' ', max - 3);
        return s[..(cut > max / 2 ? cut : max - 3)].TrimEnd(' ', ',', ';', '.') + "...";
    }
}
