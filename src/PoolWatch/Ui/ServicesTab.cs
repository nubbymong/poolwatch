using PoolWatch.Analysis;
using PoolWatch.Core.Impact;
using PoolWatch.Core.Services;
using PoolWatch.Engine;

namespace PoolWatch.Ui;

/// <summary>
/// When the Services tab reads its list again, when its buttons wait, and what its status line says (pure, so it is
/// tested). Two jobs can overlap: a read of the list and a change (Turn off, Put back, Set to its Windows default). The
/// buttons wait while either runs. A read starts when the list was never read, when it is asked for (Refresh, the end of
/// a change) or when PoolWatch's record moved on (a profile at start, an Undo in Active changes), never while a read or
/// a change runs: a change moves the record as it saves each service, and its end reads the list again anyway.
/// </summary>
internal sealed class ServicesTabState
{
    public const string ReadingText = "Reading services…", ApplyingText = "Applying…";

    private bool _stale = true;
    private int _version = -1;

    public bool Reading { get; private set; }

    /// <summary>How many changes run now: a second one (Save as profile's "turn them off now" during a Turn off) can start
    /// before the first ends, and the buttons wait until both have.</summary>
    private int _applying;

    public bool Applying => _applying > 0;

    /// <summary>The buttons wait: a read or a change runs.</summary>
    public bool Busy => Reading || Applying;

    /// <summary>A refresh reads the list again now (the record is at <paramref name="version"/>).</summary>
    public bool NeedsRead(int version) => !Reading && !Applying && (_stale || version != _version);

    /// <summary>A read starts, of the record at <paramref name="version"/>.</summary>
    public void ReadStarted(int version) => (Reading, _version, _stale) = (true, version, false);

    public void ReadFinished() => Reading = false;

    public void ApplyStarted() => _applying++;

    /// <summary>A change ended: the list is read again once every change running has ended.</summary>
    public void ApplyFinished() => (_applying, _stale) = (Math.Max(0, _applying - 1), true);

    /// <summary>Refresh: the list is read again.</summary>
    public void Stale() => _stale = true;

    /// <summary>The status line: what runs now (a change first), else <paramref name="idle"/> (a read that failed, a record that cannot be read, or nothing).</summary>
    public string Status(string idle) => Applying ? ApplyingText : Reading ? ReadingText : idle;
}

/// <summary>
/// The Services tab at work (moved out of the main window, which only adds its page): it reads the services in the
/// background when the tab shows, and again after a change, on Refresh, or when the record of PoolWatch's changes moved
/// on (a profile at start, an Undo in Active changes), never during a change (<see cref="ServicesTabState"/>); while the
/// tab shows it keeps the Cost cells up to date. It runs Turn off, Put back and Set to its Windows default with their
/// questions, each off the UI thread, then reads the list again. The profile row is <see cref="ProfilePicker"/>'s, the
/// list's filter the toolbar's (<see cref="ServicesTabView"/>), and the buttons follow <see cref="ServicesPageText.Buttons"/>.
/// A change the command line saved, in its own process, shows after Refresh.
/// </summary>
internal sealed class ServicesTab
{
    private readonly Form _owner;
    private readonly AnalysisPipeline _analysis;
    private readonly UiActions _actions;
    private readonly ProfileStore _profileStore;
    private readonly ProfilePicker _profiles;
    private readonly ServicesTabState _state = new();
    private List<ServiceOption> _options = new();

    /// <summary>The status line while nothing runs: a read that failed, or a record that cannot be read; "" otherwise.</summary>
    private string _idleStatus = "";

