using System.Runtime.CompilerServices;

namespace PoolWatch.AppTests;

/// <summary>
/// No test may put a window or a dialog on the desktop. An exception inside a control's window procedure (a list event, a
/// paint, a reflected notification) would otherwise go to WinForms' "Unhandled exception" dialog, which waits on the
/// desktop for a click. Thrown instead, it fails the test that caused it. Set once for the whole test assembly, before any
/// window handle exists (WinForms refuses the switch after that).
/// </summary>
internal static class NoDialogs
{
#pragma warning disable CA2255 // a test assembly is the place for this: it must run before any test creates a window
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void ThrowInsteadOfShowingADialog() =>
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException, threadScope: false);
}

/// <summary>The switch above is in force (a test run without it could show a dialog again).</summary>
public class NoDialogsTests
{
    [Fact]
    public void AnExceptionInAWindowProcedureIsThrownNotShownInADialog()
    {
        // Without the switch WinForms would catch the exception and hand it to this thread's handler (never to a dialog,
        // as this one is attached): the test then fails on the Throws below instead of waiting on the desktop.
        Exception? caught = null;
        ThreadExceptionEventHandler handler = (_, e) => caught = e.Exception;
        Application.ThreadException += handler;
        try
        {
            using var host = new Form();
            using var list = new ThrowingList { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true };
            list.Columns.Add("Name", 100);
            host.Controls.Add(list);
            host.CreateControl();
            _ = list.Handle;
            list.Items.Add("one");
            list.Armed = true;
            // Ticking the row makes Windows send the list its notification; the handler's exception comes back here.
            var thrown = Assert.Throws<InvalidOperationException>(() => list.Items[0].Checked = true);
            Assert.Equal(ThrowingList.Message, thrown.Message);
            Assert.Null(caught);
        }
        finally { Application.ThreadException -= handler; }
    }

    private sealed class ThrowingList : ListView
    {
        public const string Message = "thrown in the list's window procedure";
        public bool Armed;

        protected override void OnItemChecked(ItemCheckedEventArgs e)
        {
            base.OnItemChecked(e);
            if (Armed) throw new InvalidOperationException(Message);
        }
    }
}
