using System.Globalization;
using PoolWatch.Analysis;
using PoolWatch.Core;
using PoolWatch.Core.Correlation;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Findings;
using PoolWatch.Core.Identity;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Model;
using PoolWatch.Core.Recommendations;
using PoolWatch.Core.Services;
using PoolWatch.Core.Setup;
using PoolWatch.Core.TokenGuard;
using PoolWatch.Core.Util;
using PoolWatch.Ui;

namespace PoolWatch.Engine.Setup;

/// <summary>
/// Gathers <see cref="RecommendationInputs"/> from what PoolWatch already holds in memory (incidents, findings, process
/// costs, settings) and the cached facts. Nothing is scanned, enumerated or queried for it: cheap enough for the Simple
/// view's once-a-minute refresh. Read under the locks the window uses (the history lock, the cost history's own).
/// </summary>
internal static class RecommendationInputsBuilder
{
    /// <summary>
    /// The file-system metadata tags (ReFS, NTFS, the minifilter manager, the memory manager's file structures, the cache
    /// manager's views): their growth is what Defender's scanning of a busy Dev Drive adds to.
    /// </summary>
    internal static readonly IReadOnlySet<PoolTag> FileSystemMetadataTags =
        new[] { "Reff", "RefF", "Ntff", "NtfF", "MSPi", "MSeb", "MSb+", "MSde", "MSpa", "FMfn", "FMsl", "FMfc", "MmSt", "CcVl" }.Select(PoolTag.Parse).ToHashSet();

    public static readonly TimeSpan ReportableFor = TimeSpan.FromDays(7);

    /// <summary>The window the processor-time evidence is averaged over ("over the last half hour").</summary>
    private static readonly TimeSpan CostWindow = TimeSpan.FromMinutes(30);

    /// <param name="chatGptShortcutMade">The change list holds a ChatGPT shortcut PoolWatch made.</param>
    /// <param name="chatGptLauncherInstalled">The ChatGPT launcher script is installed next to PoolWatch.</param>
    /// <param name="leakTestNow">This session's token-leak check, when one ran.</param>
    public static RecommendationInputs Build(MonitorEngine engine, AnalysisPipeline analysis, EnvironmentFacts? facts, RecommendationStore store,
        bool chatGptShortcutMade, bool chatGptLauncherInstalled, LeakVerdict? leakTestNow, DateTime nowUtc)
    {
        var d = analysis.Diagnostics;
        var dismissed = store.Dismissed;
        facts = WithClearTasksNow(facts, d.Cache.HelperStatus);
        var saved = d.Guard.ReadState();
        var lastDay = d.Findings.Between(nowUtc - TimeSpan.FromDays(1), nowUtc);
        bool metadataGrew, chatGptNamed;
        (string Id, string Dir, string Headline)? reportable;
        lock (analysis.History.SyncRoot)
        {
            var incidents = analysis.Incidents.All.Where(i => i.Kind is IncidentKind.Growth or IncidentKind.NonPagedTotal).ToList();
            metadataGrew = incidents.Any(i => FileSystemMetadataTags.Contains(i.Tag) && i.OpenedUtc >= nowUtc - TimeSpan.FromDays(1))
                           || analysis.Detector.Status.Values.Any(s => s.State == TagState.Growing && FileSystemMetadataTags.Contains(s.Tag));
            chatGptNamed = incidents.Any(NamesChatGpt);
            reportable = Reportable(incidents, dismissed, nowUtc);
        }
        return new RecommendationInputs
        {
            NowUtc = nowUtc, Facts = facts, AutoClearOn = engine.Settings.AutoClearEnabled, TokenGuardOn = engine.Settings.TokenGuardEnabled,
            LeakTestNow = leakTestNow, SavedVerdictBuild = saved?.Build, SavedVerdict = saved?.Result,
            TokenSpawnLast24h = lastDay.Any(f => f.Kind == FindingKind.TokenSpawn && f.Severity >= FindingSeverity.Warning),
            SaturationOfferedTurnOffLast24h = lastDay.Any(f => f.Kind == FindingKind.Saturation && f.Data.TryGetValue("offerTurnOff", out var offer) && offer is true),
            FileSystemMetadataGrewLast24h = metadataGrew,
            DefenderCoresAvg30Min = engine.Registry.LiveRecords().FirstOrDefault(r => r.Image.Equals("MsMpEng.exe", StringComparison.OrdinalIgnoreCase)) is { } defender
                ? CoresOverWhole(d.Costs, defender.Key, CostWindow, nowUtc) : null,
            CostlyOptionalServices = CostlyOptionalServices(engine, d, nowUtc), ReportableIncident = reportable, ChatGptNamedInGrowth = chatGptNamed,
            ChatGptLauncherInstalled = chatGptLauncherInstalled, ChatGptShortcutMade = chatGptShortcutMade, Dismissed = dismissed,
        };
    }

