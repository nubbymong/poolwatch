using System.Drawing.Drawing2D;
using PoolWatch.Core.Recommendations;

namespace PoolWatch.Ui;

/// <summary>
/// The body of the main window's Simple view (its default), under the header: four tiles (growth per hour, what the
/// clears freed, the net change, over the graph's window, and the memory in use), the one graph, and "Get more from
/// PoolWatch": up to three cards in a row, first an offer PoolWatch found reason for after setup (the token-leak guard
/// once the leak shows, the cache clear setup left for later, a report about an incident), then the features you may not
/// have found, each with Hide. Cards that do not fit beside the graph's least height (<see cref="MinGraph"/>) wait for a
/// larger window. The window owns the refresh and the cards' work (<see cref="ActionClicked"/>, <see cref="FeatureClicked"/>,
/// <see cref="HideClicked"/>, <see cref="FeatureHidden"/>); a refresh that changes nothing changes nothing on screen.
/// </summary>
internal sealed class SimpleView : UserControl
{
    public const int MaxCards = 3;
    /// <summary>The graph's least height (logical pixels): cards that do not fit above it wait for a larger window.</summary>
    public const int MinGraph = 120;
    public const string Heading = "GET MORE FROM POOLWATCH";
    /// <summary>A button's answer stays this long, also after its card has gone (the change it made usually takes the card away).</summary>
    private static readonly TimeSpan AnswerFor = TimeSpan.FromMinutes(1);
    private readonly StatTiles _tiles = new() { Dock = DockStyle.Top };
    private readonly Panel _chartHost = new() { Dock = DockStyle.Fill };
    private readonly Panel _cardsHost = new() { Dock = DockStyle.Bottom, Visible = false };
    private readonly Label _heading = new() { Text = Heading, AutoSize = true, UseMnemonic = false };
    private readonly WrapLabel _answer = new() { Visible = false };
    private readonly List<Card> _cards = [];
    /// <summary>Cards taken off at the last rebuild, disposed at the next: a rebuild may run inside one of their own clicks.</summary>
    private readonly List<Card> _retired = [];
    private readonly Font _headingFont = new("Segoe UI Semibold", 8.25f), _titleFont = new("Segoe UI Semibold", 9.5f), _textFont = new("Segoe UI", 9f);
    private readonly Func<DateTime> _utcNow;
    private string _shown = "";
    /// <summary>How many of the cards fit (the first ones: they come by priority).</summary>
    private int _fitting;
    /// <summary>The offer whose answer stays, and until when.</summary>
    private (string Key, DateTime UntilUtc)? _answered;

    /// <param name="utcNow">The clock the answers stay by (tests set it).</param>
    public SimpleView(Func<DateTime>? utcNow = null)
    {
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        BackColor = SystemColors.Window;
        Padding = Px(16, 12, 16, 10);
        _heading.Font = _headingFont;
        _heading.ForeColor = UiColors.Secondary;
        _tiles.Margin = Padding.Empty;
        _chartHost.Padding = Px(0, 10, 0, 0);
        _cardsHost.Controls.Add(_heading);
        _cardsHost.Controls.Add(_answer);
        Controls.Add(_chartHost); // fill first, then the docked edges
        Controls.Add(_tiles);
        Controls.Add(_cardsHost);
    }

    /// <summary>An offer's button was pressed (it is disabled until <see cref="ShowOutcome"/> or the next list).</summary>
    public event Action<Recommendation>? ActionClicked;

    /// <summary>"Hide" on an offer: the window hides its key for 30 days and shows the list again.</summary>
    public event Action<Recommendation>? HideClicked;

    /// <summary>A feature card's link: the window opens what it names.</summary>
    public event Action<DiscoveryCard>? FeatureClicked;

    /// <summary>"Hide" on a feature card: the window remembers it for good and shows the list again.</summary>
    public event Action<DiscoveryCard>? FeatureHidden;

    /// <summary>Puts the window's one graph here (taking it from the Advanced view's layout).</summary>
    public void SetChart(Control chart)
    {
        chart.Dock = DockStyle.Fill;
        chart.Margin = Padding.Empty;
        _chartHost.Controls.Add(chart);
    }

