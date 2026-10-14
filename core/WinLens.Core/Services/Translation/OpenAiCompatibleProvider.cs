using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WinLens.Models;

namespace WinLens.Services.Translation;

/// <summary>
/// A local (or any OpenAI-compatible) chat model: Ollama, LM Studio, llama.cpp, vLLM…
/// <para>
/// The lines of a batch go in as a JSON array and must come back as a JSON array of the
/// same length. A reply that can't be parsed or has the wrong length is retried line by
/// line, so a model that loses count never shifts translations onto the wrong lines.
/// </para>
/// </summary>
internal sealed class OpenAiCompatibleProvider : ITranslationProvider
{
    // Small batches: local models lose count on long arrays, and a short reply starts
    // showing sooner.
    private const int MaxBatchChars = 1500;
    private const int MaxBatchLines = 25;

    private readonly TranslationHttp _http;
    private readonly Uri? _base;
    private readonly string? _apiKey;
    private string? _model;

    public OpenAiCompatibleProvider(TranslationHttp http, string? url, string? apiKey, string? model)
    {
        _http = http;
        _base = ApiBase(url);
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        _model = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
    }

    public string Name => "Local model";

    /// <summary>
    /// Accepts what people paste: the server root ("http://localhost:11434"), the API root
    /// (".../v1") or the full chat URL (".../v1/chat/completions"). Returns the API root.
    /// </summary>
    internal static Uri? ApiBase(string? url)
    {
        var uri = Batching.BaseUri(url);
        if (uri == null) return null;
        var path = uri.AbsolutePath.TrimEnd('/');
        foreach (var suffix in new[] { "/chat/completions", "/completions", "/models" })
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                path = path[..^suffix.Length];
        if (path.Length == 0) path = "/v1";
        return new Uri(uri, path);
    }

    public async Task<string?[]> TranslateAsync(
        IReadOnlyList<TranslationRequest> items, string target, CancellationToken ct)
    {
        var results = new string?[items.Count];
        if (_base == null)
        {
            _http.Log("local model: no valid server URL");
            return results;
        }
        if (await ModelAsync(ct) is not string model)
            return results;

        var chunks = Batching.Chunk(Enumerable.Range(0, items.Count).ToArray(),
            i => items[i].Text.Length, MaxBatchChars, MaxBatchLines);
        await Task.WhenAll(chunks.Select(async chunk =>
        {
            var texts = chunk.Select(i => items[i].Text).ToArray();
            var (translated, reached) = await _http.GatedAsync(() => RequestAsync(model, texts, target, ct), ct);
            if (translated == null && reached && chunk.Count > 1)
            {
                _http.Log($"local model: unusable reply for {chunk.Count} lines; retrying one by one");
                translated = new string?[chunk.Count];
                for (int k = 0; k < chunk.Count; k++)
                {
                    var (one, _) = await _http.GatedAsync(() => RequestAsync(model, new[] { texts[k] }, target, ct), ct);
                    translated[k] = one?[0];
                }
            }
            if (translated == null) return;
            for (int k = 0; k < chunk.Count; k++)
                results[chunk[k]] = translated[k];
        }));
        return results;
    }

    /// <summary>Asks the model to translate one phrase, for the "Test connection" button.</summary>
    public async Task<EngineTestResult> TestAsync(string phrase, string target, CancellationToken ct)
    {
        if (_base == null)
            return new(false, "Enter the server address, for example http://localhost:11434 for Ollama.");
        var model = await ModelAsync(ct);
        if (model == null)
            return new(false, $"No model server answered at {_base}, or it has no models installed.");
        var (result, reached) = await RequestAsync(model, new[] { phrase }, target, ct);
        if (result?[0] is string sample)
            return new(true, "Connected.", sample, model);
        return new(false, reached
            ? $"The server answered but \"{model}\" didn't return a translation."
            : $"\"{model}\" didn't answer. Check the model name.", null, model);
    }

    /// <summary>The configured model, or the first one the server lists.</summary>
    private async Task<string?> ModelAsync(CancellationToken ct)
    {
        if (_model != null) return _model;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Batching.Append(_base!, "models"));
            Authorize(req);
            using var resp = await _http.Client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                foreach (var m in data.EnumerateArray())
                    if (m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                        return _model = id.GetString();
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _http.Log($"local model /models: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private void Authorize(HttpRequestMessage req)
    {
        if (_apiKey != null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
    }

    internal static string Prompt(string target)
    {
        var language = LanguageList.LabelFor(target);
        return
            $"You are a translation engine. Translate every string in the JSON array the user sends into {language}. " +
            "The strings are lines of text read from a screen, in order. " +
            "Reply with only a JSON array of strings: the same number of items, in the same order, one translation per item. " +
            "Keep numbers, names, URLs and code unchanged. If an item is already in the target language, copy it. " +
            "No explanations, no notes, no code fences.";
    }

    /// <returns>The translations (null on failure), and whether the server answered at all.</returns>
    private async Task<(string?[]? Result, bool Reached)> RequestAsync(
        string model, string[] texts, string target, CancellationToken ct)
    {
        var body = new
        {
            model,
            temperature = 0,
            stream = false,
            messages = new object[]
            {
                new { role = "system", content = Prompt(target) },
                new { role = "user", content = JsonSerializer.Serialize(texts) },
            },
        };

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Batching.Append(_base!, "chat/completions"))
            {
                Content = JsonContent.Create(body),
            };
            Authorize(req);
            using var resp = await _http.Client.SendAsync(req, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _http.Log($"local model http {(int)resp.StatusCode}: {json[..Math.Min(160, json.Length)]}");
                return (null, true);
            }

            using var doc = JsonDocument.Parse(json);
            var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            return (ParseReply(content, texts.Length), true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _http.Log($"local model exception: {ex.GetType().Name}: {ex.Message}");
            // A malformed body still means the server is up; anything else (refused,
            // timed out) means it isn't, and retrying line by line would only wait longer.
            return (null, ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException);
        }
    }

    private static readonly Regex ThinkBlock = new(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase);

    /// <summary>
    /// Pulls the JSON array out of a model reply: reasoning models prepend a &lt;think&gt;
    /// block and many wrap the answer in a code fence despite being told not to.
    /// </summary>
    internal static string?[]? ParseReply(string? content, int expected)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        var text = ThinkBlock.Replace(content, "").Trim();

        int start = text.IndexOf('['), end = text.LastIndexOf(']');
        if (start >= 0 && end > start)
        {
            try
            {
                var items = JsonSerializer.Deserialize<string?[]>(text[start..(end + 1)]);
                if (items != null && items.Length == expected)
                    return items.Select(s => string.IsNullOrWhiteSpace(s) ? null : s.Trim()).ToArray();
            }
            catch (JsonException) { /* fall through */ }
        }

        // A single line: small models often answer with just the translation.
        if (expected == 1)
        {
            var plain = text.Trim().Trim('`').Trim().Trim('"').Trim();
            return plain.Length == 0 || plain.Contains('\n') ? null : new string?[] { plain };
        }
        return null;
    }
}
