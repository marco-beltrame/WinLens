using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WinLens.Services.Translation;

/// <summary>
/// The Microsoft Translator endpoint Edge's built-in page translation uses: no account or
/// key, automatic source detection per line, and reachable from networks where Google is not.
/// <para>
/// Edge hands out a short-lived JWT from a public URL; it is cached until shortly before it
/// expires. Lines go out as the elements of a JSON array, so line breaks inside a line are
/// fine and every translation comes back at the index of its source.
/// </para>
/// </summary>
internal sealed class MicrosoftTranslatorProvider : ITranslationProvider
{
    private const string AuthEndpoint = "https://edge.microsoft.com/translate/auth";
    private const string Endpoint = "https://api-edge.cognitive.microsofttranslator.com/translate";

    // The service takes up to 1000 elements and 50000 characters; stay well inside both.
    internal const int MaxBatchChars = 10_000;
    internal const int MaxBatchLines = 100;

    // Same idea as Google's breaker: after a 429 or a stall, skip the service for a while.
    private const long CooldownMs = 30_000;
    private long _blockedUntil;

    // Tokens are valid for about ten minutes; refresh a minute early, or assume this long
    // when the token can't be decoded.
    private static readonly TimeSpan TokenMargin = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DefaultTokenLife = TimeSpan.FromMinutes(8);

    private sealed record Token(string Value, long ValidUntil);

    // Concurrent callers await the same fetch. Guarded by _tokenLock.
    private readonly object _tokenLock = new();
    private Task<Token?>? _tokenTask;

    private readonly TranslationHttp _http;

    public MicrosoftTranslatorProvider(TranslationHttp http) => _http = http;

    public string Name => "Microsoft";

    public async Task<string?[]> TranslateAsync(
        IReadOnlyList<TranslationRequest> items, string target, CancellationToken ct)
    {
        var results = new string?[items.Count];
        var all = Enumerable.Range(0, items.Count).ToList();
        var work = Batching.Chunk(all, i => items[i].Text.Length, MaxBatchChars, MaxBatchLines)
            .Select(chunk => TranslateBatchAsync(items, chunk, target, results, ct))
            .ToArray();
        await Task.WhenAll(work);
        return results;
    }

