using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WinLens.Services.Translation;

/// <summary>
/// A LibreTranslate server: POST /translate with every line of a batch in <c>q</c>.
/// </summary>
internal sealed class LibreTranslateProvider : ITranslationProvider
{
    private const int MaxBatchChars = 4000;
    private const int MaxBatchLines = 100;

    private readonly TranslationHttp _http;
    private readonly Uri? _base;
    private readonly string? _apiKey;

    // Codes the server offers, read once from /languages to map our codes onto its own
    // (Chinese is "zh-Hans"/"zh-Hant" on current servers, "zh"/"zt" on older ones).
    private HashSet<string>? _serverCodes;

    public LibreTranslateProvider(TranslationHttp http, string? url, string? apiKey)
    {
        _http = http;
        _base = Batching.BaseUri(url);
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
    }

    public string Name => "LibreTranslate";

    public async Task<string?[]> TranslateAsync(
        IReadOnlyList<TranslationRequest> items, string target, CancellationToken ct)
    {
        var results = new string?[items.Count];
        if (_base == null)
        {
            _http.Log("libretranslate: no valid server URL");
            return results;
        }

        var tgt = await ServerCodeAsync(target, ct);
        if (tgt == null)
            return results;

        var chunks = Batching.Chunk(Enumerable.Range(0, items.Count).ToArray(),
            i => items[i].Text.Length, MaxBatchChars, MaxBatchLines);
        await Task.WhenAll(chunks.Select(async chunk =>
        {
            var texts = chunk.Select(i => items[i].Text).ToArray();
            var (translated, reached) = await _http.GatedAsync(() => RequestAsync(texts, tgt, ct), ct);
            if (translated == null && reached && chunk.Count > 1)
            {
                // The server answered but refused the batch (older servers take a single
                // string only): ask line by line. An unreachable server is not retried.
                translated = new string?[chunk.Count];
                for (int k = 0; k < chunk.Count; k++)
                {
                    var (one, _) = await _http.GatedAsync(() => RequestAsync(new[] { texts[k] }, tgt, ct), ct);
                    translated[k] = one?[0];
                }
            }
            if (translated == null) return;
            for (int k = 0; k < chunk.Count; k++)
                results[chunk[k]] = translated[k];
        }));
        return results;
    }

    /// <summary>Asks the server to translate one phrase, for the "Test connection" button.</summary>
    public async Task<EngineTestResult> TestAsync(string phrase, string target, CancellationToken ct)
    {
        if (_base == null)
            return new(false, "Enter the server address, for example http://localhost:5000.");
        var codes = await LoadServerCodesAsync(ct);
        if (codes == null)
            return new(false, $"No LibreTranslate server answered at {_base}.");
        var tgt = MapCode(target, codes);
        if (tgt == null)
            return new(false, "This server can't translate into the selected language.");
        var (result, _) = await RequestAsync(new[] { phrase }, tgt, ct);
        return result?[0] is string sample
            ? new(true, "Connected.", sample)
            : new(false, "The server answered but didn't translate. Check the API key.");
    }

    private async Task<string?> ServerCodeAsync(string target, CancellationToken ct)
    {
        var codes = await LoadServerCodesAsync(ct);
        if (codes == null)
            return target; // /languages unavailable: try our own code and let the server judge
        var mapped = MapCode(target, codes);
        if (mapped == null)
            _http.Log($"libretranslate: server has no target '{target}'");
        return mapped;
    }

    internal static string? MapCode(string target, IReadOnlySet<string> codes)
    {
        IEnumerable<string> candidates = target.ToLowerInvariant() switch
        {
            "zh" or "zh-cn" => new[] { "zh-Hans", "zh" },
            "zh-tw" => new[] { "zh-Hant", "zt" },
            "iw" or "he" => new[] { "he", "iw" },
            _ => new[] { target, target.Split('-')[0] },
        };
        foreach (var c in candidates)
            foreach (var code in codes)
                if (string.Equals(code, c, StringComparison.OrdinalIgnoreCase))
                    return code;
        return null;
    }

    private async Task<HashSet<string>?> LoadServerCodesAsync(CancellationToken ct)
    {
        if (_serverCodes != null) return _serverCodes;
        try
        {
            using var resp = await _http.Client.GetAsync(Batching.Append(_base!, "languages"), ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                foreach (var lang in doc.RootElement.EnumerateArray())
                    if (lang.ValueKind == JsonValueKind.Object &&
                        lang.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String)
                        codes.Add(code.GetString()!);
            return codes.Count == 0 ? null : _serverCodes = codes;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _http.Log($"libretranslate /languages: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <returns>The translations (null on failure), and whether the server answered at all.</returns>
    private async Task<(string?[]? Result, bool Reached)> RequestAsync(string[] texts, string tgt, CancellationToken ct)
    {
        var body = new Dictionary<string, object>
        {
            ["q"] = texts.Length == 1 ? texts[0] : texts,
            ["source"] = "auto",
            ["target"] = tgt,
            ["format"] = "text",
        };
        if (_apiKey != null) body["api_key"] = _apiKey;

        try
        {
            using var resp = await _http.Client.PostAsJsonAsync(
                Batching.Append(_base!, "translate"), body, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _http.Log($"libretranslate http {(int)resp.StatusCode}: {json[..Math.Min(120, json.Length)]}");
                return (null, true);
            }

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("translatedText", out var t)) return (null, true);
            if (t.ValueKind == JsonValueKind.String && texts.Length == 1)
                return (new[] { NullIfEmpty(t.GetString()) }, true);
            if (t.ValueKind == JsonValueKind.Array && t.GetArrayLength() == texts.Length)
                return (t.EnumerateArray()
                    .Select(e => e.ValueKind == JsonValueKind.String ? NullIfEmpty(e.GetString()) : null)
                    .ToArray(), true);
            return (null, true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _http.Log($"libretranslate exception: {ex.GetType().Name}: {ex.Message}");
            return (null, false);
        }
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
