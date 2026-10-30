using PoolWatch.Core.Impact;

namespace PoolWatch.Ui.Setup;

/// <summary>
/// What a page needs around it: the session, the scale and fonts, the app's icon, the window to put dialogs on, and the
/// clock (the Ready step's picture shows the time it opened; the tests give a fixed one).
/// </summary>
internal sealed record PageContext(SetupSession Session, SetupScale Scale, SetupFonts Fonts, Icon AppIcon, Func<IWin32Window> Owner, Action<string> OpenWebPage,
    Func<DateTime>? Clock = null)
{
    public DateTime Now => (Clock ?? (() => DateTime.Now))();
}

/// <summary>
/// One step's content: built once, laid out by <see cref="Arrange"/> for a width (it returns the height it needs, and
/// scrolls in the window when that is taller than the room), and updated in place by <see cref="Sync"/>: a press changes
/// the controls of its own row, never the whole page.
/// </summary>
internal abstract class StepPage : Panel
{
    protected StepPage(PageContext c)
    {
        C = c;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = SetupTheme.Window;
    }

    protected PageContext C { get; }
    protected SetupSession Session => C.Session;
    protected SetupFonts Fonts => C.Fonts;
    protected int S(float logical) => C.Scale.S(logical);

    /// <summary>The design's padding around a step's content.</summary>
    protected int Inset => S(34);

    /// <summary>The thin lines between rows, as laid out last (painted with the page).</summary>
    private readonly List<(int X1, int X2, int Y)> _lines = [];

    /// <summary>Lays the page out at <paramref name="width"/> and returns the height it needs.</summary>
    public int Arrange(int width)
    {
        _lines.Clear();
        int h = Place(width, width - 2 * Inset);
        Invalidate();
        return h;
    }

    /// <param name="room">The width of the content, inside the page's padding.</param>
    protected abstract int Place(int width, int room);

    /// <summary>Something in the session changed: the page updates the controls it concerns.</summary>
    public virtual void Sync(SetupPart part) { }

    /// <summary>The page's text in reading order (the CI UI test checks its words).</summary>
    internal IEnumerable<string> Texts() => All(this).Select(c => c switch
    {
        TextBlock t => t.Text,
        ImpactMeter m => m.Under is null ? m.LevelText : $"{m.LevelText} | {m.Under}",
        SetupButton b => b.Text,
        CheckBox b => b.Text,
        _ => "",
    }).Where(t => t.Length > 0);

    /// <summary>The controls the page shows, and theirs, in order (a hidden one with what is in it left out).</summary>
    private IEnumerable<Control> All(Control root) => root.Controls.Cast<Control>().Where(Shown).SelectMany(c => new[] { c }.Concat(All(c)));

    protected void Line(int x1, int x2, int y) => _lines.Add((x1, x2, y));

    /// <summary>Controls this page hides: kept here, since a control's Visible reads false while its window is not shown.</summary>
    private readonly HashSet<Control> _hidden = [];

    protected void ShowIf(Control c, bool on)
    {
        c.Visible = on;
        if (on) _hidden.Remove(c);
        else _hidden.Add(c);
    }

    /// <summary>A control this page no longer needs (a row that went): removed and disposed.</summary>
    protected void Drop(Control c)
    {
        _hidden.Remove(c);
        c.Parent?.Controls.Remove(c);
        c.Dispose();
    }

    /// <summary>Whether this page shows <paramref name="c"/> (whether or not the window is on screen).</summary>
    protected bool Shown(Control c) => !_hidden.Contains(c);

    /// <summary>A text on one line at its own width when it fits in <paramref name="max"/>, else wrapped to it; returns its bottom.</summary>
    protected static int Wrap(TextBlock t, int x, int y, int max)
    {
        if ((t is LinkText l ? l.OneLine : t.OneLine) <= max) PutLine(t, x, y, max);
        else Put(t, x, y, max);
        return t.Bottom;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(SetupTheme.Line);
        foreach (var (x1, x2, y) in _lines) e.Graphics.DrawLine(pen, x1, y, x2, y);
    }

    // ------------------------------------------------------------------ helpers

    protected TextBlock Words(Control parent, string text, Font font, Color? color = null)
    {
        var t = new TextBlock { Text = text, Font = font, ForeColor = color ?? SetupTheme.Ink };
        parent.Controls.Add(t);
        return t;
    }

    protected TextBlock Words(string text, Font font, Color? color = null) => Words(this, text, font, color);

    /// <summary>A link to a web page PoolWatch may open; plain text for one it may not (yet).</summary>
    protected TextBlock Link(Control parent, SetupLink link, Font font)
    {
        if (!link.Opens) return Words(parent, link.Text, font, SetupTheme.Muted);
        var l = new LinkText { Text = link.Text, Font = font, ForeColor = SetupTheme.Accent };
        l.Click += (_, _) => C.OpenWebPage(link.Url);
        parent.Controls.Add(l);
        return l;
    }

    protected IconBox Icon(Control parent, SetupIcon icon, Color? color = null)
    {
        var i = new IconBox(icon, color);
        parent.Controls.Add(i);
        return i;
    }

    /// <summary>Places a text at (x, y), <paramref name="width"/> wide, as tall as its text; returns its bottom.</summary>
    protected static int Put(TextBlock t, int x, int y, int width)
    {
        t.Location = new Point(x, y);
        t.Fit(width);
        return t.Bottom;
    }

    /// <summary>Places a one-line text (a link, a label beside a field) at its own width; returns its right edge.</summary>
    protected static int PutLine(TextBlock t, int x, int y, int max)
    {
        int w = Math.Min(max, t is LinkText l ? l.OneLine : t.OneLine);
        t.Location = new Point(x, y);
        t.Fit(Math.Max(1, w));
        return t.Right;
    }

    /// <summary>The step's heading and its lead line.</summary>
    protected int Heading(TextBlock title, TextBlock? lead, int y, int room, int below = 20)
    {
        y = Put(title, Inset, y, room) + S(6);
        if (lead is not null) y = Put(lead, Inset, y, Math.Min(room, S(560))) + S(below);
        return y;
    }
}

