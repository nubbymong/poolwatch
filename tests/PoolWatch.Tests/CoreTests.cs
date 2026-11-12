using PoolWatch.Core;
using PoolWatch.Core.Model;
using PoolWatch.Core.Util;

namespace PoolWatch.Tests;

public class PoolTagTests
{
    [Fact]
    public void TagsAreCaseSensitiveAndRoundTrip()
    {
        var a = PoolTag.Parse("Reff");
        var b = PoolTag.Parse("RefF");
        Assert.NotEqual(a, b);
        Assert.Equal("Reff", a.ToString());
        Assert.Equal(a, PoolTag.FromBytes("Reff"u8));
        Assert.Equal(0x66666552u, a.Raw);
        Assert.NotEqual(a.ToFileSafe(), b.ToFileSafe());
    }

    [Fact]
    public void ShortTagsArePaddedWithSpaces()
    {
        Assert.Equal("Key ", PoolTag.Parse("Key").ToString());
        Assert.Equal("Key", PoolTag.Parse("Key").ToDisplay());
        Assert.Equal(PoolTag.Parse("Key "), PoolTag.Parse("Key"));
    }

    [Fact]
    public void NonPrintableBytesDisplayAsDots()
    {
        var t = PoolTag.FromBytes(new byte[] { 0x41, 0x00, 0xFF, 0x42 });
        Assert.Equal("A..B", t.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("TooLong")]
    public void InvalidTagsAreRejected(string text)
    {
        Assert.False(PoolTag.TryParse(text, out _));
        Assert.Throws<FormatException>(() => PoolTag.Parse(text));
    }
}

public class CsvTests
{
    [Fact]
    public void EscapesAndSplitsRoundTrip()
    {
        var line = Csv.Line("a", "b,c", "say \"hi\"", 1.5, null);
        Assert.Equal("a,\"b,c\",\"say \"\"hi\"\"\",1.5,", line);
        Assert.Equal(["a", "b,c", "say \"hi\"", "1.5", ""], Csv.Split(line));
    }
}

public class PathGuardTests
{
    private static string Root(string name) => Path.Combine(Path.GetTempPath(), "pwtest-" + name);

    [Fact]
    public void AllowsOnlyDataRootAndExplicitOut()
    {
        var g = new PathGuard(Root("data"), Root("exe"));
        Assert.True(g.IsAllowed(Path.Combine(Root("data"), "incidents", "x.md"), out _));
        Assert.False(g.IsAllowed(Path.Combine(Root("other"), "x.md"), out _));
        Assert.False(g.IsAllowed(Root("data") + "-sibling", out _));
        g.AllowRoot(Root("out"));
        Assert.True(g.IsAllowed(Path.Combine(Root("out"), "tags.csv"), out _));
    }

    [Fact]
    public void NeverWritesNextToTheExe()
    {
        var g = new PathGuard(Root("data"), Root("exe"));
        Assert.False(g.IsAllowed(Path.Combine(Root("exe"), "log.txt"), out var reason));
        Assert.Contains("next to the executable", reason);
        Assert.Throws<UnauthorizedAccessException>(() => g.AllowRoot(Path.Combine(Root("exe"), "out")));
    }

    /// <summary>A stand-in for the app's volume rule (it refuses ReFS volumes): here drive R: is "a test volume".</summary>
    private static string? RefuseR(string p) => p.TrimStart('\\', '?', '.').StartsWith("R:", StringComparison.OrdinalIgnoreCase) ? "a test volume" : null;

    [Theory]
    [InlineData(@"R:\sample-app\out")]
    [InlineData(@"r:\x")]
    [InlineData(@"\\?\R:\x")]
    public void NeverWritesToAVolumeTheRuleRefuses(string path)
    {
        var g = new PathGuard(Root("data"), null, refuseVolume: RefuseR);
        Assert.Throws<UnauthorizedAccessException>(() => g.AllowRoot(path));
        Assert.False(g.IsAllowed(path, out var reason));
        Assert.Contains("a test volume", reason);
    }

    [Fact]
    public void NoDriveIsRefusedByItsLetter()
    {
        // The drive rule used to be a hard-coded F:. Without a volume rule every local drive is alike.
        var g = new PathGuard(Root("data"), null);
        g.AllowRoot(@"F:\out");
        Assert.True(g.IsAllowed(Path.Combine(@"F:\out", "tags.csv"), out var reason), reason);
        Assert.True(new PathGuard(Root("data"), null, refuseVolume: RefuseR).IsAllowed(Path.Combine(Root("data"), "x"), out _));
    }

    [Fact]
    public void RefusesNetworkDrives()
    {
        var remote = Root("mapped");
        var g = new PathGuard(Root("data"), null, isRemote: p => p.StartsWith(remote));
        Assert.Throws<UnauthorizedAccessException>(() => g.AllowRoot(Path.Combine(remote, "out")));
        Assert.True(g.IsAllowed(Path.Combine(Root("data"), "x"), out _));
    }

