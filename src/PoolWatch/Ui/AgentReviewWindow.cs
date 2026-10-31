using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PoolWatch.Core.Services;
using PoolWatch.Core.Services.Agent;

namespace PoolWatch.Ui;

/// <summary>One item of the review: its name on this PC, what it is and what Apply does to it, and a line more (the agent's reason, or why PoolWatch refuses it).</summary>
internal sealed record AgentReviewRow(string Name, string What, string? Note);

/// <summary>
/// A part of the review: what Apply turns off, what PoolWatch refuses, what is left as it is. <paramref name="More"/>: how
/// many more items the part has than it lists (only what is left as it is leaves any out).
/// </summary>
internal sealed record AgentReviewSection(string Heading, IReadOnlyList<AgentReviewRow> Rows, int More = 0)
{
    /// <summary>The line after the rows when some are not listed; null when every one is.</summary>
    public string? MoreText => More > 0 ? $"and {More.ToString(CultureInfo.InvariantCulture)} more, not listed here" : null;
}

/// <summary>
/// The review of an imported AI agent answer, without a window (so it is tested): the plan the Core read
/// (<see cref="AgentAnswer.Read"/>) in three parts, each item by its name on this PC (<see cref="AgentPlanItem.Name"/>,
/// never the answer's name alone) with what it is: what Apply will turn off, and how; what PoolWatch refuses to turn off,
/// with why; and what stays as it is (off already, kept by the agent, or not on this PC). Or, for an answer PoolWatch did
/// not read, why, in plain words. Nothing in it has changed anything. Every item Apply changes and every item PoolWatch
/// refuses, with why, is listed (both come from the answer's lists of what to turn off, which the Core bounds); what is
/// left as it is lists at most <see cref="MaxListed"/> items and says how many more there are, as an answer's lists of
/// what to keep may name up to 1,500 things that are not on this PC.
/// </summary>
internal sealed class AgentReview
{
    public const string Title = "Review your agent's answer";
    public const string TurnedOffHeading = "Will be turned off", RefusedHeading = "PoolWatch will not turn these off", LeftHeading = "Left as they are";
    public const string Reading = "Reading the answer and what runs on this PC…", Applying = "Applying…";
    public const string NothingChanged = "Nothing was changed.";

    /// <summary>The most items listed of what is left as it is.</summary>
    public const int MaxListed = 50;

    /// <summary>
    /// The most names the question about services PoolWatch does not recognise, and each line of the message after Apply,
    /// name; the rest are counted, so the message's buttons stay on the screen.
    /// </summary>
    public const int MaxNamed = 15;

    /// <summary>
    /// The most reasons the message after Apply gives for what it did not change, each on a line with its items; the items
    /// of the rest are counted on one more line, so the message's button stays on the screen.
    /// </summary>
    public const int MaxReasons = 10;

    /// <summary>Why there is no review when a name on this PC could not be checked for private details in time (the Core's redaction gave up).</summary>
    public const string RedactionGaveUp = "PoolWatch could not check every name on this PC for private details in time, so it did not read the " +
                                          "answer. Close this window and try again.";

    /// <summary>Why there is no review when checking the answer itself took too long: the answer's, not this PC's.</summary>
    public const string AnswerTookTooLong = "PoolWatch could not check this answer in time, so it did not read it. Ask your agent for a new answer.";

    private AgentReview(AgentPlan? plan, string? error, IReadOnlyList<ServiceOption>? services = null)
    {
        Plan = plan;
        Error = error;
        _services = services ?? [];
    }

    public AgentPlan? Plan { get; }

    /// <summary>Why the answer was not read; null when it was.</summary>
    public string? Error { get; }

    /// <summary>The services on this PC as the plan was read against them: which running ones depend on a planned one.</summary>
    private readonly IReadOnlyList<ServiceOption> _services;

    /// <param name="services">The services as read for the plan (<see cref="AgentItemList.Options"/>), so a row can say
    /// that a running service depends on one it would stop; none: no row says so.</param>
    public static AgentReview Of(AgentPlan plan, IReadOnlyList<ServiceOption>? services = null) => new(plan, null, services);

    public static AgentReview Failed(string why) => new(null, why);

