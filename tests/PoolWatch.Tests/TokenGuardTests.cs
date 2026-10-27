using PoolWatch.Core.TokenGuard;

namespace PoolWatch.Tests;

/// <summary>Addendum 3: the token-leak guard's decision table and the leak test's verdict.</summary>
public class TokenGuardTests
{
    private static readonly DateTime T0 = new(2026, 10, 26, 7, 0, 0, DateTimeKind.Utc);
    private const string Build = "26220.1000";
    private static GuardState S(GuardResult r, string build = Build) => new(build, r, T0.AddDays(-1), "earlier");

    [Fact]
    public void VerdictThresholds()
    {
        // A leaking build: run 1 {1, 0, 2} (no leak that way), then run 2 {100, 98, 101} (one reference per child) -> leak.
        Assert.Equal(LeakVerdict.Leak, GuardPolicy.Verdict([1, 0, 2, 100, 98, 101]));
        Assert.Equal(LeakVerdict.Leak, GuardPolicy.Verdict([0, 0, 0, 0, 0, 50])); // >= 0.5 n
        Assert.Equal(LeakVerdict.Unclear, GuardPolicy.Verdict([0, 0, 0, 0, 0, 49]));
        Assert.Equal(LeakVerdict.Clean, GuardPolicy.Verdict([15, -15, 0, 3, -2, 1])); // every |value| <= 0.15 n
        Assert.Equal(LeakVerdict.Unclear, GuardPolicy.Verdict([16, 0, 0, 0, 0, 0]));
        Assert.Equal(LeakVerdict.Unclear, GuardPolicy.Verdict([-16, 0, 0, 0, 0, 0]));
        Assert.Equal(LeakVerdict.Unclear, GuardPolicy.Verdict([]));
        Assert.Equal(LeakVerdict.Leak, GuardPolicy.Verdict([10], n: 20));
    }

    [Fact]
    public void LiveTimeoutAlreadyZeroChangesNothing()
    {
        Assert.Equal(GuardStep.AlreadyOn, GuardPolicy.First(null, Build, 0));
        Assert.Equal(GuardStep.AlreadyOn, GuardPolicy.First(S(GuardResult.Fixed), Build, 0));
        var o = GuardPolicy.Without(GuardStep.AlreadyOn, S(GuardResult.Leak), Build, T0);
        Assert.False(o.TurnOn);
        Assert.False(o.Changed);
        Assert.Equal(GuardResult.Leak, o.State.Result);
        Assert.Contains("already on at logon", o.State.Detail);
    }

    [Fact]
    public void SameBuildLastLeakTurnsOnWithoutTesting()
    {
        Assert.Equal(GuardStep.TurnOnKnownLeak, GuardPolicy.First(S(GuardResult.Leak), Build, 200000));
        var o = GuardPolicy.Without(GuardStep.TurnOnKnownLeak, S(GuardResult.Leak), Build, T0);
        Assert.True(o.TurnOn);
        Assert.Equal(GuardResult.Leak, o.State.Result);
        Assert.Contains("safe to start work", o.Message);
        // A new build tests again even after a leak.
        Assert.Equal(GuardStep.Test, GuardPolicy.First(S(GuardResult.Leak, "26220.1100"), Build, 200000));
    }

    [Fact]
    public void UnreportedTimeoutIsUnclearAndTurnsOn()
    {
        Assert.Equal(GuardStep.TurnOnUnreported, GuardPolicy.First(null, Build, null));
        Assert.Equal(GuardStep.TurnOnUnreported, GuardPolicy.First(S(GuardResult.Fixed), Build, null));
        var o = GuardPolicy.Without(GuardStep.TurnOnUnreported, null, Build, T0);
        Assert.True(o.TurnOn);
        Assert.Equal(GuardResult.Unclear, o.State.Result);
    }

    [Fact]
    public void ALeakTestThatNeverFinishedTurnsTheWorkaroundOnWithoutTesting()
    {
        // M3: the marker of an unfinished test (PoolWatch killed during it) wins over "test first" and over a known leak.
        Assert.Equal(GuardStep.TurnOnInterrupted, GuardPolicy.First(S(GuardResult.CleanOnce), Build, 200000, interrupted: true));
        Assert.Equal(GuardStep.TurnOnInterrupted, GuardPolicy.First(S(GuardResult.Fixed), Build, 200000, interrupted: true));
        Assert.Equal(GuardStep.TurnOnInterrupted, GuardPolicy.First(null, Build, null, interrupted: true));
        Assert.Equal(GuardStep.AlreadyOn, GuardPolicy.First(null, Build, 0, interrupted: true)); // already protected
        var o = GuardPolicy.Without(GuardStep.TurnOnInterrupted, S(GuardResult.Fixed), Build, T0);
        Assert.True(o.TurnOn);
        Assert.Equal(GuardResult.Unclear, o.State.Result);
        Assert.Contains("did not finish", o.Message);
        Assert.Equal(GuardStep.Test, GuardPolicy.First(o.State, Build, 200000)); // the logon after tests again
    }

