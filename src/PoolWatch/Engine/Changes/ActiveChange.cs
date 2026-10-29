using PoolWatch.Core.Changes;

namespace PoolWatch.Engine.Changes;

/// <summary>Every kind of lasting change PoolWatch makes on the PC, each listed under Active changes.</summary>
internal enum ChangeKind { LogonTask, AutoClear, TokenGuard, RemovedClearTask, Service, ScheduledTask, StartupEntry, Shortcut }

/// <summary>
/// Where the Active changes window lists a change: a feature PoolWatch turned on (its button turns it off, or removes
/// it), something PoolWatch turned off (its button puts it back), or another change (a service set to its Windows
/// default, a task PoolWatch turned on; its button puts it back too).
/// </summary>
internal enum ChangeGroup { Feature, TurnedOff, Other }

/// <summary>
/// One row of the Active changes list.
/// </summary>
/// <param name="ChangedUtc">When it was made; null for a change that is a state (a setting that is on, the logon task).</param>
/// <param name="What">The change in plain words, as one line (the uninstaller's summary and list, the command line's list).</param>
/// <param name="CanUndo">False when PoolWatch cannot put it back; <paramref name="Note"/> then says why and what to do.</param>
/// <param name="Note">The caveat shown with the row, or null.</param>
internal sealed record ActiveChange(string Id, ChangeKind Kind, DateTime? ChangedUtc, string What, bool CanUndo, string? Note)
{
    /// <summary>
    /// A warning the user must accept before this change is put back (a clear task whose program sits in a folder they can
    /// change); the app then passes <c>confirmed</c>. Null: no warning. The command line never confirms.
    /// </summary>
    public string? Warning { get; init; }

    /// <summary>
    /// When <see cref="Warning"/> is that the profile applied at start would turn this service off again (and putting it
    /// back takes it out of that profile): the profile and the service. Put all back says that once for all such rows.
    /// </summary>
    public AtStartHold? AtStart { get; init; }

    /// <summary>The window's bold line: what it is ("Empty the standby cache", a service's name). <see cref="What"/> when not set.</summary>
    public string Title { get; init; } = What;

    /// <summary>The window's line under the title: what kind of thing it is and how it was before ("service · was Manual"), or null.</summary>
    public string? Detail { get; init; }

    /// <summary>Where the window lists it; by its kind when not set.</summary>
    public ChangeGroup Group { get; init; } = GroupOf(Kind);

    /// <summary>
    /// Why the uninstaller leaves this change as it is, or null when it puts it back: one PoolWatch cannot put back (its
    /// note says why), and a clear task of your own that needs your yes first (the uninstaller never gives one). Written
    /// in the command line's list (<see cref="ChangeLedger.ToJson"/>), where the uninstaller reads it.
    /// </summary>
    public string? UninstallLeaves =>
        !CanUndo ? Note ?? ChangeWords.CannotPutBack
        : Kind == ChangeKind.RemovedClearTask && Warning is not null ? ChangeWords.UninstallNeedsYourYes
        : null;

    /// <summary>
    /// What stays for a while after this change is put back, or null: the token-leak guard's Windows setting lasts until
    /// you sign out. Written in the command line's list (<see cref="ChangeLedger.ToJson"/>), where the uninstaller reads it
    /// and says it with the changes it puts back; the window says it on the guard's own row.
    /// </summary>
    public string? PutBackCaveat => Kind == ChangeKind.TokenGuard && CanUndo ? ChangeWords.TokenGuardSettingStays : null;

    /// <summary>A feature PoolWatch turned on (the logon task, its settings, its shortcut), or something it turned off.</summary>
    internal static ChangeGroup GroupOf(ChangeKind kind) =>
        kind is ChangeKind.LogonTask or ChangeKind.AutoClear or ChangeKind.TokenGuard or ChangeKind.Shortcut ? ChangeGroup.Feature : ChangeGroup.TurnedOff;

    /// <summary>
    /// The row's button, and what the timeline says it did: a feature is turned off (the shortcut removed); anything else
    /// is put back. A method, not a property, so the command line's list does not carry it.
    /// </summary>
    internal static string ButtonOf(ActiveChange c) =>
        c.Group != ChangeGroup.Feature ? ChangeWords.PutBack : c.Kind == ChangeKind.Shortcut ? ChangeWords.Remove : ChangeWords.TurnOff;
}

/// <summary>A service the profile applied at start lists: <see cref="ActiveChange.AtStart"/>.</summary>
internal sealed record AtStartHold(string Profile, string Service);

/// <param name="Done">The change is no longer in effect: put back now, or nothing was left to put back. False: it still is.</param>
/// <param name="Message">What happened, in plain words.</param>
internal sealed record UndoResult(bool Done, string Message);

/// <summary>Where a kind of change is kept, and how it is put back. Called off the UI thread (COM, the SCM, the registry).</summary>
internal interface IChangeSource
{
    IEnumerable<ActiveChange> List();

    /// <param name="confirmed">The user accepted the change's <see cref="ActiveChange.Warning"/>.</param>
    UndoResult Undo(ActiveChange change, bool confirmed);

    /// <summary>
    /// The row listed in place of this source's changes when <see cref="List"/> fails: it cannot be put back and says so,
    /// so Put all back reports it as not done (and the uninstaller says not everything was put back) instead of leaving it out.
    /// </summary>
    ActiveChange Unread(string error);

    /// <summary>
    /// The order Put all back runs <paramref name="rows"/> in, this source's rows of one kind: as given (newest first),
    /// unless the source needs another (services: one that another depends on first).
    /// </summary>
    IReadOnlyList<ActiveChange> PutBackOrder(IReadOnlyList<ActiveChange> rows) => rows;
}
