using System.Text;
using PoolWatch.Core.Util;

namespace PoolWatch.Engine;

/// <summary>
/// Replaces a record file in one step: the text goes to "&lt;file&gt;.tmp", which is flushed to the disk and then moved over
/// the old file. A reader sees the old file or the new one, never a half-written one; and after a power cut or a crash of
/// the PC the new file holds what was written, where a rename without the flush can leave it empty or zero-filled. Both
/// paths go through the guard (never through a link). Throws when the file cannot be written; the old one is then as it was.
/// What remains: the rename itself is not written through, so a power cut in the seconds after a save can bring back the
/// old file whole (never an empty one), and with it the record as it was before that save.
/// </summary>
internal static class DurableFile
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <param name="encoding">Null: UTF-8 without a byte order mark (what File.WriteAllText writes).</param>
    /// <param name="open">How the temporary file is opened (null: <see cref="NoWaitOpen.Open"/>, which never waits for
    /// another program). Tests stand in their own opener.</param>
    /// <param name="failIfWait">Checked right before the rename that replaces the old file: it throws when an open of
    /// the old file would have to wait for another program at the time of the check (null:
    /// <see cref="NoWaitOpen.FailIfOpenWouldWait"/>). When it throws, the rename is not made and the old file is as it
    /// was, exactly as when the temporary file could not be written.</param>
    public static void Replace(PathGuard guard, string file, string text, Encoding? encoding = null, FileOpen? open = null, Action<string>? failIfWait = null)
    {
        open ??= NoWaitOpen.Open;
        failIfWait ??= NoWaitOpen.FailIfOpenWouldWait;
        string tmp = guard.CheckReal(file + ".tmp");
        using (var stream = open(tmp, FileMode.Create, FileAccess.Write, FileShare.None, false))
        {
            using (var writer = new StreamWriter(stream, encoding ?? Utf8, bufferSize: -1, leaveOpen: true)) writer.Write(text);
            stream.Flush(flushToDisk: true);
        }
        failIfWait(guard.CheckReal(file));
        File.Move(guard.CheckReal(tmp), guard.CheckReal(file), overwrite: true);
    }
}