    /// <summary>
    /// Reads the answer file at <paramref name="path"/> (bounded, never a link: <see cref="IAgentFiles.Read"/>), then the
    /// items on this PC as they are now (<paramref name="items"/>, built again for this answer), and the answer against them.
    /// A file PoolWatch cannot read or an answer it refuses is a review that says why; it never throws for what the file
    /// holds. The list's redaction giving up is said plainly; any other failure to read this PC is thrown, for the window to
    /// say so. Checking the answer taking too long is said to be the answer's, never a failure to read this PC.
    /// </summary>
    /// <param name="read">Reads the answer against the list (<see cref="AgentAnswer.Read(ReadOnlySpan{byte}, AgentItemList)"/>; the tests pass another).</param>
    public static AgentReview Load(string path, IAgentFiles files, Func<AgentItemList> items, Func<byte[], AgentItemList, AgentAnswerResult>? read = null)
    {
        var (bytes, error) = files.Read(path, AgentAnswer.MaxBytes + 1);
        if (bytes is null) return Failed(error ?? "PoolWatch could not read the file.");
        AgentItemList list;
        try { list = items(); }
        catch (RegexMatchTimeoutException) { return Failed(RedactionGaveUp); }
        AgentAnswerResult answer;
        try { answer = (read ?? ((b, l) => AgentAnswer.Read(b, l)))(bytes, list); }
        catch (RegexMatchTimeoutException) { return Failed(AnswerTookTooLong); }
        return answer.Plan is { } plan ? Of(plan, list.Options) : Failed(answer.Error ?? "PoolWatch could not read the answer.");
    }

    /// <summary>How many changes Apply makes.</summary>
    public int Changes => Plan is { } p ? p.Services.Count + p.Startup.Count + p.Tasks.Count : 0;

    public bool CanApply => Changes > 0;

    /// <summary>The primary button: "Apply 3 changes", "Apply 1 change"; "Apply" when there is nothing to change.</summary>
    public string ApplyText => Changes switch
    {
        0 => "Apply",
        1 => "Apply 1 change",
        int n => $"Apply {n.ToString(CultureInfo.InvariantCulture)} changes",
    };

    /// <summary>The window's line under its title.</summary>
    public string Lead => Error is { } why ? $"{NothingChanged} {why}"
        : CanApply ? "Nothing has changed yet. This is exactly what Apply changes on this PC, and what it leaves as it is."
        : "Nothing in this answer changes this PC, so there is nothing to apply.";

    /// <summary>The parts that have items, in order.</summary>
    public IReadOnlyList<AgentReviewSection> Sections
    {
        get
        {
            if (Plan is not { } p) return [];
            var off = p.Services.Select(s => new AgentReviewRow(s.Item.Name, "service · " + ServiceAction(s), Agent(s.Item.Reason)))
                .Concat(p.Startup.Select(s =>
                    new AgentReviewRow(s.Item.Name, $"startup app in {OneLine(s.Entry.Location)} · no longer starts at sign-in", Agent(s.Item.Reason))))
                .Concat(p.Tasks.Select(t =>
                    new AgentReviewRow(t.Item.Name, $"scheduled task {OneLine(t.Task.Path)} · disabled, so it no longer runs on its own", Agent(t.Item.Reason))))
                .ToList();
            var refused = p.Refused.Select(r => new AgentReviewRow(r.Item.Name, Kind(r.Item.Kind), r.Why)).ToList();
            var left = p.AlreadyOff.Select(i => new AgentReviewRow(i.Name, Kind(i.Kind) + " · off already", null))
                .Concat(p.Kept.Select(i => new AgentReviewRow(i.Name, Kind(i.Kind) + " · your agent says to keep it", Agent(i.Reason))))
                .Concat(p.KeepNotFound.Select(n => new AgentReviewRow(n, "not on this PC now, so there is nothing to keep", null)))
                .ToList();
            var sections = new List<AgentReviewSection>();
            if (off.Count > 0) sections.Add(new(Count(TurnedOffHeading, off.Count), off)); // every change, each by its name
            if (refused.Count > 0) sections.Add(new(Count(RefusedHeading, refused.Count), refused)); // every refusal, each with why
            if (left.Count > 0) sections.Add(new(Count(LeftHeading, left.Count), left.Take(MaxListed).ToList(), Math.Max(0, left.Count - MaxListed)));
            return sections;
        }
    }

    /// <summary>The planned services of other apps that PoolWatch does not recognise: Apply asks about them on their own (<see cref="UnknownQuestion"/>).</summary>
    public IReadOnlyList<PlannedService> Unknown => Plan?.Services.Where(s => s.Option.Group == ServiceGroup.Unknown).ToList() ?? [];

