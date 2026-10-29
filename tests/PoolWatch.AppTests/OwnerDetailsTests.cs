using PoolWatch.Core;
using PoolWatch.Core.Correlation;
using PoolWatch.Core.Identity;
using PoolWatch.Core.Model;
using PoolWatch.Ui;

namespace PoolWatch.AppTests;

/// <summary>
/// A Pool causes row keeps only each owner's first processes (TagViewBuilder.View); the details pane, the owner's
/// details and the copied summary still count all of them.
/// </summary>
public class OwnerDetailsTests
{
    private static readonly DateTime T0 = new(2026, 10, 27, 15, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ATrimmedOwnerStillCountsAllItsProcesses()
    {
        var owner = new OwnerSuspect { Owner = new Owner(OwnerKind.ClaudeSession, "s", "Claude Code session"), Members = [], Identities = [], Meaningful = true };
        for (int i = 0; i < 50; i++)
        {
            owner.Members.Add(new Suspect
            {
                Key = new ProcessKey(1000 + i, 1), Image = "bash.exe", StartUtc = T0.AddSeconds(i), CommandLine = i == 30 ? "bash -c \"git status\"" : null,
                Direct = new DirectSignal(null, 0, 0, 0, 0, 0, false), Activity = new ActivitySignal("total", double.NaN, 0, double.NaN, 0, 0, false),
                Lifecycle = new LifecycleSignal(false, false, false, []),
            });
            owner.Identities.Add(new ProcessIdentity("bash.exe", null, owner.Owner, false));
        }
        var view = new TagViewData(PoolTag.Parse("Reff"), "Reff", new TagAnswer("grew", [owner], 1, 0), [owner], [], T0, T0, T0);
        string all = MainForm.OwnerDetails(owner, view);
        string line = MainForm.ProcessesLine(owner);
        owner.Trim(OwnerRanking.KeptMembers);
        Assert.EndsWith("… and 30 more processes", all);
        Assert.Equal(all, MainForm.OwnerDetails(owner, view));
        Assert.EndsWith(" and 45 more", line);
        Assert.Equal(line, MainForm.ProcessesLine(owner));
    }
}