    [Fact]
    public void CheckRealRefusesAPathInADataRootThatMustBeThereAndIsNot()
    {
        // The Windows app's data root is made only by its start's checks: once it is gone (renamed or deleted while
        // PoolWatch runs), or a link or a file stands in its place, no write in it is allowed, so none makes it again.
        var data = Path.Combine(Path.GetTempPath(), "pwtest-must-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "pwtest-must-target-" + Guid.NewGuid().ToString("N"));
        var outRoot = Path.Combine(Path.GetTempPath(), "pwtest-must-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(outside);
        bool linkGone = true;
        try
        {
            var g = new PathGuard(data, null, dataRootMustExist: true);
            g.AllowRoot(outRoot);
            var file = Path.Combine(data, "cache", "x.json");
            Assert.Equal(file, g.CheckReal(file));
            Assert.Equal(data, g.CheckReal(data));
            Directory.Delete(data);
            Assert.Throws<UnauthorizedAccessException>(() => g.CheckReal(file));
            Assert.Throws<UnauthorizedAccessException>(() => g.CheckReal(data));
            Assert.Equal(Path.Combine(outRoot, "x.csv"), g.CheckReal(Path.Combine(outRoot, "x.csv"))); // an --out folder is not the data root
            File.WriteAllText(data, "x");
            Assert.Throws<UnauthorizedAccessException>(() => g.CheckReal(file));
            File.Delete(data);
            LinkHelper.MakeDirectoryLink(data, outside);
            linkGone = false;
            Assert.Throws<UnauthorizedAccessException>(() => g.CheckReal(file));
            linkGone = LinkHelper.DeleteLinkOnly(data);
            // Without the rule (tests, the Linux build), a missing data root is allowed: the caller makes it.
            Assert.Equal(file, new PathGuard(data, null).CheckReal(file));
        }
        finally
        {
            if (!linkGone) linkGone = LinkHelper.DeleteLinkOnly(data);
            if (linkGone) try { Directory.Delete(data, recursive: true); } catch { }
            try { Directory.Delete(outside, recursive: true); } catch { }
        }
        Assert.True(linkGone);
    }

    [Fact]
    public void CheckRealHoldsTheDataRootBeforeItLooksWhichFolderIsThereAndTriesAgainUntilItIsHeld()
    {
        // The hold (the Windows app: open so that no one can rename or delete the folder) is asked for the folder the start
        // checked, by the identity read for it, as the guard is made; until it is held, each check asks again before it
        // looks which folder is at the name, so once a write's check passes, that name names the folder checked.
        var data = Path.Combine(Path.GetTempPath(), "pwtest-hold-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        try
        {
            var steps = new List<string>();
            bool refuse = true;
            var g = new PathGuard(data, null, dataRootMustExist: true, dataRootIdentity: _ => { steps.Add("look"); return "folder 1"; },
                holdDataRoot: (root, identity) => { steps.Add($"hold {identity}"); return refuse ? null : new MemoryStream(); });
            Assert.Equal(["look", "hold folder 1"], steps);
            Assert.False(g.DataRootHeld);
            var file = Path.Combine(data, "x.json");
            steps.Clear();
            g.CheckReal(file);
            Assert.Equal(["hold folder 1", "look"], steps);
            refuse = false;
            steps.Clear();
            g.CheckReal(file);
            Assert.Equal(["hold folder 1", "look"], steps);
            Assert.True(g.DataRootHeld);
            steps.Clear();
            g.CheckReal(file);
            Assert.Equal(["look"], steps); // held for good: not asked again
            // A hold that throws is a hold not had; nothing is held for a root whose identity could not be read, or
            // without the must-exist rule.
            var throwing = new PathGuard(data, null, dataRootMustExist: true, dataRootIdentity: _ => "folder 1", holdDataRoot: (_, _) => throw new IOException("x"));
            Assert.False(throwing.DataRootHeld);
            Assert.Equal(file, throwing.CheckReal(file));
            int asked = 0;
            _ = new PathGuard(data, null, dataRootMustExist: true, dataRootIdentity: _ => null, holdDataRoot: (_, _) => { asked++; return new MemoryStream(); });
            _ = new PathGuard(data, null, dataRootIdentity: _ => "folder 1", holdDataRoot: (_, _) => { asked++; return new MemoryStream(); }).CheckReal(file);
            Assert.Equal(0, asked);
        }
        finally { try { Directory.Delete(data, recursive: true); } catch { } }
    }

    [Fact]
    public void CheckRealRefusesAPathInADataRootThatIsNotTheFolderTheStartChecked()
    {
        // Which folder the data root is (the Windows app: its file ID) is read once, when the guard is made for the folder the
        // start has just checked. A folder made at that name later, by anyone, is another one: no path in it is allowed.
        var data = Path.Combine(Path.GetTempPath(), "pwtest-pin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        try
        {
            string? now = "folder 1";
            var g = new PathGuard(data, null, dataRootMustExist: true, dataRootIdentity: _ => now);
            Assert.True(g.DataRootPinned);
            var file = Path.Combine(data, "x.json");
            Assert.Equal(file, g.CheckReal(file));
            now = "folder 2";
            var ex = Assert.Throws<UnauthorizedAccessException>(() => g.CheckReal(file));
            Assert.Contains("is not the data folder this PoolWatch checked at its start", ex.Message);
            now = null; // cannot be read now: refused too, and the message says that, not that it is another folder
            Assert.Equal($"PoolWatch write policy: which folder is at '{Path.GetFullPath(data)}' could not be read now, so nothing is written there",
                Assert.Throws<UnauthorizedAccessException>(() => g.CheckReal(file)).Message);
            now = "folder 1";
            Assert.Equal(file, g.CheckReal(file));
            // A step that throws counts as one that could not read it.
            bool throwing = false;
            var t = new PathGuard(data, null, dataRootMustExist: true, dataRootIdentity: _ => throwing ? throw new IOException("x") : "folder 1");
            throwing = true;
            Assert.Throws<UnauthorizedAccessException>(() => t.CheckReal(file));
            // Not readable when the guard is made: nothing in the data root is ever allowed.
            var unpinned = new PathGuard(data, null, dataRootMustExist: true, dataRootIdentity: _ => null);
            Assert.False(unpinned.DataRootPinned);
            Assert.Equal($"PoolWatch write policy: which folder its data folder '{Path.GetFullPath(data)}' is could not be read at its start",
                Assert.Throws<UnauthorizedAccessException>(() => unpinned.CheckReal(file)).Message);
            // Without the must-exist rule the identity is not used.
            Assert.True(new PathGuard(data, null, dataRootIdentity: _ => null).DataRootPinned);
            Assert.Equal(file, new PathGuard(data, null, dataRootIdentity: _ => null).CheckReal(file));
            // An --out folder is not the data root.
            var outRoot = Path.Combine(Path.GetTempPath(), "pwtest-pin-out-" + Guid.NewGuid().ToString("N"));
            unpinned.AllowRoot(outRoot);
            Assert.Equal(Path.Combine(outRoot, "x.csv"), unpinned.CheckReal(Path.Combine(outRoot, "x.csv")));
        }
        finally
        {
            try { Directory.Delete(data, recursive: true); } catch { }
        }
    }

    [Fact]
    public void CheckRealRefusesALinkInTheDataRoot()
    {
        // An unelevated user could plant a junction/symlink inside the data root pointing elsewhere, and an elevated
        // delete or write THROUGH it would land outside the root. The string policy (IsAllowed) still allows the name —
        // CheckReal looks at each folder on the path and refuses when one is a link then. On Windows the test plants a
        // directory JUNCTION: it needs no privilege (a symlink needs SeCreateSymbolicLinkPrivilege, which a standard
        // account lacks), so it is exactly what an unelevated attacker would use. Elsewhere it plants a symlink
        // (FileAttributes.ReparsePoint on Unix too).
        var data = Path.Combine(Path.GetTempPath(), "pwtest-real-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "pwtest-outside-" + Guid.NewGuid().ToString("N"));
        var link = Path.Combine(data, "evil");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "must survive the test");
        bool linkGone, targetKept;
        try
        {
            var g = new PathGuard(data, null);
            var realDir = Path.Combine(data, "incidents");
            Directory.CreateDirectory(realDir);
            Assert.Equal(realDir, g.CheckReal(realDir));                           // a real folder under the root is fine
            var newFile = Path.Combine(realDir, "new.md");
            Assert.Equal(newFile, g.CheckReal(newFile));                           // and a file that does not exist yet

            LinkHelper.MakeDirectoryLink(link, outside);
            Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint)); // the test really planted a link
            var through = Path.Combine(link, "x.md");
            var deeper = Path.Combine(link, "sub", "y.md");
            Assert.True(g.IsAllowed(through, out _));                              // string policy: name is under the root
            Assert.Throws<UnauthorizedAccessException>(() => g.CheckReal(link));   // but the component is a link
            Assert.Throws<UnauthorizedAccessException>(() => g.CheckReal(through));// and so is anything below it
            Assert.Throws<UnauthorizedAccessException>(() => g.CheckReal(deeper));
            Assert.Throws<UnauthorizedAccessException>(() => g.CheckReal(Path.Combine(link, "keep.txt"))); // an existing target file too
        }
        finally
        {
            // Remove the link itself first (never through it), then the folders; if the link cannot be removed, leave the
            // data folder in place rather than risk a recursive delete reaching the target.
            linkGone = LinkHelper.DeleteLinkOnly(link);
            targetKept = File.Exists(Path.Combine(outside, "keep.txt"));
            if (linkGone)
                try { Directory.Delete(data, recursive: true); } catch { }
            try { Directory.Delete(outside, recursive: true); } catch { }
        }
        Assert.True(linkGone);
        Assert.True(targetKept); // removing the link never touched the target's contents
    }

    [Fact]
    public void FirstLinkBelowFindsAJunctionOnTheWayToAPathOutsideTheDataRoot()
    {
        // The old token guard's Startup shortcut lives outside the data root; the elevated app deletes
        // it only when nothing between the user profile and the shortcut is a link.
        var home = Path.Combine(Path.GetTempPath(), "pwtest-home-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "pwtest-target-" + Guid.NewGuid().ToString("N"));
        var startup = Path.Combine(home, "AppData", "Startup");
        var link = Path.Combine(home, "AppData", "Linked");
        Directory.CreateDirectory(startup);
        Directory.CreateDirectory(outside);
        bool linkGone;
        try
        {
            Assert.Null(PathGuard.FirstLinkBelow(home, Path.Combine(startup, "x.lnk")));       // a real chain: no link
            LinkHelper.MakeDirectoryLink(link, outside);
            Assert.Equal(link, PathGuard.FirstLinkBelow(home, Path.Combine(link, "x.lnk")));    // the junction is found
            Assert.Equal(link, PathGuard.FirstLinkBelow(home, Path.Combine(link, "deeper", "x.lnk")));
            string elsewhere = Path.Combine(outside, "x.lnk");
            Assert.Equal(elsewhere, PathGuard.FirstLinkBelow(home, elsewhere));                 // not under the root: refuse
        }
        finally
        {
            linkGone = LinkHelper.DeleteLinkOnly(link);
            if (linkGone) try { Directory.Delete(home, recursive: true); } catch { }
            try { Directory.Delete(outside, recursive: true); } catch { }
        }
        Assert.True(linkGone);
    }

    [Fact]
    public void IsReparsePointIsFalseForMissingPathsAndRealFolders()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pwtest-rp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.False(PathGuard.IsReparsePoint(dir));
            Assert.False(PathGuard.IsReparsePoint(Path.Combine(dir, "missing.txt")));
            Assert.False(PathGuard.IsReparsePoint(Path.Combine(dir, "missing", "deeper.txt")));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }
}

/// <summary>Plants and removes directory links for the PathGuard tests, without ever deleting through the link.</summary>
internal static class LinkHelper
{
    /// <summary>Windows: a directory junction via <c>cmd.exe /c mklink /J</c> (no privilege needed). Elsewhere: a symlink.</summary>
    public static void MakeDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
        var psi = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "/d", "/c", "mklink", "/J", link, target }) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("cmd.exe did not start");
        var errTask = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(30_000)) { try { p.Kill(); } catch { } throw new TimeoutException("mklink /J did not finish"); }
        var err = errTask.Result;
        if (p.ExitCode != 0 || !Directory.Exists(link))
            throw new InvalidOperationException($"mklink /J failed (exit {p.ExitCode}): {output} {err}");
    }

    /// <summary>Removes the link entry only (a non-recursive delete of a junction / symlink removes the link, not the
    /// target's contents). Returns true when nothing is left at <paramref name="link"/>.</summary>
    public static bool DeleteLinkOnly(string link)
    {
        try
        {
            if (Directory.Exists(link) || File.Exists(link))
            {
                if (!File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint)) return false; // not a link: leave it
                Directory.Delete(link, recursive: false);
            }
        }
        catch { }
        return !Directory.Exists(link) && !File.Exists(link);
    }
}

public class SettingsTests
{
    [Fact]
    public void DefaultsMatchSpecAndRoundTrip()
    {
        var s = new Settings();
        Assert.Equal(10, s.SampleIntervalSeconds);
        Assert.Contains(s.GrowthRules, r => r.WindowMinutes == 10 && r.Bytes == 200 * Settings.MB);
        Assert.Contains(s.GrowthRules, r => r.WindowMinutes == 60 && r.Bytes == 1024 * Settings.MB);
        Assert.Equal(5L * 1024 * Settings.MB, s.NonPagedTotalAlertBytes);
        Assert.Equal(15, s.RetentionMinutes);
        Assert.Equal(7, s.RetentionDays);
        var path = Path.Combine(Path.GetTempPath(), $"pw-settings-{Guid.NewGuid():N}.json");
        s.CaptureSeconds = 999;
        s.Save(path);
        var t = Settings.Load(path);
        Assert.Equal(Settings.CaptureHardCapSeconds, t.CaptureSeconds); // clamped to the 5 min cap
        File.Delete(path);
    }
}
