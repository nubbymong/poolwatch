using System.Globalization;
using PoolWatch.Native;

namespace PoolWatch.Engine;

/// <summary>
/// What one run of the token-leak test's hidden helper is started with (<see cref="HelperCommand.For"/>): cmd.exe by its
/// full System32 path, a command line that holds no path, System32 as the working folder, the whole environment the run
/// gets, and whether its job ends it with PoolWatch.
/// </summary>
internal sealed record HelperStart(string Application, string CommandLine, string WorkingDirectory, IReadOnlyDictionary<string, string> Environment,
    bool EndWithPoolWatch);

/// <summary>Who started a logon check's helper: PoolWatch's process ID and its creation time (a FILETIME), which tell it from a later process given the same ID.</summary>
internal readonly record struct HelperParent(int Pid, long CreationTime);

/// <summary>
/// How the helper is started. A hidden console process (cmd.exe with CREATE_NO_WINDOW) starts it, and the helper attaches to
/// that console: its "inherited console" children then share a console that has no window, and the helper is never in
/// front. cmd.exe gets a fixed command line, <c>/d /v:off /s /c "%POOLWATCH_HELPER_LINE%"</c>, that holds no path: /d turns
/// AutoRun off, /v:off turns off the expansion of <c>!NAME!</c> (which a registry value can turn on), and /s makes cmd
/// remove exactly the first and the last quote. The helper's own command line is the value of that variable, in the
/// environment cmd.exe is given: cmd replaces <c>%POOLWATCH_HELPER_LINE%</c> once and does not look for <c>%</c> again in
/// what it put in, so a path with <c>%NAME%</c> or <c>!</c> in it reaches the helper as written. Every path in that line is
/// quoted, so cmd reads no character in a path as one of its own (<c>&amp; | &lt; &gt; ( ) ^</c>), and quoted by the rules
/// the helper's command-line parser follows (CommandLineToArgvW): backslashes in front of a quote are doubled, so a path
/// that ends in a backslash keeps its closing quote and every argument after it.
/// </summary>
internal static class HelperCommand
{
    /// <summary>The variable that holds the helper's command line in cmd.exe's environment.</summary>
    public const string LineVariable = "POOLWATCH_HELPER_LINE";

    /// <summary>
    /// The start of one helper run: <paramref name="exe"/> (PoolWatch.exe itself) with <c>--token-leak-test children file</c>,
    /// and for the logon check <c>--token-test-parent pid creation-time</c>.
    /// </summary>
    public static HelperStart For(string exe, int children, string resultFile, HelperParent? parent, bool endWithPoolWatch)
    {
        string system = Environment.SystemDirectory;
        string cmd = Path.Combine(system, "cmd.exe");
        var line = new List<string> { QuotePath(exe), "--token-leak-test", children.ToString(CultureInfo.InvariantCulture), QuotePath(resultFile) };
        if (parent is { } p)
            line.AddRange(["--token-test-parent", p.Pid.ToString(CultureInfo.InvariantCulture), p.CreationTime.ToString(CultureInfo.InvariantCulture)]);
        return new HelperStart(cmd, $"{QuotePath(cmd)} /d /v:off /s /c \"%{LineVariable}%\"", system, Variables(system, string.Join(' ', line)), endWithPoolWatch);
    }

    /// <summary>
    /// <paramref name="path"/> in quotes, as CommandLineToArgvW reads it back whole: each run of backslashes in front of a
    /// quote, and the run at the end (in front of the closing quote), is doubled. A Windows path cannot hold a quote; one
    /// is escaped all the same.
    /// </summary>
    internal static string QuotePath(string path)
    {
        var s = new System.Text.StringBuilder("\"");
        int slashes = 0;
        foreach (char c in path)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') s.Append('\\', slashes * 2 + 1).Append('"');
            else s.Append('\\', slashes).Append(c);
            slashes = 0;
        }
        return s.Append('\\', slashes * 2).Append('"').ToString();
    }

    /// <summary>
    /// The whole environment of cmd.exe and the helper: Windows' own folders, read from Windows (GetSystemDirectory and the
    /// known folders), never taken from PoolWatch's environment, which comes from the account's profile. So no variable
    /// that changes how .NET starts (DOTNET_, COMPlus_, CORECLR_, COR_, DOTNET_ROOT among them) or any other reaches them;
    /// only the helper's command line is added. The Program Files folders are there because a framework-dependent
    /// PoolWatch.exe looks for the .NET runtime in Program Files\dotnet through them when Windows has no install location
    /// for it.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> Variables(string systemDirectory, string helperLine)
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrEmpty(windows)) windows = Path.GetDirectoryName(systemDirectory) ?? systemDirectory;
        string temp = Path.Combine(windows, "Temp");
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ComSpec"] = Path.Combine(systemDirectory, "cmd.exe"),
            ["Path"] = systemDirectory + ";" + windows,
            ["ProgramFiles"] = programFiles,
            ["ProgramFiles(x86)"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            ["ProgramW6432"] = programFiles,
            ["SystemDrive"] = Path.GetPathRoot(windows)!.TrimEnd('\\'),
            ["SystemRoot"] = windows,
            ["TEMP"] = temp,
            ["TMP"] = temp,
            ["windir"] = windows,
            [LineVariable] = helperLine,
        };
    }
}

