using PoolWatch.Core;
using PoolWatch.Core.Tags;

namespace PoolWatch.Tests;

/// <summary>
/// L1 and L4 (security review): what the elevated app reads from its settings is bounded. Path settings must be plain
/// local drive paths (an old per-user settings.json, a file the user could write, is migrated through Settings.Parse),
/// and pooltag.txt is read only up to a size a real one never reaches.
/// </summary>
public class InputBoundsTests
{
    [Theory]
    [InlineData(@"C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\triage\pooltag.txt", true)]
    [InlineData(@"D:/aicc", true)]
    [InlineData(@"E:\", true)]
    [InlineData("", false)]
    [InlineData("relative\\x", false)]
    [InlineData("C:", false)]
    [InlineData(@"C:x", false)]                     // drive-relative
    [InlineData(@"\\server\share\x", false)]         // a share
    [InlineData(@"\\?\C:\x", false)]                 // a device path
    [InlineData(@"\\.\pipe\x", false)]               // a pipe
    [InlineData(@"C:\x\file.txt:stream", false)]     // an alternate data stream
    [InlineData(@"C:\x\*.txt", false)]
    [InlineData("C:\\x\"y", false)]
    [InlineData("C:\\x\ny", false)]
    public void APathSettingIsAPlainLocalDrivePath(string path, bool ok) => Assert.Equal(ok, Settings.IsLocalDrivePath(path));

    [Fact]
    public void ALongPathIsNotAccepted() => Assert.False(Settings.IsLocalDrivePath(@"C:\" + new string('a', Settings.MaxPathSettingLength)));

    [Fact]
    public void MigratedPathSettingsAreValidatedNotTakenAsWritten()
    {
        // What a user-writable settings.json could have said: shares, pipes, relative paths, over-long values.
        string json = $$"""
            {
              "aiccResourcesRoot": "\\\\attacker\\share\\aicc",
              "claudeVersionsDir": "relative\\versions",
              "poolTagTxtPath": "\\\\.\\pipe\\evil",
              "symbolPath": "{{new string('x', Settings.MaxSymbolPathLength + 1)}}",
              "watchedApps": ["Code", "..\\..\\evil", "a:b", "{{new string('n', 65)}}"],
              "markHotkey": "{{new string('k', 41)}}"
            }
            """;
        var s = Settings.Parse(json);
        Assert.Equal("", s.AiccResourcesRoot);
        Assert.Equal("", s.ClaudeVersionsDir);
        Assert.Equal(Settings.DefaultPoolTagTxtPath, s.PoolTagTxtPath);
        Assert.Equal("", s.SymbolPath);
        Assert.Equal(["Code"], s.WatchedApps);
        Assert.Equal("", s.MarkHotkey);

        // Plain local paths are kept (trimmed).
        var ok = Settings.Parse("""{ "aiccResourcesRoot": " D:\\aicc ", "claudeVersionsDir": "E:\\claude\\versions", "poolTagTxtPath": "C:\\tools\\pooltag.txt", "symbolPath": "C:\\symbols;D:\\pdb" }""");
        Assert.Equal(@"D:\aicc", ok.AiccResourcesRoot);
        Assert.Equal(@"E:\claude\versions", ok.ClaudeVersionsDir);
        Assert.Equal(@"C:\tools\pooltag.txt", ok.PoolTagTxtPath);
        Assert.Equal(@"C:\symbols;D:\pdb", ok.SymbolPath);
    }

    [Fact]
    public void PoolTagTxtIsReadOnlyUpToItsBound()
    {
        string file = Path.Combine(Path.GetTempPath(), "pw-pooltag-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(file, "rem comment\nFile - nt - File objects\nCtso - contoso.sys - Contoso cache\n");
            long size = new FileInfo(file).Length;
            Assert.Equal(2, PoolTagTxt.ReadBounded(file, maxBytes: size).Count);       // exactly at the bound: read
            Assert.Empty(PoolTagTxt.ReadBounded(file, maxBytes: size - 1));            // one byte over: not used at all
            Assert.True(new TagKnowledge(file, null).PoolTagTxtLoaded);
            Assert.False(new TagKnowledge(file, null, maxPoolTagTxtBytes: 16).PoolTagTxtLoaded);
            Assert.Equal("contoso.sys", new TagKnowledge(file, null).Lookup(PoolTag.Parse("Ctso")).Owner);
            Assert.True(TagKnowledge.MaxPoolTagTxtBytes >= 4L * 1024 * 1024); // far above a real pooltag.txt
        }
        finally { try { File.Delete(file); } catch { } }
    }
}
