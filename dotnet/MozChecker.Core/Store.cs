using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace MozChecker.Core;

public sealed class Store : IDisposable
{
    private readonly SqliteConnection db;
    private readonly object sync = new();
    public string Path
    {
        get;
    }
    public static string DataDirectory => Environment.GetEnvironmentVariable("MOZ_CHECKER_DATA_DIR") is { Length: > 0 } custom ? custom : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MozChecker");
    public Store(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        var existing = File.Exists(path);
        db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        if (existing && !File.Exists(path + ".before-dotnet.bak"))
        {
            using var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path + ".before-dotnet.bak", Pooling = false }.ToString());
            backup.Open();
            db.BackupDatabase(backup);
        }
        Execute("PRAGMA journal_mode=WAL;");
        Execute("CREATE TABLE IF NOT EXISTS jobs(id TEXT PRIMARY KEY, created TEXT, state TEXT); CREATE TABLE IF NOT EXISTS results(job TEXT, domain TEXT, ordinal INTEGER, data TEXT, PRIMARY KEY(job, domain)); CREATE TABLE IF NOT EXISTS cache(source TEXT, domain TEXT, checked TEXT, data TEXT, PRIMARY KEY(source,domain)); CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT); CREATE TABLE IF NOT EXISTS quota(period TEXT PRIMARY KEY, used INTEGER NOT NULL);");
        // An interrupted job is never silently restarted on application launch.
        Execute("UPDATE jobs SET state='paused' WHERE state IN ('running','queued')");
    }
    private SqliteCommand Command(string sql, params object[] args)
    {
        var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        for (int i = 0; i < args.Length; i++)
            cmd.Parameters.AddWithValue("$p" + i, args[i]);
        return cmd;
    }
    private void Execute(string sql, params object[] args)
    {
        using var cmd = Command(sql, args);
        cmd.ExecuteNonQuery();
    }
    public Settings GetSettings()
    {
        lock (sync)
        {
            var values = JsonSerializer.SerializeToNode(new Settings(), JsonData.Options)!.AsObject();
            using var cmd = Command("SELECT key,value FROM settings");
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                if (values.ContainsKey(reader.GetString(0)))
                    values[reader.GetString(0)] = System.Text.Json.Nodes.JsonNode.Parse(reader.GetString(1));
            var settings = values.Deserialize<Settings>(JsonData.Options)!;
            settings.Validate();
            return settings;
        }
    }
    public void Configure(Settings settings)
    {
        settings.Validate();
        lock (sync)
        {
            using var tx = db.BeginTransaction();
            foreach (var p in JsonSerializer.SerializeToElement(settings, JsonData.Options).EnumerateObject())
                Execute("INSERT OR REPLACE INTO settings VALUES ($p0,$p1)", p.Name, p.Value.GetRawText());
            tx.Commit();
        }
    }
    public string NewJob(IEnumerable<string> domains)
    {
        var list = domains.Distinct().ToArray();
        if (list.Length is 0 or > 1000)
            throw new ArgumentException("Masukkan 1–1.000 domain.");
        var id = Guid.NewGuid().ToString("N");
        lock (sync)
        {
            using var tx = db.BeginTransaction();
            Execute("INSERT INTO jobs VALUES ($p0,$p1,'queued')", id, JsonData.Now());
            for (int i = 0; i < list.Length; i++)
                Execute("INSERT INTO results VALUES ($p0,$p1,$p2,$p3)", id, list[i], i, JsonData.Write(new Result { Domain = list[i] }));
            tx.Commit();
        }
        return id;
    }
    public List<Job> Jobs()
    {
        lock (sync)
        {
            using var cmd = Command("SELECT id,created,state FROM jobs ORDER BY created");
            using var reader = cmd.ExecuteReader();
            var jobs = new List<Job>();
            while (reader.Read())
                jobs.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            return jobs;
        }
    }
    public void State(string job, string state)
    {
        lock (sync)
            Execute("UPDATE jobs SET state=$p0 WHERE id=$p1", state, job);
    }
    public List<Result> Results(string job)
    {
        lock (sync)
        {
            using var cmd = Command("SELECT data FROM results WHERE job=$p0 ORDER BY ordinal", job);
            using var reader = cmd.ExecuteReader();
            var rows = new List<Result>();
            while (reader.Read())
                rows.Add(JsonData.Read<Result>(reader.GetString(0)));
            return rows;
        }
    }
    public void Save(string job, Result result)
    {
        lock (sync)
            Execute("UPDATE results SET data=$p0 WHERE job=$p1 AND domain=$p2", JsonData.Write(result), job, result.Domain);
    }
    public T? CacheGet<T>(string source, string domain, int ttl)
    {
        lock (sync)
        {
            using var cmd = Command("SELECT checked,data FROM cache WHERE source=$p0 AND domain=$p1", source, domain);
            using var reader = cmd.ExecuteReader();
            return reader.Read() && JsonData.Fresh(reader.GetString(0), ttl) ? JsonData.Read<T>(reader.GetString(1)) : default;
        }
    }
    public void CachePut<T>(string source, string domain, T value)
    {
        lock (sync)
            Execute("INSERT OR REPLACE INTO cache VALUES ($p0,$p1,$p2,$p3)", source, domain, JsonData.Now(), JsonData.Write(value));
    }
    public int Used(string period)
    {
        lock (sync)
        {
            using var cmd = Command("SELECT used FROM quota WHERE period=$p0", period);
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }
    }
    public bool ReserveRow(string period, int budget, int reserve)
    {
        lock (sync)
        {
            using var tx = db.BeginTransaction(deferred: false);
            if (Used(period) >= Math.Max(0, budget - reserve))
            {
                tx.Rollback();
                return false;
            }
            Execute("INSERT INTO quota VALUES ($p0,1) ON CONFLICT(period) DO UPDATE SET used=used+1", period);
            tx.Commit();
            return true;
        }
    }
    public void RetryFailed(string job)
    {
        lock (sync)
        {
            foreach (var r in Results(job).Where(x => x.Errors.Count > 0 || x.History == "unchecked" || x.Status == "unknown"))
            {
                r.Stage = "queued";
                Save(job, r);
            }
            State(job, "queued");
        }
    }
    public void Dispose()
    {
        lock (sync)
            db.Dispose();
    }
}
