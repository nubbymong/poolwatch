using PoolWatch.Core.Model;

namespace PoolWatch.Tests;

/// <summary>
/// Retention's count (<see cref="RetentionClock"/>) on a simulated PC whose system clock is set forward and back by any
/// amount, while PoolWatch runs, while it is stopped, while the PC sleeps, hibernates or is off, and across a restart of
/// Windows: a thing is deleted only once the retention period has really passed since it or anything in it was last
/// written (rows added to a file in a folder included, which leave the folder's own last-write time as it was), and with
/// nothing changed it goes at the first check after the period and the allowance, counted from the check that first saw
/// it as it is.
/// </summary>
public class RetentionClockTests
{
    private const int Days = 7;
    private static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Day = TimeSpan.FromDays(1), Hour = TimeSpan.FromHours(1), Minute = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Period = TimeSpan.FromDays(Days) + RetentionClock.Allowance(Days);

    /// <summary>
    /// A thing retention judges: its name, when it or anything in it was really last written, its own last-write time (the
    /// system time then), and, for a folder (<c>data\</c>, <c>incidents\</c>), the files in it.
    /// </summary>
    private sealed record Thing(string Name, DateTime Real, long Written, IReadOnlyList<RetentionClock.Entry> Files)
    {
        public bool IsFolder => !Name.StartsWith(@"exports\", StringComparison.Ordinal);

        /// <summary>Its stamp, as AppPaths.ApplyRetention reads it: its own last-write time and, for a folder, every file in it.</summary>
        public long Stamp => RetentionClock.Stamp(Written, IsFolder ? -1 : 1, Files);
    }

    /// <summary>
    /// A PC: the real time, the system clock's error against it, the working-time timer (milliseconds Windows was awake
    /// since it started), the retention clock's saved state (its file), and what was written. The engine's own steps: a
    /// retention check (list, judge, delete, save), a stop that saves, a crash that does not, and a start that reads the
    /// saved state.
    /// </summary>
    private sealed class Pc
    {
        public DateTime Real = T0;
        public TimeSpan Error = TimeSpan.Zero;
        public long Timer = 3_600_000; // awake an hour since Windows started
        public RetentionClock.State? Saved;
        public RetentionClock Clock = null!;
        public readonly List<Thing> Things = new();
        public readonly List<(Thing Thing, DateTime DeletedReal)> Deleted = new();
        public readonly HashSet<string> NotListed = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Unreadable = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The system clock, held within DateTime's range.</summary>
        public DateTime Wall => Error > DateTime.MaxValue - Real ? DateTime.MaxValue : Error < DateTime.MinValue.AddYears(1600) - Real ? DateTime.MinValue.AddYears(1600) : Real + Error;

        public Pc Start()
        {
            Clock = new RetentionClock(() => Timer, Saved, s => Saved = s);
            return this;
        }

        /// <summary>Time passes with Windows awake: the timer goes on with it.</summary>
        public Pc Pass(TimeSpan t)
        {
            Real += t;
            Timer += (long)t.TotalMilliseconds;
            return this;
        }

        /// <summary>The PC sleeps or hibernates: the timer stands still, and the system clock may be moved meanwhile (by the PC's own clock, say).</summary>
        public Pc Sleep(TimeSpan t, TimeSpan clockMovedMeanwhile = default)
        {
            Real += t;
            Error += clockMovedMeanwhile;
            return this;
        }

        public Pc SetClock(TimeSpan by)
        {
            Error += by;
            return this;
        }

        /// <summary>
        /// Writes the thing <paramref name="name"/> (<c>data\x</c>, <c>incidents\y</c>...), a new one or one there already: its
        /// own last-write time becomes the clock's (for a folder, a file added, removed or renamed in it; its files stay).
        /// </summary>
        public Pc Write(string name)
        {
            var files = Things.SingleOrDefault(t => t.Name == name)?.Files ?? [];
            Things.RemoveAll(t => t.Name == name);
            Things.Add(new Thing(name, Real, Wall.Ticks, files));
            return this;
        }

