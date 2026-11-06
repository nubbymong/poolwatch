using System.Globalization;
using PoolWatch.Core.Services;

namespace PoolWatch.Core.Changes;

/// <summary>
/// The Active changes list in plain words: one line per change, as the user would say it (the uninstaller's summary and
/// the command line's list use it), the window's own words for each row (a short title and a detail line), and the notes
/// that go with them. No setting names, no codes (tested). Pure, so the Linux tests cover it.
/// </summary>
public static class ChangeWords
{
    public const string LogonTask = "PoolWatch starts when you sign in";
    /// <summary>The line for PoolWatch's logon task when it is turned off.</summary>
    public const string LogonTaskOff = "PoolWatch's sign-in task is there, turned off";
    /// <summary>The line for PoolWatch's logon task when each of its sign-in triggers is turned off.</summary>
    public const string LogonTaskTriggerOff = "PoolWatch's sign-in task is there, its sign-in trigger turned off";
    /// <summary>The line for PoolWatch's logon task when it has no sign-in trigger.</summary>
    public const string LogonTaskNoTrigger = "PoolWatch's sign-in task is there, without a sign-in trigger";
    /// <summary>The line for PoolWatch's logon task when it runs without highest privileges, which PoolWatch needs at sign-in.</summary>
    public const string LogonTaskNotHighest = "PoolWatch's sign-in task is there, without highest privileges";
    /// <summary>The line for PoolWatch's logon task when it could not be read, or the check at start asks about it.</summary>
    public const string LogonTaskThere = "PoolWatch's sign-in task is there";
    public const string TokenGuard = "The token-leak guard is on";
    /// <summary>
    /// The guard's note in the list: what you notice while it is on, that it is rare, how long it lasts, and what Turn off
    /// does: it stops that and puts back the Windows setting the guard changed in this sign-in, when Windows allows it
    /// (<see cref="TokenGuardOffRefused"/> says when it did not); a setting the guard found already changed stays until you
    /// sign out (<see cref="TokenGuardOffNotOurs"/>).
    /// </summary>
    public const string TokenGuardNote = "While it is on, other programs can bring their windows to the front by themselves and take your keystrokes, " +
                                         "even while you type; most people find that rare and easy to live with. " +
                                         "PoolWatch turns it on again each time it starts. Turn off stops that and puts back the Windows setting it changed " +
                                         "when Windows allows it; one it found already changed stays until you sign out.";
    /// <summary>What Turn off says when it turned the guard off and there was no Windows setting of the guard's to put back.</summary>
    public const string TokenGuardOff = "The token-leak guard is off.";
    /// <summary>What Turn off says when it turned the guard off and put the Windows setting it changed back.</summary>
    public const string TokenGuardOffPutBack = "The token-leak guard is off, and the Windows setting it changed is back as it was.";
    /// <summary>
    /// What Turn off says when Windows would not let PoolWatch put the setting back now (it is still 0): the setting then has
    /// a row of its own in Active changes (<see cref="TokenGuardSettingStill"/>), whose Put back tries again.
    /// </summary>
    public const string TokenGuardOffRefused = "The token-leak guard is off. Windows did not let PoolWatch put its setting back just now: " +
                                               "Active changes lists the setting, and its Put back tries again. Signing out also puts it back.";
    /// <summary>
    /// What Turn off says when Windows did not report the setting, so PoolWatch did not put anything back over it: nothing is
    /// known to list in Active changes then (its row needs the setting reported as still changed).
    /// </summary>
    public const string TokenGuardOffUnreported = "The token-leak guard is off. Windows did not report its setting just now, so PoolWatch did not put it back; " +
                                                  "signing out puts it back.";
    /// <summary>
    /// What the uninstaller says when it turned the guard off but did not put its Windows setting back (Windows refused, or
    /// did not report it): no PoolWatch window lists the setting after an uninstall, so the step is not done, and says why.
    /// </summary>
    public const string TokenGuardSettingNotPutBack = "The guard is off, but Windows did not let PoolWatch put back the Windows setting it changed, or did not report it: " +
                                                      "the setting stays as it is until you sign out.";
    /// <summary>Whether what Turn off or Put back said (<see cref="TokenGuardOffRefused"/>, <see cref="TokenGuardOffUnreported"/>) means the setting was not put back.</summary>
    public static bool SettingNotPutBack(string said) => said is TokenGuardOffRefused or TokenGuardOffUnreported;
    /// <summary>
    /// What Turn off says when the guard found the setting already changed (when PoolWatch started, or when the guard was
    /// turned on later) and has no record of changing it itself in this sign-in: another program may have changed it, so
    /// PoolWatch leaves it as it is.
    /// </summary>
    public const string TokenGuardOffNotOurs = "The token-leak guard is off. The guard found the Windows setting already changed and has no record of changing it itself, " +
                                               "so PoolWatch leaves it as it is until you sign out.";
    /// <summary>What the guard's Turn off button does (its tooltip, shown while the guard is still on).</summary>
    public const string TokenGuardTurnOffTip = "Turns the token-leak guard off and puts back the Windows setting it changed when Windows allows it " +
                                               "(one it found already changed stays until you sign out).";
    /// <summary>What the Put back button of the guard's Windows setting does (its tooltip, shown once the guard is off).</summary>
    public const string TokenGuardSettingPutBackTip = "Puts back the Windows setting the token-leak guard changed. The guard is already off.";
    /// <summary>
    /// What stays for a while once the uninstaller turned the guard off, in a sentence that stands on its own: the uninstaller
    /// shows it under its list and after it put the guard back (<c>putBackCaveat</c> in the command line's list), only when
    /// the guard found its setting already changed and has no record of changing it itself. The setting the guard changed is
    /// put back by the uninstaller's step, as by Turn off, when Windows allows it; when it does not, that step is not done
    /// and says so (<see cref="TokenGuardSettingNotPutBack"/>).
    /// </summary>
    public const string TokenGuardSettingStays = "The token-leak guard's Windows setting stays until you sign out.";
    /// <summary>
    /// The row of the guard's Windows setting while the guard is off but the setting it changed is still in effect: Windows
    /// did not let PoolWatch put it back when the guard was turned off. Its Put back tries again.
    /// </summary>
    public const string TokenGuardSettingStill = "The token-leak guard's Windows setting is still changed";
    public const string TokenGuardSettingStillNote = "The guard is off, but Windows did not let PoolWatch put this setting back when it was turned off. " +
                                                     "Put back tries again; signing out also puts it back.";
    public const string AutoClearKeepsTaskNote = "Your RAMMap task is not put back by this; it has its own row in this list.";
    public const string NotListed = "Not listed: cache clears that already ran (the cache refills by itself) and programs you ended (start them again).";

