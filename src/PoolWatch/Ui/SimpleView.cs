using PoolWatch.Core.Recommendations;

namespace PoolWatch.Ui;

/// <summary>
/// The body of the main window's Simple view (its default): the line about clearing ("Next cache clear in 23 min. Last
/// clear at 09:12 freed 4.7 GB."), the one graph, and up to 3 suggestions, each a card with one button and Hide (as many as
/// fit while the graph keeps <see cref="MinGraph"/>). The window owns the refresh (at most once a minute while it is
/// visible) and the buttons' work (<see cref="ActionClicked"/>, <see cref="HideClicked"/>); a refresh that changes nothing
/// changes nothing on screen, and the texts draw themselves, so nothing flashes.
/// </summary>
internal sealed class SimpleView : UserControl
{
    public const int MaxCards = 3;
    /// <summary>The graph's least height (logical pixels): cards that do not fit above it wait for a larger window.</summary>
    public const int MinGraph = 120;
    /// <summary>A button's answer stays this long, also after its card has gone (the change it made usually takes the card away).</summary>
    private static readonly TimeSpan AnswerFor = TimeSpan.FromMinutes(1);
    private static readonly Color Worked = Color.FromArgb(0, 110, 50); // the setup guide's "Done" green
    private readonly WrapLabel _next = new() { Dock = DockStyle.Top };
    private readonly Panel _chartHost = new() { Dock = DockStyle.Fill };
    private readonly Panel _suggestions = new() { Dock = DockStyle.Bottom, Visible = false };
    private readonly WrapLabel _heading = new() { Text = "Suggestions" };
    private readonly WrapLabel _answer = new() { Visible = false };
    private readonly List<Card> _cards = [];
    /// <summary>Cards taken off at the last rebuild, disposed at the next: a rebuild may run inside one of their own clicks.</summary>
    private readonly List<Card> _retired = [];
    private readonly Font _nextFont = new("Segoe UI", 10f), _headingFont = new("Segoe UI Semibold", 10f), _titleFont = new("Segoe UI Semibold", 9.5f);
    private readonly Func<DateTime> _utcNow;
    private string _shown = "";
    /// <summary>How many of the cards fit (the first ones: they come by priority).</summary>
    private int _fitting;
    /// <summary>The suggestion whose answer stays, and until when.</summary>
    private (string Key, DateTime UntilUtc)? _answered;

    /// <param name="utcNow">The clock the answers stay by (tests set it).</param>
    public SimpleView(Func<DateTime>? utcNow = null)
    {
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        BackColor = SystemColors.Window;
        Padding = Px(12, 2, 12, 12);
        _next.Font = _nextFont;
        _next.Padding = Px(2, 0, 2, 8);
        _heading.Font = _headingFont;
        _suggestions.Controls.Add(_heading);
        _suggestions.Controls.Add(_answer);
        Controls.Add(_chartHost); // fill first, then the docked edges
        Controls.Add(_suggestions);
        Controls.Add(_next);
    }

    /// <summary>A suggestion's button was pressed (it is disabled until <see cref="ShowOutcome"/> or the next list).</summary>
    public event Action<Recommendation>? ActionClicked;

    /// <summary>"Hide" was pressed: the window hides the suggestion's key for 30 days and shows the list again.</summary>
    public event Action<Recommendation>? HideClicked;

    /// <summary>Puts the window's one graph here (taking it from the Advanced view's layout).</summary>
    public void SetChart(Control chart)
    {
        chart.Dock = DockStyle.Fill;
        chart.Margin = Padding.Empty;
        _chartHost.Controls.Add(chart);
    }

    /// <summary>The line about clearing; set only when it changed.</summary>
    public void ShowNextClear(string text)
    {
        if (_next.Text == text) return;
        int was = _next.Height;
        _next.Text = text;
        if (_next.Height != was) ArrangeSuggestions(); // a line that wraps differently leaves the cards a different room
    }

    /// <summary>The first <see cref="MaxCards"/> suggestions; the cards are rebuilt only when what they say changed.</summary>
    public void ShowRecommendations(IReadOnlyList<Recommendation> recs)
    {
        bool answerEnds = _answered is { } answered && _utcNow() >= answered.UntilUtc;
        if (answerEnds) _answered = null;
        var shown = recs.Take(MaxCards).ToList();
        string key = string.Join("\n", shown.Select(r => $"{r.Key}\t{r.Title}\t{r.Why}\t{r.Button}"));
        if (key == _shown)
        {
            if (answerEnds) ArrangeSuggestions();
            return;
        }
        _shown = key;
        SuspendLayout();
        foreach (var card in _retired) card.Dispose();
        _retired.Clear();
        foreach (var card in _cards) _suggestions.Controls.Remove(card);
        _retired.AddRange(_cards);
        _cards.Clear();
        foreach (var r in shown)
        {
            var card = new Card(r, _titleFont, this);
            card.RunButton.Click += (_, _) =>
            {
                card.RunButton.Enabled = false;
                card.ShowMessage("", ok: true);
                ActionClicked?.Invoke(r);
            };
            card.HideLink.LinkClicked += (_, _) => HideClicked?.Invoke(r);
            _cards.Add(card);
            _suggestions.Controls.Add(card);
        }
        ResumeLayout(true);
        ArrangeSuggestions();
    }

