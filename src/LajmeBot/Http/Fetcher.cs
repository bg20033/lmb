using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace LajmeBot.Http;

public sealed record FetchResult(bool Ok, int Status, string Body, string FinalUrl, string? Error = null);

public interface IFetcher
{
    Task<FetchResult> GetAsync(string url, CancellationToken ct);
}

/// <summary>
/// HttpClient wrapper with an honest User-Agent, a minimum delay per host and retries on 429/5xx.
/// </summary>
public sealed class PoliteFetcher : IFetcher, IDisposable
{
    private readonly HttpClient _http;
    private readonly BotConfig _cfg;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _hostGates = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastHit = new();

    public PoliteFetcher(BotConfig cfg)
    {
        _cfg = cfg;
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(cfg.RequestTimeoutSeconds) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(cfg.UserAgent);
        _http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml;q=0.9,*/*;q=0.8");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("sq,en;q=0.7");
    }

    public async Task<FetchResult> GetAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
            return new(false, 0, "", url, "invalid url");

        return await RawGetAsync(uri, ct);
    }

    private async Task<FetchResult> RawGetAsync(Uri uri, CancellationToken ct)
    {
        var gate = _hostGates.GetOrAdd(uri.Host, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                if (_lastHit.TryGetValue(uri.Host, out var last))
                {
                    var wait = last.AddMilliseconds(_cfg.MinDelayPerHostMs) - DateTime.UtcNow;
                    if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                }
                _lastHit[uri.Host] = DateTime.UtcNow;
                try
                {
                    using var resp = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
                    var status = (int)resp.StatusCode;
                    if ((status == 429 || status >= 500) && attempt < 3)
                    {
                        var delay = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2 * attempt * attempt);
                        await Task.Delay(delay > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : delay, ct);
                        continue;
                    }
                    var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                    if (bytes.Length > 4_000_000) bytes = bytes[..4_000_000];
                    var body = Decode(bytes, resp.Content.Headers.ContentType?.CharSet);
                    var final = resp.RequestMessage?.RequestUri?.ToString() ?? uri.ToString();
                    return new(resp.IsSuccessStatusCode, status, body, final, resp.IsSuccessStatusCode ? null : $"HTTP {status}");
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    if (attempt >= 3) return new(false, 0, "", uri.ToString(), ex.Message);
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
                }
            }
        }
        finally { gate.Release(); }
    }

    private static string Decode(byte[] bytes, string? charset)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(charset))
                return Encoding.GetEncoding(charset.Trim('"')).GetString(bytes);
        }
        catch { /* fall back to UTF-8 */ }
        return Encoding.UTF8.GetString(bytes);
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Offline fetcher for tests: maps URLs to files listed in fixtures/map.json.</summary>
public sealed class FixtureFetcher : IFetcher
{
    private readonly Dictionary<string, string> _map;
    private readonly string _dir;

    public FixtureFetcher(string dir)
    {
        _dir = dir;
        _map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(dir, "map.json")))!;
    }

    public Task<FetchResult> GetAsync(string url, CancellationToken ct)
    {
        if (_map.TryGetValue(url, out var file))
            return Task.FromResult(new FetchResult(true, 200, File.ReadAllText(Path.Combine(_dir, file)), url));
        return Task.FromResult(new FetchResult(false, 404, "", url, "HTTP 404 (no fixture)"));
    }
}