    /// <summary>
    /// The second question before Apply turns off services PoolWatch does not recognise (Yes: them too), naming each, up to
    /// <see cref="MaxNamed"/>, so its Yes and No stay on the screen; the rest are counted, and the review lists them.
    /// </summary>
    public string UnknownQuestion
    {
        get
        {
            var names = Unknown.Select(s => s.Item.Name).ToList();
            bool one = names.Count == 1;
            int more = names.Count - MaxNamed;
            string rest = more > 0 ? $"\n  and {more.ToString(CultureInfo.InvariantCulture)} more (listed under Will be turned off)" : "";
            return $"{(one ? "This service is" : "These services are")} from other apps, and PoolWatch does not recognise {(one ? "it" : "them")}:\n\n" +
                   $"  {string.Join("\n  ", names.Take(MaxNamed))}{rest}\n\n" +
                   $"Yes turns {(one ? "it" : "them")} off with the rest. No leaves {(one ? "it as it is" : "them as they are")}.";
        }
    }

    /// <summary>The services Apply turns off, by their names on this PC: all of them, or, without <paramref name="withUnknown"/>, those PoolWatch recognises.</summary>
    public IReadOnlyList<string> ServicesToApply(bool withUnknown) =>
        Plan?.Services.Where(s => withUnknown || s.Option.Group != ServiceGroup.Unknown).Select(s => s.Option.Service.Name).ToList() ?? [];

    /// <summary>
    /// What Apply did, each result on a line with the items it is for (at most <see cref="MaxNamed"/> by name, the rest
    /// counted; at most <see cref="MaxReasons"/> reasons for what was not changed, the items of the rest counted), and
    /// where to put it back. A service whose start type was set but that is not known to have stopped
    /// (<see cref="ServicePlan.IsNotStoppedText"/>) is a change: past the last reason it is counted apart, never as not changed.
    /// </summary>
    public static string ResultText(IReadOnlyList<(string Name, string? Error)> results)
    {
        var shown = new List<(string, string?)>();
        int reasons = 0, unsaid = 0, setUnsaid = 0;
        foreach (var same in results.GroupBy(r => r.Error))
        {
            if (same.Key is not null && ++reasons > MaxReasons)
            {
                if (ServicePlan.IsNotStoppedText(same.Key)) setUnsaid += same.Count();
                else unsaid += same.Count();
                continue;
            }
            int named = 0, more = 0;
            foreach (var r in same)
            {
                if (ServicesPageText.AboutNoItem(r.Name) || named++ < MaxNamed) shown.Add(r);
                else more++;
            }
            if (more > 0) shown.Add(($"and {more.ToString(CultureInfo.InvariantCulture)} more", same.Key));
        }
        string text = ServicesPageText.ResultMessage(shown, NothingChanged);
        if (setUnsaid > 0) text += $"\nand {setUnsaid.ToString(CultureInfo.InvariantCulture)} more set to Disabled but not known to have stopped, for other reasons";
        if (unsaid > 0) text += $"\nand {unsaid.ToString(CultureInfo.InvariantCulture)} more not changed, for other reasons";
        return results.Any(r => r.Error is null || ServicePlan.IsNotStoppedText(r.Error)) ? text + "\n\nActive changes lists each change, with a way to put it back." : text;
    }

    private static string Count(string heading, int n) => $"{heading} ({n.ToString(CultureInfo.InvariantCulture)})";

    private static string Kind(AgentItemKind kind) => kind switch
    {
        AgentItemKind.Service => "service",
        AgentItemKind.Startup => "startup app",
        _ => "scheduled task",
    };

    /// <summary>
    /// What Apply does to a service, as the Services tab's question says it. Windows does not stop a service while one that
    /// depends on it runs, so a running service that running ones depend on (whether Apply turns those off or not) is
    /// "set to not start; stopped if AcmeMaps, which depends on it, has stopped by then, else it stops when Windows restarts".
    /// </summary>
    private string ServiceAction(PlannedService s)
    {
        if (s.NewStart == ServiceStartType.Manual) return "set to Manual, so it starts only when something asks for it";
        if (!s.Option.Service.Running) return "set to not start";
        var dependents = _services.Where(o => o.Service.Running && o.Service.DependsOn.Contains(s.Option.Service.Name, StringComparer.OrdinalIgnoreCase))
            .Select(o => o.Service.Name).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (dependents.Count == 0) return "stopped and set to not start";
        bool one = dependents.Count == 1;
        string named = one ? dependents[0] : string.Join(", ", dependents.Take(dependents.Count - 1)) + " and " + dependents[^1];
        return $"set to not start; stopped if {named}, which {(one ? "depends" : "depend")} on it, {(one ? "has" : "have")} stopped by then, " +
               "else it stops when Windows restarts";
    }

