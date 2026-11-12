using System.Globalization;
using System.Text.Json;
using PoolWatch.Core.Changes;
using PoolWatch.Native;

namespace PoolWatch.Engine;

/// <summary>
/// The live ForegroundLockTimeout as it was before the token-leak guard first set it to 0 in this sign-in, so that turning
/// the guard off puts it back, when Windows allows it: in the PoolWatch that set it, in a later PoolWatch in the same
/// sign-in (after an upgrade, an exit, a crash), and in the uninstaller's <c>--uninstall-cleanup</c> step. It is kept in
/// memory and in <see cref="FileName"/> in the guard's admin-only folder, with the sign-in it belongs to
/// (<see cref="TokenGuardNative.SignInId"/>: the session and when its user signed in). Windows sets the live value again
/// at each sign-in, so only this sign-in's value is ever put back. The record holds one value per sign-in, so a second
/// sign-in at the same time (fast user switching) neither reads nor removes the first one's; at most
/// <see cref="MaxSignIns"/> are kept, the newest. The first value of a sign-in is kept: what the guard replaced later
/// (after another program changed the setting) is not what the sign-in had. Thread-safe.
/// </summary>
internal sealed class TokenGuardSetting
{
    internal const string FileName = "token-guard-setting.json";
    /// <summary>The record is never larger than this: a larger one is not read, and is deleted.</summary>
    internal const int MaxLength = 4096;
    /// <summary>Sign-ins kept in the record, the newest: an older one ended long ago, and its value is never put back.</summary>
    internal const int MaxSignIns = 8;
    private readonly Func<string> _file;
    private readonly Func<string, string> _real;
    private readonly Func<string?> _signIn;
    private readonly Func<uint?> _getLive;
    private readonly Func<uint, bool> _setTo;
    private readonly Log _log;
    private readonly object _lock = new();
    private uint? _before;
    private bool _read;

    /// <param name="file">The record's path (the guard's folder made first).</param>
    /// <param name="real">Checks a path before it is used (<see cref="Core.Util.PathGuard.CheckReal"/>: it looks for a
    /// link on the path, and throws when it finds one or cannot check).</param>
    /// <param name="signIn">This sign-in (default <see cref="TokenGuardNative.SignInId"/>); null when Windows does not say,
    /// and then no record is written or used, only the value in memory.</param>
    /// <param name="getLive">Reads the live timeout.</param>
    /// <param name="setTo">Sets the live timeout (true when Windows accepted).</param>
    public TokenGuardSetting(Func<string> file, Func<string, string> real, Func<string?> signIn, Func<uint?> getLive, Func<uint, bool> setTo, Log log)
    {
        _file = file;
        _real = real;
        _signIn = signIn;
        _getLive = getLive;
        _setTo = setTo;
        _log = log;
    }

    /// <summary>The command line's (the uninstaller's step): the record in the data root's guard folder, Windows' own calls.</summary>
    public static TokenGuardSetting ForCommandLine(AppPaths paths, Log log) =>
        new(() => Path.Combine(TokenLeakGuard.GuardDirOf(paths), FileName), paths.Guard.CheckReal, TokenGuardNative.SignInId,
            TokenGuardNative.GetForegroundLockTimeout, TokenGuardNative.SetForegroundLockTimeout, log);

    /// <summary>The value this sign-in had before the guard first set 0, or null when the guard has not set it in this sign-in.</summary>
    public uint? Replaced
    {
        get
        {
            lock (_lock)
            {
                if (!_read)
                {
                    _read = true;
                    _before ??= Load();
                }
                return _before;
            }
        }
    }

    /// <summary>
    /// The guard has just set the live value to 0 from <paramref name="was"/> (not 0): remembered, and saved with this
    /// sign-in, unless this sign-in has a value already.
    /// </summary>
    public void Remember(uint was)
    {
        if (was == 0) return;
        lock (_lock)
        {
            if (Replaced is not null) return;
            _before = was;
            Save(was);
        }
    }

    /// <summary>
    /// Puts the value this sign-in had back (the guard is off): only while the live value is still 0, so a change another
    /// program made since is never undone. Returns the sentence that says what happened. Kept for a later try when Windows
    /// does not accept the change or does not report the live value.
    /// </summary>
    public string PutBack()
    {
        lock (_lock)
        {
            uint? before = Replaced, live = _getLive();
            if (before is null) return live == 0 ? ChangeWords.TokenGuardOffNotOurs : ChangeWords.TokenGuardOff;
            if (live is null)
            {
                _log.Info("token guard: off; Windows did not report the live foreground lock timeout, so it was not put back now");
                return ChangeWords.TokenGuardOffUnreported;
            }
            if (live != 0)
            {
                _log.Info($"token guard: off; the live foreground lock timeout is {live} ms, set since by something else, so it is left as it is");
                Forget();
                return ChangeWords.TokenGuardOff;
            }
            bool back;
            try { back = _setTo(before.Value); }
            catch (Exception ex) { _log.Info("token guard: putting the setting back failed: " + ex.Message); back = false; }
            _log.Info(back ? $"token guard: off; the live foreground lock timeout is back to {before} ms" : "token guard: off; Windows refused to put the live foreground lock timeout back");
            if (!back) return ChangeWords.TokenGuardOffRefused;
            Forget();
            return ChangeWords.TokenGuardOffPutBack;
        }
    }

