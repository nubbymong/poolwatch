using PoolWatch.Core;
using PoolWatch.Core.Changes;
using PoolWatch.Engine;
using PoolWatch.Engine.Changes;

namespace PoolWatch.AppTests;

/// <summary>
/// Replacing a clear task of your own (the setup guide) and putting it back (Undo), with Task Scheduler faked: a saved
/// definition is untrusted, the two clears never both run, and what PoolWatch cannot put back is saved for import.
/// </summary>
public sealed class ClearTaskReplacerTests : IDisposable
{
    private readonly ChangeFixture _f = new();

    public void Dispose() => _f.Dispose();

    private static CacheHelper.LegacyTaskInfo Own(string path = @"\Clear standby", string? xml = null, int logonType = 5, string? user = "S-1-5-18") =>
        new(path, "SYSTEM", "RAMMap64.exe -Et", "1 trigger", true) { Xml = xml ?? TaskXml.Ram(), LogonType = logonType, UserId = user };

    // ---------------------------------------------------------------- the saved definition

    /// <summary>A definition with a security descriptor that lets everyone change the task.</summary>
    private static string WithAccessRules => TaskXml.Ram().Replace("<Principals>", "<RegistrationInfo><SecurityDescriptor>D:(A;;FA;;;WD)</SecurityDescriptor></RegistrationInfo><Principals>");

    [Fact]
    public void AClearTaskDefinitionIsRead()
    {
        var (programs, refused) = ClearTaskReplacer.ReadDefinition(TaskXml.Ram(), "SYSTEM");
        Assert.Null(refused);
        Assert.Equal([TaskXml.AdminRam], programs);
        Assert.Null(ClearTaskReplacer.ReadDefinition(TaskXml.Of("<Exec><Command>\"C:\\Tools\\RAMMap.exe\"</Command><Arguments>-Ew -Es</Arguments></Exec>"), "SYSTEM").Refused);
        // Two clears from one folder: the one folder a warning names is all of it.
        Assert.Equal(2, ClearTaskReplacer.ReadDefinition(TaskXml.Of(
            @"<Exec><Command>C:\Tools\RAMMap64.exe</Command><Arguments>-Ew</Arguments></Exec><Exec><Command>C:\tools\RAMMap.exe</Command><Arguments>-Et</Arguments></Exec>"), "SYSTEM").Programs.Count);
    }

    /// <summary>The definition with its principal's account replaced by <paramref name="principal"/> (a whole UserId or GroupId part, or more).</summary>
    private static string RunsAs(string principal) => TaskXml.Ram().Replace("<UserId>S-1-5-18</UserId>", principal);

    /// <summary>Why it is refused, the definition, and a word of the reason, so each row is refused by its own check.</summary>
    public static TheoryData<string, string, string> RefusedDefinitions() => new()
    {
        { "another program", TaskXml.Ram(@"C:\Windows\System32\cmd.exe", "/c RAMMap64.exe -Et"), "other than RAMMap" },
        { "RAMMap without a clear option", TaskXml.Ram(arguments: "-accepteula"), "other than RAMMap" },
        { "a COM action", TaskXml.Of("<ComHandler><ClassId>{00000000-0000-0000-0000-000000000001}</ClassId></ComHandler>"), "not a program" },
        { "RAMMap and a COM action", TaskXml.Of($"<Exec><Command>{TaskXml.AdminRam}</Command><Arguments>-Et</Arguments></Exec><ComHandler><ClassId>{{00000000-0000-0000-0000-000000000001}}</ClassId></ComHandler>"), "not a program" },
        { "RAMMap and another program", TaskXml.Of($"<Exec><Command>{TaskXml.AdminRam}</Command><Arguments>-Et</Arguments></Exec><Exec><Command>C:\\x\\run.exe</Command></Exec>"), "other than RAMMap" },
        { "no action", TaskXml.Of(""), "no actions" },
        { "a relative program", TaskXml.Ram("RAMMap64.exe"), "full path" },
        { "a variable Task Scheduler expands as SYSTEM", TaskXml.Ram(@"%TEMP%\RAMMap64.exe"), "uses a variable" },
        { "a variable inside a full path", TaskXml.Ram(@"C:\%TOOLS%\RAMMap64.exe"), "uses a variable" },
        { "a DTD", TaskXml.Of($"<Exec><Command>{TaskXml.AdminRam}</Command><Arguments>-Et</Arguments></Exec>", "<!DOCTYPE Task [<!ENTITY e \"x\">]>"), "not a valid task" },
        { "its own access rules", WithAccessRules, "access rules" },
        { "over 64 KB", TaskXml.Ram().Replace("<Principals>", "<!--" + new string('x', ChangeRecordFile.MaxTaskXmlChars) + "--><Principals>"), "over 64 KB" },
        { "not a task", "<Other xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Actions><Exec><Command>C:\\RAMMap64.exe</Command><Arguments>-Et</Arguments></Exec></Actions></Other>", "no actions" },
        { "not XML", "<Task", "not a valid task" },
        // One program with its own options, read the way Task Scheduler runs it.
        { "two programs in one action", TaskXml.Of($"<Exec><Command>{TaskXml.AdminRam}</Command><Command>C:\\x\\run.exe</Command><Arguments>-Et</Arguments></Exec>"), "one program with its arguments" },
        { "two sets of arguments", TaskXml.Of($"<Exec><Command>{TaskXml.AdminRam}</Command><Arguments>-Et</Arguments><Arguments>-Ew</Arguments></Exec>"), "one program with its arguments" },
        { "a part an action does not have", TaskXml.Of($"<Exec><Command>{TaskXml.AdminRam}</Command><Arguments>-Et</Arguments><Other>x</Other></Exec>"), "one program with its arguments" },
        { "RAMMap with more than its clear options", TaskXml.Ram(arguments: "-Et snapshot.rmp"), "more than its memory clear options" },
        { "arguments filled in when the task starts", TaskXml.Ram(arguments: "-Et $(Arg0)"), "more than its memory clear options" },
        { "a word that ends in a clear option", TaskXml.Ram(arguments: "-Et snapshot-Et"), "more than its memory clear options" },
        { "a clear option with more after it", TaskXml.Ram(arguments: "-Et -Et.rmp"), "more than its memory clear options" },
        { "-accepteula with more after it", TaskXml.Ram(arguments: "-Et -accepteula.rmp"), "more than its memory clear options" },
        { "a folder filled in when the task starts", TaskXml.Ram(@"C:\Program Files\$(Arg0)\RAMMap64.exe"), "uses a variable" },
        { "starting in another folder", StartsIn(TaskXml.AdminRam, @"C:\Users\Public"), "starts in a folder" },
        { "starting in a folder with a variable", StartsIn(TaskXml.AdminRam, @"%TEMP%"), "starts in a folder" },
        { "starting in a folder filled in when the task starts", StartsIn(TaskXml.AdminRam, "$(Arg0)"), "starts in a folder" },
        // A variable that a ".." after it hides from the folder comparison: Task Scheduler expands it first.
        { "starting in its own folder by way of a variable", StartsIn(TaskXml.AdminRam, @"C:\Program Files\Sysinternals\%X%\.."), "starts in a folder" },
        { "starting in its own folder by way of a value filled in", StartsIn(TaskXml.AdminRam, @"C:\Program Files\Sysinternals\$(Arg0)\.."), "starts in a folder" },
        // The path judged must be the one text Task Scheduler is given, in the one form Windows makes of it.
        { "a quote inside the program's path", TaskXml.Ram("C:\\Tools\\a\"b\\RAMMap64.exe"), "quote" },
        { "a path that climbs out of its folder", TaskXml.Ram(@"C:\Program Files\Sysinternals\..\..\Users\Public\RAMMap64.exe"), "plain form" },
        { "a forward slash in the path", TaskXml.Ram("C:\\Program Files/Sysinternals/RAMMap64.exe"), "plain form" },
        { "a device path", TaskXml.Ram(@"\\?\C:\Program Files\Sysinternals\RAMMap64.exe"), "plain form" },
        { "a device path by \\\\.\\", TaskXml.Ram(@"\\.\C:\Program Files\Sysinternals\RAMMap64.exe"), "plain form" },
        { "a device path by \\??\\", TaskXml.Ram(@"\??\C:\Program Files\Sysinternals\RAMMap64.exe"), "plain form" },
        // A start folder with no drive would be read against PoolWatch's own drive: on any drive, refused by that alone.
        { "starting in a folder with no drive", StartsIn(Path.GetPathRoot(Environment.CurrentDirectory) + @"Program Files\Sysinternals\RAMMap64.exe", @"\Program Files\Sysinternals"), "starts in a folder" },
        // Each part judged is read as its joined text, so it must be plain text, as Task Scheduler writes it.
        { "a comment inside the program's path", TaskXml.Ram(@"C:\Program Files\Sysinternals\<!-- x -->RAMMap64.exe"), "not plain text" },
        { "the program's path as CDATA", TaskXml.Ram(@"<![CDATA[C:\Program Files\Sysinternals\RAMMap64.exe]]>"), "not plain text" },
        { "a processing instruction inside the arguments", TaskXml.Ram(arguments: "-E<?x y?>t"), "not plain text" },
        { "an element inside the program's path", TaskXml.Ram(@"C:\Program Files\Sysinternals\<b>RAMMap64.exe</b>"), "not plain text" },
        { "a comment inside the start folder", StartsIn(TaskXml.AdminRam, @"C:\Program Files\<!-- x -->Sysinternals"), "not plain text" },
        { "a comment inside the account", RunsAs("<UserId>S-1-5-<!-- x -->18</UserId>"), "not plain text" },
        // No file or folder name holds a line break: the text after one is not the path judged.
        { "a line break inside the program's path", TaskXml.Ram(@"C:\Program Files\Sysinternals\x" + "\n" + @"C:\Users\Public\RAMMap64.exe"), "line break" },
        { "a line break after the program's path", TaskXml.Ram(TaskXml.AdminRam + "&#10;"), "line break" },
        { "a line break in the start folder", StartsIn(TaskXml.AdminRam, @"C:\Program Files\Sysinternals&#13;"), "line break" },
        // Nor do RAMMap's options, which only spaces and tabs separate.
        { "a line break between the options", TaskXml.Ram(arguments: "-Et&#10;-Ew"), "line break" },
        { "a carriage return after the options", TaskXml.Ram(arguments: "-Et&#13;"), "line break" },
        { "a space Windows keeps in a name, before the path", TaskXml.Ram("\u00A0" + TaskXml.AdminRam), "full path" },
        { "a part in another namespace", TaskXml.Ram().Replace("</Actions>", "</Actions><Actions xmlns=\"urn:other\"><Exec><Command>C:\\x\\run.exe</Command></Exec></Actions>"), "not Task Scheduler's" },
        // A yes is about one folder: RAMMap from two is never put back by PoolWatch.
        { "RAMMap from two folders", TaskXml.Of(@"<Exec><Command>C:\Tools\RAMMap64.exe</Command><Arguments>-Et</Arguments></Exec><Exec><Command>C:\Other\RAMMap64.exe</Command><Arguments>-Ew</Arguments></Exec>"), "more than one folder" },
        // The account it runs as is the one it is registered with (SYSTEM here), whichever of the two Task Scheduler takes.
        { "a group", RunsAs("<GroupId>S-1-5-32-545</GroupId>"), "group" },
        { "another account", RunsAs("<UserId>S-1-5-21-1-2-3-1001</UserId>"), "not the one it ran as" },
        { "two accounts in one principal", RunsAs("<UserId>S-1-5-18</UserId><UserId>S-1-5-21-1-2-3-1001</UserId>"), "one account" },
        { "two principals", TaskXml.Ram().Replace("</Principal></Principals>", "</Principal><Principal id=\"Other\"><UserId>S-1-5-21-1-2-3-1001</UserId></Principal></Principals>"), "one account" },
        { "two lists of principals", TaskXml.Ram().Replace("<Principals>", "<Principals><Principal id=\"Other\"><UserId>S-1-5-21-1-2-3-1001</UserId></Principal></Principals><Principals>"), "one account" },
        { "a principal by another name", TaskXml.Ram().Replace("<Principal id=\"Author\">", "<Owner id=\"Author\">").Replace("</Principal></Principals>", "</Owner></Principals>"), "one account" },
    };

