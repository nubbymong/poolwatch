using System.Globalization;
using PoolWatch.Core.Services;

namespace PoolWatch.Ui;

/// <summary>What the profile combo can pick: a built-in profile, one of the owner's (<paramref name="AtStart"/>: the one applied at start), or ticking by hand.</summary>
internal sealed record ProfileChoice(ProfileKind Kind, string Name, bool AtStart = false)
{
    public static readonly ProfileChoice Custom = new(ProfileKind.Custom, ServiceProfileRules.Custom);

    /// <summary>The combo's text: "Telemetry only (built-in)", the owner's name ("Build box (at start)" for the one applied at start), "Custom (tick by hand)".</summary>
    public override string ToString() => Kind == ProfileKind.BuiltIn ? $"{Name} (built-in)" : AtStart ? $"{Name} (at start)" : Name;
}

internal enum ProfileKind { BuiltIn, User, Custom }

/// <summary>The words of the Services tab's profile row and its dialogs (unit-tested, so each line is the mockup's).</summary>
internal static class ProfileText
{
    private static string N(int n) => n.ToString(CultureInfo.InvariantCulture);

    private static string Minutes(int n) => $"{N(n)} minute{(n == 1 ? "" : "s")}";

    /// <summary>
    /// The line under the combo: what the chosen profile does (it describes the profile, not the PC: how many of its
    /// services are on now is said apart), and when the ticks now differ from it, how many are ticked.
    /// <paramref name="target"/>: the services it ticks on this PC (a built-in's that are not off yet);
    /// <paramref name="onNow"/>: how many of an owner's profile's services are on now.
    /// </summary>
    public static string Summary(ProfileChoice choice, ServiceProfileSet set, IReadOnlyCollection<string> target, int tickedNow, bool sameAsTarget, int onNow = 0)
    {
        string s;
        switch (choice.Kind)
        {
            case ProfileKind.Custom:
                return $"Custom: {N(tickedNow)} ticked by hand.";
            case ProfileKind.BuiltIn:
                int n = target.Count;
                bool telemetry = ServiceProfileRules.SameName(choice.Name, ServiceProfileRules.TelemetryOnly);
                s = n == 0 ? $"{choice.Name} (built-in, read-only): all of its services are already off."
                    : $"{choice.Name} (built-in, read-only): ticks the {N(n)}{(telemetry ? " telemetry " : " ")}{(n == 1 ? "service that is" : "services that are")} not off yet.";
                break;
            default:
                var p = set.Find(choice.Name);
                int k = p?.Services.Count ?? 0;
                string on = onNow <= 0 ? "" : $"; {N(onNow)} of them {(onNow == 1 ? "is" : "are")} on now";
                s = $"{choice.Name}: keeps {N(k)} service{(k == 1 ? "" : "s")} off{on}. " + (set.IsAtStart(choice.Name)
                    ? $"Applied when PoolWatch starts, after {Minutes(set.DelayMinutes)}."
                    : "Applied only when you press Turn off.");
                break;
        }
        return sameAsTarget ? s : $"{s} {N(tickedNow)} ticked now.";
    }

    /// <summary>
    /// After saving a profile applied at start while some of its services are on: "9 of Build box's services are on now.
    /// PoolWatch turns them off 3 minutes after it next starts. Turn them off now?" (Yes goes through Turn off's own question).
    /// <paramref name="waiting"/>: the run at this start is still waiting to apply this profile, and it turns off what the
    /// profile listed then whatever the answer (<see cref="Engine.ProfileAtStart.Narrowed"/>), so the question says so.
    /// </summary>
    public static string TurnOffNow(string name, int onNow, int delayMinutes, bool waiting = false)
    {
        string them = onNow == 1 ? "it" : "them";
        string still = waiting ? $" It is still waiting to apply {name} from this start, so those it listed then go off when that wait ends, whatever you answer." : "";
        return $"{N(onNow)} of {name}'s services {(onNow == 1 ? "is" : "are")} on now. PoolWatch turns {them} off {Minutes(delayMinutes)} after it next starts.{still} Turn {them} off now?";
    }

    /// <summary>The tooltip of "Save as profile...": it also changes one of the owner's profiles, applying at start included.</summary>
    public const string SaveTip = "Save the ticked services as a profile, or change one of yours: its services, and whether PoolWatch applies it when it starts.";

