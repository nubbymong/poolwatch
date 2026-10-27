using System.Buffers.Binary;
using System.Text;

namespace PoolWatch.Core.Util;

/// <summary>
/// Reads a shortcut (.lnk) file's target path and arguments from its bytes ([MS-SHLLINK]): the header's flags, the
/// LinkInfo local base path + common path suffix, and the COMMAND_LINE_ARGUMENTS string. Part 3 review: the Startup-folder
/// shortcuts are files any user can write, and PoolWatch runs elevated, so they are not handed to the shell (WScript.Shell
/// COM) but read by this small, bounded, pure parser. Anything malformed gives null; nothing is ever executed or resolved.
/// A shortcut whose target is only in its shell item ID list (no LinkInfo) has no readable target here.
/// </summary>
public static class ShellLink
{
    private const int HeaderSize = 0x4C;
    private const uint HasLinkTargetIdList = 0x1, HasLinkInfo = 0x2, HasName = 0x4, HasRelativePath = 0x8, HasWorkingDir = 0x10,
        HasArguments = 0x20, IsUnicode = 0x80;
    private static readonly Guid LinkClsid = new("00021401-0000-0000-C000-000000000046");
    /// <summary>Larger files are not read (a real shortcut is a few KB).</summary>
    public const int MaxBytes = 64 * 1024;

    /// <summary>The target and arguments, or null when the bytes are not a readable shortcut.</summary>
    public static (string Target, string? Arguments)? Read(ReadOnlySpan<byte> b)
    {
        try
        {
            if (b.Length < HeaderSize || b.Length > MaxBytes || BinaryPrimitives.ReadUInt32LittleEndian(b) != HeaderSize) return null;
            if (new Guid(b.Slice(4, 16)) != LinkClsid) return null;
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(b[0x14..]);
            int pos = HeaderSize;
            if ((flags & HasLinkTargetIdList) != 0)
            {
                if (pos + 2 > b.Length) return null;
                pos += 2 + BinaryPrimitives.ReadUInt16LittleEndian(b[pos..]);
            }
            string? target = null;
            if ((flags & HasLinkInfo) != 0)
            {
                if (pos + 4 > b.Length) return null;
                int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(b[pos..]);
                if (size < 0x1C || pos + size > b.Length) return null;
                target = LinkInfoPath(b.Slice(pos, size));
                pos += size;
            }
            bool unicode = (flags & IsUnicode) != 0;
            string? args = null;
            foreach (uint f in new[] { HasName, HasRelativePath, HasWorkingDir, HasArguments })
            {
                if ((flags & f) == 0) continue;
                if (pos + 2 > b.Length) return null;
                int chars = BinaryPrimitives.ReadUInt16LittleEndian(b[pos..]);
                int bytes = unicode ? chars * 2 : chars;
                pos += 2;
                if (pos + bytes > b.Length) return null;
                if (f == HasArguments) args = unicode ? Encoding.Unicode.GetString(b.Slice(pos, bytes)) : Encoding.Latin1.GetString(b.Slice(pos, bytes));
                pos += bytes;
            }
            return string.IsNullOrEmpty(target) ? null : (target, string.IsNullOrEmpty(args) ? null : args);
        }
        catch (ArgumentException) { return null; } // a bad offset inside the structure
    }

    /// <summary>LocalBasePath + CommonPathSuffix (the Unicode ones when present).</summary>
    private static string? LinkInfoPath(ReadOnlySpan<byte> li)
    {
        int headerSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(li[4..]);
        uint liFlags = BinaryPrimitives.ReadUInt32LittleEndian(li[8..]);
        if ((liFlags & 0x1) == 0) return null; // no VolumeID and LocalBasePath: a network target
        int baseOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(li[0x10..]);
        int suffixOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(li[0x18..]);
        if (headerSize >= 0x24 && li.Length >= 0x24)
        {
            int baseU = (int)BinaryPrimitives.ReadUInt32LittleEndian(li[0x1C..]);
            int suffixU = (int)BinaryPrimitives.ReadUInt32LittleEndian(li[0x20..]);
            if (baseU > 0) return Utf16Z(li, baseU) + (suffixU > 0 ? Utf16Z(li, suffixU) : "");
        }
        return AnsiZ(li, baseOff) + (suffixOff > 0 ? AnsiZ(li, suffixOff) : "");
    }

    private static string AnsiZ(ReadOnlySpan<byte> s, int off)
    {
        if (off <= 0 || off >= s.Length) return "";
        int end = s[off..].IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? s[off..] : s.Slice(off, end));
    }

    private static string Utf16Z(ReadOnlySpan<byte> s, int off)
    {
        if (off <= 0 || off >= s.Length) return "";
        int i = off;
        while (i + 1 < s.Length && (s[i] != 0 || s[i + 1] != 0)) i += 2;
        return Encoding.Unicode.GetString(s[off..Math.Min(i, s.Length)]);
    }
}
