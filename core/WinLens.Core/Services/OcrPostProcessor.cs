using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace WinLens.Services;

/// <summary>Platform-neutral rectangle (source-image pixels) used by the OCR post-processing.</summary>
public readonly record struct OcrRect(double X, double Y, double Width, double Height);

/// <summary>One recognized line before it is turned into an app-level block.</summary>
public sealed record OcrLine(string Text, OcrRect Rect, string? Language);

/// <summary>Writing system a recognizer reads. One recognizer per script is enough.</summary>
public enum OcrScript
{
    Latin, Cyrillic, Greek, Arabic, Hebrew, Devanagari, Thai,
    Chinese, Japanese, Korean,
}

/// <summary>
/// Pure OCR post-processing (script bucketing, fragment merge, cross-engine dedup),
/// kept free of WinRT/WPF so it can be unit-tested on any OS.
/// </summary>
public static class OcrPostProcessor
{
    public static readonly int ScriptCount = Enum.GetValues<OcrScript>().Length;

    /// <summary>Maps a recognizer BCP-47 tag to the script it reads; unknown tags are Latin.</summary>
    public static OcrScript ScriptOfTag(string? tag)
    {
        if (string.IsNullOrEmpty(tag)) return OcrScript.Latin;

        var parts = tag.Split('-', '_');
        var lang = parts[0].ToLowerInvariant();

        // An explicit script subtag (sr-Cyrl-RS, zh-Hant-TW, ...) beats the language.
        foreach (var p in parts.Skip(1))
        {
            if (p.Length != 4) continue;
            switch (p.ToLowerInvariant())
            {
                case "latn": return OcrScript.Latin;
                case "cyrl": return OcrScript.Cyrillic;
                case "grek": return OcrScript.Greek;
                case "arab": return OcrScript.Arabic;
                case "hebr": return OcrScript.Hebrew;
                case "deva": return OcrScript.Devanagari;
                case "thai": return OcrScript.Thai;
                case "hans": case "hant": case "hani": return OcrScript.Chinese;
                case "jpan": return OcrScript.Japanese;
                case "kore": case "hang": return OcrScript.Korean;
            }
        }

        return lang switch
        {
            "zh" => OcrScript.Chinese,
            "ja" => OcrScript.Japanese,
            "ko" => OcrScript.Korean,
            "ru" or "uk" or "bg" or "be" or "mk" or "kk" or "ky" or "tg" or "mn" => OcrScript.Cyrillic,
            "el" => OcrScript.Greek,
            "ar" or "fa" or "ur" or "ps" or "ug" or "sd" => OcrScript.Arabic,
            "he" or "yi" => OcrScript.Hebrew,
            "hi" or "mr" or "ne" or "sa" => OcrScript.Devanagari,
            "th" => OcrScript.Thai,
            _ => OcrScript.Latin,
        };
    }

    // Per-character writing system.
    private enum Glyph { Latin, Cyrillic, Greek, Arabic, Hebrew, Devanagari, Thai, Han, Kana, Hangul }

    internal static bool IsCjk(char c) =>
        (c >= 0x4E00 && c <= 0x9FFF) ||  // CJK Unified Ideographs
        (c >= 0x3400 && c <= 0x4DBF) ||  // Extension A
        (c >= 0x3040 && c <= 0x30FF) ||  // Hiragana + Katakana
        (c >= 0xAC00 && c <= 0xD7AF);    // Hangul syllables

    private static Glyph GlyphOf(char c)
    {
        if (c >= 0x4E00 && c <= 0x9FFF || c >= 0x3400 && c <= 0x4DBF) return Glyph.Han;
        if (c >= 0x3040 && c <= 0x30FF || c >= 0x31F0 && c <= 0x31FF) return Glyph.Kana;
        if (c >= 0xAC00 && c <= 0xD7AF || c >= 0x1100 && c <= 0x11FF || c >= 0x3130 && c <= 0x318F) return Glyph.Hangul;
        if (c >= 0x0400 && c <= 0x052F || c >= 0x1C80 && c <= 0x1C8F || c >= 0x2DE0 && c <= 0x2DFF || c >= 0xA640 && c <= 0xA69F) return Glyph.Cyrillic;
        if (c >= 0x0370 && c <= 0x03FF || c >= 0x1F00 && c <= 0x1FFF) return Glyph.Greek;
        if (c >= 0x0590 && c <= 0x05FF) return Glyph.Hebrew;
        if (c >= 0x0600 && c <= 0x06FF || c >= 0x0750 && c <= 0x077F || c >= 0x08A0 && c <= 0x08FF ||
            c >= 0xFB50 && c <= 0xFDFF || c >= 0xFE70 && c <= 0xFEFF) return Glyph.Arabic;
        if (c >= 0x0900 && c <= 0x097F) return Glyph.Devanagari;
        if (c >= 0x0E00 && c <= 0x0E7F) return Glyph.Thai;
        return Glyph.Latin;
    }

