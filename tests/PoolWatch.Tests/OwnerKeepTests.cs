using System.Runtime.CompilerServices;
using PoolWatch.Core;
using PoolWatch.Core.Correlation;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Identity;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Model;
using PoolWatch.Core.Tags;

namespace PoolWatch.Tests;

/// <summary>
/// A finished ranking keeps only what its readers print (<see cref="OwnerRanking.Keep"/>): the reports, the bundle and the
/// Pool causes row read the meaningful owners and at most 20 members each, never the other suspects of the 2,000 ranked.
/// What they print must not change.
/// </summary>
[Collection(FootprintCollection.Name)]
public class OwnerKeepTests
{
    internal static ProcessIdentity Identity(ProcessRegistry registry, Suspect s) =>
        ProcessIdentities.Of(registry.Ancestry(s.Key) is { Count: > 0 } c ? c : [new ProcessRecord { Key = s.Key, Image = s.Image }]);

    private static (ChurnWorld w, CorrelationWindow window) GrowingHour() => ChurnWorld.GrowingHour();

    /// <summary>Ranked as an incident is: every active process, grouped by owner.</summary>
    internal static List<OwnerSuspect> Ranked(ChurnWorld w, CorrelationWindow window) =>
        OwnerRanking.Rank(w.History, Correlator.Rank(w.History, w.Registry, ChurnWorld.Tag, window, maxSuspects: 2000), ChurnWorld.Tag, window,
            360L << 20, s => Identity(w.Registry, s));

    [Fact]
    public void KeepingTheOwnersHoldsAFractionOfTheRanking()
    {
        var (w, window) = GrowingHour();
        var full = Ranked(w, window);
        var kept = OwnerRanking.Keep(Ranked(w, window));
        Assert.All(kept, o => Assert.True(o.Meaningful));
        Assert.Equal(full.Where(o => o.Meaningful).Select(o => o.Name), kept.Select(o => o.Name));
        Assert.All(kept, o => Assert.InRange(o.Members.Count, 1, OwnerRanking.KeptMembers + 1));
        Assert.Equal(full.Where(o => o.Meaningful).Select(o => o.Members.Count), kept.Select(o => o.MemberCount));
        Assert.True(full.Sum(o => o.Members.Count) >= 1000, $"the world ranks {full.Sum(o => o.Members.Count)} suspects");

        // Retained memory, measured the way the incident holds it: everything the ranking built and the owners still reach.
        long fullBytes = Retained(() => Ranked(w, window));
        long keptBytes = Retained(() => OwnerRanking.Keep(Ranked(w, window)));
        Assert.True(keptBytes * 4 < fullBytes, $"kept {keptBytes / 1024} KB, full {fullBytes / 1024} KB");
    }

    /// <summary>
    /// A Pool causes row keeps five owners: its details print the first three meaningful ones (name, strength, evidence
    /// lines, five processes and "N more", one command line) and the copied summary five with up to 20 processes each.
    /// </summary>
    [Fact]
    public void APoolCausesRowKeepsEverythingItsDetailsPrint()
    {
        var (w, window) = GrowingHour();
        var full = Ranked(w, window).Where(o => o.Meaningful).ToList();
        Assert.Equal(full.Take(5).Select(o => o.Name), OwnerRanking.Keep(Ranked(w, window), maxOwners: 5).Select(o => o.Name));
        // Every meaningful owner, so the sessions with hundreds of processes are among them.
        var kept = OwnerRanking.Keep(Ranked(w, window));
        Assert.Contains(full, o => o.Members.Count > OwnerRanking.KeptMembers + 1);
        for (int i = 0; i < kept.Count; i++)
        {
            var (f, k) = (full[i], kept[i]);
            Assert.Equal((f.Name, f.Confidence, f.CorrelationOnly, f.Owner.Kind, f.Summary), (k.Name, k.Confidence, k.CorrelationOnly, k.Owner.Kind, k.Summary));
            Assert.Equal(f.Plain, k.Plain);
            Assert.Equal(f.Members.Count, k.MemberCount);
            Assert.Equal(Describe(f.Members.Take(OwnerRanking.KeptMembers)), Describe(k.Members.Take(OwnerRanking.KeptMembers)));
            Assert.Equal(f.Identities.Take(OwnerRanking.KeptMembers).Select(x => x.Display), k.Identities.Take(OwnerRanking.KeptMembers).Select(x => x.Display));
            Assert.Equal(f.Members.FirstOrDefault(m => m.CommandLine is not null)?.CommandLine, k.Members.FirstOrDefault(m => m.CommandLine is not null)?.CommandLine);
        }

        static IEnumerable<string> Describe(IEnumerable<Suspect> members) =>
            members.Select(m => $"{m.Key} {m.DisplayName} {m.StartUtc:o} {m.ExitUtc:o} {m.IsOrphan} {m.CommandLine} {m.ImagePath} {string.Join(">", m.Ancestry)} {string.Join("|", m.Evidence)}");
    }

