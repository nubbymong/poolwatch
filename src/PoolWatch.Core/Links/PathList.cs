namespace PoolWatch.Core.Links;

/// <summary>Whose PATH or environment variable: the account's own (HKCU\Environment) or the machine's (HKLM).</summary>
public enum EnvScope { User, Machine }

/// <summary>The words a change record keeps for a scope ("user", "machine"), and back.</summary>
public static class EnvScopes
{
    public const string User = "user", Machine = "machine";

    public static string Word(EnvScope scope) => scope == EnvScope.User ? User : Machine;

    /// <summary>The scope a record's word names; null for any other word, so a record of another kind is refused.</summary>
    public static EnvScope? Parse(string? word) => word switch
    {
        User => EnvScope.User,
        Machine => EnvScope.Machine,
        _ => null,
    };
}

/// <summary>
/// A PATH value as Windows keeps it: entries separated by ";", each kept exactly as written (spaces, quotes, %NAME%
/// variables and empty entries included), so changing one entry leaves every other character as it was. Pure: the
/// tests give it any text.
/// </summary>
public static class PathList
{
    /// <summary>The most entries PoolWatch looks at in one PATH value.</summary>
    public const int MaxEntries = 1024;

    /// <summary>The longest value PoolWatch writes back: an environment variable holds at most 32,767 characters.</summary>
    public const int MaxChars = 32767;

    /// <summary>The entries, in order, each exactly as written. <see cref="Join"/> gives the value back exactly.</summary>
    public static string[] Split(string value) => value.Split(';');

    public static string Join(IEnumerable<string> entries) => string.Join(';', entries);

    /// <summary>
    /// <paramref name="value"/> with the entry at <paramref name="index"/> replaced by <paramref name="entry"/> and every
    /// other character as it was; null when there is no entry there, the entry there is not <paramref name="expected"/>
    /// exactly (the PATH changed since it was shown), or the new value would be longer than <see cref="MaxChars"/>.
    /// </summary>
    public static string? Replace(string value, int index, string expected, string entry)
    {
        var parts = Split(value);
        if (index < 0 || index >= parts.Length || parts[index] != expected) return null;
        parts[index] = entry;
        string next = Join(parts);
        return next.Length <= MaxChars ? next : null;
    }

    /// <summary>
    /// Put back: <paramref name="value"/> with the entry PoolWatch wrote (<paramref name="written"/>) replaced by the one
    /// that was there before (<paramref name="original"/>), every other character as it was. The entry is looked for at
    /// <paramref name="index"/> first, where PoolWatch wrote it, then anywhere, but only when it is there exactly once (a
    /// tool may have added entries in front of it since). Null when it is not there, or is there more than once elsewhere:
    /// then there is nothing PoolWatch can tell is its own.
    /// </summary>
    public static string? PutBack(string value, int index, string written, string original)
    {
        var parts = Split(value);
        int at = index >= 0 && index < parts.Length && parts[index] == written ? index : -1;
        if (at < 0)
        {
            var found = Enumerable.Range(0, parts.Length).Where(i => parts[i] == written).Take(2).ToList();
            if (found.Count != 1) return null;
            at = found[0];
        }
        parts[at] = original;
        string next = Join(parts);
        return next.Length <= MaxChars ? next : null;
    }
}

/// <summary>Pure checks on Windows paths given as text ("C:\Users\..."), for the link finder: the same on any OS.</summary>
public static class LinkPaths
{
    /// <summary>An entry as a program reads it: the spaces around it trimmed, and one pair of quotes around all of it removed.</summary>
    public static string Unquote(string entry)
    {
        string t = entry.Trim();
        return t.Length >= 2 && t[0] == '"' && t[^1] == '"' ? t[1..^1].Trim() : t;
    }

    /// <summary>
    /// Whether <paramref name="path"/> is below <paramref name="root"/> (not the root itself), by its text, without regard
    /// to case. Both are full paths with "\" between folders, as the app gives them (already normalized).
    /// </summary>
    public static bool Under(string path, string root)
    {
        string r = root.TrimEnd('\\'), p = path.TrimEnd('\\');
        return r.Length > 0 && p.Length > r.Length + 1 && p.StartsWith(r, StringComparison.OrdinalIgnoreCase) && p[r.Length] == '\\';
    }

    /// <summary>
    /// The folders from the first one below <paramref name="root"/> down to <paramref name="path"/>, each as a full path,
    /// top first: where a link on the way can be. Empty when the path is not below the root, or has an empty, "." or ".."
    /// part (the app normalizes paths first, so such a path is not one it looks at).
    /// </summary>
    public static IReadOnlyList<string> Levels(string path, string root)
    {
        if (!Under(path, root)) return [];
        string at = root.TrimEnd('\\');
        var levels = new List<string>();
        foreach (string part in path.TrimEnd('\\')[(at.Length + 1)..].Split('\\'))
        {
            if (part is "" or "." or "..") return [];
            at += "\\" + part;
            levels.Add(at);
        }
        return levels;
    }

    /// <summary>
    /// Text PoolWatch may write as a PATH entry or an environment variable's value: a plain path on a local drive
    /// (<see cref="Settings.IsLocalDrivePath"/>: no wildcard, quote, angle bracket, bar, further colon or control
    /// character, at most 260 characters), with no ";" (it would split the entry in two) and no "%" (it would be read as
    /// a variable).
    /// </summary>
    public static bool Writable(string? path) => Settings.IsLocalDrivePath(path) && path!.IndexOfAny([';', '%']) < 0;

    /// <summary>
    /// <paramref name="path"/> with the <paramref name="profile"/> folder at its start written as %USERPROFILE%, for text
    /// that leaves the PC (the AI agent's prompt): no user name. Any other path is left as it is.
    /// </summary>
    public static string InProfileWords(string path, string profile)
    {
        string p = profile.TrimEnd('\\');
        if (p.Length == 0) return path;
        if (path.Equals(p, StringComparison.OrdinalIgnoreCase)) return "%USERPROFILE%";
        return Under(path, p) ? "%USERPROFILE%" + path[p.Length..] : path;
    }
}

/// <summary>Which of the apps whose package context has the leak are installed, from the account's Store package names.</summary>
public static class LinkApps
{
    /// <summary>
    /// The Claude desktop app's package: its name (before the first "_" of the full name) is Claude, or starts with
    /// Anthropic, as the knowledge packs name it.
    /// </summary>
    public static bool IsClaude(string fullName)
    {
        string name = Name(fullName);
        return name.Equals("Claude", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Anthropic", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The Codex desktop app's package: its name starts with OpenAI.Codex.</summary>
    public static bool IsCodex(string fullName) => Name(fullName).StartsWith("OpenAI.Codex", StringComparison.OrdinalIgnoreCase);

    private static string Name(string fullName)
    {
        int cut = fullName.IndexOf('_');
        return cut < 0 ? fullName : fullName[..cut];
    }
}
