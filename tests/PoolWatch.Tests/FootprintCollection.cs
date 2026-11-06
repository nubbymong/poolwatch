namespace PoolWatch.Tests;

/// <summary>
/// Tests that measure the whole managed heap (GC.GetTotalMemory) run on their own, never beside another test, whose
/// allocations would be counted with theirs.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FootprintCollection
{
    public const string Name = "Footprint";
}
