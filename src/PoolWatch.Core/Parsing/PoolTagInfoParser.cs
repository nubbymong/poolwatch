using System.Buffers.Binary;
using PoolWatch.Core.Model;

namespace PoolWatch.Core.Parsing;

/// <summary>
/// Parses SYSTEM_POOLTAG_INFORMATION (64-bit): ULONG Count, then 40-byte SYSTEM_POOLTAG entries from offset 8:
/// Tag[4]@0, PagedAllocs@4, PagedFrees@8, PagedUsed(SIZE_T)@16, NonPagedAllocs@24, NonPagedFrees@28, NonPagedUsed(SIZE_T)@32.
/// </summary>
public static class PoolTagInfoParser
{
    public const int HeaderSize = 8;
    public const int EntrySize = 40;

    public static List<PoolTagEntry> Parse(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < HeaderSize) throw new FormatException("Pool tag buffer too small");
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        long needed = HeaderSize + (long)count * EntrySize;
        if (needed > buffer.Length) throw new FormatException($"Pool tag buffer truncated: count {count} needs {needed} bytes, have {buffer.Length}");
        var result = new List<PoolTagEntry>((int)count);
        for (int i = 0; i < count; i++)
        {
            var e = buffer.Slice(HeaderSize + i * EntrySize, EntrySize);
            result.Add(new PoolTagEntry(
                PoolTag.FromBytes(e[..4]),
                BinaryPrimitives.ReadUInt32LittleEndian(e[4..]),
                BinaryPrimitives.ReadUInt32LittleEndian(e[8..]),
                BinaryPrimitives.ReadUInt64LittleEndian(e[16..]),
                BinaryPrimitives.ReadUInt32LittleEndian(e[24..]),
                BinaryPrimitives.ReadUInt32LittleEndian(e[28..]),
                BinaryPrimitives.ReadUInt64LittleEndian(e[32..])));
        }
        return result;
    }
}