        /// <summary>
        /// Rows added to <paramref name="file"/> in the folder <paramref name="name"/>, as the CSV sink appends them: the file
        /// grows and its last-write time becomes the clock's. A file not there yet is made, which is a file added to the
        /// folder (its own last-write time becomes the clock's too); one already there leaves the folder's own time as it was.
        /// </summary>
        public Pc WriteInto(string name, string file = "totals.csv")
        {
            var folder = Things.SingleOrDefault(t => t.Name == name);
            var files = folder?.Files.ToList() ?? [];
            int at = files.FindIndex(f => f.Name == file);
            long length = at < 0 ? 0 : files[at].Length;
            if (at >= 0) files.RemoveAt(at);
            files.Add(new RetentionClock.Entry(file, length + 80, Wall.Ticks));
            Things.RemoveAll(t => t.Name == name);
            Things.Add(new Thing(name, Real, folder is null || at < 0 ? Wall.Ticks : folder.Written, files));
            return this;
        }

        /// <summary>A retention check, as AppPaths.ApplyRetention makes it; each deletion is checked to be no earlier than the retention period.</summary>
        public Pc Check(int days = Days)
        {
            var listed = Things.Where(t => !NotListed.Contains(Folder(t.Name)))
                .Select(t => new RetentionClock.Seen(t.Name, Unreadable.Contains(t.Name) ? null : t.Stamp)).ToList();
            foreach (var name in Clock.Check(days, listed, NotListed.ToList()))
            {
                var thing = Things.Single(t => t.Name == name);
                Assert.True(Real - thing.Real > TimeSpan.FromDays(days),
                    $"{name} written {thing.Real:u} (its last-write time {new DateTime(thing.Written):u}), deleted at {Real:u} (the clock said {Wall:u}): {Real - thing.Real} after it was written");
                Things.Remove(thing);
                Deleted.Add((thing, Real));
            }
            Clock.Save();
            return this;
        }

        /// <summary>
        /// PoolWatch stops (saving, as the engine's stop does, unless it crashed) and starts again after <paramref name="off"/>:
        /// Windows awake meanwhile, or hibernated (<paramref name="hibernate"/>, a Shut down with Fast Startup: the timer
        /// stands still), or started again (<paramref name="reboot"/>: the timer counts from that start, two minutes before
        /// PoolWatch's).
        /// </summary>
        public Pc Restart(TimeSpan off, bool reboot = false, bool crash = false, bool hibernate = false, TimeSpan clockChangedMeanwhile = default)
        {
            if (!crash) Clock.Save();
            Real += off;
            Timer = reboot ? 120_000 : hibernate ? Timer : Timer + (long)off.TotalMilliseconds;
            Error += clockChangedMeanwhile;
            return Start();
        }

        public bool IsKept(string name) => Things.Any(t => t.Name == name);

        /// <summary>How long the thing has been kept as it is, by the clock's count.</summary>
        public TimeSpan KeptFor(string name) => TimeSpan.FromMilliseconds(Clock.CountedMs - Clock.Items[name].FirstSeenMs);

        private static string Folder(string name) => name[..name.IndexOf('\\')];
    }

    private static Pc Running() => new Pc { Saved = new RetentionClock.State(3_600_000, 0, new Dictionary<string, RetentionClock.Kept>()) }.Start();

