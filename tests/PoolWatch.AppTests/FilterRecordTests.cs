using System.Buffers.Binary;
using System.Text;
using PoolWatch.Collectors;

namespace PoolWatch.AppTests;

/// <summary>
/// The fltlib record readers: a name is read only when its length (and, for instance fields, its offset) stays inside
/// the record the API filled; otherwise the record is skipped instead of reading past the buffer.
/// </summary>
public sealed class FilterRecordTests
{
    private static byte[] FilterRecord(string name, int? claimedLength = null, int size = 64)
    {
        var record = new byte[size];
        var bytes = Encoding.Unicode.GetBytes(name);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(12), (ushort)(claimedLength ?? bytes.Length));
        bytes.CopyTo(record.AsSpan(14));
        return record;
    }

    [Fact]
    public void AFilterNameInsideTheRecordIsRead() =>
        Assert.Equal("WdFilter", FilterInstanceQuery.FilterName(FilterRecord("WdFilter")));

    [Theory]
    [InlineData(64 - 14 + 2)]  // two bytes past the end
    [InlineData(0xFFFE)]       // a corrupt length
    public void AFilterNameLengthThatRunsPastTheRecordIsRejected(int claimedLength) =>
        Assert.Null(FilterInstanceQuery.FilterName(FilterRecord("WdFilter", claimedLength)));

    [Fact]
    public void ARecordTooShortForTheLengthIsRejected() =>
        Assert.Null(FilterInstanceQuery.FilterName(new byte[13]));

    [Fact]
    public void AnInstanceFieldIsReadOnlyInsideTheRecord()
    {
        var record = new byte[128];
        var volume = Encoding.Unicode.GetBytes(@"\Device\HarddiskVolume3");
        volume.CopyTo(record.AsSpan(40));
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(12), (ushort)volume.Length); // volume name length
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(14), 40);                    // and offset
        Assert.Equal(@"\Device\HarddiskVolume3", FilterInstanceQuery.Field(record, 12));

        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(14), 120);                   // now it runs past the end
        Assert.Null(FilterInstanceQuery.Field(record, 12));
        Assert.Null(FilterInstanceQuery.Field(record, 126));                                // the pair itself is cut off
    }
}
