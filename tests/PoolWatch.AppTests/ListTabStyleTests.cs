using System.Drawing.Imaging;
using PoolWatch.Core.Impact;
using PoolWatch.Core.Services;
using PoolWatch.Ui;

namespace PoolWatch.AppTests;

/// <summary>
/// The look of the two list tabs (their buttons, the cost meter, the right-hand ends of their bars) and the rules of the
/// Services tab's buttons, all without a window shown.
/// </summary>
[Collection(nameof(WholeWindowLayout))] // lays windows out: one class at a time, after the parallel ones
public class ListTabStyleTests
{
    [Fact]
    public void EachKindOfButtonHasItsOwnColours()
    {
        var primary = ListTabStyle.ColoursOf(ListTabStyle.Kind.Primary, enabled: true);
        Assert.Equal((ListTabStyle.Accent, Color.White, true), (primary.Back, primary.Fore, primary.Bold));
        var accent = ListTabStyle.ColoursOf(ListTabStyle.Kind.Accent, true);
        Assert.Equal((Color.White, ListTabStyle.Accent, ListTabStyle.Accent), (accent.Back, accent.Fore, accent.Border));
        var danger = ListTabStyle.ColoursOf(ListTabStyle.Kind.Danger, true);
        Assert.Equal((Color.White, ListTabStyle.Danger), (danger.Back, danger.Fore));
        var plain = ListTabStyle.ColoursOf(ListTabStyle.Kind.Plain, true);
        Assert.Equal((Color.White, ListTabStyle.Ink, ListTabStyle.Border), (plain.Back, plain.Fore, plain.Border));
        // Disabled, none is left in its colours (a blue button with grey text reads as broken).
        foreach (var k in Enum.GetValues<ListTabStyle.Kind>())
            Assert.Equal(SystemColors.GrayText, ListTabStyle.ColoursOf(k, false).Fore);
        Assert.Equal(ListTabStyle.DisabledBack, ListTabStyle.ColoursOf(ListTabStyle.Kind.Primary, false).Back);
    }

    [Fact]
    public void AStyledButtonKeepsItsKindWhenItIsDisabledAndEnabledAgain()
    {
        using var b = new Button { Text = "Turn off 1 service" };
        ListTabStyle.Style(b, ListTabStyle.Kind.Primary);
        Assert.Equal(ListTabStyle.Kind.Primary, ListTabStyle.KindOf(b));
        if (SystemInformation.HighContrast) { Assert.True(b.UseVisualStyleBackColor); return; }
        Assert.Equal((FlatStyle.Flat, ListTabStyle.Accent, true), (b.FlatStyle, b.BackColor, b.Font.Bold));
        b.Enabled = false;
        Assert.Equal((ListTabStyle.DisabledBack, true), (b.BackColor, b.Font.Bold));
        b.Enabled = true;
        Assert.Equal((ListTabStyle.Accent, Color.White), (b.BackColor, b.ForeColor));
        Assert.Null(ListTabStyle.KindOf(new Button()));
    }

    [Fact]
    public void ABoldButtonTakesTheBoldOfItsParentsFontAndFollowsIt()
    {
        if (SystemInformation.HighContrast) return; // then it keeps Windows' own look and font
        using var host = new Form { Font = new Font("Segoe UI", 9f) };
        var bar = new FlowLayoutPanel();
        var primary = ListTabStyle.Style(new Button { Text = "Turn off 1 service" }, ListTabStyle.Kind.Primary);
        var plain = ListTabStyle.Style(new Button { Text = "Refresh" }, ListTabStyle.Kind.Plain);
        bar.Controls.AddRange([primary, plain]); // styled before it had a parent, as the tabs do
        host.Controls.Add(bar);
        Assert.Equal((true, 9f, "Segoe UI"), (primary.Font.Bold, primary.Font.SizeInPoints, primary.Font.Name));
        // Display scaling or another system font reaches it as it reaches the plain button beside it.
        host.Font = new Font("Segoe UI", 18f);
        Assert.Equal((true, 18f), (primary.Font.Bold, primary.Font.SizeInPoints));
        Assert.Equal((false, 18f), (plain.Font.Bold, plain.Font.SizeInPoints));
        // Moved to another parent, it follows that one.
        using var other = new Form { Font = new Font("Segoe UI", 12f) };
        other.Controls.Add(primary);
        Assert.Equal((true, 12f), (primary.Font.Bold, primary.Font.SizeInPoints));
        host.Font = new Font("Segoe UI", 9f);
        Assert.Equal(12f, primary.Font.SizeInPoints);                    // the old parent no longer reaches it
    }

