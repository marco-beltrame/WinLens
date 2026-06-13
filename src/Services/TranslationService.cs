using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using WinLens.Models;

namespace WinLens.Services;

/// <summary>
/// Translates short strings. By default it uses the free built-in engines
/// (Google gtx with a MyMemory fallback). When the user opts into an
/// OpenAI-compatible API in settings, that is tried first and the built-in
/// engines act as the fallback. Logs each failure to %TEMP%\winlens.log.
/// </summary>
public sealed class TranslationService : IDisposable
{
    private const string GoogleEndpoint   = "https://translate.googleapis.com/translate_a/single";
    private const string MyMemoryEndpoint = "https://api.mymemory.translated.net/get";

    private readonly HttpClient _http;
    private readonly HttpClient _llmHttp;
    private readonly ConcurrentDictionary<(string text, string tgt), string> _cache = new();
    private readonly string _logPath;
    private UserSettings? _config;

    public TranslationService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) WinLens/1.0");
        // LLM calls reason over the text and can take longer than the web engines.
        _llmHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _logPath = Path.Combine(Path.GetTempPath(), "winlens.log");
    }

    /// <summary>
    /// Supplies the live settings object (shared with the UI, so edits there
    /// apply immediately). Until called, only the built-in engines are used.
    /// </summary>
    public void Configure(UserSettings settings) => _config = settings;

    private bool UseLlm =>
        _config is { TranslationProvider: TranslationProvider.OpenAiCompatible } c &&
        !string.IsNullOrWhiteSpace(c.LlmApiKey) &&
        !string.IsNullOrWhiteSpace(c.LlmBaseUrl) &&
        !string.IsNullOrWhiteSpace(c.LlmModel);

    public async Task<string> TranslateAsync(
        string text,
        string targetLang,
        string? sourceLang = null,
        CancellationToken ct = default)
    {
        // sourceLang from the OCR engine is the engine's profile language, not
        // the actual content language. Ignore it — let the engine auto-detect.
        _ = sourceLang;

        if (string.IsNullOrWhiteSpace(text))
            return text;

        var tgt = targetLang.Split('-')[0];
        var key = (text, tgt);
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        // Opt-in LLM engine first; on any failure fall through to the built-in
        // engines so a misconfigured key never leaves text untranslated.
        if (UseLlm)
        {
            var llm = await TryLlmAsync(text, tgt, ct);
            if (llm != null)
            {
                _cache[key] = llm;
                return llm;
            }
        }

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

    private async Task<string?> TryLlmAsync(string text, string tgt, CancellationToken ct)
    {
        var cfg = _config!;
        var url = cfg.LlmBaseUrl.TrimEnd('/') + "/chat/completions";
        var payload = new Dictionary<string, object>
        {
            ["model"] = cfg.LlmModel,
            ["temperature"] = 0.1,
            ["messages"] = new object[]
            {
                new
                {
                    role = "system",
                    content =
                        "You are a translation engine. The user sends a single short string captured by OCR; " +
                        "it may contain small OCR errors (l/I confusion, missing spaces) — infer the intended text. " +
                        $"Translate it into the language whose BCP-47 code is \"{tgt}\". " +
                        "Reply with ONLY the translation: no quotes, no explanations, no markdown. " +
                        "Keep numbers, file names and proper nouns unchanged.",
                },
                new { role = "user", content = text },
            },
        };
        // DeepSeek's reasoning models default to "thinking" mode, which burns
        // tokens and latency a short translation doesn't need. The flag is
        // DeepSeek-specific, so only send it to that endpoint.
        if (url.Contains("deepseek", StringComparison.OrdinalIgnoreCase))
            payload["thinking"] = new { type = "disabled" };

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + cfg.LlmApiKey.Trim());
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var resp = await _llmHttp.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                Log($"llm http {(int)resp.StatusCode} for tgt={tgt}");
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();
            return string.IsNullOrWhiteSpace(content) ? null : content.Trim();
        }
        catch (Exception ex)
        {
            Log($"llm exception: {ex.GetType().Name}: {ex.Message}");
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

    public void Dispose()
    {
        _http.Dispose();
        _llmHttp.Dispose();
    }
}
