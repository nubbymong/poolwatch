using PoolWatch.Core;
using PoolWatch.Core.Impact;
using PoolWatch.Core.Recommendations;
using PoolWatch.Core.Setup;

namespace PoolWatch.Ui.Setup;

/// <summary>A row of the Fixes step.</summary>
internal enum Fix { SignIn, Defender, Shortcut, TokenGuard }

/// <summary>What a Fixes row offers on its right: nothing, a status ("On"), a switch, a Yes/No choice or a button.</summary>
internal enum FixInput { None, Status, Toggle, YesNo, Button }

/// <summary>A web page a "Why?" or "How PoolWatch works" names; it is a link only while PoolWatch may open it (<see cref="Credits.IsAllowedLink"/>).</summary>
internal sealed record SetupLink(string Text, string Url)
{
    public bool Opens => Credits.IsAllowedLink(Url);
}

/// <summary>One row of the Fixes step.</summary>
/// <param name="Why">The text behind its "Why?", when it has one.</param>
/// <param name="WhyOpen">"Why?" starts open: the token-leak guard says what you will notice before you agree to it.</param>
/// <param name="Instructions">The Defender how-to, which its button opens (PoolWatch never changes Defender settings).</param>
internal sealed record FixRow(Fix Fix, SetupIcon Icon, string Title, string Detail, FixInput Input, string? Status = null, string? Why = null,
    IReadOnlyList<SetupLink>? Links = null, bool WhyOpen = false, string? Instructions = null);

/// <summary>A line of the Ready step's summary.</summary>
internal sealed record SummaryRow(SetupIcon Icon, string Key, string Value);

/// <summary>
/// The pages setup names. Their addresses are written out here (a page is a link only while PoolWatch may open it: the
/// credits list it, or it is one of <see cref="Credits.ReferencePages"/>, see
/// <see cref="SetupLink.Opens"/>), so a change to the credits can turn a link into plain text but never stops setup opening.
/// </summary>
internal static class SetupLinks
{
    public static readonly SetupLink TokenLeak = new("The leak, documented by bentoner (GitHub)", "https://github.com/bentoner/windows-token-leak");
    public static readonly SetupLink LogonFix = new("The fix that lasts after sign-in, by gunrgbcr (GitHub)",
        "https://github.com/gunrgbcr/windows-token-leak-logon-fix");
    public static readonly SetupLink ForegroundLock = new("The setting it changes: foreground lock timeout (Microsoft Learn)",
        "https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-systemparametersinfow");
    public static readonly SetupLink ShaderCache = new("The workaround, found by Faarfarin (GitHub)",
        "https://github.com/openai/codex/issues/31212#issuecomment-5635622083");
    public static readonly SetupLink ReFsMemory = new("ReFS and memory use (Microsoft, KB4016173)",
        "https://learn.microsoft.com/troubleshoot/windows-server/backup-and-storage/fix-heavy-memory-usage-refs");
    public static readonly SetupLink DevDriveReport = new("Dev Drive report (Microsoft Windows-Dev-Performance #131)",
        "https://github.com/microsoft/Windows-Dev-Performance/issues/131");
    public static readonly SetupLink RamMap = new("Sysinternals RAMMap (Microsoft)", "https://learn.microsoft.com/sysinternals/downloads/rammap");

    public static IReadOnlyList<SetupLink> All => [TokenLeak, LogonFix, ForegroundLock, ShaderCache, ReFsMemory, DevDriveReport, RamMap];
}

/// <summary>
/// The Fixes step's rows from the scan and what is set up now: starting at sign-in, Defender's performance mode (a how-to
/// only, never applied), the Codex shortcut without its shader cache (a Yes/No whenever the ChatGPT desktop app is
/// installed), and the token-leak guard (only when the leak was found, or while the guard is on; while the check runs, a
/// line that says so). Pure.
/// </summary>
internal static class FixRows
{
    public const string ShortcutTitle = "Codex shortcut without its shader cache";
    public const string ShortcutDetail = "PoolWatch doesn't change the Codex app. The shortcut just starts it without that cache.";
    public const string GuardTitle = "Token-leak guard";
    /// <summary>The guard's row while the check runs: the guard is offered here when the check finds the leak, or later in the app.</summary>
    public const string GuardChecking =
        "PoolWatch is checking this Windows build for the leak (usually about 20 seconds). If it finds it, you can turn the guard on here, or later in the app.";

