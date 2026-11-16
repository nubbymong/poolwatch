using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FsCheck;
using FsCheck.Fluent;

namespace PoolWatch.Tests;

/// <summary>
/// Random input for the property tests, made with FsCheck: text and bytes as a file or an answer from the network may hold
/// them, and documents in the form PoolWatch reads, each changed at random from a good one. Most changes keep the
/// document JSON, so it gets past the JSON parser to the reader's own checks; the others break it as text. Every case is
/// made from FsCheck's random seed, which a failure prints with the case; <see cref="Cases"/> says how to make a failing
/// case again. PoolWatch.AppTests compiles this file too.
/// </summary>
internal static class PropertyInputs
{
    /// <summary>
    /// Checks <paramref name="property"/> on <paramref name="cases"/> random inputs from <paramref name="inputs"/>, each on a
    /// thread of its own: the first input it fails on, or that takes longer than <see cref="CaseTime"/>, fails the test,
    /// and the failure prints that input.
    /// </summary>
    public static void ForAll<T>(int cases, Gen<T> inputs, Action<T> property) =>
        Check.One(Cases(cases), Prop.ForAll(Arb.From(inputs), (T input) => InTime(() => property(input), CaseTime)));

    /// <summary>
    /// The longest one case may take. A case takes milliseconds, the first of a test a little longer while its code is
    /// compiled, so a case this slow is a reader that hangs, or slows down badly, on that input.
    /// </summary>
    public static readonly TimeSpan CaseTime = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Runs <paramref name="run"/> on a thread of its own and waits at most <paramref name="limit"/>: a case that never ends
    /// fails with a <see cref="TimeoutException"/> instead of holding up the test run, and a case that fails throws what
    /// it threw.
    /// </summary>
    internal static void InTime(Action run, TimeSpan limit)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { run(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        }) { IsBackground = true, Name = "property case" };
        thread.Start();
        if (!thread.Join(limit))
            throw new TimeoutException($"The case took longer than {limit.TotalSeconds:0.#} s: the reader hangs, or slows down badly, on it.");
        failure?.Throw();
    }

    /// <summary>
    /// How a property runs: <paramref name="cases"/> random cases from a new random seed each run; the first that fails fails
    /// the test, with the case. To make a failure again, copy the three numbers of its line "Replay directly at failing step
    /// with (seed,gamma,size)" into the environment variable FSCHECK_REPLAY, as "seed,gamma,size", and run that test alone
    /// (dotnet test --filter): its first case is then the one that failed.
    /// </summary>
    public static Config Cases(int cases)
    {
        var config = Config.QuickThrowOnFailure.WithMaxTest(cases).WithQuietOnSuccess(true);
        var replay = ReplayFrom(Environment.GetEnvironmentVariable("FSCHECK_REPLAY"));
        return replay is null ? config : config.WithReplay(global::Microsoft.FSharp.Core.FSharpOption<Replay>.Some(replay));
    }

    /// <summary>
    /// FSCHECK_REPLAY's seed, gamma and size, written as a failure prints them ("(1,3,7)", or without the brackets), or
    /// null when it is not set. Anything else in it (an even gamma too, which FsCheck never prints) stops the test, so a
    /// mistyped replay is never taken for a pass.
    /// </summary>
    internal static Replay? ReplayFrom(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string[] parts = text.Trim().TrimStart('(').TrimEnd('.', ')').Split(',');
        if (parts.Length == 3 &&
            ulong.TryParse(parts[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out ulong seed) &&
            ulong.TryParse(parts[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out ulong gamma) && gamma % 2 == 1 &&
            int.TryParse(parts[2].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int size))
            return new Replay(new Rnd(seed, gamma), global::Microsoft.FSharp.Core.FSharpOption<int>.Some(size));
        throw new InvalidOperationException($"FSCHECK_REPLAY is \"{text}\": it takes the three numbers a failure prints, as \"seed,gamma,size\".");
    }

    // ------------------------------------------------------------------ what changed input is made of

    /// <summary>Pieces of random text: what separates and quotes, characters a check refuses or that look like others, and a little JSON.</summary>
    private static readonly string[] Pieces =
    [
        "a", "Z", "7", "0", " ", ".", ",", ":", ";", "-", "_", "/", "\\", "\"", "'", "{", "}", "[", "]", "(", ")", "+", "*", "?",
        "<", ">", "|", "%", "&", "=", "#", "@", "v", "\t", "\n", "\r", "\0", "\u0001", "\u007F", "\u0085", "\u00A0", "\u00E9",
        "\u0130", "\u0660", "\uFF11", "\u200B", "\u200E", "\u202E", "\u2028", "\u212A", "\uE000", "\uFEFF", "\uFFFD", "\u4E2D",
        "\U0001F600", "\U000E0041", "C:\\", "\\\\?\\", "..", "true", "null", "-0", "1e400", "\\u0000", "\\ud800", "\\\"",
    ];

    /// <summary>Numbers a value may become: the edges of the ranges PoolWatch reads, and numbers no int, long or double holds.</summary>
    private static readonly string[] Numbers =
    [
        "0", "-0", "1", "-1", "2", "5", "15", "16", "59", "60", "61", "100", "300", "301", "3600", "0.5", "1.5e3", "1e-300", "1e308",
        "-1e308", "1e400", "2147483647", "2147483648", "-2147483649", "9223372036854775807", "9223372036854775808",
        "123456789012345678901234567890",
    ];

    /// <summary>
    /// Text a value may become, as JSON: empty, padded, a control or direction character, half of a surrogate pair written
    /// as an escape (JSON allows the escape; the text is not valid Unicode), paths of each kind, and a long one.
    /// </summary>
    private static readonly string[] Strings =
    [
        "\"\"", "\" \"", "\"x\"", "\" x \"", "\"X\"", "\"\\u0000\"", "\"\\n\"", "\"\\u202e\"", "\"\\ud800\"", "\"\\udc00x\"",
        "\"a\\ud83d\"", "\"\\ud83d\\ude00\"", "\"\\u00e9\"", "\"C:\\\\x\"", "\"D:/x\"", "\"C:x\"", "\"\\\\\\\\server\\\\share\"",
        "\"\\\\\\\\?\\\\C:\\\\x\"", "\"https://github.com/x\"", "\"1.0.0\"", "\"" + new string('a', 5000) + "\"",
    ];

    private static readonly string[] Flags = ["true", "false", "0", "1", "\"true\""];

    /// <summary>Other values: empty and odd lists and objects, deep ones, and a long list.</summary>
    private static readonly string[] Others =
    [
        "null", "true", "false", "[]", "{}", "[null]", "[\"\"]", "[1]", "[[]]", "{\"a\":null}", "{\"\":1}",
        string.Concat(Enumerable.Repeat("[", 20)) + string.Concat(Enumerable.Repeat("]", 20)),
        string.Concat(Enumerable.Repeat("{\"a\":", 20)) + "0" + string.Concat(Enumerable.Repeat("}", 20)),
        "[" + string.Join(",", Enumerable.Repeat("1", 600)) + "]",
    ];

    private static readonly string[] AllValues = [.. Numbers, .. Strings, .. Flags, .. Others];

    /// <summary>Bytes that are not UTF-8: a lone continuation byte, an over-long form, an encoded surrogate, a code point past Unicode, a cut sequence.</summary>
    private static readonly byte[][] NotUtf8 = [[0xFF], [0x80], [0xC0, 0x80], [0xED, 0xA0, 0x80], [0xF4, 0x90, 0x80, 0x80], [0xE2, 0x82]];

    /// <summary>Lengths and offsets at the edges, for binary files.</summary>
    private static readonly uint[] Edges = [0, 1, 2, 0x1B, 0x1C, 0x24, 0x4C, 0x7F, 0x80, 0xFF, 0x7FFF, 0x8000, 0xFFFF, 0x10000, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFF];

    // ------------------------------------------------------------------ generators

    /// <summary>Any text, as a file read as UTF-8 gives it (<see cref="AsRead"/>): of the pieces above, or of any characters.</summary>
    public static Gen<string> AnyText => Gen.OneOf(
        Gen.ArrayOf(Gen.Elements(Pieces)).Select(p => AsRead(string.Concat(p))),
        Gen.ArrayOf(Gen.Choose(0, 0xFFFF).Select(c => (char)c)).Select(c => AsRead(new string(c))));

    /// <summary>Any bytes.</summary>
    public static Gen<byte[]> AnyBytes => Gen.ArrayOf(Gen.Choose(0, 255).Select(b => (byte)b));

    /// <summary>
    /// A document from <paramref name="seeds"/> changed 1 to 4 times: mostly in its structure (a value of its kind at an edge,
    /// or of another kind, or one of <paramref name="extra"/>; a value taken out, replaced by a copy of another value of the
    /// document, or wrapped in a list; a field or an item added), and otherwise as text (cut short, a piece taken out,
    /// repeated or put in, a field written twice).
    /// </summary>
    public static Gen<string> Mutated(Gen<string> seeds, params string[] extra) =>
        from seed in seeds
        from r in Gen.Choose(0, int.MaxValue - 1)
        select Mutate(seed, new Random(r), extra, json: true);

    /// <summary>Text from <paramref name="seeds"/> changed 1 to 4 times as text only (for text that is not JSON).</summary>
    public static Gen<string> Edited(Gen<string> seeds) =>
        from seed in seeds
        from r in Gen.Choose(0, int.MaxValue - 1)
        select Mutate(seed, new Random(r), [], json: false);

    /// <summary>
    /// The text with spaces after it (JSON allows them there) to 1 to 16 bytes more than <paramref name="maxBytes"/> as UTF-8:
    /// a file or an answer just over a reader's limit, which it must refuse unread, however good the text before the spaces.
    /// </summary>
    public static Gen<string> JustOver(Gen<string> texts, int maxBytes) =>
        from text in texts
        from over in Gen.Choose(1, 16)
        select text + new string(' ', Math.Max(0, maxBytes + over - Encoding.UTF8.GetByteCount(text)));

    /// <summary>The text as UTF-8 bytes: as it is, with a byte order mark, with a byte changed, or with bytes that are not UTF-8.</summary>
    public static Gen<byte[]> AsBytes(Gen<string> texts) =>
        from text in texts
        from r in Gen.Choose(0, int.MaxValue - 1)
        select ChangeBytes(Encoding.UTF8.GetBytes(text), new Random(r));

    /// <summary>A binary file damaged 1 to 4 times: a byte changed or a bit flipped, a length or an offset set to an edge, or the file cut short.</summary>
    public static Gen<byte[]> Damaged(byte[] bytes) => Gen.Choose(0, int.MaxValue - 1).Select(r => Damage(bytes, new Random(r)));

    /// <summary>
    /// Text as reading a file as UTF-8 gives it: the decoder turns what is not UTF-8 into the replacement character, so
    /// text read from a file never holds half of a surrogate pair, which a string made in code can.
    /// </summary>
    public static string AsRead(string text) => Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(text));

    // ------------------------------------------------------------------ the changes

    private static string Mutate(string seed, Random rng, IReadOnlyList<string> extra, bool json)
    {
        string text = seed;
        for (int steps = rng.Next(1, 5); steps > 0; steps--)
            text = (json && rng.Next(3) > 0 ? InTree(text, rng, extra) : null) ?? AsText(text, rng, extra);
        return AsRead(text);
    }

    private static T Pick<T>(IReadOnlyList<T> list, Random rng) => list[rng.Next(list.Count)];

    /// <summary>A value as JSON text, often of the same kind as the one it replaces, sometimes null, sometimes one of <paramref name="extra"/>.</summary>
    private static string ValueFor(JsonValueKind kind, Random rng, IReadOnlyList<string> extra)
    {
        if (rng.Next(8) == 0) return "null";
        if (extra.Count > 0 && rng.Next(3) == 0) return Pick(extra, rng);
        string[] pool = rng.Next(4) == 0 ? AllValues : kind switch
        {
            JsonValueKind.Number => Numbers,
            JsonValueKind.String => Strings,
            JsonValueKind.True or JsonValueKind.False => Flags,
            _ => Others,
        };
        return Pick(pool, rng);
    }

    /// <summary>One change to the document's structure, or null when the text is not a JSON object or list any more.</summary>
    private static string? InTree(string json, Random rng, IReadOnlyList<string> extra)
    {
        try
        {
            var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var slots = new List<(JsonNode Parent, string? Name, int Index)>();
            if (root is not null) Collect(root, slots);
            if (root is null || slots.Count == 0) return null;
            // A new value goes in as a marker string, and the marker is swapped for its JSON text at the end: that text
            // may be what no JSON node can hold (a number too large, half of a surrogate pair).
            var values = new List<string>();
            JsonNode Value(JsonValueKind kind)
            {
                values.Add(ValueFor(kind, rng, extra));
                return JsonValue.Create("@@value" + (values.Count - 1) + "@@")!;
            }
            var (parent, name, index) = Pick(slots, rng);
            JsonNode? current = name is not null ? parent[name] : parent[index];
            JsonValueKind kind = current?.GetValueKind() ?? JsonValueKind.Null;
            void Set(JsonNode? value)
            {
                if (name is not null) parent[name] = value;
                else parent[index] = value;
            }
            switch (rng.Next(8))
            {
                case <= 3:
                    Set(Value(kind));
                    break;
                case 4:
                    if (name is not null) parent.AsObject().Remove(name);
                    else parent.AsArray().RemoveAt(index);
                    break;
                case 5:
                    var (p, n, i) = Pick(slots, rng);
                    Set((n is not null ? p[n] : p[i])?.DeepClone());
                    break;
                case 6:
                    Set(new JsonArray(current?.DeepClone()));
                    break;
                default:
                    if (name is null) parent.AsArray().Insert(index, Value(kind));
                    else parent[Pick(new[] { name.ToUpperInvariant(), name + "x", "", " " + name, Pick(slots, rng).Name ?? "x" }, rng)] = Value(kind);
                    break;
            }
            string text = root.ToJsonString();
            for (int v = 0; v < values.Count; v++) text = text.Replace("\"@@value" + v + "@@\"", values[v], StringComparison.Ordinal);
            return text;
        }
        catch (Exception)
        {
            return null; // the text is not JSON this change can read: it is changed as text instead
        }
    }

    /// <summary>Every place in the document that holds a value: each field of an object and each item of a list, at any depth.</summary>
    private static void Collect(JsonNode node, List<(JsonNode Parent, string? Name, int Index)> slots)
    {
        if (node is JsonObject o)
        {
            foreach (var (name, child) in o.ToList())
            {
                slots.Add((o, name, -1));
                if (child is not null) Collect(child, slots);
            }
        }
        else if (node is JsonArray a)
        {
            for (int i = 0; i < a.Count; i++)
            {
                slots.Add((a, null, i));
                if (a[i] is { } child) Collect(child, slots);
            }
        }
    }

    /// <summary>A field's name with its quotes and colon, as JSON text holds it.</summary>
    private static readonly Regex FieldName = new("\"(?:[^\"\\\\]|\\\\.){1,40}\"\\s*:");

    /// <summary>One change to the text.</summary>
    private static string AsText(string text, Random rng, IReadOnlyList<string> extra)
    {
        int at = rng.Next(text.Length + 1);
        switch (rng.Next(7))
        {
            case 0:
                return text.Remove(at, Math.Min(rng.Next(1, 16), text.Length - at));
            case 1:
                return text.Insert(at, Pick(Pieces, rng));
            case 2:
                return text.Insert(at, ValueFor(JsonValueKind.Undefined, rng, extra));
            case 3:
                return text.Insert(rng.Next(text.Length + 1), text.Substring(at, Math.Min(rng.Next(1, 40), text.Length - at)));
            case 4:
                return text[..at]; // cut short, as a power cut can leave a file
            case 5:
                return text.Remove(at, Math.Min(1, text.Length - at)).Insert(at, Pick(Pieces, rng));
            default:
                // A field written again at the start of an object, under a name the document uses: JSON allows it.
                var names = FieldName.Matches(text);
                int open = text.IndexOf('{', at);
                if (names.Count == 0 || open < 0) return text.Insert(at, Pick(Pieces, rng));
                return text.Insert(open + 1, names[rng.Next(names.Count)].Value + ValueFor(JsonValueKind.Undefined, rng, extra) + ",");
        }
    }

    private static byte[] ChangeBytes(byte[] bytes, Random rng)
    {
        var b = new List<byte>(bytes);
        switch (rng.Next(5))
        {
            case 0:
                b.InsertRange(0, [0xEF, 0xBB, 0xBF]); // a byte order mark
                break;
            case 1:
                if (b.Count > 0) b[rng.Next(b.Count)] = (byte)rng.Next(256);
                break;
            case 2:
                b.InsertRange(rng.Next(b.Count + 1), Pick(NotUtf8, rng));
                break;
        }
        return [.. b];
    }

    private static byte[] Damage(byte[] bytes, Random rng)
    {
        var b = (byte[])bytes.Clone();
        for (int steps = rng.Next(1, 5); steps > 0 && b.Length > 0; steps--)
        {
            int at = rng.Next(b.Length);
            switch (rng.Next(4))
            {
                case 0:
                    b[at] = (byte)rng.Next(256);
                    break;
                case 1:
                    uint edge = Pick(Edges, rng);
                    for (int i = 0; i < 4 && at + i < b.Length; i++) b[at + i] = (byte)(edge >> (8 * i));
                    break;
                case 2:
                    b = b[..at];
                    break;
                default:
                    b[at] ^= (byte)(1 << rng.Next(8));
                    break;
            }
        }
        return b;
    }
}
