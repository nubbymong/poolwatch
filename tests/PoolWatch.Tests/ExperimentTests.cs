using PoolWatch.Core;
using PoolWatch.Core.Experiments;
using PoolWatch.Core.Findings;
using PoolWatch.Core.Model;

namespace PoolWatch.Tests;

public class ExperimentTests
{
    private static readonly DateTime T0 = new(2026, 10, 26, 9, 0, 0, DateTimeKind.Utc);
    private const long MB = 1L << 20;
    private static readonly PoolTag File = PoolTag.Parse("File"), Toke = PoolTag.Parse("Toke");

    /// <summary>A sample at minute <paramref name="min"/> with File and Toke at these sizes and the app running or not.</summary>
    private static Sample S(double min, long fileMb, long tokeMb, bool app, ulong commitMb = 8000)
    {
        var tags = new List<PoolTagEntry>
        {
            new(File, 0, 0, 0, 1000, 10, (ulong)(fileMb * MB)),
            new(Toke, 500, 5, (ulong)(tokeMb * MB), 0, 0, 0),
        };
        var procs = new List<ProcessSample> { new() { Pid = 4, ImageName = "System" } };
        if (app) procs.Add(new ProcessSample { Pid = 900, ParentPid = 4, ImageName = "ChatGPT.exe" });
        ulong np = (ulong)((200 + fileMb) * MB), pg = (ulong)((300 + tokeMb) * MB);
        return new Sample(T0.AddMinutes(min), new PoolTotals(pg, np, pg, np), tags, procs) { System = new SystemCounters(commitMb * (ulong)MB, 32000 * (ulong)MB, 0, 0, 90000, 200) };
    }

    private static List<ExperimentStep> Run(Experiment e, IEnumerable<Sample> samples) => samples.Select(e.Observe).OfType<ExperimentStep>().ToList();

    [Fact]
    public void GrowthStopsWhenTheAppCloses()
    {
        var e = new Experiment("ChatGPT", TimeSpan.FromMinutes(10), T0);
        var samples = new List<Sample>();
        for (int m = 0; m <= 30; m++)
        {
            long file = m <= 10 ? 100 : m <= 20 ? 100 + (m - 10) * 12 : 220 - Math.Min(10, m - 20) * 2; // +120 MB with it, then -20 MB
            samples.Add(S(m, file, 50, app: m > 10 && m <= 20));
        }
        var steps = Run(e, samples);
        Assert.Equal([ExperimentPhase.Baseline, ExperimentPhase.AppRunning, ExperimentPhase.AfterClose, ExperimentPhase.Done], steps.Select(s => s.Phase));
        Assert.Contains("start ChatGPT now", steps[1].Prompt);
        Assert.Contains("close ChatGPT now", steps[2].Prompt);
        var r = e.Result!;
        Assert.StartsWith("Growth stopped when ChatGPT closed: non-paged pool grew +120 MB with it running and -20 MB after it closed", r.Statement);
        Assert.Contains("File +120 MB then -20 MB", r.Statement);
        Assert.Contains("20 MB was released", r.Statement);
        Assert.Empty(r.Notes);
        Assert.Equal(FindingKind.Experiment, steps[^1].Finding.Kind);
        Assert.False(e.IsActive);
        Assert.Contains("File,0,125829120,-20971520,1,1", r.Csv());
        Assert.Contains("| non-paged pool | 0 B | +120 MB | -20 MB |", r.Markdown("ChatGPT", e.PhaseLength));
    }

    [Fact]
    public void GrowthThatContinuesAfterClosingIsNotTheApp()
    {
        var e = new Experiment("Codex", TimeSpan.FromMinutes(10), T0);
        var samples = Enumerable.Range(0, 31).Select(m => S(m, 100, 50 + (m > 10 ? (m - 10) * 4 : 0), app: false)).ToList();
        Run(e, samples);
        var r = e.Result!;
        Assert.StartsWith("Growth did not stop when Codex closed: paged pool grew +40 MB with it running and +40 MB after it closed", r.Statement);
        Assert.Contains(r.Notes, n => n.Contains("saw no Codex process"));
    }

    [Fact]
    public void NothingGrows()
    {
        var e = new Experiment("Stream Deck", TimeSpan.FromMinutes(1), T0);
        var samples = Enumerable.Range(0, 13).Select(i => S(i * 0.25, 100, 50, app: i is > 4 and <= 8)).ToList();
        samples = samples.Select(s => s with { Processes = s.Processes.Select(p => p.ImageName == "ChatGPT.exe" ? new ProcessSample { Pid = 901, ImageName = "StreamDeck.exe" } : p).ToList() }).ToList();
        Run(e, samples);
        Assert.StartsWith("Nothing grew with Stream Deck running", e.Result!.Statement);
        Assert.Empty(e.Result.Notes); // StreamDeck.exe matched "Stream Deck"
    }

    [Fact]
    public void StatusAndCancel()
    {
        var e = new Experiment("ChatGPT", TimeSpan.FromMinutes(10), T0);
        e.Observe(S(0, 100, 50, false));
        Assert.Equal("Experiment: phase 1 of 3: baseline, leave ChatGPT closed, 6 min left", e.Status(T0.AddMinutes(4)));
        var f = e.Cancel(T0.AddMinutes(5));
        Assert.Equal("Experiment with ChatGPT cancelled", f!.Title);
        Assert.Null(e.Observe(S(11, 100, 50, false)));
        Assert.Null(e.Cancel(T0.AddMinutes(12)));
    }

    [Theory]
    [InlineData("Ctrl+Alt+M", Hotkey.Control | Hotkey.Alt, 'M')]
    [InlineData("win + shift + f9", Hotkey.Win | Hotkey.Shift, 0x78)]
    [InlineData("Alt+1", Hotkey.Alt, '1')]
    public void HotkeyParses(string text, uint mods, uint vk) => Assert.Equal((mods, vk), Hotkey.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("M")]
    [InlineData("Shift+M")]
    [InlineData("Ctrl+M+N")]
    [InlineData("Ctrl+Space")]
    public void HotkeyRejects(string text) => Assert.Null(Hotkey.Parse(text));

    [Fact]
    public void MarksAreCleanedAndRequestsTaken()
    {
        Assert.Null(Marks.Finding("   ", T0, "tray"));
        var f = Marks.Finding(" closed\r\nChatGPT\t", T0, "tray")!;
        Assert.Equal("Mark: closed  ChatGPT", f.Title);
        Assert.Equal(FindingKind.Mark, f.Kind);
        Assert.Equal(Marks.MaxLength, Marks.Clean(new string('x', 500)).Length);

        string dir = Path.Combine(Path.GetTempPath(), "pw-marks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            System.IO.File.WriteAllText(Path.Combine(dir, Marks.RequestName(T0.AddSeconds(5), 2)), "second");
            System.IO.File.WriteAllText(Path.Combine(dir, Marks.RequestName(T0, 1)), "lag");
            System.IO.File.WriteAllText(Path.Combine(dir, "other.txt"), "ignored");
            var got = Marks.TakeRequests(dir, p => p);
            Assert.Equal([(T0, "lag"), (T0.AddSeconds(5), "second")], got);
            Assert.Equal(["other.txt"], Directory.GetFiles(dir).Select(Path.GetFileName));
            Assert.Empty(Marks.TakeRequests(Path.Combine(dir, "missing"), p => p));
        }
        finally { Directory.Delete(dir, true); }
    }
}
