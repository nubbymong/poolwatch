using System.Text.Json.Nodes;
using PoolWatch.Core;

namespace PoolWatch.Tests;

/// <summary>
/// settings.json from the published beta (1.0.0-beta.1) holds <c>startAtLogon</c>, a setting it saved and never read
/// (starting at sign-in is the scheduled task Settings and setup create). The setting is gone: such a file still loads
/// with every other setting as written (but a watched app named twice in different case is kept once, in this build as
/// in the beta), and the next save leaves <c>startAtLogon</c> out.
/// </summary>
public sealed class OlderSettingsFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pw-settings-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>settings.json exactly as the published beta's Save writes it with every setting at its default.</summary>
    private const string Beta = """
        {
          "settingsVersion": 4,
          "sampleIntervalSeconds": 10,
          "handleIntervalSeconds": 300,
          "autoClearEnabled": false,
          "autoClearPoolGb": 8,
          "autoClearEveryHours": 1,
          "autoClearMinMinutes": 30,
          "autoClearMode": "auto",
          "cpuTraceEnabled": false,
          "tokenGuardEnabled": false,
          "tokenGuardRecheckMinutes": 3,
          "checkForUpdates": true,
          "cpuTraceSeconds": 30,
          "symbolPath": "",
          "rawHistoryMinutes": 70,
          "shortLivedKeepMinutes": 15,
          "incidentEventKeepMinutes": 90,
          "serviceIntervalSeconds": 60,
          "volumeIntervalMinutes": 60,
          "handleScanBudgetMs": 250,
          "tagCsvIntervalSeconds": 60,
          "processCsvIntervalSeconds": 60,
          "tagCsvMinBytes": 1048576,
          "processCsvTopN": 50,
          "retentionDays": 7,
          "growthRules": [
            {
              "windowMinutes": 10,
              "bytes": 209715200
            },
            {
              "windowMinutes": 60,
              "bytes": 1073741824
            }
          ],
          "nonPagedTotalAlertBytes": 5368709120,
          "nonPagedTotalRiseBytesPer10Min": 104857600,
          "retentionMinutes": 15,
          "releaseBytes": 104857600,
          "releaseWindowMinutes": 10,
          "growingDisplayBytesPer10Min": 10485760,
          "incidentQuietMinutes": 10,
          "incidentMaxMinutes": 120,
          "captureOnAlert": false,
          "captureSeconds": 60,
          "autoCaptureMinMinutes": 60,
          "deeperCapture": false,
          "aiccResourcesRoot": "",
          "transcriptMaxFiles": 400,
          "transcriptWindowMinutes": 30,
          "spawnStormWindowSeconds": 30,
          "spawnStormThreshold": 30,
          "shortLivedSeconds": 10,
          "strayGitCheckMinutes": 60,
          "tokenBucketSeconds": 60,
          "tokenWindowMinutes": 60,
          "tokenMinBuckets": 10,
          "tokenMinOutstandingRise": 500,
          "lagProbeSeconds": 10,
          "lagAlertRatio": 5,
          "lagBaselineSamples": 30,
          "accumulationIntervalSeconds": 300,
          "accumulationWindowPoints": 6,
          "accumulationMinCount": 5,
          "accumulationMinRise": 3,
          "versionCheckMinutes": 60,
          "claudeVersionsDir": "",
          "watchedApps": [
            "ChatGPT",
            "Codex",
            "claude",
            "Claude",
            "node",
            "AI Code Conductor",
            "StreamDeck",
            "Code",
            "WindowsTerminal",
            "explorer",
            "dwm"
          ],
          "crashLookbackDays": 7,
          "experimentPhaseMinutes": 10,
          "markHotkey": "Ctrl\u002BAlt\u002BM",
          "poolTagTxtPath": "C:\\Program Files (x86)\\Windows Kits\\10\\Debuggers\\x64\\triage\\pooltag.txt",
          "redactCopies": true,
          "showRam": true,
          "startAtLogon": false,
          "serviceProfiles": {
            "profiles": [],
            "delayMinutes": 3
          }
        }
        """;

    private static string Lf(string s) => s.Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>The beta's file holding <c>startAtLogon</c> as <paramref name="on"/>, and the same file without that line.</summary>
    private static (string withIt, string without) BetaFile(bool on)
    {
        string beta = Lf(Beta);
        const string Line = "  \"startAtLogon\": false,\n";
        Assert.Contains(Line, beta);
        return (beta.Replace(Line, $"  \"startAtLogon\": {(on ? "true" : "false")},\n", StringComparison.Ordinal), beta.Replace(Line, "", StringComparison.Ordinal));
    }

    private string Saved(Settings s)
    {
        string path = Path.Combine(_dir, "saved", "settings.json");
        s.Save(path);
        return File.ReadAllText(path);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheBetasSettingsFileLoadsAndTheNextSaveLeavesStartAtLogonOut(bool on)
    {
        var (withIt, without) = BetaFile(on);
        string path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, withIt);
        var s = Settings.Load(path);
        string saved = Saved(s);
        Assert.DoesNotContain("startAtLogon", saved, StringComparison.OrdinalIgnoreCase);
        // Every other line as the beta wrote it, but one: of the watched apps' names that differ only in case ("claude",
        // "Claude"), a load keeps the first (Normalize), in this build as in the beta.
        Assert.Contains("    \"claude\",\n    \"Claude\",\n", without);
        Assert.Equal(without.Replace("    \"Claude\",\n", "", StringComparison.Ordinal), Lf(saved));
    }

    /// <summary>A value other than the default for a setting the beta's file holds that is not a switch or a number.</summary>
    private static JsonNode? Changed(string name) => name switch
    {
        "autoClearMode" => "standby",
        "symbolPath" => @"C:\symbols",
        "aiccResourcesRoot" => @"D:\aicc",
        "claudeVersionsDir" => @"E:\claude\versions",
        "markHotkey" => "Ctrl+Alt+P",
        "poolTagTxtPath" => @"C:\tools\pooltag.txt",
        "growthRules" => JsonNode.Parse("""[ { "windowMinutes": 5, "bytes": 104857600 } ]"""),
        "watchedApps" => JsonNode.Parse("""[ "Code", "dwm" ]"""),
        "serviceProfiles" => JsonNode.Parse("""{ "profiles": [ { "name": "Build box", "services": [ "SysMain", "MapsBroker" ] } ], "atStart": "Build box", "delayMinutes": 7 }"""),
        _ => throw new InvalidOperationException($"no changed value for {name}"),
    };

    /// <summary>The beta's file with every setting changed (a switch turned over, a number one more, the rest from <see cref="Changed"/>).</summary>
    private static JsonObject ChangedBetaFile(bool on)
    {
        var file = JsonNode.Parse(BetaFile(on).withIt)!.AsObject();
        foreach (var (name, value) in file.ToList())
        {
            if (name is "settingsVersion" or "startAtLogon") continue;
            file[name] = value!.GetValueKind() switch
            {
                System.Text.Json.JsonValueKind.True => false,
                System.Text.Json.JsonValueKind.False => true,
                System.Text.Json.JsonValueKind.Number => value.GetValue<long>() + 1,
                _ => Changed(name),
            };
        }
        return file;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EverySettingInTheBetasFileIsReadAsWrittenBesideStartAtLogon(bool on)
    {
        var beta = JsonNode.Parse(BetaFile(on).withIt)!.AsObject();
        var changed = ChangedBetaFile(on);
        Assert.Equal(on, changed["startAtLogon"]!.GetValue<bool>());
        // Every setting but the version and startAtLogon differs from the default: each one is checked below.
        Assert.All(changed.Where(p => p.Key is not ("settingsVersion" or "startAtLogon")),
            p => Assert.NotEqual(beta[p.Key]!.ToJsonString(), p.Value!.ToJsonString()));

        string path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, changed.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        var s = Settings.Load(path);
        var saved = JsonNode.Parse(Saved(s))!.AsObject();
        Assert.False(saved.ContainsKey("startAtLogon"));
        Assert.Equal(changed.Select(p => p.Key).Where(k => k != "startAtLogon"), saved.Select(p => p.Key));
        Assert.All(saved, p => Assert.True(changed[p.Key]!.ToJsonString() == p.Value!.ToJsonString(),
            $"{p.Key}: wrote {changed[p.Key]!.ToJsonString()}, read back {p.Value!.ToJsonString()}"));
        // The same settings as the same file without startAtLogon.
        changed.Remove("startAtLogon");
        Assert.Equal(Settings.Parse(changed.ToJsonString()).ToJson(), s.ToJson());
    }
}
