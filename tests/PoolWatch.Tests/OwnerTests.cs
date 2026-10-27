using PoolWatch.Core;
using PoolWatch.Core.Correlation;
using PoolWatch.Core.Identity;
using PoolWatch.Core.Tags;

namespace PoolWatch.Tests;

public class OwnerTests
{
    private static readonly PoolTag File = PoolTag.Parse("File");

    /// <summary>A Claude session whose three sed.exe calls and node worker drive the growth, and an idle scdaemon.</summary>
    private static (World w, List<Suspect> suspects, CorrelationWindow win, long growth) Build()
    {
        var w = new World();
        w.AddProcess(4, 0, "System");
        w.AddProcess(10, 4, "claude.exe");
        w.AddProcess(20, 10, "bash.exe");
        w.AddProcess(30, 20, "node.exe");
        w.AddProcess(31, 20, "sed.exe");
        w.AddProcess(40, 4, "StreamDeck.exe");
        w.AddProcess(41, 40, "scdaemon.exe");
        long tag = 50 * Settings.MB;
        for (int i = 0; i < 10; i++) w.Sample(("File", tag));
        var rnd = new Random(5);
        for (int i = 0; i < 30; i++)
        {
            int ops = 1000 + rnd.Next(4000);
            w.Io(30, other: ops, handles: ops / 10);
            w.Io(31, other: ops / 4);
            w.Io(40, other: 50 + rnd.Next(50));
            tag += ops * 300L;
            w.Sample(("File", tag));
            if (i % 5 == 0) w.ScanHandles();
        }
        var win = new CorrelationWindow(World.T0, World.T0 + w.Interval * 10, null, w.Now);
        var suspects = Correlator.Rank(w.History, w.Registry, File, win, maxSuspects: 100);
        return (w, suspects, win, tag - 50 * Settings.MB);
    }

    private static Func<Suspect, ProcessIdentity> Ids(World w) => s => ProcessIdentities.Of(w.Registry.Ancestry(s.Key));

    [Fact]
    public void GroupsByOwnerAndWritesAPlainAnswer()
    {
        var (w, suspects, win, growth) = Build();
        var owners = OwnerRanking.Rank(w.History, suspects, File, win, growth, Ids(w));
        var top = owners[0];
        Assert.Equal(OwnerKind.ClaudeSession, top.Owner.Kind);
        Assert.Equal(2, top.Members.Count); // node + sed: one owner, not two rows
        Assert.True(top.Meaningful);
        Assert.Contains(top.Plain, p => p.StartsWith("file and other I/O:"));
        Assert.Contains(top.Plain, p => p.StartsWith("rises and falls with the tag: strongly"));
        Assert.DoesNotContain(top.Plain, p => p.Contains("Spearman") || p.Contains("quota") || p.Contains("episode"));
        var answer = OwnerRanking.Answer(new TagInfo("nt", "File objects", "builtin"), File, 0, growth + 50 * Settings.MB, growth, "the last 5 min", owners);
        Assert.StartsWith("File objects (File, non-paged) grew", answer.Line);
        Assert.Contains("Most likely cause: Claude Code session", answer.Line);
        Assert.DoesNotContain("No clear cause", answer.Line);
    }

    [Fact]
    public void SaysWhenSuspectsExplainLittle()
    {
        var owner = new OwnerSuspect { Owner = new Owner(OwnerKind.App, "app:x", "Stream Deck"), Members = [], Identities = [], Meaningful = true, ExplainedBytes = 600 * 1024, Confidence = Confidence.Weak };
        var a = OwnerRanking.Answer(new TagInfo("nt", "File objects", "builtin"), File, 0, 1, 300 * Settings.MB, "the last hour", [owner]);
        Assert.Equal("File objects (File, non-paged) grew 300 MB in the last hour. No clear cause yet: the suspects below account for 600 KB of a 300 MB rise.", a.Line);
    }

    [Fact]
    public void PlainWords()
    {
        Assert.Equal("strongly", OwnerRanking.Moves(0.8));
        Assert.Equal("somewhat", OwnerRanking.Moves(0.4));
        Assert.Equal("not", OwnerRanking.Moves(0.1));
        Assert.Equal("3.5 M", OwnerRanking.Count(3_500_000));
        Assert.Equal("strong", OwnerRanking.Strength(Confidence.StronglyCorrelated));
    }
}
