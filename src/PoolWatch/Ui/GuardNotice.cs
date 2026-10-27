using PoolWatch.Engine;

namespace PoolWatch.Ui;

/// <summary>
/// The token-leak guard's notices (addendum 3): small non-modal windows at the bottom right of the screen, never a
/// modal box. Clicking a button there makes PoolWatch the foreground program, which is what Windows needs before it
/// accepts the ForegroundLockTimeout change. All calls marshal to the UI thread.
/// </summary>
internal sealed class GuardNotices : IGuardUi
{
    private readonly SynchronizationContext _ui;
    private readonly Func<Task<bool>> _turnOn;
    private Form? _outcome;

    /// <param name="turnOn">What "Turn on" in a "NOT protected" notice does (the guard's one-click path).</param>
    public GuardNotices(SynchronizationContext ui, Func<Task<bool>> turnOn)
    {
        _ui = ui;
        _turnOn = turnOn;
    }

    public void ShowOutcome(string message, bool protectedNow) => _ui.Post(_ =>
    {
        _outcome?.Close(); // ONE outcome notice
        _outcome = protectedNow
            ? Notice(message, ("OK", null))
            : Notice(message, ("Turn on", async f => { f.Close(); await _turnOn(); }), ("Not now", null));
    }, null);

    public Task<bool> AskForClick(string message)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(_ =>
        {
            var f = Notice(message, ("OK", f => { done.TrySetResult(true); f.Close(); return Task.CompletedTask; }),
                                    ("Cancel", f => { done.TrySetResult(false); f.Close(); return Task.CompletedTask; }));
            f.FormClosed += (_, _) => done.TrySetResult(false);
        }, null);
        return done.Task;
    }

    /// <summary>A small top-most window with the text and buttons; a null action just closes it.</summary>
    private static Form Notice(string text, params (string label, Func<Form, Task>? action)[] buttons)
    {
        var f = new Form
        {
            Text = "PoolWatch: token leak guard", FormBorderStyle = FormBorderStyle.FixedToolWindow, ShowInTaskbar = true, TopMost = true,
            StartPosition = FormStartPosition.Manual, Width = 460, Height = 190, Font = new Font("Segoe UI", 9f),
        };
        var label = new TextBox { Text = text.Replace("\n", Environment.NewLine), Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, Dock = DockStyle.Fill, BackColor = SystemColors.Control, TabStop = false };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(6) };
        foreach (var (lbl, action) in buttons)
        {
            var b = new Button { Text = lbl, AutoSize = true };
            b.Click += async (_, _) =>
            {
                if (action is null) { f.Close(); return; }
                b.Enabled = false;
                try { await action(f); } catch { /* the guard logs its own failures */ }
            };
            bar.Controls.Add(b);
        }
        f.Controls.Add(label);
        f.Controls.Add(bar);
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        f.Location = new Point(area.Right - f.Width - 12, area.Bottom - f.Height - 12);
        f.Show(); // non-modal
        return f;
    }
}
