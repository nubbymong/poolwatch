using PoolWatch.Engine;

namespace PoolWatch.AppTests;

/// <summary>
/// PoolWatch's own memory trim: a compacting collection only when the heap grew past what the last full collection left,
/// and the working set emptied only after start-up and when the window hides (it faults straight back in otherwise).
/// The trim acts on the test process itself, never on another one.
/// </summary>
public class MemoryTrimTests
{
    private const long MB = 1L << 20;

    [Fact]
    public void ItCompactsOnlyWhenTheHeapGrewAndEmptiesTheWorkingSetOnlyWhenReleasing()
    {
        Assert.Equal(MemoryTrim.TrimAction.None, MemoryTrim.Decide(release: false, heapBytes: 100 * MB, lastFullHeapBytes: 70 * MB));
        Assert.Equal(MemoryTrim.TrimAction.Compact, MemoryTrim.Decide(release: false, heapBytes: 102 * MB, lastFullHeapBytes: 70 * MB));
        Assert.Equal(MemoryTrim.TrimAction.CompactAndEmpty, MemoryTrim.Decide(release: true, heapBytes: 70 * MB, lastFullHeapBytes: 70 * MB));
    }

    [Fact]
    public void AWindowThatHidesIsTrimmedOnTheNextSampleAndLeavesAFloor()
    {
        var trim = new MemoryTrim();
        trim.MaybeRun(); // before the first start-up trim (30 s): nothing
        Assert.Equal(0, trim.Compactions);
        trim.Request();
        trim.MaybeRun();
        Assert.Equal(1, trim.Compactions);
        Assert.Equal(1, trim.WorkingSetEmptied);
        var floor = trim.Floors.Last();
        Assert.True(floor.HeapBytes > 0);
        Assert.True(floor.PrivateBytes > 0);
        trim.MaybeRun(); // the request is used up
        Assert.Equal(1, trim.Compactions);
    }
}
