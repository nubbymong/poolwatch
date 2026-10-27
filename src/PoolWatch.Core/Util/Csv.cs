using System.Globalization;
using System.Text;

namespace PoolWatch.Core.Util;

/// <summary>Minimal RFC 4180 CSV helpers (invariant culture).</summary>
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
        if (s.IndexOfAny(['"', ',', '\n', '\r']) < 0) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
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
/// columns mid-day), rows go to <c>name-2.csv</c> (then -3, ...) instead, so no file mixes two layouts.
/// </summary>
public sealed class CsvFile : IDisposable
{
    private readonly StreamWriter _writer;

    public CsvFile(string path, string header)
    {
        path = PathFor(path, header);
        bool exists = File.Exists(path) && new FileInfo(path).Length > 0;
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false));
        if (!exists) _writer.WriteLine(header);
        Path_ = path;
        _name = Path.GetFileName(path);
    }

    private readonly string _name;

    public string Path_ { get; }

    /// <summary>The file to append to: <paramref name="path"/> unless it already holds another header.</summary>
    public static string PathFor(string path, string header)
    {
        string dir = Path.GetDirectoryName(path) ?? "", stem = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int n = 1; n < 100; n++)
        {
            string p = n == 1 ? path : Path.Combine(dir, $"{stem}-{n}{ext}");
            if (!File.Exists(p) || new FileInfo(p).Length == 0) return p;
            string? first;
            try
            {
                using var r = new StreamReader(new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), Encoding.UTF8);
                first = r.ReadLine();
            }
            catch (IOException) { return p; }
            if (first == header) return p;
        }
        return path;
    }

    public void Write(params object?[] values)
    {
        string line = Csv.Line(values);
        _writer.WriteLine(line);
        Profiler.WroteName(_name, line.Length + 2);
    }
    public void Flush() => _writer.Flush();
    public void Dispose() => _writer.Dispose();
}
