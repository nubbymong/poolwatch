using System.Text;
using System.Text.RegularExpressions;

namespace PoolWatch.Engine;

/// <summary>Small file logger. Each line opens the log for appending only, appends and closes it, sharing reading and
/// writing but not deleting: Windows writes each line at the true end of the file, so the log can be read and copied
/// while PoolWatch runs, other PoolWatch processes append to it too without writing over each other's lines, and the
/// log is never renamed part-way through a line. The log is rolled over to .1 at 2 MB only by the process that holds
/// the one-instance mutex, and only while it holds it (<see cref="AppPaths.OpenLog"/> passes that process's proof, an
/// <see cref="ExitSignal.Hold"/>, which only the steps that take the mutex make, as its constructors are private; the
/// roll-over runs through its <see cref="ExitSignal.Hold.WhileHeld"/>, which the proof is not let go during; a Log made
/// without one leaves it), so two processes never roll it at once. An append refused — a sharing or lock violation,
/// or an open that would have to wait for another program, which is given up at once — is tried again three times
/// over about a tenth of a second (a program that reads or copies the log in one go has let go by then). When it is
/// still refused, that line and every later one in this run go to a new file next to the log, named after the log, the
/// date and time and the process id, whose first line names the file it moves on from and says why. PoolWatch keeps
/// that file open, sharing nothing, while it writes there, so no other program can open it and refuse the append the
/// same way (CreateFileW, dwShareMode: an open is refused while a handle is open whose share mode does not allow its
/// access), and it rolls it over at 2 MB itself. It lets go of it only for that roll-over, each time it tries it. A
/// program that opens the file in that moment, or that makes the next file under its name first after the rename and
/// keeps it open (in a folder it may write), refuses the appends while it has it open: when it lets go within the
/// retries the file is kept again, otherwise the run moves on to another new file, with no line lost; a file that is
/// gone in that moment is made again, so no later line is lost. A program that may write the folder can still, as for
/// the log itself, put text of its own first in the next file, or make that name something PoolWatch cannot write, and
/// then the run's later lines are lost; the data folder lets only administrators and SYSTEM write (a run given --out
/// logs in that folder instead). A new file can be read once PoolWatch has stopped (its .1 once it has rolled over).
/// The newest five new files are kept, each with its .1, and so is every new file of another PoolWatch that is still
/// running.</summary>
internal sealed class Log : IDisposable
{
    private readonly object _lock = new();
    private readonly string _home;
    private readonly Func<string, string> _checkWrite;
    private readonly FileOpen _open;
    private readonly Func<DateTime> _now;
    private readonly Action<int> _wait;
    private readonly ExitSignal.Hold? _rollGate;
    private readonly Func<int, bool> _running;
    private string _path;
    private bool _keeping;
    private FileStream? _kept;
    private static int s_files;
    private const long MaxBytes = 2 * 1024 * 1024;
    private static readonly int[] RetryMs = [10, 25, 50];
    // Names tried for one new file, when one cannot be made (it exists, or the folder refuses it).
    private const int NamesPerNewFile = 3;
    // New files one line may start. A guard only: each new file is kept from its first byte, so a line can be refused
    // there only in the moment of a roll-over, by a program that opened the file let go for it, or that made the next
    // file under its name first after the rename and keeps it open (in a folder it may write).
    private const int NewFilesPerLine = 3;
    private const int NewFilesKept = 5;
    private const FileShare Shared = FileShare.ReadWrite;
    private const int SharingViolation = unchecked((int)0x80070020), LockViolation = unchecked((int)0x80070021);

    /// <param name="checkWrite">The write policy (PathGuard.CheckReal), applied to every open of the log or of a new
    /// file, to the roll-over and to each delete of an old new file: it looks for a link on the path before each of
    /// them and refuses a path where it finds one or cannot check, so none of them is made through a link found at that
    /// look. A line to a kept file goes through its handle, opened after such a look. Null: no check (tests).</param>
    /// <param name="now">The local time for each line and for a new file's name. Null: the system clock.</param>
    /// <param name="wait">The pause before each retry, in milliseconds. Null: Thread.Sleep.</param>
    /// <param name="rollGate">The proof that this process holds the one-instance mutex (only the steps that take the
    /// mutex make one): the log is rolled over only through its <see cref="ExitSignal.Hold.WhileHeld"/>, so only while
    /// the mutex is held. <see cref="AppPaths.OpenLog"/> passes it in the process that holds the mutex. Null when not
    /// given: a Log made without one never rolls the log over. A new file is always rolled over by the process that
    /// made it.</param>
    /// <param name="running">Whether a process id is that of a running process, for the prune: a new file of another
    /// PoolWatch that is still running is not deleted. Null: <see
    /// cref="System.Diagnostics.Process.GetProcessById(int)"/>.</param>
    /// <param name="open">How each line's file is opened (null: <see cref="NoWaitOpen.Open"/>, which never waits for
    /// another program — an open that would have to wait is given up and handled as a refused append). Tests stand in
    /// their own opener.</param>
    public Log(string path, Func<string, string>? checkWrite = null, Func<DateTime>? now = null, Action<int>? wait = null,
        ExitSignal.Hold? rollGate = null, Func<int, bool>? running = null, FileOpen? open = null)
    {
        _checkWrite = checkWrite ?? (p => p);
        _open = open ?? NoWaitOpen.Open;
        _now = now ?? (() => DateTime.Now);
        _wait = wait ?? Thread.Sleep;
        _rollGate = rollGate;
        _running = running ?? IsRunning;
        _home = _path = _checkWrite(path);
    }

