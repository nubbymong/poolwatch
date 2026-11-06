using System.Text;
using PoolWatch.Core.Util;

namespace PoolWatch.Tests;

/// <summary>
/// A CSV file is opened through the opener it is given. In the app that opener never waits for another program: an
/// open that would have to wait is given up and raised as a refusal. These prove the file and the header check both go
/// through the opener they are given; that a name whose header read or open is refused is passed over, so the rows go on
/// in the next numbered file under their own header and never under a header that was not read; that when every name is
/// refused the refusal is raised; and that rows whose write fails are kept for the next write. Which opener the app gives
/// is held by the app's NoWaitOpenSiteTests.
/// </summary>
public sealed class CsvNoWaitTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pw-csv-nowait-" + Guid.NewGuid().ToString("N"));

    public CsvNoWaitTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static readonly int SharingViolation = unchecked((int)0x80070020), LockViolation = unchecked((int)0x80070021);

    // Stands for the app's opener when the open is refused (given up rather than wait, or a plain refusal).
    private static Stream Refuses(string path, FileMode mode, FileAccess access, FileShare share) =>
        throw new IOException("the open was refused") { HResult = SharingViolation };

    [Fact]
    public void WhenEveryNameIsRefusedTheRefusalIsRaisedAndNothingIsWritten()
    {
        string path = Path.Combine(_dir, "totals.csv");
        var ex = Assert.Throws<IOException>(() => new CsvFile(path, "time,value", Refuses));
        Assert.Equal(SharingViolation, ex.HResult);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void TheHeaderCheckOfAnExistingFileGoesThroughTheGivenOpenerToo()
    {
        // A file already holds a different header, so the open must read its first line to decide the file to append to;
        // it reads through the opener. A plain opener that counts its reads proves the header check uses it.
        string path = Path.Combine(_dir, "tags.csv");
        File.WriteAllText(path, "old,header\r\n1,2\r\n", new UTF8Encoding(false));
        int reads = 0;
        Stream Counting(string p, FileMode m, FileAccess a, FileShare s)
        {
            if (a == FileAccess.Read) reads++;
            return new FileStream(p, m, a, s);
        }
        using var csv = new CsvFile(path, "new,header", Counting);
        Assert.True(reads > 0, "the existing file's header is read through the opener");
        Assert.EndsWith("tags-2.csv", csv.Path_); // a different header, so a new numbered file
    }

    /// <summary>
    /// When the header read of a file that is there is refused, that file is passed over: no row goes under a header that
    /// was not read (the file of another layout keeps only its own rows), and the rows go on in the next numbered file,
    /// under their own header. Once the header can be read, rows of the same layout go on in that numbered file.
    /// </summary>
    [Fact]
    public void AHeaderThatCannotBeReadIsNeverTakenForTheFilesLayout()
    {
        string path = Path.Combine(_dir, "processes.csv");
        const string old = "old,header\r\n1,2\r\n";
        File.WriteAllText(path, old, new UTF8Encoding(false));
        Stream RefusesReads(string p, FileMode m, FileAccess a, FileShare s) =>
            a == FileAccess.Read ? Refuses(p, m, a, s) : new FileStream(p, m, a, s);
        using (var csv = new CsvFile(path, "new,header", RefusesReads))
        {
            Assert.Equal(2, csv.Number);
            Assert.Equal([("processes.csv", "the open was refused")], csv.Refused);
            csv.Write(3, 4);
        }
        Assert.Equal(old, File.ReadAllText(path));
        Assert.Equal(["new,header", "3,4"], File.ReadAllLines(Path.Combine(_dir, "processes-2.csv")));
        // Once the header can be read, the rows of the new layout go on in that numbered file.
        using (var csv = new CsvFile(path, "new,header")) csv.Write(5, 6);
        Assert.Equal(old, File.ReadAllText(path));
        Assert.Equal(["new,header", "3,4", "5,6"], File.ReadAllLines(Path.Combine(_dir, "processes-2.csv")));
    }

    /// <summary>A caller whose write was refused starts after the file that refused it (<c>first</c>).</summary>
    [Fact]
    public void ACallerCanStartAfterTheFileThatRefusedIt()
    {
        string path = Path.Combine(_dir, "lag.csv");
        using (var csv = new CsvFile(path, "time,ms")) csv.Write(1, 2);
        using (var csv = new CsvFile(path, "time,ms", first: 2))
        {
            Assert.Equal(2, csv.Number);
            csv.Write(3, 4);
        }
        Assert.Equal(["time,ms", "1,2"], File.ReadAllLines(path));
        Assert.Equal(["time,ms", "3,4"], File.ReadAllLines(Path.Combine(_dir, "lag-2.csv")));
    }

    /// <summary>
    /// Rows whose write fails are not lost: they stay for the next flush, which writes them once, with the header, in one
    /// write of the whole batch.
    /// </summary>
    [Fact]
    public void RowsWhoseWriteFailsAreKeptForTheNextFlush()
    {
        string path = Path.Combine(_dir, "load.csv");
        var stream = new FailsOnce(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, bufferSize: 0));
        using (var csv = new CsvFile(path, "time,cpu", (_, _, _, _) => stream))
        {
            csv.Write(1, 0.5);
            Assert.Equal(LockViolation, Assert.Throws<IOException>(csv.Flush).HResult);
            csv.Write(2, 0.25);
            csv.Flush();
            Assert.Equal(1, stream.Writes);
        }
        Assert.Equal(["time,cpu", "1,0.5", "2,0.25"], File.ReadAllLines(path));
    }

    /// <summary>A stream whose first write is refused, and which counts the writes it lets through.</summary>
    private sealed class FailsOnce(Stream inner) : Stream
    {
        private bool _failed;
        public int Writes { get; private set; }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (!_failed)
            {
                _failed = true;
                throw new IOException("the write was refused") { HResult = LockViolation };
            }
            Writes++;
            inner.Write(buffer);
        }
        public override void Flush() => inner.Flush();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
