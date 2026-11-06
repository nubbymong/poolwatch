using PoolWatch.Engine;

namespace PoolWatch.AppTests;

/// <summary>
/// The privilege fallback of the move-aside open (NoFollow.OpenForMove) and of the open that reads a folder's owner and
/// rules (NoFollow.OpenToRead). The order is pure and runs here against fakes that record each step: the privileges are
/// raised only after an ACCESS_DENIED, for exactly one more open, and dropped again before the call returns on every
/// path; the result says whether it came from that open. OpenToRead's own steps (which privilege it asks for, which
/// result it returns) run with its open and its privilege step passed in, and so does the order in which privileges are
/// raised and put back (RaiseAll). The token calls themselves are not run in a test: enabling the privileges needs an
/// elevated token and would change the test host's own process.
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
        Assert.Equal(("h1", 0, false), r);
        Assert.Equal(new[] { "open h1" }, _log);
    }

    [Fact]
    public void AnotherErrorDoesNotRaiseThePrivileges()
    {
        var r = NoFollow.WithPrivilegeFallback(Opens(("h1", PathNotFound)), Raise, Discard);
        Assert.Equal(("h1", PathNotFound, false), r);
        Assert.Equal(new[] { "open h1" }, _log);
    }

    [Fact]
    public void ADeniedOpenRaisesForOneMoreOpenAndDropsThemBeforeReturning()
    {
        var r = NoFollow.WithPrivilegeFallback(Opens(("h1", AccessDenied), ("h2", 0)), Raise, Discard);
        Assert.Equal(("h2", 0, true), r); // the result came from the open made with the privileges on
        Assert.Equal(new[] { "open h1", "raise", "discard h1", "open h2", "drop" }, _log);
    }

    [Fact]
    public void ARetryThatIsDeniedAgainStillDropsThemAndDoesNotLoop()
    {
        var r = NoFollow.WithPrivilegeFallback(Opens(("h1", AccessDenied), ("h2", AccessDenied)), Raise, Discard);
        Assert.Equal(("h2", AccessDenied, true), r);
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
        Assert.Equal(("h1", AccessDenied, false), r); // the plain open's result: not one made with the privileges
        Assert.Equal(new[] { "open h1", "raise" }, _log);
    }

    [Theory]
    [InlineData(0u, false)] // already on, or not held: AdjustTokenPrivileges changed nothing, so nothing is put back
    [InlineData(1u, true)]  // it was changed: put its previous state back after the open
    public void OnlyAPrivilegeTheCallChangedIsPutBack(uint previousCount, bool restore) =>
        Assert.Equal(restore, NoFollow.MustRestore(previousCount));

    [Fact]
    public void TheOpenThatReadsAFoldersRulesAsksForTheBackupPrivilegeAloneAfterADeniedOpen()
    {
        // OpenToRead with its open and its privilege step passed in: invalid handles stand in for the two opens.
        static Microsoft.Win32.SafeHandles.SafeFileHandle Handle(int n) => new(new IntPtr(-n), ownsHandle: false);
        var first = Handle(1);
        var second = Handle(2);
        int opens = 0;
        string[]? asked = null;
        var r = NoFollow.OpenToRead(@"C:\x", _ => opens++ == 0 ? (first, AccessDenied) : (second, 0), names => { asked = names; return new Scope(_log); });
        Assert.Equal(["SeBackupPrivilege"], asked!);
        Assert.Same(second, r.Handle);
        Assert.Equal(0, r.Error);
        Assert.True(r.Privileged);
        Assert.True(first.IsClosed); // the first open's handle is let go
        Assert.Equal(["drop"], _log); // and the privilege is put back before it returns
        // An open that works asks for no privilege.
        asked = null;
        var plain = NoFollow.OpenToRead(@"C:\x", _ => (Handle(3), 0), names => { asked = names; return new Scope(_log); });
        Assert.Null(asked);
        Assert.False(plain.Privileged);
    }

    [Fact]
    public void PrivilegesAreRaisedAllOrNoneAndEveryChangeIsPutBackWhateverHappens()
    {
        // RaiseAll, the order behind the token calls: each change made on the way is recorded, and put back when not all
        // could be enabled, when a step throws, or when the scope could not be made.
        var restored = new List<string>();
        IDisposable Keep(List<string> changes) { _log.Add("scope " + string.Join(",", changes)); return new Scope(_log); }
        // All enabled: the scope takes the changes, and nothing is put back now.
        var held = NoFollow.RaiseAll<string>(["a", "b"], (name, changes) => { changes.Add(name); return true; }, c => restored.AddRange(c), Keep);
        Assert.NotNull(held);
        Assert.Equal(["scope a,b"], _log);
        Assert.Empty(restored);
        // One not enabled: what was changed is put back, and there is no scope.
        Assert.Null(NoFollow.RaiseAll<string>(["a", "b"], (name, changes) => { changes.Add(name); return name == "a"; }, c => restored.AddRange(c), Keep));
        Assert.Equal(["a", "b"], restored);
        // The second step throws after the first changed the token: the first change is put back all the same.
        restored.Clear();
        Assert.Throws<InvalidOperationException>(() => NoFollow.RaiseAll<string>(["a", "b"], (name, changes) =>
        {
            if (name != "a") throw new InvalidOperationException("boom");
            changes.Add(name);
            return true;
        }, c => restored.AddRange(c), Keep));
        Assert.Equal(["a"], restored);
        // The scope cannot be made: put back too.
        restored.Clear();
        Assert.Throws<InvalidOperationException>(() => NoFollow.RaiseAll<string>(["a"], (name, changes) => { changes.Add(name); return true; },
            c => restored.AddRange(c), _ => throw new InvalidOperationException("no scope")));
        Assert.Equal(["a"], restored);
    }
}
