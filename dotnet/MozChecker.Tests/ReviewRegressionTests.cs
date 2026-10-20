using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using MozChecker.Core;
using Xunit;

namespace MozChecker.Tests;

public sealed class ReviewRegressionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "moz-review-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(directory, "checker.sqlite3");
    private static Result Candidate(string domain, string stage) => new() { Domain = domain, Stage = stage, Status = "available", AvailabilityAt = JsonData.Now(), History = "valid", HistoryAt = JsonData.Now() };

    [Theory]
    [InlineData("history_queued", 0, 1)]
    [InlineData("history", 0, 1)]
    [InlineData("moz_queued", 0, 0)]
    [InlineData("moz", 0, 0)]
    [InlineData("opr_queued", 1, 0)]
    [InlineData("opr", 1, 0)]
    public async Task ResumeUsesCheckpointAndPreservesFinishedRows(string stage, int opr, int history)
    {
        string job, completed;
        using (var old = new Store(Database))
        {
            job = old.NewJob(["finished.com", "resume.com"]);
            var done = Candidate("finished.com", "done"); done.Da = 42;
            old.Save(job, done);
            completed = JsonData.Write(old.Results(job)[0]);
            old.Save(job, Candidate("resume.com", stage));
            old.State(job, "running");
        }
        using var store = new Store(Database);
        Assert.Equal("paused", store.Jobs().Single().State);
        store.Resume(job);
        var providers = new TrackingProviders();
        var engine = new Engine(store, new("", "id", "secret"), providers);
        engine.Start(job);
        await engine.Completion!.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("", engine.Failure);
        Assert.Empty(providers.Availability);
        Assert.Equal(opr, providers.Opr.Count);
        Assert.Equal(history, providers.History.Count);
        Assert.Single(providers.Moz);
        Assert.Equal(completed, JsonData.Write(store.Results(job)[0]));
        Assert.Equal(2, store.Counts(job).Done);
    }
    [Fact]
    public async Task SelectedResumeDoesNotStartUnrelatedQueuedSession()
    {
        using var store = new Store(Database);
        var selected = store.NewJob(["selected.com"]);
        var other = store.NewJob(["other.com"]);
        var providers = new TrackingProviders();
        var engine = new Engine(store, new(), providers);
        engine.Start(selected);
        await engine.Completion!.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(1, store.Counts(selected).Done);
        Assert.Equal(0, store.Counts(other).Done);
        Assert.Equal("queued", store.Jobs().Single(j => j.Id == other).State);
        Assert.DoesNotContain("other.com", providers.Availability);
    }
    [Fact]
    public async Task AtomicPaidReservationSurvivesRestartWithoutAnotherPaidRequest()
    {
        string job;
        var settings = new Settings();
        using (var old = new Store(Database))
        {
            job = old.NewJob(["paid.com"]);
            var candidate = Candidate("paid.com", "moz_queued");
            old.Save(job, candidate);
            Assert.True(old.ReserveMozRow(job, candidate, settings));
            Assert.Throws<InvalidOperationException>(() => old.ReserveMozRow(job, candidate, settings));
            Assert.Equal(1, old.Used(settings.Period));
        }
        using var store = new Store(Database);
        Assert.Equal("reserved", store.Results(job).Single().MozState);
        Assert.Equal(1, store.Used(settings.Period));
        store.Resume(job);
        var providers = new TrackingProviders();
        var engine = new Engine(store, new("", "id", "secret"), providers);
        engine.Start(job);
        await engine.Completion!.WaitAsync(TimeSpan.FromSeconds(15));
        var result = store.Results(job).Single();
        Assert.Equal("uncertain", result.MozState);
        Assert.Equal("done", result.Stage);
        Assert.Empty(providers.Moz);
        Assert.Equal(0, store.RetryFailed(job));
        Assert.Equal(1, store.Used(settings.Period));
    }
    [Fact]
    public void QuotaRefusalDoesNotPersistRequestMarker()
    {
        using var store = new Store(Database);
        var job = store.NewJob(["quota.com"]);
        var result = Candidate("quota.com", "moz_queued");
        store.Save(job, result);
        Assert.False(store.ReserveMozRow(job, result, new Settings { MozBudget = 0, MozReserve = 0 }));
        Assert.Equal("", store.Results(job).Single().MozState);
        Assert.Equal("moz_queued", store.Results(job).Single().Stage);
        Assert.Equal(0, store.Used(new Settings().Period));
    }
    [Fact]
    public async Task RetryOnlyRepeatsFailedLaneAndForcesAvailabilityAfterFailure()
    {
        using var store = new Store(Database);
        var job = store.NewJob(["rdap.com", "opr.com", "history.com", "nokey.com", "uncertain.com"]);
        foreach (var domain in new[] { "rdap.com", "opr.com", "history.com", "nokey.com", "uncertain.com" })
        {
            var r = Candidate(domain, "done");
            if (domain == "rdap.com") { r.Status = "unknown"; r.Errors["availability"] = "timeout"; }
            if (domain == "opr.com") r.Errors["opr"] = "timeout";
            if (domain == "history.com") { r.History = "unchecked"; r.Errors["history"] = "timeout"; }
            if (domain == "nokey.com") r.MozState = "no_key";
            if (domain == "uncertain.com") { r.MozState = "uncertain"; r.Errors["moz"] = "interrupted"; }
            store.Save(job, r);
        }
        Assert.Equal(4, store.RetryFailed(job));
        var providers = new TrackingProviders();
        var engine = new Engine(store, new("", "id", "secret"), providers);
        engine.Start(job);
        await engine.Completion!.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("", engine.Failure);
        Assert.Equal(new[] { "rdap.com" }, providers.Availability.ToArray());
        Assert.Equal(new[] { "rdap.com" }, providers.Forced.ToArray());
        Assert.Equal(new[] { "history.com" }, providers.History.ToArray());
        Assert.Equal(2, providers.Opr.Count);
        Assert.Equal(4, providers.Moz.Count);
        Assert.DoesNotContain("uncertain.com", providers.Moz);
    }
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task ConfiguredConcurrencyBoundsInFlightChecks(int workers)
    {
        using var store = new Store(Database);
        store.Configure(new Settings { AvailabilityWorkers = workers });
        var job = store.NewJob(Enumerable.Range(0, 8).Select(i => $"worker{i}.com"));
        var providers = new TrackingProviders { BlockAvailability = true, WorkerTarget = workers };
        var engine = new Engine(store, new(), providers);
        engine.Start(job);
        try
        {
            await providers.WorkersEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(workers, providers.Peak);
            Assert.Equal(workers, store.Counts(job).Active);
        }
        finally { providers.Release.TrySetResult(); }
        await engine.Completion!.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(workers, providers.Peak);
        Assert.Equal(8, store.Counts(job).Done);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(33)]
    public void InvalidConcurrencyIsRejected(int workers) => Assert.Throws<ArgumentException>(() => new Settings { AvailabilityWorkers = workers }.Validate());
    [Fact]
    public async Task BodyDeadlineAppliesAfterSuccessfulHeadersAndPaidRequestIsNotRetried()
    {
        var handler = new StubHandler(_ => new(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) });
        using var http = new Transport(handler, TimeSpan.FromMilliseconds(100));
        var failure = await Assert.ThrowsAsync<SourceException>(() => http.SendAsync(() => new(HttpMethod.Post, "https://lsapi.seomoz.com/v2/url_metrics"), null, CancellationToken.None, attempts: 1).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("timeout", failure.Message);
        Assert.Equal(1, handler.Calls);
    }
    [Fact]
    public async Task CallerCancellationIsNotConvertedIntoProviderFailure()
    {
        var handler = new StubHandler(_ => new(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) });
        using var http = new Transport(handler);
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => http.SendAsync(() => new(HttpMethod.Get, "https://web.archive.org/"), null, stop.Token));
        Assert.Equal(1, handler.Calls);
    }
    [Theory]
    [InlineData("\"4.15\"", 4.15)]
    [InlineData("0", 0)]
    public void NumericProviderValuesRetainTheirMeaning(string json, double expected) => Assert.Equal(expected, Providers.Metric(JsonNode.Parse(json), 10));
    [Theory]
    [InlineData("\"NaN\"")]
    [InlineData("\"unknown\"")]
    [InlineData("\"-1\"")]
    [InlineData("\"11\"")]
    public void InvalidProviderValuesCannotBecomeRealMetrics(string json) => Assert.Null(Providers.Metric(JsonNode.Parse(json), 10));
    [Fact]
    public async Task FailedInitialBootstrapIsNotDownloadedForEveryDomain()
    {
        using var store = new Store(Database);
        var handler = new StubHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent("{\"services\":[]}") });
        using var http = new Transport(handler);
        var providers = new Providers(store, new(), http);
        for (var i = 0; i < 8; i++)
            await Assert.ThrowsAsync<SourceException>(() => providers.ResolveRdapAsync($"test{i}.fr", CancellationToken.None));
        Assert.Equal(1, handler.Calls);
    }
    [Fact]
    public async Task CurrentNoHistoryPolicyPreventsPaidRequestOnResume()
    {
        using var store = new Store(Database);
        store.Configure(new Settings { EmptyValid = false });
        var job = store.NewJob(["empty.com"]);
        var r = Candidate("empty.com", "moz_queued"); r.History = "no_history";
        store.Save(job, r);
        var providers = new TrackingProviders();
        var engine = new Engine(store, new("", "id", "secret"), providers);
        engine.Start(job);
        await engine.Completion!.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Empty(providers.Moz);
        Assert.Equal("unchecked", store.Results(job).Single().History);
        Assert.Equal(0, store.Used(new Settings().Period));
    }
    [Fact]
    public void InconsistentCompletedSessionWithPendingRowsBecomesResumableAfterRestart()
    {
        string job;
        using (var previous = new Store(Database))
        {
            job = previous.NewJob(["pending.com", "finished.com"]);
            previous.Save(job, Candidate("finished.com", "done"));
            previous.State(job, "completed");
        }
        using var store = new Store(Database);
        Assert.Equal("paused", store.Jobs().Single().State);
        Assert.Equal(1, store.Counts(job).Done);
        Assert.Equal(1, store.Counts(job).Pending);
    }
    [Fact]
    public void ConcurrencySettingSurvivesRestart()
    {
        using (var store = new Store(Database)) store.Configure(new Settings { AvailabilityWorkers = 16 });
        using var reopened = new Store(Database);
        Assert.Equal(16, reopened.GetSettings().AvailabilityWorkers);
    }
    [Fact]
    public void MalformedCacheDoesNotEraseStoredResearch()
    {
        using var store = new Store(Database);
        var job = store.NewJob(["keep.com"]);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Database};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO cache VALUES('iana','bootstrap',$at,'broken json')";
            command.Parameters.AddWithValue("$at", JsonData.Now());
            command.ExecuteNonQuery();
        }
        Assert.Null(store.CacheGet<JsonNode>("iana", "bootstrap", 86400));
        Assert.Equal("keep.com", store.Results(job).Single().Domain);
        Assert.Equal(1, store.Counts(job).Total);
    }
    [Fact]
    public async Task UnexpectedWorkerFailureIsPersistedWithoutThirdPartyExceptionText()
    {
        using (var store = new Store(Database))
        {
            var job = store.NewJob(["failure.com"]);
            var engine = new Engine(store, new(), new TrackingProviders { ThrowOpr = true });
            engine.Start(job);
            await engine.Completion!.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal("paused", store.Jobs().Single().State);
            Assert.Contains("InvalidOperationException", engine.Failure);
            Assert.DoesNotContain("secret", engine.Failure);
        }
        using var reopened = new Store(Database);
        var failure = reopened.Events(null).Rows.Single(e => e.Stage == "worker");
        Assert.Contains("InvalidOperationException", failure.Message);
        Assert.DoesNotContain("secret", failure.Message);
    }
    [Fact]
    public void FailedExportPreservesExistingFileAndCleansTemporaryOutput()
    {
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "existing.csv");
        File.WriteAllText(file, "keep this previous export");
        IEnumerable<Result> BrokenSource()
        {
            yield return Candidate("export.com", "done");
            throw new IOException("reader disconnected");
        }
        Assert.Throws<IOException>(() => CsvExport.Save(file, BrokenSource()));
        Assert.Equal("keep this previous export", File.ReadAllText(file));
        Assert.Single(Directory.GetFiles(directory));
    }
    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
    private sealed class TrackingProviders : IProviders
    {
        public ConcurrentQueue<string> Availability { get; } = new();
        public ConcurrentQueue<string> Forced { get; } = new();
        public ConcurrentQueue<string> Opr { get; } = new();
        public ConcurrentQueue<string> History { get; } = new();
        public ConcurrentQueue<string> Moz { get; } = new();
        public bool BlockAvailability { get; init; }
        public bool ThrowOpr { get; init; }
        public int WorkerTarget { get; init; }
        private int peak;
        public int Peak => Volatile.Read(ref peak);
        private int active;
        public TaskCompletionSource WorkersEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task AvailabilityAsync(Result r, bool force, CancellationToken ct)
        {
            Availability.Enqueue(r.Domain);
            if (force) Forced.Enqueue(r.Domain);
            if (BlockAvailability)
            {
                var count = Interlocked.Increment(ref active);
                int previous;
                do { previous = Volatile.Read(ref peak); if (previous >= count) break; }
                while (Interlocked.CompareExchange(ref peak, count, previous) != previous);
                if (count == WorkerTarget) WorkersEntered.TrySetResult();
                await Release.Task.WaitAsync(ct);
                Interlocked.Decrement(ref active);
            }
            r.Status = "available"; r.AvailabilityAt = JsonData.Now(); r.Errors.Remove("availability");
        }
        public Task OprAsync(IReadOnlyList<Result> rows, CancellationToken ct)
        {
            if (ThrowOpr) throw new InvalidOperationException("third-party secret must not appear in logs");
            foreach (var r in rows) { Opr.Enqueue(r.Domain); r.Opr = 5; r.OprAt = JsonData.Now(); r.Errors.Remove("opr"); }
            return Task.CompletedTask;
        }
        public Task HistoryAsync(Result r, Settings settings, CancellationToken ct)
        {
            History.Enqueue(r.Domain); r.History = "valid"; r.HistoryAt = JsonData.Now(); return Task.CompletedTask;
        }
        public Task MozAsync(Result r, CancellationToken ct)
        {
            Moz.Enqueue(r.Domain); r.Da = 25; r.MozAt = JsonData.Now(); r.MozState = "ok"; return Task.CompletedTask;
        }
    }
    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { await Task.Delay(Timeout.Infinite, ct); return 0; }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
