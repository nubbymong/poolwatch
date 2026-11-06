using System.Text.RegularExpressions;

namespace PoolWatch.AppTests;

/// <summary>
/// A cheap extra check beside the guard, not the guard. What holds each interval PoolWatch times on the clock that only
/// goes forward is the test that sets the system clock back and forward between the two times that interval compares
/// (ForwardClockTests, ForwardClockWindowTests, ForwardClockSiteTests, ForwardClockScheduleTests, TagViewClockTests and each
/// part's own; docs/DESIGN.md, section 4). This scans the code under src/, line by line, for a few ways of writing the
/// system clock into an interval, and only those (it does not try to recognise every way of writing code):
/// arithmetic or a comparison with DateTime.UtcNow or DateTime.Now read in place (a <c>+</c>, <c>-</c>, <c>&lt;</c> or
/// <c>&gt;</c> after it, a comparison or a <c>-</c> before it, or its Add methods, Ticks, Subtract or CompareTo:
/// <c>DateTime.UtcNow - x</c>); such a read kept in a local or
/// a field declared with <c>var</c>, <c>DateTime</c> or <c>DateTime?</c> (<c>var now = DateTime.UtcNow;</c>, followed to
/// the end of the local's block, a field's through the file) or assigned to a name that starts with a small letter or an
/// underscore at the start of a line or after <c>;</c>, <c>{</c>, <c>}</c> or <c>)</c> (<c>if (saved) _last =
/// DateTime.UtcNow;</c>, followed through the file), then used in such arithmetic; a <c>Func&lt;DateTime&gt;</c> declared
/// so and called in it; a time of day turned into milliseconds by <c>.Ticks / 10_000</c> (or <c>10000</c>, or
/// <c>TimeSpan.TicksPerMillisecond</c>); and each <c>new Moment(</c> or <c>new Core.Model.Moment(</c>. It does not find,
/// among others, a read assigned after <c>else</c>, <c>=&gt;</c> or a <c>case</c> label, by <c>??=</c>, in a tuple or to
/// <c>this.x</c>; a read handed to another method; a moment made with a target-typed <c>new(...)</c> or a sample changed
/// with <c>with</c>; or milliseconds made another way (<see cref="TheScansFindTheFormsTheyNameAndNotOthers"/> pins some of
/// these). Each place it finds is named here: a system clock read with why it is a time of day, a moment with where its two
/// clocks come from.
/// </summary>
public sealed class SystemClockScanTests
{
    /// <summary>Arithmetic or a comparison with the system clock read in place (not a lambda that reads it, nor another type's Now).</summary>
    private static readonly Regex WallArithmetic =
        new(@"DateTime\.(UtcNow|Now)\s*[-+<>]|(?<!=)[<>]=?\s*DateTime\.(UtcNow|Now)\b|DateTime\.(UtcNow|Now)\.(Add|Ticks|Subtract|CompareTo)|-\s*DateTime\.(UtcNow|Now)\b");

    /// <summary>A local or a field given the system clock's time: <c>var now = DateTime.UtcNow;</c>, <c>DateTime? t = a ? b : DateTime.Now;</c>.</summary>
    private static readonly Regex StoredDeclaration = new(@"\b(?:var|DateTime\??)\s+(?<name>[A-Za-z_]\w*)\s*=(?![=>])(?:(?!=>)[^;])*\bDateTime\.(?:UtcNow|Now)\b");

    /// <summary>
    /// An assignment of it at the start of a line or after <c>;</c>, <c>{</c>, <c>}</c> or <c>)</c>, to a name that starts with
    /// a small letter or an underscore (a local or a field, not a property or an initializer's member), the time itself and
    /// not a call's result made with it: <c>_last = DateTime.UtcNow;</c>, <c>if (saved) _last = DateTime.UtcNow;</c>,
    /// <c>{ _last = DateTime.UtcNow; }</c>, <c>now = h.Count &gt; 0 ? h.Times[^1] : DateTime.UtcNow;</c> (not a lambda that
    /// reads it). The name is followed through the whole file.
    /// </summary>
    private static readonly Regex StoredAssignment =
        new(@"(?:^|[;{}]\s*|\)\s*)(?<name>[a-z_]\w*)\s*=(?![=>])[^;(]*\bDateTime\.(?:UtcNow|Now)\b[^;(]*;");

