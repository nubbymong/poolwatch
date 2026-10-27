using PoolWatch.Core.Detection;
using PoolWatch.Core.Model;
using PoolWatch.Core.Util;

namespace PoolWatch.Tests;

/// <summary>C5 (review Part 2): the decisions behind the window's refresh, kept in Core so they are unit-tested.</summary>
public class UiRefreshTests
{
    [Fact]
    public void ANewItemReplacesTheTextEvenWithASelection()
    {
        // The bug: the answer / details box skipped every refresh while text was selected, so it froze on the old tag.
        Assert.Equal(TextRefreshAction.Replace, TextRefresh.Plan("old tag answer", "new tag answer", sameItem: false, selectionStart: 0, selectionLength: 3));
        Assert.Equal(TextRefreshAction.Replace, TextRefresh.Plan("old", "new", sameItem: false, 0, 0));
        Assert.Equal(TextRefreshAction.None, TextRefresh.Plan("same", "same", sameItem: false, 0, 2)); // nothing to redraw
    }

    [Fact]
    public void TheSameItemKeepsTheSelectionOnlyWhileItIsUnchanged()
    {
        Assert.Equal(TextRefreshAction.None, TextRefresh.Plan("a", "a", sameItem: true, 0, 1));
        Assert.Equal(TextRefreshAction.Update, TextRefresh.Plan("line 1\r\nline 2", "line 1\r\nline 2b", sameItem: true, 3, 0));
        // "line 1" selected, only a later line changed: update and keep the selection.
        Assert.Equal(TextRefreshAction.UpdateKeepSelection, TextRefresh.Plan("line 1\r\nline 2", "line 1\r\nline 2b", sameItem: true, 0, 6));
        // The selected text itself changed: hold the old text until the selection is cleared.
        Assert.Equal(TextRefreshAction.Hold, TextRefresh.Plan("12 MB now", "13 MB now", sameItem: true, 0, 5));
        // The new text is shorter than the selection's end: hold.
        Assert.Equal(TextRefreshAction.Hold, TextRefresh.Plan("abcdef", "abc", sameItem: true, 2, 4));
        // A selection left in a box the user has moved away from does not freeze it: update (the selection is dropped).
        Assert.Equal(TextRefreshAction.Update, TextRefresh.Plan("12 MB now", "13 MB now", sameItem: true, 0, 5, focused: false));
        Assert.Equal(TextRefreshAction.UpdateKeepSelection, TextRefresh.Plan("12 MB now", "12 MB now!", sameItem: true, 0, 5, focused: false));
    }

    [Fact]
    public void TheTopRowIsKeptAcrossARefresh()
    {
        var present = new HashSet<object> { "b", "c", "d" };
        Assert.Null(ScrollAnchor.Pick(0, ["a", "b"], present.Contains));              // at the top: stays at the top
        Assert.Equal("b", ScrollAnchor.Pick(5, ["b", "c"], present.Contains));        // the old top row is kept on top
        Assert.Equal("c", ScrollAnchor.Pick(5, ["gone", "c", "d"], present.Contains)); // it left: the next survivor takes its place
        Assert.Null(ScrollAnchor.Pick(5, ["x", "y"], present.Contains));               // nothing left to anchor to
        Assert.Null(ScrollAnchor.Pick(5, [], present.Contains));
    }

    [Fact]
    public void AppEventsWithTheSameTimeGetDifferentKeys()
    {
        var t = new DateTime(2026, 10, 26, 8, 0, 0, DateTimeKind.Utc);
        var a = new LifecycleEvent { TimeUtc = t, Kind = LifecycleKind.Info, Image = "AppModel", Detail = "ChatGPT activated" };
        var b = a with { Detail = "Codex activated" };
        Assert.NotEqual(RowKeys.ForEvent(a), RowKeys.ForEvent(b));
        // Identical events still become two rows: later duplicates are numbered, in order, and stay unique.
        var keys = RowKeys.Unique([RowKeys.ForEvent(a), RowKeys.ForEvent(a), "x", RowKeys.ForEvent(a), "x"]);
        Assert.Equal(5, keys.Distinct().Count());
        Assert.Equal(RowKeys.ForEvent(a), keys[0]);
        Assert.Equal(RowKeys.ForEvent(a) + "#2", keys[1]);
        Assert.Equal(RowKeys.ForEvent(a) + "#3", keys[3]);
        Assert.Equal("x#2", keys[4]);
        // A real key that already looks like a numbered duplicate is not reused.
        var tricky = RowKeys.Unique(["k#2", "k", "k"]);
        Assert.Equal(3, tricky.Distinct().Count());
        Assert.Equal(["k#2", "k", "k#3"], tricky);
    }

    [Fact]
    public void TagGrowthIsTotalForAnySeries()
    {
        // TagView's growth figure: never indexes an empty series, and clamps an onset past the end.
        Assert.Equal(0, Detector.GrowthSinceOnset([], 0));
        Assert.Equal(0, Detector.GrowthSinceOnset([], 7));
        Assert.Equal(0, Detector.GrowthSinceOnset([100], 0));
        Assert.Equal(400, Detector.GrowthSinceOnset([200, 100, 150, 500], 1));  // last minus the floor up to the onset
        Assert.Equal(400, Detector.GrowthSinceOnset([200, 100, 150, 500], 99)); // onset clamped to the end
        Assert.Equal(300, Detector.GrowthSinceOnset([200, 100, 150, 500], -3)); // onset clamped to 0: floor = first value
        Assert.Equal(-300, Detector.GrowthSinceOnset([500, 300, 200], 1));      // fell below the floor: plain change over the window
    }
}
