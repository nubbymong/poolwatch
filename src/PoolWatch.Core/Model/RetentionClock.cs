using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PoolWatch.Core.Model;

/// <summary>
/// How long retention has kept each thing it judges (a daily data folder, an incident, an export, or a file directly
/// in their folders) as it is, counted on the PC's working-time timer and never on the system clock. Each retention
/// check lists every thing with its stamp (<see cref="Seen"/>, <see cref="Stamp"/>): for a file, its last-write time and
/// length; for a folder, its own last-write time and the name, length and last-write time of every file and folder in
/// it but a link (a link is left out, with what is behind it, and a folder that is itself a link is stamped by its own
/// last-write time alone: <c>AppPaths.StampOf</c>), so a row added to a file in a folder changes the folder's stamp,
/// although the folder's own last-write time stays as it was. A thing seen for the first time as it is now (a name not
/// seen before, or a stamp other than the one it was seen with) starts at 0 at that check, and its time goes on with
/// the timer from there; it is deleted only once that time is over the retention period and <see cref="Allowance"/>
/// (which covers a timer a little fast). Its stamp took the value it has at the last write into it before the check
/// that first saw it, so the time counted for it is never more than the time since it or anything in it was last
/// written, whatever the system clock said then or did since: a change of the clock deletes nothing early. A stamp is
/// only compared with the one seen before, to tell that the thing changed: a write that leaves every name, length and
/// last-write time in it as it was (a file written over with as many bytes, with the clock at the very time it was last
/// written) is no change to it.
/// <list type="bullet">
/// <item>The timer (<c>forwardMs</c>: QueryUnbiasedInterruptTime, in milliseconds) goes on only while Windows runs and
/// is awake. It stands still while the PC sleeps or hibernates (a Shut down with Fast Startup on hibernates Windows'
/// own session), it starts from 0 when Windows starts, and no clock setting moves it.</item>
/// <item>Across a restart of PoolWatch (the constructor, from the saved <see cref="State"/>), the timer now less the
/// timer at the last save is added, never below 0: within one start of Windows that is the time Windows was awake
/// meanwhile, and after Windows started again it is at most the time since that start, so never more than really
/// passed.</item>
/// </list>
/// What it cannot count keeps things longer, never shorter: the time from the last write into a thing to the check
/// that first sees it as it is (at most 6 hours of it while PoolWatch runs), the time the PC was asleep, hibernated or
/// off, the time since the last save when Windows started again after PoolWatch stopped without saving, and, the first
/// time this version runs or when its saved state cannot be read, the whole age of what is there then. A record is let
/// go once a check that listed its folder whole does not find the thing. A thing in a folder that cannot be listed is
/// still judged by its own path when it has a record (<see cref="NamesIn"/>), and the rest of a thing that could not be
/// deleted whole stays due when it holds only what was in it then (<see cref="StillDue"/>; <c>AppPaths.ApplyRetention</c>
/// tells both). One thread at a time (the sampler's, and the engine's stop once it is joined).
/// </summary>
public sealed class RetentionClock
{
    /// <summary>A saved state longer than this is not read, and a save leaves out the records that would make it longer (<see cref="State.ToJson"/>).</summary>
    public const int MaxJsonChars = 4 * 1024 * 1024;

    /// <summary>The largest time a saved state may hold, far beyond any a PC counts: adding a timer's value to it never overflows.</summary>
    private const long MaxMs = long.MaxValue / 4;

    /// <summary>On top of the retention period, a thousandth of it: the PC's timer may run a little fast.</summary>
    public static TimeSpan Allowance(int days) => TimeSpan.FromDays(days) / 1000;

    /// <summary>
    /// A thing a retention check lists: its name in the data folder, its folder's and its own (<c>data\20261024</c>), and its
    /// stamp (<see cref="Stamp"/>), null when any of it could not be read.
    /// </summary>
    public readonly record struct Seen(string Name, long? Stamp);

    /// <summary>The record of a thing: the stamp it was first seen with, and the time counted then (<see cref="CountedMs"/>).</summary>
    public readonly record struct Kept(long Stamp, long FirstSeenMs);

    /// <summary>A file or folder in a folder retention judges: its name below that folder, its length (-1 for a folder), and its last-write time in ticks (UTC).</summary>
    public readonly record struct Entry(string Name, long Length, long WrittenTicks);