    /// <summary>A time of day turned into milliseconds by <c>.Ticks / 10_000</c> (or <c>10000</c>, or <c>TimeSpan.TicksPerMillisecond</c>).</summary>
    private static readonly Regex TicksToMs = new(@"\.Ticks\s*/\s*(?:10_?000\b|TimeSpan\.TicksPerMillisecond\b)");

    /// <summary><c>new Moment(</c> or <c>new Core.Model.Moment(</c>: a moment made from two values (not a target-typed <c>new(...)</c>).</summary>
    private static readonly Regex NewMoment = new(@"\bnew\s+(?:Core\.Model\.)?Moment\s*\(");

    /// <summary>Each <c>new Moment(...)</c> in src the scan finds, by file and text, with where its two clocks come from.</summary>
    private static readonly (string File, string Text, string Why)[] MomentsMade =
    [
        ("MonitorEngine.cs", "new Moment(at.Time.Utc, at.Time.Ms)", "the sampler tick's two clocks, read together as it began"),
        ("MonitorEngine.cs", "new Moment(stopped.Utc, stopped.Ms)", "the two clocks, read together as the engine stops (TickTime.Now)"),
        ("IncidentManager.cs", "new Moment(h.Times[^1], h.Ms[^1])", "the newest sample's own moment"),
        ("ProcessRegistry.cs", "new Moment(snapshotUtc, snapshotMs)", "the snapshot's time and that time read onto the forward clock (Moment.MsOf)"),
    ];

    /// <summary>A clock function of the system clock (<c>Func&lt;DateTime&gt; now</c>), whose calls read it.</summary>
    private static readonly Regex WallClockFunction = new(@"\bFunc<DateTime>\??\s+(?<name>[A-Za-z_]\w*)");

    /// <summary>A field's declaration (its name reaches the whole file), not a local's (its block).</summary>
    private static readonly Regex FieldModifier = new(@"^\s*(?:(?:private|public|internal|protected|static|readonly)\s+)+");

    /// <summary>
    /// Arithmetic or a comparison with <paramref name="name"/> (a call of it when <paramref name="call"/>): an operator after
    /// it, or one before it written as an operator is (after a space or a bracket, so not a type's <c>Func&lt;DateTime&gt; x</c>
    /// nor a lambda's arrow), or its ticks or its Add methods.
    /// </summary>
    private static Regex UsedInArithmetic(string name, bool call)
    {
        string x = Regex.Escape(name) + (call ? @"\(\)" : "");
        return new($@"(?<![\w.]){x}(?![\w(])\s*(?:[-+]=?|<=?|>=?)|(?<=[\s)\]])(?:[-+]|<=?|(?<!=)>=?)\s*{x}(?![\w(])|(?<![\w.]){x}\.(?:Add\w*|Ticks|Subtract|CompareTo)\b");
    }

