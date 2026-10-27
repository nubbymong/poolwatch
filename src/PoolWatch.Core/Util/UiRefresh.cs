using PoolWatch.Core.Model;

namespace PoolWatch.Core.Util;

/// <summary>What a periodic refresh does to a read-only text box (C5, review Part 2).</summary>
public enum TextRefreshAction
{
    /// <summary>Same item, same text: nothing to do.</summary>
    None,
    /// <summary>A different item (another tag, another owner): replace the text, scroll to the top, drop any selection.</summary>
    Replace,
    /// <summary>Same item, new text, nothing selected: update, keeping the first visible line and the caret.</summary>
    Update,
    /// <summary>Same item, new text, and the selected text is unchanged at the same place: update and keep the selection.</summary>
    UpdateKeepSelection,
    /// <summary>Same item, new text, but the update would move or destroy the user's selection: keep the old text for now.</summary>
    Hold,
}

/// <summary>
/// Decides how a refresh updates a text box the user may be reading or selecting in. The old rule (skip while anything is
/// selected) froze the answer and details on the previous tag after the selection moved; now a different item always
/// replaces the text, and a selection is only protected while the box still shows the same item.
/// </summary>
public static class TextRefresh
{
    /// <param name="current">The text the box shows now.</param>
    /// <param name="next">The text the refresh wants to show.</param>
    /// <param name="sameItem">The box already shows this item (tag / owner), so the new text is an update of it.</param>
    /// <param name="focused">The box has the keyboard focus: the user is selecting or copying in it right now. A selection
    /// left behind in a box the user has moved away from does not hold an update back (it is dropped instead).</param>
    public static TextRefreshAction Plan(string current, string next, bool sameItem, int selectionStart, int selectionLength, bool focused = true)
    {
        if (!sameItem) return current == next ? TextRefreshAction.None : TextRefreshAction.Replace;
        if (current == next) return TextRefreshAction.None;
        if (selectionLength <= 0) return TextRefreshAction.Update;
        bool stillThere = selectionStart >= 0 && selectionStart + selectionLength <= current.Length &&
                          selectionStart + selectionLength <= next.Length &&
                          string.CompareOrdinal(current, selectionStart, next, selectionStart, selectionLength) == 0;
        if (stillThere) return TextRefreshAction.UpdateKeepSelection;
        return focused ? TextRefreshAction.Hold : TextRefreshAction.Update;
    }
}

/// <summary>Which row to keep at the top of a list after a refresh (C5 item 3: the scroll position survives).</summary>
public static class ScrollAnchor
{
    /// <summary>
    /// The key of the row to scroll back to the top, or null to leave the list where it is. A list at the very top stays at
    /// the top (new rows inserted above are shown, as before). Otherwise the row that was at the top is kept there; if it
    /// has gone, the next row below it that is still present takes its place.
    /// </summary>
    /// <param name="oldTopIndex">Index of the top visible row before the refresh.</param>
    /// <param name="oldKeysFromTop">Keys of the rows from the old top row downwards, in order.</param>
    /// <param name="stillPresent">Whether a key is still in the list after the refresh.</param>
    public static object? Pick(int oldTopIndex, IReadOnlyList<object> oldKeysFromTop, Func<object, bool> stillPresent)
    {
        if (oldTopIndex <= 0) return null;
        foreach (var k in oldKeysFromTop)
            if (stillPresent(k)) return k;
        return null;
    }
}

/// <summary>Keys for list rows that are matched across refreshes (C5: two rows must never share a key).</summary>
public static class RowKeys
{
    /// <summary>An event row's key: everything that identifies the event, including its detail (two app events with the
    /// same time, PID 0 and image differ only there).</summary>
    public static string ForEvent(LifecycleEvent e) =>
        $"{e.Kind}:{e.Pid}:{e.CreateTime}:{e.TimeUtc.Ticks}:{e.Image}:{e.Detail}";

    /// <summary>
    /// The keys made unique in order: the first occurrence keeps its key, later identical ones get "#2", "#3" ... So two
    /// identical events are two rows (a shared key made one silently replace the other), and the numbering is stable while
    /// the input order is.
    /// </summary>
    public static List<string> Unique(IEnumerable<string> keys)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var k in keys)
        {
            string key = k;
            if (!used.Add(key))
            {
                int n = seen.GetValueOrDefault(k, 1);
                do { n++; key = $"{k}#{n}"; } while (!used.Add(key));
                seen[k] = n;
            }
            result.Add(key);
        }
        return result;
    }
}