    /// <summary>The four tiles; repainted only when they changed (and laid out again when they need another row).</summary>
    public void ShowTiles(IReadOnlyList<Tile> tiles)
    {
        _tiles.Show(tiles);
        if (_tiles.Height != _tiles.HeightFor(TilesWidth)) PerformLayout();
    }

    private int TilesWidth => Math.Max(1, ClientSize.Width - Padding.Horizontal);

    /// <summary>
    /// The tiles take the height their width needs (one row, or two), before the docked layout places the rest; when that
    /// changed (longer words after a read, a narrower window), the cards are placed again in the room left above the graph.
    /// </summary>
    protected override void OnLayout(LayoutEventArgs e)
    {
        int h = _tiles.HeightFor(TilesWidth);
        bool changed = _tiles.Height != h;
        if (changed) _tiles.Height = h;
        base.OnLayout(e);
        if (!changed) return;
        Arrange();
        base.OnLayout(e); // inside a layout, the cards' new height lays nothing out by itself: dock the parts again
    }

    /// <summary>The tiles (tests, CI).</summary>
    internal StatTiles Tiles => _tiles;

    /// <summary>
    /// The cards: the offers first (by priority), then the feature cards not hidden, <see cref="MaxCards"/> at most. They
    /// are rebuilt only when what they say changed.
    /// </summary>
    public void ShowCards(IReadOnlyList<Recommendation> offers, IReadOnlyList<DiscoveryCard> features)
    {
        bool answerEnds = _answered is { } answered && _utcNow() >= answered.UntilUtc;
        if (answerEnds) _answered = null;
        var items = offers.Select(CardItem.Of).Concat(features.Select(CardItem.Of)).Take(MaxCards).ToList();
        string key = string.Join("\n", items.Select(i => $"{i.Key}\t{i.Title}\t{i.Text}\t{i.Action}"));
        if (key == _shown)
        {
            if (answerEnds) Arrange();
            return;
        }
        _shown = key;
        SuspendLayout();
        foreach (var card in _retired) card.Dispose();
        _retired.Clear();
        foreach (var card in _cards) _cardsHost.Controls.Remove(card);
        _retired.AddRange(_cards);
        _cards.Clear();
        foreach (var item in items)
        {
            var card = new Card(item, this);
            void Act()
            {
                if (item.Offer is { } offer)
                {
                    card.Action.Enabled = false;
                    card.ShowMessage("", ok: true);
                    ActionClicked?.Invoke(offer);
                }
                else if (item.Feature is { } feature) FeatureClicked?.Invoke(feature);
            }
            if (card.Action is LinkLabel link) link.LinkClicked += (_, _) => Act(); // a click or the keyboard on the link
            else card.Action.Click += (_, _) => Act();
            card.HideLink.LinkClicked += (_, _) =>
            {
                if (item.Offer is { } offer) HideClicked?.Invoke(offer);
                else if (item.Feature is { } feature) FeatureHidden?.Invoke(feature);
            };
            _cards.Add(card);
            _cardsHost.Controls.Add(card);
        }
        ResumeLayout(true);
        Arrange();
    }

    /// <summary>
    /// After an offer's button ran: its button is enabled again, and the answer (an error in red) shows on its card. The
    /// answer also stays for <see cref="AnswerFor"/> above the cards after its card has gone, since the change usually
    /// takes the card away, even one that went only part of the way.
    /// </summary>
    public void ShowOutcome(Recommendation r, bool ok, string message)
    {
        if (message.Length > 0)
        {
            _answer.Text = message;
            _answer.ForeColor = ok ? UiColors.Good : Color.Firebrick;
            _answered = (r.Key, _utcNow() + AnswerFor);
        }
        if (_cards.FirstOrDefault(c => c.Item.Key == r.Key) is { } card)
        {
            card.Action.Enabled = true;
            card.ShowMessage(message, ok);
        }
        Arrange();
    }

    /// <summary>The cards shown now: the first ones, as many as fit.</summary>
    internal IReadOnlyList<CardItem> ShownCards => _cards.Take(_fitting).Select(c => c.Item).ToList();

    /// <summary>The answer that stays after its card went, or null.</summary>
    internal string? ShownAnswer => AnswerShown ? _answer.Text : null;

