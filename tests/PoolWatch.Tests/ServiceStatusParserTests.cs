using System.Runtime.InteropServices;
using System.Text;
using PoolWatch.Core.Parsing;

namespace PoolWatch.Tests;

/// <summary>
/// The EnumServicesStatusExW result parser. Each test builds a synthetic unmanaged buffer the way the API fills one: the
/// ENUM_SERVICE_STATUS_PROCESSW entries first, then the strings, each entry holding the absolute addresses of its two
/// strings inside the same buffer. Names are only read when their address lies inside the buffer.
/// </summary>
public sealed unsafe class ServiceStatusParserTests
{
    private const int Entry = ServiceStatusParser.EntrySize;

    private sealed class FakeEnumBuffer : IDisposable
    {
        private int _next;
        public FakeEnumBuffer(int entries, int size)
        {
            Size = size;
            P = (byte*)NativeMemory.AllocZeroed((nuint)size);
            _next = entries * Entry;
        }
        public byte* P { get; }
        public int Size { get; }
        public ulong Base => (ulong)P;
        public ReadOnlySpan<byte> Span => new(P, Size);

        /// <summary>Appends a string after the entries (NUL-terminated unless <paramref name="terminate"/> is false); its address.</summary>
        public ulong AddString(string s, bool terminate = true)
        {
            var bytes = Encoding.Unicode.GetBytes(terminate ? s + "\0" : s);
            ulong at = Base + (ulong)_next;
            bytes.CopyTo(new Span<byte>(P + _next, bytes.Length));
            _next += bytes.Length;
            return at;
        }

        public void SetEntry(int i, ulong name, ulong display, int pid = 0, int state = 1)
        {
            byte* e = P + i * Entry;
            *(ulong*)(e + ServiceStatusParser.ServiceNamePointer) = name;
            *(ulong*)(e + ServiceStatusParser.DisplayNamePointer) = display;
            *(int*)(e + ServiceStatusParser.CurrentState) = state;
            *(int*)(e + ServiceStatusParser.ProcessId) = pid;
        }

        public void Dispose() => NativeMemory.Free(P);
    }

    [Fact]
    public void ReadsEachNameThroughItsAddressInsideTheBuffer()
    {
        using var b = new FakeEnumBuffer(2, 4096);
        b.SetEntry(0, b.AddString("EventLog"), b.AddString("Windows Event Log"), pid: 1234, state: 4);
        b.SetEntry(1, b.AddString("Spooler"), b.AddString("Print Spooler"), pid: 0, state: 1);

        var list = ServiceStatusParser.Parse(b.Span, b.Base, 2);

        Assert.Equal([new("EventLog", "Windows Event Log", 1234, 4), new("Spooler", "Print Spooler", 0, 1)], list);
    }

    [Fact]
    public void AnAddressOutsideTheBufferIsRejected()
    {
        using var b = new FakeEnumBuffer(4, 4096);
        b.SetEntry(0, b.Base + (ulong)b.Size + 64, b.AddString("Outside, after"));   // service name beyond the end
        b.SetEntry(1, b.Base - 16, b.AddString("Outside, before"));                   // service name before the start
        b.SetEntry(2, b.AddString("Dhcp"), b.Base + (ulong)b.Size);                   // display name just past the end
        b.SetEntry(3, 0, b.AddString("No name"));                                     // no service name at all

        var list = ServiceStatusParser.Parse(b.Span, b.Base, 4);

        var only = Assert.Single(list);
        Assert.Equal("Dhcp", only.Name);
        Assert.Equal("Dhcp", only.DisplayName); // an unreadable display name falls back to the service name
    }

    [Fact]
    public void AStringWithNoTerminatorInsideTheBufferIsRejected()
    {
        using var b = new FakeEnumBuffer(2, 2 * Entry + 64);
        b.SetEntry(0, b.AddString("BITS"), b.AddString("BITS"));
        ulong runaway = b.AddString(new string('x', (b.Size - 2 * Entry - 20) / 2), terminate: false); // runs to the end
        b.SetEntry(1, runaway, runaway);

        var list = ServiceStatusParser.Parse(b.Span, b.Base, 2);

        Assert.Equal("BITS", Assert.Single(list).Name);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)] // three entries do not fit in a buffer of two
    public void EntriesThatDoNotFitAreAFormatError(int count)
    {
        using var b = new FakeEnumBuffer(2, 2 * Entry);
        Assert.Throws<FormatException>(() => ServiceStatusParser.Parse(b.Span, b.Base, count));
    }

    [Fact]
    public void TheNamesAreReadByOffsetNotThroughTheStoredAddress()
    {
        // What a moved buffer looks like: the same bytes somewhere else. Parsed with the address the buffer had when the
        // API filled it, every name is still found at its offset; nothing is read through the stale address itself.
        using var b = new FakeEnumBuffer(1, 1024);
        b.SetEntry(0, b.AddString("WinDefend"), b.AddString("Microsoft Defender Antivirus Service"), pid: 42, state: 4);
        byte[] moved = b.Span.ToArray();
        new Span<byte>(b.P, b.Size).Clear(); // the old place no longer holds the names

        var list = ServiceStatusParser.Parse(moved, b.Base, 1);

        Assert.Equal(new ServiceStatusParser.Entry("WinDefend", "Microsoft Defender Antivirus Service", 42, 4), Assert.Single(list));
    }
}