    private static string? Agent(string? reason) => reason is { Length: > 0 } r ? "Your agent: " + OneLine(r) : null;

    /// <summary>One line of plain text: control and formatting characters become spaces, at most 160 characters.</summary>
    internal static string OneLine(string s)
    {
        var sb = new StringBuilder(Math.Min(s.Length, 200));
        foreach (char ch in s)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            sb.Append(char.IsControl(ch) || cat is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator ? ' ' : ch);
            if (sb.Length > 200) break;
        }
        string line = sb.ToString().Trim();
        if (line.Length <= 160) return line;
        int end = 157;
        if (char.IsHighSurrogate(line[end - 1])) end--;
        return line[..end].TrimEnd() + "...";
    }
}

/// <summary>
/// The review of an imported AI agent answer, opened from the Services tab's "Import answer" (no approved design shows it;
/// it follows the Active changes window's): the title, a line that nothing has changed yet, then each part of
/// <see cref="AgentReview"/> as a list (what will be turned off, what PoolWatch refuses and why, what is left as it is),
/// and a bar with the green line that anything turned off can be put back, Cancel and the primary "Apply N changes". It
/// reads the file and this PC in the background when it opens. Cancel, Escape and the close box change nothing. Apply
/// asks again, by name, before it turns off services PoolWatch does not recognise, then makes the changes off the UI
/// thread (the window waits and cannot be closed meanwhile) and says what was done. Every word from the answer is shown
/// as plain text. The list is one control that draws its lines (<see cref="AgentReviewList"/>), so a long answer costs
/// time in proportion to its lines and never a window each. Created when opened and disposed when closed.
/// </summary>
internal sealed class AgentReviewForm : AgentWindow
{
    private readonly Func<AgentReview> _load;
    private readonly Func<AgentPlan, IReadOnlyCollection<string>, List<(string Name, string? Error)>> _apply;
    private readonly IAgentFiles _files;
    private readonly IAgentWork _work;
    private readonly Font _title, _bold, _small;
    private readonly List<Label> _wrapped = [];

