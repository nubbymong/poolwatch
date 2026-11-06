using System.Globalization;
using System.Text.Json;

namespace PoolWatch.Core.TokenGuard;

/// <summary>
/// What the last logon's leak test (or decision) found on a Windows build. <see cref="Interrupted"/>: a leak test on that
/// build did not finish (PoolWatch was stopped during it), so the workaround goes on at each logon on that build, without
/// a test, until a test that finishes saves a verdict.
/// </summary>
public enum GuardResult { Leak, CleanOnce, Fixed, Unclear, Interrupted }

public enum LeakVerdict { Leak, Clean, Unclear }

/// <summary>
/// The token-leak guard's memory between logons: the build it last looked at (CurrentBuild.UBR), what it
/// found, when, and a one-line detail. Kept in PoolWatch's cache folder.
/// </summary>
public sealed record GuardState(string Build, GuardResult Result, DateTime TestedUtc, string Detail)
{
    public static string ResultText(GuardResult r) => r switch
    {
        GuardResult.Leak => "leak", GuardResult.CleanOnce => "clean-once", GuardResult.Fixed => "fixed", GuardResult.Interrupted => "interrupted", _ => "unclear",
    };

    public static GuardResult? ParseResult(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "leak" => GuardResult.Leak, "clean-once" or "clean_once" or "cleanonce" => GuardResult.CleanOnce,
        "fixed" => GuardResult.Fixed, "unclear" => GuardResult.Unclear, "interrupted" => GuardResult.Interrupted, _ => null,
    };

    public string ToJson() => JsonSerializer.Serialize(new Dictionary<string, string>
    {
        ["build"] = Build, ["result"] = ResultText(Result), ["tested"] = TestedUtc.ToString("o", CultureInfo.InvariantCulture), ["detail"] = Detail,
    }, new JsonSerializerOptions { WriteIndented = true });

