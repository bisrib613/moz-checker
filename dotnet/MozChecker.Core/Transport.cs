using System.Net;
using System.Text.Json.Nodes;

namespace MozChecker.Core;

public sealed class SourceException(string message) : Exception(message);
public sealed class RateGate(TimeSpan interval)
{
    private readonly SemaphoreSlim mutex = new(1, 1);
    private long next;
    public async Task WaitAsync(CancellationToken ct)
    {
        await mutex.WaitAsync(ct);
        try { var delay = next - Environment.TickCount64; if (delay > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay), ct); next = Environment.TickCount64 + (long)interval.TotalMilliseconds; }
        finally { mutex.Release(); }
    }
}
public sealed record WebReply(int Status, string ContentType, string Text)
{
    public JsonNode Json(bool requireSuccess = true)
    {
        if (requireSuccess && Status != 200) throw new SourceException($"HTTP {Status}");
        try { return JsonNode.Parse(Text) ?? throw new SourceException("Respons JSON kosong"); }
        catch (System.Text.Json.JsonException) { throw new SourceException("Respons JSON tidak valid"); }
    }
}
public sealed class Transport : IDisposable
{
    private readonly HttpClient client;
    public Transport(HttpMessageHandler? handler = null)
    {
        client = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None }) { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MozChecker/0.3 (Windows desktop research)");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Encoding", "identity");
    }
    public async Task<WebReply> SendAsync(Func<HttpRequestMessage> request, RateGate? gate, CancellationToken ct, int attempts = 3, int maxBytes = 2_000_000)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested(); if (gate is not null) await gate.WaitAsync(ct);
            try
            {
                using var message = request();
                if (message.RequestUri?.Scheme != "https") throw new SourceException("Hanya koneksi HTTPS yang didukung");
                using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
                if ((int)response.StatusCode is 429 or 502 or 503 or 504)
                {
                    // Billed requests are NEVER retried, including ambiguous throttling/errors.
                    if (attempts == 1) throw new SourceException($"HTTP {(int)response.StatusCode}; permintaan berpotensi terhitung, tidak diulang");
                    var retry = response.Headers.RetryAfter;
                    var delay = retry?.Delta ?? (retry?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    if (delay > TimeSpan.FromSeconds(120)) throw new SourceException("Retry-After panjang; coba ulang nanti");
                    if (delay > TimeSpan.Zero) await Task.Delay(delay, ct); continue;
                }
                await using var stream = await response.Content.ReadAsStreamAsync(ct); using var buffer = new MemoryStream(); var chunk = new byte[8192];
                int count; while ((count = await stream.ReadAsync(chunk, ct)) > 0) { if (buffer.Length + count > maxBytes) throw new SourceException("Respons terlalu besar; pemeriksaan tidak lengkap"); buffer.Write(chunk, 0, count); }
                var encoding = System.Text.Encoding.UTF8;
                try { if (response.Content.Headers.ContentType?.CharSet is { } charset) encoding = System.Text.Encoding.GetEncoding(charset.Trim('"')); } catch (ArgumentException) { }
                return new((int)response.StatusCode, response.Content.Headers.ContentType?.MediaType ?? "", encoding.GetString(buffer.ToArray()));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { if (attempt == attempts - 1) throw new SourceException("Koneksi timeout"); }
            catch (HttpRequestException) { if (attempt == attempts - 1) throw new SourceException("Koneksi sumber gagal"); }
            if (attempt + 1 < attempts) await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
        }
        throw new SourceException("Layanan sibuk; coba ulang nanti");
    }
    public void Dispose() => client.Dispose();
}