    [Fact]
    public void WithNothingChangedAThingGoesOnceItWasKeptThePeriodAndTheAllowanceSinceTheCheckThatFirstSawIt()
    {
        Assert.Equal(TimeSpan.FromDays(7) / 1000, RetentionClock.Allowance(7)); // ten minutes a week: a timer a little fast is covered
        var pc = Running();
        pc.Write(@"data\a").Check().Pass(6 * Hour).Write(@"data\b").Pass(6 * Hour).Check(); // b is first seen 6 hours after it was written
        Assert.Equal(12 * Hour, pc.KeptFor(@"data\a"));
        Assert.Equal(TimeSpan.Zero, pc.KeptFor(@"data\b"));
        pc.Pass(Days * Day - 12 * Hour + Minute).Check(); // a: the period and a minute, inside the allowance
        Assert.True(pc.IsKept(@"data\a"));
        pc.Pass(RetentionClock.Allowance(Days)).Check(); // a: the period, the allowance and a minute
        Assert.False(pc.IsKept(@"data\a"));
        Assert.True(pc.IsKept(@"data\b"));
        pc.Pass(12 * Hour).Check();
        Assert.False(pc.IsKept(@"data\b"));
        Assert.Equal(Period + 6 * Hour + Minute, pc.Deleted[1].DeletedReal - pc.Deleted[1].Thing.Real); // 6 hours to its first check, then the period
        pc.Check();
        Assert.Empty(pc.Clock.Items); // a record goes once a check of its folder no longer finds the thing
    }

    [Fact]
    public void AClockSetBackAndForwardAgainBetweenTwoTicksWithAThingWrittenBetweenDeletesItNoEarlier()
    {
        // A check sees the clock right; a year back, an incident written 3 seconds later, the clock right again, and the
        // next check 5 seconds on. The incident's last-write time is a year old, but it was first seen now.
        var pc = Running();
        pc.Check().Pass(TimeSpan.FromSeconds(2)).SetClock(-365 * Day).Pass(TimeSpan.FromSeconds(3)).Write(@"incidents\x")
            .SetClock(365 * Day).Pass(TimeSpan.FromSeconds(5)).Check();
        Assert.True(pc.IsKept(@"incidents\x"));
        pc.Pass(Days * Day).Check();
        Assert.True(pc.IsKept(@"incidents\x"));
        pc.Pass(RetentionClock.Allowance(Days) + Minute).Check();
        Assert.False(pc.IsKept(@"incidents\x"));
    }

    [Fact]
    public void AClockSetForwardOrBackByAnyAmountWhilePoolWatchRunsOrIsStoppedChangesNoCount()
    {
        // Thirty days forward, a year back, twenty years forward and to the end of DateTime's range, with things written at
        // each, and across a stop with the clock moved meanwhile too. Each thing goes on the timer's count, none earlier.
        var pc = Running();
        pc.Write(@"data\right").Check().Pass(Hour).SetClock(30 * Day).Write(@"data\ahead").Pass(Hour).SetClock(-30 * Day - 365 * Day)
            .Write(@"data\behind").Check().Pass(Hour).SetClock(365 * Day + 20 * 366 * Day).Write(@"exports\twenty-years").Check()
            .Restart(Hour, clockChangedMeanwhile: TimeSpan.MaxValue / 2).Write(@"exports\end-of-time").Check();
        Assert.Equal(DateTime.MaxValue.Ticks, pc.Things.Single(t => t.Name == @"exports\end-of-time").Written);
        Assert.Equal(4 * Hour, pc.KeptFor(@"data\right"));
        Assert.Equal(2 * Hour, pc.KeptFor(@"data\ahead"));
        Assert.Equal(2 * Hour, pc.KeptFor(@"data\behind"));
        Assert.Equal(Hour, pc.KeptFor(@"exports\twenty-years")); // the hour PoolWatch was stopped counts: the timer went on
        Assert.Equal(TimeSpan.Zero, pc.KeptFor(@"exports\end-of-time"));
        pc.SetClock(-(TimeSpan.MaxValue / 2)).Pass(Period - 4 * Hour + Minute).Check();
        Assert.Equal([@"data\right"], pc.Deleted.Select(d => d.Thing.Name));
        pc.Pass(4 * Hour).Check();
        Assert.Equal([@"data\right", @"data\ahead", @"data\behind", @"exports\twenty-years", @"exports\end-of-time"], pc.Deleted.Select(d => d.Thing.Name));
    }

