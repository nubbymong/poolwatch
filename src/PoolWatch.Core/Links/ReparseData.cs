using System.Buffers.Binary;
using System.Text;

namespace PoolWatch.Core.Links;

/// <summary>
/// The target a junction or symbolic link holds, read from its reparse data (REPARSE_DATA_BUFFER in ntifs.h) as the
/// AppData link finder reads it through the handle it checked. The data is on a disk an account can write, so it is read
/// as untrusted: bounded, every offset and length checked against the buffer, never throwing. Pure, so the Linux tests
/// give it any bytes.
/// </summary>
public static class ReparseData
{
    /// <summary>IO_REPARSE_TAG_MOUNT_POINT (a junction) and IO_REPARSE_TAG_SYMLINK (winnt.h).</summary>
    public const uint MountPoint = 0xA0000003, SymbolicLink = 0xA000000C;

    /// <summary>MAXIMUM_REPARSE_DATA_BUFFER_SIZE: the most reparse data a file can hold.</summary>
    public const int MaxBytes = 16 * 1024;

    /// <summary>SYMLINK_FLAG_RELATIVE: the symbolic link's target is relative to the folder the link is in.</summary>
    private const uint RelativeFlag = 1;

    /// <summary>
    /// The link's substitute name (its target, as Windows follows it: "\??\C:\..." for a full path) and whether it is
    /// relative to the link's own folder; null for another kind of reparse point, or data that does not hold a whole name.
    /// The layout: a ULONG tag, a USHORT data length and a USHORT reserved, then for a junction four USHORTs (substitute
    /// name offset and length, print name offset and length, in bytes from the path buffer) and the path buffer, and for
    /// a symbolic link the same four with a ULONG of flags before the path buffer.
    /// </summary>
    public static (string Target, bool Relative)? Target(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8 || data.Length > MaxBytes) return null;
        uint tag = BinaryPrimitives.ReadUInt32LittleEndian(data);
        int length = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        if (8 + length > data.Length) return null;
        var body = data.Slice(8, length);
        int header = tag switch { SymbolicLink => 12, MountPoint => 8, _ => -1 };
        if (header < 0 || body.Length < header) return null;
        int offset = BinaryPrimitives.ReadUInt16LittleEndian(body), size = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
        bool relative = tag == SymbolicLink && (BinaryPrimitives.ReadUInt32LittleEndian(body[8..]) & RelativeFlag) != 0;
        var names = body[header..];
        if (size == 0 || size % 2 != 0 || offset % 2 != 0 || offset + size > names.Length) return null;
        string target = Encoding.Unicode.GetString(names.Slice(offset, size));
        return target.Any(char.IsControl) ? null : (target, relative);
    }

    /// <summary>Reparse data for a link to <paramref name="target"/>, as Windows writes it (the tests make links of their own with it).</summary>
    public static byte[] Make(uint tag, string target, string print, bool relative = false)
    {
        byte[] sub = Encoding.Unicode.GetBytes(target), shown = Encoding.Unicode.GetBytes(print);
        int header = tag == SymbolicLink ? 12 : 8;
        // The path buffer holds the two names, each followed by a terminating null, as Windows writes them.
        int names = sub.Length + 2 + shown.Length + 2;
        var data = new byte[8 + header + names];
        BinaryPrimitives.WriteUInt32LittleEndian(data, tag);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), (ushort)(header + names));
        var body = data.AsSpan(8);
        BinaryPrimitives.WriteUInt16LittleEndian(body, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(body[2..], (ushort)sub.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(body[4..], (ushort)(sub.Length + 2));
        BinaryPrimitives.WriteUInt16LittleEndian(body[6..], (ushort)shown.Length);
        if (tag == SymbolicLink) BinaryPrimitives.WriteUInt32LittleEndian(body[8..], relative ? RelativeFlag : 0);
        sub.CopyTo(body[header..]);
        shown.CopyTo(body[(header + sub.Length + 2)..]);
        return data;
    }
}