/// <summary>Step 1: the mission, the four pillars, and Simple or Advanced for setup and the app.</summary>
internal sealed class WelcomePage : StepPage
{
    private readonly AppMark _mark;
    private readonly TextBlock _title, _mission, _question, _later;
    private readonly List<(IconBox Icon, TextBlock Title, TextBlock Text)> _pillars = [];
    private readonly (ChoiceCard Card, TextBlock Title, TextBlock Rec, TextBlock Text) _simple, _advanced;

    public WelcomePage(PageContext c) : base(c)
    {
        _mark = new AppMark(c.AppIcon);
        Controls.Add(_mark);
        _title = Words(SetupText.WelcomeTitle, Fonts.Hero);
        _mission = Words(SetupText.Mission, Fonts.Mission);
        foreach (var (icon, title, text) in SetupText.Pillars)
            _pillars.Add((Icon(this, icon), Words(title, Fonts.Bold), Words(text, Fonts.Small, SetupTheme.Muted)));
        _question = Words(SetupText.LevelQuestion, Fonts.Bold);
        _simple = Card(SetupText.Simple, SetupText.SimpleText, recommended: true, advanced: false);
        _advanced = Card(SetupText.Advanced, SetupText.AdvancedText, recommended: false, advanced: true);
        _later = Words(SetupText.LevelLater, Fonts.Small, SetupTheme.Faint);
        Sync(SetupPart.Step);
    }

    private (ChoiceCard, TextBlock, TextBlock, TextBlock) Card(string title, string text, bool recommended, bool advanced)
    {
        var card = new ChoiceCard(C.Scale, title);
        Controls.Add(card);
        var t = Words(card, title, Fonts.Bold);
        var rec = Words(card, SetupText.Recommended, Fonts.SmallBold, SetupTheme.Accent);
        ShowIf(rec, recommended);
        var d = Words(card, text, Fonts.Small, SetupTheme.Muted);
        foreach (var part in new Control[] { t, rec, d }) card.Clickable(part);
        card.Chosen += () =>
        {
            Session.Advanced = advanced;
            Sync(SetupPart.Step);
        };
        return (card, t, rec, d);
    }

    public override void Sync(SetupPart part)
    {
        _simple.Card.Selected = !Session.Advanced;
        _advanced.Card.Selected = Session.Advanced;
    }

    /// <summary>The level chosen on the page now (the tests).</summary>
    internal ChoiceCard SimpleCard => _simple.Card;
    internal ChoiceCard AdvancedCard => _advanced.Card;

    protected override int Place(int width, int room)
    {
        int y = S(28), mark = S(60), gap = S(18);
        int textRoom = room - mark - gap;
        int textH = _title.Measure(textRoom) + S(4) + _mission.Measure(Math.Min(textRoom, S(470)));
        int heroH = Math.Max(mark, textH);
        _mark.Bounds = new Rectangle(Inset, y + (heroH - mark) / 2, mark, mark);
        int ty = Put(_title, Inset + mark + gap, y + (heroH - textH) / 2, textRoom) + S(4);
        Put(_mission, Inset + mark + gap, ty, Math.Min(textRoom, S(470)));
        y += heroH + S(22);

        int colGap = S(28), col = (room - colGap) / 2;
        for (int row = 0; row < 2; row++)
        {
            int rowH = 0;
            for (int i = 0; i < 2; i++)
            {
                var (icon, title, text) = _pillars[row * 2 + i];
                int x = Inset + i * (col + colGap);
                icon.Bounds = new Rectangle(x, y + S(1), S(20), S(20));
                int tx = x + S(34), tw = col - S(34);
                int bottom = Put(text, tx, Put(title, tx, y, tw) + S(2), tw);
                rowH = Math.Max(rowH, bottom - y);
            }
            y += rowH + (row == 0 ? S(14) : 0);
        }
        y += S(14);
        Line(Inset, Inset + room, y);
        y = Put(_question, Inset, y + S(14), room) + S(10);

        int cardGap = S(12), cardW = (room - cardGap) / 2;
        int cardH = Math.Max(CardHeight(_simple, cardW), CardHeight(_advanced, cardW));
        PlaceCard(_simple, Inset, y, cardW, cardH);
        PlaceCard(_advanced, Inset + cardW + cardGap, y, cardW, cardH);
        y += cardH;
        y = Put(_later, Inset, y + S(10), room);
        return y + S(8);
    }

    private int TextX => S(15 + 18 + 10);

    private int CardHeight((ChoiceCard Card, TextBlock Title, TextBlock Rec, TextBlock Text) c, int w) =>
        S(13) + Math.Max(c.Title.Measure(w), Fonts.Bold.Height) + S(3) + c.Text.Measure(w - TextX - S(15)) + S(13);

    private void PlaceCard((ChoiceCard Card, TextBlock Title, TextBlock Rec, TextBlock Text) c, int x, int y, int w, int h)
    {
        c.Card.Bounds = new Rectangle(x, y, w, h);
        c.Card.RadioAt = new Point(S(15), S(14));
        int room = w - TextX - S(15);
        int right = PutLine(c.Title, TextX, S(13), room);
        if (Shown(c.Rec)) PutLine(c.Rec, right + S(8), S(13) + (c.Title.Height - Fonts.SmallBold.Height) / 2, Math.Max(1, room - (right - TextX) - S(8)));
        Put(c.Text, TextX, c.Title.Bottom + S(3), room);
    }
}

/// <summary>Step 2: one row per known cause, with its level on the None / Low / Medium / High scale and the step that fixes it.</summary>
internal sealed class ImpactPage : StepPage
{
    private readonly TextBlock _title, _lead, _state;
    private readonly Dictionary<Cause, (IconBox Icon, TextBlock Title, TextBlock Detail, ImpactMeter Meter)> _rows = [];

    public ImpactPage(PageContext c) : base(c)
    {
        _title = Words(SetupText.ImpactTitle, Fonts.H1);
        _lead = Words(SetupText.ImpactLead, Fonts.Body, SetupTheme.Muted);
        _state = Words(SetupText.Looking, Fonts.Body, SetupTheme.Muted);
        Sync(SetupPart.Scan);
    }

