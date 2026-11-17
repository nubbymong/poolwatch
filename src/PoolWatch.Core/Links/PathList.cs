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

    /// <summary>
    /// <paramref name="value"/> without the entry at <paramref name="index"/>, every other character as it was; null when
    /// there is no entry there, the entry there is not <paramref name="expected"/> exactly, or it is the only entry (an
    /// empty PATH could not be put back exactly).
    /// </summary>
    public static string? Remove(string value, int index, string expected)
    {
        var parts = Split(value).ToList();
        if (parts.Count < 2 || index < 0 || index >= parts.Count || parts[index] != expected) return null;
        parts.RemoveAt(index);
        return Join(parts);
    }

    /// <summary>
    /// Put back of a removed entry: <paramref name="value"/> with <paramref name="entry"/> again at <paramref name="index"/>,
    /// only while the value is exactly the one PoolWatch wrote (its <see cref="Hash"/> is <paramref name="written"/>), so
    /// the PATH comes back exactly as it was. Null otherwise: the PATH changed since, and nothing is put back.
    /// </summary>
    public static string? Insert(string value, int index, string entry, string written)
    {
        if (!Hash(value).Equals(written, StringComparison.OrdinalIgnoreCase)) return null;
        var parts = Split(value).ToList();
        if (index < 0 || index > parts.Count) return null;
        parts.Insert(index, entry);
        string next = Join(parts);
        return next.Length <= MaxChars ? next : null;
    }

    /// <summary>The SHA-256 of a PATH value's text (UTF-8), in hex: what a removal records of the value it wrote.</summary>
    public static string Hash(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
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

    /// <summary>"Claude" or "Codex" for the package of one of the two apps, else null.</summary>
    public static string? AppOf(string fullName) => IsClaude(fullName) ? Claude : IsCodex(fullName) ? Codex : null;

    public const string Claude = "Claude", Codex = "Codex";

    private static string Name(string fullName)
    {
        int cut = fullName.IndexOf('_');
        return cut < 0 ? fullName : fullName[..cut];
    }

    /// <summary>
    /// The package family name of a package full name ("Claude_5.11492.5.0_x64__pzs8sxrjxfjjc" gives
    /// "Claude_pzs8sxrjxfjjc"): the folder of its own copy of AppData under %LOCALAPPDATA%\Packages. The full name is a
    /// registry key's name the account can write, so it is read strictly: five parts split by "_", a name of letters,
    /// digits, "." and "-" (3 to 50 of them), and a publisher id of 13 lower-case letters and digits. Null for any other
    /// text, so the family name is always one plain folder name.
    /// </summary>
    public static string? Family(string? fullName)
    {
        if (fullName is null || fullName.Length > 200) return null;
        var parts = fullName.Split('_');
        if (parts.Length != 5) return null;
        string name = parts[0], publisher = parts[4];
        bool nameOk = name.Length is >= 3 and <= 50 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-') && name.Any(char.IsAsciiLetterOrDigit);
        bool publisherOk = publisher.Length == 13 && publisher.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'z');
        return nameOk && publisherOk ? name + "_" + publisher : null;
    }

    /// <summary>
    /// "Claude" or "Codex" for a program of the Claude or Codex desktop app, by where its file is: in the app's package
    /// folder (WindowsApps\Claude_..., WindowsApps\OpenAI.Codex_...), or a Claude Code engine the Claude app keeps in
    /// %APPDATA%\Claude\claude-code (<paramref name="appData"/>: the account's %APPDATA%). Null for any other program.
    /// </summary>
    public static string? AppOfProgram(string path, string appData)
    {
        string p = path.Replace('/', '\\');
        const string Apps = @"\WindowsApps\";
        int at = p.IndexOf(Apps, StringComparison.OrdinalIgnoreCase);
        if (at >= 0)
        {
            string rest = p[(at + Apps.Length)..];
            int cut = rest.IndexOf('\\');
            return cut > 0 ? AppOf(rest[..cut]) : null;
        }
        return appData.Length > 0 && LinkPaths.Under(p, appData.TrimEnd('\\') + @"\Claude\claude-code") ? Claude : null;
    }

    /// <summary>The tool that made a venv, when its folder shows it: PlatformIO's (.platformio\penv), Hermes Agent's; else null.</summary>
    public static string? ToolOf(string venvFolder) =>
        venvFolder.Contains(@"\.platformio\", StringComparison.OrdinalIgnoreCase) || venvFolder.EndsWith(@"\.platformio", StringComparison.OrdinalIgnoreCase) ? "PlatformIO"
        : venvFolder.Contains("hermes", StringComparison.OrdinalIgnoreCase) ? "Hermes Agent"
        : null;
}