    /// <summary>The guard set the live value in this sign-in and it is still 0: the guard's change is still in effect.</summary>
    public bool StillChanged => Replaced is not null && _getLive() == 0;

    /// <summary>
    /// The live value is 0 and the guard has no record of setting it in this sign-in: turning the guard off leaves it until
    /// sign-out (<see cref="ChangeWords.TokenGuardSettingStays"/>).
    /// </summary>
    public bool StaysAfterTurnOff => Replaced is null && _getLive() == 0;

    /// <summary>This sign-in's value is gone (put back, or set since by something else): its entry leaves the record.</summary>
    private void Forget()
    {
        _before = null;
        try
        {
            if (_signIn() is not { } signIn) return;
            var entries = ReadAll();
            if (entries.Remove(signIn)) WriteAll(entries, null);
        }
        catch (Exception ex) { _log.Info("token guard: the record of the setting it replaced was not updated: " + ex.Message); }
    }

    /// <summary>This sign-in's value in the record, or null (no sign-in Windows will say, none saved, or none readable).</summary>
    private uint? Load()
    {
        try
        {
            return _signIn() is { } signIn && ReadAll().TryGetValue(signIn, out var entry) ? entry.Before : null;
        }
        catch (Exception ex)
        {
            _log.Info("token guard: the setting it replaced was not read: " + ex.Message);
            return null;
        }
    }

    private void Save(uint before)
    {
        try
        {
            if (_signIn() is not { } signIn) return;
            var entries = ReadAll();
            entries.Remove(signIn);
            WriteAll(entries, (signIn, before, DateTime.UtcNow));
        }
        catch (Exception ex) { _log.Info("token guard: the setting it replaced was not saved (only this PoolWatch can put it back): " + ex.Message); }
    }

    /// <summary>
    /// The record's entries, sign-in to value: read as untrusted, at most <see cref="MaxLength"/> bytes (a larger file is
    /// deleted), and only entries in the form <see cref="WriteAll"/> writes with a value that is not 0; empty when there is
    /// no record or it is not in that form.
    /// </summary>
    private Dictionary<string, (uint Before, DateTime SavedUtc)> ReadAll()
    {
        var entries = new Dictionary<string, (uint Before, DateTime SavedUtc)>(StringComparer.Ordinal);
        string file = _real(_file());
        if (!File.Exists(file)) return entries;
        if (new FileInfo(file).Length > MaxLength)
        {
            _log.Info("token guard: the record of the setting it replaced is too large; it was deleted");
            Delete();
            return entries;
        }
        JsonDocument doc;
        try { doc = JsonDocument.Parse(File.ReadAllText(file)); }
        catch (JsonException) { return entries; } // not in the form it writes: ignored, and the next save writes over it
        using var _ = doc;
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("signIns", out var list) || list.ValueKind != JsonValueKind.Array)
            return entries;
        foreach (var e in list.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("signIn", out var s) || s.ValueKind != JsonValueKind.String || s.GetString() is not { Length: > 0 } signIn
                || !e.TryGetProperty("before", out var b) || b.ValueKind != JsonValueKind.Number || !b.TryGetUInt32(out uint before) || before == 0)
                continue;
            var saved = e.TryGetProperty("savedUtc", out var t) && t.ValueKind == JsonValueKind.String
                && DateTime.TryParse(t.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when) ? when : DateTime.MinValue;
            entries[signIn] = (before, saved);
        }
        return entries;
    }

    /// <summary>
    /// Writes the record (a new file, then moved over the old one): <paramref name="first"/>, the entry just saved, then the
    /// newest of the others, <see cref="MaxSignIns"/> in all; with none, the record is deleted.
    /// </summary>
    private void WriteAll(Dictionary<string, (uint Before, DateTime SavedUtc)> entries, (string SignIn, uint Before, DateTime SavedUtc)? first)
    {
        var kept = new List<(string SignIn, uint Before, DateTime SavedUtc)>();
        if (first is { } f) kept.Add(f);
        kept.AddRange(entries.OrderByDescending(e => e.Value.SavedUtc).Select(e => (e.Key, e.Value.Before, e.Value.SavedUtc)));
        if (kept.Count > MaxSignIns) kept.RemoveRange(MaxSignIns, kept.Count - MaxSignIns);
        if (kept.Count == 0) { Delete(); return; }
        var list = kept.Select(e => new Dictionary<string, object>
        {
            ["signIn"] = e.SignIn, ["before"] = e.Before, ["savedUtc"] = e.SavedUtc.ToString("o", CultureInfo.InvariantCulture),
        }).ToList();
        string file = _file();
        File.WriteAllText(_real(file + ".tmp"), JsonSerializer.Serialize(new Dictionary<string, object> { ["signIns"] = list }));
        File.Move(_real(file + ".tmp"), _real(file), overwrite: true);
    }

    private void Delete()
    {
        try { File.Delete(_real(_file())); }
        catch (Exception ex) { _log.Info("token guard: the record of the setting it replaced was not removed: " + ex.Message); }
    }
}
