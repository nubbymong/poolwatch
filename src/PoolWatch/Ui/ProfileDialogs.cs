using PoolWatch.Core.Services;

namespace PoolWatch.Ui;

/// <summary>What the save dialog opens with: the selected profile of the owner's filled in, else an empty name.</summary>
/// <param name="Kept">Ticked services a profile can hold (those PoolWatch recognises).</param>
/// <param name="NotKept">Ticked services it cannot (third-party ones PoolWatch does not recognise).</param>
internal sealed record SaveRequest(string Name, bool AtStart, int DelayMinutes, int Kept, int NotKept, ServiceProfileSet Set);

internal sealed record SaveAnswer(string Name, bool AtStart, int DelayMinutes);

/// <summary>The profile row's questions (the tests answer them themselves).</summary>
internal interface IProfileDialogs
{
    SaveAnswer? AskSave(SaveRequest request);
    /// <summary>The new name, or null when cancelled.</summary>
    string? AskRename(string current, ServiceProfileSet set);
    bool AskDelete(string name, bool atStart);
    /// <summary>A yes or no question (Yes: true).</summary>
    bool AskYesNo(string question, string title);
    void Tell(string message);
}

/// <summary>The real dialogs, owned by the main window.</summary>
internal sealed class ProfileDialogs(IWin32Window owner) : IProfileDialogs
{
    public SaveAnswer? AskSave(SaveRequest request)
    {
        using var d = new SaveProfileDialog(request);
        return d.ShowDialog(owner) == DialogResult.OK ? d.Answer : null;
    }

    public string? AskRename(string current, ServiceProfileSet set)
    {
        using var d = new RenameProfileDialog(current, set);
        return d.ShowDialog(owner) == DialogResult.OK ? d.NewName : null;
    }

    public bool AskDelete(string name, bool atStart) =>
        MessageBox.Show(owner, ProfileText.DeleteQuestion(name, atStart), ProfileText.DeleteTitle, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK;

    public bool AskYesNo(string question, string title) =>
        MessageBox.Show(owner, question, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

    public void Tell(string message) => MessageBox.Show(owner, message, "PoolWatch");
}

/// <summary>A small fixed dialog that sizes itself to its text at any display scaling.</summary>
internal abstract class ProfileDialog : Form
{
    protected static readonly Color ErrorText = Color.FromArgb(0xc4, 0x2b, 0x1c);
    /// <summary>One font for every profile dialog (a font given to a form is not disposed with it).</summary>
    internal static readonly Font DialogFont = new("Segoe UI", 9f);
    protected readonly TableLayoutPanel Body = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Dock = DockStyle.Fill };

    protected ProfileDialog(string title)
    {
        Text = title;
        Font = DialogFont;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = Px(14, 14, 16, 14);
        Controls.Add(Body);
    }

    protected int S(int logical) => LogicalToDeviceUnits(logical);
    protected Padding Px(int l, int t, int r, int b) => new(S(l), S(t), S(r), S(b));

    /// <summary>A text that wraps at the dialog's width, indented by <paramref name="indent"/>; grey for a note.</summary>
    protected Label Line(string text, int indent = 0, int top = 0, bool note = false) => new()
    {
        Text = text, AutoSize = true, MaximumSize = new Size(S(400) - S(indent), 0), Margin = Px(indent, top, 0, 0), UseMnemonic = false,
        ForeColor = note ? SystemColors.GrayText : SystemColors.ControlText,
    };

    protected (TextBox box, Label note) NameRow(string name)
    {
        var row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty };
        var label = new Label { Text = "Name:", AutoSize = false, Width = S(44), Height = S(23), TextAlign = ContentAlignment.MiddleLeft, Margin = Px(0, 0, 8, 0) };
        var box = new TextBox { Text = name, Width = S(330), MaxLength = ServiceProfileRules.MaxNameLength, Margin = Padding.Empty, AccessibleName = "Name" };
        row.Controls.AddRange([label, box]);
        Body.Controls.Add(row);
        var note = Line("", indent: 52, top: 3, note: true);
        Body.Controls.Add(note);
        return (box, note);
    }

