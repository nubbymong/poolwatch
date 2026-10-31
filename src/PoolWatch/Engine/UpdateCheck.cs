using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using PoolWatch.Core.Updates;

namespace PoolWatch.Engine;

/// <summary>A stable PoolWatch release newer than the one running.</summary>
/// <param name="Version">"1.2.0".</param>
/// <param name="PageUrl">Its release page on GitHub (<see cref="ReleasePage.IsValid"/>).</param>
internal sealed record UpdateRelease(string Version, string PageUrl)
{
    /// <summary>What the window and the tray say.</summary>
    public string Text => $"PoolWatch {Version} is available";
}

internal enum UpdateOutcome { UpToDate, Newer, Failed }

/// <summary>The last check: when it ran and what it found.</summary>
internal sealed record UpdateResult(DateTime CheckedUtc, UpdateOutcome Outcome);

/// <summary>
/// Update notifications, information only. About once a day, while the setting allows it, PoolWatch asks GitHub for its
/// latest stable release (one GET without authentication or cookies: nothing about the PC is sent, GitHub sees the
/// address the request comes from) and, when that release is newer than the one running, raises <see cref="Found"/>
/// with its version and release page. It never downloads or runs anything. The answer is kept in the cache folder, so
/// the notice survives a restart and a restart does not bring the next check forward. Every failure (offline, GitHub's
/// rate limit, a malformed or oversized answer, a page that is not PoolWatch's) is quiet: the first is logged, the rest
/// only after a check works again. <see cref="Available"/> and <see cref="LastResult"/> are immutable records swapped
/// whole, so any thread may read them.
/// </summary>
internal sealed class UpdateCheck : IDisposable
{
    /// <summary>
    /// github.com/nubbymong/poolwatch by its number, not its name: the number stays with the repository through a rename
    /// or a transfer and is never given to another one, so whoever holds the name later cannot answer for PoolWatch. The
    /// release page must still be on that name's pages (<see cref="ReleasePage"/>), so after a rename the check fails
    /// quietly rather than follow it.
    /// </summary>
    public const string RepositoryId = "1435312349";
    public const string LatestUrl = "https://api.github.com/repositories/" + RepositoryId + "/releases/latest";
    /// <summary>GitHub's answer for one release, notes included, is tens of kilobytes; a longer one is refused.</summary>
    internal const int MaxResponseBytes = 1024 * 1024;
    /// <summary>The most of a message's text that goes into the log (<see cref="OneLine"/>).</summary>
    internal const int MaxLoggedChars = 200;
    internal const string StateName = "update-check.json";
    private const int MaxStateBytes = 4096;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);
    /// <summary>The first check waits at least this long after start-up (start-up is busy), and up to twice it.</summary>
    private static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);
    /// <summary>Added at random to each day, so PCs that start together do not all ask at the same moment.</summary>
    private static readonly TimeSpan IntervalJitter = TimeSpan.FromHours(1);
    /// <summary>
    /// The longest the timer waits before it looks at the clock again. A timer counts only the time the PC is awake, so a
    /// wait of a whole day would make the check a night late on a PC that sleeps, or days late on a laptop.
    /// </summary>
    private static readonly TimeSpan MaxWait = TimeSpan.FromHours(6);

    private readonly AppPaths _paths;
    private readonly Log _log;
    private readonly Func<bool> _enabled;
    private readonly Func<HttpMessageHandler> _newHandler;
    private readonly SemVersion _running;
    private readonly Func<DateTime> _utcNow;
    private readonly TimeSpan _timeout;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _lock = new();
    private System.Threading.Timer? _timer;
    private bool _disposed;
    /// <summary>When the next check is due, by the clock. Before <see cref="Start"/> it is due at once.</summary>
    private DateTime _dueUtc;
    /// <summary>The latest stable release GitHub reported; kept when a later check fails.</summary>
    private (SemVersion version, string page)? _latest;
    private bool _failureLogged;

    public UpdateCheck(AppPaths paths, Log log, Func<bool> enabled)
        : this(paths, log, enabled, NewHandler, RunningVersion(), () => DateTime.UtcNow, DefaultTimeout) { }

    /// <param name="enabled">The setting, read at each due time: while it is off nothing is sent.</param>
    /// <param name="newHandler">A new HTTP handler for each check, disposed after it. Tests pass a fake: no test reaches GitHub.</param>
    /// <param name="running">The version running (default: <see cref="RunningVersion"/>).</param>
    internal UpdateCheck(AppPaths paths, Log log, Func<bool> enabled, Func<HttpMessageHandler> newHandler, SemVersion running,
        Func<DateTime> utcNow, TimeSpan timeout)
    {
        _paths = paths;
        _log = log;
        _enabled = enabled;
        _newHandler = newHandler;
        _running = running;
        _utcNow = utcNow;
        _timeout = timeout;
    }

    /// <summary>
    /// Raised on a thread-pool thread when a check finds a release newer than the one running and later than the latest
    /// one known before: once per version, also across restarts (a restored one is in <see cref="Available"/> from
    /// <see cref="Start"/> on).
    /// </summary>
    public event Action<UpdateRelease>? Found;

    /// <summary>
    /// The newer release to tell the user about, or null (up to date, or not checked yet). It can change without
    /// <see cref="Found"/> (a release taken back gives the older one, or null when none is newer), so the window reads it
    /// each time it draws the notice.
    /// </summary>
    public UpdateRelease? Available { get; private set; }

    /// <summary>The last check, or null before the first one.</summary>
    public UpdateResult? LastResult { get; private set; }

    private string StateFile => _paths.Guard.CheckReal(Path.Combine(_paths.EnsureDir(_paths.CacheDir), StateName));

    /// <summary>The running version: the informational version ("1.0.0+78b1fec"), else the assembly version.</summary>
    internal static SemVersion RunningVersion()
    {
        var assembly = typeof(UpdateCheck).Assembly;
        if (SemVersion.TryParse(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion, out var v)) return v;
        var n = assembly.GetName().Version ?? new Version(0, 0, 0);
        return new SemVersion(n.Major, n.Minor, Math.Max(n.Build, 0));
    }

    /// <summary>
    /// When the first check after start-up is due: a day after the last one, but never within a few minutes of start-up.
    /// Never checked, or the last check is in the future (the clock went back): a few minutes from now.
    /// </summary>
    /// <param name="jitter">From 0 to 1.</param>
    internal static TimeSpan FirstDelay(DateTime? lastUtc, DateTime nowUtc, double jitter)
    {
        var soonest = StartDelay * (1 + jitter);
        if (lastUtc is not { } last || last > nowUtc) return soonest;
        var due = last + Interval - nowUtc;
        return due > soonest ? due : soonest;
    }

    /// <summary>
    /// How long the timer waits before it looks at the clock again: until the check is due, but never longer than
    /// <see cref="MaxWait"/>.
    /// </summary>
    internal static TimeSpan NextWake(DateTime dueUtc, DateTime nowUtc) =>
        dueUtc > nowUtc && dueUtc - nowUtc < MaxWait ? dueUtc - nowUtc : MaxWait;

    /// <summary>Restores the saved answer and arms the first check. Only the first call does anything, and none after Dispose.</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_disposed || _timer is not null) return;
            var saved = Load();
            var now = _utcNow();
            _dueUtc = now + FirstDelay(saved?.CheckedUtc, now, Random.Shared.NextDouble());
            _timer = new System.Threading.Timer(_ => _ = RunDue(), null, NextWake(_dueUtc, now), Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// The timer's work: when a check is due by the clock and the setting allows it, it checks and makes the next one due a
    /// day later. While the setting is off nothing is sent and the check stays due, so turning it on takes effect at the
    /// next wake. Then it arms the timer again.
    /// </summary>
    internal async Task RunDue()
    {
        try
        {
            var now = _utcNow();
            // Due once its time has passed, or when it is further away than a day can put it (the clock went back).
            if ((now >= _dueUtc || _dueUtc - now > Interval + IntervalJitter) && _enabled())
            {
                _dueUtc = now + Interval + IntervalJitter * Random.Shared.NextDouble();
                await CheckNow(_stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
        catch (Exception ex) { LogFailure($"update check failed: {ex.GetType().Name}: {ex.Message}"); }
        lock (_lock) if (!_disposed) _timer?.Change(NextWake(_dueUtc, _utcNow()), Timeout.InfiniteTimeSpan);
    }

    /// <summary>One check: asks GitHub, then updates <see cref="Available"/>, <see cref="LastResult"/> and the saved answer.</summary>
    internal async Task<UpdateResult> CheckNow(CancellationToken stop)
    {
        var answer = await Ask(stop);
        UpdateRelease? found = null;
        UpdateOutcome outcome;
        if (answer.Failure is { } why)
        {
            outcome = UpdateOutcome.Failed;
            LogFailure("update check failed: " + why);
        }
        else
        {
            _failureLogged = false;
            var previous = _latest?.version;
            if (answer.Version is { } version) _latest = (version, answer.Page!);
            var newer = NewerThanRunning();
            // Only a release later than the latest one known before is announced: when a release is taken back and GitHub's
            // latest goes back to an older one, that one is not announced again.
            if (newer is not null && answer.Version?.CompareTo(previous) > 0) found = newer;
            Available = newer;
            outcome = newer is null ? UpdateOutcome.UpToDate : UpdateOutcome.Newer;
        }
        var result = new UpdateResult(_utcNow(), outcome);
        LastResult = result;
        Save(result);
        if (found is not null)
        {
            _log.Info($"update check: {found.Text} ({found.PageUrl})");
            try { Found?.Invoke(found); } catch (Exception ex) { _log.Info(OneLine("update notice failed: " + ex.Message)); }
        }
        return result;
    }

    /// <summary>What GitHub said: the latest stable release, <see cref="NotStable"/>, or why not.</summary>
    private readonly record struct Answer(SemVersion? Version, string? Page, string? Failure)
    {
        /// <summary>The latest release is a pre-release or a draft: nothing to offer, and not a failure.</summary>
        public static readonly Answer NotStable = new(null, null, null);

        public static Answer Fail(string why) => new(null, null, why);
    }

    private async Task<Answer> Ask(CancellationToken stop)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(_timeout);
        // HttpMessageInvoker rather than HttpClient: it hands back the body unread, so ReadBounded can stop at the limit,
        // and the handler goes with it after the check, so nothing stays open between checks.
        using var http = new HttpMessageInvoker(_newHandler(), disposeHandler: true);
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("PoolWatch", _running.ToString()));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        try
        {
            using var response = await http.SendAsync(request, timeout.Token);
            if (RateLimited(response)) return Answer.Fail("GitHub's rate limit was reached");
            if (response.StatusCode == HttpStatusCode.Forbidden) return Answer.Fail("the request was refused (403), by GitHub or by a proxy on the way");
            // GitHub's answer when the repository has no release yet, and also when it does not know that address any more.
            if (response.StatusCode == HttpStatusCode.NotFound) return Answer.Fail("not found (404): no release yet, or GitHub no longer answers this address");
            if (response.StatusCode != HttpStatusCode.OK) return Answer.Fail($"GitHub answered {(int)response.StatusCode}");
            var body = await ReadBounded(response.Content, timeout.Token);
            return body is null ? Answer.Fail("the answer was too large") : ParseRelease(body);
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested) { return Answer.Fail($"no answer within {_timeout.TotalSeconds:0.#} s"); }
        // Only the kind of error: its text can quote what the server or a proxy sent.
        catch (HttpRequestException ex) { return Answer.Fail($"GitHub could not be reached ({ex.HttpRequestError})"); }
        catch (IOException ex) { return Answer.Fail($"the answer was cut off ({(ex is HttpIOException h ? h.HttpRequestError.ToString() : ex.GetType().Name)})"); }
    }

    /// <summary>GitHub's rate limit: 429, or a 403 that says no requests are left.</summary>
    private static bool RateLimited(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.TooManyRequests ||
        (response.StatusCode == HttpStatusCode.Forbidden &&
         response.Headers.TryGetValues("x-ratelimit-remaining", out var left) && left.FirstOrDefault() == "0");

    /// <summary>The body, or null when it is longer than <see cref="MaxResponseBytes"/> (refused unread when its length says so).</summary>
    private static async Task<byte[]?> ReadBounded(HttpContent content, CancellationToken stop)
    {
        if (content.Headers.ContentLength > MaxResponseBytes) return null;
        await using var stream = await content.ReadAsStreamAsync(stop);
        using var body = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int n;
        while ((n = await stream.ReadAsync(chunk, stop)) > 0)
        {
            if (body.Length + n > MaxResponseBytes) return null;
            body.Write(chunk, 0, n);
        }
        return body.ToArray();
    }

    /// <summary>
    /// GitHub's latest release: a stable SemVer tag and that release's own page (<see cref="ReleasePage.For"/>, so a notice
    /// never names one release and opens another), or why not.
    /// </summary>
    private static Answer ParseRelease(byte[] json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Answer.Fail("the answer is not a release");
            string? tag = Text(root, "tag_name");
            if (!SemVersion.TryParse(tag, out var version)) return Answer.Fail("the release tag is not a version");
            if (version.IsPreRelease || Flag(root, "prerelease") || Flag(root, "draft")) return Answer.NotStable;
            string? page = Text(root, "html_url");
            return page == ReleasePage.For(tag) && ReleasePage.IsValid(page)
                ? new(version, page, null)
                : Answer.Fail("the release page is not PoolWatch's page for that release");
        }
        catch (JsonException) { return Answer.Fail("the answer is not valid JSON"); }

        static bool Flag(JsonElement o, string name) => o.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;
    }

    private static string? Text(JsonElement o, string name) =>
        o.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private UpdateRelease? NewerThanRunning() =>
        _latest is { } l && l.version.CompareTo(_running) > 0 ? new UpdateRelease(l.version.ToString(), l.page) : null;

    /// <summary>Failures are quiet: the first is logged, later ones only after a check has worked again.</summary>
    private void LogFailure(string message)
    {
        if (_failureLogged) return;
        _failureLogged = true;
        _log.Info(OneLine(message) + " (not logged again until a check works)");
    }

    /// <summary>
    /// A message for the log as one line of visible characters, as an error's text can come from the network: every
    /// control character (a carriage return, an escape, C1 codes), line and paragraph separator and formatting character
    /// (such as a direction override, or a tag character beyond U+FFFF, which a reader does not see) becomes "?", and so
    /// does a surrogate without its pair. Judged by Unicode character, not UTF-16 unit, and cut at
    /// <see cref="MaxLoggedChars"/> without splitting one.
    /// </summary>
    internal static string OneLine(string text)
    {
        var line = new StringBuilder(Math.Min(text.Length, MaxLoggedChars) + 3);
        var rest = text.AsSpan();
        while (!rest.IsEmpty)
        {
            bool whole = Rune.DecodeFromUtf16(rest, out var rune, out int used) == System.Buffers.OperationStatus.Done;
            bool hidden = !whole || Rune.IsControl(rune) ||
                Rune.GetUnicodeCategory(rune) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Format;
            if (line.Length + (hidden ? 1 : used) > MaxLoggedChars) return line.Append("...").ToString();
            if (hidden) line.Append('?');
            else line.Append(rest[..used]);
            rest = rest[used..];
        }
        return line.ToString();
    }

    /// <summary>The cache file: when the last check ran, its outcome, and the latest stable release seen. Nothing about the PC.</summary>
    private void Save(UpdateResult result)
    {
        try
        {
            string json = JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["checked"] = result.CheckedUtc.ToString("o", CultureInfo.InvariantCulture), ["result"] = result.Outcome.ToString(),
                ["latest"] = _latest?.version.ToString(), ["page"] = _latest?.page,
            });
            File.WriteAllText(_paths.Guard.CheckReal(StateFile + ".tmp"), json);
            File.Move(_paths.Guard.CheckReal(StateFile + ".tmp"), _paths.Guard.CheckReal(StateFile), overwrite: true);
        }
        catch (Exception ex) { _log.Info(OneLine("update check result not saved: " + ex.Message)); }
    }

    /// <summary>
    /// Reads the cache file as untrusted: size-bounded, and every field checked again, so a file with anything out of
    /// place is ignored whole (the next check then comes a few minutes after start-up). The saved check, or null.
    /// </summary>
    private UpdateResult? Load()
    {
        try
        {
            var file = new FileInfo(StateFile);
            if (!file.Exists || file.Length > MaxStateBytes) return null;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(file.FullName));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !DateTime.TryParse(Text(root, "checked"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var checkedUtc) ||
                !Enum.TryParse(Text(root, "result"), out UpdateOutcome saved) || !Enum.IsDefined(saved)) return null;
            string? latestText = Text(root, "latest"), page = Text(root, "page");
            if (latestText is not null || page is not null) // none yet when no check has worked
            {
                if (!SemVersion.TryParse(latestText, out var latest) || latest.IsPreRelease || !ReleasePage.IsValid(page)) return null;
                _latest = (latest, page!);
            }
            Available = NewerThanRunning();
            _failureLogged = saved == UpdateOutcome.Failed;
            // A newer release saved before PoolWatch itself was updated is no longer newer.
            var outcome = saved == UpdateOutcome.Failed ? saved : Available is null ? UpdateOutcome.UpToDate : UpdateOutcome.Newer;
            return LastResult = new UpdateResult(checkedUtc, outcome);
        }
        catch (Exception ex)
        {
            _log.Info(OneLine("update check: the saved result was not read: " + ex.Message));
            return null;
        }
    }

    /// <summary>A plain handler for the one request: no cookies, and no redirects (the address is fixed, so one is a failure, never followed).</summary>
    internal static SocketsHttpHandler NewHandler() => new() { AllowAutoRedirect = false, UseCookies = false };

    /// <summary>Stops the timer and a check in flight.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
        }
        // Cancelled, not disposed: a check that is just finishing may still read its token.
        _stop.Cancel();
    }
}