    [Fact]
    public void TimeAsleepHibernatedOrOffDoesNotCountEvenWithTheClockMovedMeanwhile()
    {
        // Six days awake; then 8 hours asleep with the clock moved 30 days ahead meanwhile (the PC's own clock changed, say);
        // a Shut down with Fast Startup (a hibernation) for 8 hours with the clock moved 30 days more; and 2 days off before
        // a full start of Windows. None of it counts, so the folder goes a day and the allowance of awake time later.
        var pc = Running();
        pc.Write(@"data\a").Check().Pass(6 * Day).Sleep(8 * Hour, 30 * Day).Check();
        Assert.Equal(6 * Day, pc.KeptFor(@"data\a"));
        pc.Restart(8 * Hour, hibernate: true, clockChangedMeanwhile: 30 * Day).Check();
        Assert.Equal(6 * Day, pc.KeptFor(@"data\a"));
        pc.Restart(2 * Day, reboot: true).Check();
        Assert.Equal(6 * Day, pc.KeptFor(@"data\a")); // the timer started again below the saved one: nothing is added
        pc.Pass(Day).Check();
        Assert.True(pc.IsKept(@"data\a"));
        pc.Pass(RetentionClock.Allowance(Days) + Minute).Check();
        Assert.False(pc.IsKept(@"data\a"));
        Assert.Equal(Period + Minute + 16 * Hour + 2 * Day, pc.Deleted.Single().DeletedReal - pc.Deleted.Single().Thing.Real);
    }

    [Fact]
    public void AStopOrACrashWithinOneStartOfWindowsLosesNothingAndAfterARestartOfWindowsOnlyTheTimeSinceThenCounts()
    {
        var pc = Running();
        pc.Write(@"data\a").Check().Pass(Day).Restart(Hour).Check(); // stopped an hour, Windows awake: the hour counts
        Assert.Equal(Day + Hour, pc.KeptFor(@"data\a"));
        pc.Pass(Hour).Restart(Hour, crash: true).Check(); // no save since the check: the timer still holds the two hours
        Assert.Equal(Day + 3 * Hour, pc.KeptFor(@"data\a"));
        pc.Pass(5 * Hour).Restart(10 * Minute, reboot: true, crash: true).Check(); // the 5 hours before the crash are not counted
        Assert.Equal(Day + 3 * Hour, pc.KeptFor(@"data\a"));
        pc.Pass(10 * Day).Check();
        Assert.False(pc.IsKept(@"data\a"));
        // After a restart of Windows whose timer is past the saved one, what is added is at most the time since that start:
        // the save was made a minute after an earlier start of Windows, and this one has been up an hour.
        pc = Running();
        pc.Write(@"data\b").Check();
        pc.Saved = pc.Saved! with { SavedMs = 60_000 };
        pc.Real += 3 * Day;
        pc.Timer = 3_600_000;
        pc.Start().Check();
        Assert.Equal(Hour - Minute, pc.KeptFor(@"data\b"));
    }

    [Fact]
    public void AThingWrittenAgainIsCountedFromTheCheckThatFirstSeesItsNewLastWriteTime()
    {
        var pc = Running();
        pc.Write(@"incidents\x").Check().Pass(5 * Day).SetClock(-10 * Day).Write(@"incidents\x").SetClock(10 * Day).Pass(Hour).Check();
        Assert.Equal(TimeSpan.Zero, pc.KeptFor(@"incidents\x")); // written again, with an older last-write time: seen anew
        pc.Pass(Period - Minute).Check();
        Assert.True(pc.IsKept(@"incidents\x"));
        pc.Pass(2 * Minute).Check();
        Assert.False(pc.IsKept(@"incidents\x"));
    }

