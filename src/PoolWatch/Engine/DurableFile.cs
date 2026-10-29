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
    public static void Replace(PathGuard guard, string file, string text, Encoding? encoding = null)
    {
        string tmp = guard.CheckReal(file + ".tmp");
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using (var writer = new StreamWriter(stream, encoding ?? Utf8, bufferSize: -1, leaveOpen: true)) writer.Write(text);
            stream.Flush(flushToDisk: true);
        }
        File.Move(guard.CheckReal(tmp), guard.CheckReal(file), overwrite: true);
    }
}