    /// <summary>The Ready step's token-leak line when the leak was found and the guard is off: where to turn it on.</summary>
    public const string FoundGuardOff = "Found; turn the guard on in the app";

    /// <summary>The guard's "Why?": its reason, then what you will notice and how to undo it, in its own words wherever it is offered.</summary>
    public static readonly string GuardWhy = TokenGuardWords.Why + Environment.NewLine + Environment.NewLine + TokenGuardWords.Details;

    /// <summary>
    /// The guard's "Why?" where no check found the leak (the guard turned on for another reason): its reason without the
    /// sentence that says PoolWatch tested this PC (<see cref="TokenGuardWords.Tested"/>), then what you will notice and how
    /// to undo it.
    /// </summary>
    public static readonly string GuardWhyUntested = TokenGuardWords.Reason + Environment.NewLine + Environment.NewLine + TokenGuardWords.Details;

    /// <param name="shortcutMade">PoolWatch's shortcut is listed under Active changes.</param>
    /// <param name="launcherInstalled">The installer put the shortcut's launcher next to PoolWatch.</param>
    /// <param name="leakFound">
    /// A check found the leak on this Windows build (this session's, or the verdict saved for this build). The guard's
    /// reason says that PoolWatch tested this PC and found it, so a guard that is on says that only then.
    /// </param>
    public static IReadOnlyList<FixRow> From(EnvironmentFacts? f, LeakState leak, bool shortcutMade, bool launcherInstalled, bool leakFound = false)
    {
        if (f is null) return [];
        var rows = new List<FixRow> { SignIn(f) };
        if (ImpactRows.DefenderLevel(f) > ImpactLevel.None && ImpactRows.ScannedFirst(f) is { Count: > 0 } drives)
            rows.Add(new FixRow(Fix.Defender, SetupIcon.Shield, $"Defender performance mode for {SetupText.Drives(drives)}",
                "A Windows setting. PoolWatch shows you where it is.", FixInput.Button,
                Why: "On a trusted Dev Drive, Defender checks a file just after it opens instead of while it opens, so tools that touch thousands " +
                     "of files wait less. Protection stays on, and PoolWatch never changes Defender settings.",
                Instructions: DefenderAdvice.Text(drives)));
        if (f.ChatGptInstalled) rows.Add(Shortcut(shortcutMade, launcherInstalled));
        switch (leak)
        {
            case LeakState.Found: rows.Add(GuardOffer()); break;
            case LeakState.GuardOn: rows.Add(GuardIsOn(leakFound)); break;
            case LeakState.Checking: rows.Add(new(Fix.TokenGuard, SetupIcon.Key, GuardTitle, GuardChecking, FixInput.None)); break;
        }
        return rows;
    }

    private static FixRow SignIn(EnvironmentFacts f) =>
        f.LogonTaskExists ? new(Fix.SignIn, SetupIcon.Power, "Start when you sign in", "PoolWatch starts when you sign in.", FixInput.Status, Status: SetupText.On)
        : f.InstalledInProgramFiles ? new(Fix.SignIn, SetupIcon.Power, "Start when you sign in", "So it watches from the moment you sign in.", FixInput.Toggle)
        : new(Fix.SignIn, SetupIcon.Power, "Start when you sign in", "This needs PoolWatch installed with its installer; it runs from another folder now.", FixInput.None);

