using PoolWatch.Engine;

namespace PoolWatch.AppTests;

/// <summary>
/// The move-aside open's privilege fallback (NoFollow.OpenForMove). The order is pure and runs here against fakes that
/// record each step: the backup / restore privileges are raised only after an ACCESS_DENIED, for exactly one more open,
/// and dropped again before the call returns on every path. The token calls themselves are not run in a test: enabling
/// the privileges needs an elevated token and would change the test host's own process.
/// </summary>
public sealed class PrivilegeFallbackTests
{
    private const int AccessDenied = 5, PathNotFound = 3;
    private readonly List<string> _log = new();

    private sealed class Scope(List<string> log) : IDisposable
    {
        public void Dispose() => log.Add("drop");
    }

    private Func<(string, int)> Opens(params (string Result, int Error)[] results)
    {
        int i = 0;
        return () =>
        {
            var r = results[i++];
            _log.Add("open " + r.Result);
            return r;
        };
    }

    private IDisposable? Raise() { _log.Add("raise"); return new Scope(_log); }
    private IDisposable? CannotRaise() { _log.Add("raise"); return null; }
    private void Discard(string result) => _log.Add("discard " + result);

    [Fact]
    public void AnOpenThatWorksNeverTouchesThePrivileges()
    {
        var r = NoFollow.WithPrivilegeFallback(Opens(("h1", 0)), Raise, Discard);
        Assert.Equal(("h1", 0), r);
        Assert.Equal(new[] { "open h1" }, _log);
    }

    [Fact]
    public void AnotherErrorDoesNotRaiseThePrivileges()
    {
        var r = NoFollow.WithPrivilegeFallback(Opens(("h1", PathNotFound)), Raise, Discard);
        Assert.Equal(("h1", PathNotFound), r);
        Assert.Equal(new[] { "open h1" }, _log);
    }

    [Fact]
    public void ADeniedOpenRaisesForOneMoreOpenAndDropsThemBeforeReturning()
    {
        var r = NoFollow.WithPrivilegeFallback(Opens(("h1", AccessDenied), ("h2", 0)), Raise, Discard);
        Assert.Equal(("h2", 0), r);
        Assert.Equal(new[] { "open h1", "raise", "discard h1", "open h2", "drop" }, _log);
    }

    [Fact]
    public void ARetryThatIsDeniedAgainStillDropsThemAndDoesNotLoop()
    {
        var r = NoFollow.WithPrivilegeFallback(Opens(("h1", AccessDenied), ("h2", AccessDenied)), Raise, Discard);
        Assert.Equal(("h2", AccessDenied), r);
        Assert.Equal(new[] { "open h1", "raise", "discard h1", "open h2", "drop" }, _log);
    }

    [Fact]
    public void ARetryThatThrowsStillDropsThem()
    {
        int calls = 0;
        Func<(string, int)> open = () =>
        {
            if (calls++ == 0) { _log.Add("open h1"); return ("h1", AccessDenied); }
            _log.Add("open throws");
            throw new InvalidOperationException("boom");
        };
        Assert.Throws<InvalidOperationException>(() => NoFollow.WithPrivilegeFallback(open, Raise, Discard));
        Assert.Equal(new[] { "open h1", "raise", "discard h1", "open throws", "drop" }, _log);
    }

    [Fact]
    public void WhenThePrivilegesCannotBeRaisedTheFirstResultIsKeptAndNotRetried()
    {
        var r = NoFollow.WithPrivilegeFallback(Opens(("h1", AccessDenied)), CannotRaise, Discard);
        Assert.Equal(("h1", AccessDenied), r);
        Assert.Equal(new[] { "open h1", "raise" }, _log);
    }

    [Theory]
    [InlineData(0u, false)] // already on, or not held: AdjustTokenPrivileges changed nothing, so nothing is put back
    [InlineData(1u, true)]  // it was changed: put its previous state back after the open
    public void OnlyAPrivilegeTheCallChangedIsPutBack(uint previousCount, bool restore) =>
        Assert.Equal(restore, NoFollow.MustRestore(previousCount));
}
