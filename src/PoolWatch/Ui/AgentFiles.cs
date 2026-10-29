using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PoolWatch.Core.Services.Agent;
using PoolWatch.Engine;

namespace PoolWatch.Ui;

/// <summary>
/// What the two AI-agent windows ask of the desktop: the Save and Open dialogs, the file they write or read, the
/// clipboard, a question and a message. The tests answer with a fake, so no test shows a dialog or touches a real file.
/// </summary>
internal interface IAgentFiles
{
    /// <summary>Where to save the prompt, picked in a Save dialog that offers <paramref name="defaultName"/>; null when cancelled.</summary>
    string? AskSavePath(IWin32Window owner, string defaultName);

    /// <summary>The answer file picked in an Open dialog; null when cancelled.</summary>
    string? AskOpenPath(IWin32Window owner);

    /// <summary>Writes <paramref name="bytes"/> to <paramref name="path"/>, never through a link: null, or why not.</summary>
    string? Write(string path, byte[] bytes);

    /// <summary>At most <paramref name="max"/> bytes of <paramref name="path"/>, never a link: the bytes, or why not.</summary>
    (byte[]? Bytes, string? Error) Read(string path, int max);

    /// <summary>Puts <paramref name="text"/> on the clipboard: null, or why not.</summary>
    string? Copy(string text);

    /// <summary>A yes or no question (Yes: true).</summary>
    bool AskYesNo(IWin32Window owner, string question, string title);

    void Tell(IWin32Window owner, string message, string title);
}

/// <summary>The real dialogs, files and clipboard.</summary>
internal sealed class WindowsAgentFiles : IAgentFiles
{
    public string? AskSavePath(IWin32Window owner, string defaultName)
    {
        // DereferenceLinks off: a shortcut picked in the dialog is the file named, never the place it points at.
        using var d = new SaveFileDialog
        {
            Title = "Save the prompt for your AI agent", FileName = defaultName, DefaultExt = "md", AddExtension = true,
            Filter = "Markdown file (*.md)|*.md", OverwritePrompt = true, CheckPathExists = true, DereferenceLinks = false,
        };
        return d.ShowDialog(owner) == DialogResult.OK ? d.FileName : null;
    }

    public string? AskOpenPath(IWin32Window owner)
    {
        using var d = new OpenFileDialog
        {
            Title = "Import your AI agent's answer", FileName = AgentAnswer.FileName, Filter = "Answer file (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true, Multiselect = false, DereferenceLinks = false,
        };
        return d.ShowDialog(owner) == DialogResult.OK ? d.FileName : null;
    }

    public string? Write(string path, byte[] bytes) => AgentFileIo.Write(path, bytes);

    public (byte[]? Bytes, string? Error) Read(string path, int max) => AgentFileIo.Read(path, max);

    public string? Copy(string text)
    {
        try
        {
            Clipboard.SetDataObject(text, copy: true, retryTimes: 5, retryDelay: 100);
            return null;
        }
        catch (Exception) { return "Another program is using the clipboard. Try again."; }
    }

    public bool AskYesNo(IWin32Window owner, string question, string title) =>
        MessageBox.Show(owner, question, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    public void Tell(IWin32Window owner, string message, string title) => MessageBox.Show(owner, message, title);
}

/// <summary>
/// The AI-agent files on disk. PoolWatch runs as administrator and writes or reads where the user picked, a folder a
/// standard user (or malware running as the user) can usually change, so neither follows a link there: the prompt is
/// written into a new file, or into the existing file itself only when it is a plain file with one name; the answer is
/// read only from a file that is not itself a link; and either only when the open file is where the user picked (no
/// folder on the way is a link to another place). OneDrive's cloud files carry a reparse tag but are files, so they are
/// allowed.
/// </summary>
internal static class AgentFileIo
{
    public const string LinkRefused = "PoolWatch did not use that file: it is a link to another place. Pick another file.";

