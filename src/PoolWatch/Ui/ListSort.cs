using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace PoolWatch.Ui;

/// <summary>
/// Sortable list headers: a click on a column header sorts by it, a second click reverses, and the header shows the
/// direction with Windows' own sort arrow (HDF_SORTUP / HDF_SORTDOWN on the header control, so a screen reader can read
/// it too). <see cref="Sorted"/> orders rows by their cells (the Cost column by what it costs, not alphabetically).
/// </summary>
internal sealed class ColumnSort
{
    private readonly ListView _list;
    private readonly Action _sorted;

    /// <param name="sorted">Called after a click changed the order: the owner shows the rows again.</param>
    public ColumnSort(ListView list, int column, bool ascending, Action sorted)
    {
        _list = list;
        _sorted = sorted;
        Column = column;
        Ascending = ascending;
        list.ColumnClick += (_, e) => Click(e.Column);
        // A new window gets its columns only after this event: the arrow goes on once they are there.
        list.HandleCreated += (_, _) => list.BeginInvoke(ShowArrow);
    }

    public int Column { get; private set; }
    public bool Ascending { get; private set; }

    /// <summary>A header click: the same column reverses, another sorts by it ascending (<see cref="ListSort.Next"/>).</summary>
    public void Click(int column)
    {
        (Column, Ascending) = ListSort.Next((Column, Ascending), column);
        _sorted();
        ShowArrow();
    }

    /// <summary>The header's arrow as the sort is now (after the rows are shown again).</summary>
    public void ShowArrow() => ListSort.SetArrow(_list, Column, Ascending);
}

internal static partial class ListSort
{
    /// <summary>
    /// The one click rule of every sortable list (Services, the tasks list, Pool causes): a click on the column already
    /// sorted by turns the order round, a click on another column sorts by it ascending.
    /// </summary>
    public static (int Column, bool Ascending) Next((int Column, bool Ascending) now, int clicked) =>
        clicked == now.Column ? (clicked, !now.Ascending) : (clicked, true);

    /// <summary>
    /// The rows ordered by <paramref name="column"/> of <paramref name="cells"/> (ignoring case), the cost column by
    /// <see cref="CostKey"/>; ties by <paramref name="tieColumn"/>, then as they came. Not ascending reverses both.
    /// </summary>
    public static List<T> Sorted<T>(IEnumerable<T> rows, Func<T, IReadOnlyList<string>> cells, int column, bool ascending, int costColumn = -1, int tieColumn = 1)
    {
        string Cell(T r, int c) => cells(r) is var x && c >= 0 && c < x.Count ? x[c] : "";
        var indexed = rows.Select((r, i) => (r, i)).ToList();
        indexed.Sort((a, b) =>
        {
            int c = column == costColumn ? CostKey(Cell(a.r, column)).CompareTo(CostKey(Cell(b.r, column)))
                : StringComparer.OrdinalIgnoreCase.Compare(Cell(a.r, column), Cell(b.r, column));
            if (c == 0 && column != tieColumn) c = StringComparer.OrdinalIgnoreCase.Compare(Cell(a.r, tieColumn), Cell(b.r, tieColumn));
            if (!ascending) c = -c;
            return c != 0 ? c : a.i.CompareTo(b.i);
        });
        return indexed.Select(x => x.r).ToList();
    }

    /// <summary>
    /// What a Cost cell sorts by: its cores ("0.7 core, 3k page faults/s (...)" is 0.7, and so is "Medium: 0.7 core"),
    /// a little for page faults alone, 0 when idle, -1 when nothing runs or it cannot be measured.
    /// </summary>
    public static double CostKey(string? text)
    {
        string t = text?.Trim() ?? "";
        if (LevelWord().Match(t) is { Success: true } lead) t = t[lead.Length..];
        if (t.StartsWith("idle", StringComparison.OrdinalIgnoreCase)) return 0;
        if (Cores().Match(t) is { Success: true } m) return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        if (Faults().IsMatch(t)) return 0.01;
        return -1;
    }

    [GeneratedRegex(@"^(\d+(?:\.\d+)?) cores?\b")]
    private static partial Regex Cores();

    [GeneratedRegex(@"^\d+(?:\.\d+)?[kM]? page faults/s")]
    private static partial Regex Faults();

    /// <summary>The level word a cost cell starts with ("Low: "), the meter's word.</summary>
    [GeneratedRegex(@"^(?:None|Low|Medium|High): ")]
    private static partial Regex LevelWord();

    // ---------------------------------------------------------------- the header's sort arrow

    private const int LVM_GETHEADER = 0x1000 + 31, HDM_GETITEMCOUNT = 0x1200, HDM_GETITEMW = 0x1200 + 11, HDM_SETITEMW = 0x1200 + 12;
    private const uint HDI_FORMAT = 0x0004;
    private const int HDF_SORTDOWN = 0x0200, HDF_SORTUP = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct HDITEMW
    {
        public uint mask;
        public int cxy;
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
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref HDITEMW item);

    /// <summary>Shows the sort arrow on <paramref name="column"/> (up when ascending) and on no other column. Needs the list's handle.</summary>
    public static void SetArrow(ListView list, int column, bool ascending)
    {
        if (!list.IsHandleCreated) return;
        IntPtr header = SendMessage(list.Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
        if (header == IntPtr.Zero) return;
        int count = (int)SendMessage(header, HDM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero);
        for (int i = 0; i < count; i++)
        {
            var item = new HDITEMW { mask = HDI_FORMAT };
            if (SendMessage(header, HDM_GETITEMW, (IntPtr)i, ref item) == IntPtr.Zero) continue;
            int fmt = item.fmt & ~(HDF_SORTUP | HDF_SORTDOWN);
            if (i == column) fmt |= ascending ? HDF_SORTUP : HDF_SORTDOWN;
            if (fmt == item.fmt) continue;
            item.fmt = fmt;
            SendMessage(header, HDM_SETITEMW, (IntPtr)i, ref item);
        }
    }

    /// <summary>The arrow a column's header shows: 1 up, -1 down, 0 none (tests).</summary>
    public static int ArrowOf(ListView list, int column)
    {
        IntPtr header = SendMessage(list.Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
        var item = new HDITEMW { mask = HDI_FORMAT };
        if (header == IntPtr.Zero || SendMessage(header, HDM_GETITEMW, (IntPtr)column, ref item) == IntPtr.Zero) return 0;
        return (item.fmt & HDF_SORTUP) != 0 ? 1 : (item.fmt & HDF_SORTDOWN) != 0 ? -1 : 0;
    }
}