    /// <summary>A RAMMap clear that starts in <paramref name="folder"/>.</summary>
    private static string StartsIn(string program, string folder) =>
        TaskXml.Of($"<Exec><Command>{program}</Command><Arguments>-Et</Arguments><WorkingDirectory>{folder}</WorkingDirectory></Exec>");

    [Theory]
    [MemberData(nameof(RefusedDefinitions))]
    public void AnythingButRamMapsClearIsRefused(string why, string xml, string reason)
    {
        string? refused = ClearTaskReplacer.ReadDefinition(xml, "SYSTEM").Refused;
        Assert.True(refused is not null, why);
        Assert.Contains(reason, refused);
    }

    [Theory]
    [InlineData("-Et")]
    [InlineData("-Ew -Es -Em -Et -E0")]
    [InlineData("/accepteula /Et")]
    [InlineData("  -accepteula\t-et ")]
    public void RamMapsClearOptionsAreAccepted(string arguments) => Assert.Null(ClearTaskReplacer.ReadDefinition(TaskXml.Ram(arguments: arguments), "SYSTEM").Refused);

    [Theory]
    [InlineData(@"C:\Program Files\Sysinternals")]
    [InlineData(@"C:\Program Files\Sysinternals\")]
    [InlineData("\"C:\\program files\\Sysinternals\"")]
    public void ATaskMayStartInRamMapsOwnFolder(string folder) => Assert.Null(ClearTaskReplacer.ReadDefinition(StartsIn(TaskXml.AdminRam, folder), "SYSTEM").Refused);

    [Fact]
    public void ADefinitionWithNoAccountOfItsOwnRunsAsTheOneItIsRegisteredWith()
    {
        Assert.Null(ClearTaskReplacer.ReadDefinition(RunsAs(""), @"PC\someone").Refused);
        Assert.Null(ClearTaskReplacer.ReadDefinition(System.Text.RegularExpressions.Regex.Replace(TaskXml.Ram(), "<Principals>.*</Principals>", ""), @"PC\someone").Refused);
        Assert.Null(ClearTaskReplacer.ReadDefinition(TaskXml.Ram(), "SYSTEM").Refused); // S-1-5-18 is SYSTEM
        Assert.Null(ClearTaskReplacer.ReadDefinition(RunsAs("<GroupId>S-1-5-32-545</GroupId>"), null).Refused); // not registered at all: the account note says why
    }

    [Theory]
    [InlineData("S-1-5-18", "SYSTEM", true)]
    [InlineData(@"NT AUTHORITY\SYSTEM", "LocalSystem", true)]
    [InlineData(@"PC\someone", @"pc\SOMEONE", true)]
    [InlineData("S-1-5-18", "S-1-5-32-544", false)] // SYSTEM is not the Administrators group
    [InlineData("S-1-5-21-1-2-3-1001", "S-1-5-21-1-2-3-1002", false)]
    [InlineData("S-1-5-21-1-2-3-1001", "SYSTEM", false)]
    [InlineData("S-1-5-18", "no such account, PoolWatch test", false)] // cannot be looked up: not the same
    public void TwoNamesAreOneAccountOnlyWhenTheyAre(string a, string b, bool same)
    {
        Assert.Equal(same, ClearTaskReplacer.SameAccount(a, b));
        Assert.Equal(same, ClearTaskReplacer.SameAccount(b, a));
    }

    [Fact]
    public void AnAccountByItsNameIsTheSameAccountByItsSid()
    {
        using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
        Assert.True(ClearTaskReplacer.SameAccount(me.Name, me.User!.Value));
        Assert.False(ClearTaskReplacer.SameAccount(me.Name, "S-1-5-18"));
    }

    [Theory]
    [InlineData(5, "S-1-5-18", 5, "SYSTEM")]
    [InlineData(3, "SYSTEM", 5, "SYSTEM")]      // SYSTEM is always the service account
    [InlineData(3, @"PC\someone", 3, @"PC\someone")]
    [InlineData(2, @"PC\someone", 2, @"PC\someone")]
    public void HowATaskIsRegisteredAgain(int logonType, string user, int asType, string asUser) =>
        Assert.Equal<(int, string?)?>((asType, asUser), ClearTaskReplacer.RegisterAs(logonType, user));

    [Theory]
    [InlineData(1)] // password
    [InlineData(6)] // interactive token or password
    [InlineData(4)] // group
    [InlineData(0)]
    public void ATaskThatNeedsAPasswordIsNotRegistered(int logonType) => Assert.Null(ClearTaskReplacer.RegisterAs(logonType, @"PC\someone"));

    [Theory]
    [InlineData(2, null)] // S4U
    [InlineData(3, null)] // interactive token
    [InlineData(5, null)] // service account
    [InlineData(3, "  ")] // a blank account is none
    [InlineData(2, "")]
    public void ATaskWithNoAccountIsNotRegistered(int logonType, string? user) => Assert.Null(ClearTaskReplacer.RegisterAs(logonType, user));

    [Theory]
    [InlineData("Clear standby", true)]
    [InlineData("", false)]
    [InlineData(@"Tools\Clear standby", false)]
    [InlineData("Tools/Clear standby", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    public void OnlyATaskNameForTheRootFolderIsRegistered(string name, bool root) => Assert.Equal(root, CacheHelper.IsRootTaskName(name));

    [Theory]
    [InlineData("Tools/Clear standby", "SYSTEM", "root folder")]
    [InlineData(@"Tools\Clear standby", "SYSTEM", "root folder")]
    [InlineData("Clear standby", " ", "the account it ran as")]
    public void TheRegistrationRefusesANameOrAnAccountBeforeAskingTaskScheduler(string name, string user, string why)
    {
        // Never near the real Task Scheduler from a test: elevated, a guard that was gone would reach it (with a
        // definition it refuses, so nothing would be registered even then). Unelevated, a gone guard says "administrator".
        if (CacheHelper.IsElevated) return;
        Assert.Contains(why, CacheHelper.RegisterOwnTask(name, "<x/>", ClearTaskReplacer.LogonServiceAccount, user));
    }

    // ---------------------------------------------------------------- putting it back (Undo)

    /// <summary>Undo as the window runs it: with <paramref name="confirmed"/>, your yes to the warning the record's row shows now.</summary>
    private UndoResult PutBack(ChangeRecord r, bool confirmed = false, LiveSettings? live = null) =>
        ClearTaskReplacer.PutBack(r, confirmed, confirmed ? ClearTaskReplacer.Row(r, _f.Records, _f.ClearTasks).Warning : null, _f.ClearTasks, _f.Records, live ?? _f.Live());

    [Fact]
    public void ItIsPutBackAfterTheAutomaticClearIsOff()
    {
        var live = _f.Live(new Settings { AutoClearEnabled = true });
        bool? onWhenRegistered = null, savedOnWhenRegistered = null;
        _f.ClearTasks.OnRegister = () => { onWhenRegistered = live.Running.AutoClearEnabled; savedOnWhenRegistered = _f.Saved().AutoClearEnabled; };
        var record = _f.Records.PutRemovedTask(Own(), "the setup guide").Record;

        var result = PutBack(record, live: live);

        Assert.True(result.Done, result.Message);
        Assert.Equal((false, false), (onWhenRegistered, savedOnWhenRegistered)); // the two never both run
        Assert.Equal([@"register Clear standby as 5 SYSTEM"], _f.ClearTasks.Calls);
        Assert.Contains("automatic cache clearing is off now", result.Message);
        Assert.Empty(_f.Records.All());
    }

    [Fact]
    public void AFailedRegistrationTurnsTheAutomaticClearBackOn()
    {
        var live = _f.Live(new Settings { AutoClearEnabled = true });
        _f.ClearTasks.RegisterError = "the task XML is malformed";
        var record = _f.Records.PutRemovedTask(Own(), "the setup guide").Record;
        var result = PutBack(record, live: live);
        Assert.False(result.Done);
        Assert.Contains("the task XML is malformed", result.Message);
        Assert.True(live.Running.AutoClearEnabled);
        Assert.True(_f.Saved().AutoClearEnabled);
        Assert.Single(_f.Records.All());
    }

    [Fact]
    public void ANameThatExistsNowIsRefused()
    {
        var record = _f.Records.PutRemovedTask(Own(), "the setup guide").Record;
        _f.ClearTasks.Existing.Add("Clear standby");
        var result = PutBack(record);
        Assert.False(result.Done);
        Assert.Contains("already exists", result.Message);
        // The task there may be your own original: no advice to delete it, and the definition is saved to compare or import.
        Assert.DoesNotContain("delete", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(_f.Records.ImportFile(record), result.Message);
        Assert.Equal(record.TaskXml, File.ReadAllText(_f.Records.ImportFile(record)));
        Assert.Empty(_f.ClearTasks.Calls);
        Assert.Single(_f.Records.All());
    }

    [Fact]
    public void UndoRemovesOnlyTheRecordItPutBack()
    {
        var record = _f.Records.PutRemovedTask(Own(), "the setup guide").Record;
        // Another removal of that name saves its own record while this one is being put back.
        var theirs = record with { ChangedUtc = record.ChangedUtc.AddSeconds(1), Via = "the command line" };
        _f.ClearTasks.OnRegister = () => _f.Records.Put(theirs);

        Assert.True(PutBack(record).Done);
        Assert.Equal([theirs], _f.Records.All());
    }

    [Fact]
    public void ATaskOutsideTheRootFolderIsRefused()
    {
        // Never through Put (it is invalid there too): a record as a tampered file would hold it.
        Assert.False(PutBack(ChangeRecordFixtures.ClearTask(@"\Tools\Clear standby")).Done);
        Assert.False(PutBack(ChangeRecordFixtures.Task() with { TaskXml = TaskXml.Ram() }).Done); // not a clear task at all
        Assert.Empty(_f.ClearTasks.Calls);
    }

    [Theory]
    [InlineData("C:\\Windows\\System32\\cmd.exe", "/c whoami")]
    [InlineData(TaskXml.AdminRam, "-accepteula")]
    public void ATamperedDefinitionIsRefusedAndSavedForImport(string program, string arguments)
    {
        var record = ChangeRecordFixtures.ClearTask(xml: TaskXml.Ram(program, arguments));
        var result = PutBack(record);
        Assert.False(result.Done);
        Assert.Contains("RAMMap's memory clear", result.Message);
        Assert.Empty(_f.ClearTasks.Calls);
        Assert.True(File.Exists(_f.Records.ImportFile(record)));
    }

    [Fact]
    public void ATaskSavedWithAPasswordCannotBeUndoneAndIsSavedForImport()
    {
        var record = _f.Records.PutRemovedTask(Own(logonType: 1, user: @"PC\someone"), "the setup guide").Record;
        string file = _f.Records.ImportFile(record);
        Assert.True(File.Exists(file)); // saved when it was recorded, as its row says
        Assert.Equal(record.TaskXml, File.ReadAllText(file));

        var row = Assert.Single(_f.Recorded(_f.Live()).List());
        Assert.False(row.CanUndo);
        Assert.Contains(file, row.Note);
        Assert.Contains("password", row.Note);
        Assert.False(PutBack(record).Done);
        Assert.Empty(_f.ClearTasks.Calls);
    }

    [Fact]
    public void ATaskWithItsOwnAccessRulesIsNeverRegisteredAndIsSavedForImport()
    {
        var record = _f.Records.PutRemovedTask(Own(xml: WithAccessRules), "the setup guide").Record;
        var row = Assert.Single(_f.Recorded(_f.Live()).List());
        Assert.False(row.CanUndo);
        Assert.Contains("its own access rules", row.Note);
        Assert.True(File.Exists(_f.Records.ImportFile(record)));

        var result = PutBack(record, confirmed: true);
        Assert.False(result.Done);
        Assert.Contains("its own access rules", result.Message);
        Assert.Empty(_f.ClearTasks.Calls);
    }

    [Fact]
    public void ATaskForAGroupCannotBeUndoneAndNeedsNoPassword()
    {
        var record = _f.Records.PutRemovedTask(Own(logonType: 4, user: null), "the setup guide").Record;
        var row = Assert.Single(_f.Recorded(_f.Live()).List());
        Assert.False(row.CanUndo);
        Assert.Contains("cannot recreate", row.Note);
        Assert.DoesNotContain("password", row.Note);
        Assert.Contains(_f.Records.ImportFile(record), row.Note);
        Assert.True(File.Exists(_f.Records.ImportFile(record)));
    }

    [Fact]
    public void TheImportFileIsWrittenOnceAndGoesWithItsRecord()
    {
        var record = _f.Records.PutRemovedTask(Own(logonType: 1, user: @"PC\someone"), "the setup guide").Record;
        string file = _f.Records.ImportFile(record);
        var written = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, written);
        Assert.Equal(file, _f.Records.SaveForImport(record)); // the same definition: left as it is
        Assert.Equal(written, File.GetLastWriteTimeUtc(file));
        Assert.False(File.Exists(file + ".tmp"));

        _f.Records.Remove(record);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void RamMapInAFolderYouCanChangeNeedsYourYes()
    {
        var record = _f.Records.PutRemovedTask(Own(xml: TaskXml.Ram(@"C:\Tools\RAMMap64.exe")), "the setup guide").Record;
        var row = Assert.Single(_f.Recorded(_f.Live()).List());
        Assert.True(row.CanUndo);
        Assert.Contains(@"C:\Tools", row.Warning);
        Assert.Contains("as SYSTEM", row.Warning);

        var refused = PutBack(record, confirmed: false); // the command line, and so the uninstaller
        Assert.False(refused.Done);
        Assert.Empty(_f.ClearTasks.Calls);
        Assert.True(File.Exists(_f.Records.ImportFile(record)));
        Assert.Contains(_f.Records.ImportFile(record), refused.Message);

        Assert.True(PutBack(record, confirmed: true).Done);
        Assert.Single(_f.ClearTasks.Calls);
    }

    [Fact]
    public void RamMapInProgramFilesNeedsNoWarning()
    {
        _f.Records.PutRemovedTask(Own(), "the setup guide");
        var row = Assert.Single(_f.Recorded(_f.Live()).List());
        Assert.True(row.CanUndo);
        Assert.Null(row.Warning);
        Assert.Equal("Replaced your own clear task \"Clear standby\" (it ran RAMMap)", row.What);
        Assert.False(File.Exists(_f.Records.ImportFile(_f.Records.All()[0]))); // it can be put back: nothing saved for import
        // Judged by the access rules too, from RAMMap's own folder up to Program Files, then RAMMap's own (made in memory here).
        Assert.Equal([@"C:\Program Files\Sysinternals", @"C:\Program Files", TaskXml.AdminRam], _f.ClearTasks.RulesRead.Distinct());
    }

    private const string VendorRam = @"C:\Program Files\Vendor\RAMMap64.exe";

    [Theory]
    [InlineData(@"C:\Program Files\Vendor", "(A;OICI;0x1301bf;;;BU)", "a folder you can change")] // Users may change what is in it
    [InlineData(@"C:\Program Files\Vendor", "(A;;0x2;;;" + Sddl.SomeoneSid + ")", "a folder you can change")] // the signed-in account may add a file
    [InlineData(@"C:\Program Files\Vendor", "(A;OIIO;FA;;;BU)", "a folder you can change")] // Users may change the files made in it, RAMMap among them
    [InlineData(VendorRam, "(A;ID;FA;;;" + Sddl.SomeoneSid + ")", "a file you can change")] // moved there, it kept the account's full control
    [InlineData(@"C:\Program Files\Vendor", Sddl.Unreadable, "PoolWatch could not read")]
    [InlineData(VendorRam, Sddl.Unreadable, "PoolWatch could not read")]
    [InlineData(VendorRam, Sddl.NotThere, "which is not there now")] // a file put there later may take rules not judged
    [InlineData(VendorRam, Sddl.Link, "a link to a file elsewhere")] // the folders of the file it points at are not judged
    public void RamMapInProgramFilesThatAStandardUserMayChangeNeedsYourYesToo(string path, string rules, string why)
    {
        _f.ClearTasks.AccessRules[path] = rules is Sddl.Unreadable or Sddl.NotThere or Sddl.Link ? rules : Sddl.AdminOnlyAnd(rules);
        var record = _f.Records.PutRemovedTask(Own(xml: TaskXml.Ram(VendorRam)), "the setup guide").Record;
        var row = Assert.Single(_f.Recorded(_f.Live()).List());
        Assert.True(row.CanUndo);
        Assert.Contains(why, row.Note);
        Assert.Contains(why, row.Warning);
        Assert.EndsWith(", as SYSTEM. Anything that replaces that file would run as SYSTEM.", row.Warning);

        var refused = PutBack(record, confirmed: false); // the command line, and so the uninstaller
        Assert.False(refused.Done);
        Assert.Contains(why, refused.Message);
        Assert.Empty(_f.ClearTasks.Calls);
        Assert.True(PutBack(record, confirmed: true).Done);
    }

    [Fact]
    public void TheWarningSaysWhatAStandardUserCouldChangeAndWhy()
    {
        var record = _f.Records.PutRemovedTask(Own(xml: TaskXml.Ram(VendorRam)), "the setup guide").Record;
        string? Warning(string path, string rules)
        {
            _f.ClearTasks.AccessRules.Clear();
            _f.ClearTasks.AccessRules[path] = rules;
            return ClearTaskReplacer.Row(record, _f.Records, _f.ClearTasks).Warning;
        }
        Assert.Equal(@"This task runs RAMMap from C:\Program Files\Vendor, a folder you can change without administrator rights, as SYSTEM. " +
                     "Anything that replaces that file would run as SYSTEM.", Warning(@"C:\Program Files\Vendor", Sddl.AdminOnlyAnd("(A;OICI;0x1301bf;;;BU)")));
        Assert.Equal($"This task runs {VendorRam}, a file you can change without administrator rights, as SYSTEM. " +
                     "Anything that replaces that file would run as SYSTEM.", Warning(VendorRam, Sddl.AdminOnlyAnd("(A;;FA;;;BU)")));
        string unreadable = $"This task runs {VendorRam}, whose permissions, or those of a folder above it, PoolWatch could not read (so you may be " +
                            "able to change it without administrator rights), as SYSTEM. Anything that replaces that file would run as SYSTEM.";
        Assert.Equal(unreadable, Warning(VendorRam, Sddl.Unreadable));
        Assert.Equal(unreadable, Warning(@"C:\Program Files\Vendor", Sddl.Unreadable));
        Assert.Equal($"This task runs {VendorRam}, which is not there now (so PoolWatch cannot check the permissions a file put there would get), " +
                     "as SYSTEM. Anything that replaces that file would run as SYSTEM.", Warning(VendorRam, Sddl.NotThere));
        Assert.Equal($"This task runs {VendorRam}, a link to a file elsewhere (so PoolWatch cannot check the folders that file is in), as SYSTEM. " +
                     "Anything that replaces that file would run as SYSTEM.", Warning(VendorRam, Sddl.Link));
        Assert.Null(Warning(VendorRam, Sddl.Installed));
    }

    [Fact]
    public void YourYesDoesNotCoverPermissionsThatChangedSince()
    {
        // Your yes was to "a folder you can change"; since then RAMMap's own rules could no longer be read. The record is
        // the same, the warning is not, so nothing is put back until you look again.
        _f.ClearTasks.AccessRules[@"C:\Program Files\Vendor"] = Sddl.AdminOnlyAnd("(A;OICI;0x1301bf;;;BU)");
        var record = _f.Records.PutRemovedTask(Own(xml: TaskXml.Ram(VendorRam)), "the setup guide").Record;
        string? yes = ClearTaskReplacer.Row(record, _f.Records, _f.ClearTasks).Warning;
        _f.ClearTasks.AccessRules.Clear();
        _f.ClearTasks.AccessRules[VendorRam] = Sddl.Unreadable;

        var refused = ClearTaskReplacer.PutBack(record, confirmed: true, yes, _f.ClearTasks, _f.Records, _f.Live());
        Assert.False(refused.Done);
        Assert.Equal("Your clear task \"Clear standby\", or the permissions of what it runs, changed since you said yes, so PoolWatch did not " +
                     "put it back. Look at the list again: it shows the task as it is now.", refused.Message);
        Assert.Empty(_f.ClearTasks.Calls);
    }

    [Fact]
    public void YourYesIsCheckedOnTheRecordThatIsRegistered()
    {
        // Your yes was to the warning for RAMMap in C:\Tools; the record PutBack is given runs it from C:\Other (a read of
        // the list made after another PoolWatch saved it, say). PutBack checks the record it would register, so nothing runs.
        var tools = _f.Records.PutRemovedTask(Own(xml: TaskXml.Ram(@"C:\Tools\RAMMap64.exe")), "the setup guide").Record;
        string? yes = ClearTaskReplacer.Row(tools, _f.Records, _f.ClearTasks).Warning;
        Assert.Contains(@"C:\Tools", yes);
        var other = tools with { TaskXml = TaskXml.Ram(@"C:\Other\RAMMap64.exe") };

        var refused = ClearTaskReplacer.PutBack(other, confirmed: true, yes, _f.ClearTasks, _f.Records, _f.Live());
        Assert.False(refused.Done);
        Assert.Contains("changed since you said yes", refused.Message);
        Assert.Contains("Look at the list again: it shows the task as it is now.", refused.Message);
        Assert.Empty(_f.ClearTasks.Looked);
        Assert.Empty(_f.ClearTasks.Calls);
        // A yes to no warning does not cover a record that has one either.
        Assert.False(ClearTaskReplacer.PutBack(tools, confirmed: true, warningYouSaw: null, _f.ClearTasks, _f.Records, _f.Live()).Done);

        Assert.True(ClearTaskReplacer.PutBack(tools, confirmed: true, yes, _f.ClearTasks, _f.Records, _f.Live()).Done);
        Assert.Equal([TaskXml.Ram(@"C:\Tools\RAMMap64.exe")], _f.ClearTasks.RegisteredXml);
    }

    [Fact]
    public void ATaskWithNoAccountIsNeverRegisteredAsWhoeverPutsItBack()
    {
        var record = _f.Records.PutRemovedTask(Own(logonType: 3, user: null), "the setup guide").Record;
        Assert.True(File.Exists(_f.Records.ImportFile(record))); // it cannot be put back, so it was saved for import

        var result = PutBack(record, confirmed: true);
        Assert.False(result.Done);
        Assert.Contains("cannot recreate", result.Message);
        Assert.Empty(_f.ClearTasks.Calls);
    }

    [Fact]
    public void YourYesHoldsOnlyForTheWarningYouSaw()
    {
        _f.Records.PutRemovedTask(Own(xml: TaskXml.Ram(@"C:\Tools\RAMMap64.exe")), "the setup guide");
        var source = _f.Recorded(_f.Live());
        var shown = Assert.Single(source.List());
        Assert.Contains(@"C:\Tools", shown.Warning);
        // Another removal of that name is saved after the list was read; its RAMMap is in another folder.
        _f.Records.PutRemovedTask(Own(xml: TaskXml.Ram(@"C:\Other\RAMMap64.exe")), "the command line");

        var refused = source.Undo(shown, confirmed: true);
        Assert.False(refused.Done);
        Assert.Contains("changed since you said yes", refused.Message); // why, and what to do: not a manual import
        Assert.Empty(_f.ClearTasks.Calls);
        Assert.Empty(_f.ClearTasks.Looked); // nothing is tried
        Assert.False(File.Exists(_f.Records.ImportFile(ChangeRecordFixtures.ClearTask()))); // and no import file you did not ask for

        var now = Assert.Single(source.List()); // asked again, with its own warning
        Assert.Contains(@"C:\Other", now.Warning);
        Assert.True(source.Undo(now, confirmed: true).Done);
        Assert.Equal([TaskXml.Ram(@"C:\Other\RAMMap64.exe")], _f.ClearTasks.RegisteredXml);
    }

    [Fact]
    public void TheDefinitionCheckedIsTheOneRegisteredNotTheOneListed()
    {
        var record = _f.Records.PutRemovedTask(Own(), "the setup guide").Record;
        var source = _f.Recorded(_f.Live());
        var row = Assert.Single(source.List());
        Assert.True(row.CanUndo);
        // The saved definition is changed after the list was read.
        _f.Records.Put(record with { TaskXml = TaskXml.Ram(@"C:\Windows\System32\cmd.exe", "/c RAMMap64.exe -Et") });

        var result = source.Undo(row, confirmed: true);
        Assert.False(result.Done);
        Assert.Contains("RAMMap's memory clear", result.Message);
        Assert.Empty(_f.ClearTasks.Calls);

        _f.Records.Put(record);
        Assert.True(source.Undo(row, confirmed: false).Done);
        Assert.Equal([record.TaskXml!], _f.ClearTasks.RegisteredXml); // exactly the definition checked
    }

    [Fact]
    public void TheUninstallerNeverAcceptsAWarning()
    {
        var record = _f.Records.PutRemovedTask(Own(xml: TaskXml.Ram(@"C:\Tools\RAMMap64.exe")), "the setup guide").Record;
        var ledger = new ChangeLedger([_f.Recorded(_f.Live())], _f.Records, _f.Log);

        var (change, result) = Assert.Single(Program.UninstallUndo(ledger, Program.UndoSelection(keepChanges: false)));
        Assert.Equal(ChangeKind.RemovedClearTask, change.Kind);
        Assert.False(result.Done); // so the uninstall exits 8 and keeps the data folder
        Assert.Contains(_f.Records.ImportFile(record), result.Message);
        Assert.Empty(_f.ClearTasks.Calls);
        Assert.Single(_f.Records.All());
    }

    [Fact]
    public void YourYesCoversEveryFolderTheTaskRunsFrom()
    {
        _f.Records.PutRemovedTask(Own(xml: TaskXml.Ram(@"C:\Tools\RAMMap64.exe")), "the setup guide");
        var source = _f.Recorded(_f.Live());
        var shown = Assert.Single(source.List());
        // Saved again with a second RAMMap in another folder you can change: the first folder's warning would read the same.
        _f.Records.PutRemovedTask(Own(xml: TaskXml.Of(
            @"<Exec><Command>C:\Tools\RAMMap64.exe</Command><Arguments>-Et</Arguments></Exec><Exec><Command>C:\Other\RAMMap64.exe</Command><Arguments>-Ew</Arguments></Exec>")),
            "the command line");

        Assert.False(source.Undo(shown, confirmed: true).Done);
        Assert.Empty(_f.ClearTasks.Calls);
        var now = Assert.Single(source.List());
        Assert.False(now.CanUndo);
        Assert.Contains("more than one folder", now.Note);
    }

    [Fact]
    public void YourYesCoversEveryProgramTheTaskRuns()
    {
        // Two RAMMaps in one folder. Your yes is to the first one's own rules; then the second one is no longer there.
        const string other = @"C:\Program Files\Vendor\RAMMap.exe";
        var record = _f.Records.PutRemovedTask(Own(xml: TaskXml.Of(
            $"<Exec><Command>{VendorRam}</Command><Arguments>-Et</Arguments></Exec><Exec><Command>{other}</Command><Arguments>-Ew</Arguments></Exec>")),
            "the setup guide").Record;
        _f.ClearTasks.AccessRules[VendorRam] = Sddl.AdminOnlyAnd("(A;;FA;;;BU)");
        string? yes = ClearTaskReplacer.Row(record, _f.Records, _f.ClearTasks).Warning;
        Assert.Equal($"This task runs {VendorRam}, a file you can change without administrator rights, as SYSTEM. " +
                     "Anything that replaces that file would run as SYSTEM.", yes);

        _f.ClearTasks.AccessRules[other] = Sddl.NotThere;
        var row = ClearTaskReplacer.Row(record, _f.Records, _f.ClearTasks);
        Assert.Equal($"This task runs {VendorRam}, a file you can change without administrator rights, and {other}, which is not there now (so " +
                     "PoolWatch cannot check the permissions a file put there would get), as SYSTEM. Anything that replaces those files would run as SYSTEM.",
            row.Warning);
        Assert.Contains($"and {other}, which is not there now", row.Note);
        Assert.False(ClearTaskReplacer.PutBack(record, confirmed: true, yes, _f.ClearTasks, _f.Records, _f.Live()).Done); // a yes to the first alone
        Assert.Contains($"and {other}, which is not there now", PutBack(record, confirmed: false).Message);
        Assert.Empty(_f.ClearTasks.Calls);

        // A folder both run from is named once, and its two programs are still "those files".
        _f.ClearTasks.AccessRules.Clear();
        _f.ClearTasks.AccessRules[@"C:\Program Files\Vendor"] = Sddl.AdminOnlyAnd("(A;OICI;0x1301bf;;;BU)");
        Assert.Equal(@"This task runs RAMMap from C:\Program Files\Vendor, a folder you can change without administrator rights, as SYSTEM. " +
                     "Anything that replaces those files would run as SYSTEM.", ClearTaskReplacer.Row(record, _f.Records, _f.ClearTasks).Warning);

        _f.ClearTasks.AccessRules[other] = Sddl.NotThere;
        Assert.True(ClearTaskReplacer.PutBack(record, confirmed: true, ClearTaskReplacer.Row(record, _f.Records, _f.ClearTasks).Warning,
            _f.ClearTasks, _f.Records, _f.Live()).Done);
    }

    [Fact]
    public void OneRamMapRunTwiceIsOneFile()
    {
        // The usual clear task runs one RAMMap twice, with two options: that is one file to replace, named once.
        var record = _f.Records.PutRemovedTask(Own(xml: TaskXml.Of(
            $"<Exec><Command>{VendorRam}</Command><Arguments>-Et</Arguments></Exec><Exec><Command>{VendorRam}</Command><Arguments>-Ew</Arguments></Exec>")),
            "the setup guide").Record;
        _f.ClearTasks.AccessRules[VendorRam] = Sddl.AdminOnlyAnd("(A;;FA;;;BU)");
        Assert.Equal($"This task runs {VendorRam}, a file you can change without administrator rights, as SYSTEM. " +
                     "Anything that replaces that file would run as SYSTEM.", ClearTaskReplacer.Row(record, _f.Records, _f.ClearTasks).Warning);
    }

    [Fact]
    public void OneRamMapNamedInTwoCasesIsOneFile()
    {
        // Windows paths ignore case: the same RAMMap written two ways is one file to replace (each text Task Scheduler is
        // given is still named, so a yes covers both).
        string upper = VendorRam.ToUpperInvariant();
        var record = _f.Records.PutRemovedTask(Own(xml: TaskXml.Of(
            $"<Exec><Command>{VendorRam}</Command><Arguments>-Et</Arguments></Exec><Exec><Command>{upper}</Command><Arguments>-Ew</Arguments></Exec>")),
            "the setup guide").Record;
        _f.ClearTasks.AccessRules[VendorRam] = Sddl.AdminOnlyAnd("(A;;FA;;;BU)");
        Assert.Equal($"This task runs {VendorRam}, a file you can change without administrator rights, and {upper}, a file you can change " +
                     "without administrator rights, as SYSTEM. Anything that replaces that file would run as SYSTEM.",
            ClearTaskReplacer.Row(record, _f.Records, _f.ClearTasks).Warning);
    }

    [Fact]
    public void ANameWithAForwardSlashIsRefusedBeforeTaskSchedulerOrTheAutomaticClear()
    {
        // Never through Put (it is invalid there too): a record as a tampered file would hold it.
        var live = _f.Live(new Settings { AutoClearEnabled = true });
        var result = PutBack(ChangeRecordFixtures.ClearTask(@"\Tools/Clear standby"), confirmed: true, live: live);

        Assert.False(result.Done);
        Assert.Contains("not valid", result.Message);
        Assert.Empty(_f.ClearTasks.Looked); // not even asked whether it exists
        Assert.Empty(_f.ClearTasks.Calls);
        Assert.True(live.Running.AutoClearEnabled);
        Assert.False(File.Exists(_f.Paths.SettingsFile)); // never turned off and on again
    }

    [Fact]
    public void ADefinitionThatRunsAsAnotherAccountIsNeverRegistered()
    {
        // Recorded as SYSTEM, with a definition whose own principal is someone else.
        var record = _f.Records.PutRemovedTask(Own(xml: RunsAs("<UserId>S-1-5-21-1-2-3-1001</UserId>")), "the setup guide").Record;
        Assert.True(File.Exists(_f.Records.ImportFile(record))); // it cannot be put back, so it was saved for import
        var row = Assert.Single(_f.Recorded(_f.Live()).List());
        Assert.False(row.CanUndo);
        Assert.Contains("not the one it ran as", row.Note);

        var result = PutBack(record, confirmed: true);
        Assert.False(result.Done);
        Assert.Contains("not the one it ran as", result.Message);
        Assert.Empty(_f.ClearTasks.Calls);
    }

    [Fact]
    public void ARowThatCannotBeUndoneHasTheImportFileItNames()
    {
        // Saved before a check that now refuses it, so no import file was written when it was recorded.
        var record = ChangeRecordFixtures.ClearTask(xml: TaskXml.Ram(@"C:\Program Files\Sysinternals\..\..\Users\Public\RAMMap64.exe"));
        _f.Records.Put(record);
        Assert.False(File.Exists(_f.Records.ImportFile(record)));

        var row = Assert.Single(_f.Recorded(_f.Live()).List());
        Assert.False(row.CanUndo);
        Assert.Contains(_f.Records.ImportFile(record), row.Note);
        Assert.Equal(record.TaskXml, File.ReadAllText(_f.Records.ImportFile(record)));
    }

    // ---------------------------------------------------------------- replacing (the setup guide)

    private string ListFile => Path.Combine(_f.Paths.DataRoot, "applied-changes.json");

    /// <summary>A definition too large to record.</summary>
    private static string TooLarge => TaskXml.Ram().Replace("<Principals>", "<!--" + new string('x', ChangeRecordFile.MaxTaskXmlChars) + "--><Principals>");

    [Fact]
    public void EveryDefinitionIsSavedBeforeAnyTaskIsRemovedThenTheHourlyClearGoesOn()
    {
        var live = _f.Live(new Settings { AutoClearEnabled = false, AutoClearEveryHours = 3 });
        _f.ClearTasks.Tasks.AddRange([Own(), Own(@"\Clear modified")]);
        var savedAtDelete = new List<int>();
        _f.ClearTasks.OnDelete = _ => savedAtDelete.Add(new ChangeRecords(_f.Paths, _f.Log).All().Count); // read as a restart would

        var (removed, error) = ClearTaskReplacer.Replace(_f.Records, live, "the setup guide", _f.ClearTasks);

        Assert.Null(error);
        Assert.Equal([2, 2], savedAtDelete); // both, before the first delete
        Assert.Equal([@"\Clear standby", @"\Clear modified"], removed.Select(t => t.Path));
        Assert.Equal(2, _f.Records.All().Count(r => r.Kind == RecordKind.RemovedClearTask && r.Via == "the setup guide"));
        Assert.Equal((true, 1.0), (live.Running.AutoClearEnabled, live.Running.AutoClearEveryHours));
        Assert.Equal((true, 1.0), (_f.Saved().AutoClearEnabled, _f.Saved().AutoClearEveryHours));
    }

    [Fact]
    public void WithNoClearTaskTheHourlyClearGoesOnEvenWhenTheListCannotBeRead()
    {
        File.WriteAllText(ListFile, "{\"version\": 1, \"changes\": ["); // damaged
        var live = _f.Live();
        var (removed, error) = ClearTaskReplacer.Replace(_f.Records, live, "the setup guide", _f.ClearTasks);
        Assert.Null(error);
        Assert.Empty(removed);
        Assert.True(live.Running.AutoClearEnabled);
    }

    [Fact]
    public void ADefinitionThatCannotBeReadLeavesEveryTaskInPlace()
    {
        var live = _f.Live();
        _f.ClearTasks.Tasks.AddRange([Own(), Own(@"\Unreadable") with { Xml = null }]);
        var (removed, error) = ClearTaskReplacer.Replace(_f.Records, live, "the setup guide", _f.ClearTasks);

        Assert.Equal(CacheHelper.LegacyKeptReason(_f.ClearTasks.Tasks), error);
        Assert.Contains("\"Unreadable\"", error);
        Assert.Empty(removed);
        Assert.Empty(_f.ClearTasks.Calls);
        Assert.Empty(_f.Records.All());
        Assert.False(live.Running.AutoClearEnabled);
    }

    [Fact]
    public void ASaveThatFailsLeavesEveryTaskInPlaceAndTakesBackTheRecordsSaved()
    {
        var live = _f.Live();
        // The first is saved, with its definition for import (a password task); the second cannot be.
        _f.ClearTasks.Tasks.AddRange([Own(logonType: 1, user: @"PC\someone"), Own(@"\Clear modified", xml: TooLarge)]);
        var (removed, error) = ClearTaskReplacer.Replace(_f.Records, live, "the setup guide", _f.ClearTasks);

        Assert.StartsWith("Your clear tasks were left in place because \"Clear modified\" could not be saved for putting back later: ", error);
        Assert.Empty(removed);
        Assert.Empty(_f.ClearTasks.Calls);
        Assert.Empty(_f.Records.All()); // no row for a task that is still there
        Assert.Empty(Directory.GetFiles(Path.Combine(_f.Paths.DataRoot, "removed-tasks")));
        Assert.False(live.Running.AutoClearEnabled);
    }

    [Fact]
    public void ADefinitionThatDoesNotReadBackAsSavedIsNotRemoved()
    {
        // The list cannot hold a lone surrogate as given (it is written as U+FFFD), so this definition would come back changed.
        _f.ClearTasks.Tasks.Add(Own(xml: TaskXml.Ram().Replace("<Principals>", "<!-- \uD800 --><Principals>")));
        var (removed, error) = ClearTaskReplacer.Replace(_f.Records, _f.Live(), "the setup guide", _f.ClearTasks);

        Assert.Equal("Your clear task \"Clear standby\" was left in place because it could not be saved for putting back later: the copy PoolWatch saved did not match the task.", error);
        Assert.Empty(removed);
        Assert.Empty(_f.ClearTasks.Calls);
        Assert.Empty(_f.Records.All());
        Assert.False(File.Exists(_f.Records.ImportFile(ChangeRecordFixtures.ClearTask())));
    }

    [Fact]
    public void AnEarlierRecordOfTheSameNameIsPutBackWhenItsTaskStays()
    {
        var earlier = ChangeRecordFixtures.ClearTask(xml: TaskXml.Ram(@"C:\Old\RAMMap64.exe")); // removed before; a task of that name was made again since
        _f.Records.Put(earlier);
        _f.ClearTasks.Tasks.AddRange([Own(), Own(@"\Clear modified", xml: TooLarge)]);
        var (_, error) = ClearTaskReplacer.Replace(_f.Records, _f.Live(), "the setup guide", _f.ClearTasks);

        Assert.Contains("left in place", error);
        Assert.Empty(_f.ClearTasks.Calls);
        Assert.Equal([earlier], _f.Records.All()); // its definition is not lost
    }

    [Fact]
    public void AnEarlierRecordPutBackKeepsItsImportFile()
    {
        // Removed before, and saved for import (a password task); a password task of that name was made again since.
        var earlier = _f.Records.PutRemovedTask(Own(xml: TaskXml.Ram(@"C:\Old\RAMMap64.exe"), logonType: 1, user: @"PC\someone"), "the setup guide").Record;
        _f.ClearTasks.Tasks.AddRange([Own(logonType: 1, user: @"PC\someone"), Own(@"\Clear modified", xml: TooLarge)]);
        var (_, error) = ClearTaskReplacer.Replace(_f.Records, _f.Live(), "the setup guide", _f.ClearTasks);

        Assert.Contains("left in place", error);
        Assert.Equal([earlier], _f.Records.All());
        Assert.Equal(earlier.TaskXml, File.ReadAllText(_f.Records.ImportFile(earlier))); // not the definition of the task still there
    }

    [Fact]
    public void ADeleteThatFailsLeavesThatTaskInPlaceWithoutARecord()
    {
        var live = _f.Live();
        _f.ClearTasks.Tasks.Add(Own());
        _f.ClearTasks.OnDelete = _ => throw new UnauthorizedAccessException("Access is denied.");
        var (removed, error) = ClearTaskReplacer.Replace(_f.Records, live, "the setup guide", _f.ClearTasks);

        Assert.Equal("Your clear task \"Clear standby\" could not be removed: Access is denied.", error);
        Assert.Empty(removed);
        Assert.Single(_f.ClearTasks.Tasks);
        Assert.Empty(_f.Records.All()); // no "Replaced" row for a task still in Task Scheduler
        Assert.False(live.Running.AutoClearEnabled);
    }

    [Fact]
    public void AFirstDeleteThatFailsNamesEveryTaskLeftInPlace()
    {
        _f.ClearTasks.Tasks.AddRange([Own(), Own(@"\Clear modified")]);
        _f.ClearTasks.OnDelete = _ => throw new UnauthorizedAccessException("Access is denied.");
        var (removed, error) = ClearTaskReplacer.Replace(_f.Records, _f.Live(), "the setup guide", _f.ClearTasks);

        Assert.Equal("Your clear tasks \"Clear standby\" and \"Clear modified\" were left in place because \"Clear standby\" could not be removed: Access is denied.", error);
        Assert.Empty(removed);
        Assert.Equal(2, _f.ClearTasks.Tasks.Count);
        Assert.Empty(_f.Records.All());
    }

    [Fact]
    public void WhenTheAutomaticClearCannotBeTurnedOnReplaceSaysSo()
    {
        var live = _f.Live();
        _f.ClearTasks.Tasks.Add(Own());
        Directory.CreateDirectory(_f.Paths.SettingsFile + ".tmp"); // settings.json cannot be written
        var (removed, error) = ClearTaskReplacer.Replace(_f.Records, live, "the setup guide", _f.ClearTasks);

        Assert.Equal(@"\Clear standby", Assert.Single(removed).Path);
        Assert.StartsWith("Your clear task was removed (Active changes can put it back), but PoolWatch's automatic cache clearing could not be turned on: ", error);
        Assert.Single(_f.Records.All()); // so Undo can put it back
        Assert.False(live.Running.AutoClearEnabled);
    }

    [Fact]
    public void ATaskGoneMeanwhileLeavesNoRecord()
    {
        var gone = Own(logonType: 1, user: @"PC\someone"); // one PoolWatch saves for import as it records it
        _f.ClearTasks.Tasks.Add(gone);
        _f.ClearTasks.OnDelete = t => _f.ClearTasks.Tasks.Remove(t); // removed by someone else just before
        var (removed, error) = ClearTaskReplacer.Replace(_f.Records, _f.Live(), "the setup guide", _f.ClearTasks);
        Assert.Null(error);
        Assert.Empty(removed);
        Assert.Empty(_f.Records.All());
        Assert.Empty(Directory.GetFiles(Path.Combine(_f.Paths.DataRoot, "removed-tasks")));
    }

    [Fact]
    public void ATaskThatCannotBeReadAtItsDeleteKeepsTheHourlyClearOff()
    {
        var live = _f.Live();
        _f.ClearTasks.Tasks.Add(Own());
        _f.ClearTasks.OnDelete = t => _f.ClearTasks.Tasks[0] = t with { Xml = null }; // unreadable just before its delete: left in place
        var (removed, error) = ClearTaskReplacer.Replace(_f.Records, live, "the setup guide", _f.ClearTasks);

        Assert.Equal("Your clear task \"Clear standby\" could not be read when PoolWatch went to remove it, so it was left in place.", error);
        Assert.Empty(removed);
        Assert.Single(_f.ClearTasks.Tasks);
        Assert.Empty(_f.Records.All());
        Assert.False(live.Running.AutoClearEnabled);
    }

    [Fact]
    public void WhenATaskThatWasNotRemovedCannotBeCheckedTheHourlyClearStaysOff()
    {
        var live = _f.Live();
        _f.ClearTasks.Tasks.Add(Own());
        _f.ClearTasks.OnDelete = t =>
        {
            _f.ClearTasks.Tasks[0] = t with { Xml = null };
            _f.ClearTasks.FindError = new UnauthorizedAccessException("Access is denied.");
        };
        var (removed, error) = ClearTaskReplacer.Replace(_f.Records, live, "the setup guide", _f.ClearTasks);

        Assert.Equal("PoolWatch could not check whether your clear task \"Clear standby\" is still there: Access is denied.", error);
        Assert.Empty(removed);
        Assert.False(live.Running.AutoClearEnabled);
    }

    [Fact]
    public void WhenTasksThatWereNotRemovedCannotBeCheckedTheNoteNamesEach()
    {
        _f.ClearTasks.Tasks.AddRange([Own(), Own(@"\Clear modified")]);
        _f.ClearTasks.OnDelete = t =>
        {
            _f.ClearTasks.Tasks[_f.ClearTasks.Tasks.IndexOf(t)] = t with { Xml = null };
            _f.ClearTasks.FindError = new UnauthorizedAccessException("Access is denied.");
        };
        var (_, error) = ClearTaskReplacer.Replace(_f.Records, _f.Live(), "the setup guide", _f.ClearTasks);
        Assert.Equal("PoolWatch could not check whether your clear tasks \"Clear standby\" and \"Clear modified\" are still there: Access is denied.", error);
    }

    [Fact]
    public void ATaskChangedBeforeItsDeleteIsRecordedAsRemoved()
    {
        _f.ClearTasks.Tasks.Add(Own());
        _f.ClearTasks.OnDelete = t => _f.ClearTasks.Tasks[0] = t with { LogonType = 3, UserId = @"PC\someone" }; // edited in Task Scheduler meanwhile
        var (removed, error) = ClearTaskReplacer.Replace(_f.Records, _f.Live(), "the setup guide", _f.ClearTasks);
        Assert.Null(error);
        Assert.Equal(@"PC\someone", Assert.Single(removed).UserId);
        Assert.Equal((3, @"PC\someone"), (Assert.Single(_f.Records.All()).TaskLogonType, _f.Records.All()[0].TaskUserId));
    }

    [Fact]
    public void ATaskWhoseImportFileCannotBeWrittenIsLeftInPlaceAndNotRecorded()
    {
        File.WriteAllText(Path.Combine(_f.Paths.DataRoot, "removed-tasks"), ""); // a file where the folder should be
        var live = _f.Live();
        _f.ClearTasks.Tasks.Add(Own(logonType: 1, user: @"PC\someone"));
        var (removed, error) = ClearTaskReplacer.Replace(_f.Records, live, "the setup guide", _f.ClearTasks);

        Assert.Contains("left in place", error);
        Assert.Empty(removed);
        Assert.Empty(_f.ClearTasks.Calls);
        Assert.Empty(_f.Records.All()); // no row for a replacement that never happened
        Assert.False(live.Running.AutoClearEnabled);
    }

    [Fact]
    public void ARemovalAnotherFinishesMeanwhileKeepsThatRemovalsRecord()
    {
        _f.ClearTasks.Tasks.Add(Own());
        var other = new ChangeRecords(_f.Paths, _f.Log); // another PoolWatch: the command line
        _f.ClearTasks.OnDelete = t =>
        {
            _f.ClearTasks.OnDelete = null;
            Assert.Single(ClearTaskReplacer.RemoveAll([t], other, "the command line", _f.ClearTasks).Removed); // all of it, inside this delete
        };
        var (removed, error) = ClearTaskReplacer.RemoveAll(_f.ClearTasks.Tasks.ToList(), _f.Records, "the tray menu", _f.ClearTasks);

        Assert.Equal((0, (string?)null), (removed.Count, error)); // this one found the task gone
        Assert.Empty(_f.ClearTasks.Tasks);
        Assert.Equal("the command line", Assert.Single(new ChangeRecords(_f.Paths, _f.Log).All()).Via); // so Undo can still put it back
    }

    [Fact]
    public void ATakeBackLeavesTheRecordAnotherRemovalSavedSince()
    {
        var mine = _f.Records.PutRemovedTask(Own(logonType: 1, user: @"PC\someone"), "the tray menu").Record; // saved for import too
        // Another PoolWatch, asked from the same place: only the time of the save tells the two apart.
        var theirs = new ChangeRecords(_f.Paths, _f.Log).PutRemovedTask(Own(logonType: 1, user: @"PC\someone"), "the tray menu").Record;

        _f.Records.Unsave(mine, null);
        Assert.Equal([theirs], new ChangeRecords(_f.Paths, _f.Log).All());
        Assert.True(File.Exists(_f.Records.ImportFile(theirs)));
    }

    [Fact]
    public void ATakeBackNeverPutsAnEarlierRecordOverAnotherRemovals()
    {
        var earlier = ChangeRecordFixtures.ClearTask(xml: TaskXml.Ram(@"C:\Old\RAMMap64.exe"), logonType: 1, user: @"PC\someone"); // removed before
        _f.Records.Put(earlier);
        var (mine, replaced) = _f.Records.PutRemovedTask(Own(logonType: 1, user: @"PC\someone"), "the tray menu");
        Assert.Equal(earlier, replaced); // as the save found it, to put back if this removal stops
        var theirs = new ChangeRecords(_f.Paths, _f.Log).PutRemovedTask(Own(logonType: 1, user: @"PC\someone"), "the command line").Record;

        _f.Records.Unsave(mine, replaced);
        Assert.Equal([theirs], new ChangeRecords(_f.Paths, _f.Log).All());
        Assert.Equal(theirs.TaskXml, File.ReadAllText(_f.Records.ImportFile(theirs))); // its definition for import, not the earlier one
    }

    // ---------------------------------------------------------------- removing after your yes (the tray menu, Settings, the command line)

    private (CacheHelper.LegacyOutcome Outcome, IReadOnlyList<CacheHelper.LegacyTaskInfo> Tasks, string? Note) Resolve(Func<IReadOnlyList<CacheHelper.LegacyTaskInfo>, bool> ask) =>
        CacheHelper.ResolveLegacy(ask, _f.Records, "the tray menu", _f.ClearTasks);

    [Fact]
    public void YourClearTasksAreRemovedOnlyAfterYourYes()
    {
        _f.ClearTasks.Tasks.AddRange([Own(), Own(@"\Clear modified")]);
        var (outcome, tasks, note) = Resolve(_ => false);
        Assert.Equal((CacheHelper.LegacyOutcome.Kept, 2, (string?)null), (outcome, tasks.Count, note)); // you kept them
        Assert.Empty(_f.ClearTasks.Calls);
        Assert.Empty(_f.Records.All());

        _f.ClearTasks.OnDelete = t => _f.ClearTasks.Tasks[_f.ClearTasks.Tasks.IndexOf(t)] = t with { LogonType = 3 }; // edited meanwhile
        (outcome, tasks, note) = Resolve(_ => true);
        Assert.Equal((CacheHelper.LegacyOutcome.Removed, (string?)null), (outcome, note));
        Assert.Equal([3, 3], tasks.Select(t => t.LogonType)); // each as it was removed
        Assert.Equal([3, 3], _f.Records.All().Select(r => r.TaskLogonType)); // and recorded so, for Undo
        Assert.All(_f.Records.All(), r => Assert.Equal("the tray menu", r.Via));
        Assert.Empty(_f.ClearTasks.Tasks);

        Assert.Equal(CacheHelper.LegacyOutcome.None, Resolve(_ => throw new InvalidOperationException("nothing to ask")).Outcome);
    }

    [Fact]
    public void ATaskWhoseDefinitionCannotBeReadIsKeptWithoutAsking()
    {
        _f.ClearTasks.Tasks.AddRange([Own(), Own(@"\Unreadable") with { Xml = null }]);
        bool asked = false;
        var (outcome, tasks, note) = Resolve(_ => asked = true);

        Assert.Equal(CacheHelper.LegacyOutcome.Kept, outcome); // so the automatic clear stays off
        Assert.False(asked);
        Assert.Empty(_f.ClearTasks.Calls);
        Assert.Equal(2, tasks.Count);
        Assert.Contains("\"Unreadable\"", note);
        Assert.Equal(note, CacheHelper.LegacyKeptReason(tasks));
    }

    [Fact]
    public void AYesThatRemovedNothingSaysWhetherOneIsStillThere()
    {
        _f.ClearTasks.Tasks.Add(Own());
        _f.ClearTasks.OnDelete = t => _f.ClearTasks.Tasks[0] = t with { Xml = null }; // unreadable just before its delete: left in place
        var (outcome, tasks, note) = Resolve(_ => true);
        Assert.Equal((CacheHelper.LegacyOutcome.Kept, @"\Clear standby"), (outcome, Assert.Single(tasks).Path));
        Assert.Equal("Your clear task \"Clear standby\" could not be read when PoolWatch went to remove it, so it was left in place.", note);
        Assert.Empty(_f.Records.All()); // no row for a task that is still there

        _f.ClearTasks.Tasks[0] = Own();
        _f.ClearTasks.OnDelete = _ => _f.ClearTasks.Tasks[0] = Own(@"\Clear standby (new)"); // replaced by another meanwhile
        (outcome, tasks, note) = Resolve(_ => true);
        Assert.Equal((CacheHelper.LegacyOutcome.Kept, @"\Clear standby (new)"), (outcome, Assert.Single(tasks).Path));
        Assert.Equal("Another clear task of your own, \"Clear standby (new)\", was set up while PoolWatch was removing yours, so it was left in place.", note);

        _f.ClearTasks.Tasks[0] = Own();
        _f.ClearTasks.OnDelete = _ => _f.ClearTasks.Tasks.Clear(); // removed by someone else meanwhile
        Assert.Equal(CacheHelper.LegacyOutcome.None, Resolve(_ => true).Outcome);
        Assert.Empty(_f.Records.All());
    }

    [Theory]
    [InlineData("{\"version\": 2, \"changes\": []}")] // a newer PoolWatch wrote it
    [InlineData("{\"version\": 1, \"changes\": [")]  // damaged
    public void AListThatCannotBeReadKeepsYourTasksAfterYourYes(string list)
    {
        File.WriteAllText(ListFile, list);
        _f.ClearTasks.Tasks.AddRange([Own(logonType: 1, user: @"PC\someone"), Own(@"\Clear modified")]); // the first is saved for import when recorded
        var (outcome, tasks, note) = Resolve(_ => true);

        Assert.Equal((CacheHelper.LegacyOutcome.Kept, 2), (outcome, tasks.Count)); // so the automatic clear stays off
        Assert.Equal($"Your clear tasks were left in place because they could not be saved for putting back later: PoolWatch cannot read its list of changes, {ListFile}: " +
                     "it is damaged, or a newer PoolWatch wrote it. The file is left as it is: install the newest PoolWatch to read it, or delete it " +
                     "to start a new list (the changes in it can then no longer be put back from PoolWatch).", note);
        Assert.Empty(_f.ClearTasks.Calls);
        Assert.Equal(list, File.ReadAllText(ListFile));
        Assert.False(Directory.Exists(Path.Combine(_f.Paths.DataRoot, "removed-tasks"))); // stopped before any import file was written
    }

    [Fact]
    public void AListThatCannotBeWrittenKeepsYourTaskAfterYourYes()
    {
        Directory.CreateDirectory(ListFile + ".tmp"); // the temporary file cannot be written
        _f.ClearTasks.Tasks.Add(Own());
        var (outcome, tasks, note) = Resolve(_ => true);

        Assert.Equal((CacheHelper.LegacyOutcome.Kept, 1), (outcome, tasks.Count));
        Assert.StartsWith("Your clear task \"Clear standby\" was left in place because it could not be saved for putting back later: ", note);
        Assert.Empty(_f.ClearTasks.Calls);
        Assert.Empty(_f.Records.All());
    }

    [Fact]
    public void ADeleteThatFailsHalfWayKeepsTheRestAndWhatWasRemovedStaysRecorded()
    {
        _f.ClearTasks.Tasks.AddRange([Own(), Own(@"\Clear modified"), Own(@"\Clear working sets")]);
        _f.ClearTasks.OnDelete = t => { if (t.Path == @"\Clear modified") throw new UnauthorizedAccessException("Access is denied."); };
        var (outcome, tasks, note) = Resolve(_ => true);

        Assert.Equal(CacheHelper.LegacyOutcome.Kept, outcome); // one is still there: the automatic clear stays off
        Assert.Equal([@"\Clear modified", @"\Clear working sets"], tasks.Select(t => t.Path));
        Assert.Equal([@"delete \Clear standby", @"delete \Clear modified"], _f.ClearTasks.Calls); // it stops at the failure
        Assert.Equal("Removed \"Clear standby\" (Active changes can put it back), but your clear tasks \"Clear modified\" and " +
                     "\"Clear working sets\" were left in place because \"Clear modified\" could not be removed: Access is denied.", note);
        Assert.Equal([@"\Clear standby"], _f.Records.All().Select(r => r.Name)); // what was removed, and only that
    }

    [Fact]
    public void ATaskThatCannotBeReadAtItsDeleteIsKeptAfterTheOthersAreRemoved()
    {
        _f.ClearTasks.Tasks.AddRange([Own(), Own(@"\Clear modified")]);
        _f.ClearTasks.OnDelete = t => { if (t.Path == @"\Clear modified") _f.ClearTasks.Tasks[_f.ClearTasks.Tasks.IndexOf(t)] = t with { Xml = null }; };
        var (outcome, tasks, note) = Resolve(_ => true);

        Assert.Equal((CacheHelper.LegacyOutcome.Kept, @"\Clear modified"), (outcome, Assert.Single(tasks).Path)); // the automatic clear stays off
        Assert.Equal("Removed \"Clear standby\" (Active changes can put it back), but your clear task \"Clear modified\" could not be " +
                     "read when PoolWatch went to remove it, so it was left in place.", note);
        Assert.Equal([@"\Clear standby"], _f.Records.All().Select(r => r.Name));
    }
}
