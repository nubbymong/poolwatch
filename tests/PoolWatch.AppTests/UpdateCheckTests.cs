using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using PoolWatch.Core.Updates;
using PoolWatch.Engine;

namespace PoolWatch.AppTests;

/// <summary>
/// Update notifications: what each answer from GitHub leads to, the remembered result and the schedule. GitHub is a fake
/// HttpMessageHandler here: no test reaches the network.
/// </summary>
public sealed class UpdateCheckTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pw-update-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly Log _log;
    private readonly DateTime _now = new(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);

    public UpdateCheckTests()
    {
        _paths = new AppPaths(Path.Combine(_root, "data"), null, secure: false);
        _log = new Log(Path.Combine(_root, "data", "test.log"));
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    /// <summary>Answers each request from a list (the last answer repeats) and keeps what each request carried.</summary>
    private sealed class FakeGitHub(params Func<CancellationToken, Task<HttpResponseMessage>>[] answers) : HttpMessageHandler
    {
        private int _next;
        public readonly List<string> Requests = new();
        public int Disposed;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            lock (Requests) Requests.Add($"{request.Method} {request.RequestUri}\n{request.Headers}");
            return answers[Math.Min(_next++, answers.Length - 1)](token);
        }

        /// <summary>Counted, and the fake stays usable: each check disposes its handler, and here one fake serves them all.</summary>
        protected override void Dispose(bool disposing) => Disposed++;
    }

    /// <summary>An answer with a new body each time (each check disposes the response it read).</summary>
    private static Func<CancellationToken, Task<HttpResponseMessage>> Status(HttpStatusCode code, Func<HttpContent>? content = null) =>
        _ => Task.FromResult(new HttpResponseMessage(code) { Content = content?.Invoke() ?? new StringContent("") });

    private static Func<CancellationToken, Task<HttpResponseMessage>> Body(string json) =>
        Status(HttpStatusCode.OK, () => new StringContent(json, Encoding.UTF8, "application/json"));

    private static string Release(string tag, string? page = null, bool prerelease = false, bool draft = false) =>
        JsonSerializer.Serialize(new
        {
            tag_name = tag, html_url = page ?? ReleasePage.Prefix + "tag/" + tag, prerelease, draft, name = "PoolWatch " + tag,
            body = "What changed.", assets = Array.Empty<object>(),
        });

    private static SemVersion V(string text) => SemVersion.TryParse(text, out var v) ? v : throw new ArgumentException(text);

    private UpdateCheck NewCheck(FakeGitHub github, string running = "1.0.0", Func<bool>? enabled = null, TimeSpan? timeout = null,
        Func<DateTime>? clock = null) =>
        new(_paths, _log, enabled ?? (() => true), () => github, V(running), clock ?? (() => _now), timeout ?? TimeSpan.FromSeconds(30));

    private string LogFile => Path.Combine(_root, "data", "test.log");

    private int LoggedFailures() => File.ReadAllLines(LogFile).Count(l => l.Contains("update check failed", StringComparison.Ordinal));

    [Fact]
    public async Task ANewerReleaseIsAvailableAndAnnouncedOncePerVersion()
    {
        var github = new FakeGitHub(Body(Release("v1.2.0")), Body(Release("v1.2.0")), Body(Release("v1.3.0")));
        using var check = NewCheck(github);
        var found = new List<UpdateRelease>();
        check.Found += found.Add;

        var result = await check.CheckNow(CancellationToken.None);

        Assert.Equal(new UpdateResult(_now, UpdateOutcome.Newer), result);
        Assert.Equal(new UpdateRelease("1.2.0", "https://github.com/nubbymong/poolwatch/releases/tag/v1.2.0"), check.Available);
        Assert.Equal("PoolWatch 1.2.0 is available", check.Available!.Text);
        Assert.Equal(check.Available, Assert.Single(found));

        // One plain GET of the latest stable release: a User-Agent, GitHub's media type, no credentials, no cookies. The
        // repository is asked for by its number, which a rename or a new owner of the name cannot give to another one.
        string request = Assert.Single(github.Requests);
        Assert.StartsWith("GET https://api.github.com/repositories/1435312349/releases/latest\n", request);
        Assert.Contains("User-Agent: PoolWatch/1.0.0", request);
        Assert.Contains("Accept: application/vnd.github+json", request);
        Assert.DoesNotContain("Authorization", request);
        Assert.DoesNotContain("Cookie", request);
        Assert.Equal(1, github.Disposed); // nothing stays open until the next day

        await check.CheckNow(CancellationToken.None); // the same release again: no second announcement
        Assert.Single(found);
        await check.CheckNow(CancellationToken.None); // a newer one still
        Assert.Equal(["1.2.0", "1.3.0"], found.Select(r => r.Version));
        Assert.Equal("1.3.0", check.Available!.Version);
    }

    [Fact]
    public async Task AReleaseTakenBackDoesNotAnnounceTheOlderOneAgain()
    {
        using var check = NewCheck(new FakeGitHub(Body(Release("v1.3.0")), Body(Release("v1.2.0"))));
        var found = new List<UpdateRelease>();
        check.Found += found.Add;

        await check.CheckNow(CancellationToken.None);
        await check.CheckNow(CancellationToken.None); // 1.3.0 was taken back: GitHub's latest is 1.2.0 again

        Assert.Equal("1.3.0", Assert.Single(found).Version);
        Assert.Equal("1.2.0", check.Available?.Version);
    }

    [Theory]
    [InlineData("v1.0.0")]
    [InlineData("1.0.0")]
    [InlineData("v0.9.9")]
    [InlineData("v1.0.0-rc.2")] // older than the release running, and a pre-release besides
    public async Task TheSameOrAnOlderReleaseIsUpToDate(string tag)
    {
        using var check = NewCheck(new FakeGitHub(Body(Release(tag))));
        bool found = false;
        check.Found += _ => found = true;

        Assert.Equal(UpdateOutcome.UpToDate, (await check.CheckNow(CancellationToken.None)).Outcome);
        Assert.Null(check.Available);
        Assert.False(found);
    }

    [Theory]
    [InlineData("v2.0.0-rc.1", false, false)]
    [InlineData("v2.0.0-beta", false, false)]
    [InlineData("v2.0.0", true, false)]  // marked as a pre-release on GitHub
    [InlineData("v2.0.0", false, true)]  // a draft
    public async Task APreReleaseIsNeverOffered(string tag, bool prerelease, bool draft)
    {
        using var check = NewCheck(new FakeGitHub(Body(Release(tag, prerelease: prerelease, draft: draft))));

        Assert.Equal(UpdateOutcome.UpToDate, (await check.CheckNow(CancellationToken.None)).Outcome);
        Assert.Null(check.Available);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"tag_name":"latest","html_url":"https://github.com/nubbymong/poolwatch/releases/tag/latest"}""")]
    [InlineData("""{"tag_name":2,"html_url":"https://github.com/nubbymong/poolwatch/releases/tag/v2.0.0"}""")]
    [InlineData("""{"tag_name":"v2.0.0"}""")]
    [InlineData("""{"tag_name":"v2.0.0+build.1","html_url":"https://github.com/nubbymong/poolwatch/releases/tag/v2.0.0+build.1"}""")] // a page the link check refuses
    [InlineData("""{"tag_name":"v2.0.0","html_url":null}""")]
    [InlineData("""{"tag_name":"v2.0.0","html_url":"https://github.com/nubbymong/poolwatch/releases/tag/v2.0.0" """)] // cut off
    public async Task AMalformedAnswerFailsQuietly(string json)
    {
        using var check = NewCheck(new FakeGitHub(Body(json)));
        bool found = false;
        check.Found += _ => found = true;

        Assert.Equal(UpdateOutcome.Failed, (await check.CheckNow(CancellationToken.None)).Outcome);
        Assert.Null(check.Available);
        Assert.False(found);
        Assert.Equal(1, LoggedFailures());
    }

    [Theory]
    [InlineData("https://evil.example/nubbymong/poolwatch/releases/tag/v2.0.0")]
    [InlineData("http://github.com/nubbymong/poolwatch/releases/tag/v2.0.0")]
    [InlineData("https://github.com/someone/poolwatch/releases/tag/v2.0.0")]
    [InlineData("https://github.com/nubbymong/poolwatch/releases/../../../someone/else")]
    [InlineData("https://github.com/nubbymong/poolwatch/releases/tag/v2.0.0?download=1")]
    [InlineData("https://github.com/nubbymong/poolwatch/releases/tag/v2.0.0\" x")]
    [InlineData("https://github.com/nubbymong/poolwatch/releases/download/v2.0.0/PoolWatch-v2.0.0-win-x64.zip")] // a download
    [InlineData("https://github.com/nubbymong/poolwatch/releases/tag/v1.9.0")] // another release's page
    public async Task AReleasePageThatIsNotPoolWatchsIsRefused(string page)
    {
        using var check = NewCheck(new FakeGitHub(Body(Release("v2.0.0", page))));

        Assert.Equal(UpdateOutcome.Failed, (await check.CheckNow(CancellationToken.None)).Outcome);
        Assert.Null(check.Available);
    }

    /// <summary>A body of exactly <paramref name="bytes"/> bytes: a valid release padded with spaces.</summary>
    private static byte[] Padded(int bytes)
    {
        var json = Encoding.UTF8.GetBytes(Release("v2.0.0"));
        var body = new byte[bytes];
        json.CopyTo(body, 0);
        body.AsSpan(json.Length).Fill((byte)' ');
        return body;
    }

    /// <summary>A body that does not say how long it is (as a chunked answer would not).</summary>
    private sealed class UnknownLength(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    /// <summary>A body whose length says it is over the limit; notes whether it was read.</summary>
    private sealed class TooLong : HttpContent
    {
        public bool WasRead;

        protected override bool TryComputeLength(out long length)
        {
            length = UpdateCheck.MaxResponseBytes + 1L;
            return true;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            WasRead = true;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task AnOversizedAnswerIsRefused()
    {
        var tooLong = new TooLong();
        var github = new FakeGitHub(
            Status(HttpStatusCode.OK, () => new ByteArrayContent(Padded(UpdateCheck.MaxResponseBytes))),
            Status(HttpStatusCode.OK, () => tooLong),
            Status(HttpStatusCode.OK, () => new StreamContent(new UnknownLength(Padded(UpdateCheck.MaxResponseBytes + 1)))));
        using var check = NewCheck(github, running: "2.0.0");

        Assert.Equal(UpdateOutcome.UpToDate, (await check.CheckNow(CancellationToken.None)).Outcome); // exactly the limit: read
        Assert.Equal(UpdateOutcome.Failed, (await check.CheckNow(CancellationToken.None)).Outcome);   // its length says too long
        Assert.False(tooLong.WasRead);                                                                 // so it is refused unread
        Assert.Equal(UpdateOutcome.Failed, (await check.CheckNow(CancellationToken.None)).Outcome);   // no length: stopped at the limit
    }

    [Fact]
    public async Task NoAnswerInTimeFailsQuietly()
    {
        var github = new FakeGitHub(async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var check = NewCheck(github, timeout: TimeSpan.FromMilliseconds(100));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        Assert.Equal(UpdateOutcome.Failed, (await check.CheckNow(CancellationToken.None)).Outcome);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Equal(1, LoggedFailures());
    }

    [Fact]
    public async Task StoppingDuringACheckIsQuiet()
    {
        var inFlight = new TaskCompletionSource();
        var github = new FakeGitHub(async token =>
        {
            inFlight.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var check = NewCheck(github);

        var run = check.RunDue();
        await inFlight.Task;
        check.Dispose(); // PoolWatch exits while the request is in flight
        await run;

        Assert.Single(github.Requests);
        Assert.Null(check.LastResult);
        Assert.False(File.Exists(LogFile)); // nothing logged: an exit is not a failed check
    }

    [Fact]
    public void TheRealHandlerFollowsNoRedirectsAndSendsNoCredentials()
    {
        using var handler = UpdateCheck.NewHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.Null(handler.Credentials);
        Assert.Null(handler.DefaultProxyCredentials);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression); // a compressed answer cannot pass the size limit
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);     // the default certificate check
    }

    [Fact]
    public async Task FailuresAreQuietAndLoggedOnceUntilACheckWorks()
    {
        var github = new FakeGitHub(
            Status(HttpStatusCode.Forbidden), Status(HttpStatusCode.TooManyRequests), _ => throw new HttpRequestException("offline"),
            Status(HttpStatusCode.NotFound), Status(HttpStatusCode.MovedPermanently), Status(HttpStatusCode.InternalServerError),
            Body(Release("v1.0.0")), Status(HttpStatusCode.TooManyRequests));
        using var check = NewCheck(github);

        for (int i = 0; i < 6; i++) Assert.Equal(UpdateOutcome.Failed, (await check.CheckNow(CancellationToken.None)).Outcome);
        Assert.Equal(1, LoggedFailures());
        Assert.Equal(UpdateOutcome.UpToDate, (await check.CheckNow(CancellationToken.None)).Outcome);
        Assert.Equal(UpdateOutcome.Failed, (await check.CheckNow(CancellationToken.None)).Outcome);
        Assert.Equal(2, LoggedFailures()); // logged again after the check that worked
    }

    private const char LineSeparator = (char)0x2028, ParagraphSeparator = (char)0x2029;

    /// <summary>A body whose read fails part way, with the error given.</summary>
    private sealed class CutOff(Exception error) : MemoryStream([1, 2, 3])
    {
        public override bool CanSeek => false;
        public override int Read(byte[] buffer, int offset, int count) => throw error;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => throw error;
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => throw error;
    }

    [Fact]
    public async Task ErrorTextReachesTheLogOnOneShortLine()
    {
        // Text an error can carry from the network: a carriage return, an escape, a next-line and a line separator, then a long tail.
        string text = "one\rtwo\u001b[0m\u0085three" + LineSeparator + "four" + new string('y', 5000);
        // A word only the two network errors carry: their text is never logged, filtered or not.
        const string FromTheNetwork = "sentfromthenetwork";
        var works = Body(Release("v1.0.0")); // a check that works, so the next failure is logged again
        var github = new FakeGitHub(
            _ => throw new HttpRequestException(FromTheNetwork + text), works,
            Status(HttpStatusCode.OK, () => new StreamContent(new CutOff(new IOException(FromTheNetwork + text)))), works,
            _ => throw new InvalidOperationException(text));
        using var check = NewCheck(github);

        for (int i = 0; i < 4; i++) await check.CheckNow(CancellationToken.None);
        await check.RunDue(); // an error the check does not expect, caught where the timer runs it

        string log = File.ReadAllText(LogFile);
        var lines = log.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Count(l => l.Contains("update check failed", StringComparison.Ordinal)));
        Assert.DoesNotContain(FromTheNetwork, log, StringComparison.Ordinal);
        Assert.Contains("one?two?[0m?three", log, StringComparison.Ordinal); // the unexpected error's text, filtered
        Assert.All(lines, l => Assert.False(l.Any(c => char.IsControl(c) || c is LineSeparator or ParagraphSeparator), "a control character in: " + l.Length));
        Assert.All(lines, l => Assert.True(l.Length <= 300, "a line of " + l.Length));
    }

    [Fact]
    public async Task ANotFoundAnswerIsLoggedAsNotFound()
    {
        using var check = NewCheck(new FakeGitHub(Status(HttpStatusCode.NotFound)));

        Assert.Equal(UpdateOutcome.Failed, (await check.CheckNow(CancellationToken.None)).Outcome);
        Assert.Contains("update check failed: not found (404): no release yet, or GitHub no longer answers this address", File.ReadAllText(LogFile));
    }

    [Theory]
    [InlineData("plain words", "plain words")]
    [InlineData("a\rb\nc\td\u001be\u007ff\u0085g", "a?b?c?d?e?f?g")] // control characters, C1 included
    public void LoggedTextIsOneLineOfVisibleCharacters(string text, string logged) => Assert.Equal(logged, UpdateCheck.OneLine(text));

    [Fact]
    public void SeparatorsAndDirectionOverridesAreNotLoggedEither() =>
        Assert.Equal("a?b?c?d", UpdateCheck.OneLine(string.Concat("a", LineSeparator, "b", ParagraphSeparator, "c", (char)0x202e, "d")));

    [Fact]
    public void FormattingCharactersAboveUFFFFAndLoneSurrogatesAreNotLoggedEither()
    {
        // A tag character (U+E0041) is unseen by a reader and read by an AI model; it is two UTF-16 units, one character.
        Assert.Equal("a?b", UpdateCheck.OneLine("a\U000E0041b"));
        Assert.Equal("a?b?c", UpdateCheck.OneLine("a\uD800b\uDC41c")); // halves of no pair
        Assert.Equal("end?", UpdateCheck.OneLine("end\uD800"));
        Assert.Equal("ok \U0001F600", UpdateCheck.OneLine("ok \U0001F600")); // a visible character beyond U+FFFF stays
    }

    [Fact]
    public void LoggedTextIsBounded()
    {
        Assert.Equal(new string('x', UpdateCheck.MaxLoggedChars) + "...", UpdateCheck.OneLine(new string('x', 5000)));
        // Cut before a character that would not fit whole, never between the halves of a pair.
        string x = new('x', UpdateCheck.MaxLoggedChars - 1);
        Assert.Equal(x + "...", UpdateCheck.OneLine(x + "\U0001F600"));
        Assert.Equal(x + "?", UpdateCheck.OneLine(x + "\U000E0041")); // shown as one "?", which fits
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, null, "GitHub's rate limit was reached")]
    [InlineData(HttpStatusCode.Forbidden, "0", "GitHub's rate limit was reached")]
    [InlineData(HttpStatusCode.Forbidden, "59", "the request was refused (403)")]
    [InlineData(HttpStatusCode.Forbidden, null, "the request was refused (403)")] // a proxy or a firewall on the way
    public async Task TheRateLimitIsToldApartFromARefusal(HttpStatusCode code, string? remaining, string logged)
    {
        using var check = NewCheck(new FakeGitHub(_ =>
        {
            var response = new HttpResponseMessage(code);
            if (remaining is not null) response.Headers.Add("x-ratelimit-remaining", remaining);
            return Task.FromResult(response);
        }));

        Assert.Equal(UpdateOutcome.Failed, (await check.CheckNow(CancellationToken.None)).Outcome);
        Assert.Contains("update check failed: " + logged, File.ReadAllText(LogFile));
    }

    [Fact]
    public async Task AFailedCheckKeepsTheNewerReleaseItKnows()
    {
        using var check = NewCheck(new FakeGitHub(Body(Release("v1.2.0")), _ => throw new HttpRequestException("offline")));
        await check.CheckNow(CancellationToken.None);

        Assert.Equal(UpdateOutcome.Failed, (await check.CheckNow(CancellationToken.None)).Outcome);
        Assert.Equal("1.2.0", check.Available?.Version);
    }

    [Fact]
    public async Task TheLastAnswerIsRememberedAcrossRestarts()
    {
        using (var first = NewCheck(new FakeGitHub(Body(Release("v1.2.0")))))
            await first.CheckNow(CancellationToken.None);

        // The saved file: the check and the release, nothing about the PC.
        string saved = File.ReadAllText(Path.Combine(_paths.CacheDir, UpdateCheck.StateName));
        using (var doc = JsonDocument.Parse(saved))
            Assert.Equal(["checked", "result", "latest", "page"], doc.RootElement.EnumerateObject().Select(p => p.Name));

        // A restart: the notice is back at once, without asking GitHub (the first check is minutes away) or announcing it again.
        var github = new FakeGitHub(Body(Release("v1.2.0")));
        using (var restarted = NewCheck(github))
        {
            bool found = false;
            restarted.Found += _ => found = true;
            restarted.Start();
            Assert.Equal("1.2.0", restarted.Available?.Version);
            Assert.Equal(new UpdateResult(_now, UpdateOutcome.Newer), restarted.LastResult);
            Assert.Empty(github.Requests);
            Assert.False(found);
        }

        // After updating to that release, it is no longer newer.
        using var updated = NewCheck(github, running: "1.2.0");
        updated.Start();
        Assert.Null(updated.Available);
        Assert.Equal(UpdateOutcome.UpToDate, updated.LastResult?.Outcome);
    }

    [Fact]
    public async Task AFailedCheckIsRememberedAndNotLoggedAgainAfterARestart()
    {
        using (var first = NewCheck(new FakeGitHub(Status(HttpStatusCode.TooManyRequests))))
            await first.CheckNow(CancellationToken.None);

        using var restarted = NewCheck(new FakeGitHub(Status(HttpStatusCode.TooManyRequests)));
        restarted.Start();
        Assert.Equal(new UpdateResult(_now, UpdateOutcome.Failed), restarted.LastResult);
        Assert.Null(restarted.Available);

        await restarted.CheckNow(CancellationToken.None);
        Assert.Equal(1, LoggedFailures());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"checked":"2026-10-28T09:00:00Z","result":"Newer","latest":"9.0.0","page":"https://evil.example/x"}""")]
    [InlineData("""{"checked":"2026-10-28T09:00:00Z","result":"Newer","latest":"9.0.0-rc.1","page":"https://github.com/nubbymong/poolwatch/releases/tag/v9.0.0-rc.1"}""")]
    [InlineData("""{"checked":"2026-10-28T09:00:00Z","result":"Newer","latest":"9.0.0"}""")]
    [InlineData("""{"checked":"yesterday","result":"Newer","latest":"9.0.0","page":"https://github.com/nubbymong/poolwatch/releases/tag/v9.0.0"}""")]
    [InlineData("""{"checked":"2026-10-28T09:00:00Z","result":"Sideways","latest":"9.0.0","page":"https://github.com/nubbymong/poolwatch/releases/tag/v9.0.0"}""")]
    public void ASavedFileWithAnythingOutOfPlaceIsIgnored(string content)
    {
        Directory.CreateDirectory(_paths.CacheDir);
        File.WriteAllText(Path.Combine(_paths.CacheDir, UpdateCheck.StateName), content);
        using var check = NewCheck(new FakeGitHub(Body(Release("v1.0.0"))));

        check.Start();

        Assert.Null(check.Available);
        Assert.Null(check.LastResult);
    }

    [Fact]
    public void AnOversizedSavedFileIsIgnored()
    {
        Directory.CreateDirectory(_paths.CacheDir);
        string release = $$"""{"checked":"2026-10-28T09:00:00Z","result":"Newer","latest":"9.0.0","page":"{{ReleasePage.Prefix}}tag/v9.0.0"}""";
        File.WriteAllText(Path.Combine(_paths.CacheDir, UpdateCheck.StateName), release + new string(' ', 5000));
        using var check = NewCheck(new FakeGitHub(Body(Release("v1.0.0"))));

        check.Start();

        Assert.Null(check.Available);
        Assert.Null(check.LastResult);
    }

    [Fact]
    public void OnlyTheFirstStartReadsTheSavedFile()
    {
        using var started = NewCheck(new FakeGitHub(Body(Release("v1.0.0"))));
        started.Start(); // nothing saved yet
        File.WriteAllText(Path.Combine(_paths.CacheDir, UpdateCheck.StateName),
            $$"""{"checked":"2026-10-28T09:00:00Z","result":"Newer","latest":"9.0.0","page":"{{ReleasePage.For("v9.0.0")}}"}""");

        started.Start();
        Assert.Null(started.Available);

        var stopped = NewCheck(new FakeGitHub(Body(Release("v1.0.0"))));
        stopped.Dispose();
        stopped.Start();
        Assert.Null(stopped.Available);

        using var restarted = NewCheck(new FakeGitHub(Body(Release("v1.0.0"))));
        restarted.Start(); // the file itself is read
        Assert.Equal("9.0.0", restarted.Available?.Version);
    }

    [Fact]
    public async Task NothingIsSentWhileTheSettingIsOff()
    {
        bool on = false;
        var github = new FakeGitHub(Body(Release("v1.2.0")));
        using var check = NewCheck(github, enabled: () => on);

        await check.RunDue();
        Assert.Empty(github.Requests);
        Assert.Null(check.LastResult);

        on = true; // the check is still due: turning the setting on takes effect at the next wake, not a day later
        await check.RunDue();
        Assert.Single(github.Requests);
        Assert.Equal("1.2.0", check.Available?.Version);
    }

    [Fact]
    public async Task TheDailyCheckFollowsTheClockAlsoOnAPCThatSleeps()
    {
        var now = _now;
        var github = new FakeGitHub(Body(Release("v1.0.0")));
        using var check = NewCheck(github, clock: () => now);

        await check.RunDue(); // never checked: due at once
        Assert.Single(github.Requests);

        now = now.AddHours(23); // a wake before the day is out: nothing is sent
        await check.RunDue();
        Assert.Single(github.Requests);

        now = now.AddHours(2); // a day and an hour after the check, past any jitter
        await check.RunDue();
        Assert.Equal(2, github.Requests.Count);

        now = now.AddDays(3); // three days asleep: checked at the first wake after it
        await check.RunDue();
        Assert.Equal(3, github.Requests.Count);

        now = now.AddDays(-10); // the clock went back: due, not ten days away
        await check.RunDue();
        Assert.Equal(4, github.Requests.Count);
    }

    [Fact]
    public void TheTimerLooksAtTheClockAtLeastEverySixHours()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), UpdateCheck.NextWake(_now.AddMinutes(5), _now)); // due soon: wakes then
        Assert.Equal(TimeSpan.FromHours(6), UpdateCheck.NextWake(_now.AddDays(1), _now));      // due in a day
        Assert.Equal(TimeSpan.FromHours(6), UpdateCheck.NextWake(_now.AddHours(-1), _now));    // due, but the setting is off
    }

    [Fact]
    public void TheFirstCheckWaitsAFewMinutesThenADayAfterTheLastOne()
    {
        var now = _now;
        Assert.Equal(TimeSpan.FromMinutes(3), UpdateCheck.FirstDelay(null, now, 0));
        Assert.Equal(TimeSpan.FromMinutes(6), UpdateCheck.FirstDelay(null, now, 1));
        Assert.Equal(TimeSpan.FromHours(22), UpdateCheck.FirstDelay(now.AddHours(-2), now, 0));           // a day after the last
        Assert.Equal(TimeSpan.FromMinutes(3), UpdateCheck.FirstDelay(now.AddHours(-30), now, 0));         // overdue: soon, not at once
        Assert.Equal(TimeSpan.FromMinutes(3), UpdateCheck.FirstDelay(now.AddMinutes(-1439), now, 0));     // due in a minute: after start-up
        Assert.Equal(TimeSpan.FromMinutes(3), UpdateCheck.FirstDelay(now.AddDays(5), now, 0));            // the clock went back
    }

    [Fact]
    public void TheRunningVersionIsTheBuiltRelease()
    {
        // What the build stamps ("1.0.0+<commit>", or "1.1.0-rc.1+<commit>" for a pre-release), without the build metadata.
        string stamped = typeof(UpdateCheck).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.Equal(stamped.Split('+')[0], UpdateCheck.RunningVersion().ToString());
    }
}