    /// <summary>The rows as shown now (the tests and the CI UI test).</summary>
    internal IEnumerable<(Cause Cause, string Title, string Detail, string Level, string? Under)> RowsShown =>
        _rows.Select(r => (r.Key, r.Value.Title.Text, r.Value.Detail.Text, r.Value.Meter.LevelText, r.Value.Meter.Under));

    /// <summary>The meter controls by cause: a row is updated in place, never made again.</summary>
    internal IReadOnlyDictionary<Cause, ImpactMeter> Meters => _rows.ToDictionary(r => r.Key, r => r.Value.Meter);

    public override void Sync(SetupPart part)
    {
        if (part is not (SetupPart.Scan or SetupPart.Leak or SetupPart.Fixes)) return;
        var rows = Session.Impact;
        ShowIf(_state, rows.Count == 0);
        _state.Text = Session.Scanned ? SetupText.NotRead : SetupText.Looking;
        foreach (var row in rows)
        {
            if (!_rows.TryGetValue(row.Cause, out var r))
            {
                r = (Icon(this, row.Icon), Words(row.Title, Fonts.Bold), Words(row.Detail, Fonts.Small, SetupTheme.Muted), new ImpactMeter(C.Scale, Fonts));
                Controls.Add(r.Meter);
                _rows[row.Cause] = r;
            }
            r.Title.Text = row.Title;
            r.Detail.Text = row.Detail;
            r.Meter.Show(row.Level, row.Under);
        }
    }

    protected override int Place(int width, int room)
    {
        int y = Heading(_title, _lead, S(28), room);
        if (Shown(_state)) return Put(_state, Inset, y, room) + S(8);
        Line(Inset, Inset + room, y);
        int meterW = _rows.Values.Select(r => r.Meter.MeterWidth).DefaultIfEmpty(S(160)).Max(), textX = Inset + S(26 + 12), textW = room - S(26 + 12) - S(12) - meterW;
        foreach (var (icon, title, detail, meter) in _rows.Values)
        {
            int top = y + S(12);
            int textH = title.Measure(textW) + S(1) + detail.Measure(textW);
            int h = Math.Max(textH, Math.Max(S(20), meter.Needed));
            int ty = Put(title, textX, top + (h - textH) / 2, textW) + S(1);
            Put(detail, textX, ty, textW);
            icon.Bounds = new Rectangle(Inset, top + (h - S(20)) / 2, S(20), S(20));
            meter.Bounds = new Rectangle(Inset + room - meterW, top + (h - meter.Needed) / 2, meterW, meter.Needed);
            y = top + h + S(12);
            Line(Inset, Inset + room, y);
        }
        return y + S(8);
    }
}

/// <summary>Step 3, in green: the three promises.</summary>
internal sealed class SafetyPage : StepPage
{
    private readonly RoundedBox _tile;
    private readonly TextBlock _title;
    private readonly List<(Dot Dot, TextBlock Text)> _promises = [];

    public SafetyPage(PageContext c) : base(c)
    {
        _tile = new RoundedBox { Fill = SetupTheme.GoodTile, Radius = c.Scale.K * 16 };
        Controls.Add(_tile);
        Icon(_tile, SetupIcon.ShieldCheck, SetupTheme.Good);
        _title = Words(SetupText.SafetyTitle, Fonts.Safety);
        foreach (var promise in SetupText.Promises)
        {
            var dot = new Dot(SetupTheme.Good, SetupTheme.OnAccent);
            Controls.Add(dot);
            _promises.Add((dot, Words(promise, Fonts.Promise)));
        }
    }

    protected override int Place(int width, int room)
    {
        int y = S(28 + 18);
        _tile.Bounds = new Rectangle(Inset, y, S(64), S(64));
        _tile.Controls[0].Bounds = new Rectangle(S(15), S(15), S(34), S(34));
        y = Put(_title, Inset, _tile.Bottom + S(18), room) + S(20);
        foreach (var (dot, text) in _promises)
        {
            int textW = room - S(24 + 12), h = Math.Max(S(24), text.Measure(textW));
            dot.Bounds = new Rectangle(Inset, y + (h - S(24)) / 2, S(24), S(24));
            Put(text, Inset + S(36), y + (h - text.Measure(textW)) / 2, textW);
            y += h + S(14);
        }
        return y;
    }
}

/// <summary>
/// Step 4: when to empty the standby cache, in four choices: on a schedule, at a pool size, whichever comes first, or
/// decide later. Where a choice turns clearing on, it says once that video or the mouse may stutter while it runs, and,
/// when the scan found a RAMMap clear task of your own, that a rule replaces it (Next then does). Until the scan has
/// ended it says Next waits for it; while Next applies the choice, the cards and fields are held.
/// </summary>
internal sealed class CachePage : StepPage
{
    private sealed record Option(CacheChoice Choice, ChoiceCard Card, IconBox Icon, TextBlock Title, TextBlock Rec, List<Control> Field);

    private readonly TextBlock _title, _lead, _waiting, _stutter, _ownTask, _error;
    private readonly RoundedBox _frame;
    private readonly CacheChart _chart;
    private readonly List<Option> _options = [];
    private readonly List<ComboBox> _hours = [], _gb = [];
    private bool _syncing;

    public CachePage(PageContext c) : base(c)
    {
        _title = Words(SetupText.CacheTitle, Fonts.H1);
        _lead = Words(SetupText.CacheLead, Fonts.Body, SetupTheme.Muted);
        _frame = new RoundedBox { Border = SetupTheme.Line, Radius = c.Scale.K * 8 };
        Controls.Add(_frame);
        _chart = new CacheChart(c.Scale);
        _frame.Controls.Add(_chart);
        Add(CacheChoice.Schedule, SetupIcon.Clock, SetupText.Schedule, card => [Words(card, SetupText.Every, Fonts.Body), Hours(card)]);
        Add(CacheChoice.PoolSize, SetupIcon.Gauge, SetupText.PoolSize, card => [Words(card, SetupText.WhenItReaches, Fonts.Body), Gb(card)]);
        Add(CacheChoice.Either, SetupIcon.Both, SetupText.Either, card => [Hours(card), Words(card, SetupText.Or, Fonts.Body), Gb(card)]);
        Add(CacheChoice.Later, SetupIcon.Later, SetupText.Later, card => [Words(card, SetupText.LaterText, Fonts.Body, SetupTheme.Muted)]);
        _waiting = Words(SetupText.WaitForScan, Fonts.Small, SetupTheme.Muted);
        _stutter = Words(SetupText.Stutter, Fonts.Small, SetupTheme.Muted);
        _ownTask = Words(SetupText.ReplacesOwnTask, Fonts.Small, SetupTheme.Muted);
        _error = Words("", Fonts.Small, SetupTheme.Error);
        Sync(SetupPart.Cache);
    }

