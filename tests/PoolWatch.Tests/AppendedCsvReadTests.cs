using System.IO.Compression;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Util;

namespace PoolWatch.Tests;

/// <summary>
/// A report folder's appended files (an incident's events.csv, a storm folder's spawns.csv) are read by an export up to
/// their last whole row: a row cut short in the moment of an append (or by a crash in it) is never in the zip as half a
/// row, and a value with a line break in it, cut inside its quotes, is not taken for a row's end.
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
}
