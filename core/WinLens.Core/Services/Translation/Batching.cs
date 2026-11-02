using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace WinLens.Services.Translation;

internal static class Batching
{
    /// <summary>
    /// True when a refused batch might still go through line by line (the server rejected
    /// the request's shape). Auth, missing model, rate limits and server errors would fail
    /// for every line too, so retrying each one only multiplies the wait and the load.
    /// </summary>
    public static bool IsWorthRetryingPerLine(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or
        HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or
        HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout => false,
        _ => true,
    };

    /// <summary>Splits item indexes into runs of at most <paramref name="maxChars"/> characters and <paramref name="maxItems"/> items.</summary>
    public static List<List<int>> Chunk(IReadOnlyList<int> indexes, Func<int, int> lengthOf, int maxChars, int maxItems)
    {
        var chunks = new List<List<int>>();
        var chunk = new List<int>();
        int chars = 0;
        foreach (var i in indexes)
        {
            int len = lengthOf(i) + 1;
            if (chunk.Count > 0 && (chars + len > maxChars || chunk.Count >= maxItems))
            {
                chunks.Add(chunk);
                chunk = new List<int>();
                chars = 0;
            }
            chunk.Add(i);
            chars += len;
        }
        if (chunk.Count > 0) chunks.Add(chunk);
        return chunks;
    }

    /// <summary>
    /// The server address the user typed, without a trailing slash, or null if it isn't an
    /// http(s) URL.
    /// </summary>
    public static Uri? BaseUri(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var text = url.Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
            text = "http://" + text; // "localhost:5000" is what people type
        if (!Uri.TryCreate(text.TrimEnd('/'), UriKind.Absolute, out var uri)) return null;
        return uri.Scheme is "http" or "https" ? uri : null;
    }

    /// <summary><paramref name="baseUri"/> with <paramref name="path"/> appended (keeps any sub-path the server lives under).</summary>
    public static Uri Append(Uri baseUri, string path) =>
        new(baseUri, baseUri.AbsolutePath.TrimEnd('/') + "/" + path.TrimStart('/'));

    /// <summary>
    /// Translates <paramref name="items"/> in chunks through <paramref name="request"/>, one
    /// gated call per chunk. A chunk the server answered but refused (wrong shape, lost
    /// lines) is retried line by line; an unreachable server is not, since every line would
    /// only wait for the same timeout. Missing results stay null.
    /// </summary>
    public static async Task<string?[]> TranslateInChunksAsync(
        TranslationHttp http,
        IReadOnlyList<TranslationRequest> items,
        int maxChars, int maxLines,
        Func<string[], Task<(string?[]? Result, bool Reached)>> request,
        Action<int>? onRetryLineByLine,
        CancellationToken ct)
    {
        var results = new string?[items.Count];
        var chunks = Chunk(Enumerable.Range(0, items.Count).ToArray(), i => items[i].Text.Length, maxChars, maxLines);
        await Task.WhenAll(chunks.Select(async chunk =>
        {
            var texts = chunk.Select(i => items[i].Text).ToArray();
            var (translated, reached) = await http.GatedAsync(() => request(texts), ct);
            if (translated == null && reached && chunk.Count > 1)
            {
                onRetryLineByLine?.Invoke(chunk.Count);
                translated = new string?[chunk.Count];
                for (int k = 0; k < chunk.Count; k++)
                {
                    var (one, _) = await http.GatedAsync(() => request(new[] { texts[k] }), ct);
                    translated[k] = one?[0];
                }
            }
            if (translated == null) return;
            for (int k = 0; k < chunk.Count; k++)
                results[chunk[k]] = translated[k];
        }));
        return results;
    }
}