    /// <summary>A string or character literal's text, left out of the scan (a file name such as "since-boot.md" is not arithmetic).</summary>
    private static readonly Regex Literal = new(@"""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)'");

    /// <summary>The places left, by file and text, each with why it reads the system clock.</summary>
    private static readonly (string File, string Text, string Why)[] TimesOfDay =
    [
        ("AnalysisPipeline.cs", "var boot = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);", "when Windows started, as a time of day"),
        ("WindowsIncidentHost.cs", "BootUtc = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64),", "when Windows started, as a time of day"),
        ("FilterCollectors.cs", "long sinceLastMs = (long)(DateTime.UtcNow - _saved).TotalMilliseconds;",
            "an event record's time is the system time Windows stamped it with, and the log's query measures from the system time now (docs/DESIGN.md, section 4)"),
        ("CostReport.cs", "double elapsed = (DateTime.Now - p.StartTime).TotalSeconds;", "Windows gives a process's start as a time of day"),
        ("UiSnapshot.cs", "var from = DateTime.UtcNow - span;", "the start of a shown window over samples stamped with the system time"),
        ("UiSnapshot.cs", "r.Error is null && r.TimeUtc >= from", "the clears marked in that shown window, by the time of day they are shown at"),
        ("UiSnapshot.cs", "var cutoff = now - span;",
            "the start of the graph's shown window over samples stamped with the system time (found through another method's local named alike)"),
        ("PoolChart.cs", "DateTime t1 = _points.Count > 0 ? _points[^1].TimeUtc : DateTime.UtcNow, t0 = t1 - _span;",
            "the graph's shown window, drawn over points stamped with the system time"),
        ("PoolChart.cs", "c.TimeUtc >= t0 && c.TimeUtc <= t1", "the clears drawn in the graph's shown window, by the time of day they are drawn at"),
        ("PoolChart.cs", "DateTime from = t1 - _span;", "the start of the graph's shown window"),
        ("PoolChart.cs", "double span = Math.Max(60, (t1 - t0).TotalSeconds);", "the width of the graph's shown window"),
        ("PoolChart.cs", "var b = c.ToUtc > t1 ? t1 : c.ToUtc;", "a RAM column cut at the end of the graph's shown window"),
        ("PoolChart.cs", "for (; tick.ToUniversalTime() <= t1; tick += interval)", "the time-of-day ticks of the graph's axis"),
    ];

    /// <summary>
    /// Parts that time their intervals on the clock that only goes forward, by the method that is fed a time: each takes a
    /// <see cref="Core.Model.Moment"/>, a count of the forward clock's milliseconds or a sample (whose moment is its own), and
    /// no DateTime. This checks their parameters only, not what a caller hands them.
    /// </summary>
    private static readonly (Type Type, string Method)[] OnTheForwardClock =
    [
        (typeof(Core.Spawn.ChildAccumulation), nameof(Core.Spawn.ChildAccumulation.Observe)),
        (typeof(Core.Spawn.ChildAccumulation), nameof(Core.Spawn.ChildAccumulation.Due)),
        (typeof(Core.Model.EventSummarizer), nameof(Core.Model.EventSummarizer.Process)),
        (typeof(Core.Model.EventSummarizer), nameof(Core.Model.EventSummarizer.Flush)),
        (typeof(Core.Spawn.SpawnTracker), nameof(Core.Spawn.SpawnTracker.Observe)),
        (typeof(Core.Spawn.SpawnTracker), nameof(Core.Spawn.SpawnTracker.Rates)),
        (typeof(Core.Spawn.GitRootResolver), nameof(Core.Spawn.GitRootResolver.FindRoot)),
        (typeof(Core.Incidents.IncidentManager), nameof(Core.Incidents.IncidentManager.Suppress)),
        (typeof(Core.Incidents.IncidentManager), nameof(Core.Incidents.IncidentManager.Process)),
        (typeof(Core.Incidents.IncidentManager), nameof(Core.Incidents.IncidentManager.AddEvents)),
        (typeof(Core.Incidents.IncidentManager), nameof(Core.Incidents.IncidentManager.EventsBetween)),
        (typeof(Core.Incidents.IncidentManager), nameof(Core.Incidents.IncidentManager.StartsByOwner)),
        (typeof(Core.Incidents.IncidentManager), nameof(Core.Incidents.IncidentManager.EventWindowEndMs)),
        (typeof(Core.Growth.GrowthLedger), nameof(Core.Growth.GrowthLedger.Observe)),
        (typeof(Core.Detection.OwnClears), nameof(Core.Detection.OwnClears.Measuring)),
        (typeof(Core.Cache.ClearPolicy), nameof(Core.Cache.ClearPolicy.Due)),
        (typeof(Core.Cache.ClearPolicy), nameof(Core.Cache.ClearPolicy.Next)),
        (typeof(Core.Cache.ClearPolicy), nameof(Core.Cache.ClearPolicy.Automatic)),
        (typeof(Core.Cache.ClearPolicy), nameof(Core.Cache.ClearPolicy.EscalateNow)),
        (typeof(Core.Experiments.Experiment), nameof(Core.Experiments.Experiment.Status)),
        (typeof(Core.Experiments.ClearLagExperiment), nameof(Core.Experiments.ClearLagExperiment.Status)),
        (typeof(Core.Experiments.MarkInbox), nameof(Core.Experiments.MarkInbox.Take)),
        (typeof(Core.Boot.BootTracker), nameof(Core.Boot.BootTracker.Flush)),
        (typeof(Core.Findings.FindingLog), nameof(Core.Findings.FindingLog.Within)),
        (typeof(Core.History.History), nameof(Core.History.History.IndexAtOrAfterMs)),
        (typeof(Core.History.History), nameof(Core.History.History.HandlesAtOrBeforeMs)),
        (typeof(Core.Model.Every), nameof(Core.Model.Every.Due)),
        (typeof(Core.Model.Every), nameof(Core.Model.Every.DueAt)),
        (typeof(Core.Model.Every), nameof(Core.Model.Every.IsDue)),
        (typeof(Core.Model.Every), nameof(Core.Model.Every.NextIn)),
        (typeof(Core.Model.Deadline), nameof(Core.Model.Deadline.After)),
        (typeof(Core.Model.Deadline), nameof(Core.Model.Deadline.Passed)),
        (typeof(Analysis.CheckSchedule), nameof(Analysis.CheckSchedule.At)),
        (typeof(Analysis.CheckSchedule), nameof(Analysis.CheckSchedule.CrashPageFull)),
        (typeof(Analysis.CheckSchedule), nameof(Analysis.CheckSchedule.ReadFilters)),
        (typeof(Analysis.CheckSchedule), "FilterServicesRead"), // private: counted as read only through ReadFilters
        (typeof(Analysis.CheckSchedule), "FilterInstancesRead"),
        (typeof(Analysis.CacheClearer), nameof(Analysis.CacheClearer.ToMeasure)),
        (typeof(Analysis.ClearerSchedule), nameof(Analysis.ClearerSchedule.At)),
        (typeof(Analysis.WorkerReads), nameof(Analysis.WorkerReads.At)),
        (typeof(Core.Filters.FilterTimeline), nameof(Core.Filters.FilterTimeline.OnLogEvent)),
        (typeof(Core.Incidents.IncidentManager), "IncidentEvents"), // internal to the core
        (typeof(Core.Recommendations.RecommendationRules), nameof(Core.Recommendations.RecommendationRules.Hidden)),
        (typeof(Engine.Setup.RecommendationStore), nameof(Engine.Setup.RecommendationStore.Dismiss)),
        (typeof(Core.Incidents.CpuProfile), nameof(Core.Incidents.CpuProfile.Recorded)),
        (typeof(Ui.ChangeCountSchedule), nameof(Ui.ChangeCountSchedule.Start)),
        (typeof(Ui.ChangeCountSchedule), nameof(Ui.ChangeCountSchedule.Activated)),
        (typeof(Ui.TaskReadClock), nameof(Ui.TaskReadClock.Due)),
        (typeof(Ui.TaskReadClock), nameof(Ui.TaskReadClock.Start)),
        (typeof(Ui.RankPlan), nameof(Ui.RankPlan.Next)),
        (typeof(Ui.PoolCauses), nameof(Ui.PoolCauses.Shown)),
        (typeof(Ui.MainForm), nameof(Ui.MainForm.RecentOwnClear)),
        (typeof(Engine.Setup.RecommendationInputsBuilder), nameof(Engine.Setup.RecommendationInputsBuilder.Reportable)),
        (typeof(Engine.Setup.RecommendationInputsBuilder), nameof(Engine.Setup.RecommendationInputsBuilder.MetadataGrewLastDay)),
        (typeof(Engine.UpdateCheck), nameof(Engine.UpdateCheck.NextWake)),
    ];

    [Fact]
    public void EachListedForwardClockMethodTakesNoTimeOfDay()
    {
        const System.Reflection.BindingFlags All = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance;
        foreach (var (type, name) in OnTheForwardClock)
        {
            var methods = type.GetMethods(All).Where(m => m.Name == name).ToList();
            Assert.True(methods.Count == 1, $"{type.Name}.{name}: {methods.Count} methods");
            var parameters = methods[0].GetParameters();
            Assert.DoesNotContain(parameters, p => p.ParameterType == typeof(DateTime) || p.ParameterType == typeof(DateTime?));
            Assert.Contains(parameters, p => p.ParameterType == typeof(Core.Model.Moment) || p.ParameterType == typeof(long) || p.ParameterType == typeof(Core.Model.Sample));
        }
    }

    /// <summary>Each line of <paramref name="lines"/> that does arithmetic or a comparison with the system clock now, in the forms the class names.</summary>
    internal static IEnumerable<string> WallLines(IReadOnlyList<string> lines)
    {
        var shown = Shown(lines);
        var code = shown.Select(l => Literal.Replace(l, "\"\"")).ToList();
        // Each line's brace depth at its start, so a local's block can be told.
        var depth = new int[code.Count + 1];
        for (int i = 0; i < code.Count; i++) depth[i + 1] = depth[i] + code[i].Count(ch => ch == '{') - code[i].Count(ch => ch == '}');
        var hits = new SortedSet<int>();
        for (int i = 0; i < code.Count; i++)
        {
            if (WallArithmetic.IsMatch(code[i])) hits.Add(i);
            void Follow(string name, bool call, bool wholeFile, int from)
            {
                var used = UsedInArithmetic(name, call);
                int end = code.Count;
                if (!wholeFile)
                    for (int j = from + 1; j < code.Count; j++) if (depth[j + 1] < depth[from]) { end = j + 1; break; }
                for (int j = wholeFile ? 0 : from; j < end; j++)
                    if (used.IsMatch(j == from && !wholeFile ? code[j][(code[j].IndexOf(name, StringComparison.Ordinal) + name.Length)..] : code[j])) hits.Add(j);
            }
            if (StoredDeclaration.Match(code[i]) is { Success: true } d) Follow(d.Groups["name"].Value, false, FieldModifier.IsMatch(code[i]), i);
            if (StoredAssignment.Match(code[i]) is { Success: true } a) Follow(a.Groups["name"].Value, false, true, i);
            if (WallClockFunction.Match(code[i]) is { Success: true } f) Follow(f.Groups["name"].Value, true, true, i);
        }
        foreach (int i in hits) yield return shown[i];
    }

    /// <summary>The lines as the scans read them: trimmed, a line comment read as empty.</summary>
    private static List<string> Shown(IReadOnlyList<string> lines) =>
        lines.Select(l => l.TrimStart()).Select(l => l.StartsWith("//", StringComparison.Ordinal) ? "" : l).ToList();

    /// <summary>Each line of <paramref name="lines"/> with <c>.Ticks / 10_000</c> (or its like) or <c>new Moment(</c>.</summary>
    internal static IEnumerable<string> MadeMoments(IReadOnlyList<string> lines) =>
        Shown(lines).Where(l => Literal.Replace(l, "\"\"") is var code && (TicksToMs.IsMatch(code) || NewMoment.IsMatch(code)));

    private static IEnumerable<(string File, string Line)> SourceLines(Func<IReadOnlyList<string>, IEnumerable<string>> scan)
    {
        string src = Path.Combine(TrayIconsTests.RepoRoot(), "src");
        char sep = Path.DirectorySeparatorChar;
        foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{sep}bin{sep}") || file.Contains($"{sep}obj{sep}")) continue;
            foreach (string line in scan(File.ReadAllLines(file))) yield return (Path.GetFileName(file), line);
        }
    }

    [Fact]
    public void EachSystemClockReadTheScanFindsInArithmeticIsATimeOfDay()
    {
        var found = SourceLines(WallLines).ToList();
        var left = found.Where(w => !TimesOfDay.Any(t => t.File == w.File && w.Line.Contains(t.Text, StringComparison.Ordinal)))
            .Select(w => $"{w.File}: {w.Line}").ToList();
        Assert.True(left.Count == 0, "the system clock read for arithmetic that is not a time of day: " + string.Join(Environment.NewLine, left));
        // Each place named is still there (a name that no longer matches anything would let a new one through).
        Assert.All(TimesOfDay, t => Assert.Contains(found, w => w.File == t.File && w.Line.Contains(t.Text, StringComparison.Ordinal)));
    }

    [Fact]
    public void EachNewMomentAndTicksToMillisecondsTheScanFindsIsNamed()
    {
        var found = SourceLines(MadeMoments).ToList();
        var left = found.Where(w => TicksToMs.IsMatch(w.Line) || !MomentsMade.Any(m => m.File == w.File && w.Line.Contains(m.Text, StringComparison.Ordinal)))
            .Select(w => $"{w.File}: {w.Line}").ToList();
        Assert.True(left.Count == 0, "a new Moment(...) not named here, or a time of day turned into milliseconds: " + string.Join(Environment.NewLine, left));
        Assert.All(MomentsMade, m => Assert.Contains(found, w => w.File == m.File && w.Line.Contains(m.Text, StringComparison.Ordinal)));
        // The forms it finds, and what it leaves.
        string[] made =
        [
            "if (_labels.Due(new Core.Model.Moment(s.TimeUtc, s.TimeUtc.Ticks / 10_000)))", "long ms = t.Ticks / TimeSpan.TicksPerMillisecond;",
            "var at = new Moment(s.TimeUtc, s.TickMs);",
        ];
        Assert.Equal(made, MadeMoments([.. made, "double cpuMs = kv.Value.CpuTicks / 10_000.0;", "// new Moment(a, b)", "Log(\"new Moment(\");"]));
    }

    [Fact]
    public void TheArithmeticScanFindsTheReadsInPlaceItNames()
    {
        string[] wall =
        [
            "if (DateTime.UtcNow - _builtAt > TimeSpan.FromMinutes(10)) { _map = Build(); }",
            "var deadline = DateTime.UtcNow.AddMinutes(6);",
            "while (Captures.Busy && DateTime.UtcNow < deadline) Thread.Sleep(200);",
            "if (_drives.TryGetValue(d, out var c) && c.until > DateTime.UtcNow) return c.fixedDrive;",
            "var giveUp = DateTime.UtcNow + MeasureWait;",
            "ProbeTripping = () => { lock (Lag) return Lag.Tripping(DateTime.UtcNow.Ticks / 10_000, probeFresh); };",
        ];
        Assert.All(wall, l => Assert.Matches(WallArithmetic, l));
        string[] fine = ["_now = now ?? (() => DateTime.UtcNow);", "long until = svc.Now + StopWaitSeconds * 1000L;", "stamp = DateTime.UtcNow;"];
        Assert.All(fine, l => Assert.DoesNotMatch(WallArithmetic, l));
    }

    [Fact]
    public void TheScanFindsTheSystemClockKeptFirstAndUsedLater()
    {
        // Kept in a local, used in a later statement of its block (the device map's age read in two statements).
        Assert.Equal(["if (now - _builtAt > TimeSpan.FromMinutes(10)) { _map = Build(); _builtAt = now; }"], WallLines(
        [
            "public static string ToDosPath(string? ntPath)",
            "{",
            "    var now = DateTime.UtcNow;",
            "    if (now - _builtAt > TimeSpan.FromMinutes(10)) { _map = Build(); _builtAt = now; }",
            "    return ntPath;",
            "}",
        ]));
        // Kept in a field, compared anywhere in the file.
        Assert.Equal(["if (DateTime.MinValue < _lastSave) return;"], WallLines(
        [
            "private DateTime _lastSave = DateTime.UtcNow;",
            "void Save() { _lastSave = DateTime.UtcNow; }",
            "void Check()",
            "{",
            "    if (DateTime.MinValue < _lastSave) return;",
            "}",
        ]));
        // Assigned to a field declared without a value, inside a method's braces or after an if, then used in another method.
        Assert.Equal(["bool Due(DateTime now) => now - _lastSave >= Every;"], WallLines(
            ["private DateTime _lastSave;", "void Save() { _lastSave = DateTime.UtcNow; }", "bool Due(DateTime now) => now - _lastSave >= Every;"]));
        Assert.Equal(["bool Old(DateTime stamp) => _lastSave.AddMinutes(10) > stamp;"], WallLines(
            ["private DateTime _lastSave;", "if (saved) _lastSave = DateTime.UtcNow;", "bool Old(DateTime stamp) => _lastSave.AddMinutes(10) > stamp;"]));
        // Assigned to a name declared elsewhere, then its ticks used.
        Assert.Equal(["long ms = wall.Ticks / 10_000;"], WallLines(["wall = DateTime.UtcNow;", "long ms = wall.Ticks / 10_000;"]));
        // Not followed: a property or an initializer's member given the time (a time written), a call's result made with it.
        Assert.Empty(WallLines(["var f = new Finding { TimeUtc = DateTime.UtcNow, Title = t };", "if (r.TimeUtc - x > y) return;"]));
        Assert.Empty(WallLines(["try { op = Plan.Build(name, DateTime.UtcNow); }", "if (op + 1 > 2) return;"]));
        // A clock function of the system clock, called in arithmetic.
        Assert.Equal(["if (_now() - _last >= Every) Run();"], WallLines(
            ["private readonly Func<DateTime> _now = () => DateTime.UtcNow;", "if (_now() - _last >= Every) Run();"]));
        // Not arithmetic: a time kept and only written or shown, a local of another block, a lambda's arrow.
        Assert.Empty(WallLines(
        [
            "void A()",
            "{",
            "    var now = DateTime.UtcNow;",
            "    Write(now);",
            "}",
            "void B(DateTime now) { if (now - x > y) return; }",
            "var t = DateTime.UtcNow; items.Where(e => t == e.Time);",
        ]));
    }

    [Fact]
    public void TheScansFindTheFormsTheyNameAndNotOthers()
    {
        // Forms the scans do not try to recognise (the set-back tests of each part are the guard): a read assigned after else,
        // => or ??=, then used in arithmetic; a moment made with a target-typed new(...), or a sample changed with `with`.
        foreach (string assigned in new[] { "if (a) Foo(); else _last = DateTime.UtcNow;", "void Save() => _last = DateTime.UtcNow;", "_last ??= DateTime.UtcNow;" })
            Assert.Empty(WallLines(["private DateTime _last;", assigned, "bool Due(DateTime t) => t - _last > Every;"]));
        Assert.Empty(MadeMoments(["Moment at = new(s.TimeUtc, (long)(s.TimeUtc - DateTime.UnixEpoch).TotalMilliseconds);",
            "_labels.DueAt(s with { TickMs = (long)(s.TimeUtc - DateTime.UnixEpoch).TotalMilliseconds });"]));
        // The forms they name.
        Assert.Single(WallLines(["private DateTime _last;", "if (a) _last = DateTime.UtcNow;", "bool Due(DateTime t) => t - _last > Every;"]));
        Assert.Single(MadeMoments(["var at = new Core.Model.Moment(s.TimeUtc, s.TickMs);"]));
    }
}
