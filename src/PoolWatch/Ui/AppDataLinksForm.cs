using PoolWatch.Core.Links;
using PoolWatch.Engine;

namespace PoolWatch.Ui;

/// <summary>What the AppData links window asks the user, so the tests answer it: a yes or no before a change, and the steps that are left.</summary>
internal interface IAppDataLinkDialogs
{
    /// <summary>The question before a change; true for yes.</summary>
    bool Confirm(string title, string question);

    /// <summary>The steps left to the user after the guided fix, with the prompt its button copies for the user's AI agent.</summary>
    void ShowSteps(string title, string steps, string prompt);
}

/// <summary>
/// The AppData links window without a window (so it is tested): a look for the links, and each row's button. A row's
/// change runs only after the user's yes to a question that says exactly what changes, off the UI thread, through
/// <see cref="AppDataLinkFixes"/> (recorded first, so Active changes can put it back). The guided fix then shows the
/// steps left, and the prompt for the user's AI agent. Refuses every change in the CI UI test.
/// </summary>
internal sealed class AppDataLinksView(AppDataLinkWatch watch, AppDataLinkFixes fixes, bool smoke, Action<string> timeline, string profile)
{
    /// <summary>Where the change was made, for the timeline and Active changes.</summary>
    public const string Via = "the AppData links window";

    public const string NothingChanged = "Nothing was changed.";

    public LinkScan? Scan { get; private set; }

    /// <summary>Looks for the links now (a worker thread); the line the window shows above the list.</summary>
    public async Task<string> Look()
    {
        try { Scan = await watch.ScanNow(); }
        catch (Exception ex) { return "PoolWatch could not look for the links: " + ex.Message; }
        return Status(Scan);
    }

    /// <summary>The line above the list for a look's result.</summary>
    internal static string Status(LinkScan scan)
    {
        if (!scan.Applies) return LinkWords.NotInstalled;
        int found = scan.Findings.Count(f => f.Kind != LinkKind.Projects);
        if (found == 0) return LinkWords.NothingFound;
        string rows = found == 1 ? "1 thing" : $"{found} things";
        return scan.ToFix switch
        {
            0 => $"PoolWatch found {rows} that go through links under AppData. Select one to see what it costs and what fixes it.",
            1 => $"PoolWatch found {rows} that go through links under AppData; PoolWatch can fix one of them. Select one to see what it costs.",
            int n => $"PoolWatch found {rows} that go through links under AppData; PoolWatch can fix {n} of them. Select one to see what it costs.",
        };
    }

    /// <summary>The row's button: asks first, then changes, and says what happened (empty for a row it does nothing for).</summary>
    public async Task<string> Fix(LinkFinding f, IAppDataLinkDialogs dialogs)
    {
        if (smoke) return RecommendationActions.Refused;
        if (!f.CanFix) return "";
        if (f.Kind == LinkKind.PathEntry)
        {
            if (!dialogs.Confirm(LinkWords.ReplaceTitle, LinkWords.ReplaceQuestion(f))) return NothingChanged;
            var (ok, message) = await Task.Run(() => fixes.Replace(f, Via));
            if (ok) timeline(message);
            return message;
        }
        if (!dialogs.Confirm(LinkWords.SetTitle, LinkWords.SetQuestion(f))) return NothingChanged;
        var (_, said, set) = await Task.Run(() => fixes.SetVariables(f, Via));
        if (set.Count > 0)
        {
            timeline(string.Join(" ", set.Select(s => Core.Changes.ChangeWords.EnvironmentVariable(s.Name, s.Value) + ".")));
            dialogs.ShowSteps(LinkWords.StepsTitle, LinkWords.StepsText(f, set), LinkWords.Prompt(f, set, profile));
        }
        return said;
    }
}

/// <summary>
/// "Links under AppData": what the AppData link finder found (each PATH entry through a junction or link under AppData,
/// winget's Links folder, uv, pnpm and the Codex command-line tool kept under AppData, and the advice about projects), a
/// row each with what it costs and what fixes it, and one button for the selected row. Opened from the tray menu and the
/// Simple view's suggestion; created when opened and disposed when closed. The logic is in <see cref="AppDataLinksView"/>.
/// </summary>
internal sealed class AppDataLinksForm : Form
{
    private readonly AppDataLinksView _view;
    private readonly IAppDataLinkDialogs _dialogs;
    private readonly ListView _list = new()
    {
        View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, Dock = DockStyle.Fill, HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };
    private readonly TextBox _details = new()
    {
        ReadOnly = true, Multiline = true, WordWrap = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None,
        BackColor = SystemColors.Window, TabStop = false,
    };
    private readonly Label _status = new() { AutoSize = true, UseMnemonic = false, Text = LinkWords.Looking };
    private readonly Button _fix = new() { Text = "Fix", AutoSize = true, Enabled = false };
    private readonly Button _again = new() { Text = LinkWords.LookAgain, AutoSize = true, Enabled = false };
    private readonly Button _close = new() { Text = LinkWords.Close, AutoSize = true };
    private IReadOnlyList<LinkFinding> _rows = [];
    private bool _busy;

