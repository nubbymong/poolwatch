using PoolWatch.Core;
using PoolWatch.Core.Model;
using PoolWatch.Engine;

namespace PoolWatch.AppTests;

/// <summary>
/// Retention on real folders in the test's own TEMP folder (their last-write times set by the test, as a clock set forward
/// or back would have set them, and rows written into daily folders by the CSV sink itself): what it deletes is what the
/// retention clock (<see cref="RetentionClock"/>) has kept as it is, the files in a folder included, for longer than the
/// period on the PC's working-time timer (a test's own here), whose state is kept in the data folder's cache and read
/// back at the next start. The engine counts on Windows' working-time timer, judges on its own job and saves as it stops.
/// </summary>
public sealed class RetentionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pw-retention-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly List<string> _log = new();
    private static readonly DateTime Now = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly long Period = (long)(TimeSpan.FromDays(7) + RetentionClock.Allowance(7)).TotalMilliseconds;
    private const long Minute = 60_000, Hour = 3_600_000, Day = 24 * Hour;
    private long _timer = 50_000_000;

    public RetentionTests() => _paths = new AppPaths(Path.Combine(_root, "data"), null, secure: false);

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    /// <summary>A daily data folder, an incident or an export (a folder, or a file with <paramref name="file"/>) last written <paramref name="ago"/> before <see cref="Now"/>.</summary>
    private string Item(string root, string name, TimeSpan ago, bool file = false)
    {
        string dir = Path.Combine(_paths.DataRoot, root);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        if (file)
        {
            File.WriteAllText(path, "x");
            File.SetLastWriteTimeUtc(path, Now - ago);
        }
        else
        {
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "samples.csv"), "x");
            Directory.SetLastWriteTimeUtc(path, Now - ago);
        }
        return path;
    }

    private static bool There(string path) => Directory.Exists(path) || File.Exists(path);

    /// <summary>A start of PoolWatch on the test's timer, after a save at <paramref name="savedMs"/> having counted <paramref name="counted"/> (none when null).</summary>
    private RetentionClock Start(long? savedMs = null, long counted = 0)
    {
        if (savedMs is { } ms)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_paths.RetentionStateFile)!);
            File.WriteAllText(_paths.RetentionStateFile, new RetentionClock.State(ms, counted, new Dictionary<string, RetentionClock.Kept>()).ToJson());
        }
        return _paths.OpenRetentionClock(() => _timer, _log.Add);
    }

    [Fact]
    public void WhatWasKeptAsItIsForLongerThanThePeriodIsDeletedAndTheRestKept()
    {
        // Last-write times 8 days, a year and 3 days old, and one 20 years ahead: none is judged by them, all are first seen
        // at this check.
        string oldDay = Item("data", "20261024", TimeSpan.FromDays(8)), yearOld = Item("incidents", "2025-10-24 File", TimeSpan.FromDays(365));
        string newDay = Item("data", "20261029", TimeSpan.FromDays(3)), ahead = Item("exports", "export.zip", -TimeSpan.FromDays(20 * 366), file: true);
        var clock = Start(savedMs: _timer);
        _paths.ApplyRetention(7, _log.Add, clock);
        Assert.True(There(oldDay) && There(yearOld) && There(newDay) && There(ahead));
        var saved = RetentionClock.State.Parse(File.ReadAllText(_paths.RetentionStateFile))!; // saved by the check, with what it saw
        Assert.Equal([@"data\20261024", @"data\20261029", @"exports\export.zip", @"incidents\2025-10-24 File"], saved.Items.Keys.Order(StringComparer.Ordinal));
        _timer += 6 * Hour;
        string later = Item("exports", "later.zip", TimeSpan.FromDays(30), file: true); // written after that check
        _timer += Period - 6 * Hour - Minute;
        _paths.ApplyRetention(7, _log.Add, clock);
        Assert.True(There(oldDay) && There(yearOld) && There(newDay) && There(ahead) && There(later)); // a minute short of the period
        _timer += 2 * Minute;
        _paths.ApplyRetention(7, _log.Add, clock);
        Assert.False(There(oldDay) || There(yearOld) || There(newDay) || There(ahead));
        Assert.True(There(later)); // first seen at the check after its write
        Assert.Contains(_log, l => Removed(l, @"data\20261024"));
        Assert.Contains(_log, l => Removed(l, @"exports\export.zip"));
    }

    /// <summary>Whether <paramref name="line"/> is a check's summary that names <paramref name="name"/> as removed (<see cref="AppPaths.RetentionSummary"/>).</summary>
    private static bool Removed(string line, string name) =>
        line.StartsWith("retention: removed ", StringComparison.Ordinal) && line[(line.IndexOf("awake time: ", StringComparison.Ordinal) + 12)..].Split(", ").Contains(name);

    /// <summary>
    /// A check that removes many things logs one line for them: the counts by folder and the first few names (the owner's
    /// log had 1,137 lines a day, one per folder removed).
    /// </summary>
    [Fact]
    public void ACheckLogsOneLineForWhatItRemoved()
    {
        var items = Enumerable.Range(0, 12).Select(i => Item("incidents", $"20261020-1000{i:D2}-SpawnStorm-bash", TimeSpan.FromDays(10))).ToList();
        items.Add(Item("data", "20261020", TimeSpan.FromDays(10)));
        var clock = Start(savedMs: _timer);
        _paths.ApplyRetention(7, _log.Add, clock);
        Assert.Equal(["retention: 13 things judged, none kept unchanged for over 7 days of the PC's awake time"], _log.Where(l => l.StartsWith("retention: ")));
        _log.Clear();
        _timer += Period + Minute;
        _paths.ApplyRetention(7, _log.Add, clock);
        Assert.All(items, i => Assert.False(There(i)));
        string line = Assert.Single(_log);
        Assert.StartsWith("retention: removed 13 of 13 things (incidents 12, data 1) kept unchanged for over 7 days of the PC's awake time: ", line);
        Assert.EndsWith(" and 8 more", line);
        Assert.Equal(AppPaths.SummaryNames, line[(line.IndexOf("awake time: ", StringComparison.Ordinal) + 12)..].Split(", ").Length);
    }

    [Fact]
    public void TheCountGoesOnAcrossAStartOfPoolWatchAndAfterARestartOfWindowsAddsOnlyWhatTheTimerVouchesFor()
    {
        string day = Item("data", "20261026", TimeSpan.FromDays(6));
        var clock = Start(savedMs: _timer);
        _paths.ApplyRetention(7, _log.Add, clock);
        _timer += 3 * Day;
        clock.Save(); // as the engine stops
        // Within the same start of Windows: stopped for a day, Windows awake meanwhile; the day counts.
        _timer += Day;
        var next = _paths.OpenRetentionClock(() => _timer, _log.Add);
        Assert.Equal(4 * Day, next.CountedMs - next.Items[@"data\20261026"].FirstSeenMs);
        next.Save();
        // Windows started again: its timer is below the saved one, so nothing is added for the time it was off.
        _timer = 2 * Minute;
        var afterRestart = _paths.OpenRetentionClock(() => _timer, _log.Add);
        Assert.Equal(4 * Day, afterRestart.CountedMs - afterRestart.Items[@"data\20261026"].FirstSeenMs);
        _timer += Period - 4 * Day - Minute;
        _paths.ApplyRetention(7, _log.Add, afterRestart);
        Assert.True(There(day));
        _timer += 2 * Minute;
        _paths.ApplyRetention(7, _log.Add, afterRestart);
        Assert.False(There(day));
    }

    [Fact]
    public void WithNoSavedStateEverythingThereIsKeptTheWholePeriodFromTheFirstCheck()
    {
        string old = Item("data", "20261023", TimeSpan.FromDays(9)), newest = Item("data", "20261030", TimeSpan.FromDays(2));
        var clock = Start();
        Assert.Contains(_log, l => l.Contains("no saved retention clock"));
        _paths.ApplyRetention(7, _log.Add, clock);
        Assert.True(There(old) && There(newest)); // 9 days old by the clock: kept, first seen now
        _timer += Period + Minute;
        _paths.ApplyRetention(7, _log.Add, clock);
        Assert.False(There(old) || There(newest));
    }

    [Fact]
    public void AStateThatIsNotOneOrTooLargeIsNotReadAndIsReplacedAtTheNextSave()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.RetentionStateFile)!);
        foreach (string text in new[] { "not json", new string('x', RetentionClock.MaxJsonChars + 1) })
        {
            File.WriteAllText(_paths.RetentionStateFile, text);
            _log.Clear();
            var clock = _paths.OpenRetentionClock(() => _timer, _log.Add);
            Assert.Contains(_log, l => l.Contains("no saved retention clock"));
            clock.Save();
            Assert.NotNull(RetentionClock.State.Parse(File.ReadAllText(_paths.RetentionStateFile)));
        }
    }

    [Fact]
    public void AnEntryWhoseLastWriteTimeCannotBeReadIsKeptAndTheRestAreStillJudged()
    {
        // One folder's own time cannot be read, and in another the time of a file: each is kept, with its record as it was.
        string unread = Item("data", "20261020", TimeSpan.FromDays(12)), other = Item("data", "20261021", TimeSpan.FromDays(11));
        string inside = Item("data", "20261019", TimeSpan.FromDays(13)), insideFile = Path.Combine(inside, "samples.csv");
        string export = Item("exports", "e.zip", TimeSpan.FromDays(11), file: true);
        var clock = Start(savedMs: _timer);
        _paths.ApplyRetention(7, _log.Add, clock);
        var records = clock.Items.ToDictionary();
        _timer += Period + Minute;
        _paths.ApplyRetention(7, _log.Add, clock, (path, dir) => path == unread || path == insideFile ? throw new IOException("cannot be read") : Windows(path, dir));
        Assert.True(There(unread) && There(inside)); // kept, and judged again at the next check
        Assert.Equal(records[@"data\20261020"], clock.Items[@"data\20261020"]);
        Assert.Equal(records[@"data\20261019"], clock.Items[@"data\20261019"]);
        Assert.False(There(other) || There(export));
        Assert.True(File.Exists(_paths.RetentionStateFile));
        _paths.ApplyRetention(7, _log.Add, clock);
        Assert.False(There(unread) || There(inside)); // their records were kept, so they go on their first count
    }

    /// <summary>
    /// Windows gives 1601-01-01 as the last-write time of a path that is gone (deleted between the listing and the read): a
    /// thing with that time, for itself or for a file in it, is not read, so it keeps its record as it was and is not
    /// deleted at that check.
    /// </summary>
    [Fact]
    public void AThingWhosePathIsGoneAsItsTimeIsReadKeepsItsRecordAsItWas()
    {
        string day = Item("data", "20261020", TimeSpan.FromDays(12)), inside = Item("data", "20261019", TimeSpan.FromDays(13));
        string insideFile = Path.Combine(inside, "samples.csv"), export = Item("exports", "e.zip", TimeSpan.FromDays(11), file: true);
        var clock = Start(savedMs: _timer);
        _paths.ApplyRetention(7, _log.Add, clock);
        var records = clock.Items.ToDictionary();
        _timer += Period + Minute;
        var gone = DateTime.FromFileTimeUtc(0);
        _paths.ApplyRetention(7, _log.Add, clock, (path, dir) => path == day || path == insideFile ? gone : Windows(path, dir));
        Assert.True(There(day) && There(inside));
        Assert.Equal(records[@"data\20261020"], clock.Items[@"data\20261020"]);
        Assert.Equal(records[@"data\20261019"], clock.Items[@"data\20261019"]);
        Assert.False(There(export));
        _paths.ApplyRetention(7, _log.Add, clock);
        Assert.False(There(day) || There(inside)); // on their first count
    }

    /// <summary>
    /// A root whose listing fails (here through the listing's seam, as a folder Windows will not list fails it) keeps the
    /// records of what was in it, and says so; once it is listed again, each thing in it goes on its first count.
    /// </summary>
    [Fact]
    public void TheRecordsOfARootWhoseListingFailsAreKept()
    {
        string incident = Item("incidents", "2026-10-30 File", TimeSpan.FromDays(2)), day = Item("data", "20261030", TimeSpan.FromDays(2));
        string incidents = Path.GetDirectoryName(incident)!;
        var clock = Start(savedMs: _timer);
        _paths.ApplyRetention(7, _log.Add, clock);
        _timer += 3 * Day;
        _paths.ApplyRetention(7, _log.Add, clock, list: (root, dirs) =>
            root == incidents ? throw new IOException("the listing failed") : dirs ? Directory.EnumerateDirectories(root) : Directory.EnumerateFiles(root));
        Assert.True(clock.Items.ContainsKey(@"incidents\2026-10-30 File"));
        Assert.Contains(_log, l => l.StartsWith($"retention: could not list {incidents}: the listing failed: ", StringComparison.Ordinal));
        _timer += Period - 3 * Day + Minute;
        _paths.ApplyRetention(7, _log.Add, clock);
        Assert.False(There(incident) || There(day)); // counted from the first check
    }

    /// <summary>
    /// The CSV sink adds rows to the fixed-name files of the day's folder (data\yyyyMMdd), which leaves the folder's own
    /// last-write time as it was. With the clock set back to a day whose folder is still there, the sink's rows go into
    /// that folder: here each file is then given back the last-write time it had (a clock set back by as long as had
    /// passed), so only the files' lengths tell. Retention counts the folder from the check that sees those rows.
    /// </summary>
    [Fact]
    public void RowsWrittenIntoADaysFolderAfterTheClockWasSetBackToThatDayAreKeptThePeriodFromTheCheckThatSeesThem()
    {
        var day = LocalTime(2026, 10, 24, 10);
        string folder = _paths.CsvDir(day);
        using (var sink = NewSink())
        {
            Sample(sink, day);
            Sample(sink, day.AddDays(1)); // the next day: the sink moves to that day's folder
        }
        var times = Directory.EnumerateFiles(folder).ToDictionary(f => f, File.GetLastWriteTimeUtc);
        var folderTime = Directory.GetLastWriteTimeUtc(folder);
        var clock = Start(savedMs: _timer);
        _paths.ApplyRetention(7, _log.Add, clock);
        _timer += 6 * Day;
        _paths.ApplyRetention(7, _log.Add, clock);
        // The clock set back to that day: the sink's rows go into its folder.
        using (var sink = NewSink()) Sample(sink, day.AddHours(1));
        foreach (var (file, time) in times) File.SetLastWriteTimeUtc(file, time);
        Assert.Equal(folderTime, Directory.GetLastWriteTimeUtc(folder));
        _timer += Hour;
        _paths.ApplyRetention(7, _log.Add, clock);
        _timer += 2 * Day;
        _paths.ApplyRetention(7, _log.Add, clock);
        Assert.True(There(folder)); // its newest rows are 2 days old: kept with 7-day retention
        Assert.Equal(2, Rows(folder, "totals.csv"));
        _timer += Period - 2 * Day - Minute;
        _paths.ApplyRetention(7, _log.Add, clock);
        Assert.True(There(folder));
        _timer += 2 * Minute;
        _paths.ApplyRetention(7, _log.Add, clock);
        Assert.False(There(folder));
    }

    /// <summary>
    /// The clock set back to a day whose folder is nearly due, while PoolWatch runs: the sink opens that folder's files
    /// and writes into them, and the next check, made while the sink holds them open, keeps the folder, so the sink's
    /// later rows are in its files, in the folder, once it closes them.
    /// </summary>
    [Fact]
    public void AFolderTheSinkWritesIntoIsNotDeletedUnderIt()
    {
        var day = LocalTime(2026, 10, 24, 10);
        string folder = _paths.CsvDir(day);
        using (var sink = NewSink())
        {
            Sample(sink, day);
            Sample(sink, day.AddDays(1));
        }
        var clock = Start(savedMs: _timer);
        _paths.ApplyRetention(7, _log.Add, clock);
        _timer += Period - Hour;
        _paths.ApplyRetention(7, _log.Add, clock);
        using (var sink = NewSink())
        {
            Sample(sink, day.AddHours(1)); // the clock set back to that day: the sink opens its files and writes
            _timer += 2 * Hour;
            _paths.ApplyRetention(7, _log.Add, clock); // while the sink holds them open
            Assert.True(There(folder));
            Sample(sink, day.AddHours(2));
        }
        Assert.Equal(3, Rows(folder, "totals.csv"));
    }

    /// <summary>
    /// The clock set forward 4 days for a moment: the sink makes that day's folder then. When the day comes, its rows go
    /// into the folder made early, and retention counts the folder from the check that sees them, not from the one that
    /// first saw the folder.
    /// </summary>
    [Fact]
    public void RowsWrittenIntoAFolderMadeEarlyByAClockSetForwardAreKeptThePeriodFromTheCheckThatSeesThem()
    {
        var today = LocalTime(2026, 10, 24, 10);
        var ahead = today.AddDays(4);
        string early = _paths.CsvDir(ahead);
        var clock = Start(savedMs: _timer);
        using (var sink = NewSink())
        {
            Sample(sink, today);
            Sample(sink, ahead); // the clock 4 days ahead, for a moment
            Sample(sink, today.AddMinutes(1));
        }
        _paths.ApplyRetention(7, _log.Add, clock);
        Assert.True(clock.Items.ContainsKey(@"data\" + Path.GetFileName(early)));
        _timer += 4 * Day;
        using (var sink = NewSink()) Sample(sink, ahead.AddMinutes(5)); // that day comes
        _timer += Hour;
        _paths.ApplyRetention(7, _log.Add, clock);
        _timer += 3 * Day;
        _paths.ApplyRetention(7, _log.Add, clock);
        Assert.True(There(early)); // its newest rows are 3 days old: kept with 7-day retention
        Assert.Equal(2, Rows(early, "totals.csv"));
        _timer += Period - 3 * Day - Minute;
        _paths.ApplyRetention(7, _log.Add, clock);
        Assert.True(There(early));
        _timer += 2 * Minute;
        _paths.ApplyRetention(7, _log.Add, clock);
        Assert.False(There(early));
    }

    /// <summary>
    /// A link in a folder retention judges (a junction here) and what is behind it are no part of the folder's stamp:
    /// nothing is read through it, so a write behind it leaves the folder's record as it was, the folder is due on its
    /// first count, and what the link leads to stays.
    /// </summary>
    [Fact]
    public void NothingIsReadThroughALinkInAFolderAndWhatItLeadsToIsNotDeleted()
    {
        string day = Item("data", "20261030", TimeSpan.FromDays(2));
        string outside = Path.Combine(_root, "outside"), behind = Path.Combine(outside, "kept.txt");
        Directory.CreateDirectory(outside);
        File.WriteAllText(behind, "x");
        Links.Junction(Path.Combine(day, "link"), outside);
        var clock = Start(savedMs: _timer);
        _paths.ApplyRetention(7, _log.Add, clock);
        var record = clock.Items[@"data\20261030"];
        _timer += Day;
        File.AppendAllText(behind, "y");
        _paths.ApplyRetention(7, _log.Add, clock);
        Assert.Equal(record, clock.Items[@"data\20261030"]);
        _timer += Period - Day + Minute;
        _paths.ApplyRetention(7, _log.Add, clock);
        // Due on its first count: removed, or its removal failed and was logged (it fails at the link in this test's run).
        Assert.Contains(_log, l => Removed(l, @"data\20261030") || l.StartsWith("retention: could not remove " + day + ": "));
        Assert.Equal("xy", File.ReadAllText(behind));
    }

    /// <summary>
    /// A thing retention lists that is itself a link (a junction in a daily folder's place) is stamped by its own
    /// last-write time alone: nothing behind it is read, so rows added to a file there leave its record as it was. It is
    /// never deleted (the delete refuses a link), and neither is what it leads to.
    /// </summary>
    [Fact]
    public void AThingThatIsItselfALinkIsStampedByItsOwnTimeAloneAndNothingBehindItIsReadOrDeleted()
    {
        string outside = Path.Combine(_root, "outside"), behind = Path.Combine(outside, "samples.csv");
        Directory.CreateDirectory(outside);
        File.WriteAllText(behind, "x");
        string data = Path.Combine(_paths.DataRoot, "data"), day = Path.Combine(data, "20261030");
        Directory.CreateDirectory(data);
        Links.Junction(day, outside);
        var clock = Start(savedMs: _timer);
        _paths.ApplyRetention(7, _log.Add, clock);
        var record = clock.Items[@"data\20261030"];
        _timer += Day;
        File.AppendAllText(behind, "y");
        _paths.ApplyRetention(7, _log.Add, clock);
        Assert.Equal(record, clock.Items[@"data\20261030"]);
        _timer += Period - Day + Minute;
        _paths.ApplyRetention(7, _log.Add, clock);
        Assert.Contains(_log, l => l.StartsWith("retention: could not remove " + day + ": ") && l.Contains("is a link"));
        Assert.True(Directory.Exists(day));
        Assert.Equal("xy", File.ReadAllText(behind));
        Assert.True(Links.DeleteLinkOnly(day));
    }

    /// <summary>
    /// An incident's stamp holds what is in its folders at any depth: rows added to a file in a folder of the incident,
    /// with every last-write time given back as it was, so only that file's length tells, count as a write into it, and
    /// the incident is kept the period from the check that sees them.
    /// </summary>
    [Fact]
    public void RowsAddedToAFileInAFolderOfAnIncidentCountAsAWriteIntoIt()
    {
        string incident = Item("incidents", "2026-10-30 File", TimeSpan.FromDays(2));
        string sub = Path.Combine(incident, "evidence"), file = Path.Combine(sub, "rows.csv");
        var then = Now - TimeSpan.FromDays(2);
        Directory.CreateDirectory(sub);
        File.WriteAllText(file, "a\n");
        File.SetLastWriteTimeUtc(file, then);
        Directory.SetLastWriteTimeUtc(sub, then);
        Directory.SetLastWriteTimeUtc(incident, then);
        var clock = Start(savedMs: _timer);
        _paths.ApplyRetention(7, _log.Add, clock);
        var record = clock.Items[@"incidents\2026-10-30 File"];
        _timer += 6 * Day;
        File.AppendAllText(file, "b\n");
        File.SetLastWriteTimeUtc(file, then);
        Directory.SetLastWriteTimeUtc(sub, then);
        Directory.SetLastWriteTimeUtc(incident, then);
        _paths.ApplyRetention(7, _log.Add, clock);
        Assert.NotEqual(record.Stamp, clock.Items[@"incidents\2026-10-30 File"].Stamp); // seen anew at this check
        _timer += Period - Minute;
        _paths.ApplyRetention(7, _log.Add, clock);
        Assert.True(There(incident)); // the period from the check that saw the rows, not from the first one
        _timer += 2 * Minute;
        _paths.ApplyRetention(7, _log.Add, clock);
        Assert.False(There(incident));
    }

    /// <summary>A UTC time that is <paramref name="hour"/> o'clock on that day here, so the sink's folder is that day's.</summary>
    private static DateTime LocalTime(int year, int month, int day, int hour) => new DateTime(year, month, day, hour, 0, 0, DateTimeKind.Local).ToUniversalTime();

    private CsvSink NewSink() => new(_paths, new Settings(), verbose: false);

    /// <summary>A sample at <paramref name="utc"/>: a row of totals.csv in that day's folder, with a tag and a process row.</summary>
    private void Sample(CsvSink sink, DateTime utc) =>
        sink.WriteSample(new Sample(utc, default, [new PoolTagEntry(PoolTag.Parse("Leak"), 0, 0, 0, 1, 0, 64UL << 20)],
            [new ProcessSample { Pid = 77, ParentPid = 4, CreateTime = 1, ImageName = "x.exe" }]) { TickMs = _timer }, new ProcessRegistry());

    /// <summary>The rows of a CSV file (its header left out).</summary>
    private static int Rows(string folder, string file) => File.ReadAllLines(Path.Combine(folder, file)).Length - 1;

    [Fact]
    public void TheRecordsOfAFolderThatCouldNotBeListedAreKept()
    {
        string incident = Item("incidents", "2026-10-30 File", TimeSpan.FromDays(2));
        string incidents = Path.GetDirectoryName(incident)!, away = incidents + "-away";
        var clock = Start(savedMs: _timer);
        _paths.ApplyRetention(7, _log.Add, clock);
        _timer += 3 * Day;
        Directory.Move(incidents, away); // not there at this check: what was in it keeps its record
        _paths.ApplyRetention(7, _log.Add, clock);
        Assert.True(clock.Items.ContainsKey(@"incidents\2026-10-30 File"));
        Directory.Move(away, incidents);
        _timer += Period - 3 * Day + Minute;
        _paths.ApplyRetention(7, _log.Add, clock);
        Assert.False(There(incident)); // counted from the first check, not from when the folder was back
    }

    /// <summary>
    /// Keeps a temp folder of this test's own, so that retention cannot list it, until the test disposes the handle this
    /// returns.
    /// </summary>
    private static Microsoft.Win32.SafeHandles.SafeFileHandle Hold(string folder)
    {
        var h = NoFollow.Open(folder, dir: true, NoFollow.FILE_LIST_DIRECTORY, 0);
        Assert.False(h.IsInvalid, $"could not keep {folder}");
        return h;
    }

    /// <summary>
    /// A retention folder that cannot be listed (here the daily data folders' folder and the exports folder, which the
    /// test keeps): what retention has a record of in it is judged by its own path, so what is due there is deleted on
    /// time: a file at once, and a folder's files at once and the folder itself as soon as it can be removed (a folder
    /// whose own removal is refused is tried again at each check, and goes at the first one after it can be removed). A
    /// thing put there since it was last listed is first seen once it can be listed again. Each folder that cannot be
    /// listed, and each thing that cannot be removed, is logged once while that lasts, and once more if it comes back
    /// after it ended.
    /// </summary>
    [Fact]
    public void WhatIsDueInAFolderThatCannotBeListedIsStillDeletedByItsOwnPath()
    {
        string day = Item("data", "20261020", TimeSpan.FromDays(12)), export = Item("exports", "e.zip", TimeSpan.FromDays(11), file: true);
        string data = Path.GetDirectoryName(day)!, exports = Path.GetDirectoryName(export)!, rows = Path.Combine(day, "samples.csv");
        var clock = Start(savedMs: _timer);
        _paths.ApplyRetention(7, _log.Add, clock);
        string later = Item("data", "20261031", TimeSpan.FromDays(1)); // not listed before the folder is held
        _timer += Period + Minute;
        using (Hold(data))
        using (Hold(exports))
        {
            Assert.ThrowsAny<IOException>(() => Directory.EnumerateDirectories(data).ToList()); // the listing is refused
            _paths.ApplyRetention(7, _log.Add, clock);
            Assert.False(There(export) || File.Exists(rows)); // due, and judged by their own paths: the file and the folder's files
            Assert.True(There(later));
            Assert.False(clock.Items.ContainsKey(@"data\20261031"));
            _timer += 6 * Hour;
            _paths.ApplyRetention(7, _log.Add, clock);
            // Gone from its path: its record goes too, although the folder still cannot be listed.
            Assert.False(clock.Items.ContainsKey(@"exports\e.zip"));
        }
        Assert.Single(_log, l => l.StartsWith($"retention: could not list {data}: ", StringComparison.Ordinal));
        Assert.Single(_log, l => l.StartsWith($"retention: could not list {exports}: ", StringComparison.Ordinal));
        Assert.True(_log.Count(l => l.StartsWith($"retention: could not remove {day}: ", StringComparison.Ordinal)) <= 1);
        Assert.EndsWith("what retention has a record of in it is judged by its own path, and the rest once it can be listed (logged once until then)",
            _log.First(l => l.StartsWith($"retention: could not list {data}: ", StringComparison.Ordinal)));
        _timer += 6 * Hour;
        _paths.ApplyRetention(7, _log.Add, clock); // listed again: first seen now
        Assert.False(There(day)); // what was left of it, at the first check after the hold
        Assert.True(clock.Items.ContainsKey(@"data\20261031"));
        _timer += Period - Minute;
        using (Hold(data)) _paths.ApplyRetention(7, _log.Add, clock);
        Assert.True(There(later)); // a minute short of its period, counted from the check that first listed it
        Assert.Equal(2, _log.Count(l => l.StartsWith($"retention: could not list {data}: ", StringComparison.Ordinal))); // held again after it was listed
        _timer += 2 * Minute;
        _paths.ApplyRetention(7, _log.Add, clock);
        Assert.False(There(later));
    }

    /// <summary>
    /// A root that can never be listed (here through the listing's seam, at every check for six weeks) keeps nothing that
    /// is due in it for ever: what retention has a record of is deleted on time by its own path, the record of what is gone
    /// goes, so the saved state does not grow, and the folder is logged once.
    /// </summary>
    [Fact]
    public void ARootThatCanNeverBeListedStillHasWhatIsDueInItDeletedAndKeepsNoRecordOfWhatIsGone()
    {
        string old = Item("incidents", "2026-10-20 File", TimeSpan.FromDays(12)), newer = Item("incidents", "2026-10-29 File", TimeSpan.FromDays(3));
        string incidents = Path.GetDirectoryName(old)!;
        var clock = Start(savedMs: _timer);
        _paths.ApplyRetention(7, _log.Add, clock);
        Func<string, bool, IEnumerable<string>> never = (root, dirs) =>
            root == incidents ? throw new IOException("the listing failed") : dirs ? Directory.EnumerateDirectories(root) : Directory.EnumerateFiles(root);
        _timer += 4 * Day;
        _paths.ApplyRetention(7, _log.Add, clock, list: never);
        Directory.SetLastWriteTimeUtc(newer, Now); // written to after it was first seen: counted from the check that sees it
        for (int check = 0; check < 6 * 7 * 4; check++)
        {
            _timer += 6 * Hour;
            _paths.ApplyRetention(7, _log.Add, clock, list: never);
            if (check == 12) Assert.False(There(old)); // 4 days and 13 checks of 6 hours: over the period
            if (check == 12) Assert.True(There(newer)); // counted anew from the check that saw its write
        }
        Assert.False(There(old) || There(newer));
        Assert.DoesNotContain(clock.Items.Keys, k => k.StartsWith(@"incidents\", StringComparison.Ordinal));
        Assert.Single(_log, l => l.StartsWith($"retention: could not list {incidents}: the listing failed: ", StringComparison.Ordinal));
    }

    /// <summary>
    /// A record whose name is not a plain name in its folder (a saved state that says so: a name with a folder in it, '.'
    /// or '..', a stream or a drive) is never looked at while that folder cannot be listed, so nothing outside the
    /// retention folders is judged or deleted: here a due record names the data folder's cache through the daily folders'
    /// folder.
    /// </summary>
    [Fact]
    public void ARecordThatNamesSomethingOutsideItsFolderIsNeverLookedAt()
    {
        Item("data", "20261020", TimeSpan.FromDays(12));
        string cache = Path.Combine(_paths.DataRoot, "cache"), kept = Path.Combine(cache, "kept.json");
        Directory.CreateDirectory(cache);
        File.WriteAllText(kept, "{}");
        string outside = Path.Combine(_paths.DataRoot, "data", "sub");
        Directory.CreateDirectory(Path.Combine(outside, "inner"));
        var records = new Dictionary<string, RetentionClock.Kept>
        {
            [@"data\..\cache"] = new(0, 0), [@"data\..\cache\kept.json"] = new(0, 0), [@"data\sub\inner"] = new(0, 0), [@"data\."] = new(0, 0),
            [@"data\x:stream"] = new(0, 0), [@"data\C:\Windows"] = new(0, 0), [@"data\"] = new(0, 0), [@"data\20261020 "] = new(0, 0),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.RetentionStateFile)!);
        File.WriteAllText(_paths.RetentionStateFile, new RetentionClock.State(_timer, 100 * Day, records).ToJson());
        var clock = _paths.OpenRetentionClock(() => _timer, _log.Add);
        string data = Path.Combine(_paths.DataRoot, "data");
        _paths.ApplyRetention(7, _log.Add, clock, list: (root, dirs) => root == data ? throw new IOException("the listing failed") : dirs ? Directory.EnumerateDirectories(root) : Directory.EnumerateFiles(root));
        Assert.True(File.Exists(kept) && Directory.Exists(Path.Combine(outside, "inner")));
        Assert.DoesNotContain(_log, l => l.StartsWith("retention: removed ", StringComparison.Ordinal));
        Assert.Equal(records.Keys.Order(StringComparer.Ordinal), clock.Items.Keys.Order(StringComparer.Ordinal)); // kept as they were, none looked at
        foreach (string name in records.Keys) Assert.Null(AppPaths.RecordedPath(data, "data", name));
        Assert.Equal(Path.Combine(data, "20261020"), AppPaths.RecordedPath(data, "data", @"data\20261020"));
        Assert.Null(AppPaths.RecordedPath(data, "data", @"incidents\20261020"));
    }

    /// <summary>
    /// A daily folder in which one file is in use cannot be deleted whole: the rest of it stays due, so it is tried
    /// again at the next check and goes once that file is free, not a whole period later; the folder is logged once
    /// while it cannot be removed. What is left after something is written into it is counted anew instead.
    /// </summary>
    [Fact]
    public void TheRestOfAFolderThatCouldNotBeDeletedWholeIsTriedAgainAtTheNextCheck()
    {
        string day = Item("data", "20261020", TimeSpan.FromDays(12)), other = Item("data", "20261021", TimeSpan.FromDays(11));
        string inUse = Path.Combine(day, "rows.csv"), alsoInUse = Path.Combine(other, "rows.csv");
        File.WriteAllText(inUse, "a\n");
        File.WriteAllText(alsoInUse, "a\n");
        var clock = Start(savedMs: _timer);
        _paths.ApplyRetention(7, _log.Add, clock);
        _timer += Period + Minute;
        using (new FileStream(inUse, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (new FileStream(alsoInUse, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            _paths.ApplyRetention(7, _log.Add, clock);
            Assert.True(File.Exists(inUse) && File.Exists(alsoInUse));
            Assert.False(File.Exists(Path.Combine(day, "samples.csv"))); // the rest was deleted
            _timer += 6 * Hour;
            _paths.ApplyRetention(7, _log.Add, clock); // still in use: tried again, not logged again
        }
        Assert.Single(_log, l => l.StartsWith($"retention: could not remove {day}: ", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(other, "new.csv"), "b\n"); // written into after it was found due
        _timer += 6 * Hour;
        _paths.ApplyRetention(7, _log.Add, clock);
        Assert.False(There(day)); // its rest, at the next check
        Assert.True(There(other)); // counted anew from the check that saw the write
        Assert.Contains(_log, l => Removed(l, @"data\20261020"));
        _timer += Period - Minute;
        _paths.ApplyRetention(7, _log.Add, clock);
        Assert.True(There(other));
        _timer += 2 * Minute;
        _paths.ApplyRetention(7, _log.Add, clock);
        Assert.False(There(other));
    }

    /// <summary>
    /// A daily folder that cannot be read whole for its stamp (the test makes it so) is kept with its record as it was,
    /// logged once while it cannot be read, and goes at the first check after it can be read again, on its first count.
    /// </summary>
    [Fact]
    public void AFolderThatCannotBeReadWholeIsKeptLoggedOnceAndGoesOnItsFirstCountOnceItCanBe()
    {
        string day = Item("data", "20261020", TimeSpan.FromDays(12));
        var clock = Start(savedMs: _timer);
        _paths.ApplyRetention(7, _log.Add, clock);
        var record = clock.Items[@"data\20261020"];
        _timer += Period + Minute;
        using (Hold(day))
        {
            _paths.ApplyRetention(7, _log.Add, clock);
            _timer += 6 * Hour;
            _paths.ApplyRetention(7, _log.Add, clock);
            Assert.True(There(day));
            Assert.Equal(record, clock.Items[@"data\20261020"]);
        }
        Assert.Single(_log, l => l.StartsWith($"retention: could not read {day} whole: ", StringComparison.Ordinal));
        _paths.ApplyRetention(7, _log.Add, clock);
        Assert.False(There(day));
    }

    [Fact]
    public void TheWorkingTimeTimerGoesOnWithTimeAndIsReadFromWindowsUnbiasedInterruptTime()
    {
        long before = PoolWatch.Native.WorkingTime.Ms();
        Thread.Sleep(300);
        long after = PoolWatch.Native.WorkingTime.Ms();
        Assert.InRange(after - before, 250, 60_000);
        string source = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Native", "WorkingTime.cs"));
        Assert.Contains("[DllImport(\"kernel32.dll\", ExactSpelling = true)]", source);
        Assert.Contains("private static extern bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);", source);
        Assert.Contains("QueryUnbiasedInterruptTime(out ulong unbiased) ? (long)(unbiased / 10_000) :", source);
        Assert.DoesNotContain("TickCount", source.Replace("Environment.TickCount64, which goes on across sleep", ""));
    }

    /// <summary>
    /// The engine counts retention on Windows' working-time timer (made with the engine, from the state its last save
    /// kept), judges on retention's own job, and saves the clock as it stops once the sampler is joined.
    /// </summary>
    [Fact]
    public void TheEngineCountsOnTheWorkingTimeTimerJudgesOnItsOwnJobAndSavesAsItStops()
    {
        string source = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Engine", "MonitorEngine.cs"));
        string ctor = UninstallerTests.OneLine(ScriptCode.CSharpMember(source, "public MonitorEngine(Settings settings, AppPaths paths, Log log, bool verboseCsv)").Value);
        Assert.Contains("_log = log; _retention = paths.OpenRetentionClock(Native.WorkingTime.Ms, log.Info); " +
                        "_sink = new CsvSink(paths, settings, verboseCsv, log.Info);", ctor);
        string stop = UninstallerTests.OneLine(System.Text.RegularExpressions.Regex.Replace(ScriptCode.CSharpMember(source, "public void Stop()").Value, @"//[^\n]*", ""));
        Assert.Contains("if (_joined) { try {", stop);
        Assert.Contains("_sink.Flush(); _retention.Save(); }", stop);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(source, @"\bApplyRetention\("));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(source, @"_retention\."));
    }

    /// <summary>
    /// Each text that says what a folder's stamp holds also says that a link is left out of it: a link in a folder, and
    /// what is behind it, is no part of that folder's stamp, and a folder that is itself a link is stamped by its own time
    /// alone (<see cref="AppPaths.StampOf"/>, which
    /// <see cref="AThingThatIsItselfALinkIsStampedByItsOwnTimeAloneAndNothingBehindItIsReadOrDeleted"/> holds). It reads
    /// <see cref="RecommendationActionsTests.ScannedTexts"/> (README.md, SECURITY.md, the .md files directly in docs, the
    /// guide the app ships for AI assistants and every comment of the C# code under src, one after code on its line and
    /// one between /* and */ included): each sentence, or clause between semicolons, that gives the last-write time "of
    /// every file" or "of everything" in a folder must also say "but a link", that a link is left out, or that nothing is
    /// read through a link. Those sentences are RetentionClock's and ApplyRetention's summaries, StampOf's, DESIGN.md's
    /// and TECHNICAL.md's.
    /// </summary>
    [Fact]
    public void EachTextThatSaysWhatAFolderIsStampedByLeavesOutALink()
    {
        var holds = new System.Text.RegularExpressions.Regex(@"last-write time of (every file|everything)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var leftOut = new System.Text.RegularExpressions.Regex(@"\bbut a link\b|\blink\b[^.;]{0,40}\bleft out\b|\bnothing (is )?read through a link\b");
        var said = RecommendationActionsTests.ScannedTexts()
            .SelectMany(t => System.Text.RegularExpressions.Regex.Split(t.Text, @"(?<=[.;])\s+").Where(s => holds.IsMatch(s)).Select(s => (t.File, Sentence: s)))
            .ToList();
        Assert.Equal(["AppPaths.cs", "AppPaths.cs", "DESIGN.md", "RetentionClock.cs", "TECHNICAL.md"], said.Select(s => s.File).Order());
        var unsaid = said.Where(s => !leftOut.IsMatch(s.Sentence)).Select(s => $"{s.File}: {s.Sentence}").ToList();
        Assert.True(unsaid.Count == 0, string.Join(Environment.NewLine, unsaid));
        // The words it looks for: what the five say, and not a sentence that names no link.
        Assert.Matches(holds, "for a folder also the name, size and last-write time of every file in it, so rows added");
        Assert.DoesNotMatch(leftOut, "for a folder also the name, size and last-write time of every file in it, so rows added");
        // Such a sentence in a comment after code on its line, or between /* and */, is read as well.
        Assert.Equal(2, RecommendationActionsTests.CommentTexts(["    var s = StampOf(d); // the last-write time of every file in it",
            "    Save(/* the last-write time of everything in it */ s);"]).Count(holds.IsMatch));
    }

    private static DateTime Windows(string path, bool dir) => dir ? Directory.GetLastWriteTimeUtc(path) : File.GetLastWriteTimeUtc(path);
}