    [Fact]
    public void ColumnsFillTheListAndNeverPassIt()
    {
        int[] ideal = [250, 210, 170, 140, 410], least = [170, 130, 120, 100, 150];
        // Room for all: each at its ideal width, the last taking the rest.
        Assert.Equal([250, 210, 170, 140, 530], ListTabStyle.FitColumns(1300, ideal, least, 4));
        // Less room: each gives up the same share of the way to its least, and together they fill the list exactly.
        var mid = ListTabStyle.FitColumns(939, ideal, least, 4);
        Assert.Equal(939, mid.Sum());
        for (int i = 0; i < 5; i++) Assert.InRange(mid[i], least[i], ideal[i]);
        Assert.True(ideal[4] - mid[4] > ideal[3] - mid[3], "the column with the most to give gives up the most pixels");
        // The window's minimum size: the least widths, filling it; below that the list scrolls, each at its least.
        Assert.Equal(675, ListTabStyle.FitColumns(675, ideal, least, 4).Sum());
        Assert.Equal(least, ListTabStyle.FitColumns(500, ideal, least, 4));
        if (!SystemInformation.HighContrast) Assert.Equal(ListTabStyle.Faint, ListTabStyle.FaintText);
    }

    [Theory]
    [InlineData(ImpactLevel.None, 0)]
    [InlineData(ImpactLevel.Low, 1)]
    [InlineData(ImpactLevel.Medium, 2)]
    [InlineData(ImpactLevel.High, 3)]
    public void TheMeterFillsOneBarPerLevelInTheLevelsColour(ImpactLevel level, int filled)
    {
        if (SystemInformation.HighContrast) return; // then the bars are drawn in the system colours
        Assert.Equal(filled, ImpactScale.Filled(level));
        using var bmp = new Bitmap(60, 20, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            ListTabStyle.PaintMeter(g, new Rectangle(0, 0, 60, 20), level, n => n);
        }
        // The middle of each bar (10 wide, 2 apart, 4 high, centred).
        var colours = Enumerable.Range(0, 3).Select(i => bmp.GetPixel(i * 12 + 5, 10).ToArgb()).ToList();
        for (int i = 0; i < 3; i++)
            Assert.Equal(i < filled ? ImpactScale.ColorOf(level)!.Value.ToArgb() : UiColors.Track.ToArgb(), colours[i]);
        Assert.Equal("None Low Medium High", string.Join(" ", Enum.GetValues<ImpactLevel>().Select(ImpactScale.Word)));
    }

    [Theory]
    [InlineData(900)] // room on the line of the others: at its right end
    [InlineData(400)] // no room: on a line of its own, at its right end
    public void TheLastControlOfABarSitsAtTheRightEndOfItsLine(int width)
    {
        using var host = new Form();
        var bar = new FlowLayoutPanel { Width = width, Height = 200, WrapContents = true };
        host.Controls.Add(bar);
        var a = new Button { Width = 150, Margin = new Padding(0) };
        var b = new Button { Width = 150, Margin = new Padding(0) };
        var right = new Button { Width = 200, Margin = new Padding(0) };
        bar.Controls.AddRange([a, b, right]);
        ListTabStyle.AlignRight(bar, right, 12);
        bar.PerformLayout();
        Assert.InRange(bar.ClientSize.Width - right.Right, 0, 2);
        Assert.Equal(width >= 300 + 12 + 200 + 1, right.Top == a.Top);
    }

