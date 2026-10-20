using Microsoft.Data.Sqlite;
using MozChecker.Core;
using Xunit;

namespace MozChecker.Tests;

public sealed class WorkspaceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "moz-workspace-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(directory, "checker.sqlite3");
    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, true);
    }
    [Fact]
    public void ScreenshotSizedListIsOneSessionWithPagedQueriesAndExactProgress()
    {
        using var store = new Store(Database);
        var job = store.NewJob(Enumerable.Range(0, 510288).Select(i => $"domain{i}.com"));
        Assert.Single(store.Jobs());
        var initial = store.Counts(job);
        Assert.Equal(510288, initial.Total);
        Assert.Equal(510288, initial.Pending);
        Assert.Equal(0, initial.Events);
        var page = store.Page(job, 2, new(), 5);
        Assert.Equal(510288, page.Total);
        Assert.Equal(200, page.Rows.Count);
        Assert.Contains(page.Rows, r => r.Domain == "domain1000.com");
        Assert.All(page.Rows, r => Assert.Equal("Belum diperiksa", r.StatusText));
        Assert.Equal(1005, store.PendingResults(job).Take(1005).Count());
        store.State(job, "running");
        store.Save(job, new Result { Domain = "domain0.com", Stage = "done", Status = "registered", AvailabilityAt = JsonData.Now() });
        store.Save(job, new Result { Domain = "domain1.com", Stage = "done", Status = "available", AvailabilityAt = JsonData.Now(), History = "valid" });
        store.Save(job, new Result { Domain = "domain2.com", Stage = "availability" });
        var counts = store.Counts(null);
        Assert.Equal(510288, counts.Total);
        Assert.Equal(2, counts.Done);
        Assert.Equal(510286, counts.Pending);
        Assert.Equal(1, counts.Active);
        Assert.Equal(1, counts.Available);
        Assert.Equal(1, counts.Other);
        Assert.Equal("Diproses", store.Page(job, 2, new()).Rows.Single(r => r.Domain == "domain2.com").WorkState);
        store.State(job, "paused");
        Assert.Equal(0, store.Counts(job).Active);
        Assert.All(store.Page(job, 2, new()).Rows, r => Assert.Equal("Dijeda", r.WorkState));
        Assert.DoesNotContain(store.Events(job).Rows, r => r.Stage == "queued");
    }
    [Fact]
    public void LegacySplitSessionsAggregateWithoutDeletingOrRequeueingCompletedResults()
    {
        using var store = new Store(Database);
        var a = store.NewJob(Enumerable.Range(0, 1000).Select(i => $"a{i}.com"));
        var b = store.NewJob(Enumerable.Range(0, 1501).Select(i => $"b{i}.com"));
        store.Save(a, new Result { Domain = "a0.com", Status = "available", Stage = "done", AvailabilityAt = JsonData.Now(), History = "valid" });
        store.Save(b, new Result { Domain = "b0.com", Status = "registered", Stage = "done", AvailabilityAt = JsonData.Now() });
        store.State(a, "paused");
        store.State(b, "paused");
        var all = store.Counts(null);
        Assert.Equal(2501, all.Total);
        Assert.Equal(2, all.Done);
        Assert.Equal(2499, all.Pending);
        Assert.Single(store.Page(null, 0, new()).Rows);
        Assert.Single(store.Page(null, 1, new()).Rows);
        store.Resume(null);
        Assert.All(store.Jobs(), j => Assert.Equal("queued", j.State));
        Assert.Equal(999, store.PendingResults(a).Count());
        Assert.Equal(1500, store.PendingResults(b).Count());
    }
    [Fact]
    public void QueueCreationNeverCreatesDomainEventsAndEventsAreActualLatestFirst()
    {
        long firstSeq;
        using (var store = new Store(Database))
        {
            var id = store.NewJob(["test.com", "queued.com"]);
            Assert.Empty(store.Events(null).Rows);
            var row = new Result { Domain = "test.com", Stage = "availability" };
            store.Save(id, row);
            firstSeq = store.Events(id).Rows.Single().Sequence;
            store.Save(id, row);
            Assert.Single(store.Events(id).Rows);
            row.Stage = "done";
            row.Status = "registered";
            row.AvailabilityAt = "2000-01-01T00:00:00Z";
            store.Save(id, row);
            var events = store.Events(id).Rows;
            Assert.Equal(2, events.Count);
            Assert.True(events[0].Sequence > firstSeq);
            Assert.Equal("registered", events[0].Status);
            Assert.True(JsonData.Fresh(events[0].At, 120));
            Assert.DoesNotContain(events, e => e.Domain == "queued.com");
            Assert.DoesNotContain(events, e => e.Stage == "queued");
        }
        using (var store = new Store(Database))
        {
            var events = store.Events(null).Rows;
            Assert.Equal(2, events.Count);
            Assert.Equal("registered", events[0].Status);
            Assert.True(events[0].Sequence > firstSeq);
        }
    }
    [Fact]
    public void HistoricLogPagesRemainAnchoredWhenNewEventsArrive()
    {
        using var store = new Store(Database);
        var id = store.NewJob(Enumerable.Range(0, 12).Select(i => $"d{i}.com"));
        for (int i = 0; i < 10; i++)
            store.Save(id, new Result { Domain = $"d{i}.com", Stage = "done", Status = "registered", AvailabilityAt = JsonData.Now() });
        var first = store.Events(id, size: 3);
        var anchor = first.Rows[0].Sequence;
        var second = store.Events(id, page: 1, anchor: anchor, size: 3);
        store.Save(id, new Result { Domain = "d10.com", Stage = "done", Status = "registered", AvailabilityAt = JsonData.Now() });
        Assert.Equal(second.Rows.Select(e => e.Sequence), store.Events(id, page: 1, anchor: anchor, size: 3).Rows.Select(e => e.Sequence));
        Assert.True(store.Events(id).Rows[0].Sequence > anchor);
    }
    [Fact]
    public void LegacySchemaMigrationRetainsProofAndDoesNotFabricateOldDomainEvents()
    {
        Directory.CreateDirectory(directory);
        using (var db = new SqliteConnection($"Data Source={Database};Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "CREATE TABLE jobs(id TEXT PRIMARY KEY,created TEXT,state TEXT); CREATE TABLE results(job TEXT,domain TEXT,ordinal INTEGER,data TEXT,PRIMARY KEY(job,domain)); INSERT INTO jobs VALUES('old','2026-01-01T00:00:00Z','running');";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "INSERT INTO results VALUES('old','google.com',0,$r)";
            cmd.Parameters.AddWithValue("$r", JsonData.Write(new Result { Domain = "google.com", Status = "registered", Stage = "done", AvailabilityAt = "2026-01-01T00:00:00Z" }));
            cmd.ExecuteNonQuery();
        }
        using (var store = new Store(Database))
        {
            Assert.Equal("paused", store.Jobs().Single().State);
            Assert.Equal(1, store.Counts(null).Total);
            Assert.Equal(1, store.Counts("old").Done);
            Assert.Equal("2026-01-01T00:00:00Z", store.Page(null, 1, new()).Rows.Single().AvailabilityAt);
            Assert.All(store.Events(null).Rows, e => Assert.Equal("", e.Domain));
            Assert.Single(store.Events(null).Rows, e => e.Stage == "migration");
        }
        using (var store = new Store(Database))
        {
            Assert.Equal(1, store.Counts(null).Total);
            Assert.Single(store.Events(null).Rows, e => e.Stage == "migration");
        }
    }
    [Fact]
    public void FilterSortAndExportCoverAllPagesRatherThanTheVisibleSlice()
    {
        using var store = new Store(Database);
        var id = store.NewJob(Enumerable.Range(0, 403).Select(i => $"d{i:D3}.com"));
        for (int i = 0; i < 403; i++)
            store.Save(id, new Result { Domain = $"d{i:D3}.com", Status = "available", Stage = "done", AvailabilityAt = JsonData.Now(), History = "valid", Opr = i % 10, Da = i % 100 });
        var filter = new ResultFilter(Tld: "com", History: "valid", Opr: 5, Da: 50);
        var all = store.ExportAvailable(id, filter, "Domain", true).ToArray();
        var page = store.Page(id, 0, filter, 0, 20, "Domain", true);
        Assert.True(all.Length > 20);
        Assert.Equal(all.Length, page.Total);
        Assert.Equal(all.Take(20).Select(r => r.Domain), page.Rows.Select(r => r.Domain));
        Assert.All(all, r => Assert.Equal(id, r.JobId));
        var path = Path.Combine(directory, "export.csv");
        CsvExport.Save(path, store.ExportAvailable(id, new()));
        var lines = File.ReadAllLines(path);
        Assert.Equal(404, lines.Length);
        Assert.Contains("job_id", lines[0]);
    }
    [Fact]
    public void RecheckStartDoesNotRelabelOldProofAsANewVerdict()
    {
        using var store = new Store(Database);
        var id = store.NewJob(["test.com"]);
        var result = new Result { Domain = "test.com", Stage = "done", Status = "registered", AvailabilityAt = "2000-01-01T00:00:00Z", RdapHttp = 200 };
        store.Save(id, result);
        result.Stage = "availability";
        store.Save(id, result);
        var entry = store.Events(id).Rows[0];
        Assert.Equal("started", entry.Status);
        Assert.Contains("Mulai", entry.Message);
        Assert.DoesNotContain("RDAP 200", entry.Message);
        Assert.Equal("2000-01-01T00:00:00Z", store.Page(id, 1, new()).Rows.Single().AvailabilityAt);
    }
    [Fact]
    public void WaitingForMozIsPendingRatherThanAnActiveRequest()
    {
        using var store = new Store(Database);
        var id = store.NewJob(["test.com"]);
        store.State(id, "running");
        var result = new Result { Domain = "test.com", Stage = "moz_queued", Status = "available", AvailabilityAt = JsonData.Now(), History = "valid" };
        store.Save(id, result);
        Assert.Equal(0, store.Counts(id).Active);
        Assert.Equal("Menunggu Moz", store.Page(id, 2, new()).Rows.Single().WorkState);
        result.Stage = "moz";
        result.MozState = "reserved";
        store.Save(id, result);
        Assert.Equal(1, store.Counts(id).Active);
        Assert.Equal("reserved", store.Events(id).Rows[0].Status);
    }
    [Fact]
    public void CancelledListCreationRollsBackTheWholeSession()
    {
        using var store = new Store(Database);
        using var cancel = new CancellationTokenSource();
        IEnumerable<string> Input()
        {
            yield return "first.com";
            cancel.Cancel();
            yield return "second.com";
        }
        Assert.Throws<OperationCanceledException>(() => store.NewJob(Input(), cancel.Token));
        Assert.Empty(store.Jobs());
        Assert.Equal(0, store.Counts(null).Total);
        Assert.Empty(store.Events(null).Rows);
    }
}
