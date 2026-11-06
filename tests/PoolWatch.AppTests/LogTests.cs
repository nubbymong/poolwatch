using System.Reflection;
using System.Security.AccessControl;
using PoolWatch.Engine;

namespace PoolWatch.AppTests;

/// <summary>poolwatch.log: a line is appended at the true end and the log stays readable, and other PoolWatch writers
/// append beside it without writing over each other's lines; only the process that holds the one-instance mutex rolls
/// it over; an append refused for a moment is tried again; when another program keeps refusing it, that line and the
/// later ones go to a new file next to it whose first line says why, no other program can open that file while
/// PoolWatch writes it except in the moment it rolls over at 2 MB, or tries to, and only the newest few such files are
/// kept, with those of another PoolWatch still running.</summary>
public sealed class LogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pw-log-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _open = [];
    private static readonly DateTime At = new(2026, 11, 5, 6, 30, 12);
    private const string Stamp = "2026-11-05 06:30:12.000 ";
    private static readonly int DiskFull = unchecked((int)0x80070070);

    public LogTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var d in _open) d.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string LogPath => Path.Combine(_root, "poolwatch.log");

    private Log NewLog(Func<string, string>? checkWrite = null, Action<int>? wait = null, bool rollsLog = true,
        Func<int, bool>? running = null)
    {
        var log = new Log(LogPath, checkWrite, () => At, wait ?? (_ => { }), rollsLog ? StandIn() : null, running ?? (_ => false));
        _open.Add(log);
        return log;
    }

    private FileStream Hold(string path, FileShare share, FileAccess access = FileAccess.Read)
    {
        var s = new FileStream(path, FileMode.OpenOrCreate, access, share);
        _open.Add(s);
        return s;
    }

    private string[] NewFiles() =>
        Directory.GetFiles(_root, $"poolwatch-20261105-063012-{Environment.ProcessId}-*.log").OrderBy(f => f, StringComparer.Ordinal).ToArray();

    private static string[] Lines(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(s);
        return r.ReadToEnd().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool Openable(string path)
    {
        try
        {
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete).Dispose();
            return true;
        }
        catch (IOException) { return false; }
    }

    private static void Fill(Log log, int lines = 2200)
    {
        string big = new('x', 1000);
        for (int i = 0; i < lines; i++) log.Info(big); // 2200 lines of 1000 characters: past 2 MB
    }

    private static string RepoDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PoolWatch.slnx"))) return dir.FullName;
        throw new InvalidOperationException("PoolWatch.slnx not found above " + AppContext.BaseDirectory);
    }

    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine([RepoDir(), .. parts]));

    // The tests' stand-in for the proof that this process holds the one-instance mutex: one made through the proof's
    // private constructor, as the two steps that take the mutex make it, with no mutex to release, so a Log given it
    // rolls the log over.
    private static ExitSignal.Hold StandIn() =>
        (ExitSignal.Hold)typeof(ExitSignal.Hold).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, [typeof(Mutex)])!
            .Invoke([null]);

    // Whether a thread other than this one could take the mutex now (gone counts as free: nobody holds it).
    private static bool FreeFromAnotherThread(string name) => Task.Factory.StartNew(() =>
    {
        if (!Mutex.TryOpenExisting(name, out var opened)) return true;
        using var m = opened;
        bool got;
        try { got = m.WaitOne(0); }
        catch (AbandonedMutexException) { got = true; }
        if (got) m.ReleaseMutex();
        return got;
    }, TaskCreationOptions.LongRunning).Result;

    // Asks every 50 ms, for up to two seconds, whether the mutex has been let go (or is gone): the step that lets it go
    // too early does so at once.
    private static bool LetGoWithinTwoSeconds(Func<bool> letGo)
    {
        for (int i = 0; i < 40; i++)
        {
            if (letGo()) return true;
            Thread.Sleep(50);
        }
        return false;
    }

    private static void FullLog(string path) => File.WriteAllText(path, new string('x', 2 * 1024 * 1024 + 10) + Environment.NewLine);

    [Fact]
    public void ALineIsAppendedAndTheLogCanBeReadAndCopiedWhilePoolWatchRuns()
    {
        var log = NewLog();
        log.Info("one");
        log.Info("two");
        // A reader that shares reading only (File.ReadAllText, a copy) opens the log between lines.
        Assert.Equal([Stamp + "one", Stamp + "two"], File.ReadAllLines(LogPath));
        File.Copy(LogPath, Path.Combine(_root, "copy.log"));
        Assert.Equal(LogPath, log.CurrentPath);
        Assert.Empty(NewFiles());
    }

    [Fact]
    public void AnotherWriterThatSharesWritingDoesNotMoveTheLogAndItsLineStays()
    {
        // Another PoolWatch part-way through its own line has the log open for appending, sharing reading and writing.
        var other = new FileInfo(LogPath).Create(FileMode.OpenOrCreate, FileSystemRights.AppendData, FileShare.ReadWrite, 4096, FileOptions.None, null);
        _open.Add(other);
        var log = NewLog();
        log.Info("one");
        other.Write("theirs\r\n"u8);
        other.Flush();
        log.Info("two");
        Assert.Empty(NewFiles());
        Assert.Equal([Stamp + "one", "theirs", Stamp + "two"], Lines(LogPath));
    }

    [Fact]
    public void AViewerThatKeepsTheLogOpenSharingReadingAndWritingDoesNotMoveTheLog()
    {
        File.WriteAllText(LogPath, "earlier" + Environment.NewLine);
        Hold(LogPath, FileShare.ReadWrite); // a viewer that follows the log as it grows
        var log = NewLog();
        log.Info("one");
        Assert.Empty(NewFiles());
        Assert.Equal(LogPath, log.CurrentPath);
        Assert.Equal(["earlier", Stamp + "one"], Lines(LogPath));
    }

    [Fact]
    public void TheLogsOwnOpenSharesNoDeletingSoTheLogIsNeverRenamedPartWayThroughALine()
    {
        File.WriteAllText(LogPath, "earlier" + Environment.NewLine);
        // A handle with delete access, as a rename takes on the file it moves: the append's open does not share
        // deleting, so it is refused while that handle is open, and a rename is refused while a line is being appended.
        var renamer = new FileInfo(LogPath).Create(FileMode.Open, FileSystemRights.ReadData | FileSystemRights.Delete,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.None, null);
        _open.Add(renamer);
        var log = NewLog();
        log.Info("one");
        string file = Assert.Single(NewFiles());
        Assert.Equal(["earlier"], Lines(LogPath));
        log.Dispose();
        Assert.Equal(Stamp + "one", Lines(file)[1]);
    }

    [Fact]
    public void TwoWritersAtOnceLoseNoLineAndWriteNoneOverAnother()
    {
        var a = NewLog();
        var b = NewLog();
        const int each = 3000;
        Parallel.Invoke(
            () => { for (int i = 0; i < each; i++) a.Info($"a {i} " + new string('a', i % 7)); },
            () => { for (int i = 0; i < each; i++) b.Info($"b {i} " + new string('b', i % 11)); });
        string[] lines = Lines(LogPath);
        Assert.Empty(NewFiles());
        Assert.Equal(2 * each, lines.Length);
        var expected = Enumerable.Range(0, each).Select(i => Stamp + $"a {i} " + new string('a', i % 7))
            .Concat(Enumerable.Range(0, each).Select(i => Stamp + $"b {i} " + new string('b', i % 11)))
            .Order(StringComparer.Ordinal);
        Assert.Equal(expected, lines.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AnAppendRefusedForAMomentIsTriedAgainAndStaysInTheLog()
    {
        var reader = Hold(LogPath, FileShare.Read);
        var waits = new List<int>();
        var log = NewLog(wait: ms =>
        {
            waits.Add(ms);
            reader.Dispose(); // the reader lets go during the first pause
        });
        log.Info("one");
        Assert.Equal([10], waits);
        Assert.Empty(NewFiles());
        Assert.Equal([Stamp + "one"], Lines(LogPath));
    }

    [Fact]
    public void ALogHeldSoNoLineCanBeAppendedGoesOnInANewFileThatSaysWhy()
    {
        File.WriteAllText(LogPath, "earlier" + Environment.NewLine);
        Hold(LogPath, FileShare.Read);
        var waits = new List<int>();
        var log = NewLog(wait: waits.Add);
        log.Info("one");
        log.Info("two");

        Assert.Equal([10, 25, 50], waits); // three tries more, then the new file; the second line goes straight there
        string file = Assert.Single(NewFiles());
        Assert.Equal(file, log.CurrentPath);
        log.Dispose();
        string[] lines = Lines(file);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith(Stamp + "poolwatch.log could not be written: another program has it open", lines[0]);
        Assert.EndsWith("This run's log goes on in this file.", lines[0]);
        Assert.Equal(Stamp + "one", lines[1]);
        Assert.Equal(Stamp + "two", lines[2]);
        Assert.Equal(["earlier"], Lines(LogPath));
    }

    [Fact]
    public void ALockOnTheLogMovesTheLinesOnToo()
    {
        File.WriteAllText(LogPath, "earlier" + Environment.NewLine);
        var s = Hold(LogPath, FileShare.ReadWrite | FileShare.Delete);
        s.Lock(0, long.MaxValue / 2); // a lock past the end refuses every append (ERROR_LOCK_VIOLATION)
        var log = NewLog();
        log.Info("one");

        string file = Assert.Single(NewFiles());
        log.Dispose();
        Assert.Equal(Stamp + "one", Lines(file)[1]);
    }

    [Fact]
    public void NoOtherProgramCanOpenOrRenameTheNewFileWhilePoolWatchWritesItBetweenRollOvers()
    {
        Hold(LogPath, FileShare.Read);
        var log = NewLog();
        log.Info("one");
        string file = Assert.Single(NewFiles());

        foreach (var share in new[] { FileShare.Read, FileShare.ReadWrite, FileShare.ReadWrite | FileShare.Delete })
            Assert.Throws<IOException>(() => new FileStream(file, FileMode.Open, FileAccess.Read, share).Dispose());
        Assert.Throws<IOException>(() => File.Move(file, file + ".x"));
        log.Info("two"); // still written
        log.Dispose();
        Assert.Equal(Stamp + "two", Lines(file)[2]);
    }

    [Fact]
    public void EachLineToAKeptNewFileIsInTheFileWhenItIsLogged()
    {
        Hold(LogPath, FileShare.Read);
        var log = NewLog();
        log.Info("one");
        string file = Assert.Single(NewFiles());
        long first = new FileInfo(file).Length; // the file is kept, so its length is what another program can see
        // A longer second line: if a line stayed in the stream's buffer until the next write moved it out, the growth
        // would be the first line's length, not this one's.
        const string longer = "two, a longer line than the one before it";
        log.Info(longer);
        long second = new FileInfo(file).Length;
        Assert.True(first > 0, "the first lines are written through");
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(Stamp + longer + Environment.NewLine), second - first);
    }

    [Fact]
    public void EveryLineThatNeedsANewFileGetsOneNoMatterHowManyInARun()
    {
        Hold(LogPath, FileShare.Read);
        var log = NewLog();
        var files = new List<string>();
        for (int i = 0; i < 5; i++)
        {
            log.Info("line " + i);
            files.Add(log.CurrentPath);
            log.Dispose();                  // lets go of the new file...
            Hold(log.CurrentPath, FileShare.Read); // ...and a program holds it, so the next line needs another
        }
        Assert.Equal(5, files.Distinct().Count());
        for (int i = 0; i < 5; i++)
        {
            // Each first line names the file the run moved on from: the log, then each earlier new file.
            string[] lines = Lines(files[i]);
            Assert.StartsWith(Stamp + Path.GetFileName(i == 0 ? LogPath : files[i - 1]) + " could not be written", lines[0]);
            Assert.Equal(Stamp + "line " + i, lines[1]);
        }
    }

    [Fact]
    public void TheNewFileRollsOverAtItsCapAndTheNextOneIsKeptFromItsFirstLine()
    {
        Hold(LogPath, FileShare.Read);
        var log = NewLog(rollsLog: false); // a new file rolls over in any process
        Fill(log);
        log.Info("after");
        string file = Assert.Single(NewFiles());
        Assert.True(File.Exists(file + ".1"), "the new file rolled over to .1");
        Assert.False(Openable(file), "the next file is kept");
        log.Dispose();
        // The lines after the roll-over are in the next file, which starts with the first of them.
        string[] next = Lines(file);
        Assert.Equal(Stamp + "after", next[^1]);
        Assert.Equal(2200 + 2, Lines(file + ".1").Length + next.Length); // the first line says why, then 2200 and "after"
        Assert.True(new FileInfo(file).Length < 1024 * 1024, "the next file holds only the lines after the roll-over");
    }

    [Fact]
    public void TheNewFileWhoseOldPartIsHeldGrowsAtItsEndAndStaysKept()
    {
        Hold(LogPath, FileShare.Read);
        var log = NewLog();
        log.Info("first");
        string file = Assert.Single(NewFiles());
        File.WriteAllText(file + ".1", "old");
        Hold(file + ".1", FileShare.Read); // its .1 is held: no roll-over
        Fill(log); // past 2 MB: each line tries the roll-over, is refused, goes on here
        log.Info("after");
        Assert.False(Openable(file), "the file is kept again after each refused roll-over");
        log.Dispose();
        string[] lines = Lines(file);
        Assert.StartsWith(Stamp + "poolwatch.log could not be written", lines[0]);
        Assert.Equal(Stamp + "first", lines[1]);
        Assert.Equal(Stamp + "after", lines[^1]);
        Assert.Equal(1 + 1 + 2200 + 1, lines.Length);
    }

    [Fact]
    public void AProgramThatOpensTheNewFileInTheMomentOfARefusedRollOverDoesNotEndTheKeep()
    {
        Hold(LogPath, FileShare.Read);
        int calls = -1;
        FileStream? racer = null;
        var log = NewLog(
            checkWrite: p =>
            {
                // Armed below: the third check of the next line is the one made to keep the file again after the
                // refused roll-over (the first two are the rename's); a program opens the file in that moment.
                if (calls >= 0 && ++calls == 3) racer = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read);
                return p;
            },
            wait: _ => racer?.Dispose()); // and lets go during the first pause
        log.Info("first");
        string file = Assert.Single(NewFiles());
        File.WriteAllText(file + ".1", "old");
        Hold(file + ".1", FileShare.Read);
        Fill(log);
        calls = 0;
        log.Info("raced");
        Assert.NotNull(racer);
        Assert.Equal(file, log.CurrentPath);
        Assert.False(Openable(file), "the file is kept again once the program has let go");
        log.Dispose();
        Assert.Equal(Stamp + "raced", Lines(file)[^1]);
    }

    [Fact]
    public void AKeptFileThatIsGoneInTheMomentOfARefusedRollOverIsMadeAgainAndNoLaterLineIsLost()
    {
        Hold(LogPath, FileShare.Read);
        int calls = -1;
        var log = NewLog(checkWrite: p =>
        {
            // Armed below: the third check of the next line is the one made to keep the file again after the refused
            // roll-over; the file is deleted in that moment (as a program in a folder it may write can do).
            if (calls >= 0 && ++calls == 3) File.Delete(p);
            return p;
        });
        log.Info("first");
        string file = Assert.Single(NewFiles());
        File.WriteAllText(file + ".1", "old");
        Hold(file + ".1", FileShare.Read);
        Fill(log);
        calls = 0;
        log.Info("gone");
        calls = -1;
        log.Info("later");
        Assert.Equal(file, log.CurrentPath);
        Assert.False(Openable(file), "the file made again is kept");
        log.Dispose();
        Assert.Equal([Stamp + "gone", Stamp + "later"], Lines(file));
    }

    [Fact]
    public void AHeldOldLogDoesNotStopTheLogItGrowsUntilLetGo()
    {
        FullLog(LogPath);
        var held = Hold(LogPath + ".1", FileShare.Read);
        var log = NewLog();
        log.Info("after");
        Assert.Empty(NewFiles());
        Assert.Equal(Stamp + "after", Lines(LogPath)[^1]);

        held.Dispose();
        log.Info("rolled");
        Assert.Equal([Stamp + "rolled"], Lines(LogPath));
        Assert.Equal(Stamp + "after", Lines(LogPath + ".1")[^1]);
    }

    [Fact]
    public void OnlyTheProcessThatHoldsTheOneInstanceMutexRollsTheLogOver()
    {
        string mutex = @"Local\PoolWatch-Test-Log-" + Guid.NewGuid().ToString("N");
        foreach (bool holds in new[] { false, true })
        {
            string root = Path.Combine(_root, holds ? "holds" : "not");
            Directory.CreateDirectory(root);
            string logFile = Path.Combine(root, "poolwatch.log");
            FullLog(logFile);
            // The paths are made as the app makes them: with the proof that this thread holds the mutex, or without
            // one.
            Log? made = null;
            void Next(ExitSignal.Hold? hold)
            {
                made = new AppPaths(root, null, secure: false, hold: hold).OpenLog();
                _open.Add(made);
                made.Info("next");
            }
            if (holds) Assert.Equal(0, ExitSignal.AsTheOneInstance(mutex, () => 1, hold => { Next(hold); return 0; }));
            else Next(null);
            Assert.Equal(holds, File.Exists(logFile + ".1"));
            Assert.EndsWith("next", Lines(logFile)[^1]);
            // Once the mutex is let go (the proof disposed), that Log no longer rolls the log over.
            File.WriteAllText(logFile, new string('y', 2 * 1024 * 1024 + 20) + Environment.NewLine);
            made!.Info("after");
            string[] after = Lines(logFile);
            Assert.Equal(2, after.Length);
            Assert.EndsWith("after", after[^1]);
        }
    }

    /// <summary>
    /// The Log that AppPaths opens rolls the log over only through a proof that holds the mutex on the very thread that
    /// makes the paths (<see cref="ExitSignal.Hold.HeldHere"/>, which also decides whether that start may move data
    /// folders): a proof still held, but on another thread, gives it no roll-over, and neither does one given with the
    /// tests' stand-in for the start's checks (holdsInstance), true or false.
    /// </summary>
    [Fact]
    public void TheLogRollsOverOnlyThroughAProofHeldOnTheThreadThatMakesThePaths()
    {
        string id = Guid.NewGuid().ToString("N");
        void NotRolled(string name, ExitSignal.Hold hold, bool? holdsInstance)
        {
            string root = Path.Combine(_root, name);
            Directory.CreateDirectory(root);
            string logFile = Path.Combine(root, "poolwatch.log");
            FullLog(logFile);
            var log = new AppPaths(root, null, secure: false, holdsInstance: holdsInstance, hold: hold).OpenLog();
            _open.Add(log);
            log.Info("next");
            Assert.False(File.Exists(logFile + ".1"), name + ": the log was rolled over");
            Assert.EndsWith("next", Lines(logFile)[^1]);
        }
        // A proof made, and still held, on a thread of its own.
        using var made = new ManualResetEventSlim();
        using var letGo = new ManualResetEventSlim();
        ExitSignal.Hold? elsewhere = null;
        var holder = new Thread(() => ExitSignal.AsTheOneInstance(@"Local\PoolWatch-Test-Log-" + id + "-a", () => 1, hold =>
        {
            elsewhere = hold;
            made.Set();
            letGo.Wait(30_000);
            return 0;
        })) { IsBackground = true };
        holder.Start();
        try
        {
            Assert.True(made.Wait(10_000));
            Assert.False(elsewhere!.HeldHere);
            Assert.True(elsewhere.WhileHeld(() => { })); // held, on its own thread
            NotRolled("elsewhere", elsewhere, holdsInstance: null);
        }
        finally
        {
            letGo.Set();
            holder.Join(10_000);
        }
        // A proof held on this thread, given with the tests' stand-in for the start's checks.
        Assert.Equal(0, ExitSignal.AsTheOneInstance(@"Local\PoolWatch-Test-Log-" + id + "-b", () => 1, hold =>
        {
            Assert.True(hold.HeldHere);
            NotRolled("stand-in-true", hold, holdsInstance: true);
            NotRolled("stand-in-false", hold, holdsInstance: false);
            return 0;
        }));
    }

    [Fact]
    public void TheProofIsNotLetGoWhileTheLogRollsOverAndNoRollOverStartsOnceItIs()
    {
        string mutex = @"Local\PoolWatch-Test-Log-" + Guid.NewGuid().ToString("N");
        Assert.Equal(0, ExitSignal.AsTheOneInstance(mutex, () => 1, hold =>
        {
            Task? letGo = null;
            using var calling = new ManualResetEventSlim();
            Assert.True(hold.WhileHeld(() =>
            {
                // The exit, on a thread of its own, while a roll-over is under way.
                letGo = Task.Factory.StartNew(() =>
                {
                    calling.Set();
                    hold.Dispose();
                }, TaskCreationOptions.LongRunning);
                Assert.True(calling.Wait(10_000));
                Assert.False(letGo.Wait(200), "the proof is let go only once the roll-over has ended");
            }));
            Assert.True(letGo!.Wait(10_000));
            bool ran = false;
            Assert.False(hold.WhileHeld(() => ran = true));
            Assert.False(ran);
            return 0;
        }));
    }

    [Fact]
    public void TheOneInstanceMutexIsNotLetGoWhileARollOverIsUnderWay()
    {
        // The tray app's and a headless run's proof: once run has returned, its mutex is kept until the roll-over ends.
        string mutex = @"Local\PoolWatch-Test-Log-" + Guid.NewGuid().ToString("N");
        using var inRoll = new ManualResetEventSlim();
        bool? letGoDuringRoll = null, rolled = null;
        Thread? roller = null;
        Assert.Equal(0, ExitSignal.AsTheOneInstance(mutex, () => 1, hold =>
        {
            roller = new Thread(() => rolled = hold.WhileHeld(() =>
            {
                inRoll.Set();
                letGoDuringRoll = LetGoWithinTwoSeconds(() => FreeFromAnotherThread(mutex));
            })) { IsBackground = true };
            roller.Start();
            Assert.True(inRoll.Wait(10_000));
            return 0; // run returns while the roll-over is under way
        }));
        Assert.True(roller!.Join(10_000));
        Assert.True(rolled);
        Assert.False(letGoDuringRoll, "the one-instance mutex was let go while the roll-over ran");
        Assert.False(Mutex.TryOpenExisting(mutex, out _), "the mutex is let go once run and the roll-over have ended");
    }

    [Fact]
    public void TheUninstallCleanupsMutexIsNotLetGoWhileARollOverIsUnderWay()
    {
        // The uninstall cleanup's proof (StopAndHold, which releases the mutex when it is disposed): disposed on the
        // thread that took the mutex while a roll-over runs on another, it keeps the mutex until the roll-over ends.
        string id = Guid.NewGuid().ToString("N");
        string signal = @"Local\PoolWatch-Test-Exit-" + id, mutex = @"Local\PoolWatch-Test-Log-" + id;
        var hold = ExitSignal.StopAndHold(signal, mutex, TimeSpan.FromSeconds(5));
        Assert.NotNull(hold);
        using var inRoll = new ManualResetEventSlim();
        bool? letGoDuringRoll = null, rolled = null;
        var roller = new Thread(() => rolled = hold!.WhileHeld(() =>
        {
            inRoll.Set();
            letGoDuringRoll = LetGoWithinTwoSeconds(() => FreeFromAnotherThread(mutex));
        })) { IsBackground = true };
        roller.Start();
        Assert.True(inRoll.Wait(10_000));
        hold!.Dispose();
        Assert.True(roller.Join(10_000));
        Assert.True(rolled);
        Assert.False(letGoDuringRoll, "the mutex was let go while the roll-over ran");
        Assert.True(FreeFromAnotherThread(mutex), "the mutex is let go once the roll-over has ended");
        Assert.False(hold.WhileHeld(() => throw new InvalidOperationException("ran once the proof was disposed")));
    }

    // Every method of the assembly that holds type whose IL may make one, as "Type.Method", once for each place, in
    // order: a newobj of one of its constructors; an ldtoken of one (how a new in an expression tree compiles); an
    // ldtoken of the type itself (how a new with a dynamic argument, and a typeof, compile).
    private static string[] MakersOf(Type type)
    {
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
                                 | BindingFlags.DeclaredOnly;
        var ctors = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Select(c => c.MetadataToken).ToHashSet();
        var makers = new List<string>();
        foreach (var t in type.Assembly.GetTypes())
            foreach (var m in t.GetMethods(All).Cast<MethodBase>().Concat(t.GetConstructors(All)))
            {
                byte[]? il = m.GetMethodBody()?.GetILAsByteArray();
                if (il is null) continue;
                for (int i = 0; i + 4 < il.Length; i++)
                {
                    int token = BitConverter.ToInt32(il, i + 1);
                    bool ofCtor = il[i] is 0x73 or 0xD0 && ctors.Contains(token); // newobj or ldtoken <constructor>
                    bool ofType = il[i] == 0xD0 && token == type.MetadataToken;   // ldtoken <type>
                    if (ofCtor || ofType) makers.Add(t.Name + "." + m.Name);
                }
            }
        return [.. makers.Order(StringComparer.Ordinal)];
    }

    [Fact]
    public void ALogRollsTheLogOverOnlyThroughTheProofThatItsProcessHoldsTheMutex()
    {
        FullLog(LogPath);
        using (var log = new Log(LogPath)) log.Info("next");
        Assert.False(File.Exists(LogPath + ".1"));
        Assert.EndsWith("next", Lines(LogPath)[^1]);
        // The gate a Log takes can only be the proof (Log has this one constructor). The proof's constructors are
        // private, and the only code in PoolWatch that makes one is the two steps that take the mutex: AsTheOneInstance
        // once, StopAndHold twice. That is read from the compiled code (MakersOf): each newobj of it, each ldtoken of
        // one of its constructors (a new in an expression tree) and each ldtoken of its type (a new with a dynamic
        // argument, or a typeof that reflection could start from). A proof made any other way is not read: for example
        // through an UnsafeAccessor, a MemberwiseClone of one, or reflection on the type got some other way (by its
        // name, from a generic argument, or from the type of Log's parameter). AppPaths passes the proof it was given
        // when that proof holds the mutex on the thread that makes the paths and no stand-in for the start's checks
        // (holdsInstance) was given, and none otherwise (as
        // TheLogRollsOverOnlyThroughAProofHeldOnTheThreadThatMakesThePaths and
        // OnlyTheProcessThatHoldsTheOneInstanceMutexRollsTheLogOver hold).
        var ctor = Assert.Single(typeof(Log).GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
        Assert.Equal(typeof(ExitSignal.Hold), Assert.Single(ctor.GetParameters(), p => p.Name == "rollGate").ParameterType);
        Assert.All(typeof(ExitSignal.Hold).GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
            c => Assert.True(c.IsPrivate, c + " is not private"));
        Assert.Equal(["Hold.AsTheOneInstance", "Hold.StopAndHold", "Hold.StopAndHold"], MakersOf(typeof(ExitSignal.Hold)));
        Assert.Contains("new Log(LogFile, Guard.CheckReal, rollGate: _rollGate)", Source("src", "PoolWatch", "Engine", "AppPaths.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void ALookupFailureStopsThePruneAndNothingIsDeleted()
    {
        var planted = Enumerable.Range(1, 7).Select(d => Path.Combine(_root, $"poolwatch-2026100{d}-010203-42-1.log")).ToArray();
        foreach (var p in planted)
        {
            File.WriteAllText(p, "old");
            File.WriteAllText(p + ".1", "old");
        }
        Hold(LogPath, FileShare.Read);
        var log = NewLog(running: _ => throw new InvalidOperationException("the process could not be looked up"));
        log.Info("one");
        Assert.Single(NewFiles());
        foreach (var p in planted)
        {
            Assert.True(File.Exists(p), Path.GetFileName(p) + " is kept");
            Assert.True(File.Exists(p + ".1"), Path.GetFileName(p) + ".1 is kept");
        }
    }

    [Fact]
    public void TwoLogsOnOneHeldLogEachGoOnInAFileOfTheirOwn()
    {
        Hold(LogPath, FileShare.Read);
        var a = NewLog();
        var b = NewLog();
        a.Info("from a");
        b.Info("from b");
        Assert.NotEqual(a.CurrentPath, b.CurrentPath);
        Assert.Equal(2, NewFiles().Length);
        a.Dispose();
        b.Dispose();
        Assert.Equal(Stamp + "from a", Lines(a.CurrentPath)[1]);
        Assert.Equal(Stamp + "from b", Lines(b.CurrentPath)[1]);
    }

    [Fact]
    public void ANewFileThatCannotBeMadeIsTriedUnderTheNextName()
    {
        Hold(LogPath, FileShare.Read);
        int refusedNew = 0;
        var log = NewLog(checkWrite: p =>
        {
            if (Path.GetFileName(p).StartsWith("poolwatch-", StringComparison.Ordinal) && refusedNew++ < 2)
                throw new IOException("refused", DiskFull);
            return p;
        });
        log.Info("one");
        string file = Assert.Single(NewFiles());
        log.Dispose();
        Assert.Equal(Stamp + "one", Lines(file)[1]);
        Assert.Equal(3, refusedNew); // two names refused, the third made
    }

    [Fact]
    public void ANewFileIsTriedUnderThreeNamesAtMostAndThenOnlyThatLineIsLost()
    {
        Hold(LogPath, FileShare.Read);
        int names = 0;
        var log = NewLog(checkWrite: p =>
        {
            if (Path.GetFileName(p).StartsWith("poolwatch-", StringComparison.Ordinal))
            {
                names++;
                throw new IOException("refused", DiskFull);
            }
            return p;
        });
        log.Info("lost");
        Assert.Equal(3, names);
        Assert.Empty(NewFiles());
        Assert.Equal(LogPath, log.CurrentPath);
    }

    [Fact]
    public void AnotherFaultLosesOnlyThatLineAndMakesNoNewFile()
    {
        bool armed = false;
        int faults = 1;
        var log = NewLog(checkWrite: p =>
        {
            if (armed && p == LogPath && faults-- > 0) throw new IOException("the disk is full", DiskFull);
            return p;
        });
        armed = true;
        log.Info("lost");
        log.Info("kept");
        Assert.Empty(NewFiles());
        Assert.Equal([Stamp + "kept"], Lines(LogPath));
        Assert.Equal(LogPath, log.CurrentPath);
    }

    [Fact]
    public void OnlyTheNewestFiveNewFilesAreKeptEachWithItsOldPartAndThoseOfAnotherRunningPoolWatchStay()
    {
        string Name(string at, long pid, int n) => Path.Combine(_root, $"poolwatch-{at}-{pid}-{n}.log");
        const int Running = 4242;
        int self = Environment.ProcessId;
        // d, c and g have one date and time: by process id as a number (10 before 9) and then by number (10 before 9).
        string a = Name("20261101-010203", 42, 1), b = Name("20261102-010203", 42, 1), c = Name("20261103-010203", 10, 9),
            d = Name("20261103-010203", 10, 10), g = Name("20261103-010203", 9, 50), x = Name("20261103-020000", 42, 1),
            e = Name("20261104-010203", 42, 1), f = Name("20261106-010203", 42, 1), inUse = Name("20261101-000000", 7, 1),
            alive = Name("20261101-000001", Running, 1), orphan = Name("20261101-000002", 42, 1),
            mine = Name("20261101-000003", self, 1);
        foreach (var p in new[] { a, b, c, d, g, x, e, f, inUse, alive, mine })
        {
            File.WriteAllText(p, "old");
            File.WriteAllText(p + ".1", "old");
        }
        File.WriteAllText(orphan + ".1", "old"); // its file is gone: the .1 counts as one
        File.WriteAllText(Path.Combine(_root, "poolwatch-notes.log"), "not one of them");
        Hold(inUse, FileShare.None); // another PoolWatch still writes there
        Hold(LogPath, FileShare.Read);
        var checkedPaths = new List<string>();
        var log = NewLog(checkWrite: p =>
        {
            checkedPaths.Add(p);
            return p;
        }, running: pid => pid == Running || pid == self);
        log.Info("one");

        // Newest first: f, this run's new file (2026-11-05 06:30:12), e, x, d; then c, g, b, a and the older ones. Of
        // those, alive's PoolWatch is still running and inUse cannot be deleted; this run's own older file is deleted.
        string file = Assert.Single(NewFiles());
        foreach (var kept in new[] { f, file, e, x, d, inUse, alive }) Assert.True(File.Exists(kept), Path.GetFileName(kept) + " is kept");
        foreach (var kept in new[] { f, e, x, d, inUse, alive }) Assert.True(File.Exists(kept + ".1"), Path.GetFileName(kept) + ".1 is kept");
        foreach (var gone in new[] { c, g, b, a, mine })
        {
            Assert.False(File.Exists(gone), Path.GetFileName(gone) + " is deleted");
            Assert.False(File.Exists(gone + ".1"), Path.GetFileName(gone) + ".1 is deleted");
            Assert.Contains(gone, checkedPaths);
            Assert.Contains(gone + ".1", checkedPaths);
        }
        Assert.False(File.Exists(orphan + ".1"), "the .1 whose file is gone is deleted");
        Assert.Contains(orphan + ".1", checkedPaths);
        Assert.True(File.Exists(Path.Combine(_root, "poolwatch-notes.log")));
        Assert.True(File.Exists(LogPath));
    }

    [Fact]
    public void AProcessIdThatNamesNoRunningProcessIsNotRunningAndThisProcessIs()
    {
        // The prune's own check, without the test stand-in: this process is running; an id of no process is not.
        Assert.True(Log.IsRunning(Environment.ProcessId));
        Assert.False(Log.IsRunning(int.MaxValue - 3));
    }

    [Fact]
    public void TheProfileNoticeNamesTheFileTheLinesGoTo() =>
        Assert.Contains("ProfileAtStart.Notice(profile, run, log.CurrentPath)", Source("src", "PoolWatch", "Ui", "UiRunner.cs"), StringComparison.Ordinal);
}
