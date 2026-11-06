namespace PoolWatch.Ui;

/// <summary>
/// The Pool causes list sorted by a column the owner clicked (pure; unit-tested): numbers by their value, not their text,
/// the confidence and the impact by their strength, When by the time; a row whose cell has no number goes last either way, and rows that
/// tie keep the list's own order (what is happening now first).
/// </summary>
internal static class PoolCausesSort
{
    /// <summary>
    /// The sort after a click on <paramref name="column"/>'s header, by the lists' one rule (<see cref="ListSort.Next"/>):
    /// that column ascending, or, when it is already the sorted one, the other way round. -1 is the list's own order (no
    /// sort yet).
    /// </summary>
    public static (int Column, bool Descending) Click((int Column, bool Descending) now, int column)
    {
        var (sorted, ascending) = ListSort.Next((now.Column, !now.Descending), column);
        return (sorted, !ascending);
    }

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