    /// <summary>The answer shows above the cards once its card has gone: while the card is still listed, the card shows it.</summary>
    private bool AnswerShown => _answered is { } a && !_cards.Any(c => c.Item.Key == a.Key);

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        Arrange();
    }

    /// <summary>
    /// The cards under the graph in one row, each as wide as the row allows for as many cards as fit: three when they fit
    /// above the graph's least height, else two wider (and lower) ones, else one. The answer that stays is above them.
    /// </summary>
    private void Arrange()
    {
        int width = Math.Max(S(200), ClientSize.Width - Padding.Horizontal), gap = S(12);
        int room = ClientSize.Height - Padding.Vertical - _tiles.Height - _chartHost.Padding.Vertical - S(MinGraph);
        _heading.Location = new Point(0, S(12));
        int y = _heading.Bottom + S(6);
        bool answer = AnswerShown;
        _answer.Visible = answer;
        if (answer)
        {
            _answer.SetBounds(0, y, width, 1); // a WrapLabel sets its own height from its width
            y = _answer.Bottom + S(4);
        }
        _fitting = 0;
        int rowHeight = 0;
        for (int n = _cards.Count; n >= 1; n--)
        {
            int w = (width - (n - 1) * gap) / n;
            int h = _cards.Take(n).Max(c => c.Arrange(w));
            if (y + h > room) continue;
            _fitting = n;
            rowHeight = h;
            for (int i = 0; i < n; i++) _cards[i].SetBounds(i * (w + gap), y, w, h);
            break;
        }
        for (int i = 0; i < _cards.Count; i++) _cards[i].Visible = i < _fitting;
        _cardsHost.Visible = _fitting > 0 || answer;
        int total = _fitting > 0 ? y + rowHeight : answer ? _answer.Bottom : 0;
        if (_cardsHost.Height != total) _cardsHost.Height = total;
    }

    /// <summary>CI: the tiles, each shown card's title, words and its one action, the answer that stays, and anything clipped or too small.</summary>
    internal object SmokeStats(string nextClear) => new
    {
        nextClear,
        tiles = _tiles.Texts,
        cards = _cards.Take(_fitting).Select(c => new { title = c.Item.Title, why = c.Item.Text, buttons = new[] { c.Item.Action } }).ToList(),
        answer = ShownAnswer,
        layoutProblems = LayoutProblems(),
    };

    /// <summary>
    /// Texts that do not fit, a control outside its card or a card outside the view, a card without exactly one action, a
    /// graph squeezed too small. Judged from the layout itself, so it holds before the window is shown.
    /// </summary>
    internal List<string> LayoutProblems()
    {
        var problems = new List<string>();
        problems.AddRange(_tiles.Problems());
        if (AnswerShown && _answer.NeededHeight > _answer.Height + 1) problems.Add($"the answer needs {_answer.NeededHeight} px, has {_answer.Height}");
        foreach (var card in _cards.Take(_fitting))
        {
            string name = $"the card \"{card.Item.Title}\"";
            if (!_cardsHost.ClientRectangle.Contains(card.Bounds)) problems.Add($"{name} at {card.Bounds} is outside the cards ({_cardsHost.ClientSize})");
            problems.AddRange(card.Problems().Select(p => $"{name}: {p}"));
        }
        if ((_fitting > 0 || AnswerShown) && _cardsHost.Bottom > ClientSize.Height - Padding.Bottom + 1) problems.Add("the cards run off the bottom of the window");
        if (_chartHost.Controls.Count > 0 && _chartHost.Height - _chartHost.Padding.Vertical < S(MinGraph)) problems.Add($"the graph is only {_chartHost.Height} px high");
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
            _headingFont.Dispose();
            _titleFont.Dispose();
            _textFont.Dispose();
        }
    }

    /// <summary>
    /// One card: an icon and its title, its words (an offer of the token-leak guard also says what you will notice and how
    /// to undo it; one that clears the cache, that a clear can stutter), the answer after its button, then its one action
    /// (a button for an offer, a link for a feature) and Hide. It draws its own thin border.
    /// </summary>
    private sealed class Card : Panel
    {
        private readonly SimpleView _view;
        private readonly WrapLabel _title = new(), _text = new(), _details = new(), _message = new() { Visible = false };

        public Card(CardItem item, SimpleView view)
        {
            Item = item;
            _view = view;
            BackColor = SystemColors.Window;
            DoubleBuffered = true;
            _title.Font = view._titleFont;
            _title.ForeColor = UiColors.Text;
            _title.Text = item.Title;
            _text.Font = _details.Font = _message.Font = view._textFont;
            _text.ForeColor = _details.ForeColor = UiColors.Secondary;
            _text.Text = item.Text;
            _details.Text = item.Details;
            _details.Visible = item.Details.Length > 0;
            Action = item.IsButton
                ? new Button { Text = item.Action, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, UseVisualStyleBackColor = true }
                : new LinkLabel { Text = item.Action, AutoSize = true, LinkBehavior = LinkBehavior.HoverUnderline, UseMnemonic = false, LinkColor = UiColors.Accent };
            HideLink = new LinkLabel { Text = "Hide", AutoSize = true, LinkBehavior = LinkBehavior.HoverUnderline, LinkColor = UiColors.Faint, ActiveLinkColor = UiColors.Muted };
            Controls.AddRange([_title, _text, _details, _message, Action, HideLink]);
        }

        public CardItem Item { get; }
        /// <summary>The card's one action: a button (an offer) or a link (a feature card).</summary>
        public Control Action { get; }
        public LinkLabel HideLink { get; }

        /// <summary>A text that does not fit, or a control outside the card.</summary>
        public IEnumerable<string> Problems()
        {
            foreach (var label in Shown().Where(l => l.NeededHeight > l.Height + 1)) yield return $"\"{label.Text}\" needs {label.NeededHeight} px, has {label.Height}";
            foreach (Control c in new Control[] { Action, HideLink })
                if (c.GetPreferredSize(Size.Empty) is var need && (need.Width > c.Width + 1 || need.Height > c.Height + 1)) yield return $"\"{c.Text}\" needs {need}, has {c.Size}";
            foreach (var c in Shown().Concat<Control>([Action, HideLink]).Where(c => !ClientRectangle.Contains(c.Bounds)))
                yield return $"\"{c.Text}\" at {c.Bounds} is outside it ({ClientSize})";
            if (Action.Bounds.IntersectsWith(HideLink.Bounds)) yield return $"\"{Action.Text}\" and Hide overlap";
        }

        /// <summary>The texts the card shows (the details and the answer only when they have one).</summary>
        private IEnumerable<WrapLabel> Shown() => new[] { _title, _text, _details, _message }.Where(l => l.Text.Length > 0);

        public void ShowMessage(string text, bool ok)
        {
            _message.Text = text;
            _message.ForeColor = ok ? UiColors.Secondary : Color.Firebrick;
            _message.Visible = text.Length > 0;
        }

        /// <summary>Lays the card out at <paramref name="width"/> and returns its height: the icon and title, the texts, then the action on the left and Hide on the right.</summary>
        public int Arrange(int width)
        {
            int pad = _view.S(14), icon = _view.S(18), gap = _view.S(4);
            int textWidth = Math.Max(_view.S(80), width - 2 * pad);
            _title.SetBounds(pad + icon + _view.S(9), pad, Math.Max(_view.S(60), textWidth - icon - _view.S(9)), 1); // its own height follows from the width
            int y = Math.Max(_title.Bottom, pad + icon) + gap;
            foreach (var label in new[] { _text, _details, _message }.Where(l => l.Text.Length > 0))
            {
                label.SetBounds(pad, y, textWidth, 1);
                y = label.Bottom + gap;
            }
            var action = Action.GetPreferredSize(Size.Empty);
            var hide = HideLink.GetPreferredSize(Size.Empty);
            int row = Math.Max(action.Height, hide.Height);
            Action.SetBounds(pad, y + (row - action.Height) / 2, action.Width, action.Height);
            HideLink.SetBounds(width - pad - hide.Width, y + (row - hide.Height) / 2, hide.Width, hide.Height);
            return y + row + pad;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var border = new Pen(UiColors.Rule)) g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
            int pad = _view.S(14), size = _view.S(18);
            // Beside the title's first line.
            CardIcons.Draw(g, Item.Icon, new Rectangle(pad, pad + Math.Max(0, (_view._titleFont.Height - size) / 2), size, size));
        }
    }
}