    /// <summary>
    /// The cached facts brought up to date with the hourly clear-task check, since they may be a day old: a clear task of
    /// your own that the check no longer finds is gone, and one it finds that the scan did not see is there (its
    /// description stands in for the path: the rules only count the tasks, and replacing one reads Task Scheduler again).
    /// </summary>
    internal static EnvironmentFacts? WithClearTasksNow(EnvironmentFacts? facts, CacheHelper.Status? now) => (facts, now) switch
    {
        ({ OwnClearTasks.Count: > 0 } f, { LegacyTaskPresent: false }) => f with { OwnClearTasks = [] },
        ({ OwnClearTasks.Count: 0 } f, { LegacyTaskPresent: true, LegacyTaskText: { } task }) => f with { OwnClearTasks = [task] },
        _ => facts,
    };

    /// <summary>
    /// A process's average cores over the whole of the last <paramref name="span"/>, or null when it was not tracked that
    /// long (idle, or busy only for a while): a minute's burst is not "over the last half hour". Takes the history's lock.
    /// </summary>
    internal static double? CoresOverWhole(ProcessCostHistory costs, ProcessKey key, TimeSpan span, DateTime nowUtc)
    {
        lock (costs)
            return costs.Average(key, span, nowUtc) is { } avg && avg.covered >= span - ProcessCostHistory.Step ? avg.cores : null;
    }

    /// <summary>
    /// The newest growth incident closed in the last 7 days whose likely cause has at least some evidence, and not hidden:
    /// its id (also its folder name), folder and a plain headline. History lock held.
    /// </summary>
    internal static (string Id, string Dir, string Headline)? Reportable(IEnumerable<Incident> incidents, IReadOnlyDictionary<string, DateTime> dismissed, DateTime nowUtc)
    {
        foreach (var i in incidents.Where(i => i.ClosedUtc is { } c && nowUtc - c <= ReportableFor).OrderByDescending(i => i.ClosedUtc))
        {
            if (UiData.CauseOf(i) is not { level: >= Confidence.Correlated } cause) continue;
            if (RecommendationRules.Hidden(dismissed, Recommendation.KeyOf(RecommendationId.ReportIncident, i.Id), nowUtc)) continue;
            string at = i.OnsetUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
            return (i.Id, i.Dir, $"Kernel pool grew {Fmt.Bytes(i.Growth)} at {at}; the likely cause is {cause.name}, {PlainWords.ConfidencePhrase(cause.level)}.");
        }
        return null;
    }

    /// <summary>
    /// Whether ChatGPT desktop (a process of an OpenAI Store package) is a likely cause of a growth incident: one of the top
    /// three trace owners (confirmed by the trace), or of the top three meaningful owners or suspects with at least some
    /// evidence. A weak ranking only says it was active, which does not link it to the growth.
    /// </summary>
    internal static bool NamesChatGpt(Incident inc) =>
        (inc.TraceOwners ?? []).Take(3).Any(o => IsChatGpt(o.Owner))
        || (inc.Owners ?? []).Where(o => o.Meaningful).Take(3)
            .Any(o => o.Confidence >= Confidence.Correlated && (IsChatGpt(o.Owner) || o.Members.Any(m => IsChatGptImage(m.ImagePath))))
        || inc.Suspects.Take(3).Any(s => s.Confidence >= Confidence.Correlated && IsChatGptImage(s.ImagePath));

    private static bool IsChatGpt(Owner o) => o.Key.Equals("app:chatgpt", StringComparison.OrdinalIgnoreCase) || o.Name.StartsWith("OpenAI.", StringComparison.OrdinalIgnoreCase);

    private static bool IsChatGptImage(string? imagePath) => imagePath?.Contains(@"\WindowsApps\OpenAI.", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// The running optional services (on the catalogue) whose share of their process averaged at least 0.1 core over the
    /// whole of the last 30 minutes: all of it in a process of its own, an even part of a shared host. Costliest first.
    /// </summary>
    private static List<string> CostlyOptionalServices(MonitorEngine engine, Analysis.Diagnostics d, DateTime nowUtc)
    {
        var services = engine.Services;
        var perPid = services.Where(s => s.ProcessId > 0).GroupBy(s => s.ProcessId).ToDictionary(g => g.Key, g => g.Count());
        var costly = new List<(string Name, double Cores)>();
        foreach (var s in services)
        {
            if (s.ProcessId <= 0 || d.Services.Catalogue.Find(s.Name) is null || engine.Registry.GetLive(s.ProcessId) is not { } host) continue;
            if (CoresOverWhole(d.Costs, host.Key, CostWindow, nowUtc) is not { } cores) continue;
            double share = ServiceCosts.Share((cores, 0), perPid[s.ProcessId]).cores;
            if (share >= 0.1) costly.Add((s.DisplayName, share));
        }
        return costly.OrderByDescending(c => c.Cores).Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
