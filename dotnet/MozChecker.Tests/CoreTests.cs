using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MozChecker.Core;
using Xunit;

namespace MozChecker.Tests;

public sealed class CoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "moz-core-" + Guid.NewGuid().ToString("N"));
    private Store Open() => new(Path.Combine(directory, "checker.sqlite3"));
    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, true);
    }
    [Theory]
    [InlineData("https://www.GOOGLE.com/path", "google.com")]
    [InlineData("bücher.de", "xn--bcher-kva.de")]
    [InlineData("EXAMPLE.co.id.", "example.co.id")]
    public void NormalizeRegistrable(string input, string expected) => Assert.Equal(expected, Domains.Normalize(input));
    [Theory]
    [InlineData("co.id")]
    [InlineData("sub.google.com")]
    [InlineData("127.0.0.1")]
    [InlineData("google.com:80")]
    [InlineData("https://user:pass@google.com")]
    [InlineData("example.123")]
    public void RejectNonBuyableInput(string input) => Assert.Throws<ArgumentException>(() => Domains.Normalize(input));
    [Fact]
    public void PublicSuffixWildcardsAndExceptions()
    {
        Assert.False(Domains.Registrable("test.ck"));
        Assert.True(Domains.Registrable("www.ck"));
        Assert.True(Domains.Registrable("example.co.id"));
        Assert.False(Domains.Registrable("host.example.co.id"));
        Assert.False(Domains.Registrable("example.unsupportedzzz"));
    }
    [Fact]
    public void CsvHeaderQuotingDedupAndLimit()
    {
        var r = Domains.Parse("score,domain\r\n10,\"google.com\"\r\n20,GOOGLE.com\r\n30,co.id");
        Assert.Equal(new[] { "google.com" }, r.Domains);
        Assert.Equal(1, r.Duplicates);
        Assert.Single(r.Invalid);
        Assert.Equal(1001, Domains.Parse(string.Join('\n', Enumerable.Range(0, 1001).Select(i => $"d{i}.com"))).Domains.Count);
        Assert.Throws<ArgumentException>(() => Domains.Parse("\"google.com"));
    }
    [Theory]
    [InlineData("DA;Domain Name;Notes\r\n90;https://www.GOOGLE.com/path;test\r\n80;google.com;duplicate\r\n70;co.id;invalid")]
    [InlineData("DA\tWebsite URL\tNotes\r\n90\thttps://www.GOOGLE.com/path\ttest\r\n80\tgoogle.com\tduplicate\r\n70\tco.id\tinvalid")]
    [InlineData("\uFEFFsep=;\r\nDA;domain_name;Notes\r\n90;https://www.GOOGLE.com/path;test\r\n80;google.com;duplicate\r\n70;co.id;invalid")]
    [InlineData("DA,DOMAIN,Notes\r\n90,\"https://www.GOOGLE.com/path\",\"text, with comma\nand newline\"\r\n80,google.com,duplicate\r\n70,co.id,invalid")]
    public void CommonCsvExportsExtractOnlyDomainColumn(string text)
    {
        var imported = Domains.Parse(text, limit: null);
        Assert.Equal(new[] { "google.com" }, imported.Domains);
        Assert.Equal(1, imported.Duplicates);
        Assert.Equal(new[] { "co.id" }, imported.Invalid);
    }
    [Fact]
    public void ExplicitDomainColumnTakesPriorityOverPageUrl()
    {
        var imported = Domains.Parse("URL,Domain Name,DA\nhttps://sub.google.com/path,google.com,90", limit: null);
        Assert.Equal(new[] { "google.com" }, imported.Domains);
        Assert.Empty(imported.Invalid);
    }
    [Fact]
    public void HeaderlessCsvWithMetricsInfersDomainColumn()
    {
        var imported = Domains.Parse("80,google.com,20\n90,example.org,30", limit: null);
        Assert.Equal(new[] { "google.com", "example.org" }, imported.Domains);
        Assert.Empty(imported.Invalid);
        var allCells = Domains.Parse("google.com,example.org\nbing.com,example.net", limit: null);
        Assert.Equal(4, allCells.Domains.Count);
    }
    [Fact]
    public void LargeImportIsDeduplicatedBeforeBatchingWithoutTruncation()
    {
        using var store = Open();
        var input = string.Join('\n', Enumerable.Range(0, 2501).Select(i => $"import{i}.com")) + "\nIMPORT0.com\nco.id";
        var imported = Domains.Parse(input, limit: null);
        Assert.Equal(2501, imported.Domains.Count);
        Assert.Equal(1, imported.Duplicates);
        Assert.Single(imported.Invalid);
        var jobs = store.NewJobs(imported.Domains);
        Assert.Single(jobs);
        Assert.Equal(new[] { 2501 }, jobs.Select(id => store.Results(id).Count));
        Assert.Equal(imported.Domains, jobs.SelectMany(store.Results).Select(r => r.Domain));
        Assert.All(store.Jobs(), j => Assert.Equal("queued", j.State));
    }
    [Fact]
    public void NewJobsRejectsEmptyListWithoutCreatingPartialQueue()
    {
        using var store = Open();
        Assert.Throws<ArgumentException>(() => store.NewJobs([]));
        Assert.Empty(store.Jobs());
    }
    [Fact]
    public void AvailableRequiresFreshProofAndHistory()
    {
        var r = new Result { Domain = "test.com", Status = "available", AvailabilityAt = JsonData.Now(), History = "no_history" };
        Assert.True(r.Ready);
        r.History = "unchecked";
        Assert.False(r.Ready);
        r.History = "valid";
        r.AvailabilityAt = DateTimeOffset.UtcNow.AddMinutes(-16).ToString("O");
        Assert.False(r.Ready);
        r.AvailabilityAt = DateTimeOffset.UtcNow.AddDays(1).ToString("O");
        Assert.False(r.Ready);
    }
    [Theory]
    [InlineData(404, "{\"errorCode\":404}", "nxdomain", "available")]
    [InlineData(404, "{}", "nxdomain", "unknown")]
    [InlineData(404, "{\"errorCode\":404}", "error", "unknown")]
    [InlineData(404, "{\"errorCode\":404}", "exists", "unknown")]
    [InlineData(200, "{}", "nxdomain", "unknown")]
    [InlineData(200, "{\"objectClassName\":\"domain\",\"ldhName\":\"test.com\"}", "nxdomain", "registered")]
    [InlineData(200, "{\"objectClassName\":\"domain\",\"ldhName\":\"wrong.com\"}", "nxdomain", "unknown")]
    [InlineData(200, "{\"objectClassName\":\"domain\",\"ldhName\":\"test.com\",\"status\":[\"pending delete\"]}", "exists", "pending_delete")]
    public void RdapDnsVerdict(int status, string json, string dns, string expected) => Assert.Equal(expected, Providers.AvailabilityVerdict(status, JsonNode.Parse(json), dns, "test.com"));
    [Fact]
    public void MissingAndNegativeMetricsStayMissing()
    {
        var values = JsonNode.Parse("{\"zero\":0,\"negative\":-1,\"text\":\"0\",\"big\":101}")!;
        Assert.Equal(0d, Providers.Metric(values["zero"]));
        Assert.Null(Providers.Metric(values["negative"]));
        Assert.Null(Providers.Metric(values["text"]));
        Assert.Null(Providers.Metric(values["missing"]));
        Assert.Null(Providers.Metric(values["big"]));
    }
    [Fact] public void HtmlScriptAndStyleAreNotVisibleText() => Assert.Equal("Clean site", Providers.VisibleText("<html><style>casino</style><script>porn</script><body>Clean site</body></html>").Trim());
    [Fact]
    public void LegacySchemaAndQuotaArePreserved()
    {
        string id;
        using (var store = Open())
        {
            id = store.NewJob(["test.com"]);
            var r = new Result { Domain = "test.com", Status = "available", Da = 41, AvailabilityAt = JsonData.Now(), History = "valid" };
            store.Save(id, r);
            var settings = store.GetSettings();
            settings.MozBudget = 10;
            settings.MozReserve = 2;
            store.Configure(settings);
            Assert.True(store.ReserveRow(settings.Period, 10, 2));
            store.CachePut("opr", "test.com", new
            {
                Opr = 2.4,
                OprAt = JsonData.Now()
            });
        }
        using (var store = Open())
        {
            Assert.Equal("paused", store.Jobs().Single().State);
            Assert.Equal(41, store.Results(id).Single().Da);
            Assert.Equal(10, store.GetSettings().MozBudget);
            Assert.Equal(1, store.Used(store.GetSettings().Period));
            Assert.NotNull(store.CacheGet<JsonNode>("opr", "test.com", 100));
            Assert.True(File.Exists(store.Path + ".before-dotnet.bak"));
        }
        // A Python-style JSON row uses exactly the same snake_case schema, including IDN and missing metrics.
        var python = JsonData.Read<Result>("{\"domain\":\"xn--bcher-kva.de\",\"rdap_http\":404,\"history\":\"valid\",\"moz_state\":\"reserved\",\"da\":null,\"matches\":[{\"keyword\":\"judi\",\"snapshot\":\"https://web.archive.org/web/example\"}],\"errors\":{\"history\":\"timeout\"}}");
        Assert.Equal(404, python.RdapHttp);
        Assert.Null(python.Da);
        Assert.Equal("reserved", python.MozState);
        Assert.Single(python.Matches);
    }
    [Fact]
    public async Task ConcurrentReservationNeverSpendsReserve()
    {
        using var store = Open();
        var attempts = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() => store.ReserveRow("test", 50, 5))));
        Assert.Equal(45, attempts.Count(x => x));
        Assert.Equal(45, store.Used("test"));
    }
    [Fact]
    public void CsvExportsOnlyAvailableAndNeutralizesFormulas()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "export.csv");
        CsvExport.Save(path, [new Result { Domain = "google.com", Status = "registered" }, new Result { Domain = "test.com", Status = "available", Registrar = "=IMPORTXML(1)" }]);
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("google.com", text);
        Assert.Contains("test.com", text);
        Assert.Contains("'=IMPORTXML", text);
        Assert.Equal(new byte[] { 239, 187, 191 }, File.ReadAllBytes(path)[..3]);
        Assert.Contains("availability_at", text);
    }
    [Fact]
    public async Task BilledTransportNeverRetriesOrRedirects()
    {
        var handler = new StubHandler(_ => new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("busy") });
        using var http = new Transport(handler);
        await Assert.ThrowsAsync<SourceException>(() => http.SendAsync(() => new(HttpMethod.Post, "https://lsapi.seomoz.com/v2/url_metrics"), null, CancellationToken.None, attempts: 1));
        Assert.Equal(1, handler.Calls);
    }
    [Fact]
    public async Task BilledThrottleDefersNextDomainWithoutSendingAgain()
    {
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromHours(1));
            return response;
        });
        using var http = new Transport(handler);
        var gate = new RateGate(TimeSpan.Zero);
        await Assert.ThrowsAsync<SourceException>(() => http.SendAsync(() => new(HttpMethod.Post, "https://lsapi.seomoz.com/v2/url_metrics"), gate, CancellationToken.None, attempts: 1));
        await Assert.ThrowsAsync<SourceException>(() => http.SendAsync(() => new(HttpMethod.Post, "https://lsapi.seomoz.com/v2/url_metrics"), gate, CancellationToken.None, attempts: 1));
        Assert.Equal(1, handler.Calls);
    }
    [Fact]
    public async Task OversizedReplyFailsClosed()
    {
        var handler = new StubHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(new string('x', 1000)) });
        using var http = new Transport(handler);
        await Assert.ThrowsAsync<SourceException>(() => http.SendAsync(() => new(HttpMethod.Get, "https://web.archive.org/"), null, CancellationToken.None, maxBytes: 10));
    }
    [Theory]
    [InlineData("[]", true, "no_history")]
    [InlineData("[[\"timestamp\",\"original\"]]", false, "unchecked")]
    public async Task EmptyArchiveHonorsUserPolicy(string json, bool allow, string expected)
    {
        using var store = Open();
        using var http = new Transport(new StubHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(json) }));
        var providers = new Providers(store, new(), http);
        var r = new Result { Domain = "test.com" };
        await providers.HistoryAsync(r, new Settings { EmptyValid = allow }, CancellationToken.None);
        Assert.Equal(expected, r.History);
        Assert.Equal(0, r.Snapshots);
    }
    [Fact]
    public async Task ArchiveOverflowCannotBecomeClean()
    {
        using var store = Open();
        var rows = new JsonArray { new JsonArray("timestamp", "original") };
        for (int i = 0; i < 10001; i++)
            rows.Add(new JsonArray("20200101000000", "http://test.com/"));
        using var http = new Transport(new StubHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(rows.ToJsonString()) }));
        var r = new Result { Domain = "test.com" };
        var providers = new Providers(store, new(), http);
        await Assert.ThrowsAsync<SourceException>(() => providers.HistoryAsync(r, new(), CancellationToken.None));
        Assert.Equal("unchecked", r.History);
    }
    [Fact]
    public async Task MalformedOprShapeIsASourceFailure()
    {
        using var store = Open();
        using var http = new Transport(new StubHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent("[]") }));
        var providers = new Providers(store, new("test-opr-key"), http);
        await Assert.ThrowsAsync<SourceException>(() => providers.OprAsync([new Result { Domain = "test.com" }], CancellationToken.None));
    }
    [Fact]
    public async Task IndependentHistoryFailureDoesNotStopOtherCandidatesOrSpendMoz()
    {
        using var store = Open();
        var job = store.NewJob(["registered.com", "unknown.com", "blocked.com", "clean.com"]);
        var fake = new FakeProviders();
        var engine = new Engine(store, new("", "id", "secret"), fake);
        engine.Start();
        await engine.Completion!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("completed", store.Jobs().Single().State);
        var results = store.Results(job);
        Assert.All(results, r => Assert.Equal("done", r.Stage));
        Assert.Equal(new[] { "clean.com" }, fake.MozDomains);
        Assert.Equal(1, store.Used(store.GetSettings().Period));
        Assert.Equal("unchecked", results.Single(r => r.Domain == "blocked.com").History);
    }
    [Fact]
    public async Task AmbiguousPaidRequestIsNotRepeatedOnRetry()
    {
        using var store = Open();
        var job = store.NewJob(["clean.com"]);
        var fake = new FakeProviders { FailMoz = true };
        var engine = new Engine(store, new("", "id", "secret"), fake);
        engine.Start();
        await engine.Completion!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("failed", store.Results(job).Single().MozState);
        store.RetryFailed(job);
        engine.Start();
        await engine.Completion!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(fake.MozDomains);
        Assert.Equal(1, store.Used(store.GetSettings().Period));
    }
    [Fact]
    public async Task StaleCandidateRefreshesBeforeSpending()
    {
        using var store = Open();
        var job = store.NewJob(["clean.com"]);
        var fake = new FakeProviders { BecomeRegistered = true };
        var engine = new Engine(store, new("", "id", "secret"), fake);
        engine.Start();
        await engine.Completion!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(fake.MozDomains);
        Assert.Equal("registered", store.Results(job).Single().Status);
        Assert.Equal(0, store.Used(store.GetSettings().Period));
        Assert.Equal(1, fake.Refreshes);
    }
    [Fact]
    public async Task PausePersistsThenResumeDoesNotRepeatFinishedDomains()
    {
        using var store = Open();
        var job = store.NewJob(["clean.com", "blocked.com"]);
        var fake = new FakeProviders { HoldHistory = true };
        var engine = new Engine(store, new(), fake);
        engine.Start();
        await fake.HistoryEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        engine.Pause();
        await engine.Completion!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("paused", store.Jobs().Single().State);
        fake.HoldHistory = false;
        store.State(job, "queued");
        engine.Start();
        await engine.Completion!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("completed", store.Jobs().Single().State);
        Assert.Empty(fake.MozDomains);
    }
    [Fact]
    public async Task DomainsWaitingForHistoryDoNotCountAsActiveOrBecomeQueueLogRows()
    {
        using var store = Open();
        var id = store.NewJob(Enumerable.Range(0, 100).Select(i => $"waiting{i}.com"));
        var fake = new FakeProviders { HoldHistory = true };
        var engine = new Engine(store, new(), fake);
        engine.Start();
        try
        {
            await fake.HistoryEntered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (store.Page(id, 2, new(), size: 500).Rows.Count(r => r.Stage == "history_queued") != 99 && DateTime.UtcNow < deadline)
                await Task.Delay(20);
            Assert.Equal(99, store.Page(id, 2, new(), size: 500).Rows.Count(r => r.Stage == "history_queued"));
            Assert.Equal(1, store.Counts(id).Active);
            Assert.DoesNotContain(store.Events(id, size: 1000).Rows, e => e.Stage.EndsWith("queued"));
        }
        finally { engine.Pause(); await engine.Completion!.WaitAsync(TimeSpan.FromSeconds(20)); }
    }
    [Fact]
    public async Task OneSessionProcessesPastTheOldThousandDomainBoundary()
    {
        using var store = Open();
        var job = store.NewJob(Enumerable.Range(0, 2501).Select(i => $"beyond{i}.com"));
        var engine = new Engine(store, new(), new FakeProviders());
        engine.Start();
        try
        {
            // Windows runners can spend several minutes flushing the per-stage WAL transactions.
            // This verifies completion beyond 1,000 domains, not a disk throughput target.
            await engine.Completion!.WaitAsync(TimeSpan.FromMinutes(10));
            Assert.Single(store.Jobs());
            Assert.Equal("completed", store.Jobs().Single().State);
            Assert.Equal(2501, store.Counts(job).Done);
            Assert.Equal(0, store.Counts(job).Pending);
            Assert.Equal(2501, store.Page(job, 0, new()).Total);
            Assert.Empty(store.PendingResults(job));
        }
        finally
        {
            engine.Pause();
            await engine.Completion!.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }
    [Fact]
    public async Task ThousandDomainBoundedPipelineCompletesWithoutDeadlock()
    {
        using var store = Open();
        var job = store.NewJob(Enumerable.Range(0, 1000).Select(i => $"candidate{i}.com"));
        var engine = new Engine(store, new(), new FakeProviders());
        engine.Start();
        await engine.Completion!.WaitAsync(TimeSpan.FromSeconds(90));
        Assert.Equal("completed", store.Jobs().Single().State);
        Assert.Equal(1000, store.Results(job).Count(r => r.Stage == "done"));
    }
}
internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public int Calls
    {
        get; private set;
    }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(respond(request));
    }
}
internal sealed class FakeProviders : IProviders
{
    public List<string> MozDomains { get; } = [];
    public bool FailMoz
    {
        get; set;
    }
    public bool BecomeRegistered
    {
        get; set;
    }
    public bool HoldHistory
    {
        get; set;
    }
    public int Refreshes
    {
        get; private set;
    }
    public TaskCompletionSource HistoryEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task AvailabilityAsync(Result r, bool force, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (force)
            Refreshes++;
        r.Status = r.Domain == "registered.com" || force && BecomeRegistered ? "registered" : r.Domain == "unknown.com" ? "unknown" : "available";
        r.AvailabilityAt = JsonData.Now();
        return Task.CompletedTask;
    }
    public Task OprAsync(IReadOnlyList<Result> rows, CancellationToken ct) => Task.CompletedTask;
    public async Task HistoryAsync(Result r, Settings settings, CancellationToken ct)
    {
        HistoryEntered.TrySetResult();
        if (HoldHistory)
            await Task.Delay(Timeout.Infinite, ct);
        if (r.Domain == "blocked.com")
            throw new SourceException("Wayback timeout");
        r.History = "valid";
        r.HistoryAt = JsonData.Now();
        if (BecomeRegistered)
            r.AvailabilityAt = DateTimeOffset.UtcNow.AddHours(-1).ToString("O");
    }
    public Task MozAsync(Result r, CancellationToken ct)
    {
        MozDomains.Add(r.Domain);
        if (FailMoz)
            throw new SourceException("HTTP 503");
        r.Da = 30;
        r.MozAt = JsonData.Now();
        r.MozState = "ok";
        return Task.CompletedTask;
    }
}
