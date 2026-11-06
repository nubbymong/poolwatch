namespace PoolWatch.Ui;

/// <summary>Small input dialogs: the Mark note, and the experiment's app and phase length.</summary>
internal static class PromptDialog
{
    /// <summary>One line of text; null when cancelled.</summary>
    public static string? Ask(string title, string label, string initial = "")
    {
        using var form = Dialog(title, 140);
        var lbl = new Label { Text = label, AutoSize = true, Left = 12, Top = 12 };
        var box = new TextBox { Left = 12, Top = 36, Width = 400, Text = initial, MaxLength = Core.Experiments.Marks.MaxLength };
        form.Controls.AddRange([lbl, box]);
        AddButtons(form, 70);
        form.Shown += (_, _) => { box.Focus(); box.SelectAll(); };
        return form.ShowDialog() == DialogResult.OK ? box.Text : null;
    }

    /// <summary>App name and minutes per phase; null when cancelled.</summary>
    public static (string app, double minutes)? AskExperiment(double defaultMinutes)
    {
        using var form = Dialog("PoolWatch experiment", 230);
        var lbl = new Label
        {
            Left = 12, Top = 10, Width = 410, Height = 60,
            Text = "Three phases: a baseline with the app closed, then you start it, then you close it. PoolWatch prompts you at each step " +
                   "and compares every pool tag, commit and handles between the phases.",
        };
        var appLabel = new Label { Text = "App (as in its exe name, e.g. ChatGPT, Codex, StreamDeck):", AutoSize = true, Left = 12, Top = 76 };
        var app = new TextBox { Left = 12, Top = 98, Width = 400, MaxLength = 80 };
        var minLabel = new Label { Text = "Minutes per phase:", AutoSize = true, Left = 12, Top = 132 };
        var minutes = new NumericUpDown { Left = 140, Top = 129, Width = 70, Minimum = 1, Maximum = 120, Value = (decimal)Math.Clamp(defaultMinutes, 1, 120) };
        form.Controls.AddRange([lbl, appLabel, app, minLabel, minutes]);
        AddButtons(form, 165);
        form.Shown += (_, _) => app.Focus();
        if (form.ShowDialog() != DialogResult.OK || string.IsNullOrWhiteSpace(app.Text)) return null;
        return (app.Text.Trim(), (double)minutes.Value);
    }

    private static Form Dialog(string title, int height) => new()
    {
        Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = true,
        StartPosition = FormStartPosition.CenterScreen, ClientSize = new Size(430, height), TopMost = true, Font = new Font("Segoe UI", 9f),
        Icon = TrayIcons.App, // its taskbar button shows the binoculars, as every window does
    };

    private static void AddButtons(Form form, int top)
    {
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 256, Top = top, Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 337, Top = top, Width = 75 };
        form.Controls.AddRange([ok, cancel]);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
    }
}
