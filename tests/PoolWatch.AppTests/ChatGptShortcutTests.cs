using System.Security.Cryptography;
using PoolWatch.Core.Changes;
using PoolWatch.Core.Util;
using PoolWatch.Engine;

namespace PoolWatch.AppTests;

/// <summary>
/// The ChatGPT desktop shortcut, written into a "desktop" in the test's own TEMP folder (never the real desktop); the
/// junctions are planted there with the <see cref="Links"/> helper and removed (the link entry only) before anything else.
/// </summary>
public sealed class ChatGptShortcutTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "pw-sc-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _links = new();
    private readonly List<ChangeRecord> _records = new();
    private string Profile => Path.Combine(_base, "profile");
    private string Desktop => Path.Combine(Profile, "Desktop");
    private string Shortcut => Path.GetFullPath(Path.Combine(Desktop, ChatGptShortcut.FileName));
    private string Launcher => Path.Combine(_base, "install", ChatGptShortcut.LauncherName);
    private static string PowerShell => Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

    public ChatGptShortcutTests()
    {
        Directory.CreateDirectory(Desktop);
        Directory.CreateDirectory(Path.GetDirectoryName(Launcher)!);
        File.WriteAllText(Launcher, "# the launcher");
    }

    public void Dispose()
    {
        bool linksGone = true;
        foreach (var l in _links) linksGone &= Links.DeleteLinkOnly(l);
        if (linksGone) try { Directory.Delete(_base, recursive: true); } catch { }
    }

    private (bool Ok, string Message) Create(string? desktop = null) =>
        ChatGptShortcut.Create(new AppPaths(Path.Combine(_base, "data"), null, secure: false), Launcher, null, "a suggestion",
            new ChatGptShortcut.Folders(desktop ?? Desktop, Profile), _records.Add);

    [Fact]
    public void WritesAShortcutThatStartsTheLauncherHiddenAndRecordsItsHash()
    {
        var (ok, message) = Create();

        Assert.True(ok, message);
        byte[] bytes = File.ReadAllBytes(Shortcut);
        var link = ShellLink.Read(bytes);
        Assert.NotNull(link);
        Assert.Equal(PowerShell, link.Value.Target, ignoreCase: true);
        Assert.Equal($"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{Launcher}\"", link.Value.Arguments);
        var r = Assert.Single(_records);
        Assert.Equal(RecordKind.Shortcut, r.Kind);
        Assert.Equal(Shortcut, r.Path);
        Assert.Equal(ChangeIds.Shortcut(Shortcut), r.Id);
        Assert.Equal("a suggestion", r.Via);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), r.Sha256);
        Assert.Null(ChangeRecordFile.Invalid(r));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_base, "data"))); // the shell's temporary copy is gone
    }

    [Fact]
    public void NeverReplacesAFileOfThatName()
    {
        File.WriteAllText(Shortcut, "yours");

        var (ok, message) = Create();

        Assert.False(ok);
        Assert.Contains("already on your desktop", message);
        Assert.Equal("yours", File.ReadAllText(Shortcut));
        Assert.Empty(_records);
    }

    [Fact]
    public void RefusesADesktopThatIsAJunction()
    {
        string elsewhere = Path.Combine(_base, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        Directory.Delete(Desktop);
        Links.Junction(Desktop, elsewhere);
        _links.Add(Desktop);

        var (ok, message) = Create();

        Assert.False(ok);
        Assert.Contains("is a link", message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(elsewhere));
        Assert.Empty(_records);
    }

    [Fact]
    public void RefusesAJunctionAboveTheDesktop()
    {
        // The desktop inside a folder that is itself a junction, as a redirected desktop could be.
        string real = Path.Combine(_base, "real-cloud");
        Directory.CreateDirectory(Path.Combine(real, "Desktop"));
        string cloud = Path.Combine(Profile, "Cloud");
        Links.Junction(cloud, real);
        _links.Add(cloud);

        var (ok, message) = Create(Path.Combine(cloud, "Desktop"));

        Assert.False(ok);
        Assert.Contains(cloud, message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(real, "Desktop")));
        Assert.Empty(_records);
    }

    [Fact]
    public void AMissingDesktopIsSaidPlainly()
    {
        var (ok, message) = Create(Path.Combine(_base, "no-such-desktop"));

        Assert.False(ok);
        Assert.Equal("PoolWatch could not find your desktop folder.", message);
    }

    [Fact]
    public void WhenTheRecordCannotBeSavedNoShortcutIsLeft()
    {
        var (ok, message) = ChatGptShortcut.Create(new AppPaths(Path.Combine(_base, "data"), null, secure: false), Launcher, null, "a suggestion",
            new ChatGptShortcut.Folders(Desktop, Profile), _ => throw new IOException("the records file is full"));

        Assert.False(ok);
        Assert.Contains("the records file is full", message);
        Assert.False(File.Exists(Shortcut));
    }

    [Theory]
    [InlineData(0x9000001Au, true)]   // IO_REPARSE_TAG_CLOUD
    [InlineData(0x9000101Au, true)]   // IO_REPARSE_TAG_CLOUD_1
    [InlineData(0x9000F01Au, true)]   // IO_REPARSE_TAG_CLOUD_F
    [InlineData(0xA0000003u, false)]  // IO_REPARSE_TAG_MOUNT_POINT (a junction)
    [InlineData(0xA000000Cu, false)]  // IO_REPARSE_TAG_SYMLINK
    [InlineData(0x9000301Bu, false)]  // not a cloud tag, though close to one
    public void OnlyCloudFilesTagsAreAccepted(uint tag, bool cloud) => Assert.Equal(cloud, NoFollow.IsCloudFilesTag(tag));

    [Fact]
    public void ARealFolderHasNoReparseTag()
    {
        using var h = NoFollow.Open(Desktop, dir: true, NoFollow.FILE_READ_ATTRIBUTES, NoFollow.ShareAll);
        Assert.False(h.IsInvalid);
        Assert.Equal(0u, NoFollow.ReparseTag(h));
    }
}
