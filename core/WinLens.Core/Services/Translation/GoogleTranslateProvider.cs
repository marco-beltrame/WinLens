using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WinLens.Services.Translation;

/// <summary>
/// Google's free "gtx" endpoint: no account or key, automatic source detection.
/// <para>
/// Lines are sent in batches, one per line of the request: Google translates each line of
/// a multi-line query as its own segment and keeps the line breaks, so one request covers
/// a whole screen instead of one request per line (which is what got us rate-limited).
/// Every batch is checked, and any doubt falls back to translating lines one by one.
/// </para>
/// </summary>
internal sealed class GoogleTranslateProvider : ITranslationProvider
{
    private const string Endpoint = "https://translate.googleapis.com/translate_a/single";

    // Encoded queries beyond this are sent as POST; long GET URLs get rejected by the server.
    private const int MaxGetQueryChars = 2000;

    // The endpoint refuses queries much over 5000 characters.
    internal const int MaxBatchChars = 4000;
    internal const int MaxBatchLines = 100;

    // Google answers 429 (or stalls) once it decides we are too chatty; retrying it for
    // every remaining request only adds a 10 s timeout each before the fallback runs.
    // After a trip, Google is skipped until this tick count (Environment.TickCount64).
    private const long CooldownMs = 30_000;
    private long _blockedUntil;

    private readonly TranslationHttp _http;

    public GoogleTranslateProvider(TranslationHttp http) => _http = http;

    public string Name => "Google";

    public async Task<string?[]> TranslateAsync(
        IReadOnlyList<TranslationRequest> items, string target, CancellationToken ct)
    {
        var results = new string?[items.Count];

        // A line with its own line break can't share a request: the split would be ambiguous.
        var singles = new List<int>();
        var batchable = new List<int>();
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].Text.AsSpan().IndexOfAny('\r', '\n') >= 0) singles.Add(i);
            else batchable.Add(i);
        }

        var work = Batching.Chunk(batchable, i => items[i].Text.Length, MaxBatchChars, MaxBatchLines)
            .Select(chunk => chunk.Count == 1
                ? TranslateOneAsync(items, chunk[0], target, results, ct)
                : TranslateBatchAsync(items, chunk, target, results, ct))
            .Concat(singles.Select(i => TranslateOneAsync(items, i, target, results, ct)))
            .ToArray();
        await Task.WhenAll(work);
        return results;
    }

    private async Task TranslateOneAsync(
        IReadOnlyList<TranslationRequest> items, int i, string target, string?[] results, CancellationToken ct)
    {
        results[i] = await RequestAsync(items[i].Text, target, ct);
    }

    private async Task TranslateBatchAsync(
        IReadOnlyList<TranslationRequest> items, List<int> chunk, string target, string?[] results, CancellationToken ct)
    {
        var query = string.Join('\n', chunk.Select(i => items[i].Text));
        var translated = await RequestAsync(query, target, ct);
        if (translated == null)
            return; // Google is failing; the next provider takes these lines

        var lines = translated.Split('\n');
        if (lines.Length != chunk.Count)
        {
            // Lines were merged or split: we can't tell which translation belongs where.
            _http.Log($"google batch returned {lines.Length} lines for {chunk.Count}; retrying one by one");
            await Task.WhenAll(chunk.Select(i => TranslateOneAsync(items, i, target, results, ct)));
            return;
        }

        var unchanged = new List<int>();
        for (int k = 0; k < chunk.Count; k++)
        {
            int i = chunk[k];
            var line = lines[k].Trim();
            results[i] = line.Length == 0 ? null : line;
            if (results[i] == null || (IsSame(items[i].Text, line) && HasLetter(line)))
                unchanged.Add(i);
        }

        // The source language is detected once per request. A line in a different language
        // from the rest (an English menu over a German page) can come back untouched, where
        // on its own it would have been translated: give those lines their own request.
        if (unchanged.Count > 0 && unchanged.Count < chunk.Count)
        {
            await Task.WhenAll(unchanged.Select(async i =>
            {
                var alone = await RequestAsync(items[i].Text, target, ct);
                if (alone != null) results[i] = alone;
            }));
        }
    }

    /// <summary>
    /// True when <paramref name="b"/> is <paramref name="a"/> handed back rather than
    /// translated. Google sometimes returns an untranslated line with its accents, spacing
    /// or punctuation touched up, so only letters and digits are compared, without accents.
    /// </summary>
    internal static bool IsSame(string a, string b) =>
        string.Equals(Skeleton(a), Skeleton(b), StringComparison.Ordinal);

    private static string Skeleton(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    private static bool HasLetter(string s)
    {
        foreach (var c in s)
            if (char.IsLetter(c)) return true;
        return false;
    }

    private bool Blocked => Environment.TickCount64 < Interlocked.Read(ref _blockedUntil);

    private void TripBreaker() =>
        Interlocked.Exchange(ref _blockedUntil, Environment.TickCount64 + CooldownMs);

    /// <summary>One request to the endpoint; null on any failure.</summary>
    private Task<string?> RequestAsync(string text, string tgt, CancellationToken ct) =>
        _http.GatedAsync(() => SendAsync(text, tgt, ct), ct);

    private async Task<string?> SendAsync(string text, string tgt, CancellationToken ct)
    {
        if (Blocked)
            return null;

        var query = Uri.EscapeDataString(text);
        var baseUrl = $"{Endpoint}?client=gtx&sl=auto&tl={tgt}&dt=t";
        try
        {
            HttpResponseMessage resp;
            if (query.Length > MaxGetQueryChars)
            {
                using var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("q", text) });
                resp = await _http.Client.PostAsync(baseUrl, content, ct);
            }
            else
            {
                resp = await _http.Client.GetAsync($"{baseUrl}&q={query}", ct);
            }

            using (resp)
            {
                if (!resp.IsSuccessStatusCode)
                {
                    _http.Log($"google http {(int)resp.StatusCode} for tgt={tgt}");
                    if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                        TripBreaker();
                    return null;
                }

                var body = await resp.Content.ReadAsStringAsync(ct);
                if (!body.StartsWith('['))
                {
                    _http.Log($"google non-json body (first 80 chars): {body.AsSpan(0, Math.Min(80, body.Length)).ToString()}");
                    return null;
                }

                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
                    return null;

                var sentences = root[0];
                if (sentences.ValueKind != JsonValueKind.Array)
                    return null;

                var sb = new StringBuilder();
                foreach (var s in sentences.EnumerateArray())
                {
                    if (s.ValueKind == JsonValueKind.Array && s.GetArrayLength() > 0 &&
                        s[0].ValueKind == JsonValueKind.String)
                    {
                        sb.Append(s[0].GetString());
                    }
                }
                var result = sb.ToString();
                return string.IsNullOrEmpty(result) ? null : result;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the caller cancelled: not a backend failure
        }
        catch (Exception ex)
        {
            _http.Log($"google exception: {ex.GetType().Name}: {ex.Message}");
            // HttpClient's own timeout surfaces as a TaskCanceledException.
            if (ex is OperationCanceledException)
                TripBreaker();
            return null;
        }
    }
}