    /// <summary>The cards by choice (the tests).</summary>
    internal IReadOnlyDictionary<CacheChoice, ChoiceCard> Cards => _options.ToDictionary(o => o.Choice, o => o.Card);
    internal IReadOnlyList<ComboBox> HourFields => _hours;
    internal IReadOnlyList<ComboBox> GbFields => _gb;
    internal CacheChart Chart => _chart;
    internal bool StutterShown => Shown(_stutter);
    /// <summary>The note that a rule replaces your own clear task is shown (the tests: it is what you agree to before Next removes it).</summary>
    internal bool OwnTaskShown => Shown(_ownTask);
    internal bool WaitingShown => Shown(_waiting);
    internal string? RecommendedShown => _options.FirstOrDefault(o => Shown(o.Rec))?.Title.Text;

    private void Add(CacheChoice choice, SetupIcon icon, string title, Func<ChoiceCard, List<Control>> field)
    {
        var card = new ChoiceCard(C.Scale, title);
        Controls.Add(card);
        var option = new Option(choice, card, Icon(card, icon), Words(card, title, Fonts.Bold), Words(card, SetupText.Recommended, Fonts.SmallBold, SetupTheme.Accent), field(card));
        foreach (var t in option.Field.OfType<TextBlock>().Append(option.Title).Append(option.Rec)) card.Clickable(t);
        card.Chosen += () => Session.ChooseCache(choice);
        _options.Add(option);
    }

    private ComboBox Hours(ChoiceCard card) => Field(card, _hours, CacheChoices.Options(CacheChoices.HourOptions, Session.Hours), SetupText.Hours,
        v => { Session.SetHours(v); Choose(card); });

    private ComboBox Gb(ChoiceCard card) => Field(card, _gb, CacheChoices.Options(CacheChoices.GbOptions, Session.PoolGb), SetupText.Gb,
        v => { Session.SetPoolGb(v); Choose(card); });

    /// <summary>Changing a card's number chooses that card.</summary>
    private void Choose(ChoiceCard card)
    {
        if (_options.FirstOrDefault(o => o.Card == card) is { } o) Session.ChooseCache(o.Choice);
    }

    private ComboBox Field(ChoiceCard card, List<ComboBox> all, IReadOnlyList<double> values, Func<double, string> words, Action<double> chosen)
    {
        var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Font = Fonts.Body, FlatStyle = FlatStyle.System };
        Fill(box, values, words);
        box.SelectedIndexChanged += (_, _) =>
        {
            if (_syncing || box.SelectedItem is not Item item) return;
            chosen(item.Value);
        };
        card.Controls.Add(box);
        all.Add(box);
        return box;
    }

    private sealed record Item(double Value, string Text)
    {
        public override string ToString() => Text;
    }

    private static void Fill(ComboBox box, IReadOnlyList<double> values, Func<double, string> words)
    {
        box.BeginUpdate();
        box.Items.Clear();
        foreach (var v in values) box.Items.Add(new Item(v, words(v)));
        box.EndUpdate();
    }

    public override void Sync(SetupPart part)
    {
        if (part is not (SetupPart.Cache or SetupPart.Scan or SetupPart.Step or SetupPart.Busy)) return;
        _syncing = true;
        try
        {
            foreach (var o in _options) o.Card.Selected = o.Choice == Session.Cache;
            var recommended = Session.Recommended;
            foreach (var o in _options) ShowIf(o.Rec, o.Choice == recommended);
            Select(_hours, Session.Hours, CacheChoices.HourOptions, SetupText.Hours);
            Select(_gb, Session.PoolGb, CacheChoices.GbOptions, SetupText.Gb);
        }
        finally { _syncing = false; }
        _chart.Show(Session.Hours, Session.PoolGb);
        // Held while Next applies the choice, so the step never goes on with one choice while showing another.
        foreach (var o in _options) o.Card.Enabled = !Session.Busy;
        bool clears = Session.Cache != CacheChoice.Later;
        ShowIf(_waiting, !Session.Scanned);
        ShowIf(_stutter, clears);
        ShowIf(_ownTask, clears && Session.HasOwnClearTask);
        _error.Text = Session.Error(SetupSession.CacheKey) ?? "";
        ShowIf(_error, _error.Text.Length > 0);
    }

    private static void Select(List<ComboBox> boxes, double value, IReadOnlyList<double> list, Func<double, string> words)
    {
        var values = CacheChoices.Options(list, value);
        foreach (var box in boxes)
        {
            if (box.Items.Count != values.Count) Fill(box, values, words);
            int i = values.ToList().IndexOf(value);
            if (box.SelectedIndex != i) box.SelectedIndex = i;
        }
    }

    protected override int Place(int width, int room)
    {
        int y = Heading(_title, _lead, S(28), room);
        int chartH = _chart.ChartHeight;
        _frame.Bounds = new Rectangle(Inset, y, room, chartH + S(8 + 2));
        _chart.Bounds = new Rectangle(S(12), S(8), room - S(24), chartH);
        y = _frame.Bottom + S(16);

        int gap = S(12), cardW = (room - gap) / 2;
        for (int row = 0; row < 2; row++)
        {
            var pair = _options.Skip(row * 2).Take(2).ToList();
            int h = pair.Max(o => CardHeight(o, cardW));
            for (int i = 0; i < pair.Count; i++) PlaceCard(pair[i], Inset + i * (cardW + gap), y, cardW, h);
            y += h + (row == 0 ? gap : 0);
        }
        foreach (var t in new[] { _waiting, _stutter, _ownTask, _error }.Where(Shown)) y = Put(t, Inset, y + S(14), room);
        return y + S(8);
    }

    private int Pad => S(14);

    /// <summary>A card's field row: its words and fields, then "Recommended" on the recommended card (as the design places it).</summary>
    private List<Control> Items(Option o) => Shown(o.Rec) ? [.. o.Field, o.Rec] : o.Field;

    /// <summary>The field row's items, wrapped to the card's width: their places, and the row's height.</summary>
    private (List<Rectangle> Places, int Height) FieldRow(Option o, int width)
    {
        var places = new List<Rectangle>();
        int x = 0, y = 0, lineH = 0, gap = S(6);
        foreach (var c in Items(o))
        {
            Size size = c switch
            {
                ComboBox box => new Size(ComboWidth(box), box.PreferredHeight),
                TextBlock t when o.Choice == CacheChoice.Later && t != o.Rec => new Size(width, t.Measure(width)),
                TextBlock t => new Size(Math.Min(width, t.OneLine), t.Measure(Math.Min(width, t.OneLine))),
                _ => c.Size,
            };
            if (x > 0 && x + size.Width > width) { x = 0; y += lineH + gap; lineH = 0; }
            places.Add(new Rectangle(x, y, size.Width, size.Height));
            x += size.Width + gap;
            lineH = Math.Max(lineH, size.Height);
        }
        // Each line's items centred on the line.
        for (int i = 0; i < places.Count; i++)
        {
            int line = places.Where(p => p.Y == places[i].Y).Max(p => p.Height);
            places[i] = places[i] with { Y = places[i].Y + (line - places[i].Height) / 2 };
        }
        return (places, y + lineH);
    }

    private int ComboWidth(ComboBox box) =>
        box.Items.Cast<object>().Select(i => SetupDraw.Line(i.ToString() ?? "", box.Font).Width).DefaultIfEmpty(0).Max() + S(34);

    private int TopHeight(Option o) => Math.Max(S(20), Math.Max(o.Title.Measure(1000), S(18)));

    private int CardHeight(Option o, int w) => Pad - S(1) + TopHeight(o) + S(9) + FieldRow(o, w - 2 * Pad).Height + Pad - S(1);

    private void PlaceCard(Option o, int x, int y, int w, int h)
    {
        o.Card.Bounds = new Rectangle(x, y, w, h);
        int top = Pad - S(1), line = TopHeight(o);
        o.Card.RadioAt = new Point(Pad, top + (line - S(18)) / 2);
        o.Icon.Bounds = new Rectangle(Pad + S(18 + 10), top + (line - S(20)) / 2, S(20), S(20));
        int tx = o.Icon.Right + S(10), room = w - Pad - tx;
        PutLine(o.Title, tx, top + (line - o.Title.Measure(1000)) / 2, room);
        var items = Items(o);
        var (places, _) = FieldRow(o, w - 2 * Pad);
        int fy = top + line + S(9);
        for (int i = 0; i < items.Count; i++)
        {
            var p = places[i];
            if (items[i] is TextBlock t) { t.Location = new Point(Pad + p.X, fy + p.Y); t.Fit(p.Width); }
            else items[i].Bounds = new Rectangle(Pad + p.X, fy + p.Y, p.Width, p.Height);
        }
    }
}

