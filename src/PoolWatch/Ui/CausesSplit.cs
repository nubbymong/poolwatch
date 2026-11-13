using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PoolWatch.Ui;

/// <summary>
/// Pool causes' split: the legend and the list above, the details below (<see cref="DetailsPane"/>, which scroll). As the
/// window is resized the list keeps its share of the split, <see cref="Share"/>. Where that share leaves the list without
/// one whole row under its header (a small window), the splitter moves down as far as that row needs, where the details
/// then keep their title's first line (<see cref="DetailsLeast"/>); where they would not, it stays at the share, which
/// keeps the details' room for no row gained. A SplitContainer keeps a ratio of its own, but it takes any distance set
/// in code as the new ratio, so this split keeps the share itself. A SplitContainer also paints its splitter while it
/// lays out and as its distance is set (in code, or as the owner lets go of the splitter or of an arrow key that moved
/// it), and that paint can fail in GDI+ while Windows switches desktops (the lock screen, an elevation prompt, a
/// remote session that went away). It then leaves both panels' layout suspended and its own layout half done: the
/// page would stop laying out, and the owner would get .NET's error box. So this split takes that failure itself
/// (<see cref="Recover"/>), GDI+'s own and no other fault (<see cref="IsGdiPlus"/>): it resumes its panels and lays
/// out again two seconds later, until a layout holds, which puts the splitter where it would have been. A move the
/// owner was making as the paint failed is given up: the splitter goes back to where it was.
/// </summary>
internal sealed class CausesSplit : SplitContainer
{
    /// <summary>How long after a layout that failed the split is laid out again, in milliseconds (<see cref="LayOutAgain"/>).</summary>
    internal const int AgainMs = 2000;

    private bool _fitting, _ownerMoving;
    private (int Height, int List, int Details) _fitted = (-1, -1, -1);
    /// <summary>Lays the split out again <see cref="AgainMs"/> after a layout that failed (<see cref="Recover"/>); made only then.</summary>
    private System.Windows.Forms.Timer? _again;

    public CausesSplit()
    {
        Orientation = Orientation.Horizontal;
        // A drag of the splitter raises SplitterMoving first, at the height the split was fitted at; a resize, or a distance
        // set in code, raises only SplitterMoved. The owner's drag is the list's new share.
        SplitterMoving += (_, _) => _ownerMoving = true;
        SplitterMoved += (_, _) =>
        {
            if (_fitting) return;
            bool owner = _ownerMoving && Height > 0 && Height == _fitted.Height;
            _ownerMoving = false;
            if (owner) Share = (SplitterDistance + 0.5) / Height;
        };
    }

    /// <summary>The list's share of the split's height (with the legend above it): the design's, until the owner moves the splitter.</summary>
    public double Share { get; private set; } = 0.58;

    /// <summary>The list under the legend in the upper panel (it measures its own header and rows).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ListView? List { get; set; }

    /// <summary>The lower panel's least height for the details, which scroll for the rest (0: none).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<int>? DetailsLeast { get; set; }

    protected override void OnLayout(LayoutEventArgs e)
    {
        try { base.OnLayout(e); }
        catch (ExternalException failure) when (IsGdiPlus(failure)) { Recover(); return; } // fitted once one holds
        Fit();
    }

    /// <summary>
    /// The owner lets go of the splitter: the base sets the distance the drag reached, which paints the splitter
    /// (<see cref="LetGo"/>).
    /// </summary>
    protected override void OnMouseUp(MouseEventArgs e) => LetGo(() => base.OnMouseUp(e));

    /// <summary>
    /// A key is let go on the splitter: the base sets the distance an arrow key moved it to, which paints the
    /// splitter, and after any key it paints the splitter once more itself (<see cref="LetGo"/>). That last paint is
    /// not part of dotnet/winforms 14565, so this stays on a .NET that has it. No test fails that paint: while the
    /// split has a background picture (the tests' way to fail a paint) the base skips its fill there, and its focus
    /// rectangle does not read the picture. The same catch takes it.
    /// </summary>
    protected override void OnKeyUp(KeyEventArgs e) => LetGo(() => base.OnKeyUp(e));

    /// <summary>
    /// The base's part as the owner lets go of the splitter or of a key, with its paint's failure taken
    /// (<see cref="Recover"/>). After a failure the base is asked once more. It had stored the distance but not ended
    /// the owner's move: left so, the mouse over the splitter would go on moving it, and the layout to come would be
    /// taken for the owner's move. Asked again it finds the distance set, paints nothing for it and ends the move (its
    /// MouseUp or KeyUp event is raised a second time; nothing listens to the split's).
    /// </summary>
    private void LetGo(Action letGo)
    {
        for (int go = 0; go < 2; go++)
        {
            try
            {
                letGo();
                return;
            }
            catch (ExternalException failure) when (IsGdiPlus(failure)) { Recover(); }
        }
    }

    /// <summary>
    /// The failure is GDI+'s own, an ExternalException and no type made from it: what the splitter's paint throws. A
    /// Win32Exception, a COMException or an SEHException is another fault (a control's window that cannot be made,
    /// say) and is let out, as it was before.
    /// </summary>
    private static bool IsGdiPlus(ExternalException failure) => failure.GetType() == typeof(ExternalException);