/// <summary>
/// One run of the token-leak test's hidden helper as the guard holds it: the process it started (cmd.exe, which waits for
/// the helper) and the job it runs in. A test passes a fake: no test starts a process.
/// </summary>
internal interface IHelperRun : IDisposable
{
    /// <summary>True once the started process has ended (within <paramref name="milliseconds"/>).</summary>
    bool WaitForExit(int milliseconds);

    int ExitCode { get; }

    /// <summary>Ends the helper and every process it started: the processes in its job, through the job's handle.</summary>
    void End();
}

/// <summary>
/// A helper run in a job of its own (<see cref="JobNative"/>): every helper runs in one, and one Windows will not start in
/// its job is not started at all. A check-only run's job (the setup guide's check, the token-leak
/// watch) ends it with PoolWatch: when the guard ends it, and when PoolWatch exits in any way, also when it is killed. Its
/// result is thrown away then, so nothing is lost, and PoolWatch.exe is not left in use after PoolWatch has gone. The logon
/// check's job does the same while its helper starts, and lets it outlive PoolWatch once it runs: orphaned, it offers to
/// protect the logon. Either is ended at the guard's timeout through the job, which ends exactly the processes in it.
/// </summary>
internal sealed class JobHelperRun : IHelperRun
{
    private readonly JobNative.IStarter _starter;
    private nint _job, _process;

    private JobHelperRun(JobNative.IStarter starter, nint job, nint process)
    {
        _starter = starter;
        _job = job;
        _process = process;
    }

    /// <summary>
    /// Starts <paramref name="start"/> in a new job. Throws, with nothing left running, when Windows refuses: whatever the
    /// start got as far as running is ended through its job, and the job's only handle is closed.
    /// </summary>
    public static JobHelperRun Start(HelperStart start, JobNative.IStarter? starter = null)
    {
        starter ??= JobNative.Win32.Instance;
        nint job = JobNative.CreateKillOnClose();
        try
        {
            return new JobHelperRun(starter, job, JobNative.StartInJob(job, start.Application, start.CommandLine, start.WorkingDirectory, start.Environment,
                outlive: !start.EndWithPoolWatch, starter));
        }
        catch
        {
            starter.EndJob(job);
            starter.Close(job);
            throw;
        }
    }

    public bool WaitForExit(int milliseconds) => JobNative.WaitForExit(_process, milliseconds);

    public int ExitCode => JobNative.ExitCode(_process);

    /// <summary>Ends every process in this run's own job (TerminateJobObject on its handle), and only those.</summary>
    public void End() => _starter.EndJob(_job);

    /// <summary>
    /// Closes the handles. Closing the job's only handle ends whatever still runs in a check-only run's job; a logon check's
    /// helper runs on.
    /// </summary>
    public void Dispose()
    {
        _starter.Close(Interlocked.Exchange(ref _process, 0));
        _starter.Close(Interlocked.Exchange(ref _job, 0));
    }
}

/// <summary>
/// One helper run's result file as the guard holds it. The guard makes it new before the helper starts (a GUID name in its
/// admin-only folder), and holds it open from then on: reading and writing for itself, and sharing it only for writing
/// (which only administrators may), never for reading or deleting. So while the run lasts no other program can open it to
/// read it, or to rename or delete it. A program that opens it only to read its attributes (Windows checks no sharing for
/// that) can keep its name listed after the run, unreadable, until it closes that handle; nothing reads the file then. The
/// helper writes its result into it (<see cref="TokenLeakGuard.WriteResult"/>), and the guard reads it through its own
/// handle once the helper has ended. Windows deletes the file once the last handle on it closes (FILE_FLAG_DELETE_ON_CLOSE;
/// the guard's closes when the run is over, whatever its end, and when PoolWatch exits in any way, also when it is killed).
/// </summary>
internal sealed class HelperResultFile : IDisposable
{
    /// <summary>A result is never this large: a larger file is not read.</summary>
    internal const int MaxLength = 64 * 1024;
    private readonly FileStream _stream;

    private HelperResultFile(string path, FileStream stream)
    {
        Path = path;
        _stream = stream;
    }

    public string Path { get; }

    /// <summary>Makes <paramref name="path"/> new (it fails if a file of that name is there) and holds it open.</summary>
    public static HelperResultFile Create(string path) =>
        new(path, new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Write, 4096, FileOptions.DeleteOnClose));

    /// <summary>What the helper wrote, read through the guard's own handle; null, with why, when it cannot be read.</summary>
    public (string? text, string? problem) ReadAll()
    {
        try
        {
            _stream.Flush();
            if (_stream.Length > MaxLength) return (null, "the result file is too large");
            _stream.Position = 0;
            var bytes = new byte[_stream.Length];
            _stream.ReadExactly(bytes);
            return (System.Text.Encoding.UTF8.GetString(bytes), null);
        }
        catch (Exception ex) { return (null, "the result could not be read: " + ex.Message); }
    }

    public void Dispose() => _stream.Dispose();
}