    [Fact]
    public void AnOrphanedHelperPromptsWhenItsParentDiedMidTest()
    {
        // M3 (review S2): if PoolWatch is killed during the test (the window in which the workaround is off), the surviving
        // helper must offer the one click that protects this logon — the only way from the background. It prompts only when
        // the parent went away AND the timeout is not already 0.
        Assert.True(GuardPolicy.HelperShouldPrompt(parentExitedDuringTest: true, liveTimeout: 200000));
        Assert.True(GuardPolicy.HelperShouldPrompt(true, liveTimeout: null));         // not reported: still protect
        Assert.False(GuardPolicy.HelperShouldPrompt(false, liveTimeout: 200000));     // parent alive: it finishes the check
        Assert.False(GuardPolicy.HelperShouldPrompt(true, liveTimeout: 0));           // already on: nothing to do
    }

    [Fact]
    public void OtherwiseTestFirst()
    {
        Assert.Equal(GuardStep.Test, GuardPolicy.First(null, Build, 200000)); // first run
        Assert.Equal(GuardStep.Test, GuardPolicy.First(S(GuardResult.CleanOnce), Build, 200000));
        Assert.Equal(GuardStep.Test, GuardPolicy.First(S(GuardResult.Fixed), Build, 200000));
        Assert.Equal(GuardStep.Test, GuardPolicy.First(S(GuardResult.Unclear), Build, 200000));
    }

    [Fact]
    public void AfterTheTest()
    {
        var leak = GuardPolicy.AfterTest(LeakVerdict.Leak, null, Build, T0, "run 1 {1, 0, 2}, run 2 {100, 98, 101}");
        Assert.True(leak.TurnOn);
        Assert.Equal(GuardResult.Leak, leak.State.Result);
        Assert.Contains("{100, 98, 101}", leak.State.Detail);

        var unclear = GuardPolicy.AfterTest(LeakVerdict.Unclear, S(GuardResult.Leak), Build, T0, "x");
        Assert.True(unclear.TurnOn);
        Assert.Equal(GuardResult.Unclear, unclear.State.Result);
        Assert.Contains("test again next logon", unclear.Message.Replace("tests again", "test again"));

        // Clean, first time on this build (no state, another build, or a last result of leak/unclear): clean-once, on anyway.
        foreach (var last in new GuardState?[] { null, S(GuardResult.CleanOnce, "26220.900"), S(GuardResult.Fixed, "26220.900"), S(GuardResult.Leak), S(GuardResult.Unclear) })
        {
            var once = GuardPolicy.AfterTest(LeakVerdict.Clean, last, Build, T0, "x");
            Assert.True(once.TurnOn);
            Assert.Equal(GuardResult.CleanOnce, once.State.Result);
        }

        // Clean on the second logon in a row on the same build: fixed, Windows left alone.
        var fixedNow = GuardPolicy.AfterTest(LeakVerdict.Clean, S(GuardResult.CleanOnce), Build, T0, "x");
        Assert.False(fixedNow.TurnOn);
        Assert.Equal(GuardResult.Fixed, fixedNow.State.Result);
        Assert.Contains("looks FIXED", fixedNow.Message);
        Assert.Contains("will test again next logon", fixedNow.Message);

        // C4 (review): a build already judged Fixed that tests clean AGAIN stays Fixed (off), never flips back to
        // clean-once (which turned the workaround on, then off, alternating every logon).
        var stillFixed = GuardPolicy.AfterTest(LeakVerdict.Clean, S(GuardResult.Fixed), Build, T0, "x");
        Assert.False(stillFixed.TurnOn);
        Assert.Equal(GuardResult.Fixed, stillFixed.State.Result);
    }

    [Fact]
    public void AlreadyOnANewBuildDoesNotInheritTheOldVerdict()
    {
        // C4 (review): already-on at logon on a NEW build must record Unclear (not yet tested on this build), never
        // carry the previous build's leak/fixed result forward (which would skip ever testing the new build).
        var o = GuardPolicy.Without(GuardStep.AlreadyOn, S(GuardResult.Fixed, "26220.900"), Build, T0);
        Assert.False(o.TurnOn);
        Assert.False(o.Changed);
        Assert.Equal(Build, o.State.Build);
        Assert.Equal(GuardResult.Unclear, o.State.Result);
        Assert.Contains("new build", o.State.Detail);

        // Same build keeps its known result (here: leak), only the detail changes.
        var same = GuardPolicy.Without(GuardStep.AlreadyOn, S(GuardResult.Leak), Build, T0);
        Assert.Equal(GuardResult.Leak, same.State.Result);
    }

    [Fact]
    public void StateRoundTripsAndParsesLeniently()
    {
        var s = new GuardState(Build, GuardResult.CleanOnce, T0, "test: run 1 {0, 0, 1}");
        Assert.Equal(s, GuardState.Parse(s.ToJson()));
        // The same keys in another case, the build as a string or a number, and a local time are all read.
        var old = GuardState.Parse("""{ "Build": "26220.1000", "result": "leak", "tested": "2026-10-20T08:15:12", "detail": "run 1 {1,0,2} run 2 {100,98,101}" }""");
        Assert.NotNull(old);
        Assert.Equal(GuardResult.Leak, old!.Result);
        Assert.Equal(Build, old.Build);
        Assert.Contains("100", old.Detail);
        Assert.Null(GuardState.Parse("""{ "build": "26220.1000", "result": "maybe" }"""));
        Assert.Null(GuardState.Parse("not json"));
        Assert.Null(GuardState.Parse("[1,2]"));
        Assert.Equal(GuardResult.CleanOnce, GuardState.Parse("""{ "build": 26220, "result": "clean_once" }""")!.Result);
    }
}
