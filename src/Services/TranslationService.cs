using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace WinLens.Services;

/// <summary>
/// Translates short strings. Tries Google gtx first (auto source detection),
/// falls back to MyMemory. Logs each failure to %TEMP%\winlens.log.
/// </summary>
public sealed class TranslationService : IDisposable
{
    private const string GoogleEndpoint   = "https://translate.googleapis.com/translate_a/single";
    private const string MyMemoryEndpoint = "https://api.mymemory.translated.net/get";

    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<(string text, string tgt), string> _cache = new();
    private readonly string _logPath;

    public TranslationService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) WinLens/1.0");
        _logPath = Path.Combine(Path.GetTempPath(), "winlens.log");
    }

    public async Task<string> TranslateAsync(
        string text,
        string targetLang,
        string? sourceLang = null,
        CancellationToken ct = default)
    {
        // sourceLang from the OCR engine is the engine's profile language, not
        // the actual content language. Ignore it — let Google auto-detect.
        _ = sourceLang;

        if (string.IsNullOrWhiteSpace(text))
            return text;

        var tgt = targetLang.Split('-')[0];
        var key = (text, tgt);
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        var google = await TryGoogleAsync(text, tgt, ct);
        if (google != null)
        {
            _cache[key] = google;
            return google;
        }

        // MyMemory fallback — has no auto-detect, default source to English.
        var memory = await TryMyMemoryAsync(text, "en", tgt, ct);
        if (memory != null)
        {
            _cache[key] = memory;
            return memory;
        }

        return text;
    }

    private async Task<string?> TryGoogleAsync(string text, string tgt, CancellationToken ct)
    {
        var url = $"{GoogleEndpoint}?client=gtx&sl=auto&tl={tgt}&dt=t&q={HttpUtility.UrlEncode(text)}";
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                Log($"google http {(int)resp.StatusCode} for tgt={tgt}");
                return null;
            }

            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!body.StartsWith('['))
            {
                Log($"google non-json body (first 80 chars): {body.AsSpan(0, Math.Min(80, body.Length)).ToString()}");
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
        catch (Exception ex)
        {
            Log($"google exception: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private async Task<string?> TryMyMemoryAsync(string text, string src, string tgt, CancellationToken ct)
    {
        if (string.Equals(src, tgt, StringComparison.OrdinalIgnoreCase))
            return text;

        var url = $"{MyMemoryEndpoint}?q={HttpUtility.UrlEncode(text)}&langpair={src}|{tgt}";
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                Log($"mymemory http {(int)resp.StatusCode} for {src}|{tgt}");
                return null;
            }
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, default, ct);
            if (doc.RootElement.TryGetProperty("responseData", out var data) &&
                data.TryGetProperty("translatedText", out var translated))
            {
                var output = translated.GetString();
                return string.IsNullOrEmpty(output) ? null : output;
            }
            return null;
        }
        catch (Exception ex)
        {
            Log($"mymemory exception: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private void Log(string line)
    {
        try
        {
            File.AppendAllText(_logPath,
                $"[{DateTime.Now:HH:mm:ss.fff}] {line}{Environment.NewLine}");
        }
        catch { /* logging must never throw */ }
    }

    public void Dispose() => _http.Dispose();
}
