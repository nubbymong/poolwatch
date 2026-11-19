using System.Reflection;
using System.Text.RegularExpressions;
using PoolWatch.Core;
using PoolWatch.Ui;

namespace PoolWatch.AppTests;

/// <summary>
/// The writes of <see cref="AutoClearSettings.ApplyTo"/>, one at a time. Save writes the automatic clear's values onto
/// the running settings one after the other while the sampler reads them, and a write to that plain object cannot be
/// watched from outside. So a test that must see every state the settings pass through reads ApplyTo's statements from
/// the source and runs them here, in the source's order, each by what it says: a value of the record, or true or false,
/// written to the setting named, under the condition before it when it has one. A line of any other shape, or a
/// condition not listed here, fails the test (ApplyTo's tests then need looking at again), and what the real ApplyTo
/// leaves is held equal to what its statements leave, so the two cannot drift apart.
/// </summary>
internal static class AutoClearWrites
{
    /// <summary>The conditions a write of ApplyTo's may have, as the source says them, each with what it means.</summary>
    private static readonly Dictionary<string, Func<AutoClearSettings, Settings, bool>> Conditions = new(StringComparer.Ordinal)
    {
        ["Enabled"] = (to, _) => to.Enabled,
        ["!Enabled"] = (to, _) => !to.Enabled,
        ["!Enabled || AutoClearSettings.Of(s) != this"] = (to, s) => !to.Enabled || AutoClearSettings.Of(s) != to,
    };

    /// <summary>ApplyTo's statements in the source's order: every line of its body but the comments.</summary>
    public static List<string> Statements()
    {
        string source = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Ui", "SettingsEditor.cs")).Replace("\r\n", "\n");
        int declared = source.IndexOf("internal sealed record AutoClearSettings(", StringComparison.Ordinal);
        Assert.True(declared >= 0, "AutoClearSettings was not found");
        const string opens = "    public void ApplyTo(Settings s)\n    {\n";
        int start = source.IndexOf(opens, declared, StringComparison.Ordinal);
        Assert.True(start >= 0, "AutoClearSettings.ApplyTo was not found");
        int end = source.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        Assert.True(end > start, "the end of AutoClearSettings.ApplyTo was not found");
        return source[(start + opens.Length)..end].Split('\n').Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("//", StringComparison.Ordinal)).ToList();
    }

    /// <summary>Settings that hold <paramref name="a"/> as their automatic clear, each value set here (not through ApplyTo).</summary>
    public static Settings Holding(AutoClearSettings a) => new()
    {
        AutoClearEnabled = a.Enabled, AutoClearEveryHours = a.EveryHours, AutoClearPoolGb = a.PoolGb, AutoClearMinMinutes = a.MinMinutes,
        AutoClearMode = a.Mode, AutoClearUrgent = a.Urgent, AutoClearUrgentAsk = a.UrgentAsk,
    };

    /// <summary>
    /// The automatic clear's settings that settings holding <paramref name="from"/> hold before ApplyTo's first write
    /// and after each one, as <paramref name="to"/> is written onto them: every state a reader could find them in. The
    /// last is what the real ApplyTo leaves.
    /// </summary>
    public static List<AutoClearSettings> States(AutoClearSettings from, AutoClearSettings to)
    {
        var s = Holding(from);
        var states = new List<AutoClearSettings> { AutoClearSettings.Of(s) };
        foreach (string statement in Statements())
        {
            var write = Regex.Match(statement, @"^(?:if \((?<when>.+)\) )?s\.(?<setting>AutoClear\w+) = (?<value>\w+);$");
            Assert.True(write.Success, $"AutoClearSettings.ApplyTo has a line that is not one write of a setting: {statement}");
            if (write.Groups["when"].Success)
            {
                string condition = write.Groups["when"].Value;
                Assert.True(Conditions.TryGetValue(condition, out var holds), $"AutoClearSettings.ApplyTo writes under a condition this test does not know: {condition}");
                if (!holds!(to, s)) continue;
            }
            string value = write.Groups["value"].Value;
            object? written = value switch
            {
                "true" => true,
                "false" => false,
                _ => Property(typeof(AutoClearSettings), value, statement).GetValue(to),
            };
            Property(typeof(Settings), write.Groups["setting"].Value, statement).SetValue(s, written);
            states.Add(AutoClearSettings.Of(s));
        }
        var real = Holding(from);
        to.ApplyTo(real);
        Assert.Equal(AutoClearSettings.Of(real), states[^1]);
        return states;
    }

    private static PropertyInfo Property(Type type, string name, string statement)
    {
        var property = type.GetProperty(name);
        Assert.True(property is not null, $"AutoClearSettings.ApplyTo names {type.Name}.{name}, which is not there: {statement}");
        return property!;
    }
}
