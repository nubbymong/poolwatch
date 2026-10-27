using PoolWatch.Core.Filters;
using PoolWatch.Core.Findings;
using PoolWatch.Core.Versions;

namespace PoolWatch.Tests;

public class VersionTests
{
    private static readonly DateTime T0 = new(2026, 10, 26, 7, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("OpenAI.ChatGPT-Desktop_1.2025.112.0_x64__2p2nqsd0c76g0", "ChatGPT", "1.2025.112.0")]
    [InlineData("OpenAI.Codex_26.1021.3705.0_x64__2p2nqsd0c76g0", "Codex", "26.1021.3705.0")]
    [InlineData("Microsoft.WindowsTerminal_1.21.2911.0_x64__8wekyb3d8bbwe", null, null)]
    [InlineData("garbage", null, null)]
    public void PackageVersions(string full, string? name, string? version) =>
        Assert.Equal(name is null ? null : (name, version!), VersionMarkers.FromPackageFullName(full));

    [Fact]
    public void NewestClaudeCodeVersion()
    {
        Assert.Equal("2.0.15", VersionMarkers.NewestClaudeCode(["2.0.9", "2.0.15", "2.0.14", "latest", ".lock"]));
        Assert.Null(VersionMarkers.NewestClaudeCode(["latest"]));
    }

    [Fact]
    public void VersionChangesAcrossRuns()
    {
        var first = VersionMarkers.Diff(null, new Dictionary<string, string> { ["Claude Code"] = "2.0.14", ["ChatGPT"] = "1.2025.1.0" }, T0);
        Assert.Equal("App versions: ChatGPT 1.2025.1.0, Claude Code 2.0.14", Assert.Single(first).Title);
        var state = VersionMarkers.Parse(VersionMarkers.Serialize(new Dictionary<string, string> { ["Claude Code"] = "2.0.14", ["ChatGPT"] = "1.2025.1.0", ["Codex"] = "26.1" }));
        var changes = VersionMarkers.Diff(state, new Dictionary<string, string> { ["Claude Code"] = "2.0.15", ["ChatGPT"] = "1.2025.1.0", ["AI Code Conductor"] = "3.4.0" }, T0);
        Assert.Equal(["Version change: AI Code Conductor 3.4.0 installed", "Version change: Claude Code updated 2.0.14 -> 2.0.15", "Version change: Codex 26.1 no longer found"],
            changes.Select(c => c.Title));
        Assert.All(changes, c => Assert.Equal(FindingKind.VersionChange, c.Kind));
        Assert.Empty(VersionMarkers.Diff(state, state, T0));
    }

    private static LogEvent E(int minutes, string provider, int id, params string[] props) =>
        new(T0.AddMinutes(minutes), "System", provider, id, null, props);

    [Fact]
    public void SystemCrashCombinesBugCheckKernelPowerAndBucket()
    {
        var watched = CrashMarkers.DefaultWatchedApps;
        var records = new[]
        {
            E(0, CrashMarkers.KernelPowerProvider, 41, "314", "17", "0", "0", "0"),
            E(1, CrashMarkers.BugCheckProvider, 1001, "0x0000013a (0x0000000000000011, 0xffffd60c5d202140, 0xffffd60c9ab1d000, 0x0000000000000000)", @"C:\WINDOWS\MEMORY.DMP", "abc-123"),
            E(2, CrashMarkers.WerProvider, 1001, "0x13A_11_nt!RtlpHpHeapHandleError", "0", "BlueScreen", "Not available", "0"),
            E(600, CrashMarkers.KernelPowerProvider, 41, "0", "0"),
        }.Select(e => CrashMarkers.Parse(e, watched)).OfType<CrashRecord>().ToList();
        Assert.Equal(4, records.Count);
        var crashes = CrashMarkers.SystemCrashes(records);
        Assert.Equal(2, crashes.Count);
        Assert.Equal("0x0000013a", crashes[0].Code);
        Assert.Equal("0x13A_11_nt!RtlpHpHeapHandleError", crashes[0].Bucket);
        var f = CrashMarkers.ToFinding(crashes[0]);
        Assert.Equal(FindingSeverity.Alert, f.Severity);
        Assert.StartsWith("System crash (bugcheck 0x0000013a, bucket 0x13A_11_nt!RtlpHpHeapHandleError)", f.Title);
        Assert.Equal(3, f.Details.Count);
        Assert.StartsWith("Unexpected restart", crashes[1].Title);
        Assert.Equal(FindingSeverity.Warning, CrashMarkers.ToFinding(crashes[1]).Severity);
    }

    [Fact]
    public void AppCrashesOnlyForWatchedApps()
    {
        string[] watched = ["ChatGPT", "Leaker"];
        var chat = CrashMarkers.Parse(new LogEvent(T0, "Application", CrashMarkers.AppErrorProvider, 1000,
            null, ["ChatGPT.exe", "1.2025.1.0", "6700", "ntdll.dll", "10.0.26100.1", "6700", "c0000374", "0000000000104f29"]), watched)!;
        Assert.Equal(CrashKind.AppCrash, chat.Kind);
        Assert.Equal("ChatGPT.exe 1.2025.1.0 crashed in ntdll.dll 10.0.26100.1, exception 0xc0000374", chat.Text);
        Assert.Null(CrashMarkers.Parse(new LogEvent(T0, "Application", CrashMarkers.AppErrorProvider, 1000, null, ["notepad.exe", "1"]), watched));
        var rep = CrashMarkers.Parse(new LogEvent(T0, "Application", CrashMarkers.WerProvider, 1001, null, ["123456", "4", "APPCRASH", "Not available", "0", "Leaker.exe"]), watched)!;
        Assert.Equal(CrashKind.AppCrashReport, rep.Kind);
        Assert.Equal("123456", rep.Bucket);
        Assert.Null(CrashMarkers.Parse(new LogEvent(T0, "Application", CrashMarkers.WerProvider, 1001, null, ["1", "4", "APPCRASH", "", "", "notepad.exe"]), watched));
        Assert.Equal(2, CrashMarkers.Queries(T0.AddDays(-1), T0).Count);
        Assert.Contains("timediff(@SystemTime) <= 86400000", CrashMarkers.Queries(T0.AddDays(-1), T0)[0].xpath);
    }
}