/// <summary>
/// Step 5: the fixes for this PC, each row with its own switch, choice, button or status. A change runs when Next is
/// pressed; afterwards only the rows that changed are updated (or replaced, when a row now offers something else).
/// </summary>
internal sealed class FixesPage : StepPage
{
    private sealed class Row
    {
        public required Fix Fix;
        public required FixInput Input;
        public required IconBox Icon;
        public required TextBlock Title, Detail, Error;
        public LinkText? Why;
        public RoundedBox? WhyBox;
        public TextBlock? WhyText;
        public List<TextBlock> Links = [];
        public Control? End;
        public bool Open;
        public IEnumerable<Control> Controls => new Control?[] { Icon, Title, Detail, Error, Why, WhyBox, End }.OfType<Control>();
    }

    private readonly TextBlock _title, _state;
    private readonly List<Row> _rows = [];

    public FixesPage(PageContext c) : base(c)
    {
        _title = Words(SetupText.FixesTitle, Fonts.H1);
        _state = Words(SetupText.Looking, Fonts.Body, SetupTheme.Muted);
        Sync(SetupPart.Scan);
    }

    /// <summary>The rows' controls by fix (the tests: a row is updated in place, or replaced alone).</summary>
    internal IReadOnlyDictionary<Fix, Control> Ends => _rows.Where(r => r.End is not null).ToDictionary(r => r.Fix, r => r.End!);
    internal IReadOnlyList<(Fix Fix, string Title, string Detail, FixInput Input, string? Error, bool WhyOpen)> RowsShown =>
        _rows.Select(r => (r.Fix, r.Title.Text, r.Detail.Text, r.Input, Shown(r.Error) ? r.Error.Text : null, r.Open)).ToList();

    /// <summary>
    /// The rows as they are now, also when the step is brought up: a row can change with nothing told (the shortcut put back
    /// from Active changes), and Next applies the switches and Yes/Nos the window was told of, so the page shows each one.
    /// The session tells the window of them with these same notices (<see cref="SetupSession.ShowsFixRows"/>).
    /// </summary>
    public override void Sync(SetupPart part)
    {
        if (!SetupSession.ShowsFixRows(part)) return;
        var rows = Session.Fixes;
        ShowIf(_state, rows.Count == 0);
        _state.Text = Session.Scanned ? SetupText.NotRead : SetupText.WaitForScan;
        // Rows that are gone, or that now offer something else, go; the others stay and are updated.
        foreach (var gone in _rows.Where(r => rows.All(n => n.Fix != r.Fix || n.Input != r.Input)).ToList())
        {
            foreach (var c in gone.Controls) Drop(c);
            _rows.Remove(gone);
        }
        for (int i = 0; i < rows.Count; i++)
        {
            var row = _rows.FirstOrDefault(r => r.Fix == rows[i].Fix) ?? New(rows[i]);
            if (_rows.IndexOf(row) != i) { _rows.Remove(row); _rows.Insert(i, row); }
            Update(row, rows[i]);
        }
    }