    /// <param name="agentApplied">Told once an AI agent's answer was applied (its review closed after Apply ran).</param>
    public ServicesTab(Form owner, ToolTip tips, SetupContext context, Action? agentApplied = null)
    {
        _owner = owner;
        _analysis = context.Analysis;
        _actions = context.Actions;
        View = new ServicesTabView(owner, tips);
        _profileStore = new ProfileStore(context.Live);
        _profiles = new ProfilePicker(View.Profile, View.SaveProfile, View.RenameProfile, View.DeleteProfile, View.Summary, View.Services,
            _profileStore, new ProfileDialogs(owner), (what, a) => _actions.Guarded(owner, what, a))
        {
            ApplyNow = (services, via) => _actions.Guarded(owner, "Turn off services", () => Apply(services, via)),
            WaitingAtStart = () => Services.WaitingAtStart,
        };
        // A service a Put back or an Undo (here or in Active changes) turned on is unticked, so Turn off does not turn it off again.
        Services.TurnedOn += TurnedOn;
        owner.Disposed += (_, _) => Services.TurnedOn -= TurnedOn;

        void On(Button b, string what, Action a) => b.Click += (_, _) => _actions.Guarded(owner, what, a);
        void OnItem(ToolStripItem i, string what, Action a) => i.Click += (_, _) => _actions.Guarded(owner, what, a);
        On(View.TurnOff, "Turn off services", () => Apply(View.Services.Ticked, _profiles.Via));
        On(View.Refresh, "Refresh services", () => { _state.Stale(); Refresh(); });
        OnItem(View.PutBackSelected, "Put back service", RestoreSelected);
        OnItem(View.ToWindowsDefault, "Set to Windows default", RestoreSelectedToDefault);
        OnItem(View.PutAllBack, "Put all back", () => DoRestore(null));
        View.Guard = (what, a) => _actions.Guarded(owner, what, a); // the toolbar's filter
        View.UseAgent(new AgentDecisions(owner, context, applied: agentApplied)); // "Decide with your AI agent" and "Import answer"
        View.Services.TicksChanged += (_, _) => ShowButtons();
        View.Services.SelectedIndexChanged += (_, _) => ShowButtons();
        ShowButtons();
    }

    public ServicesTabView View { get; }
    public TabPage Page => View.Page;

    /// <summary>The services as last read (none until the tab first showed).</summary>
    public IReadOnlyList<ServiceOption> Options => _options;

    /// <summary>The list has read the services (CI waits for it).</summary>
    public bool Loaded => _options.Count > 0;

    /// <summary>Every service as last read, the filter's hidden ones too (CI; as <see cref="TasksTab.Rows"/> has every row).</summary>
    public int Rows => _options.Count;

    private ServicesController Services => _analysis.Diagnostics.Services;

    /// <summary>
    /// Every refresh while the tab shows: the list is read (in the background) when <see cref="ServicesTabState.NeedsRead"/>
    /// says so; otherwise only the Cost cells change.
    /// </summary>
    public void Refresh()
    {
        int version = Services.Version;
        if (!_state.NeedsRead(version)) { View.Services.UpdateCosts(CostCell); return; }
        _state.ReadStarted(version);
        ShowState();
        Task.Run(() => Services.Options()).ContinueWith(t =>
        {
            if (!_owner.IsHandleCreated || _owner.IsDisposed) return;
            try
            {
                _owner.BeginInvoke(() =>
                {
                    _state.ReadFinished();
                    if (t.IsFaulted)
                    {
                        _idleStatus = ServicesPageText.ReadFailed(t.Exception!.GetBaseException().Message);
                        ShowState();
                    }
                    else Show(t.Result);
                });
            }
            catch (InvalidOperationException) { } // the window is closing
        });
    }

    private void Show(List<ServiceOption> options)
    {
        _options = options;
        // The list keeps its ticks (a folded group's too), its selection and its place; the count line follows it.
        View.Services.SetRows(options.Select(o => ServiceRows.RowOf(o, CostCell(o))).ToList());
        _profiles.RowsShown();
        _idleStatus = ServicesPageText.RecordProblem(Services.RecordError) ?? "";
        ShowState();
    }

    /// <summary>The status line and the buttons as <see cref="_state"/> has them now (a read ending during a change still says "Applying…").</summary>
    private void ShowState()
    {
        string status = _state.Status(_idleStatus);
        if (View.Status.Text != status) View.Status.Text = status;
        ShowButtons();
    }

    /// <summary>The buttons as the ticks, the selection and PoolWatch's record have them now.</summary>
    private void ShowButtons()
    {
        var (change, _) = ServicesPageText.ApplyPlan(View.Services.Ticked, _options);
        View.ShowButtons(ServicesPageText.Buttons(_state.Busy, change.Count, Services.Changes.Count, Services.RecordError, SelectedService()));
    }

    /// <summary>
    /// The Cost cell: the live CPU and page faults of the service's process with its level (a shared host says so), or,
    /// for a service PoolWatch turned off, what that saved over the hour after the change; "measuring…" until PoolWatch
    /// has its first sampling window.
    /// </summary>
    private (string Text, ImpactLevel? Level) CostCell(ServiceOption o)
    {
        var d = _analysis.Diagnostics;
        return CostCellOf(o, o.PoolWatchChange is not null ? Services.SavingsText(o.Service.Name) : null, d.CostOfPid, d.Load);
    }

