using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WinLens.Models;

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

    // A full-screen capture can produce dozens of blocks; firing them all at the free
    // endpoints at once invites 429s (which degrade silently to untranslated text).
    private readonly SemaphoreSlim _gate = new(4);
    private const int MaxCacheEntries = 2000;

    // Google answers 429 (or stalls) once it decides we are too chatty; retrying it for
    // every remaining block only adds a 10 s timeout per block before the fallback runs.
    // After a trip, Google is skipped until this tick count (Environment.TickCount64).
    private const long GoogleCooldownMs = 30_000;
    private long _googleBlockedUntil;

    // MyMemory rejects queries over 500 chars (in-body, with HTTP 200).
    private const int MyMemoryMaxChars = 500;

    // Encoded queries beyond this are sent as POST; long GET URLs get rejected by the server.
    private const int MaxGetQueryChars = 2000;

    public TranslationService() : this(null) { }

    internal TranslationService(HttpMessageHandler? handler, string? logPath = null)
    {
        _http = handler == null
            ? new HttpClient { Timeout = TimeSpan.FromSeconds(10) }
            : new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) WinLens/1.0");
        _logPath = logPath ?? Path.Combine(Path.GetTempPath(), "winlens.log");
    }

    public async Task<string> TranslateAsync(
        string text,
        string targetLang,
        string? sourceLang = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var tgt = NormalizeTarget(targetLang);
        var key = (text, tgt);
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        await _gate.WaitAsync(ct);
        try
        {
            // Re-check: an identical block may have been translated while we waited.
            if (_cache.TryGetValue(key, out cached))
                return cached;

            var google = await TryGoogleAsync(text, tgt, ct);
            if (google != null)
            {
                Store(key, google);
                return google;
            }

            // MyMemory fallback — it has no auto-detect, so we must name the source.
            var memory = await TryMyMemoryAsync(text, GuessSource(text, sourceLang), tgt, ct);
            if (memory != null)
            {
                Store(key, memory);
                return memory;
            }

            return text;
        }
        finally
        {
            // Dispose() may have run while this request was in flight.
            try { _gate.Release(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// Codes offered in <see cref="LanguageList"/> are passed through as-is — some carry a
    /// region subtag the backend needs (e.g. "zh-TW" for Traditional Chinese; stripping it
    /// would silently return Simplified). Anything else falls back to the base tag.
    /// </summary>
    private static string NormalizeTarget(string targetLang)
    {
        foreach (var (code, _) in LanguageList.Items)
            if (string.Equals(code, targetLang, StringComparison.OrdinalIgnoreCase))
                return code;
        return targetLang.Split('-')[0];
    }

    /// <summary>
    /// Best guess at the source language for the MyMemory fallback, which has no
    /// auto-detect and silently echoes the input back when told the wrong pair.
    /// Hard-coding "en" here used to make the fallback useless for exactly the case
    /// WinLens exists for — a page of Chinese sent as en→xx comes back unchanged, so
    /// a Google rate-limit looked to the user like "it stopped translating".
    /// <para>
    /// The script is read off the text itself, which is reliable for CJK: kana means
    /// Japanese, Hangul means Korean, otherwise Han means Chinese. For Latin text the
    /// script says nothing about the language, so we fall back to the OCR recognizer's
    /// tag (a weak signal — it is the installed pack, not the content) and finally "en".
    /// </para>
    /// </summary>
    private static string GuessSource(string text, string? ocrTag)
    {
        int han = 0, kana = 0, hangul = 0, latin = 0;
        foreach (var c in text)
        {
            if (c >= 0x3040 && c <= 0x30FF) kana++;
            else if (c >= 0xAC00 && c <= 0xD7AF) hangul++;
            else if ((c >= 0x4E00 && c <= 0x9FFF) || (c >= 0x3400 && c <= 0x4DBF)) han++;
            else if (char.IsLetter(c)) latin++;
        }

        // Kana and Hangul are decisive even in small amounts: Japanese mixes kana into
        // Han text, so requiring a majority would misread most Japanese as Chinese.
        if (kana > 0) return "ja";
        if (hangul > 0) return "ko";
        if (han > latin) return "zh";

        if (!string.IsNullOrWhiteSpace(ocrTag))
        {
            var baseTag = ocrTag.Split('-')[0];
            // The text is Latin here, so a CJK recognizer tag is simply the wrong
            // answer (it happens: the CJK engine also returns the Latin lines it sees).
            bool tagIsCjk = baseTag is "zh" or "ja" or "ko";
            if (!tagIsCjk && baseTag.Length is 2 or 3) return baseTag;
        }
        return "en";
    }

    private void Store((string text, string tgt) key, string value)
    {
        // Crude bound: the cache only needs to help within a session; reset when huge.
        if (_cache.Count >= MaxCacheEntries)
            _cache.Clear();
        _cache[key] = value;
    }

    private bool GoogleBlocked => Environment.TickCount64 < Interlocked.Read(ref _googleBlockedUntil);

    private void TripGoogleBreaker() =>
        Interlocked.Exchange(ref _googleBlockedUntil, Environment.TickCount64 + GoogleCooldownMs);

    private async Task<string?> TryGoogleAsync(string text, string tgt, CancellationToken ct)
    {
        if (GoogleBlocked)
            return null;

        var query = Uri.EscapeDataString(text);
        var baseUrl = $"{GoogleEndpoint}?client=gtx&sl=auto&tl={tgt}&dt=t";
        try
        {
            HttpResponseMessage resp;
            if (query.Length > MaxGetQueryChars)
            {
                using var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("q", text) });
                resp = await _http.PostAsync(baseUrl, content, ct);
            }
            else
            {
                resp = await _http.GetAsync($"{baseUrl}&q={query}", ct);
            }

            using (resp)
            {
                if (!resp.IsSuccessStatusCode)
                {
                    Log($"google http {(int)resp.StatusCode} for tgt={tgt}");
                    if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                        TripGoogleBreaker();
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
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the caller cancelled: not a backend failure
        }
        catch (Exception ex)
        {
            Log($"google exception: {ex.GetType().Name}: {ex.Message}");
            // HttpClient's own timeout surfaces as a TaskCanceledException.
            if (ex is OperationCanceledException)
                TripGoogleBreaker();
            return null;
        }
    }

    private async Task<string?> TryMyMemoryAsync(string text, string src, string tgt, CancellationToken ct)
    {
        // Nothing to translate; return null so the caller returns the text without caching it.
        if (string.Equals(src, tgt, StringComparison.OrdinalIgnoreCase))
            return null;

        if (text.Length > MyMemoryMaxChars)
        {
            Log($"mymemory skipped: text is {text.Length} chars (max {MyMemoryMaxChars})");
            return null;
        }

        var url = $"{MyMemoryEndpoint}?q={Uri.EscapeDataString(text)}&langpair={src}|{tgt}";
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
            var root = doc.RootElement;

            // Errors (quota, query too long) arrive as HTTP 200 with the status in the body,
            // as a number or a string, and the message in place of the translation.
            if (!root.TryGetProperty("responseStatus", out var status) || !IsStatusOk(status))
            {
                Log($"mymemory in-body status {status.ToString()} for {src}|{tgt}");
                return null;
            }

            if (root.TryGetProperty("responseData", out var data) &&
                data.ValueKind == JsonValueKind.Object &&
                data.TryGetProperty("translatedText", out var translated) &&
                translated.ValueKind == JsonValueKind.String)
            {
                var output = translated.GetString();
                if (string.IsNullOrEmpty(output) ||
                    output.StartsWith("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase) ||
                    output.StartsWith("QUERY LENGTH LIMIT", StringComparison.OrdinalIgnoreCase))
                {
                    Log($"mymemory rejected message: {output}");
                    return null;
                }
                return output;
            }
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log($"mymemory exception: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static bool IsStatusOk(JsonElement status) => status.ValueKind switch
    {
        JsonValueKind.Number => status.TryGetInt32(out var n) && n == 200,
        JsonValueKind.String => int.TryParse(status.GetString(), out var n) && n == 200,
        _ => false,
    };

    // Bound the log the same way App does for crash.log. A backend that is down or
    // rate-limiting writes one line per text block per translate, so an unbounded file
    // grows fast and unattended.
    private const long MaxLogBytes = 256 * 1024;
    private readonly object _logLock = new();
    private long _logBytes = -1; // -1 = size not read from disk yet

    private void Log(string line)
    {
        try
        {
            var entry = $"[{DateTime.Now:HH:mm:ss.fff}] {line}{Environment.NewLine}";

            // Up to _gate translations run at once and each may log; AppendAllText opens
            // the file exclusively, so without this they would trip over each other.
            lock (_logLock)
            {
                if (_logBytes < 0)
                {
                    var info = new FileInfo(_logPath);
                    _logBytes = info.Exists ? info.Length : 0;
                }

                if (_logBytes + entry.Length > MaxLogBytes)
                {
                    // Start fresh rather than grow forever. Tracking the size in memory
                    // keeps this to one stat() per process instead of one per line.
                    File.Delete(_logPath);
                    _logBytes = 0;
                }

                File.AppendAllText(_logPath, entry);
                _logBytes += entry.Length;
            }
        }
        catch { /* logging must never throw */ }
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
