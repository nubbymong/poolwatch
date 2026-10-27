using System.Text;
using PoolWatch.Core.Findings;

namespace PoolWatch.Core.Experiments;

/// <summary>
/// Your notes on the timeline ("lag", "closed ChatGPT"), from the tray Mark item, the hotkey, or
/// <c>PoolWatch.exe --mark "text"</c> (which drops a request file the running instance picks up on its next sample;
/// the requests folder is only ever read, at most <see cref="MaxPerTick"/> files a tick).
/// </summary>
public static class Marks
{
    public const int MaxLength = 200;
    public const int MaxPerTick = 20;

    public static string Clean(string text)
    {
        var sb = new StringBuilder(Math.Min(text.Length, MaxLength));
        foreach (char c in text.Trim())
        {
            if (sb.Length >= MaxLength) break;
            sb.Append(char.IsControl(c) ? ' ' : c);
        }
        return sb.ToString().Trim();
    }

    /// <summary>Null for an empty note.</summary>
    public static Finding? Finding(string text, DateTime timeUtc, string source)
    {
        string note = Clean(text);
        if (note.Length == 0) return null;
        return new Finding
        {
            TimeUtc = timeUtc, Kind = FindingKind.Mark, Title = "Mark: " + note,
            Data = new Dictionary<string, object?> { ["note"] = note, ["source"] = source },
        };
    }

    /// <summary>A request file for the running instance: <c>mark-&lt;ticks&gt;-&lt;n&gt;.txt</c>.</summary>
    public static string RequestName(DateTime utc, int n) => $"mark-{utc.Ticks}-{n}.txt";

    /// <summary>Reads and deletes pending requests (oldest first), in one pass. Returns (time, text) pairs. The running
    /// instance uses a <see cref="MarkInbox"/> (no listing every tick; files it cannot delete never block new marks).</summary>
    public static List<(DateTime timeUtc, string text)> TakeRequests(string dir, Func<string, string> checkWrite) =>
        new MarkInbox(dir, checkWrite).Take(DateTime.UtcNow);
}

/// <summary>
/// The running instance's view of the mark requests folder (C6, review Part 2). The folder is listed only when its
/// last-write time changed (a file was added or removed), when a request is waiting for a retry, or once every
/// <see cref="RelistEvery"/> as a safety net, not on every tick. A request whose file cannot be deleted is still recorded,
/// once, and then skipped by name, so twenty undeletable files can no longer fill the per-tick budget and block every new
/// mark; one that cannot be read (still being written) is retried, at most <see cref="MaxAttempts"/> times.
/// </summary>
public sealed class MarkInbox
{
    public static readonly TimeSpan RelistEvery = TimeSpan.FromSeconds(60);
    public const int MaxAttempts = 5;
    private readonly string _dir;
    private readonly Func<string, string> _checkWrite;
    private readonly Func<string, DateTime?> _stamp;
    private readonly HashSet<string> _skip = new(StringComparer.OrdinalIgnoreCase);     // handled, but the file stayed
    private readonly Dictionary<string, int> _attempts = new(StringComparer.OrdinalIgnoreCase);
    private DateTime? _lastStamp;
    private DateTime _lastList = DateTime.MinValue;
    private bool _relist = true;

    /// <param name="folderStamp">Test seam: the folder's last-write time, null when it does not exist.</param>
    public MarkInbox(string dir, Func<string, string> checkWrite, Func<string, DateTime?>? folderStamp = null)
    {
        _dir = dir;
        _checkWrite = checkWrite;
        _stamp = folderStamp ?? (d => Directory.Exists(d) ? Directory.GetLastWriteTimeUtc(d) : null);
    }

    /// <summary>How many times the folder has been listed (for tests and the overhead report).</summary>
    public int Listings { get; private set; }

    /// <summary>New requests (oldest first, at most <see cref="Marks.MaxPerTick"/>), each recorded once.</summary>
    public List<(DateTime timeUtc, string text)> Take(DateTime nowUtc)
    {
        var found = new List<(DateTime, string)>();
        DateTime? stamp;
        try { stamp = _stamp(_dir); } catch { stamp = null; }
        if (stamp is null) { _lastStamp = null; return found; }
        if (!_relist && _attempts.Count == 0 && stamp == _lastStamp && nowUtc - _lastList < RelistEvery) return found;
        _lastStamp = stamp;
        _lastList = nowUtc;
        _relist = false;
        Listings++;
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<string> paths;
        try { paths = Directory.EnumerateFiles(_dir, "mark-*.txt").Order(StringComparer.Ordinal).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _relist = true; return found; }
        foreach (var path in paths)
        {
            string name = Path.GetFileName(path);
            present.Add(name);
            if (_skip.Contains(name)) continue;          // recorded before; its file could not be deleted
            if (found.Count >= Marks.MaxPerTick) { _relist = true; continue; } // the rest on the next tick
            string text;
            DateTime t;
            try
            {
                var info = new FileInfo(path);
                text = info.Length <= 4096 ? File.ReadAllText(path) : "";
                var parts = Path.GetFileNameWithoutExtension(path).Split('-');
                t = parts.Length >= 2 && long.TryParse(parts[1], out long ticks) && ticks > 0 && ticks < DateTime.MaxValue.Ticks
                    ? new DateTime(ticks, DateTimeKind.Utc) : info.LastWriteTimeUtc;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still being written (or unreadable): retried on the next ticks, then given up so it never blocks the folder.
                int n = _attempts.GetValueOrDefault(name) + 1;
                if (n >= MaxAttempts) { _attempts.Remove(name); _skip.Add(name); }
                else _attempts[name] = n;
                continue;
            }
            _attempts.Remove(name);
            found.Add((t, text));
            try { File.Delete(_checkWrite(path)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _skip.Add(name); } // recorded once, then skipped
        }
        _skip.IntersectWith(present); // forget names whose files are gone
        foreach (var gone in _attempts.Keys.Where(k => !present.Contains(k)).ToList()) _attempts.Remove(gone);
        return found;
    }
}