    private static bool Owns(OcrScript s, Glyph g) => s switch
    {
        // The CJK engines keep accepting any CJK text, as before the per-script split: a
        // user with only zh installed still gets the zh engine's best effort on Japanese,
        // and when several CJK engines run, DedupOverlaps keeps the longest reading.
        OcrScript.Chinese or OcrScript.Japanese or OcrScript.Korean => g is Glyph.Han or Glyph.Kana or Glyph.Hangul,
        OcrScript.Latin => g == Glyph.Latin,
        OcrScript.Cyrillic => g == Glyph.Cyrillic,
        OcrScript.Greek => g == Glyph.Greek,
        OcrScript.Arabic => g == Glyph.Arabic,
        OcrScript.Hebrew => g == Glyph.Hebrew,
        OcrScript.Devanagari => g == Glyph.Devanagari,
        OcrScript.Thai => g == Glyph.Thai,
        _ => false,
    };

    /// <summary>True when the text's dominant script matches the recognizer's script.</summary>
    public static bool IsAppropriate(string text, OcrScript engineScript)
    {
        int own = 0, other = 0;
        foreach (var c in text)
        {
            if (!char.IsLetter(c)) continue;
            if (Owns(engineScript, GlyphOf(c))) own++; else other++;
        }
        if (own == 0 && other == 0) return true; // digits/punctuation only — keep, dedup later
        return own >= other;
    }

    /// <summary>
    /// Merge OCR fragments that sit on the same text line and are horizontally
    /// adjacent (small gap) into one block. Fixes a stray leading word (e.g. "I")
    /// being left untranslated/uncovered, and gives the translator the full sentence.
    /// </summary>
    public static List<OcrLine> MergeLineFragments(List<OcrLine> blocks)
    {
        if (blocks.Count < 2) return blocks;

        var ordered = blocks
            .OrderBy(b => b.Rect.Y)
            .ThenBy(b => b.Rect.X)
            .ToList();

        var used = new bool[ordered.Count];
        var result = new List<OcrLine>(ordered.Count);

        for (int i = 0; i < ordered.Count; i++)
        {
            if (used[i]) continue;
            used[i] = true;

            var rect = ordered[i].Rect;
            var text = new StringBuilder(ordered[i].Text);
            var lang = ordered[i].Language;

            bool extended = true;
            while (extended)
            {
                extended = false;
                for (int j = 0; j < ordered.Count; j++)
                {
                    if (used[j]) continue;
                    var rb = ordered[j].Rect;

                    double centerA = rect.Y + rect.Height / 2;
                    double centerB = rb.Y + rb.Height / 2;
                    if (Math.Abs(centerA - centerB) >= 0.6 * Math.Min(rect.Height, rb.Height))
                        continue; // not on the same line

                    // Only absorb a fragment that follows closely on the right (or overlaps).
                    double gap = rb.X - (rect.X + rect.Width);
                    if (gap > 1.2 * Math.Max(rect.Height, rb.Height)) continue;
                    if (rb.X + rb.Width < rect.X) continue; // fully to the left

                    double nx = Math.Min(rect.X, rb.X);
                    double ny = Math.Min(rect.Y, rb.Y);
                    double nr = Math.Max(rect.X + rect.Width, rb.X + rb.Width);
                    double nb = Math.Max(rect.Y + rect.Height, rb.Y + rb.Height);
                    rect = new OcrRect(nx, ny, nr - nx, nb - ny);
                    text.Append(' ').Append(ordered[j].Text);

                    used[j] = true;
                    extended = true;
                }
            }

            result.Add(new OcrLine(text.ToString(), rect, lang));
        }
        return result;
    }

    public static List<OcrLine> DedupOverlaps(List<OcrLine> blocks)
    {
        var kept = new List<OcrLine>();
        foreach (var b in blocks.OrderByDescending(x => x.Text?.Length ?? 0))
        {
            if (!kept.Any(k => OverlapRatio(k.Rect, b.Rect) > 0.5))
                kept.Add(b);
        }
        return kept;
    }

    public static double OverlapRatio(OcrRect a, OcrRect b)
    {
        double ix = Math.Max(0, Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X));
        double iy = Math.Max(0, Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y));
        double inter = ix * iy;
        double smaller = Math.Min(a.Width * a.Height, b.Width * b.Height);
        return smaller <= 0 ? 0 : inter / smaller;
    }
}