    /// <param name="load">Reads the answer and this PC, off the UI thread (<see cref="AgentReview.Load"/>).</param>
    /// <param name="apply">Makes the changes, off the UI thread (<see cref="AgentApply.Run"/>): the plan and the services to turn off.</param>
    public AgentReviewForm(Func<AgentReview> load, Func<AgentPlan, IReadOnlyCollection<string>, List<(string Name, string? Error)>> apply, IAgentFiles files,
        IAgentWork? work = null, int? percent = null)
        : base(new Size(680, 560), new Size(560, 420), percent)
    {
        _load = load;
        _apply = apply;
        _files = files;
        _work = work ?? BackgroundWork.Instance;
        Text = AgentReview.Title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        _title = new Font("Segoe UI Semibold", Pt(14f));
        _bold = new Font("Segoe UI Semibold", Pt(9.5f));
        _small = new Font("Segoe UI", Pt(8.5f));

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = Px(24, 20, 24, 8), BackColor = SystemColors.Window };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // the title
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // the line under it
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // what runs now
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // the list
        Heading = Wrapped(AgentReview.Title, _title, InkColour, Px(0, 0, 0, 8));
        Lead = Wrapped("", null, MutedColour, Px(0, 0, 0, 10));
        Status = new Label { AutoSize = true, ForeColor = MutedColour, UseMnemonic = false, Margin = Px(0, 0, 0, 8), Visible = false };
        // The list scrolls up and down under a line that stays where it is.
        List = new AgentReviewList(_bold, _small, S) { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = SystemColors.Window };
        var frame = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(0, 1, 0, 0), BackColor = SystemColors.Window };
        frame.Controls.Add(List);
        frame.Paint += (_, e) => { using var pen = new Pen(LineColour); e.Graphics.DrawLine(pen, 0, 0, frame.Width, 0); };
        // Each in its own row: a hidden control is skipped when the table fills its cells in order.
        body.Controls.Add(Heading, 0, 0);
        body.Controls.Add(Lead, 0, 1);
        body.Controls.Add(Status, 0, 2);
        body.Controls.Add(frame, 0, 3);

        CancelChanges = BarButton("Cancel", ListTabStyle.Kind.Plain);
        ApplyButton = BarButton("Apply", ListTabStyle.Kind.Primary);
        CancelChanges.Click += (_, _) => Close();
        ApplyButton.Click += (_, _) => Apply();
        CancelButton = CancelChanges;
        Buttons = Row(CancelChanges, ApplyButton);
        Note = new GreenNote("Anything you turn off can be put back from Active changes.") { Margin = Px(0, 7, 12, 0) };
        var bar = Bar(Note, Buttons);

        Content.Controls.Add(body); // fill first, then the docked edge
        Content.Controls.Add(bar);
        List.SizeChanged += (_, _) => Rewrap();
        Display(null);
    }

    internal Label Heading { get; }
    internal Label Lead { get; }
    internal Label Status { get; }

    /// <summary>The review's list: each part's heading, its items and how many more it has.</summary>
    internal AgentReviewList List { get; }

    internal GreenNote Note { get; }
    internal FlowLayoutPanel Buttons { get; }
    internal Button CancelChanges { get; }
    internal Button ApplyButton { get; }

    /// <summary>The review shown; null while the answer is read.</summary>
    internal AgentReview? Review { get; private set; }

    /// <summary>Apply runs: the buttons wait and the window stays open.</summary>
    internal bool Busy { get; private set; }

    /// <summary>What Apply said it did, once it ran (the message shown).</summary>
    internal string? Result { get; private set; }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Start();
    }

    /// <summary>Reads the answer and this PC in the background, then shows the review (or why there is none).</summary>
    internal void Start() => _work.Run(this, _load, Display,
        ex => Display(AgentReview.Failed($"PoolWatch could not read what runs on this PC: {ex.Message.Trim().TrimEnd('.')}.")));

    /// <summary>Shows <paramref name="review"/>; null: still reading.</summary>
    internal void Display(AgentReview? review)
    {
        Review = review;
        Lead.Text = review?.Lead ?? "";
        Lead.Visible = review is not null;
        ShowStatus(review is null ? AgentReview.Reading : null);
        List.Show(AgentReviewList.Of(review?.Sections ?? []));
        ApplyButton.Text = review?.ApplyText ?? "Apply";
        ApplyButton.Visible = review is null || review.Error is null;
        CancelChanges.Text = review is { CanApply: false } ? "Close" : "Cancel";
        ShowButtons();
        Rewrap();
    }

    /// <summary>A label that wraps at the list's width (<see cref="Rewrap"/>), its text shown as it is.</summary>
    private Label Wrapped(string text, Font? font, Color colour, Padding margin)
    {
        var label = Words(text, font, colour, 1, margin);
        _wrapped.Add(label);
        return label;
    }

    /// <summary>
    /// The title, the line under it and the list's lines, as wide as the list less a scroll bar, whether one shows or not,
    /// so a scroll bar that comes or goes never wraps the lines again. The list measures its lines only when this width or
    /// its lines changed.
    /// </summary>
    private void Rewrap()
    {
        int width = Math.Max(S(120), List.Width - ScrollBar - S(4));
        foreach (var label in _wrapped)
            if (!label.IsDisposed && label.MaximumSize.Width != width) label.MaximumSize = new Size(width, 0);
        List.LayOut(width);
    }

    private void ShowStatus(string? status)
    {
        Status.Text = status ?? "";
        Status.Visible = status is not null;
    }

    private void ShowButtons()
    {
        ApplyButton.Enabled = !Busy && Review is { CanApply: true };
        CancelChanges.Enabled = !Busy;
    }

    /// <summary>
    /// Apply: services PoolWatch does not recognise are asked about first, by name (No leaves them out); then the
    /// changes run off the UI thread, and the window says what was done and closes.
    /// </summary>
    private void Apply()
    {
        if (Busy || Review is not { CanApply: true, Plan: { } plan } review) return;
        bool withUnknown = review.Unknown.Count == 0 || _files.AskYesNo(this, review.UnknownQuestion, "PoolWatch: services of other apps");
        var services = review.ServicesToApply(withUnknown);
        if (services.Count + plan.Startup.Count + plan.Tasks.Count == 0)
        {
            _files.Tell(this, AgentReview.NothingChanged, AgentReview.Title);
            return;
        }
        Busy = true;
        ShowStatus(AgentReview.Applying);
        ShowButtons();
        _work.Run(this, () => _apply(plan, services), results => Finish(AgentReview.ResultText(results)),
            ex => Finish($"PoolWatch could not apply the answer: {ex.Message.Trim().TrimEnd('.')}."));
    }

    private void Finish(string result)
    {
        Busy = false;
        Result = result;
        ShowStatus(null);
        ShowButtons();
        _files.Tell(this, result, "PoolWatch: AI agent answer");
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>The window stays open while Apply runs, so what it did is always said (Windows shutting down still closes it).</summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (Busy && e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        _title.Dispose();
        _bold.Dispose();
        _small.Dispose();
    }
}

