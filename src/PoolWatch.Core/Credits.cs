using PoolWatch.Core.Report;
using PoolWatch.Core.Updates;

namespace PoolWatch.Core;

/// <summary>Software or work PoolWatch uses or builds on, as About lists it (THIRD-PARTY-NOTICES.md has the full texts).</summary>
/// <param name="What">What it does for PoolWatch, in a sentence.</param>
/// <param name="Shipped">Part of a release (inside PoolWatch.exe, next to it, or the installer), not credited only.</param>
public sealed record Credit(string Name, string What, string Licence, string Url, bool Shipped);

/// <summary>The credits, and the only web pages PoolWatch opens (<see cref="IsAllowedLink"/>).</summary>
public static class Credits
{
    public const string RepoUrl = "https://github.com/nubbymong/poolwatch";
    public const string NoticesUrl = RepoUrl + "/blob/main/THIRD-PARTY-NOTICES.md";

    public static IReadOnlyList<Credit> All { get; } =
    [
        new("TraceEvent", "Reads Windows' event traces: process starts and the confirmation trace.", "MIT", "https://github.com/microsoft/perfview", true),
        new("KernelTraceControl and msdia140", "TraceEvent's native DLLs: they read a trace's stacks.", "Microsoft redistributable",
            "https://github.com/microsoft/perfview", true),
        new("Microsoft.Diagnostics.NETCore.Client", "A part TraceEvent needs.", "MIT", "https://github.com/dotnet/diagnostics", true),
        new("Microsoft.Extensions packages", "Parts the diagnostics client needs.", "MIT", "https://github.com/dotnet/runtime", true),
        new("System.Management", "Reads Windows Management Instrumentation, such as the antivirus in use.", "MIT", "https://github.com/dotnet/runtime", true),
        new(".NET and the Windows Desktop runtime", "Run PoolWatch; the installer and the self-contained download include them.", "MIT",
            "https://github.com/dotnet/runtime", true),
        new("Inno Setup", "Builds the installer and the uninstaller.", "Inno Setup License", "https://jrsoftware.org/isinfo.php", true),
        new("bentoner/windows-token-leak", "Documented the Windows 11 token leak: the builds it affects and its cause.", "MIT",
            "https://github.com/bentoner/windows-token-leak", false),
        new("gunrgbcr/windows-token-leak-logon-fix", "The idea behind the token-leak guard, which PoolWatch re-implemented; no code was copied.",
            "No licence declared", "https://github.com/gunrgbcr/windows-token-leak-logon-fix", false),
        new("Faarfarin, openai/codex issue 31212", "Traced the Codex app's shader-cache file storm and the workaround the Codex shortcut uses.",
            "Credit only", "https://github.com/openai/codex/issues/31212#issuecomment-5635622083", false),
        new("Sysinternals RAMMap", "PoolWatch only recognises a clear task of yours that runs it.", "Not redistributed",
            "https://learn.microsoft.com/sysinternals/downloads/rammap", false),
        new("pooltag.txt, Debugging Tools for Windows", "Names pool tags when the Debugging Tools are installed.", "Not redistributed",
            "https://learn.microsoft.com/windows-hardware/drivers/debugger/", false),
        new("xUnit and the .NET test SDK", "Run PoolWatch's tests; not part of a release.", "Apache 2.0 and MIT", "https://xunit.net/", false),
    ];

    /// <summary>
    /// The web pages PoolWatch may open (UiActions.OpenWebPage): the credits' pages, the repository, its notices and
    /// private advisory pages, one release's own page (an update notice's, <see cref="ReleasePage.IsValid"/>), and a
    /// new-issue address PoolWatch built (<see cref="IssueReport.Url"/>): short enough for explorer.exe to pass on whole,
    /// and with a query of escaped characters only, so it can never carry a quote or a space onto explorer.exe's command line.
    /// </summary>
    public static bool IsAllowedLink(string url)
    {
        if (url is RepoUrl or NoticesUrl or IssueReport.AdvisoryUrl || All.Any(c => c.Url == url) || ReleasePage.IsValid(url)) return true;
        const string issue = IssueReport.NewIssueUrl + "?";
        // An escaped query holds unreserved characters, '%' escapes and the '=' and '&' between fields.
        return url.Length <= IssueReport.MaxUrlLength && url.StartsWith(issue, StringComparison.Ordinal) &&
               url[issue.Length..].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~' or '%' or '=' or '&');
    }
}
