using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using WinLens.Models;

namespace WinLens.Services;

[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class OcrService
{
    // Upscaling the screenshot before OCR markedly improves accuracy on small,
    // anti-aliased UI text (chat bubbles etc.). Capped so we don't blow up memory.
    private const double TargetScale = 2.0;
    private const int MaxDimension = 8000;

    /// <summary>
    /// OCR recognizer languages installed on this machine (tag + display name).
    /// Queried live each call so newly installed packs appear without restarting.
    /// </summary>
    public static IReadOnlyList<(string Tag, string Display)> AvailableLanguages => LoadAvailable();

    private static IReadOnlyList<(string, string)> LoadAvailable()
    {
        try
        {
            return OcrEngine.AvailableRecognizerLanguages
                .Select(l => (l.LanguageTag, l.DisplayName))
                .ToList();
        }
        catch { return new List<(string, string)>(); }
    }

    /// <summary>
    /// Run OCR on the bitmap. When <paramref name="sourceLang"/> is null/"auto" the best
    /// installed recognizer is chosen automatically; otherwise that specific recognizer is used.
    /// The image is upscaled before recognition; returned coordinates are in ORIGINAL pixels.
    /// </summary>
    public async Task<List<OcrBlock>> RecognizeAsync(Bitmap bitmap, string? sourceLang = null)
    {
        double scale = ChooseScale(bitmap);
        Bitmap? scaledOwned = scale == 1.0 ? null : Upscale(bitmap, scale);
        var source = scaledOwned ?? bitmap;

        try
        {
            using var softwareBitmap = ToSoftwareBitmap(source);

            // Explicit source language → use only that recognizer.
            if (!string.IsNullOrWhiteSpace(sourceLang) &&
                !string.Equals(sourceLang, "auto", StringComparison.OrdinalIgnoreCase))
            {
                var engine = TryCreate(sourceLang);
                if (engine != null)
                    return MergeLineFragments(await RunAsync(engine, softwareBitmap, scale));
            }

            // Auto: run every installed recognizer and keep from each ONLY the blocks
            // whose text matches that recognizer's script (Latin vs CJK). This handles
            // mixed screens (e.g. a Chinese bubble among Latin UI): the Latin engine
            // contributes the Latin text, the CJK engine the CJK text. A single engine
            // can't do both, so we union the appropriate parts and dedup overlaps.
            var engines = new List<OcrEngine>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(OcrEngine? e)
            {
                if (e == null) return;
                var tag = e.RecognizerLanguage?.LanguageTag ?? Guid.NewGuid().ToString();
                if (seen.Add(tag)) engines.Add(e);
            }

            Add(OcrEngine.TryCreateFromUserProfileLanguages());
            foreach (var lang in OcrEngine.AvailableRecognizerLanguages)
                Add(OcrEngine.TryCreateFromLanguage(lang));

            var all = new List<OcrBlock>();
            foreach (var engine in engines)
            {
                var es = ScriptOfTag(engine.RecognizerLanguage?.LanguageTag);
                foreach (var b in await RunAsync(engine, softwareBitmap, scale))
                    if (IsAppropriate(b.OriginalText, es))
                        all.Add(b);
            }
            return MergeLineFragments(DedupOverlaps(all));
        }
        finally
        {
            scaledOwned?.Dispose();
        }
    }

    private static double ChooseScale(Bitmap b)
    {
        double s = TargetScale;
        while (s > 1.0 && (b.Width * s > MaxDimension || b.Height * s > MaxDimension))
            s -= 0.5;
        return s < 1.0 ? 1.0 : s;
    }

    private static Bitmap Upscale(Bitmap src, double scale)
    {
        int w = (int)Math.Round(src.Width * scale);
        int h = (int)Math.Round(src.Height * scale);
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.DrawImage(src, 0, 0, w, h);
        return bmp;
    }

    private static OcrEngine? TryCreate(string tag)
    {
        try
        {
            var lang = new Language(tag);
            return OcrEngine.IsLanguageSupported(lang) ? OcrEngine.TryCreateFromLanguage(lang) : null;
        }
        catch { return null; }
    }

    private static async Task<List<OcrBlock>> RunAsync(OcrEngine engine, SoftwareBitmap bmp, double scale)
    {
        var result = await engine.RecognizeAsync(bmp);

        var blocks = new List<OcrBlock>(result.Lines.Count);
        foreach (var line in result.Lines)
        {
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            foreach (var word in line.Words)
            {
                var r = word.BoundingRect;
                if (r.X < minX) minX = r.X;
                if (r.Y < minY) minY = r.Y;
                if (r.X + r.Width  > maxX) maxX = r.X + r.Width;
                if (r.Y + r.Height > maxY) maxY = r.Y + r.Height;
            }
            if (minX == double.MaxValue) continue;

            // Map back from upscaled image coords to original screenshot pixels.
            blocks.Add(new OcrBlock
            {
                OriginalText = line.Text,
                BoundingRect = new Rect(minX / scale, minY / scale,
                                        (maxX - minX) / scale, (maxY - minY) / scale),
                DetectedLanguage = engine.RecognizerLanguage?.LanguageTag,
            });
        }
        return blocks;
    }

    /// <summary>
    /// Merge OCR fragments that sit on the same text line and are horizontally
    /// adjacent (small gap) into one block. Fixes a stray leading word (e.g. "I")
    /// being left untranslated/uncovered, and gives the translator the full sentence.
    /// </summary>
    private static List<OcrBlock> MergeLineFragments(List<OcrBlock> blocks)
    {
        if (blocks.Count < 2) return blocks;

        var ordered = blocks
            .OrderBy(b => b.BoundingRect.Y)
            .ThenBy(b => b.BoundingRect.X)
            .ToList();

        var used = new bool[ordered.Count];
        var result = new List<OcrBlock>(ordered.Count);

        for (int i = 0; i < ordered.Count; i++)
        {
            if (used[i]) continue;
            used[i] = true;

            var rect = ordered[i].BoundingRect;
            var text = new StringBuilder(ordered[i].OriginalText);
            var lang = ordered[i].DetectedLanguage;

            bool extended = true;
            while (extended)
            {
                extended = false;
                for (int j = 0; j < ordered.Count; j++)
                {
                    if (used[j]) continue;
                    var rb = ordered[j].BoundingRect;

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
                    rect = new Rect(nx, ny, nr - nx, nb - ny);
                    text.Append(' ').Append(ordered[j].OriginalText);

                    used[j] = true;
                    extended = true;
                }
            }

            result.Add(new OcrBlock
            {
                OriginalText = text.ToString(),
                BoundingRect = rect,
                DetectedLanguage = lang,
            });
        }
        return result;
    }

    private enum Script { Latin, Cjk }

    private static Script ScriptOfTag(string? tag)
    {
        if (tag != null &&
            (tag.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ||
             tag.StartsWith("ja", StringComparison.OrdinalIgnoreCase) ||
             tag.StartsWith("ko", StringComparison.OrdinalIgnoreCase)))
            return Script.Cjk;
        return Script.Latin;
    }

    private static bool IsCjk(char c) =>
        (c >= 0x4E00 && c <= 0x9FFF) ||  // CJK Unified Ideographs
        (c >= 0x3400 && c <= 0x4DBF) ||  // Extension A
        (c >= 0x3040 && c <= 0x30FF) ||  // Hiragana + Katakana
        (c >= 0xAC00 && c <= 0xD7AF);    // Hangul syllables

    /// <summary>True when the text's dominant script matches the recognizer's script.</summary>
    private static bool IsAppropriate(string text, Script engineScript)
    {
        int cjk = 0, latin = 0;
        foreach (var c in text)
        {
            if (IsCjk(c)) cjk++;
            else if (char.IsLetter(c)) latin++;
        }
        if (cjk == 0 && latin == 0) return true; // digits/punctuation only — keep, dedup later
        return engineScript == Script.Cjk ? cjk >= latin : latin >= cjk;
    }

    private static List<OcrBlock> DedupOverlaps(List<OcrBlock> blocks)
    {
        var kept = new List<OcrBlock>();
        foreach (var b in blocks.OrderByDescending(x => x.OriginalText?.Length ?? 0))
        {
            if (!kept.Any(k => OverlapRatio(k.BoundingRect, b.BoundingRect) > 0.5))
                kept.Add(b);
        }
        return kept;
    }

    private static double OverlapRatio(Rect a, Rect b)
    {
        double ix = Math.Max(0, Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X));
        double iy = Math.Max(0, Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y));
        double inter = ix * iy;
        double smaller = Math.Min(a.Width * a.Height, b.Width * b.Height);
        return smaller <= 0 ? 0 : inter / smaller;
    }

    /// <summary>
    /// Convert a GDI bitmap straight into a SoftwareBitmap by copying its pixels once.
    /// The previous path went bitmap → BMP encode → byte[] → IBuffer → decode, i.e. four
    /// extra full-frame copies that, on an upscaled multi-monitor screenshot, each ran to
    /// tens of MB and landed on the Large Object Heap. A 32bpp GDI bitmap is already BGRA
    /// with a tight (w*4) stride, so we lock it and hand the bytes to the SoftwareBitmap.
    /// </summary>
    private static SoftwareBitmap ToSoftwareBitmap(Bitmap bitmap)
    {
        int w = bitmap.Width, h = bitmap.Height;
        var rect = new Rectangle(0, 0, w, h);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int rowBytes = w * 4;
            var buffer = new byte[rowBytes * h];
            if (data.Stride == rowBytes)
            {
                Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
            }
            else
            {
                for (int y = 0; y < h; y++)
                    Marshal.Copy(data.Scan0 + y * data.Stride, buffer, y * rowBytes, rowBytes);
            }

            var sb = new SoftwareBitmap(BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);
            sb.CopyFromBuffer(buffer.AsBuffer());
            return sb;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