/// <summary>
/// The review's list, drawn by one control that scrolls up and down: each part's heading in capitals, its items as the
/// design's list rows (the name in bold, what it is and what Apply does in grey, and the line more under it: the agent's
/// reason, or why PoolWatch refuses it; a line under each), and how many more the part has. It is one window whatever the
/// answer holds: each line's pieces of text are measured and placed once for the width they are laid out at
/// (<see cref="LayOut"/>), the line is as tall as they need, and drawing puts each piece where it was placed, only for
/// the lines in view, so a long answer costs time in proportion to its lines. Every word is drawn as plain text (an
/// ampersand is not a shortcut key), wrapped at the width. It is a tab stop that the arrow keys, Page Up, Page Down, Home
/// and End scroll, with a focus rectangle while it has the focus. A screen reader reads each heading, item and line whole.
/// </summary>
internal sealed class AgentReviewList : ScrollableControl
{
    /// <summary>How every word is measured and drawn: wrapped at words, as a text box wraps them, and never a shortcut key.</summary>
    internal const TextFormatFlags Flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;

    /// <summary>The line under each item, as the design's list rows.</summary>
    private static readonly Color RowLine = UiColors.RowLine;

    internal enum EntryKind { Heading, Item, More }

    /// <summary>A line of the list: a part's heading, one of its items (<see cref="Row"/>), or how many more the part has.</summary>
    internal sealed record Entry(EntryKind Kind, string Text, AgentReviewRow? Row = null);

    /// <summary>How a piece of text is coloured: an item's name (ink), a heading or what an item is (faint), an item's line more or how many more (muted).</summary>
    internal enum Tone { Ink, Faint, Muted }

    /// <summary>A piece of a line's text, in its font and tone, and the rectangle it is drawn in.</summary>
    internal readonly record struct Part(string Text, Font Font, Tone Tone, Rectangle Bounds);

    private readonly Font _bold, _small;
    private readonly int _headingAbove, _headingBelow, _itemPad, _noteGap, _moreAbove;
    private IReadOnlyList<Entry> _entries = [];
    /// <summary>Where each line is in the list's content (not scrolled), at <see cref="_width"/>.</summary>
    private Rectangle[] _at = [];
    /// <summary>Each line's pieces of text, placed in the list's content (not scrolled), at <see cref="_width"/>.</summary>
    private Part[][] _parts = [];
    private int _width = -1;
    private AccessibleObject?[] _spoken = [];

