using PoolWatch.Core.Services;
using PoolWatch.Engine;

namespace PoolWatch.Ui;

/// <summary>
/// The Services tab's profile row: the combo (the built-in profiles, the owner's own, then "Custom (tick by hand)"), its
/// Save as profile..., Rename... and Delete buttons, and the line under it. Picking a profile only ticks its services on
/// the list (nothing changes until Apply); a built-in ticks the catalogue's services that are not off yet. Rename and
/// Delete are for the owner's own profiles only; deleting one leaves every service as it is.
/// </summary>
internal sealed class ProfilePicker
{
    private readonly ComboBox _combo;
    private readonly Button _rename, _delete;
    private readonly Control _summary;
    private readonly ServiceListView _list;
    private readonly ProfileStore _store;
    private readonly IProfileDialogs _ask;
    private bool _filling;
    private bool _firstRows = true;
    /// <summary>The ticks made by hand under Custom when another entry was picked: Custom brings them back.</summary>
    private List<string>? _handTicks;
    private ProfileKind _shown = ProfileKind.Custom;

    /// <param name="guard">Runs each of the row's actions (the window shows an error instead of throwing); null: as it is.</param>
    public ProfilePicker(ComboBox combo, Button save, Button rename, Button delete, Control summary, ServiceListView list, ProfileStore store, IProfileDialogs ask,
        Action<string, Action>? guard = null)
    {
        _combo = combo;
        _rename = rename;
        _delete = delete;
        _summary = summary;
        _list = list;
        _store = store;
        _ask = ask;
        guard ??= (_, a) => a();
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        combo.AccessibleName = "Profile";
        combo.SelectedIndexChanged += (_, _) => { if (!_filling) guard("Profile", Picked); };
        save.Click += (_, _) => guard("Save as profile", SaveAs);
        rename.Click += (_, _) => guard("Rename profile", Rename);
        delete.Click += (_, _) => guard("Delete profile", Delete);
        list.TicksChanged += (_, _) => UpdateSummary();
        Fill(null);
    }

    public ProfileChoice Selected => _combo.SelectedItem as ProfileChoice ?? ProfileChoice.Custom;

    /// <summary>
    /// Called for "Turn them off now" after a save: the window's Apply, with its own question, for exactly the profile's
    /// services that are on now and with the profile's Via.
    /// </summary>
    public Action<IReadOnlyList<string>, string>? ApplyNow { get; set; }

    /// <summary>
    /// The profile the run at this start is still waiting to apply, or null (<see cref="ServicesController.WaitingAtStart"/>):
    /// "Turn them off now?" for that profile says the run turns off what it listed then, whatever the answer.
    /// </summary>
    public Func<string?>? WaitingAtStart { get; set; }

    /// <summary>
    /// The Via of an Apply from the tab: the built-in's name, "profile NAME" for the owner's, else "manual"; ", edited"
    /// when the ticks were changed by hand from the profile's.
    /// </summary>
    public string Via
    {
        get
        {
            var choice = Selected;
            if (choice.Kind == ProfileKind.Custom) return "manual";
            string via = choice.Kind == ProfileKind.User ? $"profile {choice.Name}" : choice.Name;
            return SameAsTarget(Target()) ? via : via + ", edited";
        }
    }

    private bool SameAsTarget(IReadOnlyCollection<string> target)
    {
        var ticked = _list.Ticked;
        return target.Count == ticked.Count && target.All(_list.IsTicked);
    }

    /// <summary>The combo's entries again, the named one selected (else Custom), without ticking anything.</summary>
    private void Fill(string? select)
    {
        _filling = true;
        try
        {
            _combo.BeginUpdate();
            _combo.Items.Clear();
            foreach (var b in ServiceProfileRules.BuiltIns) _combo.Items.Add(new ProfileChoice(ProfileKind.BuiltIn, b));
            foreach (var p in _store.Current.Profiles) _combo.Items.Add(new ProfileChoice(ProfileKind.User, p.Name, _store.Current.IsAtStart(p.Name)));
            _combo.Items.Add(ProfileChoice.Custom);
            _combo.EndUpdate();
            int index = _combo.Items.Count - 1; // Custom
            for (int i = 0; select is not null && i < _combo.Items.Count; i++)
                if (_combo.Items[i] is ProfileChoice { Kind: ProfileKind.User } c && ServiceProfileRules.SameName(c.Name, select)) { index = i; break; }
            _combo.SelectedIndex = index;
        }
        finally { _filling = false; }
        _shown = Selected.Kind;
        UpdateSummary();
    }

    /// <summary>
    /// The list has read the services (after a load, Refresh or Apply). The first time, the profile applied at start is
    /// picked and its services ticked, so the tab opens on what PoolWatch keeps off; later reads keep the ticks.
    /// </summary>
    public void RowsShown()
    {
        if (_firstRows && _list.Rows.Count > 0)
        {
            _firstRows = false;
            if (_store.Current.AtStart is { } start && _store.Current.Find(start) is { } p)
            {
                Fill(p.Name);
                Picked();
                return;
            }
        }
        UpdateSummary();
    }

