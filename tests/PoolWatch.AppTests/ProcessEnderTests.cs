using PoolWatch.Core.Model;
using PoolWatch.Engine;

namespace PoolWatch.AppTests;

/// <summary>
/// S6 (review round 2): End process opens the handle first, checks the create time on that handle, asks, and ends the
/// process through the same handle. Everything goes through a fake opener: no real process is opened or ended.
/// </summary>
public class ProcessEnderTests
{
    private const long Created = 133_000_000_000_000_000;
    private static readonly ProcessKey Key = new(4242, Created);

    /// <summary>Records every call in order; each handle has its own id.</summary>
    private sealed class FakeOpener : IProcessOpener
    {
        public readonly List<string> Calls = new();
        public long? LiveCreateTime = Created;
        public bool Exists = true;
        public string? TerminateError;
        public int Opened;
        public readonly List<FakeHandle> Handles = new();

        public IProcessHandle? Open(int pid, out string? error)
        {
            Calls.Add($"open {pid}");
            error = null;
            if (!Exists) return null;
            var h = new FakeHandle(this, ++Opened);
            Handles.Add(h);
            return h;
        }
    }

    private sealed class FakeHandle(FakeOpener o, int id) : IProcessHandle
    {
        public bool Disposed;
        public long? CreateTime() { o.Calls.Add($"createtime#{id}"); return o.LiveCreateTime; }
        public string? Terminate() { o.Calls.Add($"terminate#{id}"); return o.TerminateError; }
        public void Dispose() { Disposed = true; o.Calls.Add($"close#{id}"); }
    }

    private static Func<bool> Confirm(FakeOpener o, bool answer) => () => { o.Calls.Add("confirm"); return answer; };

    [Fact]
    public void EndsThroughTheSameHandleItCheckedAfterTheOwnerSaysYes()
    {
        var o = new FakeOpener();
        var r = ProcessEnder.End(o, Key, "app.exe", Confirm(o, true));
        Assert.Equal(ProcessEnder.Outcome.Ended, r.Outcome);
        // Open first, check on that handle, ask, check again, terminate through the SAME handle, close.
        Assert.Equal(["open 4242", "createtime#1", "confirm", "createtime#1", "terminate#1", "close#1"], o.Calls);
        Assert.Equal(1, o.Opened); // never re-opened by PID
    }

    [Fact]
    public void AnUnknownCreateTimeEndsNothingAndOpensNothing()
    {
        var o = new FakeOpener();
        var r = ProcessEnder.End(o, new ProcessKey(4242, 0), "app.exe", Confirm(o, true));
        Assert.Equal(ProcessEnder.Outcome.Refused, r.Outcome);
        Assert.Empty(o.Calls);
        Assert.Equal(ProcessEnder.Outcome.Refused, ProcessEnder.End(o, new ProcessKey(4242, -5), "app.exe", Confirm(o, true)).Outcome);
    }

    [Fact]
    public void AReusedPidIsNeverEnded()
    {
        var o = new FakeOpener { LiveCreateTime = Created + 1 }; // a different process now has the PID (even 100 ns later)
        var r = ProcessEnder.End(o, Key, "app.exe", Confirm(o, true));
        Assert.Equal(ProcessEnder.Outcome.Refused, r.Outcome);
        Assert.DoesNotContain("confirm", o.Calls);
        Assert.DoesNotContain(o.Calls, c => c.StartsWith("terminate"));
        Assert.True(o.Handles.Single().Disposed);
    }

    [Fact]
    public void NoChangeWhenTheOwnerCancelsOrTheProcessIsGone()
    {
        var o = new FakeOpener();
        Assert.Equal(ProcessEnder.Outcome.Cancelled, ProcessEnder.End(o, Key, "app.exe", Confirm(o, false)).Outcome);
        Assert.DoesNotContain(o.Calls, c => c.StartsWith("terminate"));
        Assert.True(o.Handles.Single().Disposed);

        var gone = new FakeOpener { Exists = false };
        Assert.Equal(ProcessEnder.Outcome.Gone, ProcessEnder.End(gone, Key, "app.exe", Confirm(gone, true)).Outcome);
        Assert.DoesNotContain("confirm", gone.Calls);

        var unreadable = new FakeOpener { LiveCreateTime = null };
        Assert.Equal(ProcessEnder.Outcome.Failed, ProcessEnder.End(unreadable, Key, "app.exe", Confirm(unreadable, true)).Outcome);
        Assert.DoesNotContain("confirm", unreadable.Calls);
    }

    [Fact]
    public void AFailedTerminateIsReported()
    {
        var o = new FakeOpener { TerminateError = "error 5" };
        var r = ProcessEnder.End(o, Key, "app.exe", Confirm(o, true));
        Assert.Equal(ProcessEnder.Outcome.Failed, r.Outcome);
        Assert.Contains("error 5", r.Message);
    }
}
