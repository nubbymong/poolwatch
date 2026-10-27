using System.Buffers.Binary;
using System.Text;

namespace PoolWatch.Core;

/// <summary>
/// A kernel pool tag keyed by its raw four bytes (little-endian, as stored by the kernel).
/// Comparison is ordinal on the raw value, so <c>Reff</c> and <c>RefF</c> are different tags.
/// </summary>
public readonly record struct PoolTag(uint Raw) : IComparable<PoolTag>
{
    public static PoolTag FromBytes(ReadOnlySpan<byte> four) => new(BinaryPrimitives.ReadUInt32LittleEndian(four));

    /// <summary>Builds a tag from a 1-4 character string; shorter strings are padded with spaces (e.g. "Key").</summary>
    public static PoolTag Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length is 0 or > 4) throw new FormatException($"Pool tag must be 1-4 characters: '{text}'");
        Span<byte> b = stackalloc byte[4] { 0x20, 0x20, 0x20, 0x20 };
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c > 0xFF) throw new FormatException($"Pool tag must be single-byte characters: '{text}'");
            b[i] = (byte)c;
        }
        return FromBytes(b);
    }

    public static bool TryParse(string? text, out PoolTag tag)
    {
        tag = default;
        if (string.IsNullOrEmpty(text) || text.Length > 4) return false;
        foreach (char c in text) if (c > 0xFF) return false;
        tag = Parse(text);
        return true;
    }

    public byte[] GetBytes()
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, Raw);
        return b;
    }

    /// <summary>Display form: printable ASCII as-is, other bytes as '.'; trailing spaces preserved as in pooltag.txt.</summary>
    public override string ToString()
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, Raw);
        var sb = new StringBuilder(4);
        foreach (var x in b) sb.Append(x is >= 0x20 and < 0x7F ? (char)x : '.');
        return sb.ToString();
    }

    /// <summary>Display form with trailing spaces trimmed (for file names and headlines).</summary>
    public string ToDisplay() => ToString().TrimEnd();

    /// <summary>File-name-safe form; case is encoded so that Reff and RefF never collide on case-insensitive file systems.</summary>
    public string ToFileSafe()
    {
        var sb = new StringBuilder();
        foreach (var c in ToString())
        {
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(c);
            else sb.Append('_');
        }
        return sb.ToString().TrimEnd('_') + "-" + Raw.ToString("x8");
    }

    public int CompareTo(PoolTag other) => Raw.CompareTo(other.Raw);
}
