using System.ComponentModel;

namespace PoolWatch.Ui;

/// <summary>
/// Pool causes' split: the legend and the list above, the details below (<see cref="DetailsPane"/>, which scroll). As the
/// window is resized the list keeps its share of the split, <see cref="Share"/>. Where that share leaves the list without
/// one whole row under its header (a small window), the splitter moves down as far as that row needs, where the details
/// then keep their title's first line (<see cref="DetailsLeast"/>); where they would not, it stays at the share, which
/// keeps the details' room for no row gained. A SplitContainer keeps a ratio of its own, but it takes any distance set
/// in code as the new ratio, so this split keeps the share itself.
/// </summary>
internal sealed class CausesSplit : SplitContainer
{
    private bool _fitting, _ownerMoving;
    private (int Height, int List, int Details) _fitted = (-1, -1, -1);

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
        base.OnLayout(e);
        Fit();
    }

    /// <summary>
    /// Puts the splitter where <see cref="At"/> says, when the split's height, what the list needs for a row, or the
    /// details' least changed (the window resized, the list's first rows came): from the share after a resize, from where
    /// it is otherwise. When nothing changed it stays, so a move by the owner stays too. The list measures its border and
    /// scroll bar only where it has room for them (squeezed to nothing under the legend it reads none), so once the
    /// splitter has moved it is measured again, from the same start, until the measure holds.
    /// </summary>
    public void Fit()
    {
        if (_fitting || Height <= SplitterWidth) return;
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
            finally { _fitting = false; }
        }
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
