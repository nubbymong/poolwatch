namespace PoolWatch.Core.Services;

/// <summary>
/// The Explorer "StartupApproved" state (addendum 4 C): Task Manager enables/disables a startup entry by writing a
/// 12-byte value under ...\Explorer\StartupApproved\*; byte 0 is 0x02 (enabled) or 0x03 (disabled), the rest a
/// timestamp. PoolWatch uses the same bytes, so the owner can flip it back in Task Manager. Pure byte logic here; the
/// registry read/write is the caller's.
/// </summary>
public static class StartupApproved
{
    /// <summary>A startup entry is enabled unless byte 0 has the low disable bit set.</summary>
    public static bool IsEnabled(byte[]? value) => value is null || value.Length == 0 || (value[0] & 0x01) == 0;

    /// <summary>The 12-byte value Task Manager writes for enabled / disabled, with the change time in the trailing FILETIME.</summary>
    public static byte[] Make(bool enabled, DateTime whenUtc)
    {
        var v = new byte[12];
        v[0] = (byte)(enabled ? 0x02 : 0x03);
        long ft = whenUtc.ToFileTimeUtc();
        BitConverter.GetBytes(ft).CopyTo(v, 4);
        return v;
    }

    /// <summary>Flips an existing value to enabled/disabled, keeping its length, so Task Manager still reads it.</summary>
    public static byte[] Set(byte[]? existing, bool enabled, DateTime whenUtc)
    {
        if (existing is null || existing.Length < 12) return Make(enabled, whenUtc);
        var v = (byte[])existing.Clone();
        v[0] = (byte)(enabled ? (v[0] & ~0x01) : (v[0] | 0x01));
        long ft = whenUtc.ToFileTimeUtc();
        BitConverter.GetBytes(ft).CopyTo(v, 4);
        return v;
    }
}
