using PoolWatch.Core;
using PoolWatch.Core.Parsing;

namespace PoolWatch.Tests;

public class ParserTests
{
    [Fact]
    public void PoolTags_ParseAllFieldsAtSpecOffsets()
    {
        var buf = FixtureBuilder.PoolTags(
            ("Reff", 10, 3, 4096, 0, 0, 0),
            ("RefF", 1, 0, 512, 2, 1, 128),
            ("Key", 5, 5, 0, 0, 0, 64));
        var tags = PoolTagInfoParser.Parse(buf);
        Assert.Equal(3, tags.Count);
        Assert.Equal(PoolTag.Parse("Reff"), tags[0].Tag);
        Assert.Equal(10u, tags[0].PagedAllocs);
        Assert.Equal(3u, tags[0].PagedFrees);
        Assert.Equal(4096ul, tags[0].PagedUsed);
        Assert.Equal(2u, tags[1].NonPagedAllocs);
        Assert.Equal(1u, tags[1].NonPagedFrees);
        Assert.Equal(128ul, tags[1].NonPagedUsed);
        Assert.Equal(640ul, tags[1].TotalUsed);
        Assert.Equal("Key ", tags[2].Tag.ToString());
        Assert.NotEqual(tags[0].Tag, tags[1].Tag); // case-sensitive
    }

    [Fact]
    public void PoolTags_TruncatedBufferThrows()
    {
        var buf = FixtureBuilder.PoolTags(("File", 1, 0, 1, 0, 0, 0), ("Toke", 1, 0, 1, 0, 0, 0));
        Assert.Throws<FormatException>(() => PoolTagInfoParser.Parse(buf.AsSpan(0, buf.Length - 1)));
    }

    [Fact]
    public void PoolTags_HugeSizesUse64Bits()
    {
        var buf = FixtureBuilder.PoolTags(("MmSt", 1, 0, 23UL * 1024 * 1024 * 1024, 0, 0, 0));
        Assert.Equal(23UL * 1024 * 1024 * 1024, PoolTagInfoParser.Parse(buf)[0].PagedUsed);
    }

    [Fact]
    public void Processes_ParseChainAndTranslateNamePointers()
    {
        const ulong baseAddr = 0x7FF0_0000_0000;
        var buf = FixtureBuilder.Processes(baseAddr,
            new(0, 0, 0, "", 0, 0, 0, 0, 0),
            new(4, 0, 100, "", 2000, 0, 0, 0, 0),
            new(4242, 1234, 133_000_000_000_000_000, "find.exe", 900_000, 5_000_000, 70_000, 123_456, 9_999_999, Threads: 1, PageFaults: 887_000, KernelTime: 17_000_000, UserTime: 3_000_000));
        var procs = ProcessInfoParser.Parse(buf, baseAddr);
        Assert.Equal(3, procs.Count);
        Assert.Equal("Idle", procs[0].ImageName);
        Assert.Equal("System", procs[1].ImageName);
        var f = procs[2];
        Assert.Equal("find.exe", f.ImageName);
        Assert.Equal(4242, f.Pid);
        Assert.Equal(1234, f.ParentPid);
        Assert.Equal(133_000_000_000_000_000, f.CreateTime);
        Assert.Equal(900_000, f.HandleCount);
        Assert.Equal(1, f.ThreadCount);
        Assert.Equal(5_000_000ul, f.QuotaPagedPoolUsage);
        Assert.Equal(70_000ul, f.QuotaNonPagedPoolUsage);
        Assert.Equal(123_456, f.ReadOperationCount);
        Assert.Equal(7, f.WriteOperationCount);
        Assert.Equal(9_999_999, f.OtherOperationCount);
        Assert.Equal(12345678ul, f.PrivateBytes);
        Assert.Equal(887_000u, f.PageFaultCount);
        Assert.Equal(17_000_000, f.KernelTime);
        Assert.Equal(3_000_000, f.UserTime);
    }

    [Fact]
    public void Processes_NamePointerOutsideBufferIsIgnored()
    {
        var buf = FixtureBuilder.Processes(0x1000, new FixtureBuilder.Proc(88, 4, 1, "x.exe", 1, 0, 0, 0, 0));
        var procs = ProcessInfoParser.Parse(buf, 0x9999_0000);
        Assert.Equal("", procs[0].ImageName);
    }

    [Fact]
    public void Handles_CountPerProcessPerType()
    {
        var buf = FixtureBuilder.Handles((100, 37), (100, 37), (100, 12), (200, 37));
        var c = HandleInfoParser.Parse(buf);
        Assert.Equal(4, c.TotalHandles);
        Assert.Equal(2, c.Count(100, 37));
        Assert.Equal(1, c.Count(100, 12));
        Assert.Equal(1, c.Count(200, 37));
        Assert.Equal(0, c.Count(300, 37));
    }

    [Fact]
    public void Handles_CountLargerThanBufferIsClamped()
    {
        var buf = FixtureBuilder.Handles((1, 2), (1, 2));
        BitConverter.TryWriteBytes(buf.AsSpan(0, 8), 1_000_000UL);
        Assert.Equal(2, HandleInfoParser.Parse(buf).TotalHandles);
    }

    [Fact]
    public void ObjectTypes_ParseNamesIndexesAndAlignment()
    {
        const ulong baseAddr = 0x2000_0000;
        var buf = FixtureBuilder.ObjectTypes(baseAddr, ("Type", 2, 70, 0), ("File", 37, 5000, 6000), ("Key", 0, 9, 9));
        var types = ObjectTypesParser.Parse(buf, baseAddr);
        Assert.Equal(["Type", "File", "Key"], types.Select(t => t.Name));
        Assert.Equal(37, types[1].TypeIndex);
        Assert.Equal(5000u, types[1].TotalObjects);
        Assert.Equal(6000u, types[1].TotalHandles);
        Assert.Equal(4, types[2].TypeIndex); // index 0 falls back to position + 2
    }
}
