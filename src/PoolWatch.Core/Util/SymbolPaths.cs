namespace PoolWatch.Core.Util;

/// <summary>One access rule of a folder's DACL, as <see cref="SymbolPaths.AdminOnly"/> needs it.</summary>
/// <param name="Sid">The trustee's SID string.</param>
/// <param name="Allow">An allow rule (deny rules never grant anything).</param>
/// <param name="Rights">The FileSystemRights bits.</param>
public readonly record struct FolderRule(string Sid, bool Allow, int Rights);

/// <summary>
/// The symbol folders PoolWatch may read to name kernel functions in a CPU trace (addendum 2 C). PoolWatch runs elevated
/// and the symbol reader parses the PDB files it finds, so (Part 3 review) a folder counts only when it is local (no
/// symbol server, no share, no mapped network drive) and admin-only: its owner is Administrators / SYSTEM /
/// TrustedInstaller and nobody else may write into it. `_NT_SYMBOL_PATH` is not used: an unelevated process can set it for
/// the user, and the usual C:\symbols is writable by any user. The default folder is `symbols` inside PoolWatch's own
/// admin-only data root.
/// </summary>
public static class SymbolPaths
{
    private const string Administrators = "S-1-5-32-544", System = "S-1-5-18",
        TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464", CreatorOwner = "S-1-3-0";

    // FileSystemRights that let a trustee change what is in a folder or its rules: write / append data (create files and
    // folders), write attributes and extended attributes, delete, delete children, change permissions, take ownership,
    // and the generic GENERIC_ALL / GENERIC_WRITE bits an inheritable rule may carry.
    private const int WriteRights = 0x2 | 0x4 | 0x10 | 0x100 | 0x40 | 0x10000 | 0x40000 | 0x80000 | 0x10000000 | 0x40000000;

    /// <summary>
    /// The folder may be trusted by an elevated process: owned by Administrators, SYSTEM or TrustedInstaller, and no allow rule
    /// gives write access to anyone else (CREATOR OWNER counts as its owner, already checked).
    /// </summary>
    public static bool AdminOnly(string ownerSid, IEnumerable<FolderRule> rules)
    {
        static bool Trusted(string sid) => sid is Administrators or System or TrustedInstaller;
        if (!Trusted(ownerSid)) return false;
        foreach (var r in rules)
            if (r.Allow && (r.Rights & WriteRights) != 0 && !Trusted(r.Sid) && r.Sid != CreatorOwner) return false;
        return true;
    }

    /// <summary>The usable folders of a ';'-separated list (local drive paths only; <paramref name="usable"/> checks the rest), ';'-joined; null when none.</summary>
    public static string? LocalOnly(string? symbolPath, Func<string, bool> usable)
    {
        if (string.IsNullOrWhiteSpace(symbolPath)) return null;
        var dirs = new List<string>();
        foreach (var element in symbolPath.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            foreach (var raw in element.Split('*', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string part = raw.Trim('"');
                if (part.Equals("srv", StringComparison.OrdinalIgnoreCase) || part.Equals("symsrv", StringComparison.OrdinalIgnoreCase) ||
                    part.Equals("cache", StringComparison.OrdinalIgnoreCase) || part.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                if (part.Contains("://", StringComparison.Ordinal) || part.StartsWith(@"\\", StringComparison.Ordinal) || part.StartsWith("//", StringComparison.Ordinal)) continue;
                if (part.Length < 3 || part[1] != ':' || part[2] is not ('\\' or '/') || !char.IsAsciiLetter(part[0])) continue; // a drive path
                if (!dirs.Contains(part, StringComparer.OrdinalIgnoreCase) && usable(part)) dirs.Add(part);
            }
        return dirs.Count == 0 ? null : string.Join(';', dirs);
    }

    /// <summary>The setting's usable folders, else the default folder (PoolWatch's data root\symbols) when usable; null when none.</summary>
    /// <param name="usable">Exists, is on a local (not network) drive, and is admin-only.</param>
    public static string? Resolve(string? setting, string defaultFolder, Func<string, bool> usable) =>
        LocalOnly(setting, usable) ?? (usable(defaultFolder) ? defaultFolder : null);
}