    // ---------------------------------------------------------------- the Active changes window

    public const string WindowTitle = "Active changes";
    public const string Lead = "What PoolWatch has changed on this PC and is still in effect.";
    public const string FeaturesHeading = "Features you turned on";
    public const string TurnedOffHeading = "Turned off by PoolWatch";
    public const string OtherHeading = "Other changes PoolWatch made";
    public const string NothingActive = "PoolWatch has not changed anything on this PC that is still in effect.";
    public const string PutAllBack = "Put all back";
    /// <summary>A row's button: a feature is turned off, the shortcut removed, anything else put back.</summary>
    public const string TurnOff = "Turn off", Remove = "Remove", PutBack = "Put back";
    /// <summary>
    /// The window's footer when no row says the uninstaller leaves it: each is put back, or the uninstaller says why it
    /// could not be.
    /// </summary>
    public const string UninstallPutsAllBack = "Uninstalling puts each of these back, or says why it could not.";
    /// <summary>The window's footer when a row says uninstalling does not put it back.</summary>
    public const string UninstallPutsBackTheOthers = "Uninstalling puts each of these back, except where marked, or says why it could not.";
    /// <summary>The line on a row PoolWatch cannot put back, before its note (which says why and what to do).</summary>
    public const string CannotPutBack = "PoolWatch cannot put this back.";