    /// <summary>
    /// Puts the splitter where <see cref="At"/> says, when the split's height, what the list needs for a row, or the
    /// details' least changed (the window resized, the list's first rows came): from the share after a resize, from where
    /// it is otherwise. When nothing changed it stays, so a move by the owner stays too. The list measures its border and
    /// scroll bar only where it has room for them (squeezed to nothing under the legend it reads none), so once the
    /// splitter has moved it is measured again, from the same start, until the measure holds. Where the splitter's paint
    /// fails as the distance is set (<see cref="Recover"/>), nothing is taken as fitted, and nothing is fitted while
    /// the split waits to lay out again (<see cref="LayoutDue"/>): that layout fits it. A fit before it would find the
    /// distance set already, take that as fitted, and leave the splitter where the layout then puts it back.
    /// </summary>
    public void Fit()
    {
        if (_fitting || LayoutDue || Height <= SplitterWidth) return;
        int from = Height == _fitted.Height ? SplitterDistance : (int)(Height * Share);
        for (int pass = 0; pass < 4; pass++)
        {
            (int Height, int List, int Details) now = (Height, ListLeast(), DetailsLeast?.Invoke() ?? 0);
            if (now == _fitted) return;
            int to = At(from, now.Height - SplitterWidth, now.List, now.Details);
            to = Math.Clamp(to, Panel1MinSize, Math.Max(Panel1MinSize, now.Height - SplitterWidth - Panel2MinSize));
            _fitting = true;
            try
            {
                if (SplitterDistance != to) SplitterDistance = to;
                _fitted = now;
            }
            catch (InvalidOperationException) { return; } // not laid out yet: fitted at its next layout
            catch (ExternalException failure) when (IsGdiPlus(failure)) { Recover(); return; } // fitted by that layout
            finally { _fitting = false; }
        }
    }

    /// <summary>
    /// After the splitter's paint failed: in a layout, as <see cref="Fit"/> set the distance, or as the owner let go
    /// of the splitter or of a key. The base suspends both panels' layout before that paint and resumes it only after
    /// it: once in a layout, and once more where the layout sets the distance. Nothing else suspends these panels, and
    /// a resume of a panel that is not suspended changes nothing, so two resumes of each undo whatever was left, and
    /// lay the panels' own controls out as the base's resumes would have. A drag of the owner's is over: the move the
    /// next layout makes is the split's own, never the list's new share. The split is then drawn again, and laid out
    /// again <see cref="AgainMs"/> later (<see cref="LayOutAgain"/>): not sooner, and not at all where no paint
    /// failed.
    /// </summary>
    private void Recover()
    {
        for (int resume = 0; resume < 2; resume++)
        {
            Panel1.ResumeLayout();
            Panel2.ResumeLayout();
        }
        _ownerMoving = false;
        Invalidate();
        if (_again is null)
        {
            _again = new System.Windows.Forms.Timer { Interval = AgainMs };
            _again.Tick += (_, _) => LayOutAgain();
        }
        _again.Start();
    }

    /// <summary>
    /// A layout failed and the split waits to lay out again: until it has, <see cref="Fit"/> fits nothing.
    /// </summary>
    internal bool LayoutDue => _again is { Enabled: true };

    /// <summary>
    /// For the tests, the wait as the split's timer has it, in milliseconds: its interval (<see cref="AgainMs"/>)
    /// while the split waits to lay out again, 0 once a layout held, and -1 while it has no timer (no paint has
    /// failed).
    /// </summary>
    internal int LayoutDueIn => _again is null ? -1 : _again.Enabled ? _again.Interval : 0;

    /// <summary>
    /// The timer's layout, <see cref="AgainMs"/> after one that failed (most tests call it in the timer's place: no
    /// message loop runs a timer there). It stops the timer first, so this layout's own fit runs (<see cref="Fit"/>),
    /// then lays the split out once, which fits it too. Where that layout fails as well, <see cref="Recover"/> starts
    /// the wait again; where it holds, nothing more is due.
    /// </summary>
    internal void LayOutAgain()
    {
        _again?.Stop();
        PerformLayout();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _again?.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>
    /// The splitter's distance: <paramref name="from"/> (the list's share), or <paramref name="listLeast"/> where the list
    /// needs that for one whole row and <paramref name="room"/> (the split's height less the splitter) still leaves the
    /// details <paramref name="detailsLeast"/>. Never part of the way: that would take the details' room for no row.
    /// </summary>
    internal static int At(int from, int room, int listLeast, int detailsLeast) =>
        from < listLeast && listLeast <= room - detailsLeast ? listLeast : from;

    /// <summary>
    /// The upper panel's height for the list to show its header and one whole row: the legend above it, its border and
    /// sideways scroll bar, and its first shown row's bottom. 0 while the list has no row (nothing to show).
    /// </summary>
    private int ListLeast()
    {
        if (List is not { IsHandleCreated: true, Items.Count: > 0 } list || list.TopItem is not { } top) return 0;
        return list.Top + (list.Height - list.ClientSize.Height) + top.Bounds.Bottom;
    }
}
