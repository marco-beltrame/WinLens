using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WinLens.Services.Translation;

/// <summary>
/// MyMemory's free endpoint, the fallback when Google fails. One request per text (more
/// for a long paragraph): it has no batch form, no auto-detect (so the source is guessed)
/// and a low daily quota.
/// </summary>
internal sealed class MyMemoryProvider : ITranslationProvider
{
    private const string Endpoint = "https://api.mymemory.translated.net/get";

    // MyMemory rejects queries over 500 bytes of UTF-8 (in-body, with HTTP 200).
    internal const int MaxBytes = 500;

    private readonly TranslationHttp _http;

    public MyMemoryProvider(TranslationHttp http) => _http = http;

    public string Name => "MyMemory";

    public async Task<string?[]> TranslateAsync(
        IReadOnlyList<TranslationRequest> items, string target, CancellationToken ct)
    {
        var results = new string?[items.Count];
        await Task.WhenAll(items.Select(async (item, i) =>
        {
            var src = GuessSource(item.Text, item.SourceHint);
            results[i] = await _http.GatedAsync(() => TranslateOneAsync(item.Text, src, target, ct), ct);
        }));
        return results;
    }

    private async Task<string?> TranslateOneAsync(string text, string src, string tgt, CancellationToken ct)
    {
        // Nothing to translate; null so the caller returns the text without caching it.
        if (string.Equals(src, tgt, StringComparison.OrdinalIgnoreCase))
            return null;

        var pieces = Split(text, MaxBytes);
        if (pieces.Count == 1)
            return await QueryAsync(text, src, tgt, ct);

        // A paragraph too long for one query goes a few sentences at a time.
        var whole = new StringBuilder();
        foreach (var (piece, spaceBefore) in pieces)
        {
            if (await QueryAsync(piece, src, tgt, ct) is not { } part)
                return null;
            if (spaceBefore && whole.Length > 0) whole.Append(' ');
            whole.Append(part);
        }
        return whole.ToString();
    }

    /// <summary>
    /// Cuts <paramref name="text"/> into pieces of at most <paramref name="maxBytes"/> of
    /// UTF-8, at the end of a sentence where there is one, else of a clause, else between
    /// words. SpaceBefore says whether the piece was set off from the one before it.
    /// </summary>
    internal static List<(string Text, bool SpaceBefore)> Split(string text, int maxBytes)
    {
        var pieces = new List<(string, bool)>();
        int start = 0;
        bool spaceBefore = false;
        while (start < text.Length)
        {
            // The longest stretch that fits, never ending inside a surrogate pair.
            int end = start, bytes = 0;
            while (end < text.Length)
            {
                int length = char.IsHighSurrogate(text[end]) && end + 1 < text.Length ? 2 : 1;
                int size = Encoding.UTF8.GetByteCount(text.AsSpan(end, length));
                if (bytes + size > maxBytes && end > start) break;
                bytes += size;
                end += length;
            }

            if (end < text.Length)
            {
                // Not so early that a paragraph turns into a request per clause.
                int earliest = start + (end - start) / 3;
                int cut = LastBreak(text, earliest, end, ".!?…。！？");
                if (cut < 0) cut = LastBreak(text, earliest, end, ",;:，、；：");
                if (cut < 0) cut = LastBreak(text, earliest, end, null);
                if (cut > start) end = cut;
            }

            var piece = text.AsSpan(start, end - start).Trim();
            if (piece.Length > 0)
                pieces.Add((piece.ToString(), spaceBefore));
            spaceBefore = char.IsWhiteSpace(text[end - 1]) || (end < text.Length && char.IsWhiteSpace(text[end]));
            start = end;
        }
        return pieces;
    }

    /// <summary>
    /// The last place in [from, to) to cut after: one of <paramref name="marks"/> that ends
    /// a word (so not the stop in "3.5"), or any whitespace when marks is null. -1 if none.
    /// </summary>
    private static int LastBreak(string text, int from, int to, string? marks)
    {
        for (int i = to - 1; i >= from; i--)
        {
            char c = text[i];
            if (marks == null ? char.IsWhiteSpace(c)
                : marks.Contains(c) && (c > 0x2000 || i + 1 == text.Length || char.IsWhiteSpace(text[i + 1])))
                return i + 1;
        }
        return -1;
    }

    private async Task<string?> QueryAsync(string text, string src, string tgt, CancellationToken ct)
    {
        var url = $"{Endpoint}?q={Uri.EscapeDataString(text)}&langpair={src}|{tgt}";
        try
        {
            using var resp = await _http.Client.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _http.Log($"mymemory http {(int)resp.StatusCode} for {src}|{tgt}");
                return null;
            }
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, default, ct);
            var root = doc.RootElement;

            // Errors (quota, query too long) arrive as HTTP 200 with the status in the body,
            // as a number or a string, and the message in place of the translation.
            if (!root.TryGetProperty("responseStatus", out var status) || !IsStatusOk(status))
            {
                _http.Log($"mymemory in-body status {status.ToString()} for {src}|{tgt}");
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
                    _http.Log($"mymemory rejected message: {output}");
                    return null;
                }
                // MyMemory often hands back HTML entities ("L&#39;eau").
                return System.Net.WebUtility.HtmlDecode(output);
            }
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _http.Log($"mymemory exception: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static bool IsStatusOk(JsonElement status) => status.ValueKind switch
    {
        JsonValueKind.Number => status.TryGetInt32(out var n) && n == 200,
        JsonValueKind.String => int.TryParse(status.GetString(), out var n) && n == 200,
        _ => false,
    };

    /// <summary>
    /// Best guess at the source language, since MyMemory has no auto-detect and silently
    /// echoes the input back when told the wrong pair. Hard-coding "en" here used to make
    /// the fallback useless for exactly the case WinLens exists for — a page of Chinese
    /// sent as en→xx comes back unchanged, so a Google rate-limit looked to the user like
    /// "it stopped translating".
    /// <para>
    /// The script is read off the text itself, which is reliable for CJK: kana means
    /// Japanese, Hangul means Korean, otherwise Han means Chinese. For Latin text the
    /// script says nothing about the language, so we fall back to the OCR recognizer's
    /// tag (a weak signal — it is the installed pack, not the content) and finally "en".
    /// </para>
    /// </summary>
    internal static string GuessSource(string text, string? ocrTag)
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
}
