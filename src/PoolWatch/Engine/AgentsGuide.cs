using System.Text;

namespace PoolWatch.Engine;

/// <summary>
/// AGENTS.md, the guide for a user's AI assistant (Assets\AGENTS.md, embedded in the exe): what PoolWatch's files hold,
/// how to read them and what never to do with them. It goes into the data folder, at the root of every export and
/// vendor bundle, and next to the exe (the installer). It describes PoolWatch, never this PC, so it is never redacted.
/// </summary>
internal static class AgentsGuide
{
    public const string FileName = "AGENTS.md";

    /// <summary>The embedded file.</summary>
    public static string Text { get; } = Load();

    private static string Load()
    {
        using var stream = typeof(AgentsGuide).Assembly.GetManifestResourceStream(FileName)
            ?? throw new InvalidOperationException(FileName + " is not embedded in the exe");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Writes the guide into the data root when it is missing or different (an older version), through the write policy,
    /// to a temporary file first. Never throws: a failure is logged.
    /// </summary>
    public static void EnsureInDataRoot(AppPaths paths, Log log)
    {
        try
        {
            string file = paths.Guard.CheckReal(Path.Combine(paths.DataRoot, FileName));
            byte[] wanted = new UTF8Encoding(false).GetBytes(Text);
            // Compared by size first: an unchanged file is read once, a changed size is never read at all.
            var existing = new FileInfo(file);
            if (existing.Exists && existing.Length == wanted.Length && File.ReadAllBytes(file).AsSpan().SequenceEqual(wanted)) return;
            File.WriteAllBytes(paths.Guard.CheckReal(file + ".tmp"), wanted);
            File.Move(paths.Guard.CheckReal(file + ".tmp"), file, overwrite: true);
            log.Info($"{FileName} written to the data folder (the guide for AI assistants)");
        }
        catch (Exception ex) { log.Info($"{FileName} not written: {ex.Message}"); }
    }
}