/// <summary>Which small outline icon a card shows.</summary>
internal enum CardIcon { Causes, Services, Mark, Shield, Clear, Report }

/// <summary>One card in "Get more from PoolWatch": an offer (a <see cref="Recommendation"/>) or a feature (a <see cref="DiscoveryCard"/>).</summary>
internal sealed record CardItem(string Key, string Title, string Text, string Action, bool IsButton, CardIcon Icon)
{
    /// <summary>Under the words: what the token-leak guard does to you and how to undo it, or that a clear can stutter.</summary>
    public string Details { get; init; } = "";
    public Recommendation? Offer { get; init; }
    public DiscoveryCard? Feature { get; init; }

    public static CardItem Of(Recommendation r)
    {
        bool guard = r.Id == RecommendationId.TokenLeakGuard;
        var icon = r.Id switch
        {
            RecommendationId.TokenLeakGuard => CardIcon.Shield,
            RecommendationId.HourlyCacheClear or RecommendationId.ReplaceClearTask => CardIcon.Clear,
            RecommendationId.ReportIncident => CardIcon.Report,
            RecommendationId.ReviewServices => CardIcon.Services,
            _ => CardIcon.Causes,
        };
        return new CardItem(r.Key, r.Title, guard ? TokenGuardWords.CardWhy(r.Why) : r.Why, r.Button, IsButton: true, icon)
        {
            Details = guard ? TokenGuardWords.Details : SimpleSettings.RunsAClear(r) ? SimpleSettings.ClearStutter : "",
            Offer = r,
        };
    }

