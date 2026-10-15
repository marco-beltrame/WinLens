using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WinLens.Models;
using WinLens.Services.Translation;

namespace WinLens.Services;

/// <summary>
/// Translates OCR lines. By default it tries Google gtx first (auto source detection, a
/// whole screen per request), then Microsoft (the keyless endpoint Edge uses, which works
/// where Google is blocked), then MyMemory for whatever is still untranslated; no account
/// or key is needed for any of them. <see cref="Configure"/> switches to a LibreTranslate
/// server or a local model instead. Logs each failure to %TEMP%\winlens.log.
/// </summary>
public sealed class TranslationService : IDisposable
{
    private const string TestPhrase = "Hello, how are you?";

    private readonly TranslationHttp _http;
    // Local models can take a while per batch and usually serve one request at a time.
    private readonly TranslationHttp _slowHttp;
    private volatile ITranslationProvider[] _providers;
    private readonly ConcurrentDictionary<(string text, string tgt), string> _cache = new();
    private const int MaxCacheEntries = 2000;

    public TranslationService() : this(null) { }

    internal TranslationService(HttpMessageHandler? handler, string? logPath = null)
    {
        _http = new TranslationHttp(handler, logPath);
        _slowHttp = new TranslationHttp(handler, logPath, TimeSpan.FromSeconds(120), maxConcurrent: 1);
        _providers = BuildChain(EngineOptions.Default);
    }

    /// <summary>Switches engine. Translations already running finish on the old one.</summary>
    public void Configure(EngineOptions options)
    {
        _providers = BuildChain(options);
        // Translations from another engine shouldn't answer for this one.
        _cache.Clear();
    }

    private ITranslationProvider[] BuildChain(EngineOptions options)
    {
        var google = new ITranslationProvider[] { new GoogleTranslateProvider(_http), new MicrosoftTranslatorProvider(_http), new MyMemoryProvider(_http) };
        ITranslationProvider? chosen = options.Engine switch
        {
            TranslationEngine.LibreTranslate => new LibreTranslateProvider(_http, options.Url, options.ApiKey),
            TranslationEngine.LocalLlm => new OpenAiCompatibleProvider(_slowHttp, options.Url, options.ApiKey, options.Model),
            _ => null,
        };
        if (chosen == null) return google;
        return options.FallbackToGoogle ? google.Prepend(chosen).ToArray() : new[] { chosen };
    }

    /// <summary>
    /// Translates a short phrase with <paramref name="options"/> (not the active engine),
    /// for the settings window's "Test connection" button.
    /// </summary>
    public async Task<EngineTestResult> TestAsync(EngineOptions options, string targetLang, CancellationToken ct = default)
    {
        var tgt = NormalizeTarget(targetLang);
        switch (options.Engine)
        {
            case TranslationEngine.LibreTranslate:
                return await new LibreTranslateProvider(_http, options.Url, options.ApiKey).TestAsync(TestPhrase, tgt, ct);
            case TranslationEngine.LocalLlm:
                return await new OpenAiCompatibleProvider(_slowHttp, options.Url, options.ApiKey, options.Model).TestAsync(TestPhrase, tgt, ct);
            default:
                var phrase = new[] { new TranslationRequest(TestPhrase, null) };
                var result = await new GoogleTranslateProvider(_http).TranslateAsync(phrase, tgt, ct);
                if (result[0] == null)
                    result = await new MicrosoftTranslatorProvider(_http).TranslateAsync(phrase, tgt, ct);
                return result[0] is string sample
                    ? new EngineTestResult(true, "Connected.", sample)
                    : new EngineTestResult(false, "No translation service answered. Check your connection.");
        }
    }

    /// <summary>Translates one line; the input comes back unchanged if every backend fails.</summary>
    public async Task<string> TranslateAsync(
        string text,
        string targetLang,
        string? sourceLang = null,
        CancellationToken ct = default)
    {
        var results = await TranslateManyAsync(new[] { new TranslationRequest(text, sourceLang) }, targetLang, ct);
        return results[0];
    }

    /// <summary>
    /// Translates a whole screen of lines at once, so it costs one or two requests instead
    /// of one per line. The result is aligned with <paramref name="items"/>; a line every
    /// backend failed on comes back unchanged.
    /// </summary>
    public async Task<string[]> TranslateManyAsync(
        IReadOnlyList<TranslationRequest> items,
        string targetLang,
        CancellationToken ct = default)
    {
        var tgt = NormalizeTarget(targetLang);
        var results = new string[items.Count];

        // Each distinct text is translated once; repeated lines share the answer.
        var pending = new List<TranslationRequest>();
        var slots = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int i = 0; i < items.Count; i++)
        {
            var text = items[i].Text;
            results[i] = text;
            if (string.IsNullOrWhiteSpace(text))
                continue;
            if (_cache.TryGetValue((text, tgt), out var cached))
            {
                results[i] = cached;
                continue;
            }
            if (!slots.TryGetValue(text, out var list))
            {
                slots[text] = list = new List<int>();
                pending.Add(items[i]);
            }
            list.Add(i);
        }

        foreach (var provider in _providers) // a snapshot: Configure swaps the array
        {
            if (pending.Count == 0)
                break;

            var translated = await provider.TranslateAsync(pending, tgt, ct);
            var failed = new List<TranslationRequest>();
            for (int k = 0; k < pending.Count; k++)
            {
                var text = pending[k].Text;
                if (translated[k] is not string done)
                {
                    failed.Add(pending[k]);
                    continue;
                }
                Store((text, tgt), done);
                foreach (var i in slots[text])
                    results[i] = done;
            }
            pending = failed;
        }

        return results;
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

    private void Store((string text, string tgt) key, string value)
    {
        // Crude bound: the cache only needs to help within a session; reset when huge.
        if (_cache.Count >= MaxCacheEntries)
            _cache.Clear();
        _cache[key] = value;
    }

    public void Dispose()
    {
        _http.Dispose();
        _slowHttp.Dispose();
    }
}