    [Fact]
    public void TrimKeepsTheFirstTwentyAndTheFirstMemberWithACommandLine()
    {
        var owner = new OwnerSuspect { Owner = new Owner(OwnerKind.ClaudeSession, "s", "Claude Code session"), Members = [], Identities = [] };
        for (int i = 0; i < 50; i++)
        {
            owner.Members.Add(new Suspect
            {
                Key = new ProcessKey(i + 1, 1), Image = "bash.exe", CommandLine = i == 30 ? "bash -c \"git status\"" : null,
                Direct = new DirectSignal(null, 0, 0, 0, 0, 0, false), Activity = new ActivitySignal("total", double.NaN, 0, double.NaN, 0, 0, false),
                Lifecycle = new LifecycleSignal(false, false, false, []),
            });
            owner.Identities.Add(new ProcessIdentity("bash.exe", $"role {i % 4}", owner.Owner, false));
        }
        string summary = owner.Summary;
        owner.Trim(20);
        Assert.Equal(21, owner.Members.Count);
        Assert.Equal(21, owner.Identities.Count);
        Assert.Equal(Enumerable.Range(1, 20).Append(31), owner.Members.Select(m => m.Key.Pid));
        Assert.Equal("role 2", owner.Identities[20].Role); // the identity moves with its member
        Assert.Equal(50, owner.MemberCount);
        Assert.Equal(summary, owner.Summary);
        Assert.Contains("50 processes", summary);
    }

    [Fact]
    public void TrimmingTheOwnersLeavesReportMdAndReportJsonByteIdentical()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pw-keep-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new Settings { GrowthRules = [new(10, 100 * Settings.MB)], IncidentQuietMinutes = 5 };
            var w = new ChurnWorld();
            var host = new FakeHost(dir);
            var knowledge = new TagKnowledge(null, null);
            var mgr = new IncidentManager(settings, knowledge, host, w.Registry);
            var det = new Detector(settings);
            void Step(long growth)
            {
                // The first session's commands do more I/O while the tag grows: a session of many processes ranks among
                // the three owners report.md describes, so their summaries are where a trim would show.
                var s = w.Step(shortLived: 20, busy: 5, tagGrowth: growth, firstSessionOps: growth > 0 ? 600 : 0);
                mgr.AddEvents(w.Events);
                mgr.Process(w.History, det, det.Evaluate(w.History), s.TimeUtc);
            }
            for (int i = 0; i < 90; i++) Step(0);
            for (int i = 0; i < 90; i++) Step(2L << 20);
            var inc = Assert.Single(mgr.Open);
            Assert.All(inc.Owners!, o => Assert.True(o.Meaningful && o.Members.Count <= OwnerRanking.KeptMembers + 1));
            // What profile.json reports as held.
            Assert.Equal((1, inc.Owners!.Count, inc.Owners.Sum(o => o.Members.Count), inc.Events.Count), mgr.Held());

