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
    // anti-aliased UI text (chat bubbles etc.). Capped so we don't blow up memory:
    // a per-side limit AND a total-area limit, because on a large or high-DPI
    // multi-monitor desktop a 2x upscale can otherwise allocate hundreds of MB per
    // copy (and the conversion holds two copies at once).
    private const double TargetScale = 2.0;
    private const int MaxDimension = 8000;
    private const long MaxPixels = 40_000_000; // ~40 MP upscaled (~160 MB per 32bpp copy)

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
        Bitmap? scaledOwned = Math.Abs(scale - 1.0) < 0.001 ? null : Rescale(bitmap, scale);

        // Convert to a SoftwareBitmap, then free the upscaled GDI bitmap immediately —
        // before the (memory-heavy) recognizers run — so we never hold the scaled
        // bitmap and the SoftwareBitmap alongside the OCR working set at the same time.
        SoftwareBitmap softwareBitmap;
        try
        {
            softwareBitmap = ToSoftwareBitmap(scaledOwned ?? bitmap);
        }
        finally
        {
            scaledOwned?.Dispose();
        }

        using (softwareBitmap)
        {
            // Explicit source language → use only that recognizer.
            if (!string.IsNullOrWhiteSpace(sourceLang) &&
                !string.Equals(sourceLang, "auto", StringComparison.OrdinalIgnoreCase))
            {
                var engine = TryCreate(sourceLang);
                if (engine != null)
                    return MergeLineFragments(await RunAsync(engine, softwareBitmap, scale));
            }

            // Auto: run every installed recognizer and keep from each ONLY the blocks
            // whose text matches that recognizer's script (Latin, Cyrillic, Greek, ..., or
            // Chinese/Japanese/Korean separately). This handles mixed screens (e.g. a
            // Chinese bubble among Latin UI): the Latin engine contributes the Latin
            // text, the Chinese engine the Chinese text. A single engine
            // can't do both, so we union the appropriate parts and dedup overlaps.
            // ONE recognizer per script is enough, and that cap matters: each engine makes
            // a full pass over an image of up to MaxPixels, so a desktop with en-US, it-IT
            // and fr-FR installed used to run three Latin passes whose extra blocks
            // IsAppropriate keeps and DedupOverlaps then discards — pure cost. The user's
            // profile engine is offered first so it wins its own bucket.
            var engines = new List<OcrEngine>();
            var covered = new HashSet<OcrScript>();

            void Add(OcrEngine? e)
            {
                if (e == null) return;
                if (covered.Add(OcrPostProcessor.ScriptOfTag(e.RecognizerLanguage?.LanguageTag)))
                    engines.Add(e);
            }

            Add(OcrEngine.TryCreateFromUserProfileLanguages());
            foreach (var lang in OcrEngine.AvailableRecognizerLanguages)
            {
                if (covered.Count == OcrPostProcessor.ScriptCount) break; // every bucket filled
                // Check the tag before building the engine — creating one we would
                // immediately discard is wasted work.
                if (covered.Contains(OcrPostProcessor.ScriptOfTag(lang.LanguageTag))) continue;
                Add(OcrEngine.TryCreateFromLanguage(lang));
            }

            // Run the recognizers concurrently. Each pass over the (upscaled) bitmap is
            // independent and read-only, so wall-time is the slowest engine, not their sum.
            var perEngine = await Task.WhenAll(engines.Select(async engine =>
            {
                var es = OcrPostProcessor.ScriptOfTag(engine.RecognizerLanguage?.LanguageTag);
                var blocks = await RunAsync(engine, softwareBitmap, scale);
                return blocks.Where(b => OcrPostProcessor.IsAppropriate(b.OriginalText, es));
            }));

            var all = perEngine.SelectMany(b => b).ToList();
            return MergeLineFragments(DedupOverlaps(all));
        }
    }

    private static double ChooseScale(Bitmap b)
    {
        // Largest scale (≤ TargetScale) that keeps the image inside the per-side and
        // total-area caps. This may drop BELOW 1.0: a very wide virtual desktop (e.g.
        // three 4K monitors ≈ 11520 px) exceeds what Windows OCR accepts at native
        // size, so oversized captures are downscaled rather than failing outright.
        double s = TargetScale;
        s = Math.Min(s, (double)MaxDimension / b.Width);
        s = Math.Min(s, (double)MaxDimension / b.Height);
        s = Math.Min(s, Math.Sqrt((double)MaxPixels / ((double)b.Width * b.Height)));
        return s;
    }

    private static Bitmap Rescale(Bitmap src, double scale)
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

    private static List<OcrBlock> MergeLineFragments(List<OcrBlock> blocks) =>
        FromLines(OcrPostProcessor.MergeLineFragments(ToLines(blocks)));

    private static List<OcrBlock> DedupOverlaps(List<OcrBlock> blocks) =>
        FromLines(OcrPostProcessor.DedupOverlaps(ToLines(blocks)));

    private static List<OcrLine> ToLines(List<OcrBlock> blocks) =>
        blocks.Select(b => new OcrLine(b.OriginalText,
            new OcrRect(b.BoundingRect.X, b.BoundingRect.Y, b.BoundingRect.Width, b.BoundingRect.Height),
            b.DetectedLanguage)).ToList();

    private static List<OcrBlock> FromLines(List<OcrLine> lines) =>
        lines.Select(l => new OcrBlock
        {
            OriginalText = l.Text,
            BoundingRect = new Rect(l.Rect.X, l.Rect.Y, l.Rect.Width, l.Rect.Height),
            DetectedLanguage = l.Language,
        }).ToList();

    /// <summary>
    /// Convert a GDI bitmap into a SoftwareBitmap by copying its pixels once into a
    /// byte[] and handing that to the SoftwareBitmap. A 32bpp GDI bitmap is already
    /// BGRA with a tight (w*4) stride, so the copy is a straight memcpy in the common
    /// case. (An earlier attempt wrote directly into the SoftwareBitmap buffer via
    /// IMemoryBufferByteAccess to drop this byte[]; that COM cast doesn't work under
    /// CsWinRT and broke OCR, so we keep the simple, correct path. Peak memory is
    /// instead bounded by the upscale caps in ChooseScale.)
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
