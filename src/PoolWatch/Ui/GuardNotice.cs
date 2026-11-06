using PoolWatch.Engine;

namespace PoolWatch.Ui;

/// <summary>
/// The token-leak guard's notices: small non-modal windows at the bottom right of the screen, never a
/// modal box, shown only when the guard needs the user's click (the "NOT protected" notice and the click prompt; what
/// needs no click goes to the log and the findings only), and only for the sign-in check and what the user clicked, never
/// for the background recheck later in a sign-in (the tray icon shows that instead, <see cref="GuardTray"/>), and closed
/// by the guard when it finds that nothing needs that click, the notice also as a click prompt opens, which asks for the
/// same click (<see cref="Withdraw"/>). Clicking a
/// button there makes PoolWatch the foreground program, which is what Windows needs before it accepts the
/// ForegroundLockTimeout change. All calls marshal to the UI thread, in the order they were made, so a notice the guard
/// takes back as a prompt opens is closed before that prompt is shown.
/// </summary>
internal sealed class GuardNotices : IGuardUi
{
    private readonly SynchronizationContext _ui;
    private readonly Func<Task<bool>> _turnOn;
    /// <summary>The open "NOT protected" notice and the open click prompt (each null once its window closed).</summary>
    private Form? _notProtected, _prompt;

    /// <param name="turnOn">What "Turn on" in a "NOT protected" notice does (the guard's one-click path).</param>
    public GuardNotices(SynchronizationContext ui, Func<Task<bool>> turnOn)
    {
        _ui = ui;
        _turnOn = turnOn;
    }

    public void ShowNotProtected(string message) => _ui.Post(_ =>
    {
        _notProtected?.Close(); // ONE such notice
        var notice = Notice(message, ("Turn on", async f => { f.Close(); await _turnOn(); }), ("Not now", null));
        notice.FormClosed += (_, _) => { if (_notProtected == notice) _notProtected = null; };
        _notProtected = notice;
    }, null);

    public Task<bool> AskForClick(string message)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(_ =>
        {
            var prompt = Notice(message, ("OK", f => { done.TrySetResult(true); f.Close(); return Task.CompletedTask; }),
                                         ("Cancel", f => { done.TrySetResult(false); f.Close(); return Task.CompletedTask; }));
            prompt.FormClosed += (_, _) =>
            {
                done.TrySetResult(false);
                if (_prompt == prompt) _prompt = null;
            };
            _prompt = prompt;
        }, null);
        return done.Task;
    }

    /// <summary>Closes the open "NOT protected" notice and, when <paramref name="prompt"/>, the click prompt (it completes false).</summary>
    public void Withdraw(bool prompt) => _ui.Post(_ =>
    {
        _notProtected?.Close();
        if (prompt) _prompt?.Close();
    }, null);

    /// <summary>A small top-most window with the text and buttons, shown; a null action just closes it.</summary>
    private static Form Notice(string text, params (string label, Func<Form, Task>? action)[] buttons)
    {
        var f = NewNotice(text, buttons);
        f.Show(); // non-modal
        return f;
    }

    /// <summary>The notice's window as <see cref="Notice"/> shows it, not shown yet (its test looks at it here).</summary>
    internal static Form NewNotice(string text, params (string label, Func<Form, Task>? action)[] buttons)
    {
        var f = new Form
        {
            Text = "PoolWatch: token-leak guard", Icon = TrayIcons.App, FormBorderStyle = FormBorderStyle.FixedToolWindow, ShowInTaskbar = true, TopMost = true,
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
        return f;
    }
}
