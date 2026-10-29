using System.Runtime.InteropServices;

namespace PoolWatch.Ui;

/// <summary>
/// The Pool causes list sorted by a column the owner clicked (pure; unit-tested): numbers by their value, not their text,
/// the confidence and the impact by their strength, When by the time; a row whose cell has no number goes last either way, and rows that
/// tie keep the list's own order (what is happening now first).
/// </summary>
internal static class PoolCausesSort
{
    /// <summary>
    /// The sort after a click on <paramref name="column"/>'s header: that column ascending, or, when it is already the sorted
    /// one, the other way round. -1 is the list's own order (no sort yet).
    /// </summary>
    public static (int Column, bool Descending) Click((int Column, bool Descending) now, int column) =>
        (column, column == now.Column && !now.Descending);

    /// <summary><paramref name="rows"/> (in the list's own order) by <paramref name="column"/>, ascending or descending.</summary>
    public static List<CauseRow> Sort(IReadOnlyList<CauseRow> rows, int column, bool descending)
    {
        var order = rows.Select((r, i) => (Row: r, Index: i, Key: Key(r, column))).ToList();
        order.Sort((a, b) =>
        {
            if (a.Key is null || b.Key is null) return a.Key is null == b.Key is null ? a.Index.CompareTo(b.Index) : a.Key is null ? 1 : -1;
            int c = a.Key is string x && b.Key is string y ? string.Compare(x, y, StringComparison.CurrentCultureIgnoreCase) : a.Key.CompareTo(b.Key);
            return c != 0 ? (descending ? -c : c) : a.Index.CompareTo(b.Index);
        });
        return order.Select(o => o.Row).ToList();
    }

    /// <summary>What a column sorts a row by; null when its cell says no number (or no cause).</summary>
    private static IComparable? Key(CauseRow r, int column) => column switch
    {
        0 => r.What,
        PoolCauses.GrewColumn => r.GrewBytes,
        PoolCauses.ClearedColumn => r.ClearedBytes,
        PoolCauses.NetColumn => r.NetBytes,
        PoolCauses.PerHourColumn => r.PerHourBytes,
        PoolCauses.CauseColumn => r.Cause.Length > 0 ? r.Cause : null,
        PoolCauses.ConfidenceColumn => r.Level is { } level ? (int)level : null,
        PoolCauses.ImpactColumn => r.Impact is { } impact ? (int)impact : null,
        PoolCauses.WhenColumn => r.TimeUtc,
        _ => null,
    };
}

/// <summary>
/// The sort arrow in the Pool causes list's header: the system's own (up for ascending, down for descending) on the
/// sorted column, none on the others. It is set again whenever the list's window is made again.
/// </summary>
internal static class PoolCausesHeader
{
    private const int LVM_GETHEADER = 0x1000 + 31, HDM_GETITEMW = 0x1200 + 11, HDM_SETITEMW = 0x1200 + 12;
    private const int HDI_FORMAT = 0x0004, HDF_SORTDOWN = 0x0200, HDF_SORTUP = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct HDITEM
    {
        public int mask, cxy;
        public IntPtr pszText, hbm;
        public int cchTextMax, fmt;
        public IntPtr lParam;
        public int iImage, iOrder;
        public uint type;
        public IntPtr pvFilter;
        public uint state;
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref HDITEM lParam);

    /// <summary>The arrow on <paramref name="column"/> (none anywhere for -1); the header's other formatting is kept.</summary>
    public static void Show(ListView list, int column, bool descending)
    {
        if (!list.IsHandleCreated) return;
        var header = SendMessage(list.Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
        if (header == IntPtr.Zero) return;
        for (int i = 0; i < list.Columns.Count; i++)
        {
            var item = new HDITEM { mask = HDI_FORMAT };
            if (SendMessage(header, HDM_GETITEMW, i, ref item) == IntPtr.Zero) continue;
            int fmt = item.fmt & ~(HDF_SORTUP | HDF_SORTDOWN);
            if (i == column) fmt |= descending ? HDF_SORTDOWN : HDF_SORTUP;
            if (fmt == item.fmt) continue;
            item.fmt = fmt;
            SendMessage(header, HDM_SETITEMW, i, ref item);
        }
    }

    /// <summary>The arrow the header shows on a column: false for up (ascending), true for down (descending), null for none (tests, CI).</summary>
    public static bool? Arrow(ListView list, int column)
    {
        if (!list.IsHandleCreated) return null;
        var header = SendMessage(list.Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
        var item = new HDITEM { mask = HDI_FORMAT };
        if (header == IntPtr.Zero || SendMessage(header, HDM_GETITEMW, column, ref item) == IntPtr.Zero) return null;
        return (item.fmt & HDF_SORTDOWN) != 0 ? true : (item.fmt & HDF_SORTUP) != 0 ? false : null;
    }
}
