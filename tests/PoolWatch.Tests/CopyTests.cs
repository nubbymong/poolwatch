using PoolWatch.Core.Incidents;
using PoolWatch.Core.Util;

namespace PoolWatch.Tests;

public class CopyTests
{
    [Fact]
    public void TsvHasHeaderAndEscapesCells()
    {
        string t = CopyText.Tsv(["Process", "PID", "Command line"], [["node.exe", "12", "node\tx.js\r\nmore"], ["git.exe", "13", "git status"]]);
        Assert.Equal("Process\tPID\tCommand line\r\nnode.exe\t12\tnode x.js  more\r\ngit.exe\t13\tgit status\r\n", t);
    }

    [Fact]
    public void RedactionAppliesToCopies()
    {
        var r = new RedactionInfo("alice", "ALICE-PC", [@"C:\Users\alice"]);
        Assert.Equal(@"<HOME>\x on <MACHINE>", CopyText.Apply(@"C:\Users\alice\x on ALICE-PC", r));
        Assert.Equal("as is", CopyText.Apply("as is", null));
    }

    [Fact]
    public void IncidentSummaryFromReportJson()
    {
        string json = """
        { "id": "20261026-0530-File", "kind": "Growth", "tag": "File", "headline": "`File` grew 12 MB; top suspect Leaker.exe",
          "plainAnswer": "File objects (File, non-paged) grew 12 MB in 39 s. Most likely cause: Leaker (strong).",
          "window": { "fromUtc": "2026-10-26T05:29:00Z", "onsetUtc": "2026-10-26T05:30:00Z", "toUtc": "2026-10-26T05:31:00Z" },
          "suspects": [ { "displayName": "Leaker.exe", "pid": 7716, "confidence": "StronglyCorrelated", "commandLine": "Leaker.exe leak-handles",
                          "evidence": [ "direct charge: File handles 0 -> 30,026" ] } ] }
        """;
        string s = CopyText.IncidentSummary(json);
        Assert.StartsWith("`File` grew 12 MB; top suspect Leaker.exe\r\nIncident 20261026-0530-File (Growth), tag File\r\n", s);
        Assert.Contains("Most likely cause: Leaker (strong).", s);
        Assert.Contains("1. Leaker.exe (PID 7716), StronglyCorrelated\r\n   - direct charge: File handles 0 -> 30,026\r\n   command line: Leaker.exe leak-handles", s);
    }
}
