using System.Text;
using System.Text.Json;
using PoolWatch.Core.Boot;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Findings;
using PoolWatch.Core.Incidents;
using PoolWatch.Engine;

namespace PoolWatch.Analysis;

/// <summary>Collects the vendor bundle's parts from what PoolWatch already holds (no new collection) and writes the zip.</summary>
internal static class BundleBuilder
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>
    /// An incident in the bundle's incidents.md: its growth from start to peak, and, when PoolWatch's own cache clears freed
    /// part of it (the peak counts that back), its level and what they freed, said as their result. The level is "now"
    /// only for an open incident: a closed one's is as it was last measured (at its close, or when it was found
    /// retained), and a bundle is made hours or days later, so it is given with its sample's time
    /// (<see cref="Incident.LevelSaid"/>, as report.md gives it). An incident made for a trace of its own that has no
    /// span (its trace is not attached yet, as in a bundle made while the trace records, or recorded nothing
    /// PoolWatch could use) has no growth to give: its line says which, as its report.md does
    /// (<see cref="Incident.NoTraceSpan"/>), "its trace had not finished by" the time of the newest sample as the
    /// bundle is made (<paramref name="newestUtc"/>; null: no sample yet, and the line gives the moment the incident
    /// was made), or "its trace recorded nothing PoolWatch could use", and in brackets the level its tag held as it
    /// was last measured, with that sample's time. Its onset and its close are the moment it was made. (Before:
    /// "100 MB -> 100 MB (+0 B)", which reads as what the trace found.)
    /// </summary>
    internal static string IncidentLine(Incident i, DateTime? newestUtc = null)
    {
        string when = $", onset {Fmt.Stamp(i.OnsetUtc)}" + (i.ClosedUtc is { } c ? $", closed {Fmt.Stamp(c)}" : ", open");
        if (i.NoTraceSpan(newestUtc, Fmt.Stamp) is { } why)
            return $"- {i.Kind} on `{i.Tag.ToDisplay()}`: {why}" + (i.CurrentAtUtc is { } at ? $" (the tag held {Fmt.Bytes(i.CurrentBytes)} at {Fmt.Stamp(at)})" : "") + when;
        return $"- {i.Kind} on `{i.Tag.ToDisplay()}`: {Fmt.Bytes(i.StartBytes)} -> {Fmt.Bytes(i.PeakBytes)} (+{Fmt.Bytes(i.Growth)})" + when +
               (i.OwnClearsFreed is { } own ? $"{i.LevelSaid(Fmt.Stamp)}, after {own} of it" : "");
    }

    /// <summary>A suspect under its incident in incidents.md, its confidence in the words of Pool causes' Confidence column.</summary>
    internal static string SuspectLine(Core.Correlation.OwnerSuspect o) => $"- Suspect: {o.Summary} ({Core.Util.PlainWords.Confidence(o.Confidence)})";

    /// <summary>
    /// The bundle's incidents.md: the last 50 incidents, each with its <see cref="IncidentLine"/> and its first three
    /// meaningful owners (<see cref="SuspectLine"/>). <paramref name="newestUtc"/>: the newest sample's time as the
    /// bundle is made (null: no sample yet), for the line of a trace's incident whose trace has not finished.
    /// </summary>
    internal static string IncidentsMarkdown(IEnumerable<Incident> incidents, DateTime? newestUtc = null)
    {
        var inc = new StringBuilder("# Incidents\n\n");
        foreach (var i in incidents.TakeLast(50))
        {
            inc.AppendLine($"## {i.Id}").AppendLine();
            inc.AppendLine(IncidentLine(i, newestUtc));
            foreach (var o in (i.Owners ?? []).Where(o => o.Meaningful).Take(3)) inc.AppendLine(SuspectLine(o));
            inc.AppendLine();
        }
        return inc.ToString();
    }

    /// <param name="redaction">Null for an unredacted bundle (the owner opted out).</param>
    public static string Export(MonitorEngine engine, AnalysisPipeline analysis, RedactionInfo? redaction)
    {
        var now = DateTime.UtcNow;
        var d = analysis.Diagnostics;
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        string version = typeof(BundleBuilder).Assembly.GetName().Version?.ToString() ?? "?";
        files["README.md"] = VendorBundle.Readme(redaction is not null, now, version);

        var sys = analysis.Host.GetSystemInfo();
        files["system.json"] = JsonSerializer.Serialize(new
        {
            windows = sys.OsDescription, build = sys.OsBuild, machine = sys.Machine, ramBytes = sys.RamBytes, logicalCpus = sys.LogicalCpus,
            bootUtc = sys.BootUtc, uptimeHours = Math.Round(Environment.TickCount64 / 3_600_000.0, 2), poolWatch = version,
            appVersions = d.Versions, filterDrivers = sys.FilterDrivers, antiVirus = sys.AntiVirus,
        }, Json);

        // Boot files are read outside the history lock (up to 60 baselines: the sampler must not wait on them).
        var baselines = analysis.Boots.Baselines();
        var boots = analysis.Boots.All();
        lock (analysis.History.SyncRoot)
        {
            var h = analysis.History;
            if (analysis.Boots.Baseline is { } b)
            {
                files["boot-baseline.json"] = JsonSerializer.Serialize(b with { Tags = b.Tags.Take(200).ToList() }, Json);
                files["tags/after-reboot.csv"] = $"# boot baseline, {b.UptimeMinutes:0} min after boot{(b.NearBoot ? "" : " (PoolWatch started late this boot)")}\n" + VendorBundle.TagCsv(b.Tags);
            }
            // The cleanest baseline seen across boots (taken soonest after boot).
            if (baselines.OrderBy(x => x.UptimeMinutes).FirstOrDefault() is { } clean && clean.BootId != analysis.Boots.Baseline?.BootId)
                files["tags/clean-boot.csv"] = $"# boot {Fmt.Stamp(clean.BootUtc)}, {clean.UptimeMinutes:0} min after boot\n" + VendorBundle.TagCsv(clean.Tags);
            files["boots.md"] = BootTracker.Table(boots);
            files["samples.csv"] = VendorBundle.SamplesCsv(h);
            if (h.Minutes.Count > 0) files["tags/history-start.csv"] = VendorBundle.MinuteTagCsv(h.Minutes[0]);
            if (engine.Latest is { } latest)
                files["tags/now.csv"] = $"# {Iso(latest.TimeUtc)}\n" + VendorBundle.TagCsv(latest.Tags.Select(t => new TagSize(t.Tag.ToDisplay(), (long)t.PagedUsed, (long)t.NonPagedUsed)).Where(t => t.Total > 0));

            files["incidents.md"] = IncidentsMarkdown(analysis.Incidents.All, h.Count > 0 ? h.Times[^1] : null);
            // During growth: the one-minute tag table at each of the last three incidents' peaks. The entry name is the
            // file-safe tag (readable form + hex of the raw bytes: Reff and RefF never collide) and the peak's date and minute
            // (the same tag at the same minute is the same table: kept once).
            foreach (var i in analysis.Incidents.All.Where(i => i.Kind != IncidentKind.Release).TakeLast(3))
                if (h.Minutes.LastOrDefault(m => m.TimeUtc <= (i.EndUtc ?? i.LastRiseUtc)) is { } m)
                    files.TryAdd(VendorBundle.DuringGrowthEntry(i.Tag, m.TimeUtc), VendorBundle.MinuteTagCsv(m));
        }

        lock (d.ExperimentLock)
            if (d.Experiment is { Result: { } r } x)
            {
                files["experiment/report.md"] = r.Markdown(x.App, x.PhaseLength);
                files["experiment/experiment.csv"] = r.Csv();
                string[] names = ["before-launch", "app-running", "after-exit"];
                for (int k = 1; k < x.Snapshots.Count && k <= 3; k++)
                    files[$"tags/{names[k - 1]}.csv"] = $"# {x.App} experiment, {Iso(x.Snapshots[k].TimeUtc)}\n" +
                        VendorBundle.TagCsv(x.Snapshots[k].Tags.Select(kv => new TagSize(kv.Key.ToDisplay(), kv.Value.paged, kv.Value.nonPaged)).Where(t => t.Total > 0));
            }

        // Outside experiments, each watched app's last snapshot before it started and first after it exited.
        lock (d.AppSnapshots)
            foreach (var s in d.AppSnapshots.Snapshots.OrderBy(s => s.App).ThenBy(s => s.TimeUtc))
                files[$"tags/{s.Moment}-{s.App}.csv"] = $"# {s.App}: {(s.BeforeLaunch ? "the last snapshot before it started" : "the first snapshot after it exited")}, {Iso(s.TimeUtc)}\n" +
                    VendorBundle.TagCsv(s.Tags.Where(t => t.Total > 0));

        lock (d.Filters)
            files["filters.json"] = JsonSerializer.Serialize(new
            {
                instancesNote = d.Filters.InstancesNote, instances = d.Filters.Instances, services = d.Filters.Services,
                filterEvents = d.Filters.FilterEvents, packageEvents = d.Filters.PackageEvents, eventLog = d.EventLog.Status,
            }, Json);

        var findings = d.Findings.Snapshot();
        var git = new StringBuilder("# Git roots and spawn storms\n\n");
        foreach (var f in findings.Where(f => f.Kind is FindingKind.GitRoot or FindingKind.StrayGit or FindingKind.SpawnStorm or FindingKind.ChildAccumulation).TakeLast(100))
        {
            git.AppendLine($"- {Fmt.Stamp(f.TimeUtc)} {f.Kind}: {FindingReport.Md(f.Title)}");
            foreach (var line in f.Details.Take(5)) git.AppendLine($"  - {FindingReport.Md(line)}");
        }
        files["git-and-spawns.md"] = git.ToString();
        try
        {
            // The most recently active spawn-storm folder's trace (capped at 5 MB), its whole rows only.
            List<(string? Dir, long LastActiveMs)> held;
            lock (d.Spawns) held = d.Spawns.Folders.Select(f => (f.Dir, f.LastActiveMs)).ToList();
            if (LatestStormSpawns(held, engine.Paths.IncidentsDir) is { } sp && new FileInfo(sp).Length <= 5 << 20)
                files["spawns.csv"] = Core.Util.Csv.ReadCompleteRows(sp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        var crashes = new StringBuilder("# Crashes (event logs, since PoolWatch last ran)\n\n");
        foreach (var c in d.SystemCrashes)
        {
            crashes.AppendLine($"## {FindingReport.Md(c.Title)}").AppendLine();
            foreach (var rec in c.Records) crashes.AppendLine($"- {Fmt.Stamp(rec.TimeUtc)} {rec.Kind}: {FindingReport.Md(rec.Text)}");
            crashes.AppendLine();
        }
        foreach (var f in findings.Where(f => f.Kind == FindingKind.Crash && f.Title.StartsWith("App crash", StringComparison.Ordinal)))
            crashes.AppendLine($"- {Fmt.Stamp(f.TimeUtc)} {FindingReport.Md(f.Title)}");
        if (d.SystemCrashes.Count == 0 && !findings.Any(f => f.Kind == FindingKind.Crash)) crashes.AppendLine("None found.");
        files["crashes.md"] = crashes.ToString();
        files["findings.jsonl"] = string.Concat(findings.Select(f => FindingLog.ToJsonLine(f) + "\n"));

        return VendorBundle.Write(engine.Paths.ExportsDir, now, files, redaction, engine.Paths.Guard.CheckReal,
            new Dictionary<string, string> { [AgentsGuide.FileName] = AgentsGuide.Text });
    }

    /// <summary>The most storm folders on disk whose spawns.csv is looked at (the newest by name), when none is held in memory.</summary>
    internal const int MaxStormFoldersLookedAt = 500;

    /// <summary>
    /// The spawns.csv of the most recently active spawn-storm folder, or null. Not the newest by name: a folder takes every
    /// storm of its program that comes within an hour of the one before (SpawnTracker), so an older folder can be the one
    /// still in use. Of the folders PoolWatch holds (<paramref name="held"/>: its storms', the last active the latest), the
    /// latest with a spawns.csv; else, after a restart, of the newest <see cref="MaxStormFoldersLookedAt"/> storm folders in
    /// <paramref name="incidentsDir"/>, the one whose spawns.csv was written last.
    /// </summary>
    internal static string? LatestStormSpawns(IEnumerable<(string? Dir, long LastActiveMs)> held, string incidentsDir)
    {
        foreach (var (dir, _) in held.Where(h => h.Dir is not null).OrderByDescending(h => h.LastActiveMs))
            if (Path.Combine(dir!, "spawns.csv") is var sp && File.Exists(sp)) return sp;
        if (!Directory.Exists(incidentsDir)) return null;
        return Directory.EnumerateDirectories(incidentsDir, "*-SpawnStorm*").Order(StringComparer.Ordinal).TakeLast(MaxStormFoldersLookedAt)
            .Select(d => new FileInfo(Path.Combine(d, "spawns.csv"))).Where(f => f.Exists).MaxBy(f => f.LastWriteTimeUtc)?.FullName;
    }

    private static string Iso(DateTime utc) => utc.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
}
