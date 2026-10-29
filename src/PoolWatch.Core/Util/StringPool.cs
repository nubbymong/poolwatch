using System.Collections.Concurrent;

namespace PoolWatch.Core.Util;

/// <summary>
/// One shared instance per distinct image name, path, user and short command line. A busy PC starts the same
/// "git status --porcelain" and "C:\Program Files\Git\usr\bin\bash.exe" thousands of times an hour; every process
/// record and lifecycle event held its own copy. Strings over <see cref="MaxLength"/> characters are not pooled: an
/// agent's long shell scripts are nearly all different, and pooled they outlived their records. Bounded: past
/// <see cref="Max"/> entries the pool starts over (strings already shared stay shared). Thread-safe.
/// </summary>
public static class StringPool
{
    public const int Max = 20_000;
    public const int MaxLength = 256;
    private static readonly ConcurrentDictionary<string, string> Pool = new(StringComparer.Ordinal);

    public static string? Intern(string? s)
    {
        if (s is null || s.Length == 0 || s.Length > MaxLength) return s;
        if (Pool.TryGetValue(s, out var shared)) return shared;
        if (Pool.Count >= Max) Pool.Clear();
        return Pool.GetOrAdd(s, s);
    }

    public static int Count => Pool.Count;
}