    /// <summary>
    /// <see cref="CostCell"/> from what it reads: what turning it off saved (<paramref name="saved"/>, for a service PoolWatch
    /// turned off), the cost of a process now (<paramref name="costOfPid"/>), and the latest sampling window
    /// (<paramref name="load"/>; none yet: "measuring…").
    /// </summary>
    internal static (string Text, ImpactLevel? Level) CostCellOf(ServiceOption o, string? saved, Func<int, (double cores, long faultsPerSec)?> costOfPid,
        ProcessLoad.Rates? load)
    {
        if (o.PoolWatchChange is not null && saved is not null) return (saved, null);
        var live = o.Service.Running && o.Service.ProcessId > 0 && costOfPid(o.Service.ProcessId) is { } c
            ? o.Service with { Cores = c.cores, PageFaultsPerSec = c.faultsPerSec } : o.Service;
        return ServiceRows.CostOf(live, measured: load is not null);
    }

    /// <summary>
    /// Turns off <paramref name="ticked"/> after the question, which lists only the services it changes: those off
    /// already are left as they are, and said in a line.
    /// </summary>
    private void Apply(IReadOnlyList<string> ticked, string via)
    {
        if (ticked.Count == 0) { MessageBox.Show(_owner, "Tick the services to turn off first.", "PoolWatch"); return; }
        var (change, offAlready) = ServicesPageText.ApplyPlan(ticked, _options);
        if (change.Count == 0) { MessageBox.Show(_owner, ServicesPageText.AllOffAlready(ticked.Count), "PoolWatch"); return; }
        bool Unknown(string n) => _options.FirstOrDefault(o => o.Service.Name.Equals(n, StringComparison.OrdinalIgnoreCase))?.Group == ServiceGroup.Unknown;
        if (MessageBox.Show(_owner, ServicesPageText.ApplyQuestion(change, offAlready, ServicesPageText.ToManual(change, _options)), "PoolWatch: turn off services",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        if (change.Any(Unknown) && MessageBox.Show(_owner, "Some are services of other apps that PoolWatch does not recognise. Turn them off too?", "PoolWatch",
                MessageBoxButtons.YesNo) != DialogResult.Yes)
            change = change.Where(n => !Unknown(n)).ToList();
        Run(() => Services.Apply(change, via));
    }

    /// <summary>
    /// Put all back (<paramref name="service"/> null) or one service: while PoolWatch's record cannot be read, the
    /// controller's Restore says why, never "nothing to put back".
    /// </summary>
    private void DoRestore(string? service)
    {
        var services = Services;
        if (ServicesPageText.StopsBeforeRestore(service, services) is { } nothing) { MessageBox.Show(_owner, nothing, "PoolWatch"); return; }
        // Services the profile applied at start lists, which Put back turns on: they stay on only out of it, so it asks
        // first (also for one service whose row was read before its change was saved). Only the services the question
        // names come out of the profile; one turned off after it was asked stays in it, and the result says so.
        var (profile, held) = ServiceProfileRules.AtStartHolding(_profileStore.Current, OnOnceRestored(services, service));
        IReadOnlyList<string> named = [];
        if (profile is not null)
        {
            string keep = ProfileText.KeepsOn(profile, held, all: service is null, service is null ? "Put all back" : "Put back");
            if (MessageBox.Show(_owner, keep + (service is null ? "\n\nPut all back?" : $"\n\nPut {service} back?"), service is null ? "PoolWatch: put all back" : "PoolWatch: put back a service",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            named = held; // exactly the services the question named
        }
        Run(() => _profileStore.RestoreKeepingOn(services, service, named), ServicesPageText.NothingToRestoreText(service));
    }

    /// <summary>The services (all, or <paramref name="service"/>) that Put back turns on (not those it puts back to off).</summary>
    private static List<string> OnOnceRestored(ServicesController services, string? service) =>
        services.Changes.Where(c => (service is null || c.Service.Equals(service, StringComparison.OrdinalIgnoreCase))
                                    && ServicePlan.OnAfterRestore(c, services.Catalogue.Find(c.Service))).Select(c => c.Service).ToList();

    /// <summary>The first selected service (a selected group header is not one).</summary>
    private ServiceOption? SelectedService() =>
        View.Services.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag).OfType<ServiceOption>().FirstOrDefault();

    /// <summary>Put back the selected service: exactly what PoolWatch saved before changing it (and start it if it was running).</summary>
    private void RestoreSelected()
    {
        if (SelectedService() is not { } o) { MessageBox.Show(_owner, "Select a service first.", "PoolWatch"); return; }
        if (o.PoolWatchChange is not { } c)
        {
            // Not in the record as last read: nothing to put back, unless the record could not be read (Put back then says why).
            DoRestore(o.Service.Name);
            return;
        }
        string again = ServicePlan.StartAfterRestore(c) ? " and start it again (it was running before)" : "";
        string keep = ServicePlan.OnAfterRestore(c, o.Catalogue) && ProfileText.RestoreKeepsOn(_profileStore.Current, [o.Service.Name], action: "Put back") is { } k ? "\n\n" + k : "";
        if (MessageBox.Show(_owner, $"Put {o.Service.Name} back to {c.OriginalStart}{again}?{keep}", "PoolWatch: put back a service", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        var services = Services;
        IReadOnlyList<string> named = keep.Length > 0 ? [o.Service.Name] : [];
        Run(() => _profileStore.RestoreKeepingOn(services, o.Service.Name, named));
    }

    /// <summary>"Set the selected service to its Windows default", for a service that is off outside PoolWatch (through the same gate; it can be put back).</summary>
    private void RestoreSelectedToDefault()
    {
        if (SelectedService() is not { } o) { MessageBox.Show(_owner, "Select a service first.", "PoolWatch"); return; }
        if (!o.CanRestoreDefault)
        {
            string why = o.Group == ServiceGroup.Protected ? "it is a protected service"
                : o.PoolWatchChange is not null ? "PoolWatch changed it: use Put back"
                : o.OffOutsidePoolWatch != ServicePlan.OffOutside ? "it is not off outside PoolWatch"
                : "PoolWatch does not know its Windows default start type (set it in services.msc)";
            MessageBox.Show(_owner, $"{o.Service.Name}: not offered: {why}.", "PoolWatch");
            return;
        }
        // A default that is on would be turned off again by the profile applied at start, if it lists the service: it is said,
        // and on OK the service comes out of that profile.
        string keep = ServicePlan.OnAfterDefault(o)
            && ProfileText.RestoreKeepsOn(_profileStore.Current, [o.Service.Name], action: "Set to its Windows default") is { } k ? "\n\n" + k : "";
        if (MessageBox.Show(_owner, $"Set {o.Service.Name} back to its Windows default ({o.WindowsDefault})?\n\nIt starts at the next boot or when something asks for it. " +
                $"Your current setting (Disabled) is saved first, so you can put it back.{keep}", "PoolWatch: Windows default", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        var services = Services;
        bool named = keep.Length > 0;
        Run(() => _profileStore.DefaultKeepingOn(services, o, named));
    }

    /// <summary>
    /// Runs a service change off the UI thread (it can take seconds; the buttons wait, and no refresh reads the list while
    /// it runs), then reads the list again and says what was done.
    /// </summary>
    /// <param name="none">What to say when it changed nothing at all.</param>
    private void Run(Func<List<(string service, string? error)>> op, string none = "Nothing was changed.")
    {
        _state.ApplyStarted();
        ShowState();
        Task.Run(op).ContinueWith(t =>
        {
            if (!_owner.IsHandleCreated || _owner.IsDisposed) return;
            try
            {
                _owner.BeginInvoke(() =>
                {
                    _state.ApplyFinished();
                    Refresh(); // the list is read again: its buttons wait for that read
                    string msg = t.IsFaulted ? t.Exception!.GetBaseException().Message : ServicesPageText.ResultMessage(t.Result, none);
                    MessageBox.Show(_owner, msg, "PoolWatch: services");
                });
            }
            catch (InvalidOperationException) { } // the window is closing
        });
    }

    /// <summary>Any thread: services a Put back, an Undo or "Set to its Windows default" turned on, unticked on the UI thread.</summary>
    private void TurnedOn(IReadOnlyList<string> services)
    {
        if (!_owner.IsHandleCreated || _owner.IsDisposed) return; // never shown: nothing is ticked
        try { _owner.BeginInvoke(() => _profiles.TurnedOn(services)); }
        catch (InvalidOperationException) { } // the window is closing
    }
}
