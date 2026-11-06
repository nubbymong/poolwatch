using PoolWatch.Core;
using PoolWatch.Core.Model;
using PoolWatch.Core.Util;
using PoolWatch.Engine;

namespace PoolWatch.AppTests;

/// <summary>
/// The daily CSV files hold text a standard user chooses: their process's image name and command line, and a volume
/// label (a USB disk reports as fixed). Opened in a spreadsheet, none of it runs as a formula.
/// </summary>
public sealed class CsvFormulaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pw-csvf-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public void TheDailyCsvFilesRunNoFormulaFromAProcessOrAVolumeLabel()
    {
        var paths = new AppPaths(Path.Combine(_root, "data"), null, secure: false);
        var now = new DateTime(2026, 10, 30, 10, 0, 0, DateTimeKind.Utc);
        const string image = "=HYPERLINK(CHAR(104),CHAR(88)).exe", command = "@SUM(1)|x", label = "=SUM(1,2)|x";
        using (var sink = new CsvSink(paths, new Settings(), verbose: true))
        {
            sink.WriteEvents([new LifecycleEvent { TimeUtc = now, Kind = LifecycleKind.Start, Pid = 42, ParentPid = 4, Image = image, CommandLine = command, Detail = "-x" }], now);
            sink.WriteVolumes([new VolumeInfo(@"E:\", "NTFS", label, false, "Fixed")], now);
        }
        string dir = paths.CsvDir(now);
        var ev = Csv.Split(File.ReadAllLines(Path.Combine(dir, "events.csv"))[1]);
        Assert.Equal(["Start", "42", "4"], new[] { ev[1], ev[2], ev[4] });
        Assert.Equal("'" + image, ev[5]);
        Assert.Equal("'" + command, ev[8]);
        Assert.Equal("'-x", ev[9]);
        var vol = Csv.Split(File.ReadAllLines(Path.Combine(dir, "volumes.csv"))[1]);
        Assert.Equal("'" + label, vol[3]);
        Assert.Equal(["E:\\", "NTFS", "False", "Fixed"], new[] { vol[1], vol[2], vol[4], vol[5] });
    }
}