    private Row New(FixRow f)
    {
        var row = new Row
        {
            Fix = f.Fix, Input = f.Input, Icon = Icon(this, f.Icon), Title = Words(f.Title, Fonts.Bold), Detail = Words(f.Detail, Fonts.Small, SetupTheme.Muted),
            Error = Words("", Fonts.Small, SetupTheme.Error), Open = f.WhyOpen,
        };
        if (f.Why is not null)
        {
            row.Why = new LinkText { Text = SetupText.Why, Font = Fonts.Small, ForeColor = SetupTheme.Accent, Icon = f.WhyOpen ? SetupIcon.ChevronDown : SetupIcon.ChevronRight };
            Controls.Add(row.Why);
            row.WhyBox = new RoundedBox { Fill = SetupTheme.Soft, Radius = C.Scale.K * 6 };
            Controls.Add(row.WhyBox);
            ShowIf(row.WhyBox, f.WhyOpen);
            row.WhyText = Words(row.WhyBox, f.Why, Fonts.Small);
            foreach (var link in f.Links ?? []) row.Links.Add(Link(row.WhyBox, link, Fonts.Small));
            row.Why.Click += (_, _) =>
            {
                row.Open = !row.Open;
                ShowIf(row.WhyBox, row.Open);
                row.Why.Icon = row.Open ? SetupIcon.ChevronDown : SetupIcon.ChevronRight;
                row.Why.Invalidate();
                Relayout();
            };
        }
        row.End = f.Input switch
        {
            FixInput.Toggle => NewToggle(f),
            FixInput.YesNo => NewYesNo(f),
            FixInput.Button => NewButton(f),
            FixInput.Status => new StatusText { Text = f.Status ?? "", Font = Fonts.Small, ForeColor = SetupTheme.Muted },
            _ => null,
        };
        if (row.End is not null) Controls.Add(row.End);
        _rows.Add(row);
        return row;
    }

    private Toggle NewToggle(FixRow f)
    {
        var t = new Toggle(C.Scale, Fonts, f.Title) { On = f.Fix == Fix.SignIn ? Session.SignIn : Session.Guard };
        t.Changed += on => Session.Choose(f.Fix, on);
        return t;
    }

    private YesNo NewYesNo(FixRow f)
    {
        var y = new YesNo(C.Scale, Fonts, f.Title) { Value = Session.Shortcut };
        y.Changed += yes => Session.Choose(f.Fix, yes);
        return y;
    }

    private SetupButton NewButton(FixRow f)
    {
        var b = new SetupButton(C.Scale, Fonts, SetupText.ShowMeHow, primary: false, small: true);
        b.Click += async (_, _) => await Session.Press(f.Fix, C.Owner());
        return b;
    }

    private void Update(Row row, FixRow f)
    {
        row.Title.Text = f.Title;
        row.Detail.Text = f.Detail;
        string? error = Session.Error(SetupSession.Key(f.Fix));
        row.Error.Text = error ?? "";
        ShowIf(row.Error, error is not null);
        switch (row.End)
        {
            case Toggle t: t.On = f.Fix == Fix.SignIn ? Session.SignIn : Session.Guard; t.Enabled = !Session.Busy; break;
            case YesNo y: y.Value = Session.Shortcut; y.Enabled = !Session.Busy; break;
            case SetupButton b: b.Enabled = !Session.Busy; break;
            case StatusText s: s.Text = f.Status ?? ""; break;
        }
    }

    /// <summary>A "Why?" opened or closed: the page lays out again where it is.</summary>
    private void Relayout() => (Parent as SetupWizardView.ContentHost)?.Relayout();

    protected override int Place(int width, int room)
    {
        int y = Put(_title, Inset, S(28), room) + S(8);
        if (Shown(_state)) return Put(_state, Inset, y, room) + S(8);
        Line(Inset, Inset + room, y);
        foreach (var row in _rows)
        {
            int top = y + S(10);
            var end = row.End is null ? Size.Empty : EndSize(row.End);
            int textX = Inset + S(22 + 14), textW = room - S(22 + 14) - (end.Width > 0 ? end.Width + S(14) : 0);
            row.Icon.Bounds = new Rectangle(Inset, top + S(1), S(20), S(20));
            int ty = Put(row.Title, textX, top, textW) + S(2);
            ty = Put(row.Detail, textX, ty, textW);
            if (Shown(row.Error)) ty = Put(row.Error, textX, ty + S(2), textW);
            if (row.Why is { } why)
            {
                ty = Wrap(why, textX, ty + S(4), textW);
                if (row.WhyBox is { } box && row.Open)
                {
                    int pad = S(12), inner = textW - 2 * pad, by = S(8);
                    by = Put(row.WhyText!, pad, by, inner) + S(6);
                    foreach (var link in row.Links) by = Wrap(link, pad, by, inner) + S(2);
                    box.Bounds = new Rectangle(textX, ty + S(6), textW, by - S(2) + S(8));
                    ty = box.Bottom;
                }
            }
            if (row.End is { } c) c.Bounds = new Rectangle(Inset + room - end.Width, top, end.Width, end.Height);
            y = Math.Max(ty, top + end.Height) + S(10);
            Line(Inset, Inset + room, y);
        }
        return y + S(8);
    }

    private Size EndSize(Control c) => c switch
    {
        Toggle t => t.Needed,
        YesNo y => y.Needed,
        SetupButton b => b.Needed,
        StatusText s => s.Needed,
        _ => c.Size,
    };

    /// <summary>A status at a row's end: a green dot and its words ("On", "On your desktop").</summary>
    internal sealed class StatusText : TextBlock
    {
        public Size Needed => new(OneLine + Font.Height, Font.Height);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float d = Font.Height * 0.45f;
            using (var b = new SolidBrush(SetupTheme.Good)) g.FillEllipse(b, 0, (Height - d) / 2, d, d);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(Font.Height, 0, Width - Font.Height, Height), ForeColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }
    }
}