    protected (Button ok, Button cancel) Buttons(string okText)
    {
        var row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Right, Margin = Px(0, 16, 0, 0), WrapContents = false };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(S(75), 0), Margin = Px(8, 0, 0, 0), UseVisualStyleBackColor = true };
        var ok = new Button { Text = okText, DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(S(75), 0), Margin = Padding.Empty, UseVisualStyleBackColor = true };
        row.Controls.AddRange([cancel, ok]); // right to left: OK shows first
        Body.Controls.Add(row);
        AcceptButton = ok;
        CancelButton = cancel;
        return (ok, cancel);
    }

    protected static void Note(Label label, string text, bool error = false)
    {
        label.Text = text;
        label.ForeColor = error ? ErrorText : SystemColors.GrayText;
        label.Visible = text.Length > 0;
    }
}

/// <summary>
/// "Save as profile...": a name (an owner's profile of that name is replaced; a built-in's name is refused), what it keeps
/// off, and whether PoolWatch applies it when it starts, after how many minutes (1 to 15).
/// </summary>
internal sealed class SaveProfileDialog : ProfileDialog
{
    private readonly SaveRequest _request;
    internal readonly TextBox NameBox;
    internal readonly Label NameNote, What, NotKeptNote, StartNote, OtherNote;
    internal readonly CheckBox AtStart;
    internal readonly NumericUpDown Delay;
    internal readonly Button Save;

    public SaveProfileDialog(SaveRequest request) : base(ProfileText.SaveTitle)
    {
        _request = request;
        (NameBox, NameNote) = NameRow(request.Name);
        What = Line("", top: 12);
        NotKeptNote = Line("", top: 3, note: true);
        Body.Controls.AddRange([What, NotKeptNote]);
        var start = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Px(0, 12, 0, 0) };
        AtStart = new CheckBox { Text = "Apply this profile when PoolWatch starts", AutoSize = true, Checked = request.AtStart, Margin = Px(0, 3, 0, 0), UseMnemonic = false };
        var after = new Label { Text = ", after", AutoSize = true, Margin = Px(0, 4, 6, 0) };
        Delay = new NumericUpDown
        {
            Minimum = ServiceProfileRules.MinDelayMinutes, Maximum = ServiceProfileRules.MaxDelayMinutes, Width = S(48), Margin = Padding.Empty, AccessibleName = "Minutes",
            Value = Math.Clamp(request.DelayMinutes, ServiceProfileRules.MinDelayMinutes, ServiceProfileRules.MaxDelayMinutes),
        };
        var minutes = new Label { Text = "minutes", AutoSize = true, Margin = Px(6, 4, 0, 0) };
        start.Controls.AddRange([AtStart, after, Delay, minutes]);
        StartNote = Line(ProfileText.AtStartNote, indent: 19, top: 4, note: true);
        OtherNote = Line("", indent: 19, top: 4, note: true);
        Body.Controls.AddRange([start, StartNote, OtherNote]);
        (Save, _) = Buttons("Save");
        NameBox.TextChanged += (_, _) => RefreshState();
        AtStart.CheckedChanged += (_, _) => RefreshState();
        Shown += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
        RefreshState();
    }

    public SaveAnswer Answer => new(NameBox.Text.Trim(), AtStart.Checked, (int)Delay.Value);

    /// <summary>The notes and the Save button as the name and the switch are now.</summary>
    internal void RefreshState()
    {
        var (note, error) = ProfileText.SaveNameNote(NameBox.Text, _request.Set);
        Note(NameNote, note, error);
        What.Text = ProfileText.SaveWhat(_request.Kept);
        Note(NotKeptNote, ProfileText.NotKept(_request.NotKept));
        Delay.Enabled = AtStart.Checked;
        Note(OtherNote, ProfileText.StopsOther(AtStart.Checked, NameBox.Text, _request.Set));
        Save.Enabled = !error && _request.Kept > 0;
    }
}

/// <summary>"Rename...": a new name for one of the owner's profiles (not a built-in's, nor another profile's).</summary>
internal sealed class RenameProfileDialog : ProfileDialog
{
    private readonly string _current;
    private readonly ServiceProfileSet _set;
    internal readonly TextBox NameBox;
    internal readonly Label NameNote;
    internal readonly Button Rename;

    public RenameProfileDialog(string current, ServiceProfileSet set) : base(ProfileText.RenameTitle)
    {
        _current = current;
        _set = set;
        (NameBox, NameNote) = NameRow(current);
        (Rename, _) = Buttons("Rename");
        NameBox.TextChanged += (_, _) => RefreshState();
        Shown += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
        RefreshState();
    }

    public string NewName => NameBox.Text.Trim();

    internal void RefreshState()
    {
        string note = ProfileText.RenameNote(NameBox.Text, _current, _set);
        Note(NameNote, note, error: true);
        Rename.Enabled = NewName.Length > 0 && note.Length == 0;
    }
}
