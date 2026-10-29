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
    /// part of it (the peak counts that back), its level now and what they freed, said as their result.
    /// </summary>
    internal static string IncidentLine(Incident i) =>
        $"- {i.Kind} on `{i.Tag.ToDisplay()}`: {Fmt.Bytes(i.StartBytes)} -> {Fmt.Bytes(i.PeakBytes)} (+{Fmt.Bytes(i.Growth)}), onset {Fmt.Stamp(i.OnsetUtc)}" +
        (i.ClosedUtc is { } c ? $", closed {Fmt.Stamp(c)}" : ", open") +
        (i.OwnClearsFreed is { } own ? $"; now {Fmt.Bytes(i.CurrentBytes)}, after {own} of it" : "");

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

            var inc = new StringBuilder("# Incidents\n\n");
            foreach (var i in analysis.Incidents.All.TakeLast(50))
            {
                inc.AppendLine($"## {i.Id}").AppendLine();
                inc.AppendLine(IncidentLine(i));
                foreach (var o in (i.Owners ?? []).Where(o => o.Meaningful).Take(3)) inc.AppendLine($"- Suspect: {o.Summary} ({OwnerRankingStrength(o)})");
                inc.AppendLine();
            }
            files["incidents.md"] = inc.ToString();
            // During growth: the one-minute tag table at each of the last three incidents' peaks. C6: the entry name is the
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

        // Item 13 (Part 3): outside experiments, each watched app's last snapshot before it started and first after it exited.
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
            // The newest spawn-storm trace (explicit folder pattern under PoolWatch's own incidents\, capped at 5 MB).
            if (Directory.Exists(engine.Paths.IncidentsDir) &&
                Directory.EnumerateDirectories(engine.Paths.IncidentsDir, "*-SpawnStorm*").Order(StringComparer.Ordinal).LastOrDefault() is { } storm &&
                Path.Combine(storm, "spawns.csv") is var sp && File.Exists(sp) && new FileInfo(sp).Length <= 5 << 20)
                files["spawns.csv"] = File.ReadAllText(sp);
        }
        catch (IOException) { }

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

    private static string Iso(DateTime utc) => utc.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);

    private static string OwnerRankingStrength(Core.Correlation.OwnerSuspect o) => Core.Correlation.OwnerRanking.Strength(o.Confidence);
}