/// <summary>Step 6, for later in the app: setup changes no services; Windows services and other apps' services, and where to find it.</summary>
internal sealed class ServicesPage : StepPage
{
    private readonly RoundedBox _tag, _windows, _apps, _green;
    private readonly IconBox _tagIcon, _greenIcon, _winIcon, _appsIcon;
    private readonly TextBlock _tagText, _title, _lead, _winTitle, _winSub, _winText, _appsTitle, _appsSub, _greenText, _where;
    private readonly List<(Dot Dot, TextBlock Text)> _flow = [];

    public ServicesPage(PageContext c) : base(c)
    {
        _tag = new RoundedBox { Fill = SetupTheme.AccentSoft, Radius = c.Scale.K * 4 };
        Controls.Add(_tag);
        _tagIcon = Icon(_tag, SetupIcon.Later, SetupTheme.Accent);
        _tagText = Words(_tag, SetupText.LaterTag, Fonts.SmallBold, SetupTheme.Accent);
        _title = Words(SetupText.ServicesTitle, Fonts.H1);
        _lead = Words(SetupText.ServicesLead, Fonts.Body, SetupTheme.Muted);
        _windows = new RoundedBox { Border = SetupTheme.Line, Radius = c.Scale.K * 10 };
        _apps = new RoundedBox { Border = SetupTheme.Line, Radius = c.Scale.K * 10 };
        Controls.Add(_windows);
        Controls.Add(_apps);
        _winIcon = Icon(_windows, SetupIcon.Windows);
        _winTitle = Words(_windows, SetupText.WindowsServices, Fonts.Card);
        _winSub = Words(_windows, SetupText.KnowsThese, Fonts.SmallBold, SetupTheme.Accent);
        _winText = Words(_windows, SetupText.WindowsServicesText, Fonts.Small, SetupTheme.Muted);
        _appsIcon = Icon(_apps, SetupIcon.Layers);
        _appsTitle = Words(_apps, SetupText.OtherServices, Fonts.Card);
        _appsSub = Words(_apps, SetupText.WithYourAgent, Fonts.SmallBold, SetupTheme.Accent);
        for (int i = 0; i < SetupText.AgentSteps.Count; i++)
        {
            var dot = new Dot(SetupTheme.AccentSoft, SetupTheme.Accent, (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)) { Font = Fonts.SmallBold };
            _apps.Controls.Add(dot);
            _flow.Add((dot, Words(_apps, SetupText.AgentSteps[i], Fonts.Small)));
        }
        _green = new RoundedBox { Fill = SetupTheme.GoodSoft, Radius = c.Scale.K * 8 };
        Controls.Add(_green);
        _greenIcon = Icon(_green, SetupIcon.ShieldCheck, SetupTheme.Good);
        _greenText = Words(_green, SetupText.PutBack, Fonts.Body);
        _where = Words(SetupText.FindIt, Fonts.Small, SetupTheme.Faint);
    }

    protected override int Place(int width, int room)
    {
        int y = S(28);
        int tagText = Math.Min(room - S(9 + 14 + 7 + 9), _tagText.OneLine);
        _tagIcon.Bounds = new Rectangle(S(9), S(3) + (Fonts.SmallBold.Height - S(14)) / 2, S(14), S(14));
        PutLine(_tagText, _tagIcon.Right + S(7), S(3), tagText);
        _tag.Bounds = new Rectangle(Inset, y, _tagText.Right + S(9), Fonts.SmallBold.Height + S(6));
        y = Heading(_title, _lead, _tag.Bottom + S(10), room);

        int gap = S(14), cardW = (room - gap) / 2, pad = S(18), inner = cardW - 2 * pad;
        int h = Math.Max(WindowsCard(inner), AppsCard(inner)) + 2 * S(16);
        _windows.Bounds = new Rectangle(Inset, y, cardW, h);
        _apps.Bounds = new Rectangle(Inset + cardW + gap, y, cardW, h);
        y += h + S(14);

        int gy = S(11), gtext = room - S(14 + 20 + 12 + 14);
        int gh = Math.Max(S(20), _greenText.Measure(gtext));
        _greenIcon.Bounds = new Rectangle(S(14), gy + (gh - S(20)) / 2, S(20), S(20));
        Put(_greenText, S(14 + 20 + 12), gy + (gh - _greenText.Measure(gtext)) / 2, gtext);
        _green.Bounds = new Rectangle(Inset, y, room, gh + 2 * gy);
        y = Put(_where, Inset, _green.Bottom + S(14), room);
        return y + S(8);
    }

    /// <summary>Lays out a card's head (icon and title) at its top padding; returns the y below it.</summary>
    private int Head(IconBox icon, TextBlock title, int inner)
    {
        int top = S(16), pad = S(18), line = Math.Max(S(22), title.Measure(inner - S(32)));
        icon.Bounds = new Rectangle(pad, top + (line - S(22)) / 2, S(22), S(22));
        Put(title, pad + S(32), top + (line - title.Measure(inner - S(32))) / 2, inner - S(32));
        return top + line + S(8);
    }

    private int WindowsCard(int inner)
    {
        int pad = S(18), y = Head(_winIcon, _winTitle, inner);
        y = Put(_winSub, pad, y, inner) + S(8);
        return Put(_winText, pad, y, inner) - S(16);
    }

    private int AppsCard(int inner)
    {
        int pad = S(18), y = Head(_appsIcon, _appsTitle, inner);
        y = Put(_appsSub, pad, y, inner) + S(8 + 2);
        foreach (var (dot, text) in _flow)
        {
            int tw = inner - S(20 + 9), h = Math.Max(S(20), text.Measure(tw));
            dot.Bounds = new Rectangle(pad, y + (h - S(20)) / 2, S(20), S(20));
            Put(text, pad + S(29), y + (h - text.Measure(tw)) / 2, tw);
            y += h + S(7);
        }
        return y - S(7) - S(16);
    }
}

/// <summary>Step 7: what setup set, the update notices' box, and where PoolWatch lives (next to the clock).</summary>
internal sealed class ReadyPage : StepPage
{
    private readonly RoundedBox _badge;
    private readonly TextBlock _title, _lead, _updatesHint, _updatesError, _trayText;
    private readonly CheckBox? _updates;
    private readonly TrayPicture _tray;
    private readonly List<(string Key, IconBox Icon, TextBlock Name, TextBlock Value)> _summary = [];
    private bool _syncing;

