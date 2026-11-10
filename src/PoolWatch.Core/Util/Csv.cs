using System.Globalization;
using System.Text;

namespace PoolWatch.Core.Util;

/// <summary>
/// Minimal RFC 4180 CSV helpers (invariant culture), written so that opening a file in a spreadsheet runs nothing,
/// whether it splits a line at commas or at semicolons (as Excel does where the list separator is a semicolon). A text
/// value that a spreadsheet would read as a formula (one that starts with = + - @, a tab or a line break, or their
/// full-width forms) is written with a leading apostrophe: a reader drops that one apostrophe to get the value. A
/// spreadsheet that splits at semicolons can start a cell after a semicolon, and a row after a line break, inside a
/// value whose quotes are not at the start of a cell as it splits the line; so each part of a text value after a
/// semicolon or a line break that starts that way, or with a quote, gets an apostrophe too, which stays in the value as
/// read back. A value with a quote, a comma, a semicolon or a line break is quoted.
/// Numbers never get an apostrophe.
/// </summary>
public static class Csv
{
    public static string Escape(object? value)
    {
        string s = value switch
        {
            null => "",
            DateTime dt => dt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            double d => d.ToString("0.###", CultureInfo.InvariantCulture),
            float f => f.ToString("0.###", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };
        if (value is not null and not IFormattable) s = NoFormulaInAnyCell(s);
        if (s.IndexOfAny(['"', ',', ';', '\n', '\r']) < 0) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>Whether a spreadsheet would read a text cell starting this way as a formula (or a DDE call).</summary>
    public static bool IsFormulaStart(string s) => s.Length > 0 && IsFormulaChar(s[0]);

    private static bool IsFormulaChar(char c) => c is '=' or '+' or '-' or '@' or '\t' or '\r' or '\n' or '＝' or '＋' or '－' or '＠';

    /// <summary>
    /// <paramref name="s"/> with an apostrophe before each place a spreadsheet could start a cell or a row with a formula:
    /// its start, when it starts with a formula (<see cref="IsFormulaStart"/>), and each place after a semicolon or a line
    /// break (a carriage return and a line feed together are one) that starts with a formula or a quote.
    /// </summary>
    internal static string NoFormulaInAnyCell(string s)
    {
        StringBuilder? sb = null;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            bool cellStart = i == 0 || s[i - 1] is ';' or '\n' || (s[i - 1] == '\r' && c != '\n');
            if (cellStart && (IsFormulaChar(c) || (i > 0 && c == '"')))
            {
                sb ??= new StringBuilder(s, 0, i, s.Length + 4);
                sb.Append('\'');
            }
            sb?.Append(c);
        }
        return sb?.ToString() ?? s;
    }

    public static string Line(params object?[] values)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < values.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(Escape(values[i]));
        }
        return sb.ToString();
    }

    /// <summary>
    /// <paramref name="text"/> up to the end of its last whole row: up to and including its last line break outside a
    /// quoted value. A file PoolWatch appends rows to (events.csv, spawns.csv) can be read in the moment of an append, or
    /// after a crash in it, with its last row cut short; this leaves that row out, and a value with a line break in it,
    /// cut inside its quotes, is not taken for a row's end. Text with no line break outside quotes gives "".
    /// </summary>
    public static string CompleteRows(string text)
    {
        bool quoted = false;
        int end = -1;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"') quoted = !quoted; // a doubled quote inside a value toggles twice
            else if (c == '\n' && !quoted) end = i;
        }
        return end == text.Length - 1 ? text : text[..(end + 1)];
    }

    /// <summary>
    /// A CSV file PoolWatch appends to, read beside its writer (sharing reading, writing and deleting) up to the end of its
    /// last whole row (<see cref="CompleteRows"/>).
    /// </summary>
    public static string ReadCompleteRows(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(s, Encoding.UTF8);
        return CompleteRows(r.ReadToEnd());
    }

    /// <summary>Splits one CSV line (no embedded newlines).</summary>
    public static List<string> Split(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else quoted = false;
                }
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { result.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        result.Add(sb.ToString());
        return result;
    }
}

/// <summary>
/// Append-only CSV file that writes its header once. When the file exists with a different header (an upgrade added
/// columns mid-day), rows go to <c>name-2.csv</c> (then -3, ...) instead, so no file mixes two layouts. A name whose
/// header read or open is refused (another program's share mode or lock refuses it, or the opener gives up an open
/// that would have to wait) is passed over the same way, so the rows go on in the next numbered file, under their own
/// header, and never under a header that was not read. Rows are written in batches, each in one write of the whole
/// batch. Through a stream with no buffer of its own (the default opener's and the app's), a batch whose write fails is
/// not written at all, and stays with the caller, who can write it to the next numbered file (<see cref="Number"/>).
/// </summary>
public sealed class CsvFile : IDisposable
{
    private readonly Stream _stream;
    private readonly string _header;
    private bool _headerDue;
    private readonly List<string> _rows = new();
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    /// <summary>The numbers a file's names go up to: the name itself is 1, <c>name-2.csv</c> is 2, up to 99.</summary>
    public const int LastNumber = 99;

