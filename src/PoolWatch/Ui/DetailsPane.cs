namespace PoolWatch.Ui;

/// <summary>
/// The Pool causes details (<see cref="MainForm.LayDetails"/>): the title, the words and the green line (what PoolWatch
/// already does about the row) one under the other, and the actions in one column down the right, all on
/// <see cref="Content"/>, which is never shorter than they need. Where the pane is at least that tall, the content is
/// the pane's size and the words take what the others leave, as the details always had it. Where the pane is shorter (a
/// small window), the content keeps the height it needs and the pane scrolls up and down to it: its scroll bar, the
/// mouse wheel, or the keyboard. The pane is a tab stop of its own, before the words and the actions: when Tab or
/// Shift+Tab brings the focus to it, it shows its top; with the focus its title is framed by the focus rectangle (while
/// Windows shows focus cues) and the arrow keys, Page Up, Page Down, Home and End scroll it; the focus given back when the window is active again keeps
/// where it was scrolled. Tab then goes on to the words and each action, which WinForms brings into view as each takes
/// the focus. So the green line never lies over the words or the title, no action is cut off, and the keyboard reaches
/// all of it. Another row's details open at their top (<see cref="ShowRow"/>). Not sealed: its test makes one that has the
/// focus, which no window that is never shown can have.
/// </summary>
internal class DetailsPane : Panel
{
    /// <summary>
    /// What the details show when no row is selected (<see cref="KeyOf"/>): nothing is listed, or rows are listed and none
    /// is selected. A row's own key has a kind and a colon ("tag:", "inc:", "proc:", "find:"), so these are never one.
    /// </summary>
    internal const string NothingListed = "empty", NoneSelected = "none";

    private readonly WrapLabel _title, _handled;
    private readonly Control _words, _actions;
    private string? _row;

    /// <param name="title">The title, docked at the top.</param>
    /// <param name="words">The plain explanation, which fills what the others leave (at least two of its lines).</param>
    /// <param name="handled">The green line, docked at the bottom at the height its text needs (it shows only with a text: <c>MainForm.ShowHandled</c>).</param>
    /// <param name="actions">The actions' column, docked at the right.</param>
    public DetailsPane(WrapLabel title, Control words, WrapLabel handled, Control actions)
    {
        (_title, _words, _handled, _actions) = (title, words, handled, actions);
        AutoScroll = true;
        // A tab stop of its own (a Panel is not one): the title is no tab stop, so before this the keyboard reached the words
        // and the actions but never the title of details scrolled down in a small window. A screen reader reads the title.
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
        AccessibleRole = AccessibleRole.Pane;
        AccessibleName = title.Text;
        title.TextChanged += (_, _) => AccessibleName = title.Text;
        Content = new DetailsContent(this) { Margin = Padding.Empty };
        (title.Dock, words.Dock, handled.Dock, actions.Dock) = (DockStyle.Top, DockStyle.Fill, DockStyle.Bottom, DockStyle.Right);
        // Docked at the bottom, the green line takes the height for its width in the content's own layout (AutoSize,
        // WrapLabel.GetPreferredSize): a height it set itself afterwards kept its top and ran past the bottom.
        handled.AutoSize = true;
        Content.Controls.Add(words); // fill first, then the docked edges (the last added takes its edge first: the actions the whole height)
        Content.Controls.Add(handled);
        Content.Controls.Add(title);
        Content.Controls.Add(actions);
        Controls.Add(Content);
    }

    /// <summary>What scrolls: the title, the words, the green line and the actions, laid out on it (the pane's only child).</summary>
    public Panel Content { get; }

    /// <summary>
    /// The key the details show (<see cref="ShowRow"/>): the selected row's (<paramref name="rowKey"/>), else which of the
    /// two texts without a row they show, nothing listed (no row at all, <paramref name="rows"/> 0) or none selected. Both
    /// had the key null: from one to the other the pane kept where it was scrolled, the new title out of view in a small
    /// window.
    /// </summary>
    internal static string KeyOf(string? rowKey, int rows) => rowKey ?? (rows == 0 ? NothingListed : NoneSelected);

    /// <summary>
    /// The details now show <paramref name="key"/> (<see cref="KeyOf"/>). Another key's details open at their top, title
    /// first, as its words do (<c>MainForm.SetText</c>); the same row's, refreshed by a sample, stay where they were
    /// scrolled. Kept for every row, a row selected after the details were scrolled opened part-way down, its title out of
    /// view.
    /// </summary>
    public void ShowRow(string key)
    {
        if (key == _row) return;
        _row = key;
        AutoScrollPosition = Point.Empty;
    }

    /// <summary>The words' least height: two of their lines, so they always show and their own scroll bar has room.</summary>
    public int WordsLeast => 2 * _words.Font.Height;

    /// <summary>
    /// The height the details need at <paramref name="width"/>: the title, the words' least and the green line at the
    /// width they get beside the actions, or the actions' column down to its last shown action, whichever is taller.
    /// </summary>
    public int NeededHeight(int width)
    {
        var column = _actions.GetPreferredSize(Size.Empty);
        int text = width - Content.Padding.Horizontal - (_actions.AutoSize ? column.Width : _actions.Width);
        int stack = _title.HeightAt(text) + WordsLeast + (_handled.Text.Length > 0 ? _handled.HeightAt(text) : 0);
        return Content.Padding.Vertical + Math.Max(stack, ColumnHeight(column.Height));
    }