    /// <param name="bold">An item's name.</param>
    /// <param name="small">A part's heading (the list's own font draws the rest).</param>
    /// <param name="scale">Logical pixels in the window's.</param>
    public AgentReviewList(Font bold, Font small, Func<int, int> scale)
    {
        _bold = bold;
        _small = small;
        (_headingAbove, _headingBelow, _itemPad, _noteGap, _moreAbove) = (scale(12), scale(2), scale(8), scale(2), scale(8));
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw |
                 ControlStyles.Selectable, true);
        TabStop = true;
        AutoScroll = true;
        AccessibleRole = AccessibleRole.List;
    }

    /// <summary>The lines of <paramref name="sections"/>: each heading in capitals, its items, and how many more it has.</summary>
    internal static List<Entry> Of(IEnumerable<AgentReviewSection> sections)
    {
        var entries = new List<Entry>();
        foreach (var section in sections)
        {
            entries.Add(new(EntryKind.Heading, section.Heading.ToUpperInvariant()));
            entries.AddRange(section.Rows.Select(r => new Entry(EntryKind.Item, r.Name, r)));
            if (section.MoreText is { } more) entries.Add(new(EntryKind.More, more));
        }
        return entries;
    }

    /// <summary>The lines shown, in order.</summary>
    internal IReadOnlyList<Entry> Entries => _entries;

    /// <summary>
    /// Shows <paramref name="entries"/> from the top (the list has no content until they are measured at the next
    /// <see cref="LayOut"/>, which takes it back to its top).
    /// </summary>
    internal void Show(IReadOnlyList<Entry> entries)
    {
        _entries = entries;
        _at = [];
        _parts = [];
        _width = -1;
        _spoken = new AccessibleObject?[entries.Count];
        AutoScrollMinSize = Size.Empty;
        Invalidate();
        if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.Reorder, -1);
    }

    /// <summary>
    /// Places every line's pieces of text at <paramref name="width"/> (<see cref="PartsOf"/>) and puts each line under the
    /// one before, as tall as its pieces need, in one pass: nothing else lays them out, and drawing uses what this placed.
    /// Again only when the width or the lines changed.
    /// </summary>
    internal void LayOut(int width)
    {
        if (width == _width && _at.Length == _entries.Count) return;
        _width = width;
        var at = new Rectangle[_entries.Count];
        var parts = new Part[_entries.Count][];
        int y = 0;
        for (int i = 0; i < at.Length; i++)
        {
            var entry = _entries[i];
            y += entry.Kind switch { EntryKind.Heading => _headingAbove, EntryKind.More => _moreAbove, _ => 0 };
            parts[i] = PartsOf(entry, y, width);
            int bottom = parts[i][^1].Bounds.Bottom + (entry.Row is null ? 0 : _itemPad);
            at[i] = new Rectangle(0, y, width, bottom - y);
            y = bottom + (entry.Kind == EntryKind.Heading ? _headingBelow : 0);
        }
        _at = at;
        _parts = parts;
        AutoScrollMinSize = new Size(0, y); // up and down only
        Invalidate();
    }

    /// <summary>
    /// A line's pieces of text, each measured once at <paramref name="width"/> and placed under the one before from
    /// <paramref name="top"/>: a heading or how many more, alone; an item's name, what it is, and its line more when it has
    /// one, inside the row's padding.
    /// </summary>
    private Part[] PartsOf(Entry entry, int top, int width)
    {
        if (entry.Row is not { } row)
            return entry.Kind == EntryKind.Heading ? [Placed(entry.Text, _small, Tone.Faint, top, width)] : [Placed(entry.Text, Font, Tone.Muted, top, width)];
        var name = Placed(row.Name, _bold, Tone.Ink, top + _itemPad, width);
        var what = Placed(row.What, Font, Tone.Faint, name.Bounds.Bottom, width);
        return row.Note is { } note ? [name, what, Placed(note, Font, Tone.Muted, what.Bounds.Bottom + _noteGap, width)] : [name, what];
    }

    private static Part Placed(string text, Font font, Tone tone, int top, int width) =>
        new(text, font, tone, new Rectangle(0, top, width, Measure(text, font, width)));

    private static int Measure(string text, Font font, int width) =>
        TextRenderer.MeasureText(text.Length == 0 ? " " : text, font, new Size(Math.Max(1, width), int.MaxValue), Flags).Height;

    /// <summary>Where line <paramref name="index"/> is in the list's client area now, as scrolled.</summary>
    internal Rectangle EntryBounds(int index) => Scrolled(_at[index]);

    /// <summary>Line <paramref name="index"/>'s pieces of text, each where it is drawn in the list's client area now, as scrolled.</summary>
    internal IReadOnlyList<Part> Parts(int index) => _parts[index].Select(p => p with { Bounds = Scrolled(p.Bounds) }).ToList();

    private Rectangle Scrolled(Rectangle r)
    {
        r.Offset(0, AutoScrollPosition.Y);
        return r;
    }

    /// <summary>What a screen reader reads of a line: every word of it.</summary>
    internal static string Spoken(Entry entry) => entry.Row is { } r
        ? string.Join(". ", new[] { r.Name, r.What, r.Note }.Where(s => !string.IsNullOrEmpty(s)))
        : entry.Text;

    /// <summary>A new font (a DPI change, say) places every line again at the same width, so nothing is drawn in a font it was not measured in.</summary>
    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        if (_width < 0) return;
        int width = _width;
        _width = -1;
        LayOut(width);
    }

    /// <summary>The colour a piece of text in <paramref name="tone"/> is drawn in: the design's, or the system's with <paramref name="highContrast"/> on.</summary>
    internal static Color ColourOf(Tone tone, bool highContrast) => tone switch
    {
        Tone.Ink => highContrast ? SystemColors.WindowText : ListTabStyle.Ink,
        Tone.Faint => highContrast ? SystemColors.GrayText : ListTabStyle.Faint,
        _ => highContrast ? SystemColors.GrayText : ListTabStyle.Muted,
    };

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        bool contrast = SystemInformation.HighContrast;
        using var line = new Pen(contrast ? SystemColors.WindowText : RowLine);
        var g = e.Graphics;
        for (int i = 0; i < _at.Length; i++)
        {
            var r = EntryBounds(i);
            if (r.Bottom <= e.ClipRectangle.Top) continue;
            if (r.Top >= e.ClipRectangle.Bottom) break;
            foreach (var part in Parts(i))
                TextRenderer.DrawText(g, part.Text, part.Font, part.Bounds, ColourOf(part.Tone, contrast), Flags);
            if (_entries[i].Row is not null) g.DrawLine(line, r.Left, r.Bottom - 1, r.Right, r.Bottom - 1);
        }
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, ClientRectangle);
    }

    // ---------------------------------------------------------------- the keyboard

    /// <summary>The keys that scroll the list are the list's, not the window's (which would move the focus with the arrows).</summary>
    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Modifiers != Keys.None || ScrollTarget(e.KeyCode) is not { } y) return;
        AutoScrollPosition = new Point(0, Math.Max(0, y)); // the list keeps it within its content
        Invalidate();
        e.Handled = true;
    }

    /// <summary>
    /// How far down the list's content a key scrolls it: a line of text up or down with the arrows, a page less a line with
    /// Page Up and Page Down, the top with Home and the end with End; null for any other key.
    /// </summary>
    private int? ScrollTarget(Keys key)
    {
        int now = -AutoScrollPosition.Y, line = Font.Height, page = Math.Max(line, ClientSize.Height - line);
        return key switch
        {
            Keys.Up => now - line,
            Keys.Down => now + line,
            Keys.PageUp => now - page,
            Keys.PageDown => now + page,
            Keys.Home => 0,
            Keys.End => AutoScrollMinSize.Height,
            _ => null,
        };
    }

    // The focus rectangle is drawn at the edges of the client area, where scrolling would move it: the list is drawn again
    // whole when the focus comes or goes and whenever it scrolls.
    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void OnScroll(ScrollEventArgs se)
    {
        base.OnScroll(se);
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        Invalidate();
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new ListAccessible(this);

    /// <summary>The list as a screen reader has it: each line one of its items, read whole.</summary>
    private sealed class ListAccessible : ControlAccessibleObject
    {
        private readonly AgentReviewList _list;

        public ListAccessible(AgentReviewList list) : base(list) => _list = list;

        public override int GetChildCount() => _list._entries.Count;

        public override AccessibleObject? GetChild(int index) =>
            index < 0 || index >= _list._entries.Count ? null : _list._spoken[index] ??= new LineAccessible(_list, this, _list._entries, index);
    }

    /// <summary>One line of the list for a screen reader: its words, what it is, and where it is while it is still shown.</summary>
    private sealed class LineAccessible(AgentReviewList owner, AccessibleObject parent, IReadOnlyList<Entry> shown, int index) : AccessibleObject
    {
        private bool Placed => ReferenceEquals(owner._entries, shown) && index < owner._at.Length;

        public override string? Name => Spoken(shown[index]);

        public override AccessibleRole Role => shown[index].Row is null ? AccessibleRole.StaticText : AccessibleRole.ListItem;

        public override AccessibleObject? Parent => parent;

        public override AccessibleStates State =>
            AccessibleStates.ReadOnly | (Placed && owner.ClientRectangle.IntersectsWith(owner.EntryBounds(index)) ? AccessibleStates.None : AccessibleStates.Offscreen);

        public override Rectangle Bounds => Placed && owner.IsHandleCreated ? owner.RectangleToScreen(owner.EntryBounds(index)) : Rectangle.Empty;
    }
}