    /// <summary>
    /// The caveat of a change PoolWatch cannot put back: <see cref="CannotPutBack"/> and why, or the why alone when it
    /// already says PoolWatch cannot put it back (a clear task's note does), so the row does not say it twice.
    /// </summary>
    public static string CannotPutBackLine(string? why) =>
        string.IsNullOrWhiteSpace(why) ? CannotPutBack
        : why.Contains("cannot put", StringComparison.OrdinalIgnoreCase) ? why
        : CannotPutBack + " " + why;

    /// <summary>The line on a row the uninstaller leaves as it is, before why.</summary>
    public const string UninstallLeaves = "Uninstalling does not put this back:";
    /// <summary>
    /// Why the uninstaller leaves a clear task of your own that needs your yes (it never gives one): in the window's row
    /// and in the uninstaller's own list.
    /// </summary>
    public const string UninstallNeedsYourYes = "PoolWatch puts it back only after your yes in its Active changes window. If you uninstall first, " +
                                                "its definition is saved in PoolWatch's data folder for you to import in Task Scheduler.";

    // ---------------------------------------------------------------- the timeline's line after a row's button

    /// <summary>
    /// The timeline's line after a row's button ran (<paramref name="button"/>: <see cref="TurnOff"/>, <see cref="Remove"/>
    /// or <see cref="PutBack"/>): "Turned off in Active changes: Token-leak guard. The token-leak guard is off. ...", or
    /// "Not put back in Active changes: ..." with why.
    /// </summary>
    public static string Timeline(string button, bool done, string name, string message)
    {
        string past = button switch { TurnOff => "Turned off", Remove => "Removed", _ => "Put back" };
        return $"{(done ? past : "Not " + past.ToLowerInvariant())} in Active changes: {name}. {message}";
    }

    public const string LogonTaskTitle = "Start PoolWatch when you sign in";
    /// <summary>
    /// Not the approved design's "set up by the installer": the installer never makes this task, PoolWatch does when you
    /// say yes (the setup guide, Settings), so the row says what it does instead.
    /// </summary>
    public const string LogonTaskDetail = "PoolWatch starts in the tray each time you sign in";
    /// <summary>The row's detail line for a logon task that is turned off.</summary>
    public const string LogonTaskOffDetail = "It is turned off in Task Scheduler, so PoolWatch does not start when you sign in";
    /// <summary>The row's detail line for a logon task whose sign-in triggers are turned off.</summary>
    public const string LogonTaskTriggerOffDetail = "Its sign-in trigger is turned off in Task Scheduler, so PoolWatch does not start when you sign in";
    /// <summary>The row's detail line for a logon task without a sign-in trigger.</summary>
    public const string LogonTaskNoTriggerDetail = "It has no sign-in trigger in Task Scheduler, so PoolWatch does not start when you sign in";
    /// <summary>The row's detail line for a logon task that runs without highest privileges where PoolWatch needs them.</summary>
    public const string LogonTaskNotHighestDetail = "It runs without highest privileges, so it cannot start PoolWatch when you sign in";
    /// <summary>The note for a logon task whose state is not known (<see cref="Setup.SignInState.NotKnown"/>).</summary>
    public const string LogonTaskStateNotRead = "PoolWatch could not read all of this task's settings. Remove, Put all back and uninstalling delete it all the same.";
    /// <summary>The detail line for a logon task that could not be read, or that the check at start asks about.</summary>
    public const string LogonTaskThereDetail = "A scheduled task named PoolWatch, in Task Scheduler";
    /// <summary>The note for a logon task that the check at start asks about.</summary>
    public const string LogonTaskAskedAbout = "PoolWatch asks about this task at each start (its question and its log say why). Remove deletes it.";
    /// <summary>What Remove says when it deleted a logon task that did not start PoolWatch when you signed in.</summary>
    public const string LogonTaskRemoved = "PoolWatch's sign-in task is deleted.";
    public const string AutoClearTitle = "Empty the standby cache";
    public const string TokenGuardTitle = "Token-leak guard";
    public const string TokenGuardSettingTitle = "Token-leak guard's Windows setting";

    /// <summary>
    /// The shortcut's row, in the approved design's words: PoolWatch makes one shortcut only, on your desktop, for the app
    /// the setup guide calls Codex (the ChatGPT desktop app). Its Remove button's tooltip says a changed one stays.
    /// </summary>
    public const string ShortcutTitle = "Codex shortcut on the desktop";
    public const string ShortcutDetail = "starts Codex without its shader cache";