    public bool EchoToConsole { get; set; }

    /// <summary>The file this run's lines go to now: the log, or the new file next to it once the file before it was
    /// refused. The lines before that are in this run's earlier files: the log, then each new file it moved on to, or
    /// their .1 after a roll-over, while they are kept (the newest five new files are; an older one of this run is
    /// pruned like any other). Each new file's first line names the file it moved on from; once that new file has
    /// rolled over, that line is in its .1, until a second roll-over replaces that .1. The notice after the service
    /// profile applied at start names this when that run ends; other messages name poolwatch.log, next to which the new
    /// file is.</summary>
    public string CurrentPath { get { lock (_lock) return _path; } }

    /// <summary>Lets go of a kept file, so it can be read; a later line opens that file for the line only, as for the
    /// log. The app keeps its log for its whole run and never calls this.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            _kept?.Dispose();
            _kept = null;
            _keeping = false;
        }
    }

    public void Info(string message)
    {
        var line = $"{_now():yyyy-MM-dd HH:mm:ss.fff} {message}";
        lock (_lock)
        {
            try
            {
                Append(line);
            }
            catch { /* logging must never take the app down */ }
            try
            {
                if (EchoToConsole) Console.WriteLine(line);
            }
            catch { /* nor must the console */ }
        }
    }

    private void Append(string line)
    {
        for (var moves = 0; ; moves++)
        {
            try
            {
                Retrying(() =>
                {
                    RollIfFull();
                    Write(line);
                });
                return;
            }
            catch (IOException e) when (IsHeld(e) && moves < NewFilesPerLine)
            {
                MoveOn(e); // throws when no new file could be made: then only this line is lost
            }
        }
    }

    private void Retrying(Action act)
    {
        for (var i = 0; ; i++)
        {
            try
            {
                act();
                return;
            }
            catch (IOException e) when (IsHeld(e) && i < RetryMs.Length)
            {
                _wait(RetryMs[i]);
            }
        }
    }

    // ERROR_SHARING_VIOLATION and ERROR_LOCK_VIOLATION: another handle's share mode or lock refuses this write. Any
    // other failure loses only that line, as before, and the next line tries the same file again (made again when it is
    // gone).
    private static bool IsHeld(IOException e) => e.HResult is SharingViolation or LockViolation;

    // A new file this run keeps is rolled over here; the log only through the gate, while the one-instance mutex is
    // held.
    private void RollIfFull()
    {
        if (_keeping) Roll();
        else _rollGate?.WhileHeld(Roll);
    }

    private void Roll()
    {
        var fi = new FileInfo(_path);
        if (!fi.Exists || fi.Length <= MaxBytes) return;
        string from = _checkWrite(_path), to = _checkWrite(_path + ".1");
        // Windows renames a file only while no open handle refuses it, so a kept file is let go for the rename. The
        // write below then keeps the file under this name again: the next one after the rename, made before its first
        // byte unless a program made that name first; this one after a refused rename (the file or its .1 is held). In
        // either case, when a program has that name open (it opened the file let go, or made the next one first and
        // keeps it open, in a folder it may write), the write finds it held: the file is kept again when the program
        // lets go within the retries, and otherwise the run moves on. A refused rename leaves the file growing past the
        // cap.
        _kept?.Dispose();
        _kept = null;
        try
        {
            File.Move(from, to, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // not rolled: the file grows on
        }
    }

    // A new file this run keeps, opened sharing nothing so no other program can open it while it is kept. The open is
    // given up rather than let wait, as every log open is.
    private FileStream Keep(string path, FileMode mode) => _open(path, mode, FileAccess.Write, FileShare.None, false);

    // Append-only access (FILE_APPEND_DATA without FILE_WRITE_DATA): Windows writes at the end of the file as it is at
    // the write, whatever another writer did since this open. An open that would otherwise have to wait for another
    // program is given up at once and handled as a refused append (the retries, then the move to a new file).
    private FileStream AppendOnly(string path) => _open(path, FileMode.OpenOrCreate, FileAccess.Write, Shared, true);

    private void Write(string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
        if (_keeping)
        {
            var kept = _kept ??= Keep(_checkWrite(_path), FileMode.OpenOrCreate);
            kept.Seek(0, SeekOrigin.End);
            kept.Write(bytes);
            kept.Flush();
        }
        else
        {
            using var s = AppendOnly(_checkWrite(_path));
            s.Write(bytes);
        }
        Core.Util.Profiler.Wrote(_path, line.Length + 2);
    }

    // Makes the new file next to the log and keeps it; its first line names the file it moves on from and says why.
    private void MoveOn(IOException why)
    {
        var refused = Path.GetFileName(_path);
        string dir = Path.GetDirectoryName(_home) ?? ".", stem = Path.GetFileNameWithoutExtension(_home), ext = Path.GetExtension(_home);
        Exception? failed = null;
        for (var i = 0; i < NamesPerNewFile; i++)
        {
            FileStream kept;
            string next;
            try
            {
                next = _checkWrite(Path.Combine(dir, $"{stem}-{_now():yyyyMMdd-HHmmss}-{Environment.ProcessId}-{Interlocked.Increment(ref s_files)}{ext}"));
                kept = Keep(next, FileMode.CreateNew);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                failed = e;
                continue;
            }
            _kept?.Dispose();
            _kept = kept;
            _keeping = true;
            _path = next;
            Write($"{_now():yyyy-MM-dd HH:mm:ss.fff} {refused} could not be written: another program has it open in a way "
                  + $"that refuses the append ({why.Message.Trim()}). This run's log goes on in this file.");
            Prune(dir, stem, ext);
            return;
        }
        throw failed!;
    }

    // Keeps the newest few new files, each with its .1 (a .1 whose file is gone counts as one), so holds at many starts
    // cannot fill the folder. Newest is by the name's date and time, then process id and number as numbers. A new file
    // of another PoolWatch that is still running is not deleted, nor can one another PoolWatch keeps open be: it stays,
    // and so does its .1 (a file whose process id a process other than the pruning one has taken since stays until that
    // one ends too). This run's own earlier new files are deleted like any other, and so is a stopped PoolWatch's file
    // whose process id the pruning PoolWatch has taken since.
    private void Prune(string dir, string stem, string ext)
    {
        try
        {
            var name = new Regex("^" + Regex.Escape(stem) + @"-(\d{8}-\d{6})-(\d{1,10})-(\d{1,10})" + Regex.Escape(ext) + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var old = Directory.GetFiles(dir)
                .Select(f => Path.GetFileName(f))
                .Select(f => f.EndsWith(".1", StringComparison.Ordinal) ? f[..^2] : f)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(f => (File: Path.Combine(dir, f), M: name.Match(f)))
                .Where(x => x.M.Success)
                .Select(x => (x.File, At: x.M.Groups[1].Value, Pid: Number(x.M.Groups[2].Value), N: Number(x.M.Groups[3].Value)))
                .OrderByDescending(x => x.At, StringComparer.Ordinal)
                .ThenByDescending(x => x.Pid)
                .ThenByDescending(x => x.N)
                .Skip(NewFilesKept)
                .Where(x => !RunsElsewhere(x.Pid))
                .Select(x => x.File)
                .ToList();
            foreach (var f in old)
            {
                try
                {
                    if (File.Exists(f)) File.Delete(_checkWrite(f));
                }
                catch { continue; } // kept by another PoolWatch, or refused: it and its .1 are left for a later prune
                try
                {
                    if (File.Exists(f + ".1")) File.Delete(_checkWrite(f + ".1"));
                }
                catch { /* left for a later prune */ }
            }
        }
        catch { /* the folder could not be listed, or a process could not be looked up: nothing is deleted */ }
    }

    private static long Number(string digits) => long.Parse(digits, System.Globalization.CultureInfo.InvariantCulture);

    private bool RunsElsewhere(long pid) => pid != Environment.ProcessId && pid is > 0 and <= int.MaxValue && _running((int)pid);

    // A process id that names no running process throws ArgumentException. Any other failure is not caught here: the
    // prune then deletes nothing this time.
    internal static bool IsRunning(int pid)
    {
        try
        {
            using var _ = System.Diagnostics.Process.GetProcessById(pid);
            return true;
        }
        catch (ArgumentException) { return false; }
    }
}
