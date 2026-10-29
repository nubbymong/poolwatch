using System.Diagnostics;
using PoolWatch.Collectors;

namespace PoolWatch.AppTests;

/// <summary>
/// The PEB reader reads 64-bit processes and skips 32-bit (WOW64) ones, whose PEB has another layout. CI also runs these
/// tests on Windows on Arm, where the older IsWow64Process call says "not WOW64" for a 32-bit x86 app. Each test starts
/// its own cmd.exe in the test's TEMP folder; it waits on its input and ends when that closes.
/// </summary>
public sealed class PebReaderTests : IDisposable
{
    private readonly string _dir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "pw-peb-" + Guid.NewGuid().ToString("N")));
    private readonly List<Process> _started = new();

    public PebReaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var p in _started)
        {
            p.StandardInput.Close();
            if (!p.WaitForExit(10_000)) p.Kill(); // the test's own child, if it did not end
            p.Dispose();
        }
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private Process StartCmd(string folder)
    {
        var psi = new ProcessStartInfo(Path.Combine(folder, "cmd.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, WorkingDirectory = _dir,
        };
        psi.ArgumentList.Add("/d");
        var p = Process.Start(psi) ?? throw new InvalidOperationException("cmd.exe did not start");
        _started.Add(p);
        return p;
    }

    [Fact]
    public void ReadsTheCurrentDirectoryOfA64BitProcess()
    {
        // System32's cmd.exe is native: x64 on x64 Windows, Arm64 on Windows on Arm (read from this x64 test under emulation).
        var cmd = StartCmd(Environment.SystemDirectory);
        var r = Assert.NotNull(PebReader.Read(cmd.Id, 0, []));
        Assert.Equal(_dir, r.cwd?.TrimEnd('\\'), ignoreCase: true);
    }

    [Fact]
    public void SkipsA32BitProcess()
    {
        // SysWOW64 holds the 32-bit x86 programs, on x64 and on Arm64 Windows alike.
        var cmd = StartCmd(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86));
        Assert.Null(PebReader.Read(cmd.Id, 0, []));
    }
}