    public static CardItem Of(DiscoveryCard c) => new(c.Key, c.Title, c.Text, c.Link, IsButton: false,
        c.Id switch { DiscoveryId.Services => CardIcon.Services, DiscoveryId.Mark => CardIcon.Mark, _ => CardIcon.Causes }) { Feature = c };
}

/// <summary>The cards' small outline icons, drawn (no image files): as the design draws them, in the text's grey.</summary>
internal static class CardIcons
{
    public static void Draw(Graphics g, CardIcon icon, Rectangle r)
    {
        bool hc = SystemInformation.HighContrast;
        var color = hc ? SystemColors.WindowText : icon == CardIcon.Shield ? UiColors.Good : UiColors.Icon;
        using var pen = new Pen(color, Math.Max(1.4f, r.Width / 12f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        float x = r.Left, y = r.Top, s = r.Width / 20f;
        PointF P(float px, float py) => new(x + px * s, y + py * s);
        switch (icon)
        {
            case CardIcon.Causes: // a magnifier over a rising line
                g.DrawEllipse(pen, x + 3.5f * s, y + 3.5f * s, 10 * s, 10 * s);
                g.DrawLine(pen, P(12.5f, 12.5f), P(17, 17));
                g.DrawLines(pen, [P(6.5f, 9.5f), P(8, 7.5f), P(9.5f, 9), P(11, 6.5f)]);
                break;
            case CardIcon.Services: // a four-point spark
                g.DrawPolygon(pen, [P(10, 2.5f), P(11.7f, 7.1f), P(16.5f, 9), P(11.7f, 10.9f), P(10, 15.5f), P(8.3f, 10.9f), P(3.5f, 9), P(8.3f, 7.1f)]);
                break;
            case CardIcon.Mark: // a flag
                g.DrawLine(pen, P(5, 17), P(5, 3.5f));
                g.DrawLines(pen, [P(5, 4), P(14, 4), P(12, 7), P(14, 10), P(5, 10)]);
                break;
            case CardIcon.Shield: // a shield with a tick
                g.DrawPolygon(pen, [P(10, 2.5f), P(16, 4.8f), P(16, 9.5f), P(13, 15), P(10, 17.5f), P(7, 15), P(4, 9.5f), P(4, 4.8f)]);
                g.DrawLines(pen, [P(7, 10), P(9.1f, 12.1f), P(13.2f, 8)]);
                break;
            case CardIcon.Clear: // bars going down, and an arrow
                g.DrawLine(pen, P(3, 15), P(17, 15));
                g.DrawLine(pen, P(5, 15), P(5, 9));
                g.DrawLine(pen, P(9, 15), P(9, 5));
                g.DrawLine(pen, P(13, 15), P(13, 11));
                g.DrawLines(pen, [P(15, 4), P(17, 6), P(15, 8)]);
                break;
            default: // a speech bubble: a report
                g.DrawPolygon(pen, [P(3, 4.5f), P(17, 4.5f), P(17, 13.5f), P(9, 13.5f), P(5, 16.5f), P(5, 13.5f), P(3, 13.5f)]);
                break;
        }
    }
}
