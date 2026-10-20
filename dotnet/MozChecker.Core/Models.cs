using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MozChecker.Core;

public static class JsonData
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true };
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Options) ?? throw new InvalidDataException("Data lokal tidak valid");
    public static bool Fresh(string? value, int seconds) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp) && DateTimeOffset.UtcNow - stamp >= TimeSpan.Zero && DateTimeOffset.UtcNow - stamp < TimeSpan.FromSeconds(seconds);
    public static string Now() => DateTimeOffset.UtcNow.ToString("O");
}

public sealed class Result
{
    public string Domain { get; set; } = "";
    public string Stage { get; set; } = "queued";
    public string Status { get; set; } = "unknown";
    public int? RdapHttp { get; set; }
    public string RdapUrl { get; set; } = "";
    public string Dns { get; set; } = "";
    public string Expires { get; set; } = "";
    public string Registrar { get; set; } = "";
    public string AvailabilityAt { get; set; } = "";
    public double? Opr { get; set; }
    public string OprAt { get; set; } = "";
    public string History { get; set; } = "";
    public int? Snapshots { get; set; }
    public int Sampled { get; set; }
    public List<HistoryMatch> Matches { get; set; } = [];
    public string HistoryAt { get; set; } = "";
    public double? Da { get; set; }
    public double? Pa { get; set; }
    public double? Spam { get; set; }
    public string MozAt { get; set; } = "";
    public string MozState { get; set; } = "";
    public Dictionary<string, string> Errors { get; set; } = [];
    [JsonIgnore] public bool Ready => Status == "available" && JsonData.Fresh(AvailabilityAt, 900) && History is "valid" or "no_history";
    [JsonIgnore] public string Readiness => Ready ? "Kandidat · cek registrar" : Status == "available" && !JsonData.Fresh(AvailabilityAt, 900) ? "Basi · periksa ulang" : "Belum lolos";
}
public sealed record HistoryMatch(string Keyword, string Snapshot);
public sealed record ImportResult(List<string> Domains, int Duplicates, List<string> Invalid);
public sealed record Job(string Id, string Created, string State)
{
    public override string ToString() => $"{Created[..Math.Min(16, Created.Length)].Replace('T', ' ')} UTC · {Id[..8]} · {State}";
}
public sealed class Settings
{
    public int MozBudget { get; set; } = 50;
    public int MozReserve { get; set; } = 5;
    public string MozPeriod { get; set; } = "";
    public int Samples { get; set; } = 5;
    public bool EmptyValid { get; set; } = true;
    public double OprMin { get; set; }
    public List<string> Keywords { get; set; } = ["judi", "slot gacor", "casino", "togel", "sportsbook", "porn", "xxx", "bokep", "pornography"];
    [JsonIgnore] public string Period => string.IsNullOrWhiteSpace(MozPeriod) ? DateTimeOffset.UtcNow.ToString("yyyy-MM") : MozPeriod;
    public void Validate()
    {
        if (MozBudget < 0 || MozReserve < 0 || MozReserve > MozBudget || Samples is < 1 or > 20 || OprMin is < 0 or > 10 || !double.IsFinite(OprMin))
            throw new ArgumentException("Anggaran/cadangan tidak valid; sampel 1–20 dan OPR 0–10.");
        if (MozPeriod.Length > 40 || Keywords.Count > 200 || Keywords.Any(x => x.Length > 100)) throw new ArgumentException("Periode atau daftar kata kunci terlalu panjang.");
    }
}