    /// <summary>
    /// What Put back says when the profile applied at start lists services it turns on: that the profile would turn them
    /// off again, and that Put back takes them out of it so they stay on. Null when it lists none of them.
    /// <paramref name="action"/>: what takes them out ("Put back", "Put all back" with <paramref name="all"/>, or
    /// "Set to its Windows default"), as the buttons say it.
    /// </summary>
    public static string? RestoreKeepsOn(ServiceProfileSet set, IReadOnlyCollection<string> services, bool all = false, string? action = null)
    {
        var (profile, held) = ServiceProfileRules.AtStartHolding(set, services);
        return profile is null ? null : KeepsOn(profile, held, all, action ?? (all ? "Put all back" : "Put back"));
    }

    /// <summary>
    /// "Build box, the profile PoolWatch applies when it starts, turns 2 of these services off again then: SysMain,
    /// MapsBroker. Put all back also takes them out of Build box, so they stay on." (one service, not of a list: "turns SysMain
    /// off again then").
    /// </summary>
    public static string KeepsOn(string profile, IReadOnlyList<string> held, bool all, string action)
    {
        bool one = held.Count == 1;
        string which = all ? $"{N(held.Count)} of these services off again then: {string.Join(", ", held)}" : $"{held[0]} off again then";
        return $"{profile}, the profile PoolWatch applies when it starts, turns {which}. {action} also takes " +
               $"{(one ? "it" : "them")} out of {profile}, so {(one ? "it stays" : "they stay")} on.";
    }

    /// <summary>The note under the save dialog's name, and whether it stops the save.</summary>
    public static (string text, bool error) SaveNameNote(string name, ServiceProfileSet set)
    {
        string n = name.Trim();
        if (n.Length == 0) return ("", true);
        if (ServiceProfileRules.BuiltIns.FirstOrDefault(b => ServiceProfileRules.SameName(b, n)) is { } builtIn) return ($"{builtIn} is a built-in profile: pick another name.", true);
        if (ServiceProfileRules.SameName(n, ServiceProfileRules.Custom)) return ("Pick another name.", true);
        if (ServiceProfileRules.NameProblem(n) is { } why) return (Sentence(why), true);
        if (set.Find(n) is { } p) return ($"Replaces your saved {p.Name} profile.", false);
        if (set.Profiles.Count >= ServiceProfileRules.MaxProfiles) return (Sentence(ServiceProfileRules.TooMany), true);
        return ("", false);
    }

    /// <summary>The save dialog's line on what the profile keeps off.</summary>
    public static string SaveWhat(int kept) => kept > 0
        ? $"Keeps the {N(kept)} ticked service{(kept == 1 ? "" : "s")} off and leaves the others as they are."
        : "Tick the services this profile keeps off first.";

    /// <summary>The save dialog's note when some ticked services cannot be in a profile (only services PoolWatch recognises can).</summary>
    public static string NotKept(int notKept) => notKept <= 0 ? ""
        : $"{N(notKept)} ticked {(notKept == 1 ? "service is" : "services are")} not kept: a profile holds only services PoolWatch recognises.";

    /// <summary>The save dialog's note on applying at start.</summary>
    public const string AtStartNote = "At each start it turns off any of this profile's services that are on, for example after a Windows update. It never touches a protected service.";

    /// <summary>The save dialog's note when saving with "apply at start" takes that from another profile.</summary>
    public static string StopsOther(bool atStart, string name, ServiceProfileSet set) =>
        atStart && set.AtStart is { } other && !ServiceProfileRules.SameName(other, name) && set.Find(other) is { } p
            ? $"Saving this stops {p.Name} being applied at start." : "";

    /// <summary>The note under the rename dialog's name ("" when the name may be taken).</summary>
    public static string RenameNote(string name, string current, ServiceProfileSet set)
    {
        string n = name.Trim();
        if (n.Length == 0) return "";
        if (ServiceProfileRules.BuiltIns.FirstOrDefault(b => ServiceProfileRules.SameName(b, n)) is { } builtIn) return $"{builtIn} is a built-in profile: pick another name.";
        if (ServiceProfileRules.SameName(n, ServiceProfileRules.Custom)) return $"There is already a profile called {ServiceProfileRules.Custom}.";
        if (set.Find(n) is { } p && !ServiceProfileRules.SameName(p.Name, current)) return $"There is already a profile called {p.Name}.";
        return ServiceProfileRules.NameProblem(n) is { } why ? Sentence(why) : "";
    }

    public static string DeleteQuestion(string name, bool atStart = false) =>
        $"Delete the profile {name}? The services stay as they are now; Put back turns back on the ones PoolWatch turned off." +
        (atStart ? " PoolWatch then no longer applies it when it starts." : "");

    public const string SaveTitle = "PoolWatch: save profile", RenameTitle = "PoolWatch: rename profile", DeleteTitle = "PoolWatch: delete profile";

    private static string Sentence(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..] + ".";
}
