namespace PoolWatch.Engine;

/// <summary>Small file logger, size-capped (rolls to .1 at 2 MB).</summary>
internal sealed class Log
{
    private readonly object _lock = new();
    private readonly string _path;
    private readonly Func<string, string> _checkWrite;
    private const long MaxBytes = 2 * 1024 * 1024;

    /// <param name="checkWrite">Review round 2 (S3): the write policy (PathGuard.CheckReal), applied to every append and to
    /// the roll-over, so a link planted in place of the log never redirects an elevated write. Null: no check (tests).</param>
    public Log(string path, Func<string, string>? checkWrite = null)
    {
        _checkWrite = checkWrite ?? (p => p);
        _path = _checkWrite(path);
    }

    public bool EchoToConsole { get; set; }

    public void Info(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
        lock (_lock)
        {
            try
            {
                var fi = new FileInfo(_path);
                if (fi.Exists && fi.Length > MaxBytes) File.Move(_checkWrite(_path), _checkWrite(_path + ".1"), overwrite: true);
                File.AppendAllText(_checkWrite(_path), line + Environment.NewLine);
                Core.Util.Profiler.Wrote(_path, line.Length + 2);
            }
            catch { /* logging must never take the app down */ }
            if (EchoToConsole) Console.WriteLine(line);
        }
    }
}
