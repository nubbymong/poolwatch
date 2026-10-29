using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PoolWatch.Core.Updates;

/// <summary>
/// A semantic version (semver.org 2.0.0): MAJOR.MINOR.PATCH, an optional pre-release ("-rc.1") and optional build
/// metadata ("+78b1fec", dropped: it never changes the order), written with or without the leading "v" of a release tag.
/// Ordered by SemVer precedence, so 1.10.0 is after 1.9.0 and 1.2.0-rc.1 is before 1.2.0.
/// </summary>
public sealed partial record SemVersion(int Major, int Minor, int Patch, string PreRelease = "") : IComparable<SemVersion>
{
    /// <summary>Longer text is refused before it is parsed (a tag from GitHub, the running assembly's version).</summary>
    public const int MaxLength = 128;

    /// <summary>A pre-release ("1.2.0-rc.1"): never offered as an update.</summary>
    public bool IsPreRelease => PreRelease.Length > 0;

    public static bool TryParse([NotNullWhen(true)] string? text, [NotNullWhen(true)] out SemVersion? version)
    {
        version = null;
        if (text is null || text.Length > MaxLength) return false;
        var m = Grammar().Match(text);
        if (!m.Success || !Number(m.Groups["major"].Value, out int major) || !Number(m.Groups["minor"].Value, out int minor) ||
            !Number(m.Groups["patch"].Value, out int patch)) return false;
        version = new SemVersion(major, minor, patch, m.Groups["pre"].Value);
        return true;

        static bool Number(string s, out int n) => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out n);
    }

    public int CompareTo(SemVersion? other)
    {
        if (other is null) return 1;
        int c = Major.CompareTo(other.Major);
        if (c == 0) c = Minor.CompareTo(other.Minor);
        if (c == 0) c = Patch.CompareTo(other.Patch);
        return c != 0 ? c : ComparePreRelease(PreRelease, other.PreRelease);
    }

    public override string ToString() => IsPreRelease ? $"{Major}.{Minor}.{Patch}-{PreRelease}" : $"{Major}.{Minor}.{Patch}";

    /// <summary>
    /// semver.org rule 11: a release is after its pre-releases; otherwise the dot-separated identifiers are compared in
    /// turn, numbers by value and before words, words in ASCII order, and a longer list is after a shorter one it starts with.
    /// </summary>
    private static int ComparePreRelease(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return b.Length.CompareTo(a.Length);
        string[] x = a.Split('.'), y = b.Split('.');
        for (int i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            bool xNumber = x[i].All(char.IsAsciiDigit), yNumber = y[i].All(char.IsAsciiDigit);
            int c = (xNumber, yNumber) switch
            {
                // The grammar forbids leading zeros, so the longer number is the larger one (and no number overflows).
                (true, true) => x[i].Length != y[i].Length ? x[i].Length.CompareTo(y[i].Length) : string.CompareOrdinal(x[i], y[i]),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(x[i], y[i]),
            };
            if (c != 0) return Math.Sign(c);
        }
        return x.Length.CompareTo(y.Length);
    }

    /// <summary>The grammar from semver.org with ASCII digits only (\d also matches other scripts' digits), an optional
    /// "v", and \z so a trailing newline does not match.</summary>
    [GeneratedRegex(@"^[vV]?(?<major>0|[1-9][0-9]*)\.(?<minor>0|[1-9][0-9]*)\.(?<patch>0|[1-9][0-9]*)" +
                    @"(?:-(?<pre>(?:0|[1-9][0-9]*|[0-9]*[a-zA-Z-][0-9a-zA-Z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[a-zA-Z-][0-9a-zA-Z-]*))*))?" +
                    @"(?:\+[0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*)?\z")]
    private static partial Regex Grammar();
}

/// <summary>PoolWatch's release pages on GitHub: the only address an update notice may open.</summary>
public static class ReleasePage
{
    public const string Prefix = "https://github.com/nubbymong/poolwatch/releases/";
    public const int MaxLength = 200;
    private const string TagPrefix = Prefix + "tag/";

    /// <summary>The page of the release tagged <paramref name="tag"/> ("v1.2.0"), as GitHub writes it.</summary>
    public static string For(string tag) => TagPrefix + tag;

    /// <summary>
    /// One release's page: "tag/" under <see cref="Prefix"/>, then a tag of plain characters with no "/" or "..". So never
    /// a download or another page of the releases, no query, no escapes, and no quote or space for explorer.exe's
    /// command line.
    /// </summary>
    public static bool IsValid(string? url) =>
        url is not null && url.Length > TagPrefix.Length && url.Length <= MaxLength && url.StartsWith(TagPrefix, StringComparison.Ordinal) &&
        url[TagPrefix.Length..].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_') &&
        !url.Contains("..", StringComparison.Ordinal);
}