    /// <summary>
    /// Rows the CSV sink adds to a daily folder's files leave the folder's own last-write time as it was: the folder is
    /// still seen anew, by its files. With the clock set back to a day whose folder is still there, the rows go into that
    /// folder (here at the very last-write time its file had, the clock having been set back by as long as had passed, so
    /// only the file's length tells them apart); with the clock set forward for a moment, a later day's folder is made
    /// early, and that day's rows go into it when the day comes. Each check holds that nothing is deleted within the
    /// retention period of its last write; and a folder written all day long is never deleted while it is written, even
    /// with 1-day retention across midnight.
    /// </summary>
    [Fact]
    public void RowsAddedToAFoldersFilesAfterTheClockWasSetBackOrForwardAreCountedFromTheCheckThatSeesThem()
    {
        var pc = Running();
        pc.WriteInto(@"data\20261001").WriteInto(@"data\20261001", "tags.csv").Check().Pass(6 * Day).SetClock(-6 * Day).WriteInto(@"data\20261001");
        var back = pc.Things.Single();
        Assert.Equal((T0.Ticks, T0.Ticks, T0.Ticks), (back.Written, back.Files.Single(f => f.Name == "totals.csv").WrittenTicks, back.Files.Max(f => f.WrittenTicks)));
        pc.Pass(Hour).Check();
        Assert.Equal(TimeSpan.Zero, pc.KeptFor(@"data\20261001")); // seen anew by its file's length
        pc.SetClock(6 * Day).Pass(2 * Day).Check();
        Assert.True(pc.IsKept(@"data\20261001"));
        pc.Pass(Period - 2 * Day - Minute).Check();
        Assert.True(pc.IsKept(@"data\20261001"));
        pc.Pass(2 * Minute).Check();
        Assert.False(pc.IsKept(@"data\20261001"));

        // Four days ahead for a moment: that day's folder is made then, and its rows go into it when the day comes.
        pc = Running();
        pc.WriteInto(@"data\20261001").SetClock(4 * Day).WriteInto(@"data\20261005").SetClock(-4 * Day).WriteInto(@"data\20261001").Check();
        pc.Pass(4 * Day).WriteInto(@"data\20261005").Pass(Hour).Check();
        Assert.Equal(TimeSpan.Zero, pc.KeptFor(@"data\20261005"));
        pc.Pass(3 * Day).Check();
        Assert.True(pc.IsKept(@"data\20261005"));
        pc.Pass(Period - 3 * Day - Minute).Check();
        Assert.True(pc.IsKept(@"data\20261005"));
        pc.Pass(2 * Minute).Check();
        Assert.False(pc.IsKept(@"data\20261005"));

        // Written every 10 minutes for two days with 1-day retention, checked every 6 hours: never deleted while written.
        pc = Running();
        for (int i = 0; i < 2 * 24 * 6; i++)
        {
            pc.WriteInto(@"data\20261001").Pass(10 * Minute);
            if (i % 36 == 35) pc.Check(days: 1);
        }
        Assert.True(pc.IsKept(@"data\20261001"));
        pc.Pass(Day).Check(days: 1).Pass(6 * Hour).Check(days: 1);
        Assert.False(pc.IsKept(@"data\20261001"));
    }

    /// <summary>
    /// A stamp changes with any name, length or last-write time in the thing, a file added or gone included, whatever the
    /// order its files are listed in, and it is the same number in every process: a saved state's stamps are compared
    /// with those a later start of PoolWatch reads.
    /// </summary>
    [Fact]
    public void AStampChangesWithAnyNameLengthOrLastWriteTimeInItAndIsTheSameInEveryProcess()
    {
        RetentionClock.Entry totals = new("totals.csv", 100, 5), below = new(@"sub\x.txt", 3, 7);
        long stamp = RetentionClock.Stamp(42, -1, [totals, below]);
        Assert.Equal(stamp, RetentionClock.Stamp(42, -1, [below, totals]));
        Assert.Equal(StampOfTheExample, stamp);
        long[] others =
        [
            RetentionClock.Stamp(43, -1, [totals, below]),
            RetentionClock.Stamp(42, 1, [totals, below]),
            RetentionClock.Stamp(42, -1, [totals with { Length = 101 }, below]),
            RetentionClock.Stamp(42, -1, [totals with { WrittenTicks = 6 }, below]),
            RetentionClock.Stamp(42, -1, [totals with { Name = "tags.csv" }, below]),
            RetentionClock.Stamp(42, -1, [totals]),
            RetentionClock.Stamp(42, -1, [totals, below, new("events.csv", 0, 5)]),
            RetentionClock.Stamp(42, -1, []),
        ];
        Assert.DoesNotContain(stamp, others);
        Assert.Equal(others.Length, others.Distinct().Count());
        // Names are told apart whole: "ab" and "c" are not "a" and "bc".
        Assert.NotEqual(RetentionClock.Stamp(1, -1, [new("ab", 1, 1), new("c", 1, 1)]), RetentionClock.Stamp(1, -1, [new("a", 1, 1), new("bc", 1, 1)]));
    }

