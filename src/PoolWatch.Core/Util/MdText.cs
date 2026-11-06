using System.Globalization;
using System.Text;

namespace PoolWatch.Core.Util;

/// <summary>
/// Text from outside PoolWatch (process names, command lines, paths, version resources, signers, transcript ids, volume
/// labels) as it goes into a Markdown report: it stays plain text on its own line. It can start no heading, list, quote,
/// link, image, HTML tag, table cell, character reference, emphasis (bold or italic, with '*' or '_') or strikethrough
/// (with '~'), and it cannot end the code span it is in. A word that would
/// become a link, a mention or a reference by itself goes into a code span, where neither GitHub-flavoured Markdown nor
/// GitHub.com makes one (<see cref="MakesALink"/>): a web or e-mail address (a word with "://", "www." or an '@'), an
/// @mention (any word with an '@'), an issue or pull request reference ('#' or "GH-" followed by a digit, as in "#12",
/// "GH-12" or "owner/repo#12") and a commit hash (a run of 7 to 40 hexadecimal digits that stands on its own). Outside a
/// code span an '&' that could start a character reference is escaped, so "&amp;commat;" or "&amp;#x40;" shows as written
/// and never as the '@' or '#' it would name. A repository's own custom autolinks, which its owners set up for their own
/// tracker, are not known here. Line breaks and other control
/// characters become spaces, and every character Unicode lists as one a reader may not see is left out: the formatting
/// characters (category Cf: the bidi overrides, zero-width characters, the tag characters) and the rest of the
/// Default_Ignorable_Code_Point list (the variation selectors, the combining grapheme joiner, the Hangul fillers and the
/// like), those above U+FFFF included, so no text hides in a report where a reader sees nothing (<see cref="IsInvisible"/>).
/// </summary>
public static class MdText
{
    /// <summary>Outside a code span: links, images, code spans, HTML, table cells, character references, emphasis and strikethrough are escaped; a word that would link goes in a code span.</summary>
    public static string Text(string? s)
    {
        string t = Plain(s);
        if (t.Length == 0) return "";
        var sb = new StringBuilder(t.Length + 8);
        int i = 0;
        while (i < t.Length)
        {
            int end = i;
            while (end < t.Length && !EndsAWord(t[end])) end++;
            if (end > i && MakesALink(t.AsSpan(i, end - i)))
            {
                // Code spans are never made links, mentions or references (GitHub, CommonMark, the previews that link bare
                // addresses). The word holds no space or backtick here (Plain made controls spaces; Code makes a backtick ').
                sb.Append('`').Append(t.AsSpan(i, end - i).ToString().Replace('`', '\'')).Append('`');
                i = end;
                continue;
            }
            int stop = Math.Max(end, i + 1); // a word, or the one character that ends it
            for (; i < stop; i++)
            {
                char c = t[i];
                switch (c)
                {
                    // A backslash is literal in Markdown unless it escapes the next character. One that would (or that ends the
                    // text, before whatever follows it) is doubled, so it can never take back an escape added here.
                    case '\\': sb.Append(i + 1 == t.Length || IsAsciiPunctuation(t[i + 1]) ? @"\\" : @"\"); break;
                    case '|': sb.Append(@"\|"); break;
                    // Emphasis and strikethrough: a run of '*', '_' or '~' around a word makes it bold, italic or struck
                    // through, wherever it is on the line.
                    case '*': sb.Append(@"\*"); break;
                    case '_': sb.Append(@"\_"); break;
                    case '~': sb.Append(@"\~"); break;
                    case '[': sb.Append(@"\["); break;
                    case ']': sb.Append(@"\]"); break;
                    case '`': sb.Append(@"\`"); break;
                    case '<': sb.Append("&lt;"); break;
                    // A character reference ("&commat;", "&#x40;", "&#64;") shows as the character it names, outside a code
                    // span: an '&' that could start one (an ASCII letter or digit, or '#', follows it, or it ends the text,
                    // before whatever follows it) is written as the reference to '&' itself, so it shows as written. Every
                    // reference starts so: the names and the numbers are ASCII.
                    case '&': sb.Append(i + 1 == t.Length || t[i + 1] == '#' || char.IsAsciiLetterOrDigit(t[i + 1]) ? "&amp;" : "&"); break;
                    default: sb.Append(c); break;
                }
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// <see cref="Text"/> for text that starts a line: a leading heading, quote, list or rule marker is escaped too.
    /// </summary>
    public static string Line(string? s)
    {
        string t = Text(s).TrimStart(' ');
        if (t.Length == 0) return t;
        if (t[0] is '#' or '>' or '-' or '+' or '*' or '=' or '_' or '~') return "\\" + t;
        int digits = 0;
        while (digits < t.Length && char.IsAsciiDigit(t[digits])) digits++;
        if (digits > 0 && digits < t.Length && t[digits] is '.' or ')') return t[..digits] + "\\" + t[digits..];
        return t;
    }

    /// <summary>Inside a `code span`: nothing is interpreted there, so only what would end the span or the line changes (a backtick becomes ').</summary>
    public static string Code(string? s) => Plain(s).Replace('`', '\'');

    /// <summary>
    /// <see cref="Code"/> inside a table cell: a pipe there would end the cell even inside the code span, so it is escaped,
    /// with an odd number of backslashes before it whichever way the table's reader counts them.
    /// </summary>
    public static string CodeCell(string? s)
    {
        string code = Code(s);
        var sb = new StringBuilder(code.Length + 4);
        foreach (char c in code)
        {
            if (c == '|')
            {
                int run = 0;
                for (int j = sb.Length - 1; j >= 0 && sb[j] == '\\'; j--) run++;
                if (run % 2 == 1) sb.Append('\\');
                sb.Append(@"\|");
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// The text by Unicode character (a pair of surrogates is one): a control or line-separator character becomes a space,
    /// an invisible one (<see cref="IsInvisible"/>) is left out, and a surrogate without its pair becomes U+FFFD.
    /// </summary>
    private static string Plain(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length;)
        {
            Rune.DecodeFromUtf16(s.AsSpan(i), out Rune r, out int used); // an unpaired surrogate: Rune.ReplacementChar
            i += used;
            var cat = Rune.GetUnicodeCategory(r);
            if (IsInvisible(r.Value, cat)) continue;
            if (cat is UnicodeCategory.Control or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) sb.Append(' ');
            else sb.Append(r.ToString());
        }
        return sb.ToString();
    }

    /// <summary>
    /// A formatting character (Cf), or a code point in Unicode's Default_Ignorable_Code_Point list (DerivedCoreProperties.txt):
    /// one a reader is shown nothing for where it is not supported, so it could carry text no one sees.
    /// </summary>
    internal static bool IsInvisible(int codePoint, UnicodeCategory category) =>
        category == UnicodeCategory.Format || codePoint is 0x00AD or 0x034F or 0x061C or (>= 0x115F and <= 0x1160) or (>= 0x17B4 and <= 0x17B5)
            or (>= 0x180B and <= 0x180F) or (>= 0x200B and <= 0x200F) or (>= 0x202A and <= 0x202E) or (>= 0x2060 and <= 0x206F) or 0x3164
            or (>= 0xFE00 and <= 0xFE0F) or 0xFEFF or 0xFFA0 or (>= 0xFFF0 and <= 0xFFF8) or (>= 0x1BCA0 and <= 0x1BCA3)
            or (>= 0x1D173 and <= 0x1D17A) or (>= 0xE0000 and <= 0xE0FFF);

    /// <summary>Where a word ends for <see cref="MakesALink"/>: white space, or a pipe (which stays outside a code span, escaped).</summary>
    private static bool EndsAWord(char c) => char.IsWhiteSpace(c) || c == '|';

    /// <summary>
    /// A word that GitHub-flavoured Markdown or GitHub.com could turn into a link by itself: one holding "://" (a web
    /// address) or "www." (any case); one holding an '@' (an e-mail address, a "mailto:" or "xmpp:" one, an @mention of an
    /// account or a team, a commit reference such as "user@hash"); one with '#' followed by a digit anywhere in it (an issue
    /// or pull request: "#12", "owner/repo#12") or "GH-" followed by a digit (any case); and one with a run of 7 to 40
    /// hexadecimal digits that no other letter, digit or underscore joins (a commit hash, which GitHub links when the
    /// repository has a commit that starts so).
    /// </summary>
    private static bool MakesALink(ReadOnlySpan<char> word)
    {
        if (word.IndexOf("://", StringComparison.Ordinal) >= 0 || word.IndexOf("www.", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (word.IndexOf('@') >= 0) return true;
        for (int i = 0; i + 1 < word.Length; i++)
            if (word[i] == '#' && char.IsAsciiDigit(word[i + 1])) return true;
        for (int i = 0; i + 3 < word.Length; i++)
            if (word.Slice(i, 3).Equals("GH-", StringComparison.OrdinalIgnoreCase) && char.IsAsciiDigit(word[i + 3])) return true;
        for (int i = 0; i < word.Length;)
        {
            if (!char.IsAsciiHexDigit(word[i])) { i++; continue; }
            int start = i;
            while (i < word.Length && char.IsAsciiHexDigit(word[i])) i++;
            bool alone = (start == 0 || !IsWordCharacter(word[start - 1])) && (i == word.Length || !IsWordCharacter(word[i]));
            if (alone && i - start is >= 7 and <= 40) return true;
        }
        return false;
    }

    private static bool IsWordCharacter(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static bool IsAsciiPunctuation(char c) => c is >= '!' and <= '/' or >= ':' and <= '@' or >= '[' and <= '`' or >= '{' and <= '~';
}
