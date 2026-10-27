using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using PoolWatch.Native;

namespace PoolWatch.AppTests;

/// <summary>
/// The EnumServicesStatusExW loop (ERROR_MORE_DATA, the resume handle, buffer growth, the bounds) against a fake API
/// that fills the buffer the way the real one does: as many whole ENUM_SERVICE_STATUS_PROCESSW entries as fit, then
/// their strings, each entry holding the absolute addresses of its strings inside the buffer; ERROR_MORE_DATA with the
/// resume index and the bytes still needed when not everything fitted.
/// </summary>
public sealed class ServiceEnumerationLoopTests
{
    private const int Entry = 0x38, ServiceName = 0x00, DisplayName = 0x08, State = 0x14, ProcessId = 0x2C; // x64 layout

    private sealed class FakeScm((string Name, string Display, int Pid)[] services)
    {
        public List<int> Sizes { get; } = new();
        public int Calls => Sizes.Count;

        public FakeScm(int count, int nameExtra = 0)
            : this(Enumerable.Range(0, count).Select(i => ($"Svc{i:D4}" + new string('x', i % 40 + nameExtra), $"Service number {i}", 1000 + i)).ToArray()) { }

        private static int Bytes(string s) => (s.Length + 1) * 2;
        private int Cost(int i) => Entry + Bytes(services[i].Name) + Bytes(services[i].Display);
        public int TotalBytes => Enumerable.Range(0, services.Length).Sum(Cost);
        public IEnumerable<string> Names => services.Select(s => s.Name);

        public bool Call(IntPtr buffer, int size, out int needed, out int count, ref int resume, out int error)
        {
            Sizes.Add(size);
            int start = resume, end = start, used = 0;
            while (end < services.Length && used + Cost(end) <= size) used += Cost(end++);
            count = end - start;
            int at = count * Entry;
            for (int j = 0; j < count; j++)
            {
                var s = services[start + j];
                Marshal.WriteInt64(buffer, j * Entry + ServiceName, (long)buffer + at); at += Write(buffer, at, s.Name);
                Marshal.WriteInt64(buffer, j * Entry + DisplayName, (long)buffer + at); at += Write(buffer, at, s.Display);
                Marshal.WriteInt32(buffer, j * Entry + State, 4);
                Marshal.WriteInt32(buffer, j * Entry + ProcessId, s.Pid);
            }
            if (end == services.Length) { needed = 0; resume = 0; error = 0; return true; }
            needed = Enumerable.Range(end, services.Length - end).Sum(Cost);
            resume = end;
            error = ServiceEnumeration.ERROR_MORE_DATA;
            return false;
        }

        private static int Write(IntPtr buffer, int at, string s)
        {
            var bytes = Encoding.Unicode.GetBytes(s + "\0");
            Marshal.Copy(bytes, 0, buffer + at, bytes.Length);
            return bytes.Length;
        }
    }

    [Fact]
    public void ACallThatDoesNotFitEverythingIsContinuedFromTheResumeHandleAndEveryEntryComesOnce()
    {
        var scm = new FakeScm(300);
        var list = ServiceEnumeration.Query(scm.Call, initialSize: 4096, maxSize: 4096);

        Assert.Equal(scm.Names, list.Select(e => e.Name)); // all of them, once each, in order
        Assert.Equal(Enumerable.Range(0, 300).Select(i => 1000 + i), list.Select(e => e.ProcessId));
        Assert.InRange(scm.Calls, scm.TotalBytes / 4096 + 1, scm.TotalBytes / 4096 + 3); // as few calls as 4 KB allows
    }

    [Fact]
    public void TheBufferGrowsToWhatIsStillNeededWhenNotEvenOneEntryFits()
    {
        var scm = new FakeScm(50);
        var list = ServiceEnumeration.Query(scm.Call, initialSize: 32);

        Assert.Equal(50, list.Count);
        Assert.Equal([32, scm.TotalBytes], scm.Sizes);
    }

    [Fact]
    public void TheLargestSizeIsTriedBeforeGivingUp()
    {
        // Everything needs about 50 KB but the cap is 2 KB: the loop must use 2 KB buffers, not give up at the cap.
        var scm = new FakeScm(300);
        var list = ServiceEnumeration.Query(scm.Call, initialSize: 32, maxSize: 2048);

        Assert.Equal(300, list.Count);
        Assert.Equal(2048, scm.Sizes[1]);
    }

    [Fact]
    public void AnEntryThatCannotFitTheLargestSizeEndsTheLoop()
    {
        var scm = new FakeScm(3, nameExtra: 2000); // every name is over 4 KB
        Assert.Throws<InvalidOperationException>(() => ServiceEnumeration.Query(scm.Call, initialSize: 32, maxSize: 1024));
        Assert.Equal([32, 1024], scm.Sizes);
    }

    [Fact]
    public void AResumeHandleThatNeverAdvancesIsBoundedByTheCallLimit()
    {
        var inner = new FakeScm(10);
        int calls = 0;
        ServiceEnumeration.EnumCall stuck = (IntPtr buffer, int size, out int needed, out int count, ref int resume, out int error) =>
        {
            calls++;
            int keep = 0;
            inner.Call(buffer, Math.Min(size, Entry + 64), out needed, out count, ref keep, out error); // always the first entry
            error = ServiceEnumeration.ERROR_MORE_DATA;
            return false;
        };
        Assert.Throws<InvalidOperationException>(() => ServiceEnumeration.Query(stuck, maxCalls: 10));
        Assert.Equal(10, calls);
    }

    [Fact]
    public void AnyOtherErrorEndsTheLoop()
    {
        ServiceEnumeration.EnumCall denied = (IntPtr buffer, int size, out int needed, out int count, ref int resume, out int error) =>
        {
            needed = 0; count = 0; error = 5; // ERROR_ACCESS_DENIED
            return false;
        };
        Assert.Equal(5, Assert.Throws<Win32Exception>(() => ServiceEnumeration.Query(denied)).NativeErrorCode);
    }
}