    /// <summary>
    /// After a suggestion's button ran: its button is enabled again, and the answer (an error in red) shows on its card. The
    /// answer also stays for <see cref="AnswerFor"/> under Suggestions after its card has gone, since the change usually
    /// takes the card away, even one that went only part of the way.
    /// </summary>
    public void ShowOutcome(Recommendation r, bool ok, string message)
    {
        if (message.Length > 0)
        {
            _answer.Text = message;
            _answer.ForeColor = ok ? Worked : Color.Firebrick;
            _answered = (r.Key, _utcNow() + AnswerFor);
        }
        if (_cards.FirstOrDefault(c => c.Recommendation.Key == r.Key) is { } card)
        {
            card.RunButton.Enabled = true;
            card.ShowMessage(message, ok);
        }
        ArrangeSuggestions();
    }

    /// <summary>The suggestions shown now: the first ones, as many as fit.</summary>
    internal IReadOnlyList<Recommendation> ShownCards => _cards.Take(_fitting).Select(c => c.Recommendation).ToList();

    /// <summary>The answer that stays after its card went, or null.</summary>
    internal string? ShownAnswer => AnswerShown ? _answer.Text : null;

    /// <summary>The answer shows under Suggestions once its card has gone: while the card is still listed, the card shows it.</summary>
    private bool AnswerShown => _answered is { } a && !_cards.Any(c => c.Recommendation.Key == a.Key);

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        ArrangeSuggestions();
    }

    /// <summary>
    /// The suggestions under the graph, each card as high as its wrapped texts at the current width: the answer that stays,
    /// then the cards in order while the graph keeps <see cref="MinGraph"/>. Those left out show again at a larger size.
    /// </summary>
    private void ArrangeSuggestions()
    {
        int width = Math.Max(S(200), ClientSize.Width - Padding.Horizontal), gap = S(8);
        int room = ClientSize.Height - Padding.Vertical - _next.Height - S(MinGraph);
        _heading.SetBounds(0, S(6), width, 1); // a WrapLabel sets its own height from its width
        int y = _heading.Bottom + S(4);
        bool answer = AnswerShown;
        _answer.Visible = answer;
        if (answer)
        {
            _answer.SetBounds(0, y, width, 1);
            y = _answer.Bottom + gap;
        }
        _fitting = 0;
        for (int i = 0; i < _cards.Count; i++)
        {
            int height = _cards[i].Arrange(width);
            bool fits = _fitting == i && y + height <= room; // in order: once one is left out, so are the ones after it
            _cards[i].Visible = fits;
            if (!fits) continue;
            _cards[i].SetBounds(0, y, width, height);
            _fitting++;
            y = _cards[i].Bottom + gap;
        }
        _suggestions.Visible = _fitting > 0 || answer;
        int total = y - gap;
        if (_suggestions.Height != total) _suggestions.Height = total;
    }

    /// <summary>CI: the line about clearing, each shown card's title, reason and buttons, the answer that stays, and anything clipped or too small.</summary>
    internal object SmokeStats() => new
    {
        nextClear = _next.Text,
        cards = _cards.Take(_fitting).Select(c => new
        {
            title = c.Recommendation.Title, why = c.Recommendation.Why, buttons = c.Controls.OfType<Button>().Select(b => b.Text).ToList(),
        }).ToList(),
        answer = ShownAnswer,
        layoutProblems = LayoutProblems(),
    };

    /// <summary>
    /// Texts that do not fit, a control outside its card or a card outside the view, a card without exactly one button, a
    /// graph squeezed too small. Judged from the layout itself, so it holds before the window is shown.
    /// </summary>
    internal List<string> LayoutProblems()
    {
        var problems = new List<string>();
        if (_next.NeededHeight > _next.Height + 1) problems.Add($"the line about clearing needs {_next.NeededHeight} px, has {_next.Height}");
        if (AnswerShown && _answer.NeededHeight > _answer.Height + 1) problems.Add($"the answer needs {_answer.NeededHeight} px, has {_answer.Height}");
        foreach (var card in _cards.Take(_fitting))
        {
            string name = $"the card \"{card.Recommendation.Title}\"";
            if (card.Controls.OfType<Button>().Count() != 1) problems.Add($"{name} has {card.Controls.OfType<Button>().Count()} buttons");
            if (!_suggestions.ClientRectangle.Contains(card.Bounds)) problems.Add($"{name} at {card.Bounds} is outside the suggestions ({_suggestions.ClientSize})");
            problems.AddRange(card.Problems().Select(p => $"{name}: {p}"));
        }
        if ((_fitting > 0 || AnswerShown) && _suggestions.Bottom > ClientSize.Height - Padding.Bottom + 1) problems.Add("the suggestions run off the bottom of the window");
        if (_chartHost.Controls.Count > 0 && _chartHost.Height < S(MinGraph)) problems.Add($"the graph is only {_chartHost.Height} px high");
        return problems;
    }

    private int S(int logical) => LogicalToDeviceUnits(logical);
    private Padding Px(int l, int t, int r, int b) => new(S(l), S(t), S(r), S(b));

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            foreach (var card in _retired) card.Dispose();
            _nextFont.Dispose();
            _headingFont.Dispose();
            _titleFont.Dispose();
        }
    }

    /// <summary>One suggestion: its title, why, what you will notice (the token-leak guard), the answer after its button, then the button and Hide.</summary>
    private sealed class Card : Panel
    {
        private readonly SimpleView _view;
        private readonly WrapLabel _title = new(), _why = new(), _details = new() { ForeColor = SystemColors.GrayText }, _message = new() { Visible = false };

        public Card(Recommendation r, Font titleFont, SimpleView view)
        {
            Recommendation = r;
            _view = view;
            BackColor = Color.FromArgb(244, 247, 250);
            _title.Text = r.Title;
            _title.Font = titleFont;
            bool guard = r.Id == RecommendationId.TokenLeakGuard;
            _why.Text = guard ? TokenGuardWords.CardWhy(r.Why) : r.Why;
            // What you will notice: the token-leak guard's trade-off, or that a clear can make video or the mouse stutter.
            _details.Text = guard ? TokenGuardWords.Details : SimpleSettings.RunsAClear(r) ? SimpleSettings.ClearStutter : "";
            _details.Visible = _details.Text.Length > 0;
            RunButton = new Button { Text = r.Button, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, UseVisualStyleBackColor = true };
            HideLink = new LinkLabel { Text = "Hide", AutoSize = true, LinkBehavior = LinkBehavior.HoverUnderline };
            Controls.AddRange([_title, _why, _details, _message, RunButton, HideLink]);
        }

        public Recommendation Recommendation { get; }
        public Button RunButton { get; }
        public LinkLabel HideLink { get; }

        /// <summary>A text that does not fit, or a control outside the card.</summary>
        public IEnumerable<string> Problems()
        {
            foreach (var label in Shown().Where(l => l.NeededHeight > l.Height + 1)) yield return $"\"{label.Text}\" needs {label.NeededHeight} px, has {label.Height}";
            foreach (Control c in new Control[] { RunButton, HideLink })
                if (c.GetPreferredSize(Size.Empty) is var need && (need.Width > c.Width + 1 || need.Height > c.Height + 1)) yield return $"\"{c.Text}\" needs {need}, has {c.Size}";
            foreach (var c in Shown().Concat<Control>([RunButton, HideLink]).Where(c => !ClientRectangle.Contains(c.Bounds)))
                yield return $"\"{c.Text}\" at {c.Bounds} is outside it ({ClientSize})";
        }

        /// <summary>The texts the card shows (the details and the answer only when they have one).</summary>
        private IEnumerable<WrapLabel> Shown() => new[] { _title, _why, _details, _message }.Where(l => l.Text.Length > 0);

        public void ShowMessage(string text, bool ok)
        {
            _message.Text = text;
            _message.ForeColor = ok ? SystemColors.GrayText : Color.Firebrick;
            _message.Visible = text.Length > 0;
        }

        /// <summary>Lays the card out at <paramref name="width"/> and returns its height: texts on the left, the button and Hide on the right.</summary>
        public int Arrange(int width)
        {
            int pad = _view.S(10);
            var button = RunButton.GetPreferredSize(Size.Empty);
            var hide = HideLink.GetPreferredSize(Size.Empty);
            int right = Math.Max(button.Width, hide.Width);
            int textWidth = Math.Max(_view.S(120), width - 2 * pad - right - _view.S(16));
            int y = pad;
            foreach (var label in Shown())
            {
                label.SetBounds(pad, y, textWidth, 1); // its own height follows from the width
                y = label.Bottom + _view.S(2);
            }
            RunButton.SetBounds(width - pad - button.Width, pad, button.Width, button.Height);
            HideLink.SetBounds(width - pad - hide.Width, RunButton.Bottom + _view.S(4), hide.Width, hide.Height);
            return Math.Max(y, HideLink.Bottom) + pad;
        }
    }
}