    /// <summary>The stamp of the example above, as this version computes it (the first 8 bytes of its SHA-256).</summary>
    private const long StampOfTheExample = 4277825291809342022;

    [Fact]
    public void WithNoSavedStateEverythingThereIsKeptTheWholePeriodFromTheFirstCheck()
    {
        var pc = Running();
        pc.Write(@"data\old").Pass(30 * Day).Write(@"data\new");
        pc.Saved = null; // the first start of this version, or a state that cannot be read
        pc.Restart(Hour, crash: true).Check();
        Assert.Equal(TimeSpan.Zero, pc.KeptFor(@"data\old"));
        pc.Pass(Period - Minute).Check();
        Assert.True(pc.IsKept(@"data\old") && pc.IsKept(@"data\new"));
        pc.Pass(2 * Minute).Check();
        Assert.Empty(pc.Things);
    }

    [Fact]
    public void ARecordIsLetGoOnlyOnceACheckThatListedItsFolderWholeDoesNotFindTheThing()
    {
        var pc = Running();
        pc.Write(@"incidents\x").Write(@"data\a").Check().Pass(3 * Day);
        pc.NotListed.Add("incidents"); // the incidents folder could not be listed whole: its records stay
        pc.Check();
        Assert.Equal(3 * Day, pc.KeptFor(@"incidents\x"));
        pc.NotListed.Clear();
        pc.Pass(Period - 3 * Day + Minute).Check();
        Assert.Empty(pc.Things); // counted on from its first check, as if it had been listed all along
        // A thing gone from a folder listed whole loses its record: one there again with that name is new.
        pc.Write(@"data\b").Check().Pass(Day);
        var b = pc.Things.Single();
        pc.Things.Clear();
        pc.Check();
        Assert.Empty(pc.Clock.Items);
        pc.Things.Add(b);
        pc.Check();
        Assert.Equal(TimeSpan.Zero, pc.KeptFor(@"data\b"));
    }

