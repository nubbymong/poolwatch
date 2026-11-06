using System.Security.AccessControl;
using System.Security.Principal;
using PoolWatch.Engine;

namespace PoolWatch.AppTests;

/// <summary>The data root's ACL as built in memory (never written by a test).</summary>
public class DataRootAclTests
{
    private static readonly SecurityIdentifier Owner = new("S-1-5-21-1111111111-2222222222-3333333333-1001");

    private static List<FileSystemAccessRule> Rules(DirectorySecurity sec) =>
        sec.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>().ToList();

    [Fact]
    public void AdministratorsAndSystemOnlyPlusTheOwnersRead()
    {
        var sec = Interop.DataRootSecurity(Owner);
        var rules = Rules(sec);
        Assert.True(sec.AreAccessRulesProtected);                                      // nothing inherited from %ProgramData%
        Assert.Equal("S-1-5-32-544", sec.GetOwner(typeof(SecurityIdentifier))!.Value); // owned by Administrators
        Assert.Equal(3, rules.Count);
        Assert.Contains(rules, r => r.IdentityReference.Value == "S-1-5-32-544" && r.FileSystemRights == FileSystemRights.FullControl);
        Assert.Contains(rules, r => r.IdentityReference.Value == "S-1-5-18" && r.FileSystemRights == FileSystemRights.FullControl);
        var read = Assert.Single(rules, r => r.IdentityReference.Value == Owner.Value);
        Assert.Equal(AccessControlType.Allow, read.AccessControlType);
        Assert.Equal(FileSystemRights.ReadAndExecute, read.FileSystemRights & ~FileSystemRights.Synchronize);
        Assert.DoesNotContain(rules, r => r.IdentityReference.Value == "S-1-5-32-545");  // no longer all Users
    }

    [Theory]
    [InlineData("S-1-5-32-545")]                                   // Users
    [InlineData("S-1-5-11")]                                       // Authenticated Users
    [InlineData("S-1-1-0")]                                        // Everyone
    [InlineData("S-1-5-18")]                                       // SYSTEM
    [InlineData("S-1-5-21-1111111111-2222222222-3333333333-513")]  // Domain Users / None
    public void AGroupIsNeverTheReader(string sid)
    {
        var rules = Rules(Interop.DataRootSecurity(new SecurityIdentifier(sid)));
        Assert.Equal(2, rules.Count);
        Assert.All(rules, r => Assert.Contains(r.IdentityReference.Value, new[] { "S-1-5-32-544", "S-1-5-18" }));
    }

    [Fact]
    public void AnEntraIdAccountIsOneAccount() =>
        Assert.True(Interop.IsOneAccount(new SecurityIdentifier("S-1-12-1-1111111111-2222222222-3333333333-4444444444")));

    [Fact]
    public void NoReaderMeansAdministratorsAndSystemOnly() => Assert.Equal(2, Rules(Interop.DataRootSecurity(null)).Count);

    /// <summary>
    /// Why the signed-in account keeps its read: the data folder has unelevated readers, which the grant's summary names
    /// (Interop.DataRootSecurity) and this test holds. Every place PoolWatch shows a folder or a file in Explorer
    /// (<c>UiActions.OpenFolder</c>, <c>UiActions.ShowFile</c>), which runs unelevated as the signed-in account, is listed
    /// here with what it shows, so a new one is placed in or out of the data folder here; those in it, and the user
    /// reading the log, the CSV files and the incidents, read through this grant. So the grant is read and execute,
    /// inherited by every folder and file, for that one account, and a change that drops or narrows it fails here with
    /// the readers it would break.
    /// </summary>
    [Fact]
    public void TheSignedInAccountsReadIsKeptForTheUnelevatedReadersItNames()
    {
        const string readers = "the About window's data folder, an incident's folder, the exports folder after an export or a saved issue text, " +
                               "the reports folder after the since-boot report, the apps opened from them, and the user reading poolwatch.log, the CSV files and the incidents";
        var read = Assert.Single(Rules(Interop.DataRootSecurity(Owner)), r => r.IdentityReference.Value == Owner.Value);
        Assert.True(read.AccessControlType == AccessControlType.Allow && (read.FileSystemRights & FileSystemRights.ReadAndExecute) == FileSystemRights.ReadAndExecute,
            "the signed-in account's read is needed by " + readers);
        Assert.True(read.InheritanceFlags == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit) && read.PropagationFlags == PropagationFlags.None,
            "every folder and file in the data folder inherits the read that " + readers + " need");

        // The summary names each reader.
        string src = Path.Combine(TokenLeakHelperStartTests.RepoDir(), "src");
        string summary = UninstallerTests.OneLine(string.Concat(File.ReadAllLines(Path.Combine(src, "PoolWatch", "Engine", "Interop.cs"))
            .SkipWhile(l => !l.Contains("The data root's security descriptor", StringComparison.Ordinal)).TakeWhile(l => l.TrimStart().StartsWith("///", StringComparison.Ordinal))
            .Select(l => l.TrimStart()[3..] + " ")));
        foreach (string named in new[] { "<c>UiActions.OpenFolder</c>", "<c>UiActions.ShowFile</c>", "Explorer, which shows them, runs unelevated as the signed-in account",
                     "the About window's data folder", "an incident's folder or the incidents folder", "the exports folder after a report or a vendor bundle is exported or an issue text is saved",
                     "the reports folder after the since-boot report", "the apps the user opens those files with from there", "<c>poolwatch.log</c>, the daily CSV files and the incidents",
                     "inherited by every folder and file in it", "<c>DataRootAclTests</c>" })
            Assert.Contains(named, summary);

        // Every place that shows a folder or a file in Explorer, with what it shows: in the data folder or not.
        var shown = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .SelectMany(f => File.ReadAllLines(f).Select(l => l.Trim()).Where(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"(?<![\w.])(UiActions\.)?(OpenFolder|ShowFile)\(") &&
                                                                                  !l.StartsWith("public static void", StringComparison.Ordinal) && !l.StartsWith("///", StringComparison.Ordinal))
                .Select(l => Path.GetFileName(f) + ": " + l))
            .Order(StringComparer.Ordinal).ToList();
        Assert.Equal(
        [
            "AboutDialog.cs: if (File.Exists(notices)) UiActions.ShowFile(notices);",                                                    // the install folder's notices: not the data folder
            "AboutDialog.cs: open.Click += (_, _) => { if (actions is not null) UiActions.OpenFolder(facts.DataRoot); };",                // the data folder
            "ReportIssueDialog.cs: UiActions.ShowFile(file);",                                                                           // a saved issue text, in exports
            "TasksTab.cs: UiActions.ShowFile(exe);",                                                                                     // a task's program: not the data folder
            "UiActions.cs: OpenFolder(Path.GetDirectoryName(zip)!);",                                                                    // an exported report, in exports
            "UiActions.cs: OpenFolder(Path.GetDirectoryName(zip)!);",                                                                    // a vendor bundle, in exports
            "UiActions.cs: OpenFolder(dir is not null && Directory.Exists(dir) ? dir : _engine.Paths.EnsureDir(_engine.Paths.IncidentsDir));", // an incident, or the incidents folder
            "UiActions.cs: else ShowFile(t.Result); // t.Result is the report file; show it in its folder, never open it through an association", // the since-boot report, in reports
        ], shown);
    }
}
