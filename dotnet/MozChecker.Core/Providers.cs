using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DnsClient;
using HtmlAgilityPack;

namespace MozChecker.Core;

public sealed record ApiKeys(string OprKey = "", string MozId = "", string MozSecret = "");
public interface IProviders
{
    Task AvailabilityAsync(Result result, bool force, CancellationToken ct);
    Task OprAsync(IReadOnlyList<Result> results, CancellationToken ct);
    Task HistoryAsync(Result result, Settings settings, CancellationToken ct);
    Task MozAsync(Result result, CancellationToken ct);
}
public sealed class Providers(Store store, ApiKeys keys, Transport http) : IProviders
{
    private readonly RateGate archiveGate = new(TimeSpan.FromSeconds(4.1));
    private readonly RateGate oprGate = new(TimeSpan.FromSeconds(1.1));
    private readonly RateGate mozGate = new(TimeSpan.FromSeconds(10.1));
    private readonly ConcurrentDictionary<string, RateGate> rdapGates = new();
    private readonly SemaphoreSlim bootstrapLock = new(1, 1);
    private JsonNode? bootstrap;
    private readonly LookupClient dns = new(new LookupClientOptions { Timeout = TimeSpan.FromSeconds(5), Retries = 0, UseCache = false, ThrowDnsErrors = false });
    private async Task<JsonNode> BootstrapAsync(CancellationToken ct)
    {
        await bootstrapLock.WaitAsync(ct);
        try
        {
            if (bootstrap is not null)
                return bootstrap;
            bootstrap = store.CacheGet<JsonNode>("iana", "bootstrap", 7 * 86400);
            if (bootstrap is null)
            {
                var downloaded = (await http.SendAsync(() => new(HttpMethod.Get, "https://data.iana.org/rdap/dns.json"), null, ct)).Json();
                if (downloaded is not JsonObject || downloaded["services"] is not JsonArray)
                    throw new SourceException("Bootstrap IANA tidak valid");
                bootstrap = downloaded;
                store.CachePut("iana", "bootstrap", bootstrap);
            }
            return bootstrap;
        }
        finally { bootstrapLock.Release(); }
    }
    public static Uri RdapUri(string baseUrl, string domain)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0 || !uri.IsDefaultPort || !uri.Host.Contains('.') || uri.Host.EndsWith(".local") || uri.Host.EndsWith(".localhost") || IPAddress.TryParse(uri.Host, out _))
            throw new SourceException("Rute RDAP tidak aman");
        return new(baseUrl.TrimEnd('/') + "/domain/" + domain);
    }
    private async Task<string> DnsAsync(string domain, CancellationToken ct)
    {
        try
        {
            var answer = await dns.QueryAsync(domain, QueryType.SOA, QueryClass.IN, ct);
            return (int)answer.Header.ResponseCode == 3 ? "nxdomain" : (int)answer.Header.ResponseCode == 0 ? answer.Answers.Count > 0 ? "exists" : "exists_no_soa" : "error";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is DnsResponseException or System.Net.Sockets.SocketException or OperationCanceledException) { return "error"; }
    }
    private static void CopyAvailability(Result source, Result target)
    {
        target.Status = source.Status;
        target.RdapHttp = source.RdapHttp;
        target.RdapUrl = source.RdapUrl;
        target.Dns = source.Dns;
        target.Expires = source.Expires;
        target.Registrar = source.Registrar;
        target.AvailabilityAt = source.AvailabilityAt;
        if (source.Errors.TryGetValue("availability", out var reason))
            target.Errors["availability"] = reason;
    }
    public static string AvailabilityVerdict(int httpStatus, JsonNode? body, string dnsStatus, string domain)
    {
        if (httpStatus == 404 && body is JsonObject && body["errorCode"]?.ToString() == "404" && dnsStatus == "nxdomain")
            return "available";
        if (httpStatus != 200 || body is not JsonObject || !string.Equals(body["objectClassName"]?.ToString(), "domain", StringComparison.OrdinalIgnoreCase) || !string.Equals(body["ldhName"]?.ToString()?.TrimEnd('.'), domain, StringComparison.OrdinalIgnoreCase))
            return "unknown";
        var statuses = (body["status"] as JsonArray)?.Select(x => x?.ToString().Replace(" ", "").ToLowerInvariant()).ToArray() ?? [];
        return statuses.Any(x => x?.Contains("pendingdelete") == true) ? "pending_delete" : statuses.Any(x => x?.Contains("redemption") == true) ? "redemption" : "registered";
    }
    public async Task AvailabilityAsync(Result result, bool force, CancellationToken ct)
    {
        result.Errors.Remove("availability");
        if (!force && store.CacheGet<Result>("availability", result.Domain, 900) is { } cached)
        {
            CopyAvailability(cached, result);
            return;
        }
        result.Status = "unknown";
        result.AvailabilityAt = JsonData.Now();
        result.RdapHttp = null;
        result.RdapUrl = "";
        result.Expires = "";
        result.Registrar = "";
        if (!Domains.Registrable(result.Domain))
        {
            result.Errors["availability"] = "Domain tidak dikenali Public Suffix List";
            return;
        }
        result.Dns = await DnsAsync(result.Domain, ct);
        var bootstrap = await BootstrapAsync(ct);
        var route = bootstrap["services"]!.AsArray().Where(x => x is JsonArray a && a.Count == 2 && a[0] is JsonArray tlds && a[1] is JsonArray && tlds.Any(t => t?.ToString() == result.Domain.Split('.')[^1])).SelectMany(x => x![1]!.AsArray()).Select(x => x!.ToString()).FirstOrDefault(x => x.StartsWith("https://"));
        if (route is null)
        {
            result.Errors["availability"] = "TLD tanpa RDAP HTTPS; WHOIS belum didukung";
            return;
        }
        var uri = RdapUri(route, result.Domain);
        result.RdapUrl = uri.AbsoluteUri;
        var reply = await http.SendAsync(() => new(HttpMethod.Get, uri), rdapGates.GetOrAdd(uri.Host, _ => new(TimeSpan.FromSeconds(.5))), ct);
        result.RdapHttp = reply.Status;
        JsonNode? body;
        try
        {
            body = reply.Json(false);
        }
        catch (SourceException) { body = null; }
        result.Status = AvailabilityVerdict(reply.Status, body, result.Dns, result.Domain);
        if (result.Status == "unknown")
            result.Errors["availability"] = $"Bukti belum cukup (RDAP {reply.Status}; DNS {result.Dns})";
        if (result.Status is "registered" or "redemption" or "pending_delete")
        {
            result.Expires = (body?["events"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(x => x["eventAction"]?.ToString() == "expiration")?["eventDate"]?.ToString() ?? "";
            var registrar = (body?["entities"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(x => x["roles"] is JsonArray roles && roles.Any(r => r?.ToString() == "registrar"));
            if (registrar?["vcardArray"] is JsonArray vcard && vcard.Count > 1 && vcard[1] is JsonArray fields)
                result.Registrar = fields.FirstOrDefault(x => x is JsonArray item && item.Count > 3 && item[0]?.ToString() == "fn")?[3]?.ToString() ?? "";
        }
        store.CachePut("availability", result.Domain, result);
    }
    public async Task OprAsync(IReadOnlyList<Result> results, CancellationToken ct)
    {
        var pending = new List<Result>();
        foreach (var r in results)
        {
            if (store.CacheGet<JsonNode>("opr", r.Domain, 30 * 86400) is { } cache)
            {
                r.Opr = Metric(cache["opr"], 10);
                r.OprAt = cache["opr_at"]?.ToString() ?? "";
                r.Errors.Remove("opr");
            }
            else
                pending.Add(r);
        }
        if (string.IsNullOrWhiteSpace(keys.OprKey))
            return;
        foreach (var batch in pending.Chunk(100))
        {
            var query = string.Join('&', batch.Select(r => "domains[]=" + Uri.EscapeDataString(r.Domain)));
            var data = (await http.SendAsync(() => { var request = new HttpRequestMessage(HttpMethod.Get, "https://openpagerank.domcop.com/api/v1.0/getPageRank?" + query); request.Headers.Add("API-OPR", keys.OprKey); return request; }, oprGate, ct)).Json();
            if (data is not JsonObject || data["response"] is not JsonArray rows)
                throw new SourceException("Struktur OPR tidak valid");
            foreach (var r in batch)
            {
                var row = rows.OfType<JsonObject>().FirstOrDefault(x => x["domain"]?.ToString() == r.Domain);
                if (row?["status_code"]?.ToString() == "200" && Metric(row["page_rank_decimal"], 10) is { } score)
                {
                    r.Opr = score;
                    r.OprAt = JsonData.Now();
                    r.Errors.Remove("opr");
                    store.CachePut("opr", r.Domain, new
                    {
                        Opr = score,
                        r.OprAt
                    });
                }
                else
                    r.Errors["opr"] = "Tidak ada data OPR";
            }
        }
    }
    public static double? Metric(JsonNode? value, double max = 100) => value is JsonValue v && v.TryGetValue<double>(out var n) && double.IsFinite(n) && n >= 0 && n <= max ? n : null;
    public static string VisibleText(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        foreach (var node in doc.DocumentNode.SelectNodes("//script|//style|//noscript|//template") ?? Enumerable.Empty<HtmlNode>())
            node.Remove();
        return HtmlEntity.DeEntitize(string.Join(' ', doc.DocumentNode.DescendantsAndSelf().Where(x => x.NodeType == HtmlNodeType.Text).Select(x => x.InnerText)));
    }
    public async Task HistoryAsync(Result result, Settings settings, CancellationToken ct)
    {
        result.History = "unchecked";
        result.HistoryAt = JsonData.Now();
        result.Matches = [];
        result.Sampled = 0;
        result.Snapshots = null;
        var url = "https://web.archive.org/cdx/search/cdx?url=" + Uri.EscapeDataString(result.Domain + "/*") + "&output=json&filter=statuscode:200&filter=mimetype:text/html&collapse=digest&fl=timestamp,original&limit=10001";
        var data = (await http.SendAsync(() => new(HttpMethod.Get, url), archiveGate, ct, maxBytes: 3_000_000)).Json();
        if (data is not JsonArray rows || (rows.Count > 0 && rows[0]?.ToJsonString() != "[\"timestamp\",\"original\"]"))
            throw new SourceException("Metadata arsip tidak valid");
        var entries = rows.Skip(1).ToArray();
        if (entries.Length >= 10001)
            throw new SourceException("Arsip melebihi batas metadata; periksa manual");
        result.Snapshots = entries.Length;
        if (entries.Length == 0)
        {
            result.History = settings.EmptyValid ? "no_history" : "unchecked";
            return;
        }
        var count = Math.Min(settings.Samples, entries.Length);
        var failed = false;
        var indices = Enumerable.Range(0, count).Select(i => (int)Math.Round((double)i * (entries.Length - 1) / Math.Max(1, count - 1))).Distinct();
        foreach (var i in indices)
        {
            var entry = entries[i];
            if (entry is not JsonArray pair || pair.Count != 2)
                throw new SourceException("Entri arsip tidak valid");
            var stamp = pair[0]?.ToString() ?? "";
            var original = pair[1]?.ToString() ?? "";
            if (!Regex.IsMatch(stamp, @"^\d{14}$", RegexOptions.CultureInvariant) || !Uri.TryCreate(original, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || (uri.Host != result.Domain && uri.Host != "www." + result.Domain))
                throw new SourceException("Snapshot tidak cocok dengan domain");
            var snapshotUrl = $"https://web.archive.org/web/{stamp}id_/{original}";
            try
            {
                var reply = await http.SendAsync(() => new(HttpMethod.Get, snapshotUrl), archiveGate, ct);
                if (reply.Status != 200 || !reply.ContentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                {
                    failed = true;
                    continue;
                }
                var text = VisibleText(reply.Text);
                result.Sampled++;
                foreach (var keyword in settings.Keywords.Where(x => !string.IsNullOrWhiteSpace(x)))
                    if (Regex.IsMatch(text, @"(?<!\w)" + Regex.Escape(keyword.Trim()) + @"(?!\w)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                        result.Matches.Add(new(keyword, snapshotUrl));
            }
            catch (SourceException) { failed = true; }
        }
        result.History = result.Matches.Count > 0 ? "invalid" : failed ? "unchecked" : "valid";
    }
    public async Task MozAsync(Result result, CancellationToken ct)
    {
        var data = (await http.SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "https://lsapi.seomoz.com/v2/url_metrics");
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(keys.MozId + ":" + keys.MozSecret)));
            request.Content = new StringContent(JsonData.Write(new
            {
                Targets = new[] { "https://" + result.Domain + "/" }
            }), Encoding.UTF8, "application/json");
            return request;
        }, mozGate, ct, attempts: 1)).Json();
        if (data is not JsonObject || data["results"] is not JsonArray { Count: > 0 } rows || rows[0] is not JsonObject row)
            throw new SourceException("Struktur Moz tidak valid");
        result.Da = Metric(row["domain_authority"]);
        result.Pa = Metric(row["page_authority"]);
        result.Spam = Metric(row["spam_score"]);
        result.MozAt = JsonData.Now();
        result.MozState = result.Da is not null || result.Pa is not null || result.Spam is not null ? "ok" : "no_data";
    }
}
