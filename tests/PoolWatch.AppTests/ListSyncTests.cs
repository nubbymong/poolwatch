using PoolWatch.Ui;

namespace PoolWatch.AppTests;

/// <summary>The in-place list update behind Pool causes, when the main window hides and shows again. No window is shown.</summary>
public class ListSyncTests
{
    [Fact]
    public void AClearedListGetsItsRowsBackAtTheNextUpdate()
    {
        using var list = new ListView { View = View.Details };
        list.Columns.Add("What");
        var sync = new ListSync(list);
        var rows = new List<ListSync.Row> { new("inc:a", ["Open files"]), new("tag:b", ["File system metadata"]) };
        sync.Update(rows);
        Assert.Equal(2, list.Items.Count);

        sync.Clear(); // the window hid and dropped its rows
        Assert.Empty(list.Items);
        Assert.Null(sync.ItemFor("inc:a"));

        sync.Update(rows); // shown again
        Assert.Equal(["Open files", "File system metadata"], list.Items.Cast<ListViewItem>().Select(i => i.Text));
        Assert.Same(list.Items[0], sync.ItemFor("inc:a"));
    }

    [Fact]
    public void AColumnSortAppliesAtOnceWithARowSelectedAndTheRowStaysSelected()
    {
        using var host = new Form();
        var list = new ListView { View = View.Details, FullRowSelect = true };
        list.Columns.Add("What");
        host.Controls.Add(list);
        _ = list.Handle; // the native list, as on screen
        var sync = new ListSync(list);
        ListSync.Row R(string key) => new(key, [key]);
        string Order() => string.Concat(list.Items.Cast<ListViewItem>().Select(i => i.Text));
        sync.Update([R("a"), R("b"), R("c")]);
        list.Items[1].Selected = true; // b
        var b = list.Items[1];
        // A refresh in another order waits while a row is selected (so the row does not jump under the pointer)...
        sync.Update([R("c"), R("b"), R("a")]);
        Assert.Equal(("abc", true), (Order(), sync.OrderHeld));
        // ...but the owner's sort is applied at once, the same rows kept, and b stays selected.
        sync.Update([R("c"), R("b"), R("a")], forceOrder: true);
        Assert.Equal(("cba", false), (Order(), sync.OrderHeld));
        Assert.Same(b, Assert.Single(list.SelectedItems.Cast<ListViewItem>()));
        // A live refresh of the sorted list keeps it in place and the selection with it.
        sync.Update([R("c"), R("b"), R("a")]);
        Assert.Equal("cba", Order());
        Assert.Same(b, list.SelectedItems[0]);
    }

    [Fact]
    public void TheSortedColumnShowsTheSystemsArrowAndNoOtherDoes()
    {
        using var host = new Form();
        var list = new BufferedListView { View = View.Details };
        foreach (var c in PoolCauses.Columns) list.Columns.Add(c);
        host.Controls.Add(list);
        _ = list.Handle;
        // Pool causes uses the same header arrow as the Services and tasks lists (ListSort).
        Assert.All(Enumerable.Range(0, 8), i => Assert.Equal(0, ListSort.ArrowOf(list, i)));
        ListSort.SetArrow(list, PoolCauses.GrewColumn, ascending: true);
        Assert.Equal(1, ListSort.ArrowOf(list, PoolCauses.GrewColumn)); // up: ascending
        Assert.Equal(0, ListSort.ArrowOf(list, 0));
        ListSort.SetArrow(list, PoolCauses.GrewColumn, ascending: false);
        Assert.Equal(-1, ListSort.ArrowOf(list, PoolCauses.GrewColumn)); // down: the other way
        ListSort.SetArrow(list, 7, ascending: true);
        Assert.Equal(0, ListSort.ArrowOf(list, PoolCauses.GrewColumn));
        Assert.Equal(1, ListSort.ArrowOf(list, 7));
        ListSort.SetArrow(list, -1, ascending: true);                    // the list's own order: no arrow
        Assert.All(Enumerable.Range(0, 8), i => Assert.Equal(0, ListSort.ArrowOf(list, i)));
        // The header's text stays as it was.
        Assert.Equal(PoolCauses.Columns, list.Columns.Cast<ColumnHeader>().Select(c => c.Text));
    }

    [Fact]
    public void AClickSortsByTheColumnAndASecondClickTurnsItRound()
    {
        var none = (-1, false);
        Assert.Equal((2, false), PoolCausesSort.Click(none, 2));               // first click: ascending
        Assert.Equal((2, true), PoolCausesSort.Click((2, false), 2));          // again: the other way
        Assert.Equal((2, false), PoolCausesSort.Click((2, true), 2));          // and back
        Assert.Equal((5, false), PoolCausesSort.Click((2, true), 5));          // another column starts ascending
        // The one rule every sortable list uses.
        Assert.Equal((3, true), ListSort.Next((0, true), 3));
        Assert.Equal((3, false), ListSort.Next((3, true), 3));
        Assert.Equal((3, true), ListSort.Next((3, false), 3));
    }
}
