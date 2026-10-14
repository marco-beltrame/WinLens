using System;
using System.Collections.Generic;

namespace WinLens.Services.Translation;

internal static class Batching
{
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
}
