using System.Buffers.Binary;
using PoolWatch.Core.Model;

namespace PoolWatch.Core.Parsing;

/// <summary>
/// Parses SYSTEM_HANDLE_INFORMATION_EX (64-bit): NumberOfHandles(ULONG_PTR)@0, Reserved@8, then 40-byte
/// SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX entries from offset 16: Object@0, UniqueProcessId@8, HandleValue@16,
/// GrantedAccess@24, CreatorBackTraceIndex@28, ObjectTypeIndex(USHORT)@30, HandleAttributes@32, Reserved@36.
/// Only counts are kept: (pid, type index) -> handles.
/// </summary>
public static class HandleInfoParser
{
    public const int HeaderSize = 16;
    public const int EntrySize = 40;

    public static HandleCounts Parse(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < HeaderSize) throw new FormatException("Handle buffer too small");
        ulong count = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
        ulong fits = (ulong)(buffer.Length - HeaderSize) / EntrySize;
        if (count > fits) count = fits; // the table can grow between calls; parse what is present
        var result = new HandleCounts();
        for (int i = 0; i < (int)count; i++)
        {
            var e = buffer.Slice(HeaderSize + i * EntrySize, EntrySize);
            int pid = (int)BinaryPrimitives.ReadUInt64LittleEndian(e[8..]);
            int type = BinaryPrimitives.ReadUInt16LittleEndian(e[30..]);
            result.Add(pid, type);
        }
        return result;
    }
}
