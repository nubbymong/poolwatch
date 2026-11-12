using System.IO.Compression;
using System.Text;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Util;

namespace PoolWatch.Tests;

/// <summary>
/// A report folder's appended files (an incident's events.csv, a storm folder's spawns.csv) are read by an export up to
/// their last whole row: a row cut short in the moment of an append (or by a crash in it) is never in the zip as half a
/// row, and a value with a line break in it, cut inside its quotes, is not taken for a row's end. A read with a bound
/// (the vendor bundle's) never reads past it. An export refuses a report folder that is a link when it looks at it, and
/// leaves out each file and folder in it that is a link as it lists the folder; a link put in place after that look or
/// that listing is not found (SECURITY.md, Known limitations).
/// </summary>
public sealed class AppendedCsvReadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pw-appended-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private const string Header = "time,kind,pid,create_time,parent_pid,image,exit_code,user,command_line,detail\r\n";
    private const string Whole = Header + "2026-11-09T23:00:00.000Z,Start,10,1,4,bash.exe,,,\"bash -c \"\"echo a\nb\"\"\",\r\n" +
                                 "2026-11-09T23:00:01.000Z,Exit,10,1,4,bash.exe,0,,,\r\n";

    [Theory]
    [InlineData("", Whole)]
    [InlineData("2026-11-09T23:00:02.000Z,Start,11,2,4,git.e", Whole)] // cut in a row
    [InlineData("2026-11-09T23:00:02.000Z,Start,11,2,4,git.exe,,,\"git commit -m \"\"line one\n", Whole)] // cut inside a quoted value's line break
    [InlineData("2026-11-09T23:00:02.000Z,Exit,11,2,4,git.exe,0,,,\r", Whole)] // cut between its CR and LF
    public void OnlyWholeRowsAreRead(string tail, string expected)
    {
        Assert.Equal(expected, Csv.CompleteRows(Whole + tail));
        Assert.Equal("", Csv.CompleteRows("time,kind"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnExportOfAFolderBeingAppendedToHoldsItsWholeRowsOnly(bool redact)
    {
        string inc = Directory.CreateDirectory(Path.Combine(_dir, "20261109-230000-Leak-6b61654c")).FullName;
        File.WriteAllText(Path.Combine(inc, "events.csv"), Whole + "2026-11-09T23:00:02.000Z,Start,11,2,4,git.exe,,,\"git log --");
        File.WriteAllText(Path.Combine(inc, "spawns.csv"), "child_pid,image\r\n12,git.exe\r\n13,se");
        File.WriteAllText(Path.Combine(inc, "report.json"), "{ \"id\": \"x\" }");
        // The append's writer still holds the file open, sharing reads, as IncidentWriter's append does.
        using var writer = new FileStream(Path.Combine(inc, "events.csv"), FileMode.Append, FileAccess.Write, FileShare.Read);
        var redaction = redact ? new RedactionInfo("someone", "SOMEPC", [@"C:\Users\someone"]) : null;
        string zip = Exporter.Export(inc, Path.Combine(_dir, "exports"), redaction, p => p);
        using var archive = ZipFile.OpenRead(zip);
        string Entry(string name) { using var r = new StreamReader(archive.GetEntry(name)!.Open()); return r.ReadToEnd(); }
        Assert.Equal(Whole, Entry("events.csv"));
        Assert.Equal("child_pid,image\r\n12,git.exe\r\n", Entry("spawns.csv"));
        Assert.True(Exporter.IsAppended("events.csv") && Exporter.IsAppended("spawns.csv") && !Exporter.IsAppended("timeline.csv"));
    }

    /// <summary>A file that gives a short length and then more bytes than that: one that grew, or stands in another's place, after a look at its size.</summary>
    private sealed class LongerThanItSays(long holds) : Stream
    {
        /// <summary>The bytes read from it so far.</summary>
        public long Given { get; private set; }
        public override long Length => 10;
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = (int)Math.Min(count, holds - Given);
            Array.Fill(buffer, (byte)'a', offset, n);
            Given += n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Position { get => Given; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// The read with a bound (the vendor bundle's spawns.csv) takes its size from what it reads through the handle it
    /// opened, never from a length asked beforehand: of a file that holds more than the bound, whatever length it gives,
    /// it reads the bound and one byte more and uses nothing; of one within the bound, its whole rows.
    /// </summary>
    [Fact]
    public void ABoundedReadNeverReadsPastItsBoundWhateverLengthTheFileGives()
    {
        const int Bound = 100_000;
        var grown = new LongerThanItSays(4L * Bound);
        Assert.Null(Csv.ReadCompleteRows(grown, Bound));
        Assert.Equal(Bound + 1, grown.Given);
        var within = new LongerThanItSays(Bound);
        Assert.Equal("", Csv.ReadCompleteRows(within, Bound)); // all of it, and it holds no whole row
        Assert.Equal(Bound, within.Given);

        const string Rows = "a,b\r\n1,2\r\n";
        byte[] bytes = Encoding.UTF8.GetBytes(Rows + "3,");
        Assert.Equal(Rows, Csv.ReadCompleteRows(new MemoryStream(bytes), bytes.Length));
        Assert.Null(Csv.ReadCompleteRows(new MemoryStream(bytes), bytes.Length - 1));

        // By its path, beside its writer, as the read without a bound.
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "spawns.csv");
        File.WriteAllText(path, Whole + "2026-11-09T23:00:02.000Z,Start,11,2,4,git.e");
        using var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        Assert.Equal(Whole, Csv.ReadCompleteRows(path, 1 << 20));
        Assert.Equal(Csv.ReadCompleteRows(path), Csv.ReadCompleteRows(path, (int)new FileInfo(path).Length));
        Assert.Null(Csv.ReadCompleteRows(path, (int)new FileInfo(path).Length - 1));
    }

    /// <summary>
    /// An export is made as administrator and kept where the signed-in account can read it, and the report folders can be
    /// where someone who is not an administrator may write (a run given --out). So an export reads nothing through a
    /// link: a link in the report folder (here to another folder, with a file of each kind the export reads) is left out
    /// with all it leads to, and a report folder that is itself a link is refused, with nothing written for it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnExportReadsNothingOfAReportFolderThroughALink(bool redact)
    {
        string elsewhere = Directory.CreateDirectory(Path.Combine(_dir, "elsewhere")).FullName;
        File.WriteAllText(Path.Combine(elsewhere, "events.csv"), Header + "2026-11-09T23:00:05.000Z,Start,99,9,4,from-another-folder.exe,,,,\r\n");
        File.WriteAllText(Path.Combine(elsewhere, "notes.md"), "from another folder");
        File.WriteAllText(Path.Combine(elsewhere, "data.bin"), "from another folder");
        string incidents = Directory.CreateDirectory(Path.Combine(_dir, "incidents")).FullName, exports = Path.Combine(_dir, "exports");
        string inc = Directory.CreateDirectory(Path.Combine(incidents, "20261109-230000-Leak-6b61654c")).FullName;
        File.WriteAllText(Path.Combine(inc, "events.csv"), Whole);
        File.WriteAllText(Path.Combine(inc, "report.md"), "# a report");
        var redaction = redact ? new RedactionInfo("someone", "SOMEPC", [@"C:\Users\someone"]) : null;
        string inside = Path.Combine(inc, "more"), planted = Path.Combine(incidents, "20261109-235900-Leak-00000000");
        bool linksGone;
        try
        {
            LinkHelper.MakeDirectoryLink(inside, elsewhere);
            Assert.True(File.Exists(Path.Combine(inside, "events.csv"))); // the link leads there
            string zip = Exporter.Export(inc, exports, redaction, p => p);
            using (var archive = ZipFile.OpenRead(zip))
            {
                Assert.Equal(["events.csv", "report.md"], archive.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
                using var r = new StreamReader(archive.GetEntry("events.csv")!.Open());
                Assert.Equal(Whole, r.ReadToEnd());
            }

            LinkHelper.MakeDirectoryLink(planted, elsewhere);
            var refused = Assert.Throws<UnauthorizedAccessException>(() => Exporter.Export(planted, exports, redaction, p => p));
            Assert.Contains("is a link", refused.Message);
            Assert.Throws<UnauthorizedAccessException>(() => Exporter.Export(planted + Path.DirectorySeparatorChar, exports, redaction, p => p));
            Assert.Equal([Path.GetFileName(zip)], Directory.GetFiles(exports).Select(Path.GetFileName)); // nothing was written for it
        }
        finally { linksGone = LinkHelper.DeleteLinkOnly(inside) & LinkHelper.DeleteLinkOnly(planted); }
        Assert.True(linksGone);
    }
}