    private static FixRow Shortcut(bool made, bool launcher)
    {
        const string why = "Some users found the Codex app uses less of the kernel pool when it starts without its GPU shader disk cache. " +
                           "The shortcut starts it that way, and your usual way of starting it stays as it is.";
        if (made) return new(Fix.Shortcut, SetupIcon.Chat, ShortcutTitle, ShortcutDetail, FixInput.Status, Status: "On your desktop", Why: why, Links: [SetupLinks.ShaderCache]);
        if (!launcher) return new(Fix.Shortcut, SetupIcon.Chat, ShortcutTitle, "This needs PoolWatch installed with its installer.", FixInput.None);
        return new(Fix.Shortcut, SetupIcon.Chat, "Create a Codex shortcut without its shader cache?", ShortcutDetail, FixInput.YesNo, Why: why, Links: [SetupLinks.ShaderCache]);
    }

    private static readonly IReadOnlyList<SetupLink> GuardLinks = [SetupLinks.TokenLeak, SetupLinks.LogonFix, SetupLinks.ForegroundLock];

    /// <summary>The guard offered, in its own words: why, what you will notice, and how to undo it ("Why?" open, so it is read before Next).</summary>
    private static FixRow GuardOffer() =>
        new(Fix.TokenGuard, SetupIcon.Key, GuardTitle, "Found on this PC. The guard turns on the Windows setting that stops it, each time PoolWatch starts.",
            FixInput.Toggle, Why: GuardWhy, Links: GuardLinks, WhyOpen: true);

    /// <summary>
    /// The guard already on: a status, with its "Why?" (what you will notice, how to undo it) and the pages about it. The
    /// sentence that PoolWatch tested this PC and found the leak only where a check did; a guard turned on for another
    /// reason (the leak seen building up, a Windows update since the check) says the rest.
    /// </summary>
    private static FixRow GuardIsOn(bool leakFound) =>
        new(Fix.TokenGuard, SetupIcon.Key, GuardTitle, "It is on. To stop it, turn it off in Active changes.", FixInput.Status, Status: SetupText.On,
            Why: leakFound ? GuardWhy : GuardWhyUntested, Links: GuardLinks);

    /// <summary>The Ready step's summary, from what is in effect now.</summary>
    /// <param name="activeChanges">Every change listed under Active changes, and how many of them can be put back; null while it is read, or when it could not be.</param>
    /// <param name="changesRead">The list was read (or could not be): null then means it could not.</param>
    public static IReadOnlyList<SummaryRow> Summary(Settings running, EnvironmentFacts? f, LeakState leak, bool shortcutMade, (int All, int CanPutBack)? activeChanges,
        bool changesRead = true)
    {
        var rows = new List<SummaryRow> { new(SetupIcon.Both, "Standby cache", CacheChoices.Summary(running)) };
        if (f?.ChatGptInstalled == true) rows.Add(new(SetupIcon.Chat, "Codex shortcut", shortcutMade ? "On your desktop" : "Not created"));
        rows.Add(new(SetupIcon.Key, "Token leak", running.TokenGuardEnabled ? "The guard is on" : leak switch
        {
            LeakState.Found => FoundGuardOff,
            LeakState.Checking => SetupText.CheckingNow,
            _ => "Watching for it",
        }));
        rows.Add(new(SetupIcon.List, "Active changes", ActiveChangesWords(activeChanges, changesRead)));
        return rows;
    }

    /// <summary>"None yet", "1, it can be put back", "3, each can be put back", "3, 2 of them can be put back", "2, none can be put back".</summary>
    public static string ActiveChangesWords((int All, int CanPutBack)? activeChanges, bool changesRead = true) => activeChanges switch
    {
        null => changesRead ? "Listed under Active changes in the app" : "Reading...",
        ({ } all, _) when all <= 0 => "None yet",
        (1, >= 1) => "1, it can be put back",
        (1, _) => "1, it cannot be put back",
        var (all, back) when back >= all => $"{all}, each can be put back",
        var (all, back) when back <= 0 => $"{all}, none can be put back",
        var (all, back) => $"{all}, {back} of them can be put back",
    };
}
