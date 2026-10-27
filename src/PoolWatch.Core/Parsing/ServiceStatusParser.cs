using System.Buffers.Binary;
using System.Text;

namespace PoolWatch.Core.Parsing;

/// <summary>
/// Parses the ENUM_SERVICE_STATUS_PROCESSW array (x64) that EnumServicesStatusExW(SC_ENUM_PROCESS_INFO) writes. The
/// service and display names are absolute addresses inside the caller's buffer, so the caller passes the address the
/// buffer had when the API filled it, and every name is read through the span at its offset. A name whose address lies
/// outside the buffer, or that has no terminating NUL inside it, is rejected rather than read. The caller's buffer must
/// be unmanaged (or pinned for the whole parse): the addresses the API wrote are only meaningful while the buffer stays
/// where it was.
/// </summary>
public static class ServiceStatusParser
{
    // x64 layout: LPWSTR lpServiceName, LPWSTR lpDisplayName, SERVICE_STATUS_PROCESS (9 DWORDs), padded to 8 bytes.
    internal const int ServiceNamePointer = 0x00;
    internal const int DisplayNamePointer = 0x08;
    internal const int CurrentState = 0x10 + 0x04;
    internal const int ProcessId = 0x10 + 0x1C;
    internal const int EntrySize = 0x38;

    public readonly record struct Entry(string Name, string DisplayName, int ProcessId, int CurrentState);

    /// <summary>
    /// The <paramref name="count"/> entries at the start of <paramref name="buffer"/>. An entry whose service name cannot
    /// be read is left out; a display name that cannot be read falls back to the service name.
    /// </summary>
    /// <exception cref="FormatException">The entries themselves do not fit in the buffer.</exception>
    public static List<Entry> Parse(ReadOnlySpan<byte> buffer, ulong baseAddress, int count)
    {
        if (count < 0 || (long)count * EntrySize > buffer.Length)
            throw new FormatException($"{count} service entries do not fit in a {buffer.Length}-byte buffer");
        var list = new List<Entry>(count);
        for (int i = 0; i < count; i++)
        {
            var e = buffer.Slice(i * EntrySize, EntrySize);
            string? name = ReadString(buffer, baseAddress, BinaryPrimitives.ReadUInt64LittleEndian(e[ServiceNamePointer..]));
            if (string.IsNullOrEmpty(name)) continue;
            string display = ReadString(buffer, baseAddress, BinaryPrimitives.ReadUInt64LittleEndian(e[DisplayNamePointer..])) ?? name;
            list.Add(new Entry(name, display, BinaryPrimitives.ReadInt32LittleEndian(e[ProcessId..]), BinaryPrimitives.ReadInt32LittleEndian(e[CurrentState..])));
        }
        return list;
    }

    /// <summary>The NUL-terminated UTF-16 string at <paramref name="address"/>, or null when it is not wholly inside the buffer.</summary>
    internal static string? ReadString(ReadOnlySpan<byte> buffer, ulong baseAddress, ulong address)
    {
        if (address == 0 || address < baseAddress || address - baseAddress >= (ulong)buffer.Length) return null;
        var rest = buffer[(int)(address - baseAddress)..];
        for (int i = 0; i + 1 < rest.Length; i += 2)
            if (rest[i] == 0 && rest[i + 1] == 0) return Encoding.Unicode.GetString(rest[..i]);
        return null; // no terminator before the end of the buffer
    }
}