    public AppDataLinksForm(AppDataLinksView view, IAppDataLinkDialogs? dialogs = null)
    {
        _view = view;
        _dialogs = dialogs ?? new Dialogs(this);
        Text = LinkWords.WindowTitle;
        Icon = TrayIcons.App;
        Font = new Font("Segoe UI", 9f);
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        Size = LogicalToDeviceUnits(new Size(820, 600));
        MinimumSize = LogicalToDeviceUnits(new Size(520, 400));
        BackColor = SystemColors.Window;

        int side = S(16);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(side, S(12), side, S(12)) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // the lead
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // the status line
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 45));   // the list
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 55));   // the details
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // the buttons
        var lead = new Label { Text = LinkWords.Lead, AutoSize = true, UseMnemonic = false, MaximumSize = new Size(S(780), 0), Margin = new Padding(0, 0, 0, S(8)) };
        _status.MaximumSize = new Size(S(780), 0);
        _status.Margin = new Padding(0, 0, 0, S(8));
        _list.Columns.Add(LinkWords.WhatColumn, S(330));
        _list.Columns.Add(LinkWords.WhereColumn, S(200));
        _list.Columns.Add(LinkWords.FixColumn, S(220));
        _list.SelectedIndexChanged += (_, _) => ShowSelected();
        var detailsPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, S(8), 0, 0), BackColor = SystemColors.Window };
        detailsPanel.Controls.Add(_details);
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
            Margin = new Padding(0, S(8), 0, 0),
        };
        _close.Click += (_, _) => Close();
        _again.Click += async (_, _) => await LookNow();
        _fix.Click += async (_, _) => await FixSelected();
        buttons.Controls.AddRange([_close, _again, _fix]);
        CancelButton = _close;
        layout.Controls.Add(lead, 0, 0);
        layout.Controls.Add(_status, 0, 1);
        layout.Controls.Add(_list, 0, 2);
        layout.Controls.Add(detailsPanel, 0, 3);
        layout.Controls.Add(buttons, 0, 4);
        Controls.Add(layout);
        Shown += async (_, _) => await LookNow();
    }

    private int S(int logical) => LogicalToDeviceUnits(logical);

    /// <summary>Looks again and lists what it found; the selected row stays selected when it is still there.</summary>
    private async Task LookNow()
    {
        if (_busy) return;
        _busy = true;
        _again.Enabled = _fix.Enabled = false;
        _status.Text = LinkWords.Looking;
        string? selected = Selected()?.Key;
        try
        {
            string status = await _view.Look();
            if (IsDisposed) return;
            _status.Text = status;
            _rows = _view.Scan?.Findings ?? [];
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var f in _rows)
                _list.Items.Add(new ListViewItem(new[] { LinkWords.What(f), LinkWords.Where(f), LinkWords.Fix(f) }) { Tag = f });
            _list.EndUpdate();
            var again = _list.Items.Cast<ListViewItem>().FirstOrDefault(i => ((LinkFinding)i.Tag!).Key == selected) ?? _list.Items.Cast<ListViewItem>().FirstOrDefault();
            if (again is not null) again.Selected = true;
            ShowSelected();
        }
        finally
        {
            _busy = false;
            if (!IsDisposed) { _again.Enabled = true; ShowSelected(); }
        }
    }

    private LinkFinding? Selected() => _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as LinkFinding : null;

    private void ShowSelected()
    {
        var f = Selected();
        _details.Text = f is null ? "" : LinkWords.Details(f).ReplaceLineEndings("\r\n");
        _fix.Text = f is null ? "Fix" : LinkWords.Button(f);
        _fix.Enabled = !_busy && f is { CanFix: true };
    }

    private async Task FixSelected()
    {
        if (_busy || Selected() is not { CanFix: true } f) return;
        _busy = true;
        _fix.Enabled = _again.Enabled = false;
        string said;
        try { said = await _view.Fix(f, _dialogs); }
        catch (Exception ex) { said = "That did not work: " + ex.Message; }
        finally { _busy = false; }
        if (IsDisposed) return;
        if (said == AppDataLinksView.NothingChanged || said.Length == 0) { ShowSelected(); _again.Enabled = true; return; }
        await LookNow();
        if (!IsDisposed) _status.Text = said;
    }

    /// <summary>The real questions: Windows' message box over this window, and the steps in a window of their own.</summary>
    private sealed class Dialogs(Form owner) : IAppDataLinkDialogs
    {
        public bool Confirm(string title, string question) =>
            MessageBox.Show(owner, question, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

        public void ShowSteps(string title, string steps, string prompt) => new InstructionsDialog(title, steps, LinkWords.CopyPrompt, prompt).Show(owner);
    }
}