    public ReadyPage(PageContext c) : base(c)
    {
        _badge = new RoundedBox { Fill = SetupTheme.AccentSoft, Radius = c.Scale.K * 24 };
        Controls.Add(_badge);
        _badge.Controls.Add(new AppMark(c.AppIcon));
        _title = Words(SetupText.ReadyTitle, Fonts.H1);
        _lead = Words(SetupText.ReadyLead, Fonts.Body, SetupTheme.Muted);
        if (Session.Updates is { } updates)
        {
            _updates = new CheckBox { Text = SimpleSettings.UpdatesLabel, Checked = updates.Enabled, Font = Fonts.Body, ForeColor = SetupTheme.Ink, AutoSize = false, UseMnemonic = false };
            _updates.CheckedChanged += (_, _) =>
            {
                if (_syncing) return;
                Session.SetUpdates(_updates.Checked);
            };
            Controls.Add(_updates);
        }
        _updatesHint = Words(SimpleSettings.UpdatesHint, Fonts.Small, SetupTheme.Faint);
        ShowIf(_updatesHint, _updates is not null);
        _updatesError = Words("", Fonts.Small, SetupTheme.Error);
        ShowIf(_updatesError, false);
        _tray = new TrayPicture(c.Scale, Fonts, c.AppIcon, c.Now);
        Controls.Add(_tray);
        _trayText = Words(SetupText.TrayNote, Fonts.Body);
        Sync(SetupPart.Summary);
    }

    internal IReadOnlyList<(string Key, string Value)> RowsShown => _summary.Select(s => (s.Key, s.Value.Text)).ToList();
    internal IReadOnlyDictionary<string, TextBlock> Values => _summary.ToDictionary(s => s.Key, s => s.Value);
    internal CheckBox? UpdatesBox => _updates;
    internal TrayPicture Tray => _tray;

    public override void Sync(SetupPart part)
    {
        if (part == SetupPart.Step) _tray.ShowTime(C.Now); // the step opens (the view syncs Step only for the step shown)
        if (part is SetupPart.Summary or SetupPart.Step or SetupPart.Scan or SetupPart.Fixes or SetupPart.Cache or SetupPart.Leak)
        {
            var rows = Session.Summary;
            foreach (var gone in _summary.Where(s => rows.All(r => r.Key != s.Key)).ToList())
            {
                foreach (var c in new Control[] { gone.Icon, gone.Name, gone.Value }) Drop(c);
                _summary.Remove(gone);
            }
            for (int i = 0; i < rows.Count; i++)
            {
                int at = _summary.FindIndex(s => s.Key == rows[i].Key);
                if (at < 0)
                {
                    _summary.Insert(i, (rows[i].Key, Icon(this, rows[i].Icon), Words(rows[i].Key, Fonts.Body, SetupTheme.Muted), Words(rows[i].Value, Fonts.Bold)));
                    continue;
                }
                if (at != i) { var item = _summary[at]; _summary.RemoveAt(at); _summary.Insert(i, item); }
                _summary[i].Value.Text = rows[i].Value;
            }
        }
        if (_updates is not null && Session.Updates is { } updates)
        {
            _syncing = true;
            _updates.Checked = updates.Enabled; // what is in effect, also when a change did not work
            _syncing = false;
            _updatesError.Text = Session.UpdatesError ?? "";
            ShowIf(_updatesError, Session.UpdatesError is not null);
        }
    }

    protected override int Place(int width, int room)
    {
        int y = S(28 + 4), badge = S(48), mark = S(30);
        int textX = Inset + badge + S(16), textW = room - badge - S(16);
        int textH = _title.Measure(textW) + S(2) + _lead.Measure(textW);
        int head = Math.Max(badge, textH);
        _badge.Bounds = new Rectangle(Inset, y + (head - badge) / 2, badge, badge);
        _badge.Controls[0].Bounds = new Rectangle((badge - mark) / 2, (badge - mark) / 2, mark, mark);
        Put(_lead, textX, Put(_title, textX, y + (head - textH) / 2, textW) + S(2), textW);
        y += head + S(22);

        Line(Inset, Inset + room, y);
        int keyX = Inset + S(22 + 12), valueX = keyX + S(150 + 12), valueW = Inset + room - valueX;
        foreach (var (_, icon, name, value) in _summary)
        {
            int top = y + S(10);
            int h = Math.Max(S(18), Math.Max(name.Measure(S(150)), value.Measure(valueW)));
            icon.Bounds = new Rectangle(Inset, top + (h - S(18)) / 2, S(18), S(18));
            Put(name, keyX, top + (h - name.Measure(S(150))) / 2, S(150));
            Put(value, valueX, top + (h - value.Measure(valueW)) / 2, valueW);
            y = top + h + S(10);
            Line(Inset, Inset + room, y);
        }
        y += S(18);

        if (_updates is not null)
        {
            var box = _updates.GetPreferredSize(Size.Empty);
            int boxW = Math.Min(box.Width, room);
            _updates.Bounds = new Rectangle(Inset, y, boxW, box.Height);
            int rest = room - boxW - S(4);
            int hintW = _updatesHint.OneLine;
            if (hintW <= rest) PutLine(_updatesHint, _updates.Right + S(4), y + (box.Height - _updatesHint.Measure(rest)) / 2, rest);
            else Put(_updatesHint, Inset + S(20), y + box.Height, room - S(20));
            y = Math.Max(_updates.Bottom, _updatesHint.Bottom);
            if (Shown(_updatesError)) y = Put(_updatesError, Inset + S(20), y + S(2), room - S(20));
            y += S(18);
        }

        var tray = _tray.Needed;
        int trayTextW = room - tray.Width - S(18), rowH = Math.Max(tray.Height, _trayText.Measure(trayTextW));
        _tray.Bounds = new Rectangle(Inset, y + (rowH - tray.Height) / 2, tray.Width, tray.Height);
        Put(_trayText, _tray.Right + S(18), y + (rowH - _trayText.Measure(trayTextW)) / 2, trayTextW);
        return y + rowH + S(8);
    }
}