    /// <summary>A service's title in the window: the name Windows' Services console shows, or its service name when it has no other.</summary>
    public static string ServiceTitle(string displayName, string service) => displayName.Trim().Length > 0 ? displayName : service;

    /// <summary>A startup app's title in the window: "Helper at sign-in".</summary>
    public static string StartupTitle(string name) => name + " at sign-in";

    /// <summary>"every hour, or at 8 GB", "every 30 minutes", "when the kernel pool reaches 8 GB".</summary>
    public static string AutoClearWhen(double everyHours, double poolGb)
    {
        string? every = everyHours > 0 ? Every(everyHours) : null;
        string? size = poolGb > 0 ? Number(poolGb) + " GB" : null;
        return (every, size) switch
        {
            ({ } e, { } s) => $"{e}, or at {s}",
            ({ } e, null) => e,
            (null, { } s) => $"when the kernel pool reaches {s}",
            _ => "on, but with no schedule and no size rule",
        };
    }

    /// <summary>
    /// "service · was Automatic and running", "service · set to its Windows default · was Manual", with the profile that did
    /// it; "service · was Automatic, not known if it was running" for a record whose running state is not known
    /// (<paramref name="runningNotKnown"/>: <see cref="ServiceChange.OriginalRunningNotKnown"/>), never the words of one
    /// that was not running. An original of Disabled says nothing of whether it ran, running, not running or not known, in
    /// either form ("service · was Disabled", "service · set to its Windows default · was Disabled"): Put back sets that
    /// one back to Disabled and stops it if it runs, whatever it did before (<see cref="ServicePlan.StartAfterRestore"/> is
    /// false for it), so "and running" would read as a service Put back starts again.
    /// </summary>
    public static string ServiceDetail(ServiceStartType originalStart, bool originalRunning, bool turnedOff, string? via = null, bool runningNotKnown = false)
    {
        string running = originalStart == ServiceStartType.Disabled ? ""
            : runningNotKnown ? RunningNotKnownWords
            : originalRunning ? " and running" : "";
        string was = $"was {StartWord(originalStart)}{running}";
        string by = via is { } v && v.StartsWith("profile ", StringComparison.Ordinal) ? " · " + v : "";
        return turnedOff ? $"service · {was}{by}" : $"service · set to its Windows default · {was}";
    }

    /// <summary>What a service row adds after its start type when PoolWatch does not know whether it was running.</summary>
    public const string RunningNotKnownWords = ", not known if it was running";

    /// <summary>The start type as Windows' Services console names it.</summary>
    public static string StartWord(ServiceStartType start) => start switch
    {
        ServiceStartType.Automatic => "Automatic",
        ServiceStartType.Manual => "Manual",
        ServiceStartType.Disabled => "Disabled",
        ServiceStartType.Boot => "Boot",
        ServiceStartType.System => "System",
        _ => "unknown",
    };

    /// <summary>The window's title of a recorded change.</summary>
    public static string TitleOf(ChangeRecord r) => r.Kind switch
    {
        RecordKind.RemovedClearTask => $"Your clear task \"{r.Name!.TrimStart('\\')}\"",
        RecordKind.ScheduledTask => r.Location!.TrimStart('\\'),
        RecordKind.StartupEntry => StartupTitle(r.Name!),
        RecordKind.Shortcut => ShortcutTitle,
        _ => Of(r),
    };

    /// <summary>The window's detail line of a recorded change: what it is, and how it was before PoolWatch changed it.</summary>
    public static string DetailOf(ChangeRecord r) => r.Kind switch
    {
        RecordKind.RemovedClearTask => "scheduled task that ran RAMMap · PoolWatch's automatic clear took its place",
        RecordKind.ScheduledTask => $"scheduled task · was {OnOff(r.EnabledBefore == true)}",
        RecordKind.StartupEntry => $"startup app · was {OnOff(r.EnabledBefore == true)}",
        RecordKind.Shortcut => ShortcutDetail,
        _ => "",
    };

