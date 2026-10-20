using System.Threading.Channels;

namespace MozChecker.Core;

public sealed class Engine(Store store, ApiKeys keys, IProviders providers)
{
    private readonly object lifecycle = new();
    private CancellationTokenSource? stop;
    public Task? Completion
    {
        get; private set;
    }
    public bool Running => Completion is { IsCompleted: false };
    public string Failure { get; private set; } = "";
    public event Action? Changed;
    public void Start(string? selectedJob = null)
    {
        lock (lifecycle)
        {
            if (Running)
                return;
            stop?.Dispose();
            stop = new();
            Failure = "";
            var token = stop.Token;
            Completion = Task.Run(() => RunAsync(selectedJob, token));
        }
    }
    public void Pause()
    {
        lock (lifecycle)
            stop?.Cancel();
    }
    private void Emit(string job, Result result)
    {
        store.Save(job, result);
        Changed?.Invoke();
    }
    private async Task RunAsync(string? selectedJob, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var job = store.Jobs().FirstOrDefault(x => (selectedJob is null || x.Id == selectedJob) && x.State is "queued" or "running");
                if (job is null)
                    break;
                store.State(job.Id, "running");
                Changed?.Invoke();
                try
                {
                    await RunJobAsync(job.Id, ct);
                    ct.ThrowIfCancellationRequested();
                    store.State(job.Id, "completed");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { store.State(job.Id, "paused"); break; }
                catch (Exception ex) { Failure = $"Worker berhenti ({ex.GetType().Name}); pemeriksaan dijeda."; store.State(job.Id, "paused"); store.NoteFailure(job.Id, ex.GetType().Name); break; }
                Changed?.Invoke();
            }
        }
        finally { Changed?.Invoke(); }
    }
    private async Task RunJobAsync(string job, CancellationToken ct)
    {
        var settings = store.GetSettings();
        var availability = Channel.CreateBounded<Result>(new BoundedChannelOptions(1000) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        var history = Channel.CreateBounded<Result>(new BoundedChannelOptions(1000) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        var moz = Channel.CreateBounded<Result>(new BoundedChannelOptions(1000) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        // One linked cancellation scope prevents a failed lane leaving other lanes blocked on a full channel.
        using var lanes = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = lanes.Token;
        async Task Guard(Func<Task> action)
        {
            try
            {
                await action();
            }
            catch { await lanes.CancelAsync(); throw; }
        }
        var a = Guard(async () =>
        {
            try
            {
                await Parallel.ForEachAsync(store.PendingResults(job), new ParallelOptions { MaxDegreeOfParallelism = settings.AvailabilityWorkers, CancellationToken = token }, async (r, t) =>
                {
                    // Resume the persisted lane; completed free-source work is not replayed.
                    if (r.Stage is "moz" or "moz_queued")
                    {
                        await moz.Writer.WriteAsync(r, t);
                        return;
                    }
                    if (r.Stage is "history" or "history_queued")
                    {
                        await history.Writer.WriteAsync(r, t);
                        return;
                    }
                    if (r.Stage is "opr" or "opr_queued")
                    {
                        await availability.Writer.WriteAsync(r, t);
                        return;
                    }
                    r.Stage = "availability";
                    Emit(job, r);
                    try
                    {
                        await providers.AvailabilityAsync(r, r.Errors.ContainsKey("availability"), t);
                    }
                    catch (SourceException ex) { r.Status = "unknown"; r.Errors["availability"] = ex.Message; }
                    Emit(job, r);
                    if (r.Status == "available")
                    {
                        r.Stage = "opr_queued";
                        Emit(job, r);
                        await availability.Writer.WriteAsync(r, t);
                    }
                    else
                    {
                        r.Stage = "done";
                        Emit(job, r);
                    }
                });
            }
            finally { availability.Writer.TryComplete(); }
        });
        var o = Guard(async () =>
        {
            try
            {
                await foreach (var first in availability.Reader.ReadAllAsync(token))
                {
                    var batch = new List<Result> { first };
                    while (batch.Count < 100 && availability.Reader.TryRead(out var next))
                        batch.Add(next);
                    foreach (var r in batch)
                    {
                        r.Stage = "opr";
                        Emit(job, r);
                    }
                    try
                    {
                        await providers.OprAsync(batch, token);
                    }
                    catch (SourceException ex) { foreach (var r in batch) r.Errors["opr"] = ex.Message; }
                    foreach (var r in batch)
                    {
                        Emit(job, r);
                        if (r.Opr is null || r.Opr >= settings.OprMin)
                        {
                            if (r.History is "valid" or "no_history" && r.HistoryAt.Length > 0 && !r.Errors.ContainsKey("history"))
                            {
                                r.Stage = "moz_queued";
                                Emit(job, r);
                                await moz.Writer.WriteAsync(r, token);
                            }
                            else
                            {
                                r.Stage = "history_queued";
                                Emit(job, r);
                                await history.Writer.WriteAsync(r, token);
                            }
                        }
                        else
                        {
                            r.Stage = "done";
                            Emit(job, r);
                        }
                    }
                }
            }
            finally { history.Writer.TryComplete(); }
        });
        var h = Guard(async () =>
        {
            try
            {
                await foreach (var r in history.Reader.ReadAllAsync(token))
                {
                    r.Stage = "history";
                    Emit(job, r);
                    try
                    {
                        await providers.HistoryAsync(r, settings, token);
                        r.Errors.Remove("history");
                        if (r.History == "unchecked")
                            r.Errors["history"] = "Sampel tidak lengkap atau tanpa riwayat tidak diizinkan";
                    }
                    catch (SourceException ex) { r.History = "unchecked"; r.Errors["history"] = ex.Message; }
                    Emit(job, r);
                    if (r.History is "valid" or "no_history" && string.IsNullOrEmpty(r.MozAt))
                    {
                        r.Stage = "moz_queued";
                        Emit(job, r);
                        await moz.Writer.WriteAsync(r, token);
                    }
                    else
                    {
                        r.Stage = "done";
                        Emit(job, r);
                    }
                }
            }
            finally { moz.Writer.TryComplete(); }
        });
        var m = Guard(async () => { await foreach (var r in moz.Reader.ReadAllAsync(token)) { await CheckMozAsync(job, r, settings, token); } });
        await Task.WhenAll(a, o, h, m);
    }
    private async Task CheckMozAsync(string job, Result r, Settings settings, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (r.MozAt.Length > 0)
        {
            r.Stage = "done";
            Emit(job, r);
            return;
        }
        if (r.History == "no_history" && !settings.EmptyValid)
        {
            r.History = "unchecked";
            r.Errors["history"] = "Tanpa riwayat tidak diizinkan oleh pengaturan saat ini";
        }
        if (r.History is not ("valid" or "no_history"))
        {
            r.Stage = "done";
            Emit(job, r);
            return;
        }
        if (r.MozState == "reserved")
        {
            r.MozState = "uncertain";
            r.Errors["moz"] = "Permintaan terputus; kuota tetap dihitung dan tidak diulang otomatis";
        }
        else if (r.MozState is "failed" or "uncertain")
        {
        }
        else if (keys.MozId.Length == 0 || keys.MozSecret.Length == 0)
            r.MozState = "no_key";
        else
        {
            try
            {
                // Archive work can take hours. Refresh availability before any paid request.
                if (!JsonData.Fresh(r.AvailabilityAt, 900))
                {
                    r.Stage = "availability";
                    Emit(job, r);
                    await providers.AvailabilityAsync(r, true, ct);
                    Emit(job, r);
                }
                if (r.Status != "available" || !JsonData.Fresh(r.AvailabilityAt, 900))
                    r.MozState = "not_available";
                else if (!store.ReserveMozRow(job, r, settings))
                    r.MozState = "quota_reserved";
                else
                {
                    Changed?.Invoke();
                    await providers.MozAsync(r, ct);
                    r.Errors.Remove("moz");
                }
            }
            catch (SourceException ex) { if (r.MozState == "reserved") { r.MozState = "failed"; r.Errors["moz"] = ex.Message; } else { r.Status = "unknown"; r.MozState = "availability_failed"; r.Errors["availability"] = ex.Message; } }
        }
        r.Stage = "done";
        Emit(job, r);
    }
}
