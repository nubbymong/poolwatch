using System.Runtime;
using PoolWatch.Engine;
using PoolWatch.Native;

namespace PoolWatch.AppTests;

[CollectionDefinition(nameof(GcStressCollection), DisableParallelization = true)]
public sealed class GcStressCollection;

/// <summary>
/// The real service list (a read-only service control manager query) while another thread does what PoolWatch's own
/// memory trim does: a forced, compacting full collection that also compacts the large object heap, in a loop, with
/// large arrays allocated and dropped so there is something to slide. Reading the names through the addresses the API
/// stored in a managed array crashed the app (an access violation) when such a collection moved the array; the names
/// now come from unmanaged memory and are copied before anything else runs. Runs on its own: the collections would
/// disturb the timing of other tests.
/// </summary>
[Collection(nameof(GcStressCollection))]
public sealed class ServiceEnumerationGcTests
{
    private const uint SERVICE_WIN32 = 0x30, SERVICE_STATE_ALL = 0x3, SC_MANAGER_ENUMERATE_SERVICE = 0x0004;

    [Fact]
    public void TheServiceListIsReadCorrectlyWhileTheLargeObjectHeapIsCompacted()
    {
        using var stop = new CancellationTokenSource();
        int collections = 0;
        var hammer = new Thread(() =>
        {
            var keep = new byte[4][];
            for (int i = 0; !stop.IsCancellationRequested; i++)
            {
                keep[i % keep.Length] = new byte[120_000 + i % 7 * 4096]; // large-object-heap garbage ahead of later arrays
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                Interlocked.Increment(ref collections);
                Thread.Sleep(2);
            }
        }) { IsBackground = true, Name = "LOH compaction" };
        hammer.Start();
        try
        {
            // The full path the services page uses: the list, then the slow per-service queries (configuration, status,
            // signer) that gave a collection time to move the old buffer.
            for (int round = 0; round < 2; round++)
            {
                var views = ServiceControl.Enumerate();
                Assert.True(views.Count > 20, $"only {views.Count} services listed");
                Assert.All(views, v => Assert.False(string.IsNullOrWhiteSpace(v.Name)));
                Assert.Contains(views, v => v.Name.Equals("EventLog", StringComparison.OrdinalIgnoreCase));
                Assert.Contains(views, v => v.Name.Equals("RpcSs", StringComparison.OrdinalIgnoreCase));
            }
            // The list alone, many times (the running-services collector's path).
            var scm = NativeMethods.OpenSCManagerW(null, null, SC_MANAGER_ENUMERATE_SERVICE);
            Assert.NotEqual(IntPtr.Zero, scm);
            try
            {
                for (int i = 0; i < 40; i++)
                {
                    var list = ServiceEnumeration.Query(scm, SERVICE_WIN32, SERVICE_STATE_ALL);
                    Assert.True(list.Count > 20);
                    Assert.Equal(list.Count, list.Select(e => e.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
                    Assert.Contains(list, e => e.Name.Equals("EventLog", StringComparison.OrdinalIgnoreCase));
                }
            }
            finally { NativeMethods.CloseServiceHandle(scm); }
        }
        finally
        {
            stop.Cancel();
            hammer.Join();
        }
        Assert.True(collections > 0, "the compacting collections did not run");
    }
}