    /// <summary>
    /// A thing's stamp: its own last-write time in ticks (UTC), its length (-1 for a folder) and, for a folder, every file
    /// and folder in it (<paramref name="inside"/>, in any order), folded into one number (the first 8 bytes of a SHA-256
    /// over them, in name order), the same in every process. A change of any name, length or last-write time in it gives
    /// another stamp.
    /// </summary>
    public static long Stamp(long writtenTicks, long length, IEnumerable<Entry> inside)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var numbers = new byte[16];
        Add(writtenTicks, length);
        foreach (var e in inside.OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            byte[] name = Encoding.UTF8.GetBytes(e.Name);
            BinaryPrimitives.WriteInt32LittleEndian(numbers, name.Length);
            hash.AppendData(numbers, 0, 4);
            hash.AppendData(name);
            Add(e.WrittenTicks, e.Length);
        }
        return BinaryPrimitives.ReadInt64LittleEndian(hash.GetHashAndReset());

        void Add(long ticks, long size)
        {
            BinaryPrimitives.WriteInt64LittleEndian(numbers, ticks);
            BinaryPrimitives.WriteInt64LittleEndian(numbers.AsSpan(8), size);
            hash.AppendData(numbers);
        }
    }

    /// <summary>What a save keeps: the timer when it was made, the time counted by then, and each thing's record by its name.</summary>
    public sealed record State(long SavedMs, long CountedMs, IReadOnlyDictionary<string, Kept> Items)
    {
        /// <summary>
        /// The state as JSON, never longer than <see cref="MaxJsonChars"/>: the records go in the earliest first seen first,
        /// and those that would make it longer are left out (a later start then sees those things for the first time, so
        /// they are kept longer, never shorter).
        /// </summary>
        public string ToJson()
        {
            const string end = "]}";
            var sb = new StringBuilder();
            sb.Append("{\"savedMs\":").Append(SavedMs).Append(",\"countedMs\":").Append(CountedMs).Append(",\"items\":[");
            bool first = true;
            foreach (var (name, kept) in Items.OrderBy(i => i.Value.FirstSeenMs).ThenBy(i => i.Key, StringComparer.Ordinal))
            {
                string item = new JsonObject { ["name"] = name, ["stamp"] = kept.Stamp, ["firstSeenMs"] = kept.FirstSeenMs }.ToJsonString();
                if (sb.Length + (first ? 0 : 1) + item.Length + end.Length > MaxJsonChars) break;
                if (!first) sb.Append(',');
                sb.Append(item);
                first = false;
            }
            return sb.Append(end).ToString();
        }

        /// <summary>
        /// The saved state, or null when the text is not one (never throws): oversize text, a missing or unreadable value, a
        /// time below 0 or beyond any a PC counts, a record first seen after the time counted, an empty name, or a name twice
        /// (in any case).
        /// </summary>
        public static State? Parse(string json)
        {
            if (json.Length > MaxJsonChars) return null;
            try
            {
                if (JsonNode.Parse(json) is not JsonObject o || !Ms(o["savedMs"], out long savedMs) || !Ms(o["countedMs"], out long counted)
                    || o["items"] is not JsonArray list) return null;
                var items = new Dictionary<string, Kept>(StringComparer.OrdinalIgnoreCase);
                foreach (var node in list)
                {
                    if (node is not JsonObject item || item["name"] is not JsonValue n || !n.TryGetValue(out string? name) || string.IsNullOrEmpty(name)
                        || item["stamp"] is not JsonValue w || !w.TryGetValue(out long stamp)
                        || !Ms(item["firstSeenMs"], out long firstSeen) || firstSeen > counted || !items.TryAdd(name, new Kept(stamp, firstSeen))) return null;
                }
                return new State(savedMs, counted, items);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { return null; }
        }

        private static bool Ms(JsonNode? node, out long ms)
        {
            ms = 0;
            return node is JsonValue v && v.TryGetValue(out ms) && ms is >= 0 and <= MaxMs;
        }
    }

    private readonly Func<long> _forwardMs;
    private readonly Action<State>? _save;
    private Dictionary<string, Kept> _items;
    // The timer at the last read (the highest it was), and the time counted up to then.
    private long _atMs, _counted;

    /// <param name="forwardMs">The PC's working-time timer, in milliseconds (QueryUnbiasedInterruptTime; a test gives its own).</param>
    /// <param name="saved">The state the last save kept; null when there is none or it could not be read.</param>
    /// <param name="save">Keeps a state (a file); a save that throws is let go, the state staying in memory.</param>
    public RetentionClock(Func<long> forwardMs, State? saved, Action<State>? save)
    {
        _forwardMs = forwardMs;
        _save = save;
        _atMs = forwardMs();
        _items = saved is null ? new(StringComparer.OrdinalIgnoreCase) : new(saved.Items, StringComparer.OrdinalIgnoreCase);
        // The timer since the save: within one start of Windows the time Windows was awake meanwhile; after Windows started
        // again at most the time since then (the timer counts from that start), so never more than really passed.
        _counted = saved is null ? 0 : saved.CountedMs + Math.Max(0, _atMs - saved.SavedMs);
    }

    /// <summary>The time counted so far, in milliseconds on the timer, across PoolWatch's restarts.</summary>
    public long CountedMs
    {
        get
        {
            Advance();
            return _counted;
        }
    }

    /// <summary>Each thing's record, by its name.</summary>
    public IReadOnlyDictionary<string, Kept> Items => _items;

    /// <summary>
    /// A retention check of what <paramref name="seen"/> lists, listed before this call (the timer is read here, after it):
    /// a thing seen for the first time as it is now gets a record at the time counted now, and the names of the things
    /// kept as they are for longer than <paramref name="days"/> days and the allowance come back, to be deleted. A thing
    /// whose stamp could not be read keeps its record as it was and is never named. The record of a thing not
    /// listed is let go, unless its folder (its name up to the first '\') is in <paramref name="notListedWhole"/>.
    /// </summary>
    public IReadOnlyList<string> Check(int days, IReadOnlyCollection<Seen> seen, IReadOnlyCollection<string> notListedWhole)
    {
        Advance();
        long limit = (long)(TimeSpan.FromDays(days) + Allowance(days)).TotalMilliseconds;
        var next = new Dictionary<string, Kept>(StringComparer.OrdinalIgnoreCase);
        var due = new List<string>();
        foreach (var s in seen)
        {
            bool had = _items.TryGetValue(s.Name, out var kept);
            if (s.Stamp is not { } stamp)
            {
                if (had) next[s.Name] = kept;
                continue;
            }
            if (!had || kept.Stamp != stamp) kept = new Kept(stamp, _counted);
            next[s.Name] = kept;
            if (_counted - kept.FirstSeenMs > limit) due.Add(s.Name);
        }
        foreach (var (name, kept) in _items)
            if (!next.ContainsKey(name) && notListedWhole.Contains(FolderOf(name), StringComparer.OrdinalIgnoreCase)) next[name] = kept;
        _items = next;
        return due;
    }

    /// <summary>
    /// The names of the things recorded in the folder <paramref name="folder"/> (a name up to its first '\'), so a folder
    /// that cannot be listed can still have what is due in it judged by each one's own path.
    /// </summary>
    public IReadOnlyList<string> NamesIn(string folder) =>
        _items.Keys.Where(n => FolderOf(n).Equals(folder, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>
    /// Lets the record of <paramref name="name"/> go: retention looked for the thing at its own path, in a folder that could
    /// not be listed (so <see cref="Check"/> would keep the record), and it is not there. If it is there after all, it is
    /// seen anew, so kept longer, never shorter.
    /// </summary>
    public void Forget(string name) => _items.Remove(name);

    /// <summary>
    /// A thing retention found due and could not delete whole, whose rest holds only what was in it then (the caller
    /// tells): its record takes the rest's stamp (<paramref name="stamp"/>) and keeps the time the thing was first seen,
    /// so the next check finds the rest due as the thing was, instead of seeing it for the first time. Nothing is done
    /// for a name without a record.
    /// </summary>
    public void StillDue(string name, long stamp)
    {
        if (_items.TryGetValue(name, out var kept)) _items[name] = kept with { Stamp = stamp };
    }

    /// <summary>Keeps the state: the timer now, the time counted by now, and the records.</summary>
    public void Save()
    {
        Advance();
        var state = new State(_atMs, _counted, new Dictionary<string, Kept>(_items, StringComparer.OrdinalIgnoreCase));
        try { _save?.Invoke(state); }
        catch { } // kept in memory: the next save tries again, and a restart counts from the last save that was kept
    }

    /// <summary>Counts the timer on to now; a timer read lower than before (never, within one start of Windows) counts nothing, then or later.</summary>
    private void Advance()
    {
        long ms = _forwardMs();
        if (ms <= _atMs) return;
        _counted += ms - _atMs;
        _atMs = ms;
    }

    private static string FolderOf(string name)
    {
        int slash = name.IndexOf('\\');
        return slash < 0 ? name : name[..slash];
    }
}