    /// <summary>
    /// Reads the state file (four keys: build, result, tested, detail; names in any case, the build as a string or a
    /// number). Null when the file is unreadable or has no usable build / result.
    /// </summary>
    public static GuardState? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            string? Str(string name)
            {
                foreach (var p in doc.RootElement.EnumerateObject())
                    if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        return p.Value.ValueKind switch { JsonValueKind.String => p.Value.GetString(), JsonValueKind.Number => p.Value.GetRawText(), _ => null };
                return null;
            }
            string? build = Str("build");
            if (string.IsNullOrWhiteSpace(build) || ParseResult(Str("result")) is not { } result) return null;
            var tested = DateTime.TryParse(Str("tested"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeLocal, out var t) ? t : DateTime.MinValue;
            string detail = Str("detail") ?? "";
            return new GuardState(build.Trim(), result, tested, detail.Length > 500 ? detail[..500] : detail);
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>What to do at logon, before any test runs.</summary>
public enum GuardStep
{
    /// <summary>The live timeout is already 0: change nothing.</summary>
    AlreadyOn,
    /// <summary>Same build, last result leak: turn the workaround on without testing.</summary>
    TurnOnKnownLeak,
    /// <summary>Windows did not report the timeout: result unclear, turn it on to be safe.</summary>
    TurnOnUnreported,
    /// <summary>
    /// A leak test on this build never finished (PoolWatch was killed or crashed during it, so no failsafe ran; or its
    /// outcome could not be saved): turn the workaround on without testing, at this logon and at each later one on this
    /// build, until a test that finishes saves a verdict. So no later logon on this build is left without the workaround
    /// by a test that is cut short again.
    /// </summary>
    TurnOnInterrupted,
    /// <summary>First run, a new build, or the last result was neither leak nor interrupted: run the leak test first.</summary>
    Test,
}

/// <summary>
/// The outcome of a logon: the new state, whether to set the timeout to 0, and what the guard says of it in its log and
/// its findings, followed, when the outcome turns the workaround on (TurnOn) and the workaround is on, by a sentence
/// that any app may then take the focus. Other words take the place of Message in three cases. The workaround the
/// outcome turns on is not on (Windows refused the change): the log, the findings and a notice that asks for the user's
/// click say so (the guard's Report says when that notice shows). The guard was turned off while it checked: they say
/// so. With the guard on, the logon check failed after it decided (an exception, such as PoolWatch stopping while the
/// guard waits between two tries): they say that it failed, and so does that notice (by the same rule) when the
/// workaround is not on, unless the outcome it saved turns nothing on. Two more cases: a logon check whose outcome
/// leaves Windows alone (the leak looks fixed) and cannot be saved replaces it with one that turns the workaround on and
/// says why, and the setup guide's check keeps only the state (it says what it found in words of its own).
/// </summary>
public sealed record GuardOutcome(GuardState State, bool TurnOn, string Message, bool Changed = true);

/// <summary>
/// The guard's decision table and the leak test's verdict. Pure: the Windows calls (SystemParametersInfo, the
/// helper runs) are made by the caller.
/// </summary>
public static class GuardPolicy
{
    /// <summary>Children per way in one helper run.</summary>
    public const int Children = 100;

    /// <summary>
    /// Over all the helper's numbers (two runs x three ways): leak if any is at least half of <paramref name="n"/>,
    /// clean if every one is within 15% of n either way, else unclear.
    /// </summary>
    public static LeakVerdict Verdict(IReadOnlyList<long> deltas, int n = Children)
    {
        if (deltas.Count == 0) return LeakVerdict.Unclear;
        if (deltas.Any(d => d >= 0.5 * n)) return LeakVerdict.Leak;
        if (deltas.All(d => Math.Abs(d) <= 0.15 * n)) return LeakVerdict.Clean;
        return LeakVerdict.Unclear;
    }

    /// <summary>
    /// What the hidden leak-test helper must do when it sees the PoolWatch that started it exit before the
    /// test finishes. During the test the workaround is deliberately off, and the parent that would turn it back on is gone,
    /// so this logon is unprotected and no running PoolWatch will fix it until the next start. A background process cannot
    /// set the live foreground lock timeout (Windows accepts that only from the foreground program), so the only way to
    /// protect this logon is to ask the user for one click. True when the helper should raise that prompt: the parent went
    /// away during the test and the timeout is not already 0 (someone else already turned it on).
    /// </summary>
    public static bool HelperShouldPrompt(bool parentExitedDuringTest, uint? liveTimeout) =>
        parentExitedDuringTest && liveTimeout != 0;

    /// <summary>
    /// Whether that prompt is still wanted, asked before it shows and again while it is open: only while the user's click
    /// is needed. Not once the live timeout is 0 (the workaround is on: the state it asked about is gone), nor while a
    /// PoolWatch tray app runs in the helper's own Windows session whose token guard shows its notices
    /// (<paramref name="trayGuardRuns"/>: that guard runs a logon check of its own as it starts, which turns the workaround
    /// on when this logon needs it, or asks for the same click when Windows refuses it from the background, so this prompt
    /// would be a second; a tray app in another session protects that session only). Any other PoolWatch running does not
    /// count: a headless run shows no notice, and runs the guard only with --token-guard. A timeout that could not be read
    /// (null) leaves it wanted.
    /// </summary>
    public static bool HelperPromptStillNeeded(uint? liveTimeout, bool trayGuardRuns) => liveTimeout != 0 && !trayGuardRuns;

    /// <param name="interrupted">The marker of an unfinished leak test on this build was found (the last test was cut
    /// short). With the live timeout already 0 the step is still <see cref="GuardStep.AlreadyOn"/>; <see cref="Without"/> then
    /// saves the build as interrupted.</param>
    public static GuardStep First(GuardState? last, string build, uint? liveTimeout, bool interrupted = false)
    {
        if (liveTimeout == 0) return GuardStep.AlreadyOn;
        if (interrupted) return GuardStep.TurnOnInterrupted;
        if (last is { Result: GuardResult.Interrupted } && last.Build == build) return GuardStep.TurnOnInterrupted;
        if (last is { Result: GuardResult.Leak } && last.Build == build && liveTimeout is not null) return GuardStep.TurnOnKnownLeak;
        if (liveTimeout is null) return GuardStep.TurnOnUnreported;
        return GuardStep.Test;
    }

    /// <summary>
    /// What a logon says on a build whose test did not finish, and the way out: a check that finishes saves a verdict, and
    /// the setup guide runs one while the guard is off and its Windows setting is back (the check needs the workaround off:
    /// when Windows did not let Turn off put the setting back, Active changes lists it, and its Put back tries again). A new
    /// build is tested anyway.
    /// </summary>
    public const string InterruptedMessage = "Token-leak guard: a check on this Windows build did not finish, so the workaround is ON at each logon on this build until one does. " +
                                             "To check now, turn the guard off (if Active changes then still lists its Windows setting, put that back too), then choose Run setup again in the tray menu.";

    /// <summary>The outcome of a step that needs no test.</summary>
    /// <param name="interrupted">The marker of an unfinished leak test on this build was found (<see cref="First"/>). With
    /// the live timeout already 0 (<see cref="GuardStep.AlreadyOn"/>) nothing is turned on, but the build is saved as
    /// interrupted all the same, so the next sign-in, where Windows has set the timeout again, turns the workaround on
    /// without testing (the other steps say what to do with it already).</param>
    public static GuardOutcome Without(GuardStep step, GuardState? last, string build, DateTime nowUtc, bool interrupted = false) => step switch
    {
        // When the live timeout is already 0 on a NEW build, we have no verdict for that build yet -
        // record it as Unclear, never inherit the previous build's leak/fixed result (that would skip ever testing it).
        GuardStep.AlreadyOn => new GuardOutcome(
            interrupted ? Interrupted(last, build, nowUtc)
            : last is not null && last.Build == build
                ? last with { Detail = "already on at logon (Windows may have stopped resetting it)" }
                : new GuardState(build, GuardResult.Unclear, nowUtc, "already on at logon (new build, not yet tested)"),
            TurnOn: false, Message: "Token-leak guard: the workaround was already on at logon, nothing changed. Safe to start work.", Changed: false),
        GuardStep.TurnOnKnownLeak => new GuardOutcome(last! with { Detail = "same build, last result leak: turned on without testing" + (last.Detail.Length > 0 ? $" (last: {Trim(last.Detail)})" : "") },
            TurnOn: true, Message: "Token-leak guard: workaround ON (this build leaks), safe to start work."),
        GuardStep.TurnOnUnreported => new GuardOutcome(new GuardState(build, GuardResult.Unclear, nowUtc, "Windows did not report the foreground lock timeout"),
            TurnOn: true, Message: "Token-leak guard: Windows did not report the setting; workaround ON to be safe."),
        // Interrupted for this build: each later logon on it turns the workaround on without testing, until a test that
        // finishes (a check while the workaround is off, or the test of a new build) saves a verdict.
        GuardStep.TurnOnInterrupted => new GuardOutcome(Interrupted(last, build, nowUtc), TurnOn: true, Message: InterruptedMessage),
        _ => throw new ArgumentException("this step needs the leak test", nameof(step)),
    };

    /// <summary>The build's state once a leak test on it did not finish: the one saved already, or a new one.</summary>
    private static GuardState Interrupted(GuardState? last, string build, DateTime nowUtc) =>
        last is { Result: GuardResult.Interrupted } && last.Build == build
            ? last
            : new GuardState(build, GuardResult.Interrupted, nowUtc, "a leak test on this build did not finish (PoolWatch was stopped during it)");

    /// <summary>The outcome after the leak test.</summary>
    public static GuardOutcome AfterTest(LeakVerdict verdict, GuardState? last, string build, DateTime nowUtc, string numbers)
    {
        string detail = $"test {Fmt(nowUtc)}: {numbers}";
        switch (verdict)
        {
            case LeakVerdict.Leak:
                return new GuardOutcome(new GuardState(build, GuardResult.Leak, nowUtc, detail), true, "Token-leak guard: this build leaks; workaround ON, safe to start work.");
            case LeakVerdict.Unclear:
                return new GuardOutcome(new GuardState(build, GuardResult.Unclear, nowUtc, detail), true, "Token-leak guard: the test was unclear; workaround ON to be safe (tests again next logon).");
            default:
                // A build already judged Fixed that tests clean again must STAY Fixed (workaround off),
                // not drop back to CleanOnce (which turned the workaround back on and then off, alternating every
                // logon). So "clean and already fixed-or-clean-once on this build" keeps the workaround off.
                bool secondClean = last is { Result: GuardResult.CleanOnce or GuardResult.Fixed } && last.Build == build;
                return secondClean
                    ? new GuardOutcome(new GuardState(build, GuardResult.Fixed, nowUtc, detail), false, "Token-leak guard: the leak looks FIXED on this build (clean twice in a row); Windows left alone, will test again next logon.")
                    : new GuardOutcome(new GuardState(build, GuardResult.CleanOnce, nowUtc, detail), true, "Token-leak guard: clean once on this build; workaround ON anyway (tests again next logon).");
        }
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] : s;

    private static string Fmt(DateTime t) => t.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
}
