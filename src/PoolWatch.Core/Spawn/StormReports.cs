using System.Text;
using PoolWatch.Core.Findings;

namespace PoolWatch.Core.Spawn;

/// <summary>
/// Writes a spawn-storm report folder (<see cref="StormFolder"/>): report.md and report.json, each replaced in one step
/// (a reader never sees half of one), and spawns.csv, to which each start is appended once, as whole rows, once its
/// process exited (every start held is appended on the folder's last write), so a folder written many times writes each
/// row once. Sampler thread only.
/// </summary>
public static class StormReports
{
    /// <summary>
    /// Writes <paramref name="f"/> into <paramref name="incidentsDir"/>, its folder made on its first write; puts the folder on
    /// its storms' latest findings; marks it written at <paramref name="nowMs"/> (the clock that only goes forward). Returns
    /// the bytes written.
    /// </summary>
    /// <param name="costLine">PoolWatch's own cost now, the report's last line.</param>
    /// <param name="checkWrite">The write policy's check of each path before it is written or replaced.</param>
    public static long Write(SpawnTracker tracker, StormFolder f, string incidentsDir, string costLine, Func<string, string> checkWrite, long nowMs)
    {
        f.Dir ??= FindingReport.NewDir(incidentsDir, new Finding { TimeUtc = f.FirstOpenedUtc, Kind = FindingKind.SpawnStorm, Title = "" }, f.Subject);
        Directory.CreateDirectory(checkWrite(f.Dir));
        long bytes = AppendTrace(f, checkWrite);
        var head = tracker.FolderFinding(f); // with the rows spawns.csv holds now
        bytes += FindingReport.Write(f.Dir, head, tracker.StormMarkdown(f) + "\n" + costLine + "\n", null, checkWrite);
        f.MarkWritten(nowMs);
        foreach (var s in f.Open.Append(f.Latest))
            if (s.LastFinding is { } lf) lf.ReportDir = f.Dir;
        return bytes;
    }

    /// <summary>
    /// <see cref="Write"/> within the report folders' write budget: a folder's first write and its last
    /// (<see cref="StormFolder.Final"/>) are always made; an update in between only while <paramref name="budget"/> has room
    /// to spare (<see cref="Incidents.WriteBudget.HasRoomToSpare"/>: the open incidents' reports, which the window reads,
    /// come first), else it stays due for a later sample. Returns the bytes written, or -1 when it was held back.
    /// </summary>
    public static long WriteWithin(Incidents.WriteBudget budget, SpawnTracker tracker, StormFolder f, string incidentsDir, string costLine,
        Func<string, string> checkWrite, long nowMs)
    {
        bool must = f.LastWriteMs is null || f.Final;
        if (!must && !budget.HasRoomToSpare(nowMs))
        {
            budget.Hold();
            return -1;
        }
        long bytes = Write(tracker, f, incidentsDir, costLine, checkWrite, nowMs);
        budget.Spend(bytes, nowMs, must);
        return bytes;
    }

    /// <summary>Appends the starts due to spawns.csv (made, with its header, when it is not there); returns the bytes written.</summary>
    private static long AppendTrace(StormFolder f, Func<string, string> checkWrite)
    {
        string path = Path.Combine(f.Dir!, "spawns.csv");
        var rows = f.TakeRows(all: f.Final);
        bool fresh = !File.Exists(path);
        if (rows.Count == 0 && !fresh) return 0;
        try
        {
            if (fresh)
            {
                // A new file (or one deleted meanwhile): its header and these rows, in one step.
                long written = FindingReport.Atomic(path, SpawnTracker.TraceHeader + "\n" + SpawnTracker.TraceRows(rows), checkWrite);
                f.TraceRows = rows.Count;
                return written;
            }
            var bytes = new UTF8Encoding(false).GetBytes(SpawnTracker.TraceRows(rows));
            // Whole rows in one write at the end of the file.
            using (var fs = new FileStream(checkWrite(path), FileMode.Append, FileAccess.Write, FileShare.Read)) fs.Write(bytes);
            f.TraceRows += rows.Count;
            Util.Profiler.Wrote(path, bytes.Length);
            return bytes.Length;
        }
        catch
        {
            f.PutBack(rows); // written at the next write instead
            throw;
        }
    }
}
