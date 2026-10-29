using PoolWatch.Core;
using PoolWatch.Core.Util;

namespace PoolWatch.Tests;

/// <summary>
/// The process registry and the string pool on a PC that starts thousands of processes a minute, most of them short
/// commands with their own long command lines.
/// </summary>
[Collection(FootprintCollection.Name)] // the string pool is process-wide: no other test may fill and clear it meanwhile
public class RegistryChurnTests
{
    private static readonly DateTime T0 = new(2026, 10, 20, 6, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void OverTheCapTheRegistryEvictsToFourFifths()
    {
        var reg = new ProcessRegistry(maxRecords: 1000) { KeepShortLived = TimeSpan.FromMinutes(15) };
        reg.ObserveSnapshot([new() { Pid = 4, ImageName = "System" }], T0);
        for (int i = 0; i < 1100; i++)
        {
            var t = T0.AddMilliseconds(i * 50);
            reg.OnStart(10_000 + i, 4, t.ToFileTimeUtc(), "git.exe", null, $"git -C /d/r{i} status", null, t);
            reg.OnStop(10_000 + i, t.ToFileTimeUtc(), 0, t.AddMilliseconds(20));
        }
        reg.ObserveSnapshot([new() { Pid = 4, ImageName = "System" }], T0.AddMinutes(1));
        Assert.InRange(reg.Count, 700, 800); // room for 200 more starts before the next full pass
    }

    [Fact]
    public void ARecordCreatesItsContextOnlyWhenAValueIsStored()
    {
        var reg = new ProcessRegistry();
        Assert.Null(reg.OnStart(400, 4, T0.ToFileTimeUtc(), "git.exe", null, "git status", null, T0).ContextValue("cwd")); // warm-up (JIT)
        // A fresh record: had the warm-up created a context, this read would create another one.
        var rec = reg.OnStart(500, 4, T0.ToFileTimeUtc(), "git.exe", null, "git status", null, T0);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Null(rec.ContextValue("cwd"));
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        lock (rec.Context) rec.Context["cwd"] = @"D:\repo";
        Assert.Equal(@"D:\repo", rec.ContextValue("cwd"));
        Assert.Same(rec.Context, rec.Context); // one context: it is also the lock its readers take
    }

    [Fact]
    public void OnlyShortStringsArePooled()
    {
        string Copy(string s) => new(s.AsSpan());
        string command = "git status --porcelain";
        Assert.Same(StringPool.Intern(command), StringPool.Intern(Copy(command)));
        // An agent's script: nearly always unique, so pooling it only kept it alive after its record was gone.
        string script = "bash -c \"" + string.Concat(Enumerable.Repeat("echo step >> /tmp/run/out.txt; ", 8)) + "\"";
        Assert.True(script.Length > StringPool.MaxLength);
        string copy = Copy(script);
        Assert.Same(copy, StringPool.Intern(copy));
        Assert.NotSame(StringPool.Intern(script), StringPool.Intern(copy));
    }
}
