using PoolWatch.Core;
using PoolWatch.Ui;
using PoolWatch.Ui.Setup;

namespace PoolWatch.AppTests;

/// <summary>
/// Setup's Standby cache step offers the urgent clear on a switch of its own (the owner asked for it there): on to begin
/// with, shown with every choice that clears, gone with Decide later, which is still offered. Next writes it with the
/// rule, as the switch shows it; Decide later leaves it as it is. The host and the PC are SetupWizardTests' fakes.
/// </summary>
public sealed partial class SetupWizardTests
{
    [Fact]
    public async Task TheStandbyCacheStepOffersTheUrgentClearOnASwitchAndNextWritesItWithTheRule()
    {
        var host = new FakeHost(Busy());
        var session = await Session(Busy(), host);
        var (view, fonts) = View(session, 100, new Size(880, 586));
        using (view)
        using (fonts)
        {
            await session.GoTo(SetupStep.StandbyCache);
            var page = (CachePage)view.Pages[SetupStep.StandbyCache];
            string row = SetupText.UrgentRow(host.Running.NonPagedTotalAlertBytes);
            Assert.Equal("Also clear at once if the non-paged pool is over 5 GB and still rising", row);
            // On to begin with, with the recommended choice; the four choices are all there, Decide later among them.
            Assert.True(session.Urgent);
            Assert.Equal((true, true), (page.UrgentShown, page.UrgentSwitch.On));
            Assert.Contains(row, page.Texts());
            Assert.Equal(4, page.Cards.Count);
            Assert.True(page.Cards.ContainsKey(CacheChoice.Later));
            // The step still shows whole at the design's size, nothing overlapping.
            Assert.Empty(view.LayoutProblems());
            Assert.False(view.Scrolls);
            // Decide later clears nothing: no switch.
            session.ChooseCache(CacheChoice.Later);
            Assert.False(page.UrgentShown);
            Assert.DoesNotContain(row, page.Texts());
            Assert.Empty(view.LayoutProblems());
            // A choice that clears shows it again, as it was.
            session.ChooseCache(CacheChoice.Schedule);
            Assert.True(page.UrgentShown && page.UrgentSwitch.On);
            // Turned off (as a press on it does), Next writes it off with the rule.
            session.SetUrgent(false);
            Assert.False(page.UrgentSwitch.On);
            Assert.Equal("Clear at once if the non-paged pool is over its limit and still rising: Off", page.UrgentSwitch.AccessibleName);
            Assert.True(await session.NextAsync(null!));
            Assert.Equal([false], host.UrgentGiven);
            Assert.Equal((true, false), (host.Running.AutoClearEnabled, host.Running.AutoClearUrgent));
        }
    }

    [Fact]
    public async Task TheUrgentClearsSwitchIsWrittenWithAChoiceThatClearsAndDecidingLaterLeavesIt()
    {
        var system = new FakeSystem();
        var live = _f.Live();
        Assert.True(live.Running.AutoClearUrgent); // on to begin with
        var (ok, message) = await Cache(system, live).Apply(CacheChoice.Either, 2, 6, replaceOwnTask: false, urgent: false);
        Assert.True(ok, message);
        Assert.False(live.Running.AutoClearUrgent);
        Assert.False(_f.Saved().AutoClearUrgent);
        Assert.Equal("Automatic cache clearing turned on, every 2 hours, or when the kernel pool reaches 6 GB, without the urgent clear (from setup).",
            Assert.Single(system.TimelineLines));
        // The same rule with the switch turned on again is a change too.
        Assert.True((await Cache(system, live).Apply(CacheChoice.Either, 2, 6, replaceOwnTask: false, urgent: true)).Ok);
        Assert.True(live.Running.AutoClearUrgent);
        Assert.True(_f.Saved().AutoClearUrgent);
        Assert.Equal("Automatic cache clearing turned on, every 2 hours, or when the kernel pool reaches 6 GB, and at once while the non-paged pool is over its limit and rising (from setup).",
            system.TimelineLines[^1]);
        // With everything as it is, nothing.
        Assert.Equal((true, ""), await Cache(system, live).Apply(CacheChoice.Either, 2, 6, replaceOwnTask: false, urgent: true));
        Assert.Equal(2, system.TimelineLines.Count);
        // Decide later turns clearing off and leaves the switch as it is.
        Assert.True((await Cache(system, live).Apply(CacheChoice.Later, 1, 8, replaceOwnTask: false, urgent: false)).Ok);
        Assert.False(live.Running.AutoClearEnabled);
        Assert.True(live.Running.AutoClearUrgent);
        // In effect: the switch counts only for a choice that clears.
        var s = new Settings { AutoClearEnabled = true, AutoClearEveryHours = 2, AutoClearPoolGb = 6, AutoClearUrgent = true };
        Assert.True(CacheChoices.InEffect(s, CacheChoice.Either, 2, 6, urgent: true));
        Assert.False(CacheChoices.InEffect(s, CacheChoice.Either, 2, 6, urgent: false));
        Assert.True(CacheChoices.InEffect(s, CacheChoice.Either, 2, 6));
        s.AutoClearEnabled = false;
        Assert.True(CacheChoices.InEffect(s, CacheChoice.Later, 1, 8, urgent: false));
    }
}
