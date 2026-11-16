using System.Text.RegularExpressions;
using FsCheck;
using FsCheck.Fluent;
using static PoolWatch.Tests.PropertyInputs;

namespace PoolWatch.Tests;

/// <summary>
/// The property tests' own helpers (<see cref="PropertyInputs"/>): a failure is made again from the numbers it prints, as
/// <see cref="PropertyInputs.Cases"/> says, and a case that never ends fails instead of holding up the test run.
/// </summary>
public class PropertyInputsTests
{
    [Fact]
    public void AFailureIsMadeAgainFromTheNumbersItPrints()
    {
        var seen = new List<string>();
        var failure = Assert.ThrowsAny<Exception>(() => Check.One(Config.QuickThrowOnFailure.WithMaxTest(100), Prop.ForAll(Arb.From(AnyText), (string text) =>
        {
            seen.Add(text);
            Assert.True(seen.Count < 10, "the tenth case fails");
        })));
        var printed = Regex.Match(failure.Message, @"Replay directly at failing step with (\([0-9]+,[0-9]+,[0-9]+\))");
        Assert.True(printed.Success, failure.Message);

        var replay = ReplayFrom(printed.Groups[1].Value);
        Assert.NotNull(replay);
        var again = new List<string>();
        Check.One(Config.QuickThrowOnFailure.WithMaxTest(1).WithReplay(global::Microsoft.FSharp.Core.FSharpOption<Replay>.Some(replay)),
            Prop.ForAll(Arb.From(AnyText), (string text) => again.Add(text)));
        Assert.Equal(seen[^1], Assert.Single(again)); // the first case of the replay is the one that failed
    }

    [Theory]
    [InlineData("(1,2,3)", 1UL, 2UL, 3)]
    [InlineData("5468017883443469810,12266597578681389103,7", 5468017883443469810UL, 12266597578681389103UL, 7)]
    [InlineData(" (18446744073709551615, 1, 100). ", ulong.MaxValue, 1UL, 100)]
    public void AReplayIsReadAsAFailurePrintsIt(string text, ulong seed, ulong gamma, int size) =>
        Assert.Equal(new Replay(new Rnd(seed, gamma), global::Microsoft.FSharp.Core.FSharpOption<int>.Some(size)), ReplayFrom(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void NoReplayIsSetByDefault(string? text) => Assert.Null(ReplayFrom(text));

    [Theory]
    [InlineData("1,2")]
    [InlineData("1,2,3,4")]
    [InlineData("a,b,c")]
    [InlineData("1,2,-3")]
    [InlineData("18446744073709551616,1,1")]
    public void AReplayThatIsNotThreeNumbersStopsTheTest(string text) => Assert.Throws<InvalidOperationException>(() => ReplayFrom(text));

    [Fact]
    public void ACaseThatNeverEndsFailsInsteadOfHoldingUpTheRun() =>
        Assert.Throws<TimeoutException>(() => InTime(() => Thread.Sleep(Timeout.Infinite), TimeSpan.FromMilliseconds(200)));

    [Fact]
    public void ACaseThatFailsThrowsWhatItThrew()
    {
        var thrown = new FormatException("an example failure");
        Assert.Same(thrown, Assert.Throws<FormatException>(() => InTime(() => throw thrown, CaseTime)));
    }

    [Fact]
    public void JustOverIsJustOverTheLimit()
    {
        var texts = JustOver(Gen.Elements("[]", "{\"a\":\"é\"}"), 1000).Sample(50);
        Assert.All(texts, t => Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(t), 1001, 1016));
    }
}