    /// <summary>
    /// The actions' column down to the bottom of its last shown action. Its preferred height (<paramref name="preferred"/>)
    /// also counts the margin under that action, which holds nothing: counted, it gave the details a scroll bar where
    /// everything fitted. The actions share one margin (<c>MainForm.DetailActions</c>), so the last one's is the last shown
    /// one's. With none shown the column is its padding; with no action at all, its preferred height as it is.
    /// </summary>
    private int ColumnHeight(int preferred) =>
        _actions.Controls.Count == 0 ? preferred : Math.Max(_actions.Padding.Vertical, preferred - _actions.Controls[^1].Margin.Bottom);

    /// <summary>
    /// The least height the pane keeps in a split (<see cref="CausesSplit"/>): down to the bottom of its title's first line
    /// as the title draws it (its top padding, then one line of its font), the rest scrolls. The title's padding under
    /// that line is not needed to read it.
    /// </summary>
    public int TitleLine => Padding.Vertical + Content.Padding.Top + _title.Padding.Top + PaintedLabel.Measure(" ", _title.Font, short.MaxValue);

    // ---------------------------------------------------------------- the keyboard

    /// <summary>The keys that scroll the pane are the pane's while it has the focus, not the window's (which would move the focus with the arrows).</summary>
    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Modifiers != Keys.None || ScrollTarget(e.KeyCode) is not { } y) return;
        AutoScrollPosition = new Point(0, Math.Max(0, y)); // the pane keeps it within its content
        e.Handled = true;
    }

    /// <summary>
    /// How far down the content a key scrolls the pane: a line of the words up or down with the arrows, a page less a line
    /// with Page Up and Page Down, the top with Home and the end with End; null for any other key.
    /// </summary>
    private int? ScrollTarget(Keys key)
    {
        int now = -AutoScrollPosition.Y, line = _words.Font.Height, page = Math.Max(line, ClientSize.Height - line);
        return key switch
        {
            Keys.Up => now - line,
            Keys.Down => now + line,
            Keys.PageUp => now - page,
            Keys.PageDown => now + page,
            Keys.Home => 0,
            Keys.End => Content.Height,
            _ => null,
        };
    }

    /// <summary>
    /// Tab or Shift+Tab brings the focus to the pane (WinForms selects it "directed"): its top shows, the title first.
    /// Only then: WinForms also gives the focus back to the pane whenever the window is active again (Alt+Tab back, a
    /// restore, a show from the tray), and a top shown on every focus lost the place a keyboard user had scrolled to.
    /// </summary>
    protected override void Select(bool directed, bool forward)
    {
        base.Select(directed, forward);
        if (directed) AutoScrollPosition = Point.Empty;
    }

    /// <summary>The focus comes to the pane, by any way: the title framed as the focus (while Windows shows focus cues); the place stays.</summary>
    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        ShowFocus();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        ShowFocus();
    }

    /// <summary>Windows starts or stops showing focus cues (the first Tab or Alt shows them).</summary>
    protected override void OnChangeUICues(UICuesEventArgs e)
    {
        base.OnChangeUICues(e);
        ShowFocus();
    }

    private void ShowFocus() => _title.FocusCue = Focused && ShowFocusCues;

    /// <summary>
    /// Where the content goes: at the pane's width, or beside the scroll bar where it needs more than the pane's height,
    /// and at the height it needs or the pane's, whichever is more, where the pane is scrolled to.
    /// </summary>
    private Rectangle ContentBounds()
    {
        int width = Width - Padding.Horizontal, height = Height - Padding.Vertical, need = NeededHeight(width);
        if (need > height)
        {
            width -= SystemInformation.GetVerticalScrollBarWidthForDpi(DeviceDpi);
            need = NeededHeight(width);
        }
        return new Rectangle(Padding.Left, Padding.Top + AutoScrollPosition.Y, Math.Max(0, width), Math.Max(height, need));
    }

    /// <summary>The pane resized: the content to its new bounds, then the scroll bars follow it.</summary>
    protected override void OnLayout(LayoutEventArgs levent)
    {
        if (Content is { } content) content.Bounds = ContentBounds(); // (none yet while the pane is built)
        base.OnLayout(levent);
    }

    /// <summary>
    /// The content sizes itself in its own layout, before it docks what it holds: a new text or an action shown or hidden
    /// lays it out (it is their parent), and a size set from outside in the middle of that layout would not dock them
    /// again (the green line kept the old height's place, below the content's new bottom). Then the pane's scroll bar
    /// follows the new height.
    /// </summary>
    private sealed class DetailsContent(DetailsPane pane) : Panel
    {
        protected override void OnLayout(LayoutEventArgs levent)
        {
            if (pane.Content != this) // not the pane's content yet: it is being built
            {
                base.OnLayout(levent);
                return;
            }
            var bounds = pane.ContentBounds();
            bool resized = bounds != Bounds;
            if (resized) Bounds = bounds;
            base.OnLayout(levent);
            if (resized) pane.PerformLayout();
        }
    }
}
