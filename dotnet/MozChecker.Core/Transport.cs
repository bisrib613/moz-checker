using System.Net;
using System.Text.Json.Nodes;

namespace MozChecker.Core;

public sealed class SourceException(string message) : Exception(message);
public sealed class RateGate(TimeSpan interval)
{
    private readonly SemaphoreSlim mutex = new(1, 1);
    private long next;
    private long penaltyUntil;
    public void Defer(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            return;
        var proposed = Environment.TickCount64 + (long)Math.Min(duration.TotalMilliseconds, TimeSpan.FromDays(365).TotalMilliseconds);
        long observed;
        do
        {
            observed = Interlocked.Read(ref penaltyUntil);
            if (observed >= proposed)
                return;
        }
        while (Interlocked.CompareExchange(ref penaltyUntil, proposed, observed) != observed);
    }
    public async Task WaitAsync(CancellationToken ct)
    {
        await mutex.WaitAsync(ct);
        try
        {
            while (true)
            {
                var delay = Math.Max(next, Interlocked.Read(ref penaltyUntil)) - Environment.TickCount64;
                if (delay <= 0)
                    break;
                if (delay > 120_000)
                    throw new SourceException("Retry-After panjang; sumber ditunda, coba ulang nanti");
                await Task.Delay(TimeSpan.FromMilliseconds(delay), ct);
            }
            next = Environment.TickCount64 + (long)interval.TotalMilliseconds;
        }
        finally { mutex.Release(); }
    }
}
public sealed record WebReply(int Status, string ContentType, string Text)
{
    public JsonNode Json(bool requireSuccess = true)
    {
        if (requireSuccess && Status != 200)
            throw new SourceException($"HTTP {Status}");
        try
        {
            return JsonNode.Parse(Text) ?? throw new SourceException("Respons JSON kosong");
        }
        catch (System.Text.Json.JsonException) { throw new SourceException("Respons JSON tidak valid"); }
    }
}
public sealed class Transport : IDisposable
{
    private readonly HttpClient client;
    private readonly TimeSpan requestTimeout;
    public Transport(HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        requestTimeout = timeout ?? TimeSpan.FromSeconds(25);
        if (requestTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        client = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MozChecker/0.5 (Windows desktop research)");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Encoding", "identity");
    }
    public async Task<WebReply> SendAsync(Func<HttpRequestMessage> request, RateGate? gate, CancellationToken ct, int attempts = 3, int maxBytes = 2_000_000)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (gate is not null)
                await gate.WaitAsync(ct);
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(requestTimeout);
                var requestToken = deadline.Token;
                using var message = request();
                if (message.RequestUri?.Scheme != "https")
                    throw new SourceException("Hanya koneksi HTTPS yang didukung");
                using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, requestToken);
                if ((int)response.StatusCode is 429 or 502 or 503 or 504)
                {
                    var retry = response.Headers.RetryAfter;
                    var delay = retry?.Delta ?? (retry?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    gate?.Defer(delay);
                    // Do not retry a billed request. The shared gate also delays the NEXT domain.
                    if (attempts == 1)
                        throw new SourceException($"HTTP {(int)response.StatusCode}; permintaan berpotensi terhitung, tidak diulang");
                    if (delay > TimeSpan.FromSeconds(120))
                        throw new SourceException("Retry-After panjang; coba ulang nanti");
                    if (delay > TimeSpan.Zero)
                        await Task.Delay(delay, ct);
                    continue;
                }
                await using var stream = await response.Content.ReadAsStreamAsync(requestToken);
                using var buffer = new MemoryStream();
                var chunk = new byte[8192];
                int count;
                while ((count = await stream.ReadAsync(chunk, requestToken)) > 0)
                {
                    if (buffer.Length + count > maxBytes)
                        throw new SourceException("Respons terlalu besar; pemeriksaan tidak lengkap");
                    buffer.Write(chunk, 0, count);
                }
                var encoding = System.Text.Encoding.UTF8;
                try
                {
                    if (response.Content.Headers.ContentType?.CharSet is { } charset)
                        encoding = System.Text.Encoding.GetEncoding(charset.Trim('"'));
                }
                catch (ArgumentException) { }
                return new((int)response.StatusCode, response.Content.Headers.ContentType?.MediaType ?? "", encoding.GetString(buffer.ToArray()));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { if (attempt == attempts - 1) throw new SourceException("Koneksi timeout"); }
            catch (HttpRequestException) { if (attempt == attempts - 1) throw new SourceException("Koneksi sumber gagal"); }
            catch (IOException) { if (attempt == attempts - 1) throw new SourceException("Pembacaan respons sumber terputus"); }
            if (attempt + 1 < attempts)
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
        }
        throw new SourceException("Layanan sibuk; coba ulang nanti");
    }
    public void Dispose() => client.Dispose();
}
