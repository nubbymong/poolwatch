using System.Runtime.InteropServices;
using PoolWatch.Engine;

namespace PoolWatch.AppTests;

/// <summary>
/// The setup's and the uninstaller's step that ends the token-leak test helpers still running from PoolWatch.exe once
/// PoolWatch itself has exited (<see cref="LeftoverHelpers"/>). The choice runs against a fake process table: no process
/// is ended here. The real table is only read: this test process's own exe and command line, and the identity of files
/// the test makes in TEMP.
/// </summary>
public sealed class LeftoverHelpersTests
{
    private const string Exe = @"C:\Program Files\PoolWatch\PoolWatch.exe";
    private static readonly FileIdentity Ours = new(1, 2, 3), Theirs = new(1, 9, 3);

    private sealed class FakeProcess(string? image, string? commandLine, string? endError = null) : IOpenedProcess
    {
        public bool Ended { get; private set; }
        public bool Disposed { get; private set; }
        public string? ImagePath() => image;
        public string? CommandLine() => commandLine;

        public string? End()
        {
            if (endError is null) Ended = true;
            return endError;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeTable(Dictionary<int, FakeProcess?> processes, Dictionary<string, FileIdentity> files) : IProcessTable
    {
        public IReadOnlyList<int> Ids() => processes.Keys.ToList();
        public IOpenedProcess? Open(int id) => processes[id];
        public FileIdentity? IdentityOf(string path) => files.TryGetValue(path, out var id) ? id : null;
    }

    private static string HelperLine(string exe) => $"\"{exe}\" --token-leak-test 64 \"C:\\ProgramData\\PoolWatch\\cache\\token-test-1.json\" --token-test-parent 1234";

    [Fact]
    public void OnlyAHelperRunningFromThisVeryExeIsEnded()
    {
        const string shortExe = @"C:\PROGRA~1\POOLWA~1\POOLWA~1.EXE", copy = @"D:\Builds\PoolWatch.exe";
        var helper = new FakeProcess(Exe, HelperLine(Exe));
        var shortHelper = new FakeProcess(shortExe, HelperLine(shortExe)); // started through the folder's short (8.3) spelling
        var tray = new FakeProcess(Exe, $"\"{Exe}\" --tray");
        var mark = new FakeProcess(Exe, $"\"{Exe}\" --mark \"--token-leak-test\""); // the option, but not as the helper's first argument
        var otherCopy = new FakeProcess(copy, HelperLine(copy)); // another PoolWatch.exe: not this one
        var unreadable = new FakeProcess(null, HelperLine(Exe));
        var self = new FakeProcess(Exe, HelperLine(Exe));
        var table = new FakeTable(new() { [10] = helper, [11] = shortHelper, [12] = tray, [13] = mark, [14] = otherCopy, [15] = unreadable, [16] = null, [99] = self },
            new() { [Exe] = Ours, [shortExe] = Ours, [copy] = Theirs });
        var log = new List<string>();

        int ended = LeftoverHelpers.End(table, Exe, 99, log.Add);

        Assert.Equal(2, ended);
        Assert.True(helper.Ended);
        Assert.True(shortHelper.Ended);
        Assert.All(new[] { tray, mark, otherCopy, unreadable, self }, p => Assert.False(p.Ended));
        Assert.All(new[] { helper, shortHelper, tray, mark, otherCopy, unreadable }, p => Assert.True(p.Disposed)); // each handle is closed
        Assert.Contains("leftover helpers: ended the token-leak test helper (process 10) that still ran from " + Exe, log);
        Assert.Contains("leftover helpers: ended the token-leak test helper (process 11) that still ran from " + shortExe, log);
        Assert.Equal(2, log.Count);
    }

    [Fact]
    public void AHelperThatCannotBeEndedIsLoggedAndNotCounted()
    {
        var helper = new FakeProcess(Exe, HelperLine(Exe), endError: "error 5");
        var log = new List<string>();

        int ended = LeftoverHelpers.End(new FakeTable(new() { [10] = helper }, new() { [Exe] = Ours }), Exe, 99, log.Add);

        Assert.Equal(0, ended);
        Assert.Equal(["leftover helpers: the token-leak test helper (process 10) could not be ended: error 5"], log);
    }

    [Fact]
    public void NothingIsEndedWhenThisExeCannotBeRead()
    {
        var helper = new FakeProcess(Exe, HelperLine(Exe));
        var log = new List<string>();

        Assert.Equal(0, LeftoverHelpers.End(new FakeTable(new() { [10] = helper }, new()), Exe, 99, log.Add));
        Assert.Equal(0, LeftoverHelpers.End(new FakeTable(new() { [10] = helper }, new() { [Exe] = Ours }), null, 99, log.Add));

        Assert.False(helper.Ended);
        Assert.Equal(2, log.Count(l => l.Contains("could not be read, so none is looked for")));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\PoolWatch\\PoolWatch.exe\" --token-leak-test 64 \"C:\\x.json\"", true)]
    [InlineData("C:\\PROGRA~1\\POOLWA~1\\POOLWA~1.EXE --token-leak-test 64 C:\\x.json", true)]
    [InlineData("\"C:\\Program Files\\PoolWatch\\PoolWatch.exe\"\t--token-leak-test", true)]
    [InlineData("  \"C:\\Program Files\\PoolWatch\\PoolWatch.exe\"   --token-leak-test 64", true)]
    [InlineData("\"C:\\Program Files\\PoolWatch\\PoolWatch.exe\" --token-leak-testing 64", false)]
    [InlineData("\"C:\\Program Files\\PoolWatch\\PoolWatch.exe\" --tray --token-leak-test 64", false)]
    [InlineData("\"C:\\Program Files\\PoolWatch\\PoolWatch.exe\"--token-leak-test 64", false)]
    [InlineData("\"C:\\a --token-leak-test b\\PoolWatch.exe\" --tray", false)]
    [InlineData("\"C:\\Program Files\\PoolWatch\\PoolWatch.exe\"", false)]
    [InlineData("\"C:\\Program Files\\PoolWatch\\PoolWatch.exe", false)]
    [InlineData("PoolWatch.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TheHelperIsKnownByItsFirstArgument(string? commandLine, bool helper) =>
        Assert.Equal(helper, LeftoverHelpers.IsHelper(commandLine));

    [Fact]
    public void TheHelperIsStartedWithTheOptionAsItsFirstArgument()
    {
        // TokenLeakGuard starts it through cmd /c with the option right after the exe, so the helper's own command line is
        // "<exe>" --token-leak-test <n> "<file>" ...: either written out in TokenLeakGuard.RunHelper, or built by
        // HelperCommand.For (HelperRun.cs) from a list whose first two items are the quoted exe and the option. And the
        // option is the one the command line parses.
        string? engine = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null && engine is null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PoolWatch.slnx"))) engine = Path.Combine(dir.FullName, "src", "PoolWatch", "Engine");
        Assert.NotNull(engine);
        string guard = File.ReadAllText(Path.Combine(engine, "TokenLeakGuard.cs"));
        string run = File.Exists(Path.Combine(engine, "HelperRun.cs")) ? File.ReadAllText(Path.Combine(engine, "HelperRun.cs")) : "";
        bool writtenOut = guard.Contains("$\"/d /c \\\"\\\"{exe}\\\" " + LeftoverHelpers.HelperOption + " {GuardPolicy.Children} ");
        bool built = guard.Contains("HelperCommand.For(exe,") && run.Contains("{ QuotePath(exe), \"" + LeftoverHelpers.HelperOption + "\", ");
        Assert.True(writtenOut || built, "the helper's option is not right after the exe where TokenLeakGuard starts it");
        Assert.NotNull(Headless.CommandLineOptions.Parse([LeftoverHelpers.HelperOption, "64", @"C:\x.json"]).TokenLeakTest);
        Assert.True(LeftoverHelpers.IsHelper(HelperLine(Exe)));
    }

    // ---------------------------------------------------------------- the real table, read only

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool CreateHardLinkW(string newFile, string existingFile, IntPtr security);

    [Fact]
    public void EverySpellingOfOneFileHasOneIdentity()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pw-leftover-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "PoolWatch.exe"), other = Path.Combine(dir, "Other.exe"), link = Path.Combine(dir, "Linked.exe");
            File.WriteAllText(file, "x");
            File.WriteAllText(other, "x");
            var table = new WindowsProcessTable();

            var id = table.IdentityOf(file);
            Assert.NotNull(id);
            Assert.Equal(id, table.IdentityOf(Path.Combine(dir, ".", "POOLWATCH.EXE"))); // another spelling of the same path
            Assert.NotEqual(id, table.IdentityOf(other));
            Assert.Null(table.IdentityOf(Path.Combine(dir, "missing.exe")));
            if (CreateHardLinkW(link, file, IntPtr.Zero)) // a second name of the same file, where the volume allows it
                Assert.Equal(id, table.IdentityOf(link));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>Only this test process is opened (never another one on the PC), and nothing is ended.</summary>
    [Fact]
    public void TheRealTableReadsAProcessThroughItsHandle()
    {
        var table = new WindowsProcessTable();
        Assert.Contains(Environment.ProcessId, table.Ids());
        using var me = table.Open(Environment.ProcessId);
        Assert.NotNull(me);
        Assert.Equal(table.IdentityOf(Environment.ProcessPath!), table.IdentityOf(me.ImagePath()!));
        Assert.False(string.IsNullOrEmpty(me.CommandLine()));
        Assert.False(LeftoverHelpers.IsHelper(me.CommandLine()));
    }
}
