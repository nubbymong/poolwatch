using PoolWatch.Core.Services.Agent;
using PoolWatch.Ui;
using Xunit.Abstractions;

namespace PoolWatch.AppTests;

/// <summary>
/// The two AI-agent windows on the smallest working area PoolWatch fits (1024 x 600 logical) at 100, 125, 150 and 200%,
/// at the design's size where there is room and at their least size: each is never larger than the working area, and
/// laid out at its client size nothing is cut off, nothing scrolls sideways, the prompt keeps lines to read and the review
/// keeps rows to read, and the buttons stay in the window. As far as a test can scale it (its text, its sizes). Never
/// shown: each window's content sits in a panel of the window's client size, which no desktop cuts.
/// </summary>
[Collection(nameof(WholeWindowLayout))] // lays windows out: one class at a time, after the parallel ones
public sealed class AgentWindowsLayoutTests(ITestOutputHelper output) : IDisposable
{
    private readonly AgentPc _pc = new();
    private readonly FakeAgentFiles _files = new();

    public void Dispose() => _pc.Dispose();

    public static IEnumerable<object[]> Scales() => [[100], [125], [150], [200]];

    private static Rectangle Smallest(int percent) => new(0, 0, 1024 * percent / 100, 600 * percent / 100);

    /// <summary>A window's client area at <paramref name="percent"/>: its frame and title bar taken off.</summary>
    private static Size Client(Size window, int percent) => new(window.Width - 16 * percent / 100, window.Height - 39 * percent / 100);

    /// <summary>
    /// The window's content laid out in a panel of <paramref name="client"/> in a window that is never shown; the host
    /// must be disposed with the window.
    /// </summary>
    private static Form Lay(AgentWindow window, Size client)
    {
        var host = new Form { FormBorderStyle = FormBorderStyle.None, Font = window.Font };
        var panel = new Panel { Size = client, Font = window.Font, Margin = Padding.Empty };
        host.Controls.Add(panel);
        window.Controls.Remove(window.Content);
        panel.Controls.Add(window.Content);
        host.CreateControl();
        Handles(panel);
        for (int i = 0; i < 3; i++) Layout(panel);
        return host;
    }

    private static void Handles(Control c)
    {
        _ = c.Handle;
        foreach (Control child in c.Controls) Handles(child);
    }

    private static void Layout(Control c)
    {
        c.PerformLayout();
        foreach (Control child in c.Controls) Layout(child);
    }

    private static Rectangle In(Control c, Control ancestor)
    {
        var r = c.Bounds;
        for (var p = c.Parent; p is not null && p != ancestor; p = p.Parent) r.Offset(p.Location);
        return r;
    }

    [Theory]
    [MemberData(nameof(Scales))]
    public void NeitherWindowIsLargerThanTheSmallestWorkingArea(int percent)
    {
        var area = Smallest(percent);
        // The design's sizes, cut to the area: the prompt window is taller than 600, the review is not.
        Assert.Equal(new Size(900 * percent / 100, area.Height), AgentWindow.Within(new Size(900 * percent / 100, 660 * percent / 100), area));
        Assert.Equal(new Size(680 * percent / 100, 560 * percent / 100), AgentWindow.Within(new Size(680 * percent / 100, 560 * percent / 100), area));
        using var prompt = new AgentPromptForm(() => "", _files, new InlineWork(), percent);
        using var review = new AgentReviewForm(() => AgentReview.Failed("x"), (_, _) => [], _files, new InlineWork(), percent);
        foreach (AgentWindow w in new AgentWindow[] { prompt, review })
        {
            w.FitTo(area);
            Assert.True(w.Width <= area.Width && w.Height <= area.Height, $"{w.Text} is {w.Size} on {area.Size}");
            Assert.True(w.MinimumSize.Width <= w.Width && w.MinimumSize.Height <= w.Height, $"{w.Text}'s least size {w.MinimumSize} is over {w.Size}");
        }
    }

