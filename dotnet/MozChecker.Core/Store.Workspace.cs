using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace MozChecker.Core;

public sealed record WorkspaceCounts(long Total, long Done, long Checked, long Available, long Registered, long Passed, long Pending, long Active, long Other, long Events, long Revision);
public sealed record EventRow(long Sequence, string At, string Job, string Domain, string Stage, string Status, string Message)
{
    public string LocalTime => DateTimeOffset.Parse(At).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff zzz");
    public string Session => Job.Length > 8 ? Job[..8] : Job;
}
public sealed record ResultFilter(string Search = "", string Tld = "", string History = "", int MaxLength = 0, double Opr = 0, double Da = 0, int Snapshots = 0);
public sealed record ResultPage(List<Result> Rows, long Total);
public sealed record EventPage(List<EventRow> Rows, long Total);

public sealed partial class Store
{
    private static readonly Dictionary<string, string> StatFlags = new()
    {
        ["done"] = "json_extract(D,'$.stage')='done'",
        ["checked"] = "coalesce(json_extract(D,'$.availability_at'),'')<>'' OR json_extract(D,'$.stage')='done'",
        ["available"] = "json_extract(D,'$.status')='available'",
        ["registered"] = "json_extract(D,'$.status')='registered'",
        ["passed"] = "json_extract(D,'$.status')='available' AND json_extract(D,'$.history') IN ('valid','no_history')",
        ["pending"] = "json_extract(D,'$.stage')<>'done'",
        ["active"] = "json_extract(D,'$.stage') NOT IN ('queued','moz_queued','done')",
        ["other"] = "json_extract(D,'$.status')<>'available' AND (coalesce(json_extract(D,'$.availability_at'),'')<>'' OR json_extract(D,'$.stage')='done')"
    };
    private void InitializeWorkspace()
    {
        using var check = Command("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='job_stats'");
        var fresh = Convert.ToInt32(check.ExecuteScalar()) == 0;
        using var tx = db.BeginTransaction();
        Execute("CREATE TABLE IF NOT EXISTS job_stats(job TEXT PRIMARY KEY,total INTEGER NOT NULL DEFAULT 0,done INTEGER NOT NULL DEFAULT 0,checked INTEGER NOT NULL DEFAULT 0,available INTEGER NOT NULL DEFAULT 0,registered INTEGER NOT NULL DEFAULT 0,passed INTEGER NOT NULL DEFAULT 0,pending INTEGER NOT NULL DEFAULT 0,active INTEGER NOT NULL DEFAULT 0,other INTEGER NOT NULL DEFAULT 0,events INTEGER NOT NULL DEFAULT 0,revision INTEGER NOT NULL DEFAULT 0); CREATE TABLE IF NOT EXISTS event_log(seq INTEGER PRIMARY KEY AUTOINCREMENT,at TEXT NOT NULL,job TEXT NOT NULL,domain TEXT NOT NULL,stage TEXT NOT NULL,status TEXT NOT NULL,message TEXT NOT NULL); CREATE INDEX IF NOT EXISTS event_job_seq ON event_log(job,seq); CREATE INDEX IF NOT EXISTS results_job_order ON results(job,ordinal); CREATE INDEX IF NOT EXISTS results_pending ON results(job,ordinal) WHERE json_extract(data,'$.stage')<>'done'; CREATE INDEX IF NOT EXISTS results_status ON results(json_extract(data,'$.status'),job,ordinal);");
        var names = string.Join(',', StatFlags.Keys);
        if (fresh)
            Execute($"INSERT INTO job_stats(job,total,{names}) SELECT job,COUNT(*),{string.Join(',', StatFlags.Values.Select(x => "SUM(CASE WHEN " + x.Replace("(D,", "(data,") + " THEN 1 ELSE 0 END)"))} FROM results GROUP BY job");
        string Flag(string expression, string prefix) => "(CASE WHEN " + expression.Replace("(D,", "(" + prefix + ".data,") + " THEN 1 ELSE 0 END)";
        Execute($"CREATE TRIGGER IF NOT EXISTS result_stats_insert AFTER INSERT ON results BEGIN INSERT OR IGNORE INTO job_stats(job) VALUES(new.job); UPDATE job_stats SET total=total+1,revision=revision+1,{string.Join(',', StatFlags.Select(x => x.Key + "=" + x.Key + "+" + Flag(x.Value, "new")))} WHERE job=new.job; END;");
        Execute($"CREATE TRIGGER IF NOT EXISTS result_stats_update AFTER UPDATE OF data ON results BEGIN UPDATE job_stats SET revision=revision+1,{string.Join(',', StatFlags.Select(x => x.Key + "=" + x.Key + "+" + Flag(x.Value, "new") + "-" + Flag(x.Value, "old")))} WHERE job=new.job; END;");
        Execute($"CREATE TRIGGER IF NOT EXISTS result_stats_delete AFTER DELETE ON results BEGIN UPDATE job_stats SET total=total-1,revision=revision+1,{string.Join(',', StatFlags.Select(x => x.Key + "=" + x.Key + "-" + Flag(x.Value, "old")))} WHERE job=old.job; END;");
        if (fresh && Convert.ToInt64(Scalar("SELECT COUNT(*) FROM results")) > 0)
            AddEvent("", "", "migration", "info", "Data lama dipertahankan. Baris menunggu tidak dimasukkan ke log; log kejadian bertimestamp dimulai pada versi ini.");
        tx.Commit();
    }
    private object? Scalar(string sql, params object[] args)
    {
        using var cmd = Command(sql, args);
        return cmd.ExecuteScalar();
    }
    private void AddEvent(string job, string domain, string stage, string status, string message)
    {
        Execute("INSERT INTO event_log(at,job,domain,stage,status,message) VALUES($p0,$p1,$p2,$p3,$p4,$p5)", JsonData.Now(), job, domain, stage, status, message);
        Execute("INSERT OR IGNORE INTO job_stats(job) VALUES($p0); UPDATE job_stats SET events=events+1,revision=revision+1 WHERE job=$p0", job);
    }
    private static string EventMessage(Result r, string previousStage)
    {
        var source = r.Stage switch
        {
            "availability" => previousStage != "availability" || r.AvailabilityAt.Length == 0 ? "Mulai / ulang RDAP-DNS" : $"RDAP {r.RdapHttp?.ToString() ?? "—"}; DNS {r.Dns}; status {r.Status}",
            "history" => "Pemeriksaan Wayback dimulai",
            "moz" => "Moz: kuota lokal dipesan untuk permintaan",
            "moz_queued" => $"Wayback selesai: {r.History}; sampel {r.Sampled}",
            "done" => $"Selesai: {r.Status}; riwayat {r.History}; Moz {r.MozState}",
            _ => r.Stage
        };
        return r.Errors.Count == 0 ? source : source + " · " + string.Join(" | ", r.Errors.Select(p => p.Key + ": " + p.Value));
    }
    public WorkspaceCounts Counts(string? job)
    {
        lock (sync)
        {
            using var cmd = Command("SELECT coalesce(sum(total),0),coalesce(sum(done),0),coalesce(sum(checked),0),coalesce(sum(available),0),coalesce(sum(registered),0),coalesce(sum(passed),0),coalesce(sum(pending),0),coalesce(sum(CASE WHEN jobs.state='running' THEN active ELSE 0 END),0),coalesce(sum(other),0),coalesce(sum(events),0),coalesce(sum(revision),0) FROM job_stats LEFT JOIN jobs ON jobs.id=job_stats.job WHERE ($p0='' OR job_stats.job=$p0)", job ?? "");
            using var r = cmd.ExecuteReader();
            r.Read();
            return new(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), r.GetInt64(8), r.GetInt64(9), r.GetInt64(10));
        }
    }
    public IEnumerable<Result> PendingResults(string job)
    {
        long cursor = -1;
        while (true)
        {
            List<(long Ordinal, Result Result)> batch = [];
            lock (sync)
            {
                using var cmd = Command("SELECT ordinal,data FROM results WHERE job=$p0 AND ordinal>$p1 AND json_extract(data,'$.stage')<>'done' ORDER BY ordinal LIMIT 128", job, cursor);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    batch.Add((reader.GetInt64(0), JsonData.Read<Result>(reader.GetString(1))));
            }
            if (batch.Count == 0)
                yield break;
            foreach (var row in batch)
            {
                cursor = row.Ordinal;
                yield return row.Result;
            }
        }
    }
    public void Resume(string? job)
    {
        lock (sync)
            Execute("UPDATE jobs SET state='queued' WHERE ($p0='' OR id=$p0) AND state='paused'", job ?? "");
    }
    private static (string Where, object[] Args) ViewWhere(string? job, int view, ResultFilter filter)
    {
        var parts = new List<string>();
        var args = new List<object>();
        void Add(string expression, object value)
        {
            parts.Add(expression.Replace("?", "$p" + args.Count));
            args.Add(value);
        }
        if (job is not null)
            Add("r.job=?", job);
        parts.Add(view switch
        {
            0 => "json_extract(r.data,'$.status')='available'",
            1 => "json_extract(r.data,'$.status')<>'available' AND (coalesce(json_extract(r.data,'$.availability_at'),'')<>'' OR json_extract(r.data,'$.stage')='done')",
            _ => "json_extract(r.data,'$.stage')<>'done'"
        });
        if (filter.Search.Length > 0)
            Add("instr(lower(r.domain),lower(?))>0", filter.Search);
        if (filter.Tld.Length > 0)
            Add("substr(r.domain,-length(?))=?", "." + filter.Tld.TrimStart('.'));
        if (filter.History.Length > 0)
            Add("json_extract(r.data,'$.history')=?", filter.History);
        if (filter.MaxLength > 0)
            Add("instr(r.domain,'.')-1<=?", filter.MaxLength);
        if (filter.Opr > 0)
            Add("json_extract(r.data,'$.opr')>=?", filter.Opr);
        if (filter.Da > 0)
            Add("json_extract(r.data,'$.da')>=?", filter.Da);
        if (filter.Snapshots > 0)
            Add("json_extract(r.data,'$.snapshots')>=?", filter.Snapshots);
        return (string.Join(" AND ", parts), args.ToArray());
    }
    private static string ViewOrder(string sort, bool descending)
    {
        var expression = sort switch
        {
            "Domain" => "r.domain",
            "Opr" => "json_extract(r.data,'$.opr')",
            "Da" => "json_extract(r.data,'$.da')",
            "Pa" => "json_extract(r.data,'$.pa')",
            "Spam" => "json_extract(r.data,'$.spam')",
            "History" => "json_extract(r.data,'$.history')",
            "Snapshots" => "json_extract(r.data,'$.snapshots')",
            "StatusText" => "json_extract(r.data,'$.status')",
            "AvailabilityAt" => "json_extract(r.data,'$.availability_at')",
            "Stage" => "json_extract(r.data,'$.stage')",
            "Dns" => "json_extract(r.data,'$.dns')",
            "RdapHttp" => "json_extract(r.data,'$.rdap_http')",
            "Registrar" => "json_extract(r.data,'$.registrar')",
            "Expires" => "json_extract(r.data,'$.expires')",
            "JobId" => "r.job",
            "WorkState" => "jobs.state",
            "Readiness" => "CASE WHEN julianday('now')-julianday(json_extract(r.data,'$.availability_at')) BETWEEN 0 AND (15.0/1440) AND json_extract(r.data,'$.history') IN ('valid','no_history') THEN 1 ELSE 0 END",
            _ => "r.job,r.ordinal"
        };
        return expression + (descending ? " DESC" : " ASC") + ",r.rowid";
    }
    public ResultPage Page(string? job, int view, ResultFilter filter, int page = 0, int size = 200, string sort = "", bool descending = false)
    {
        var (where, args) = ViewWhere(job, view, filter);
        lock (sync)
        {
            var stats = filter == new ResultFilter() ? Counts(job) : null;
            var count = stats is null ? Convert.ToInt64(Scalar("SELECT COUNT(*) FROM results r WHERE " + where, args)) : view == 0 ? stats.Available : view == 1 ? stats.Other : stats.Pending;
            using var cmd = Command("SELECT r.data,r.job,CASE WHEN jobs.state='running' AND json_extract(r.data,'$.stage') NOT IN ('queued','moz_queued') THEN 'Diproses' WHEN jobs.state='paused' THEN 'Dijeda' WHEN json_extract(r.data,'$.stage')='moz_queued' THEN 'Menunggu Moz' ELSE 'Menunggu' END FROM results r JOIN jobs ON jobs.id=r.job WHERE " + where + " ORDER BY " + ViewOrder(sort, descending) + $" LIMIT {Math.Clamp(size, 1, 1000)} OFFSET {Math.Max(0, (long)page * size)}", args);
            using var reader = cmd.ExecuteReader();
            var rows = new List<Result>();
            while (reader.Read())
            {
                var r = JsonData.Read<Result>(reader.GetString(0));
                r.JobId = reader.GetString(1);
                r.WorkState = reader.GetString(2);
                rows.Add(r);
            }
            return new(rows, count);
        }
    }
    public EventPage Events(string? job, string search = "", int page = 0, long? anchor = null, int size = 200)
    {
        lock (sync)
        {
            var argsList = new List<object>();
            var predicates = new List<string>();
            void Add(string expression, object value)
            {
                predicates.Add(expression.Replace("?", "$p" + argsList.Count));
                argsList.Add(value);
            }
            if (job is not null)
                Add("job=?", job);
            if (search.Length > 0)
                Add("instr(lower(domain),lower(?))>0", search);
            if (anchor.HasValue)
                Add("seq<=?", anchor.Value);
            var where = predicates.Count == 0 ? "1=1" : string.Join(" AND ", predicates);
            var args = argsList.ToArray();
            var count = search.Length == 0 && !anchor.HasValue ? Counts(job).Events : Convert.ToInt64(Scalar("SELECT COUNT(*) FROM event_log WHERE " + where, args));
            using var cmd = Command("SELECT seq,at,job,domain,stage,status,message FROM event_log WHERE " + where + $" ORDER BY seq DESC LIMIT {Math.Clamp(size, 1, 1000)} OFFSET {Math.Max(0, (long)page * size)}", args);
            using var reader = cmd.ExecuteReader();
            var rows = new List<EventRow>();
            while (reader.Read())
                rows.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6)));
            return new(rows, count);
        }
    }
    public IEnumerable<Result> ExportAvailable(string? job, ResultFilter filter, string sort = "", bool descending = false)
    {
        var (where, args) = ViewWhere(job, 0, filter);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT r.data,r.job FROM results r JOIN jobs ON jobs.id=r.job WHERE " + where + " ORDER BY " + ViewOrder(sort, descending);
        for (int i = 0; i < args.Length; i++)
            cmd.Parameters.AddWithValue("$p" + i, args[i]);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var result = JsonData.Read<Result>(reader.GetString(0));
            result.JobId = reader.GetString(1);
            yield return result;
        }
    }
}
