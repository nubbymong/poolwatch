using PoolWatch.Core;
using PoolWatch.Core.Services;
using PoolWatch.Engine;

namespace PoolWatch.AppTests;

/// <summary>Settings that take effect at once: the running object and settings.json (in TEMP), and nothing written with --config.</summary>
public sealed class LiveSettingsTests : IDisposable
{
    private readonly ChangeFixture _f = new();

    public void Dispose() => _f.Dispose();

    [Fact]
    public void AppliesToTheRunningSettingsAndTheFileAndKeepsTheRest()
    {
        new Settings { CaptureSeconds = 90, AutoClearPoolGb = 6 }.Save(_f.Paths.SettingsFile);
        var running = new Settings { CaptureSeconds = 60 };
        var live = _f.Live(running);
        var applied = new List<Action<Settings>>();
        live.Applied += applied.Add;

        Action<Settings> on = s => { s.AutoClearEnabled = true; s.AutoClearEveryHours = 1; };
        live.Apply("the hourly clear", on);

        Assert.Same(running, live.Running);
        Assert.Equal((true, 1.0, 60), (running.AutoClearEnabled, running.AutoClearEveryHours, running.CaptureSeconds));
        var saved = _f.Saved();
        Assert.Equal((true, 1.0, 90, 6.0), (saved.AutoClearEnabled, saved.AutoClearEveryHours, saved.CaptureSeconds, saved.AutoClearPoolGb)); // other saved values stay
        Assert.Same(on, Assert.Single(applied));
        Assert.False(File.Exists(_f.Paths.SettingsFile + ".tmp"));
    }

    [Fact]
    public void TheFileAndTheRunningSettingsAreNormalized()
    {
        var live = _f.Live();
        live.Apply("a schedule too long", s => s.AutoClearEveryHours = 10_000);
        Assert.Equal(24 * 7, _f.Saved().AutoClearEveryHours);
        Assert.Equal(24 * 7, live.Running.AutoClearEveryHours);

        var readOnly = _f.Live(persist: false);
        readOnly.Apply("a schedule too long", s => s.AutoClearEveryHours = 10_000);
        Assert.Equal(24 * 7, readOnly.Running.AutoClearEveryHours);
    }

    [Fact]
    public void WhatTheNextStartReadsIsTheSavedFile()
    {
        new Settings { DeeperCapture = true }.Save(_f.Paths.SettingsFile);
        Assert.True(_f.Live(new Settings()).Saved()!.DeeperCapture); // saved for the next start, not running yet
        // With --config the running settings are what the next start reads; a file that cannot be read gives none either.
        Assert.Null(_f.Live(new Settings(), persist: false).Saved());
        File.WriteAllText(_f.Paths.SettingsFile, "{ not json");
        Assert.Null(_f.Live().Saved());
    }

    [Fact]
    public void WithAReadOnlyConfigNothingIsWritten()
    {
        var live = _f.Live(new Settings(), persist: false);
        live.Apply("the token-leak guard", s => s.TokenGuardEnabled = true);
        Assert.True(live.Running.TokenGuardEnabled);
        Assert.False(File.Exists(_f.Paths.SettingsFile));
    }

    [Fact]
    public void SettingsSaveWritesNothingWithAConfigFileSoItsProfileIsNeverAppliedAtTheNextStart()
    {
        // A --config file's settings, with a service profile chosen to apply at start, as Settings' editor holds them.
        var fromConfig = new Settings
        {
            DeeperCapture = true,
            ServiceProfiles = new ServiceProfileSet { Profiles = [new("P", ["SysMain", "MapsBroker"])], AtStart = "P" },
        };
        var live = _f.Live(fromConfig, persist: false);
        Assert.False(live.SaveForNextStart(Clone(fromConfig)));
        Assert.False(File.Exists(_f.Paths.SettingsFile));

        // PoolWatch's own settings.json is left byte for byte as it was, so the next start applies no profile from that file.
        new ProfileStore(_f.Live()).Save("Mine", ["Fax"], atStart: false, 3);
        byte[] before = File.ReadAllBytes(_f.Paths.SettingsFile);
        Assert.False(live.SaveForNextStart(Clone(fromConfig)));
        Assert.Equal(before, File.ReadAllBytes(_f.Paths.SettingsFile));
        Assert.Null(_f.Saved().ServiceProfiles.AtStart);
    }

    [Fact]
    public void SettingsSaveKeepsTheServiceProfilesAsTheyAreNowAndWritesEveryOtherSetting()
    {
        var live = _f.Live();
        var store = new ProfileStore(live);
        store.Save("Build box", ["SysMain"], atStart: true, 5);
        var opened = Clone(_f.Saved());                                   // Settings opened now...
        store.Save("Build box", ["SysMain", "MapsBroker"], atStart: false, 5); // ...then the Services tab changed the profiles
        store.Save("Second", ["Fax"], atStart: true, 7);
        opened.DeeperCapture = true;

        Assert.True(live.SaveForNextStart(opened));
        var saved = _f.Saved();
        Assert.True(saved.DeeperCapture);
        Assert.Equal(["Build box", "Second"], saved.ServiceProfiles.Profiles.Select(p => p.Name));
        Assert.Equal(["SysMain", "MapsBroker"], saved.ServiceProfiles.Find("Build box")!.Services);
        Assert.Equal(("Second", 7), (saved.ServiceProfiles.AtStart, saved.ServiceProfiles.DelayMinutes));
        Assert.False(File.Exists(_f.Paths.SettingsFile + ".tmp"));
    }

    private static Settings Clone(Settings s) => Settings.Parse(s.ToJson());

    [Fact]
    public void AFailedWriteChangesNothing()
    {
        Directory.CreateDirectory(_f.Paths.SettingsFile + ".tmp"); // the temporary file cannot be written
        var live = _f.Live();
        bool raised = false;
        live.Applied += _ => raised = true;
        Assert.Throws<UnauthorizedAccessException>(() => live.Apply("the token-leak guard", s => s.TokenGuardEnabled = true));
        Assert.False(live.Running.TokenGuardEnabled);
        Assert.False(raised);
    }
}