    /// <summary>
    /// The records a folder holds can be named (<see cref="RetentionClock.NamesIn"/>, by the folder's name in any case,
    /// never a folder whose name only starts with it), so a folder that cannot be listed still has what is due in it judged
    /// by each one's own path; a record can be let go (<see cref="RetentionClock.Forget"/>) when its thing is not at its
    /// path, although its folder was not listed whole; and the rest of a thing that could not be deleted whole takes its
    /// new stamp and keeps the time it was first seen (<see cref="RetentionClock.StillDue"/>), so it is due at the next
    /// check, while a name without a record gets none.
    /// </summary>
    [Fact]
    public void ARecordCanBeNamedByItsFolderLetGoAndKeptDueWithTheStampOfWhatIsLeft()
    {
        long timer = 1_000;
        var clock = new RetentionClock(() => timer, null, null);
        clock.Check(Days, [new(@"data\a", 1), new(@"data\b", 2), new(@"incidents\x", 3), new(@"database\c", 4)], []);
        Assert.Equal([@"data\a", @"data\b"], clock.NamesIn("data").Order(StringComparer.Ordinal));
        Assert.Equal([@"data\a", @"data\b"], clock.NamesIn("DATA").Order(StringComparer.Ordinal));
        Assert.Empty(clock.NamesIn("exports"));
        timer += (long)(Period + Minute).TotalMilliseconds;
        long firstSeen = clock.Items[@"data\a"].FirstSeenMs;
        Assert.True(clock.CountedMs - firstSeen > (long)Period.TotalMilliseconds); // the count is read on to now, past the period
        clock.StillDue(@"data\a", 11);
        Assert.Equal(new RetentionClock.Kept(11, firstSeen), clock.Items[@"data\a"]);
        clock.StillDue(@"data\none", 5);
        Assert.False(clock.Items.ContainsKey(@"data\none"));
        clock.Forget(@"data\b");
        // a's rest is due at once; x, written since, starts again; b's record is gone although data was not listed whole.
        Assert.Equal([@"data\a"], clock.Check(Days, [new(@"data\a", 11), new(@"incidents\x", 30)], ["data"]));
        Assert.Equal([@"data\a", @"incidents\x"], clock.Items.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AThingWhoseLastWriteTimeCannotBeReadIsKeptAndKeepsItsRecord()
    {
        var pc = Running();
        pc.Write(@"exports\e.zip").Check().Pass(Day);
        pc.Unreadable.Add(@"exports\e.zip");
        pc.Pass(Period).Check();
        Assert.True(pc.IsKept(@"exports\e.zip")); // never deleted while its time cannot be read
        Assert.Equal(Day + Period, pc.KeptFor(@"exports\e.zip"));
        pc.Unreadable.Clear();
        pc.Check();
        Assert.False(pc.IsKept(@"exports\e.zip"));
        // One never read at all has no record and is never named.
        pc.Write(@"exports\f.zip");
        pc.Unreadable.Add(@"exports\f.zip");
        pc.Check().Pass(30 * Day).Check();
        Assert.True(pc.IsKept(@"exports\f.zip"));
        Assert.False(pc.Clock.Items.ContainsKey(@"exports\f.zip"));
    }

    [Fact]
    public void TheSavedStateIsReadBackAsWrittenAndOneThatIsNotAStateIsNotRead()
    {
        var items = new Dictionary<string, RetentionClock.Kept> { [@"data\20261001"] = new(T0.Ticks, 5), [@"incidents\2026-10-01 x"] = new(DateTime.MaxValue.Ticks, 40) };
        var state = new RetentionClock.State(42, 50, items);
        var back = RetentionClock.State.Parse(state.ToJson())!;
        Assert.Equal((42L, 50L), (back.SavedMs, back.CountedMs));
        Assert.Equal(items.OrderBy(i => i.Key), back.Items.OrderBy(i => i.Key));
        Assert.Null(RetentionClock.State.Parse(new string(' ', RetentionClock.MaxJsonChars + 1)));
        foreach (string bad in new[]
                 {
                     "{}", "[1]", "not json",
                     """{"savedMs":-1,"countedMs":50,"items":[]}""",
                     """{"savedMs":1,"countedMs":-50,"items":[]}""",
                     """{"savedMs":"1","countedMs":50,"items":[]}""",
                     $$"""{"savedMs":{{long.MaxValue / 4 + 1}},"countedMs":50,"items":[]}""",
                     """{"savedMs":1,"countedMs":50,"items":[{"name":"data\\a","stamp":1,"firstSeenMs":51}]}""",
                     """{"savedMs":1,"countedMs":50,"items":[{"name":"data\\a","stamp":1,"firstSeenMs":-1}]}""",
                     """{"savedMs":1,"countedMs":50,"items":[{"name":"","stamp":1,"firstSeenMs":1}]}""",
                     """{"savedMs":1,"countedMs":50,"items":[{"name":"data\\a","firstSeenMs":1}]}""",
                     """{"savedMs":1,"countedMs":50,"items":[{"name":"data\\a","stamp":1,"firstSeenMs":1},{"name":"DATA\\A","stamp":2,"firstSeenMs":2}]}""",
                 })
            Assert.Null(RetentionClock.State.Parse(bad));
        Assert.NotNull(RetentionClock.State.Parse("""{"savedMs":0,"countedMs":0,"items":[]}"""));
    }

    [Fact]
    public void ASaveNeverWritesMoreThanAStateMayHoldAndLeavesOutTheLatestFirstSeen()
    {
        // 20,000 names of 200 characters outside ASCII (each written as six characters): far more than a state may hold.
        var items = Enumerable.Range(0, 20_000).ToDictionary(i => @"data\" + i.ToString("D5") + new string('é', 200), i => new RetentionClock.Kept(i, 20_000 - i));
        string json = new RetentionClock.State(1, 20_000, items).ToJson();
        Assert.InRange(json.Length, RetentionClock.MaxJsonChars - 2000, RetentionClock.MaxJsonChars);
        var back = RetentionClock.State.Parse(json)!;
        Assert.InRange(back.Items.Count, 1000, 19_999);
        Assert.Equal(Enumerable.Range(1, back.Items.Count), back.Items.Values.Select(k => (int)k.FirstSeenMs).Order()); // the earliest first seen are kept
    }

    [Fact]
    public void RandomClockChangesSleepsStopsCrashesAndRestartsOfWindowsNeverDeleteAnythingEarly()
    {
        // Each change of the clock is a whole number of seconds (or days) and one tick of 100 ns, and time passes in whole
        // minutes, so, as on a real PC, no write of a thing itself gets the very last-write time an earlier one had, and rows
        // added to a folder's file make the file longer whatever its time: what the count relies on to tell that a thing was
        // written again.
        string[] names = [@"data\1", @"data\2", @"data\3", @"incidents\a", @"incidents\b", @"exports\x.zip", @"exports\y.zip"];
        string[] folders = ["data", "incidents", "exports"];
        int deleted = 0;
        for (int seed = 1; seed <= 80; seed++)
        {
            var rnd = new Random(seed);
            var pc = Running();
            for (int step = 0; step < 200; step++)
            {
                switch (rnd.Next(12))
                {
                    case 0 or 1: pc.Write(names[rnd.Next(names.Length)]); break;
                    case 2: pc.WriteInto(names[rnd.Next(5)], rnd.Next(2) == 0 ? "totals.csv" : "tags.csv"); break; // a daily folder or an incident
                    case 3:
                        var by = rnd.Next(8) == 0 ? TimeSpan.FromDays(rnd.Next(-9000, 9001)) : TimeSpan.FromSeconds(rnd.Next(-40, 41) * Math.Pow(10, rnd.Next(0, 7)));
                        if ((pc.Error + by).Duration() > TimeSpan.FromDays(300 * 365)) by = -by; // within the years Windows keeps
                        pc.SetClock(by + TimeSpan.FromTicks(1));
                        break;
                    case 4: pc.Sleep(TimeSpan.FromMinutes(rnd.Next(1, 5000)), rnd.Next(2) == 0 ? TimeSpan.FromHours(rnd.Next(-2000, 2001)) : default); break;
                    case 5: pc.Restart(TimeSpan.FromMinutes(rnd.Next(1, 3000)), reboot: rnd.Next(3) == 0, crash: rnd.Next(4) == 0, hibernate: rnd.Next(2) == 0,
                        clockChangedMeanwhile: rnd.Next(3) == 0 ? TimeSpan.FromHours(rnd.Next(-5000, 5001)) : default); break;
                    case 6 or 7: pc.Check(rnd.Next(1, 4)); break;
                    case 8:
                        string folder = folders[rnd.Next(folders.Length)];
                        if (!pc.NotListed.Remove(folder)) pc.NotListed.Add(folder);
                        break;
                    case 9:
                        string name = names[rnd.Next(names.Length)];
                        if (!pc.Unreadable.Remove(name)) pc.Unreadable.Add(name);
                        break;
                    default: pc.Pass(TimeSpan.FromMinutes(rnd.Next(1, 1500))); break;
                }
            }
            // Then a long while with every folder listed and every time read, still never early (each check asserts it).
            pc.NotListed.Clear();
            pc.Unreadable.Clear();
            pc.Pass(Day).Check(1).Pass(30 * Day).Check(1);
            Assert.Empty(pc.Things); // all gone in the end
            deleted += pc.Deleted.Count;
        }
        Assert.True(deleted > 300, $"only {deleted} deleted"); // the checks judged and deleted, so the rule was tried
    }
}
