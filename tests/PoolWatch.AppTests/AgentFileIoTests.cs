using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PoolWatch.Ui;

namespace PoolWatch.AppTests;

/// <summary>
/// The AI-agent files on disk, in a TEMP folder of their own (no ACL is read or changed): the prompt is written into a new
/// file or over a plain one, never through a link, a file with another name or a folder that is a link; the answer is
/// read bounded, and never from a link.
/// </summary>
public sealed class AgentFileIoTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pw-agent-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static readonly byte[] Prompt = Encoding.UTF8.GetBytes("# Help me choose\n\nEvery word.\n");

    [Fact]
    public void ThePromptIsWrittenAsANewFileOrReplacesAPlainOneWhole()
    {
        string file = Path.Combine(_root, "poolwatch-services.md");
        Assert.Null(AgentFileIo.Write(file, Prompt));
        Assert.Equal(Prompt, File.ReadAllBytes(file));
        File.WriteAllText(file, new string('x', 500)); // longer than the prompt: none of it is left after it
        Assert.Null(AgentFileIo.Write(file, Prompt));
        Assert.Equal(Prompt, File.ReadAllBytes(file));
        File.WriteAllText(file, "old"); // shorter: the file gets its room first, then the prompt
        Assert.Null(AgentFileIo.Write(file, Prompt));
        Assert.Equal(Prompt, File.ReadAllBytes(file));
    }

    /// <summary>A save that failed while a file the user agreed to replace was being written over says what that may have left.</summary>
    [Fact]
    public void ASaveThatFailedSaysWhatItLeft()
    {
        Assert.Equal("PoolWatch could not save the file: There is not enough space on the disk.", AgentFileIo.SaveFailed("There is not enough space on the disk. ", partlyReplaced: false));
        Assert.Equal("PoolWatch could not save the file: The request could not be performed because of an I/O device error. The file you picked may now hold " +
                     "only part of the prompt: save it again, or pick another name.",
            AgentFileIo.SaveFailed("The request could not be performed because of an I/O device error.", partlyReplaced: true));
    }

    private const string DiskFull = "There is not enough space on the disk.", DeviceError = "The request could not be performed because of an I/O device error.";

    /// <summary>Writes as the real write does, and counts each write.</summary>
    private static Action<SafeFileHandle, byte[]> Writes(List<int> writes) => (file, data) =>
    {
        writes.Add(data.Length);
        RandomAccess.Write(file, data, fileOffset: 0);
    };

    /// <summary>A file the user agreed to replace gets its room before any of its bytes is written over, so a full disk leaves it as it was.</summary>
    [Fact]
    public void AFileToReplaceIsLeftWholeWhenItsRoomCannotBeMade()
    {
        string file = Path.Combine(_root, "poolwatch-services.md");
        File.WriteAllText(file, "old"); // shorter than the prompt: it needs more room
        var writes = new List<int>();
        string? error = AgentFileIo.Write(file, Prompt, (_, _) => throw new IOException(DiskFull), Writes(writes));
        Assert.Equal(AgentFileIo.SaveFailed(DiskFull, partlyReplaced: false), error);
        Assert.Empty(writes);
        Assert.Equal("old", File.ReadAllText(file));
    }

    /// <summary>A write that fails once the file the user agreed to replace is being written over says it may now hold part of the prompt.</summary>
    [Fact]
    public void AFileToReplaceThatFailsWhileWrittenOverIsSaidToHoldPartOfThePrompt()
    {
        string file = Path.Combine(_root, "poolwatch-services.md");
        File.WriteAllText(file, "old");
        string? error = AgentFileIo.Write(file, Prompt, RandomAccess.SetLength, (_, _) => throw new IOException(DeviceError));
        Assert.Equal(AgentFileIo.SaveFailed(DeviceError, partlyReplaced: true), error);
        Assert.Contains("only part of the prompt", error);
    }

    /// <summary>A new file whose write fails is removed again, and nothing the user had is said to be partly replaced.</summary>
    [Fact]
    public void ANewFileThatFailsWhileWrittenIsRemovedAndNothingIsSaidToBePartlyReplaced()
    {
        string file = Path.Combine(_root, "poolwatch-services.md");
        string? error = AgentFileIo.Write(file, Prompt, RandomAccess.SetLength, (_, _) => throw new IOException(DeviceError));
        Assert.Equal(AgentFileIo.SaveFailed(DeviceError, partlyReplaced: false), error);
        Assert.False(File.Exists(file));
        // As a check on the seam: the real steps write the prompt whole, room first and then cut to it.
        var writes = new List<int>();
        Assert.Null(AgentFileIo.Write(file, Prompt, RandomAccess.SetLength, Writes(writes)));
        Assert.Equal([Prompt.Length], writes);
        Assert.Equal(Prompt, File.ReadAllBytes(file));
    }

    /// <summary>
    /// A link that opens as itself (here the reparse point an AF_UNIX socket's file is; a symbolic link to a file or a cloud
    /// placeholder open the same way, but making those needs a privilege): it is opened without being followed, and the
    /// open handle is then refused for what it is, so nothing is read from it or written into it.
    /// </summary>
    [Fact]
    public void ALinkThatOpensAsItselfIsRefusedForWhatItIsBeforeAnyByteIsReadOrWritten()
    {
        string file = Path.Combine(_root, "poolwatch-services-answer.json");
        Reparse.Tag(file, Reparse.AfUnix);
        Assert.Equal(Reparse.AfUnix, Reparse.TagOf(file)); // a reparse point PoolWatch opens as itself

        Assert.Equal<(byte[]?, string?)>((null, AgentFileIo.LinkRefused), AgentFileIo.Read(file, 100));
        Assert.Equal(AgentFileIo.LinkRefused, AgentFileIo.Write(file, Prompt));
        Assert.Equal(0, new FileInfo(file).Length); // nothing was written into it
        Assert.Equal(Reparse.AfUnix, Reparse.TagOf(file));
    }

    /// <summary>The decision on what was opened: a link (any reparse point but a cloud file's) or a folder is refused.</summary>
    [Theory]
    [InlineData(FileAttributes.Archive, 0u, null)]                                                                  // a plain file
    [InlineData(FileAttributes.Archive | FileAttributes.ReparsePoint, 0x9000001Au, null)]                           // a OneDrive cloud file
    [InlineData(FileAttributes.ReparsePoint, 0x9000F01Au, null)]                                                    // the last cloud tag
    [InlineData(FileAttributes.Archive | FileAttributes.ReparsePoint, 0xA000000Cu, AgentFileIo.LinkRefused)]        // a symbolic link
    [InlineData(FileAttributes.Directory | FileAttributes.ReparsePoint, 0xA0000003u, AgentFileIo.LinkRefused)]      // a junction
    [InlineData(FileAttributes.ReparsePoint, 0x8000001Bu, AgentFileIo.LinkRefused)]                                 // an app execution alias
    [InlineData(FileAttributes.Directory, 0u, AgentFileIo.FolderNotFile)]                                           // a folder
    [InlineData(FileAttributes.Directory | FileAttributes.ReparsePoint, 0x9000001Au, AgentFileIo.FolderNotFile)]    // a OneDrive folder
    public void OnlyAFileOrACloudFileIsUsed(FileAttributes attributes, uint tag, string? refusal) =>
        Assert.Equal(refusal, AgentFileIo.Refusal(attributes, tag));

    [Fact]
    public void AFileWithAnotherNameIsNotReplaced()
    {
        string target = Path.Combine(_root, "someone-elses.txt");
        File.WriteAllText(target, "kept");
        string file = Path.Combine(_root, "poolwatch-services.md");
        Links.HardLink(file, target);
        Assert.Equal(AgentFileIo.OtherNameRefused, AgentFileIo.Write(file, Prompt));
        Assert.Equal("kept", File.ReadAllText(target));
    }

    [Fact]
    public void AFolderThatIsALinkIsNotWrittenThrough()
    {
        string elsewhere = Directory.CreateDirectory(Path.Combine(_root, "elsewhere")).FullName;
        string folder = Path.Combine(_root, "Documents");
        Links.Junction(folder, elsewhere);
        try
        {
            Assert.Equal(AgentFileIo.FolderRefused, AgentFileIo.Write(Path.Combine(folder, "poolwatch-services.md"), Prompt));
            Assert.Empty(Directory.GetFileSystemEntries(elsewhere)); // and the file it made there is gone again
        }
        finally { Links.DeleteLinkOnly(folder); }
    }

    /// <summary>
    /// A junction in the file's place: Windows refuses to open it as a file at all, so nothing goes through it, and the entry
    /// itself is then looked at to say it is a link (<see cref="AgentFileIo.Refusal"/>), not that access was refused.
    /// </summary>
    [Fact]
    public void ALinkInTheFilesPlaceIsNeitherWrittenNorRead()
    {
        string elsewhere = Directory.CreateDirectory(Path.Combine(_root, "elsewhere")).FullName;
        File.WriteAllText(Path.Combine(elsewhere, "kept.txt"), "kept");
        string file = Path.Combine(_root, "poolwatch-services-answer.json");
        Links.Junction(file, elsewhere);
        try
        {
            Assert.Equal(AgentFileIo.LinkRefused, AgentFileIo.Write(file, Prompt));
            Assert.Equal<(byte[]?, string?)>((null, AgentFileIo.LinkRefused), AgentFileIo.Read(file, 100));
            Assert.Equal(["kept.txt"], Directory.GetFileSystemEntries(elsewhere).Select(Path.GetFileName));
            Assert.Equal("kept", File.ReadAllText(Path.Combine(elsewhere, "kept.txt")));
        }
        finally { Links.DeleteLinkOnly(file); }
    }

    [Fact]
    public void AFolderThatIsALinkIsNotReadThrough()
    {
        string elsewhere = Directory.CreateDirectory(Path.Combine(_root, "elsewhere")).FullName;
        File.WriteAllText(Path.Combine(elsewhere, "poolwatch-services-answer.json"), """{ "version": 1 }""");
        string folder = Path.Combine(_root, "Downloads");
        Links.Junction(folder, elsewhere);
        try
        {
            Assert.Equal<(byte[]?, string?)>((null, AgentFileIo.FolderReadRefused), AgentFileIo.Read(Path.Combine(folder, "poolwatch-services-answer.json"), 100));
            // The same file in its own folder is read.
            Assert.Equal(16, AgentFileIo.Read(Path.Combine(elsewhere, "poolwatch-services-answer.json"), 100).Bytes!.Length);
        }
        finally { Links.DeleteLinkOnly(folder); }
    }

    [Fact]
    public void TheAnswerIsReadBoundedAndAMissingFileOrAFolderSaysSo()
    {
        string file = Path.Combine(_root, "poolwatch-services-answer.json");
        File.WriteAllBytes(file, Encoding.UTF8.GetBytes(new string('a', 300)));
        var (bytes, error) = AgentFileIo.Read(file, 101);
        Assert.Null(error);
        Assert.Equal(101, bytes!.Length);
        Assert.Equal(300, AgentFileIo.Read(file, 1000).Bytes!.Length);
        Assert.Equal("PoolWatch could not open the file: it is not there.", AgentFileIo.Read(Path.Combine(_root, "none.json"), 10).Error);
        Assert.Equal<(byte[]?, string?)>((null, AgentFileIo.FolderNotFile), AgentFileIo.Read(_root, 10));
        Assert.Equal(AgentFileIo.FolderNotFile, AgentFileIo.Write(Directory.CreateDirectory(Path.Combine(_root, "a folder")).FullName, Prompt));
    }

    /// <summary>
    /// Makes a file in the test's own folder a reparse point, as Windows does for an AF_UNIX socket's file, with no
    /// privilege (a symbolic link needs one). Its data is empty: it points nowhere, and nothing in the test follows it.
    /// </summary>
    private static class Reparse
    {
        public const uint AfUnix = 0x80000023; // IO_REPARSE_TAG_AF_UNIX
        private const uint FSCTL_SET_REPARSE_POINT = 0x000900A4;

        public static void Tag(string path, uint tag)
        {
            File.WriteAllBytes(path, []);
            using var file = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var header = new byte[8]; // REPARSE_DATA_BUFFER: the tag, a data length of 0, a reserved 0
            BitConverter.TryWriteBytes(header, tag);
            if (!DeviceIoControl(file, FSCTL_SET_REPARSE_POINT, header, header.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "the reparse point was not made");
        }

        /// <summary>The entry's reparse tag, read without following it.</summary>
        public static uint TagOf(string path)
        {
            using var entry = PoolWatch.Engine.NoFollow.Open(path, dir: false, PoolWatch.Engine.NoFollow.FILE_READ_ATTRIBUTES, PoolWatch.Engine.NoFollow.ShareAll);
            Assert.False(entry.IsInvalid, $"the entry did not open (error {Marshal.GetLastWin32Error()})");
            return PoolWatch.Engine.NoFollow.ReparseTag(entry);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputSize, IntPtr output, int outputSize,
            out int returned, IntPtr overlapped);
    }
}