    public const string FolderNotFile = "That is a folder, not a file.";

    public const string FolderRefused = "PoolWatch did not save it there: that folder is a link to another place, or a drive letter that " +
                                        "stands for another folder. Pick a folder on this PC, such as Documents.";

    public const string FolderReadRefused = "PoolWatch did not read that file: its folder is a link to another place, or a drive letter that " +
                                            "stands for another folder. Move the file to a folder on this PC, such as Documents, and import it from there.";

    public const string OtherNameRefused = "PoolWatch did not replace that file: it is also stored under another name somewhere else on this PC. " +
                                           "Pick another name.";

    /// <summary>
    /// Writes <paramref name="bytes"/> as the whole of <paramref name="path"/>: null, or why not. A new file is created
    /// without following a link (a name taken by a link counts as taken). A name already taken was agreed to in the Save
    /// dialog: that file itself is opened (never what a link points at) and replaced only when it is not a link and has no
    /// other name (a hard link). Either way the open handle's final path must be the one picked, so a folder on the way
    /// that is a link refuses the write; a file created for it is removed again.
    /// </summary>
    public static string? Write(string path, byte[] bytes) =>
        Write(path, bytes, RandomAccess.SetLength, (file, data) => RandomAccess.Write(file, data, fileOffset: 0));

    /// <summary><see cref="Write(string, byte[])"/>, its two steps that can fail on a disk given (the tests pass ones that fail).</summary>
    /// <param name="setLength">Sets the open file's length: first the room for the prompt, then cut to it.</param>
    /// <param name="write">Writes the prompt into the open file from its start.</param>
    internal static string? Write(string path, byte[] bytes, Action<SafeFileHandle, long> setLength, Action<SafeFileHandle, byte[]> write)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return "PoolWatch cannot save a file of that name."; }