    public static IEnumerable<object[]> PromptSizes() =>
    [
        [900, 600, 100], [900, 600, 125], [900, 600, 150], [900, 600, 200], // the design's, on the smallest working area
        [900, 660, 100],                                                    // the design's, where there is room
        [640, 440, 100], [640, 440, 200],                                   // the least it shrinks to
    ];

    [Theory]
    [MemberData(nameof(PromptSizes))]
    public void ThePromptWindowKeepsEveryPartInViewAndLinesToRead(int width, int height, int percent)
    {
        using var form = new AgentPromptForm(() => AgentPrompt.Build(_pc.Items(), "Windows 11 Pro 25H2 (build 26220.9757)"), _files, new InlineWork(), percent);
        form.Start();
        using var host = Lay(form, Client(new Size(width * percent / 100, height * percent / 100), percent));
        var problems = new List<string>();
        MainForm.ClippedControls(form.Content, problems, WindowFitTests.OwnVisible);
        Assert.Empty(problems);

        // The left column scrolls only up and down, its steps and the green panel inside its width.
        var column = form.Column.Controls[0];
        Assert.True(column.Right <= form.Column.ClientSize.Width, $"the column's content ends at {column.Right}, the column at {form.Column.ClientSize.Width}");
        Assert.False(form.Column.HorizontalScroll.Visible);
        // The preview keeps lines to read, and the buttons stay inside the window.
        int lines = form.Preview.ClientSize.Height / form.Preview.Font.Height;
        Assert.True(lines >= (height >= 600 ? 18 : 10), $"the preview shows {lines} lines");
        var save = In(form.SaveButton, form.Content);
        Assert.True(form.Content.ClientRectangle.Contains(save), $"Save the prompt is at {save} in {form.Content.ClientSize}");
        var close = In(form.CloseButton, form.Content);
        Assert.True(form.Content.ClientRectangle.Contains(close), $"Close is at {close} in {form.Content.ClientSize}");
        output.WriteLine($"{width} x {height} at {percent}%: {lines} lines; left column needs {column.Bottom} of {form.Column.ClientSize.Height} px" +
                         (form.Column.VerticalScroll.Visible ? " (it scrolls)" : ""));
    }

    public static IEnumerable<object[]> ReviewSizes() =>
    [
        [680, 560, 100], [680, 560, 125], [680, 560, 150], [680, 560, 200], // the design's, which fits the smallest working area
        [560, 420, 100], [560, 420, 200],                                   // the least it shrinks to
    ];

    [Theory]
    [MemberData(nameof(ReviewSizes))]
    public void TheReviewKeepsRowsToReadAndItsButtonsInView(int width, int height, int percent)
    {
        using var form = new AgentReviewForm(() => AgentReview.Of(_pc.Plan()), (_, _) => [], _files, new InlineWork(), percent);
        form.Start();
        using var host = Lay(form, Client(new Size(width * percent / 100, height * percent / 100), percent));
        var problems = new List<string>();
        MainForm.ClippedControls(form.Content, problems, WindowFitTests.OwnVisible);
        Assert.Empty(problems);

        // The list scrolls only up and down: every line in it is inside its width.
        Assert.False(form.List.HorizontalScroll.Visible);
        var lines = Enumerable.Range(0, form.List.Entries.Count).Select(form.List.EntryBounds).ToList();
        Assert.NotEmpty(lines);
        for (int i = 0; i < lines.Count; i++)
            Assert.True(lines[i].Right <= form.List.ClientSize.Width, $"\"{form.List.Entries[i].Text}\" ends at {lines[i].Right}, the list at {form.List.ClientSize.Width}");
        // At least the first part's heading and two of its rows show without scrolling.
        int shown = lines.Count(r => r.Bottom <= form.List.ClientSize.Height);
        Assert.True(shown >= 3, $"{shown} rows show in a list of {form.List.ClientSize}");
        foreach (var b in new[] { form.ApplyButton, form.CancelChanges })
        {
            var r = In(b, form.Content);
            Assert.True(form.Content.ClientRectangle.Contains(r), $"{b.Text} is at {r} in {form.Content.ClientSize}");
        }
        output.WriteLine($"{width} x {height} at {percent}%: {shown} rows show; the list is {form.List.ClientSize}");
    }
}
