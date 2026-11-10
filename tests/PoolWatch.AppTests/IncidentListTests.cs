using PoolWatch.Ui;

namespace PoolWatch.AppTests;

/// <summary>
/// The incident list's rows from disk: the newest 500 incident folders, told by name from the finding reports beside them,
/// so a busy day's spawn-storm folders (the owner had about 3,000 a day) never hide the growth incidents.
/// </summary>
public sealed class IncidentListTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pw-incidentlist-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private void Incident(string id, DateTime opened)
    {
        Directory.CreateDirectory(Path.Combine(_dir, id));
        File.WriteAllText(Path.Combine(_dir, id, "report.json"),
            $"{{\"id\":\"{id}\",\"kind\":\"Growth\",\"tag\":\"FMfn\",\"growthBytes\":1048576,\"openedUtc\":\"{opened:O}\",\"suspects\":[]}}");
    }

    [Theory]
    [InlineData("20261109-225024-MmSt-74536d4d", true)]
    [InlineData("20261109-225024-MmSt-74536d4d-2", true)]
    [InlineData("20261109-231334-Reff-66666552", true)]
    [InlineData("20261109-120000-Ntf_-5f66744e", true)]
    [InlineData("20261109-231314-SpawnStorm-bash", false)]
    [InlineData("20261109-231314-SpawnStorm-bash-2", false)]
    [InlineData("20261109-120000-Crash-restart", false)]
    [InlineData("20261109-120000-Experiment-lag-vs-clear", false)]
    public void AnIncidentFolderIsToldFromAFindingReportByItsName(string name, bool incident) => Assert.Equal(incident, UiData.IsIncidentName(name));

    [Fact]
    public void GrowthIncidentsAreListedHoweverManyStormFoldersAreNewer()
    {
        var day = new DateTime(2026, 11, 9, 8, 0, 0, DateTimeKind.Utc);
        Incident("20261109-080000-FMfn-6e664d46", day);
        Incident("20261109-090000-MmSt-74536d4d", day.AddHours(1));
        Incident("20261109-100000-Reff-66666552", day.AddHours(2));
        // 600 storm folders after them, each with its finding's report.json.
        for (int i = 0; i < 600; i++)
        {
            string id = $"20261109-{11 + i / 3600:D2}{i / 60 % 60:D2}{i % 60:D2}-SpawnStorm-bash";
            Directory.CreateDirectory(Path.Combine(_dir, id));
            File.WriteAllText(Path.Combine(_dir, id, "report.json"), "{\"kind\":\"SpawnStorm\",\"severity\":\"Alert\"}");
        }
        var rows = UiData.ListDisk(_dir, new HashSet<string>(StringComparer.OrdinalIgnoreCase), Core.Model.Moment.Now());
        Assert.Equal(["20261109-100000-Reff-66666552", "20261109-090000-MmSt-74536d4d", "20261109-080000-FMfn-6e664d46"], rows.Select(r => r.Id));
    }

    [Fact]
    public void TheListStaysBoundedToTheNewestIncidents()
    {
        var day = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < UiData.MaxDiskRows + 20; i++) Incident($"20261101-{i / 3600:D2}{i / 60 % 60:D2}{i % 60:D2}-File-656c6946", day.AddSeconds(i));
        var rows = UiData.ListDisk(_dir, new HashSet<string>(StringComparer.OrdinalIgnoreCase), Core.Model.Moment.Now());
        Assert.Equal(UiData.MaxDiskRows, rows.Count);
        Assert.Equal($"20261101-{(UiData.MaxDiskRows + 19) / 3600:D2}{(UiData.MaxDiskRows + 19) / 60 % 60:D2}{(UiData.MaxDiskRows + 19) % 60:D2}-File-656c6946", rows[0].Id);
    }
}
