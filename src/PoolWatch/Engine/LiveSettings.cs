using PoolWatch.Core;

namespace PoolWatch.Engine;

/// <summary>
/// Settings that take effect at once: the setup guide, a suggestion, Settings and Undo turn the automatic clear and the
/// token-leak guard on or off while PoolWatch runs, or the "Next cache clear" line would be wrong until the next start.
/// <see cref="Apply"/> changes settings.json and the running engine's settings object (the sampler reads the automatic
/// clear's values once per sample; a bool or double write is atomic on x64), then raises <see cref="Applied"/> so an
/// open Settings form's copy follows. Every other setting still applies at the next start.
/// </summary>
internal sealed class LiveSettings
{
    private readonly AppPaths _paths;
    private readonly bool _persist;
    private readonly Log _log;
    private readonly object _lock = new();

    /// <param name="running">The running engine's settings object (changed in place).</param>
    /// <param name="persist">Write settings.json too; false with <c>--config</c>, which is read-only.</param>
    public LiveSettings(AppPaths paths, Settings running, bool persist, Log log)
    {
        _paths = paths;
        Running = running;
        _persist = persist;
        _log = log;
    }

    public Settings Running { get; }

    /// <summary>Raised on the caller's thread after each <see cref="Apply"/>, with the change that was applied.</summary>
    public event Action<Action<Settings>>? Applied;

    /// <summary>
    /// Any thread. The file is changed first (loaded, changed, normalized, written to a temporary file and moved over the
    /// old one), so a failed write throws and changes nothing; the other saved values stay as they are. The running
    /// settings are normalized the same way, so the engine runs with the values the file holds.
    /// </summary>
    public void Apply(string what, Action<Settings> change)
    {
        lock (_lock)
        {
            if (_persist)
            {
                string file = _paths.SettingsFile;
                var saved = Settings.Load(file);
                change(saved);
                saved.Normalize();
                saved.Save(_paths.Guard.CheckReal(file + ".tmp"));
                File.Move(_paths.Guard.CheckReal(file + ".tmp"), _paths.Guard.CheckReal(file), overwrite: true);
            }
            change(Running);
            Running.Normalize();
        }
        _log.Info("settings applied now: " + what);
        Applied?.Invoke(change);
    }
}