public static partial class Domains
{
    private static readonly IdnMapping Idn = new();
    private static readonly HashSet<string> Suffixes = LoadSuffixes();
    private static HashSet<string> LoadSuffixes()
    {
        using var stream = typeof(Domains).Assembly.GetManifestResourceStream("MozChecker.Core.Data.public_suffix_list.dat")!;
        using var reader = new StreamReader(stream);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.ReadLine() is { } line)
        {
            line = line.Trim();
            if (line.Length == 0 || line.StartsWith("//")) continue;
            var prefix = line.StartsWith('!') ? "!" : line.StartsWith("*.") ? "*." : "";
            result.Add(prefix + Idn.GetAscii(line[prefix.Length..]));
        }
        return result;
    }
    public static int PublicSuffixLabels(string domain)
    {
        var labels = domain.Split('.'); var best = 0;
        for (int i = 0; i < labels.Length; i++)
        {
            var tail = string.Join('.', labels[i..]);
            if (Suffixes.Contains("!" + tail)) return labels.Length - i - 1;
            if (Suffixes.Contains(tail)) best = Math.Max(best, labels.Length - i);
            if (i + 1 < labels.Length && Suffixes.Contains("*." + string.Join('.', labels[(i + 1)..]))) best = Math.Max(best, labels.Length - i);
        }
        return best;
    }
    public static bool Registrable(string domain) => PublicSuffixLabels(domain) is var n && n > 0 && domain.Split('.').Length == n + 1;
    public static string Normalize(string value)
    {
        value = value.Trim().Trim('"', '\'');
        if (!Uri.TryCreate(value.Contains("://") ? value : "https://" + value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || !uri.IsDefaultPort || uri.Authority.Contains(':'))
            throw new ArgumentException("Masukkan domain tanpa port atau kredensial.");
        var host = Idn.GetAscii(uri.Host.TrimEnd('.')).ToLowerInvariant();
        if (host.StartsWith("www.")) host = host[4..];
        var parts = host.Split('.');
        if (host.Length > 253 || parts.Length < 2 || IPAddress.TryParse(host, out _) || parts[^1].All(char.IsDigit) || parts.Any(x => !LabelPattern().IsMatch(x)))
            throw new ArgumentException("Nama domain tidak valid.");
        if (PublicSuffixLabels(host) > 0 && !Registrable(host)) throw new ArgumentException("Subdomain atau public suffix tidak dapat dibeli.");
        return host;
    }
    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.CultureInvariant)] private static partial Regex LabelPattern();
    public static List<List<string>> CsvRows(string text)
    {
        var rows = new List<List<string>>(); var row = new List<string>(); var cell = new StringBuilder(); var quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"') { if (quoted && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; } else quoted = !quoted; }
            else if (c == ',' && !quoted) { row.Add(cell.ToString()); cell.Clear(); }
            else if ((c == '\n' || c == '\r') && !quoted) { if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++; row.Add(cell.ToString()); rows.Add(row); row = []; cell.Clear(); }
            else cell.Append(c);
        }
        if (quoted) throw new ArgumentException("Tanda kutip CSV tidak lengkap.");
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }
    public static ImportResult Parse(string text)
    {
        var rows = CsvRows(text.TrimStart('\uFEFF')); var domains = new List<string>(); var seen = new HashSet<string>(); var invalid = new List<string>(); var duplicates = 0;
        var column = rows.Count == 0 ? -1 : rows[0].FindIndex(x => new[] { "domain", "domains", "url" }.Contains(x.Trim().ToLowerInvariant()));
        IEnumerable<string> values = column >= 0 ? rows.Skip(1).Where(x => x.Count > column).Select(x => x[column]) : rows.SelectMany(x => x).SelectMany(x => Regex.Split(x, @"[;\s]+"));
        foreach (var raw in values.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            string domain; try { domain = Normalize(raw); } catch (ArgumentException) { invalid.Add(raw); continue; }
            if (seen.Add(domain)) domains.Add(domain); else duplicates++;
            if (domains.Count > 1000) throw new ArgumentException("Maksimal 1.000 domain unik per hunting.");
        }
        return new(domains, duplicates, invalid);
    }
    public static IEnumerable<string> Generate(string keywords, string tlds)
    {
        var words = Regex.Split(keywords.Trim(), @"[,;\s]+").Where(x => x.Length > 0).ToArray();
        var suffixes = Regex.Split(tlds.Trim(), @"[,;\s]+").Where(x => x.Length > 0).ToArray();
        if ((long)words.Length * suffixes.Length > 1000) throw new ArgumentException("Maksimal 1.000 kombinasi per hunting.");
        return words.SelectMany(w => suffixes.Select(t => $"{w}.{t.TrimStart('.')}"));
    }
}

public static class CsvExport
{
    public static string Cell(string value)
    {
        if (value.Length > 0 && ("=+-@\t\r\n".Contains(value[0]) || "=+-@".Contains(value.TrimStart().FirstOrDefault()))) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
    public static void Save(string path, IEnumerable<Result> results)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        var properties = typeof(Result).GetProperties().Where(x => x.Name != nameof(Result.Readiness)).ToArray();
        writer.WriteLine(string.Join(',', properties.Select(x => Cell(JsonNamingPolicy.SnakeCaseLower.ConvertName(x.Name)))) + ",authority_sources");
        foreach (var r in results.Where(x => x.Status == "available"))
            writer.WriteLine(string.Join(',', properties.Select(p => Cell(p.GetValue(r) is var v && v is not null ? v is string s ? s : JsonData.Write(v) : ""))) + ",\"OPR; Moz DA; Moz PA; Moz Spam Score\"");
    }
}