            // The same incident written with every owner of its ranking, then with what the manager keeps.
            var window = inc.Window!;
            List<OwnerSuspect> Owners() => OwnerRanking.Rank(w.History, Correlator.Rank(w.History, w.Registry, inc.Tag, window, maxSuspects: 2000),
                inc.Tag, window, Math.Max(0, inc.Growth), s => Identity(w.Registry, s));
            var writer = new IncidentWriter(knowledge, host, w.Registry);
            (string md, string json) Write(List<OwnerSuspect> owners)
            {
                inc.Owners = owners;
                writer.Write(w.History, inc, inc.Events);
                return (File.ReadAllText(Path.Combine(inc.Dir, "report.md")), File.ReadAllText(Path.Combine(inc.Dir, "report.json")));
            }
            var full = Owners();
            Assert.True(full.Count > full.Count(o => o.Meaningful)); // the keep has owners to drop
            Assert.Contains(full.Where(o => o.Meaningful).Take(3), o => o.Owner.Kind == OwnerKind.ClaudeSession && o.Members.Count > OwnerRanking.KeptMembers + 1);
            var before = Write(full);
            var after = Write(OwnerRanking.Keep(Owners()));
            Assert.Contains("## In plain words", before.md);
            Assert.Equal(before.md, after.md);
            Assert.Equal(before.json, after.json);

            // The vendor bundle's incidents.md and the Pool causes row read the first meaningful owners' summary and strength.
            var shown = full.Where(o => o.Meaningful).Take(3).Select(o => (o.Name, o.Summary, o.Confidence)).ToList();
            Assert.Equal(shown, OwnerRanking.Keep(Owners()).Take(3).Select(o => (o.Name, o.Summary, o.Confidence)));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// The answer line adds up the bytes every meaningful owner explains, not only the three the reports list, so an
    /// incident keeps every meaningful owner; the others explain nothing the line counts.
    /// </summary>
    [Fact]
    public void TheAnswerLineStillAddsUpEveryMeaningfulOwner()
    {
        var info = new TagKnowledge(null, null).Lookup(ChurnWorld.Tag);
        List<OwnerSuspect> Owners() => Enumerable.Range(0, 7).Select(i => new OwnerSuspect
        {
            Owner = new Owner(OwnerKind.App, $"app{i}", $"app{i}.exe"), Members = [], Identities = [],
            Meaningful = i != 6, Confidence = i == 0 ? Confidence.Correlated : Confidence.Weak,
            ExplainedBytes = i == 4 ? 30L << 20 : i == 6 ? 50L << 20 : 0,
        }).ToList();
        string Line(IReadOnlyList<OwnerSuspect> owners) => OwnerRanking.Answer(info, ChurnWorld.Tag, 400L << 20, 0, 400L << 20, "10 min", owners).Line;
        string full = Line(Owners());
        Assert.Contains("accounts for 30 MB", full);
        Assert.Equal(full, Line(OwnerRanking.Keep(Owners())));
        Assert.NotEqual(full, Line(OwnerRanking.Keep(Owners(), maxOwners: 3))); // what keeping only the listed owners would print
    }

    /// <summary>
    /// Bytes still reachable from what <paramref name="make"/> returns, after a full collection on each side. The count is
    /// the whole process's, so memory other tests freed meanwhile can make one measure come out at nothing or below: then
    /// it is measured again (at most three times), and the largest is kept.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static long Retained(Func<object> make)
    {
        GC.KeepAlive(make()); // warm-up: JIT and static caches are not the object's cost
        long largest = long.MinValue;
        for (int i = 0; i < 3 && largest <= 0; i++)
        {
            long before = GC.GetTotalMemory(forceFullCollection: true);
            var kept = make();
            long after = GC.GetTotalMemory(forceFullCollection: true);
            GC.KeepAlive(kept);
            largest = Math.Max(largest, after - before);
        }
        return largest;
    }
}
