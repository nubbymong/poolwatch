using System.Buffers.Binary;
using System.Text;
using PoolWatch.Core.Model;

namespace PoolWatch.Core.Parsing;

/// <summary>
/// Parses OBJECT_TYPES_INFORMATION (64-bit) from NtQueryObject(NULL, ObjectTypesInformation):
/// ULONG NumberOfTypes, then entries aligned to 8 starting at offset 8. Each OBJECT_TYPE_INFORMATION is
/// 0x68 bytes (TypeName UNICODE_STRING@0, TotalNumberOfObjects@0x10, TotalNumberOfHandles@0x14,
/// TypeIndex(UCHAR)@0x5A) followed by the name buffer; the next entry starts at
/// ALIGN_UP(name buffer + MaximumLength, 8).
/// </summary>
public static class ObjectTypesParser
{
    internal const int EntrySize = 0x68;
    internal const int TypeIndexOffset = 0x5A;

    public static List<ObjectTypeInfo> Parse(ReadOnlySpan<byte> buffer, ulong baseAddress)
    {
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        var list = new List<ObjectTypeInfo>((int)count);
        int offset = 8;
        for (int i = 0; i < count; i++)
        {
            if (offset + EntrySize > buffer.Length) throw new FormatException($"Object type entry {i} exceeds buffer");
            var e = buffer[offset..];
            ushort len = BinaryPrimitives.ReadUInt16LittleEndian(e);
            ushort max = BinaryPrimitives.ReadUInt16LittleEndian(e[2..]);
            ulong ptr = BinaryPrimitives.ReadUInt64LittleEndian(e[8..]);
            int nameOffset = offset + EntrySize;
            if (ptr >= baseAddress && ptr - baseAddress < (ulong)buffer.Length) nameOffset = (int)(ptr - baseAddress);
            if (nameOffset + len > buffer.Length) throw new FormatException($"Object type name {i} exceeds buffer");
            string name = Encoding.Unicode.GetString(buffer.Slice(nameOffset, len));
            int typeIndex = e[TypeIndexOffset];
            if (typeIndex == 0) typeIndex = i + 2; // pre-8.1 kernels: index is position + 2
            list.Add(new ObjectTypeInfo(typeIndex, name,
                BinaryPrimitives.ReadUInt32LittleEndian(e[0x10..]),
                BinaryPrimitives.ReadUInt32LittleEndian(e[0x14..])));
            offset = Align8(nameOffset + max);
        }
        return list;
    }

    private static int Align8(int v) => (v + 7) & ~7;
}