    /// <summary>When a change was made, for its detail line ("since 30/10/2026 00:54", in the user's own format).</summary>
    public static string Since(DateTime local, CultureInfo culture) => "since " + local.ToString("g", culture);

    // ---------------------------------------------------------------- the list's lines

    /// <summary>The row in place of changes PoolWatch could not read: "PoolWatch could not read the services it changed".</summary>
    public static string Unread(string what) => "PoolWatch could not read " + what;

    /// <summary>Its note: what that means, then what went wrong.</summary>
    public static string UnreadNote(string error) => "Put all back and uninstalling leave this as it is. " + error;

    /// <summary>"The cache is cleared every hour" / "every 2.5 hours" / "when the kernel pool reaches 8 GB" / both.</summary>
    public static string AutoClear(double everyHours, double poolGb)
    {
        string? every = everyHours > 0 ? Every(everyHours) : null;
        string? size = poolGb > 0 ? Number(poolGb) + " GB" : null;
        return (every, size) switch
        {
            ({ } e, { } s) => $"The cache is cleared {e}, or sooner when the kernel pool reaches {s}",
            ({ } e, null) => $"The cache is cleared {e}",
            (null, { } s) => $"The cache is cleared when the kernel pool reaches {s}",
            _ => "Automatic cache clearing is on, with no schedule and no size rule",
        };
    }

    public static string RemovedClearTask(string taskPath) => $"Replaced your own clear task \"{taskPath.TrimStart('\\')}\" (it ran RAMMap)";

    /// <summary>"Downloaded Maps Manager (MapsBroker)", or the service name alone when it has no other display name.</summary>
    public static string ServiceName(string displayName, string service) =>
        displayName.Length > 0 && !displayName.Equals(service, StringComparison.OrdinalIgnoreCase) ? $"{displayName} ({service})" : service;

    /// <summary>
    /// "Turned off Downloaded Maps Manager (MapsBroker)", with the profile that did it when one did ("... (profile Build
    /// box, at start)"); "Set ... back to its Windows default".
    /// </summary>
    public static string Service(string displayName, string service, bool turnedOff, string? via = null) => turnedOff
        ? $"Turned off {ServiceName(displayName, service)}" + (via is { } v && v.StartsWith("profile ", StringComparison.Ordinal) ? $" ({v})" : "")
        : $"Set {ServiceName(displayName, service)} back to its Windows default";

    /// <summary>The warning before putting back a service the profile applied at start lists: it would be turned off again, so putting it back takes it out.</summary>
    public static string KeptOffAtStart(string profile, string name) =>
        $"{profile}, the profile PoolWatch applies when it starts, turns {name} off again then. Putting it back also takes it out of {profile}, so it stays on.";

    public static string ScheduledTask(string taskPath, bool enabledNow) => $"Turned {OnOff(enabledNow)} the scheduled task {taskPath}";

    public static string StartupEntry(string name, bool enabledNow) => $"Turned {OnOff(enabledNow)} the startup app {name}";

    public static string Shortcut(string path) => $"Created the desktop shortcut \"{ShortcutName(path)}\"";

    /// <summary>The shortcut's name as the desktop shows it (the file name without ".lnk"; any path separator, on any OS).</summary>
    public static string ShortcutName(string path)
    {
        string name = path[(path.LastIndexOfAny(['\\', '/']) + 1)..];
        return name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    /// <summary>The line for a recorded change (a record that passed <see cref="ChangeRecordFile.Invalid"/>).</summary>
    public static string Of(ChangeRecord r) => r.Kind switch
    {
        RecordKind.RemovedClearTask => RemovedClearTask(r.Name!),
        RecordKind.ScheduledTask => ScheduledTask(r.Location!, !r.EnabledBefore!.Value),
        RecordKind.StartupEntry => StartupEntry(r.Name!, !r.EnabledBefore!.Value),
        RecordKind.Shortcut => Shortcut(r.Path!),
        _ => "A change this PoolWatch does not know",
    };

    private static string OnOff(bool on) => on ? "on" : "off";

    private static string Every(double hours)
    {
        if (hours < 1) return $"every {Number(hours * 60)} minutes";
        string h = Number(hours);
        return h == "1" ? "every hour" : $"every {h} hours";
    }

    private static string Number(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
}
