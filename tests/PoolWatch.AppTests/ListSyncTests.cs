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
}