    /// <summary>The default opener: a plain <see cref="FileStream"/>, with no buffer of its own, so a write that fails
    /// leaves nothing behind to be written later. The Windows app stands in one that never waits for another program (an
    /// open that would have to wait is given up and refused).</summary>
    public static readonly Func<string, FileMode, FileAccess, FileShare, Stream> DefaultOpen = (p, m, a, s) => new FileStream(p, m, a, s, bufferSize: 0);

    /// <param name="open">How the CSV file is opened (null: a plain <see cref="FileStream"/>). The Windows app passes an
    /// opener that never waits for another program. The stream it gives should have no buffer of its own, as the
    /// default's has not: a batch whose write fails is then not written later.</param>
    /// <param name="first">The number of the first name that may be used (1: <paramref name="path"/> itself; n: its
    /// n-th numbered file), so a caller whose write was refused goes on after the file that refused it.</param>
    public CsvFile(string path, string header, Func<string, FileMode, FileAccess, FileShare, Stream>? open = null, int first = 1)
    {
        open ??= DefaultOpen;
        _header = header;
        Exception? refused = null;
        for (int n = Math.Max(first, 1); n <= LastNumber; n++)
        {
            string p = Numbered(path, n);
            try
            {
                if (!HoldsLayout(p, header, open)) continue; // another layout: the next number
                bool exists = File.Exists(p) && new FileInfo(p).Length > 0;
                _stream = open(p, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                _headerDue = !exists;
                Path_ = p;
                Name = Path.GetFileName(p);
                Number = n;
                return;
            }
            catch (IOException e) when (IsRefused(e))
            {
                refused = e;
                _refused.Add((Path.GetFileName(p), e.Message.Trim()));
            }
        }
        if (refused is not null) throw refused;
        throw new IOException($"Every file of {Path.GetFileName(path)} up to its {LastNumber}th numbered file holds another layout.");
    }

    public string Path_ { get; } = "";

    /// <summary>The file's own name (<c>totals-2.csv</c>, say).</summary>
    public string Name { get; } = "";

    /// <summary>The number of the name used: 1 for the name itself, n for its n-th numbered file.</summary>
    public int Number { get; }

    private readonly List<(string Name, string Why)> _refused = new();

    /// <summary>The names passed over at the open because their header read or open was refused, with why.</summary>
    public IReadOnlyList<(string Name, string Why)> Refused => _refused;

    /// <summary>A sharing or lock refusal (ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION): another handle's share mode or
    /// lock refuses the open or the write, or the opener gave up an open that would have had to wait.</summary>
    public static bool IsRefused(Exception e) => e is IOException && e.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021);

    private static string Numbered(string path, int n) =>
        n == 1 ? path : Path.Combine(Path.GetDirectoryName(path) ?? "", $"{Path.GetFileNameWithoutExtension(path)}-{n}{Path.GetExtension(path)}");

    /// <summary>The file to append to: <paramref name="path"/> unless it already holds another header.</summary>
    public static string PathFor(string path, string header) => PathFor(path, header, DefaultOpen);

    /// <summary>The file to append to, read through <paramref name="open"/> (which, in the app, never waits for another
    /// program). Throws when the header of a file that is there cannot be read (a refused read included), so no row is
    /// appended under a header that was not read.</summary>
    public static string PathFor(string path, string header, Func<string, FileMode, FileAccess, FileShare, Stream> open)
    {
        for (int n = 1; n <= LastNumber; n++)
        {
            string p = Numbered(path, n);
            if (HoldsLayout(p, header, open)) return p;
        }
        return path;
    }

    /// <summary>Whether <paramref name="p"/> is not there, is empty, or starts with <paramref name="header"/>; throws when
    /// the header of a file that is there cannot be read.</summary>
    private static bool HoldsLayout(string p, string header, Func<string, FileMode, FileAccess, FileShare, Stream> open)
    {
        if (!File.Exists(p) || new FileInfo(p).Length == 0) return true;
        try
        {
            using var r = new StreamReader(open(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), Encoding.UTF8);
            return r.ReadLine() == header;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return true; } // gone since: made anew, with its header
    }

    /// <summary>Adds a row, written at the next <see cref="Flush"/>.</summary>
    public void Write(params object?[] values)
    {
        string line = Csv.Line(values);
        _rows.Add(line);
        Profiler.WroteName(Name, line.Length + 2);
    }

    /// <summary>Writes the rows added since the last flush; when the write fails they are kept for the next one.</summary>
    public void Flush()
    {
        Append(_rows);
        _rows.Clear();
    }

    /// <summary>
    /// Writes <paramref name="rows"/> (each a line <see cref="Csv.Line"/> made), with the header first when the file had
    /// none, in one write of the whole batch. Throws when the write fails; the batch is then not written.
    /// </summary>
    public void Append(IReadOnlyList<string> rows)
    {
        if (rows.Count == 0 && !_headerDue) return;
        var text = new StringBuilder();
        if (_headerDue) text.Append(_header).Append(Environment.NewLine);
        foreach (string row in rows) text.Append(row).Append(Environment.NewLine);
        _stream.Write(Utf8.GetBytes(text.ToString()));
        _stream.Flush();
        _headerDue = false;
    }

    /// <summary>Lets go of the file and writes nothing more (after a write that failed, say).</summary>
    public void Close() => _stream.Dispose();

    public void Dispose()
    {
        try { Flush(); }
        finally { _stream.Dispose(); }
    }
}