        bool created = true;
        var file = NoFollow.CreateNew(full, NoFollow.GENERIC_WRITE | NoFollow.DELETE);
        if (file.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            file.Dispose();
            if (error is not (NoFollow.ERROR_FILE_EXISTS or NoFollow.ERROR_ALREADY_EXISTS)) return OpenFailed("save", full, error);
            created = false;
            file = NoFollow.Open(full, dir: false, NoFollow.GENERIC_WRITE, share: 0);
            if (file.IsInvalid)
            {
                error = Marshal.GetLastWin32Error();
                file.Dispose();
                return OpenFailed("save", full, error);
            }
        }
        using (file)
        {
            bool writing = false;
            try
            {
                long had = 0;
                if (!created)
                {
                    var facts = NoFollow.Query(file);
                    if ((Refusal(facts.Attributes, NoFollow.ReparseTag(file)) ?? (facts.Links > 1 ? OtherNameRefused : null)) is { } why) return why;
                    had = facts.Length;
                }
                if (!NoFollow.SamePath(NoFollow.FinalPath(file), full))
                {
                    if (created) NoFollow.DeleteOnClose(file);
                    return FolderRefused;
                }
                // A file the user agreed to replace gets its room first, so a full disk refuses before any of its bytes is
                // replaced; then the prompt is written over it and what is left of the old file is cut off.
                if (had < bytes.Length) setLength(file, bytes.Length);
                writing = true;
                write(file, bytes);
                setLength(file, bytes.Length);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or NotSupportedException)
            {
                // A file PoolWatch made is removed again, so none of its own is left empty or half written. A file the user
                // agreed to replace is unchanged until its room is there; a write that still fails after that (the disk
                // itself failing) may leave it partly replaced, and the message says so.
                if (created) NoFollow.DeleteOnClose(file);
                return SaveFailed(ex.Message, partlyReplaced: writing && !created);
            }
        }
    }

    /// <summary>
    /// At most <paramref name="max"/> bytes from the start of <paramref name="path"/>, or why not: the file itself is opened
    /// (never what a link points at), one that is a link or a folder is refused, and so is one whose open handle is not at
    /// the path picked (a folder on the way that is a link to another place), before any byte is read.
    /// </summary>
    public static (byte[]? Bytes, string? Error) Read(string path, int max)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return (null, "PoolWatch cannot open a file of that name."); }
        using var file = NoFollow.Open(full, dir: false, NoFollow.GENERIC_READ, NoFollow.FILE_SHARE_READ | NoFollow.FILE_SHARE_WRITE);
        if (file.IsInvalid) return (null, OpenFailed("open", full, Marshal.GetLastWin32Error()));
        try
        {
            if (NotAFile(file) is { } why) return (null, why);
            if (!NoFollow.SamePath(NoFollow.FinalPath(file), full)) return (null, FolderReadRefused);
            var buffer = new byte[Math.Max(0, max)];
            int n = 0;
            while (n < buffer.Length)
            {
                int read = RandomAccess.Read(file, buffer.AsSpan(n), n);
                if (read == 0) break;
                n += read;
            }
            return (buffer[..n], null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or NotSupportedException)
        {
            return (null, "PoolWatch could not read the file: " + ex.Message);
        }
    }

    /// <summary>
    /// A save that failed, in plain words; <paramref name="partlyReplaced"/>: the file the user agreed to replace was being
    /// written over when it failed, so it may now hold part of the prompt.
    /// </summary>
    internal static string SaveFailed(string why, bool partlyReplaced) =>
        $"PoolWatch could not save the file: {why.Trim().TrimEnd('.')}." +
        (partlyReplaced ? " The file you picked may now hold only part of the prompt: save it again, or pick another name." : "");

    /// <summary>Why the open entry is not a file PoolWatch uses (<see cref="Refusal"/>); null for a file.</summary>
    private static string? NotAFile(SafeFileHandle file) => Refusal(NoFollow.Query(file).Attributes, NoFollow.ReparseTag(file));

    /// <summary>
    /// Why an entry with these <paramref name="attributes"/> and reparse <paramref name="tag"/> (0: none) is not a file
    /// PoolWatch writes or reads: a link to another place (a symbolic link, a junction, any reparse point but a cloud
    /// file's), or a folder; null for a file, a cloud file included.
    /// </summary>
    internal static string? Refusal(FileAttributes attributes, uint tag) =>
        tag != 0 && !NoFollow.IsCloudFilesTag(tag) ? LinkRefused
        : (attributes & FileAttributes.Directory) != 0 ? FolderNotFile
        : null;

    /// <summary>
    /// Why the open of <paramref name="full"/> failed, in plain words. A folder or a junction in the file's place cannot be
    /// opened as a file at all (Windows refuses access to it), and that refusal is what keeps PoolWatch from writing or
    /// reading through it; to say so rather than "refused access", the entry itself is looked at once more, for its
    /// attributes only (opened as itself, never followed, with no right to its contents).
    /// </summary>
    private static string OpenFailed(string what, string full, int error)
    {
        if (error == NoFollow.ERROR_ACCESS_DENIED)
        {
            using var entry = NoFollow.Open(full, dir: true, NoFollow.FILE_READ_ATTRIBUTES, NoFollow.ShareAll);
            if (!entry.IsInvalid)
            {
                try
                {
                    if (NotAFile(entry) is { } why) return why;
                }
                catch (Win32Exception) { }
            }
        }
        return Failed(what, error);
    }

    /// <summary>A failed open in plain words: what Windows says, with its error number.</summary>
    private static string Failed(string what, int error) => error switch
    {
        NoFollow.ERROR_FILE_NOT_FOUND or NoFollow.ERROR_PATH_NOT_FOUND => $"PoolWatch could not {what} the file: it is not there.",
        NoFollow.ERROR_ACCESS_DENIED => $"PoolWatch could not {what} the file: Windows refused access to it.",
        _ => $"PoolWatch could not {what} the file: {new Win32Exception(error).Message.TrimEnd('.')} (error {error}).",
    };
}