    private async Task TranslateBatchAsync(
        IReadOnlyList<TranslationRequest> items, List<int> chunk, string target, string?[] results, CancellationToken ct)
    {
        if (Blocked)
            return;

        var body = JsonSerializer.Serialize(chunk.Select(i => new { Text = items[i].Text }));
        var url = $"{Endpoint}?api-version=3.0&to={Uri.EscapeDataString(MapTarget(target))}";
        try
        {
            var token = await GetTokenAsync(null, ct);
            if (token == null)
                return;

            var (status, json) = await PostAsync(url, body, token, ct);
            if (status == HttpStatusCode.Unauthorized)
            {
                // The token was revoked or expired early: get a fresh one and try once more.
                token = await GetTokenAsync(token, ct);
                if (token == null)
                    return;
                (status, json) = await PostAsync(url, body, token, ct);
            }

            if (status != HttpStatusCode.OK)
            {
                _http.Log($"microsoft http {(int)status} for tgt={target}");
                if (status == HttpStatusCode.TooManyRequests)
                    TripBreaker();
                return;
            }

            using var doc = JsonDocument.Parse(json!);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != chunk.Count)
            {
                _http.Log($"microsoft returned an unexpected body for {chunk.Count} lines");
                return;
            }

            for (int k = 0; k < chunk.Count; k++)
                results[chunk[k]] = TextOf(root[k]);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the caller cancelled: not a backend failure
        }
        catch (Exception ex)
        {
            _http.Log($"microsoft exception: {ex.GetType().Name}: {ex.Message}");
            // HttpClient's own timeout surfaces as a TaskCanceledException.
            if (ex is OperationCanceledException)
                TripBreaker();
        }
    }

    /// <summary>translations[0].text of one element of the response; null if missing or empty.</summary>
    private static string? TextOf(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("translations", out var translations) ||
            translations.ValueKind != JsonValueKind.Array || translations.GetArrayLength() == 0 ||
            translations[0].ValueKind != JsonValueKind.Object ||
            !translations[0].TryGetProperty("text", out var text) ||
            text.ValueKind != JsonValueKind.String)
            return null;
        var s = text.GetString();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    private Task<(HttpStatusCode, string?)> PostAsync(string url, string body, string token, CancellationToken ct) =>
        _http.GatedAsync(async () =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await _http.Client.SendAsync(req, ct);
            var text = resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync(ct) : null;
            return (resp.StatusCode, text);
        }, ct);

    /// <summary>
    /// WinLens/Google codes to the ones Microsoft expects. Most are shared; the rest are a
    /// different spelling of the same language or a script the service wants spelled out.
    /// </summary>
    internal static string MapTarget(string code) => code switch
    {
        "zh" => "zh-Hans",
        "zh-TW" => "zh-Hant",
        "iw" => "he",
        "jw" => "jv",
        "tl" => "fil",
        "no" => "nb",
        "sr" => "sr-Cyrl",
        "mn" => "mn-Cyrl",
        "ku" => "kmr",
        "hmn" => "mww",
        "ny" => "nya",
        _ => code,
    };

    private bool Blocked => Environment.TickCount64 < Interlocked.Read(ref _blockedUntil);

    private void TripBreaker() =>
        Interlocked.Exchange(ref _blockedUntil, Environment.TickCount64 + CooldownMs);

    /// <summary>
    /// The cached token, fetching one when there is none or it is about to expire.
    /// <paramref name="rejected"/> is a token the service just refused: it is dropped.
    /// </summary>
    private async Task<string?> GetTokenAsync(string? rejected, CancellationToken ct)
    {
        Task<Token?> task;
        lock (_tokenLock)
        {
            var current = _tokenTask;
            bool usable = current != null &&
                (!current.IsCompleted ||
                 (current.IsCompletedSuccessfully && current.Result is { } t &&
                  t.Value != rejected && Environment.TickCount64 < t.ValidUntil));
            if (!usable)
                _tokenTask = FetchTokenAsync();
            task = _tokenTask!;
        }
        // The fetch is shared, so one caller cancelling must not cancel it for the rest.
        return (await task.WaitAsync(ct))?.Value;
    }

    private async Task<Token?> FetchTokenAsync()
    {
        try
        {
            using var resp = await _http.Client.GetAsync(AuthEndpoint);
            if (!resp.IsSuccessStatusCode)
            {
                _http.Log($"microsoft auth http {(int)resp.StatusCode}");
                if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                    TripBreaker();
                return null;
            }

            var jwt = (await resp.Content.ReadAsStringAsync()).Trim();
            if (jwt.Length == 0)
                return null;
            return new Token(jwt, Environment.TickCount64 + (long)LifeOf(jwt).TotalMilliseconds);
        }
        catch (Exception ex)
        {
            _http.Log($"microsoft auth exception: {ex.GetType().Name}: {ex.Message}");
            if (ex is OperationCanceledException)
                TripBreaker();
            return null;
        }
    }

    /// <summary>How long <paramref name="jwt"/> stays usable: until a minute before its exp claim.</summary>
    internal static TimeSpan LifeOf(string jwt, DateTimeOffset? now = null)
    {
        try
        {
            var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            var exp = DateTimeOffset.FromUnixTimeSeconds(doc.RootElement.GetProperty("exp").GetInt64());
            var life = exp - (now ?? DateTimeOffset.UtcNow) - TokenMargin;
            return life > TimeSpan.Zero ? life : TimeSpan.Zero;
        }
        catch
        {
            return DefaultTokenLife;
        }
    }
}