    [Fact]
    public void TheGreenNoteIsSizedToItsText()
    {
        using var host = new Form { Font = new Font("Segoe UI", 9f) };
        var note = new GreenNote("Anything you turn off can be put back from Active changes.");
        host.Controls.Add(note);
        var size = TextRenderer.MeasureText(note.Text, note.Font);
        Assert.True(note.GetPreferredSize(Size.Empty).Width > size.Width, "the shield goes before the text");
        Assert.Equal(note.Text, note.AccessibleName);
        using var bmp = new Bitmap(note.Width, note.Height);
        note.DrawToBitmap(bmp, new Rectangle(Point.Empty, note.Size));
    }

    // ---------------------------------------------------------------- the Services tab's buttons

    private static ServiceOption Option(string name, ServiceChange? change = null, string? offOutside = null, ServiceStartType? windowsDefault = null) => new()
    {
        Service = new ServiceView { Name = name, StartType = ServiceStartType.Disabled },
        Group = ServiceGroup.Optional, PoolWatchChange = change, OffOutsidePoolWatch = offOutside, WindowsDefault = windowsDefault,
    };

    private static readonly ServiceChange Change = new()
    {
        Service = "MapsBroker", OriginalStart = ServiceStartType.Automatic, OriginalRunning = true, NewStart = ServiceStartType.Disabled, ChangedUtc = DateTime.UtcNow, Via = "manual",
    };

    [Fact]
    public void TurnOffSaysHowManyItTurnsOffAndIsOnlyPressableThen()
    {
        Assert.Equal(("Turn off", "Turn off 1 service", "Turn off 3 services"), (ServicesPageText.TurnOffText(0), ServicesPageText.TurnOffText(1), ServicesPageText.TurnOffText(3)));
        Assert.False(ServicesPageText.Buttons(busy: false, change: 0, changed: 0, null, null).TurnOff);
        Assert.True(ServicesPageText.Buttons(false, 2, 0, null, null).TurnOff);
        Assert.False(ServicesPageText.Buttons(busy: true, 2, 0, null, null).TurnOff); // while the list is read or a change runs
    }

    [Fact]
    public void PutBackOffersOnlyWhatCanBePutBack()
    {
        var none = ServicesPageText.Buttons(false, 0, changed: 0, null, selected: null);
        Assert.Equal((false, false, false, false), (none.PutBack, none.PutBackSelected, none.ToWindowsDefault, none.PutAllBack));
        var some = ServicesPageText.Buttons(false, 0, changed: 2, null, Option("Fax"));
        Assert.Equal((true, false, false, true), (some.PutBack, some.PutBackSelected, some.ToWindowsDefault, some.PutAllBack));
        var mine = ServicesPageText.Buttons(false, 0, 1, null, Option("MapsBroker", Change));
        Assert.True(mine.PutBackSelected);
        var outside = ServicesPageText.Buttons(false, 0, 0, null, Option("Fax", offOutside: ServicePlan.OffOutside, windowsDefault: ServiceStartType.Manual));
        Assert.Equal((true, true, false), (outside.PutBack, outside.ToWindowsDefault, outside.PutAllBack));
        // While the record cannot be read, Put back goes to the controller, which says why: it is not hidden.
        var unreadable = ServicesPageText.Buttons(false, 0, 0, "damaged", Option("Fax"));
        Assert.Equal((true, true, true), (unreadable.PutBack, unreadable.PutBackSelected, unreadable.PutAllBack));
        var busy = ServicesPageText.Buttons(true, 1, 1, null, Option("MapsBroker", Change));
        Assert.Equal((false, false, false, false), (busy.PutBack, busy.PutBackSelected, busy.PutAllBack, busy.Refresh));
    }
}