    /// <summary>The services the selected profile ticks on this PC (none for Custom).</summary>
    public IReadOnlyCollection<string> Target()
    {
        var options = _list.Rows.Select(r => r.Option).ToList();
        var choice = Selected;
        if (choice.Kind == ProfileKind.BuiltIn)
            return ServicePlan.Tick(ServiceProfileRules.SameName(choice.Name, ServiceProfileRules.TelemetryOnly) ? Preset.TelemetryOnly : Preset.AgenticCodingPc, options);
        if (choice.Kind == ProfileKind.User && _store.Current.Find(choice.Name) is { } p)
        {
            var tickable = options.Where(o => o.CanTurnOff).Select(o => o.Service.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return p.Services.Where(tickable.Contains).ToList();
        }
        return [];
    }

    /// <summary>
    /// A profile picked in the combo: its services are ticked. Leaving Custom keeps its hand ticks, and picking Custom again
    /// brings them back; with none made by hand, Custom leaves the ticks as they are.
    /// </summary>
    private void Picked()
    {
        if (_shown == ProfileKind.Custom && Selected.Kind != ProfileKind.Custom) _handTicks = _list.Ticked;
        if (Selected.Kind != ProfileKind.Custom) _list.SetTicks(Target());
        else if (_handTicks is { Count: > 0 } hand) _list.SetTicks(hand);
        _shown = Selected.Kind;
        UpdateSummary();
    }

    /// <summary>
    /// Services a Restore, an Undo or "Restore to Windows default" turned on: unticked (and dropped from the hand ticks
    /// Custom brings back), so the next Apply does not turn them off again; the line under the combo follows.
    /// </summary>
    public void TurnedOn(IReadOnlyCollection<string> services)
    {
        var on = services.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _handTicks?.RemoveAll(on.Contains);
        var ticked = _list.Ticked;
        if (ticked.Any(on.Contains)) _list.SetTicks(ticked.Where(t => !on.Contains(t)).ToList());
    }

    /// <summary>An owner's profile's services that are on now (installed, may be turned off, and not as Apply leaves them).</summary>
    private List<string> OnNow(string profile)
    {
        if (_store.Current.Find(profile) is not { } p) return [];
        var listed = p.Services.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _list.Rows.Where(r => listed.Contains(r.Name) && r.Tickable && !ServicePlan.IsOff(r.Option)).Select(r => r.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void UpdateSummary()
    {
        var choice = Selected;
        var ticked = _list.Ticked;
        var target = Target();
        bool same = choice.Kind == ProfileKind.Custom || SameAsTarget(target);
        string text = ProfileText.Summary(choice, _store.Current, target, ticked.Count, same, choice.Kind == ProfileKind.User ? OnNow(choice.Name).Count : 0);
        if (_summary.Text != text) _summary.Text = text;
        bool own = choice.Kind == ProfileKind.User;
        _rename.Enabled = own;
        _delete.Enabled = own;
    }

    public void SaveAs()
    {
        var set = _store.Current;
        var choice = Selected;
        bool own = choice.Kind == ProfileKind.User && set.Find(choice.Name) is not null;
        var ticked = _list.Ticked;
        var kept = ticked.Where(n => DefaultCatalogue.Shared.Find(n) is not null && !ServiceCatalogue.IsProtected(n, insiderEnrolled: true)).ToList();
        var request = new SaveRequest(own ? choice.Name : "", own && set.IsAtStart(choice.Name), own ? set.DelayMinutes : ServiceProfileRules.DefaultDelayMinutes,
            kept.Count, ticked.Count - kept.Count, set);
        if (_ask.AskSave(request) is not { } answer) return;
        try { _store.Save(answer.Name, kept, answer.AtStart, answer.DelayMinutes); }
        catch (Exception ex) { _ask.Tell("PoolWatch could not save the profile: " + ex.Message); return; }
        Fill(answer.Name);
        // Applied at start, it would turn its services that are on off at the next start, without Apply's question: it
        // offers to do that now, through Turn off, for exactly those services (not the other ticks, nor the ones off already).
        if (answer.AtStart && _store.Current.Find(answer.Name) is { } saved && OnNow(saved.Name) is { Count: > 0 } on && ApplyNow is { } apply
            && _ask.AskYesNo(ProfileText.TurnOffNow(saved.Name, on.Count, Math.Clamp(answer.DelayMinutes, ServiceProfileRules.MinDelayMinutes, ServiceProfileRules.MaxDelayMinutes),
                   WaitingAtStart?.Invoke() is { } waiting && ServiceProfileRules.SameName(waiting, saved.Name)), ProfileText.SaveTitle))
            apply(on, $"profile {saved.Name}");
    }

    public void Rename()
    {
        if (Selected is not { Kind: ProfileKind.User } choice) return;
        if (_ask.AskRename(choice.Name, _store.Current) is not { Length: > 0 } name) return;
        try { _store.Rename(choice.Name, name); }
        catch (Exception ex) { _ask.Tell("PoolWatch could not rename the profile: " + ex.Message); return; }
        Fill(name);
    }

    public void Delete()
    {
        if (Selected is not { Kind: ProfileKind.User } choice || !_ask.AskDelete(choice.Name, _store.Current.IsAtStart(choice.Name))) return;
        try { _store.Delete(choice.Name); }
        catch (Exception ex) { _ask.Tell("PoolWatch could not delete the profile: " + ex.Message); return; }
        Fill(null);
    }
}
