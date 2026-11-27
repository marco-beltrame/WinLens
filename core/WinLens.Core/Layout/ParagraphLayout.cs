using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using WinLens.Services;

namespace WinLens.Layout;

/// <summary>A translated paragraph laid back over the lines the original occupied.</summary>
/// <param name="Lines">One entry per original line, top to bottom; trailing ones may be empty.</param>
/// <param name="Scale">Factor (at most 1) to apply to the font size so every line fits.</param>
public sealed record ParagraphFit(IReadOnlyList<string> Lines, double Scale);

/// <summary>
/// Puts a translated paragraph back on the lines of the original. The translation rarely
/// has the length of the source, so the words are poured over the same number of lines
/// again: each line takes about the share of the text the original line had, the font
/// only shrinks when the text cannot fit at full size, and every line of the paragraph
/// shares one size. Pure (the caller supplies the text measurer), so it is unit-tested
/// without WPF.
/// </summary>
public static class ParagraphLayout
{
    /// <summary>A run of text that must stay on one line.</summary>
    internal readonly record struct Token(string Text, bool SpaceBefore);

    /// <param name="text">The translated paragraph.</param>
    /// <param name="capacities">The widest each line may become, at scale 1.</param>
    /// <param name="shares">How wide each original line was; steers how the text is spread.</param>
    /// <param name="measure">Width of a string at scale 1. Must count a lone space.</param>
    /// <param name="minScale">Smallest scale worth reading. Below it the last line overflows.</param>
    public static ParagraphFit Flow(
        string text,
        IReadOnlyList<double> capacities,
        IReadOnlyList<double> shares,
        Func<string, double> measure,
        double minScale)
    {
        if (capacities.Count == 0 || capacities.Count != shares.Count)
            throw new ArgumentException("One capacity and one share per line are required.");

        int lineCount = capacities.Count;
        var tokens = Tokenize(text);
        if (tokens.Count == 0)
            return new ParagraphFit(Enumerable.Repeat("", lineCount).ToArray(), 1);

        var widths = new double[tokens.Count];
        for (int i = 0; i < widths.Length; i++)
            widths[i] = measure(tokens[i].Text);
        double space = measure(" ");
        minScale = Math.Clamp(minScale, 0.01, 1);

        // 1. The largest font at which the text fits on these lines at all.
        double scale = 1;
        if (Fill(tokens, widths, space, capacities, capacities, 1) == null)
        {
            if (Fill(tokens, widths, space, capacities, capacities, minScale) == null)
            {
                // Hopeless (one very long word, or far more text than room): fill what
                // fits at the smallest size and let the last line run over.
                return new ParagraphFit(Lines(tokens, FillForced(tokens, widths, space, capacities, minScale)), minScale);
            }

            double tooBig = 1;
            scale = minScale;
            for (int i = 0; i < 20; i++)
            {
                double mid = (scale + tooBig) / 2;
                if (Fill(tokens, widths, space, capacities, capacities, mid) != null) scale = mid;
                else tooBig = mid;
            }
        }

        // 2. At that size, spread the text the way the original was spread: a short last
        //    line stays short, and a translation shorter than the source still reaches
        //    the last line instead of leaving it empty.
        double maxRatio = 0;
        for (int k = 0; k < lineCount; k++)
            if (shares[k] > 0) maxRatio = Math.Max(maxRatio, capacities[k] / shares[k]);

        var ends = Fill(tokens, widths, space, capacities, capacities, scale)!;
        if (maxRatio > 0)
        {
            double lo = 0, hi = maxRatio;
            var soft = new double[lineCount];
            for (int i = 0; i < 24; i++)
            {
                double mid = (lo + hi) / 2;
                for (int k = 0; k < lineCount; k++)
                    soft[k] = Math.Min(capacities[k], mid * shares[k]);
                if (Fill(tokens, widths, space, soft, capacities, scale) is { } spread)
                {
                    ends = spread;
                    hi = mid;
                }
                else
                {
                    lo = mid;
                }
            }
        }
        return new ParagraphFit(Lines(tokens, ends), scale);
    }

    /// <summary>
    /// The rows a translated paragraph is drawn in: the original lines made equally tall
    /// and evenly spaced. OCR boxes hug the ink, so a line without descenders is shorter
    /// than its neighbours and one without capitals starts lower; text in one font size
    /// drawn into those would sit unevenly.
    /// </summary>
    /// <param name="lines">Top and height of each original line, top to bottom.</param>
    public static (double Top, double Height)[] EvenRows(IReadOnlyList<(double Top, double Height)> lines)
    {
        if (lines.Count < 2)
            return lines.ToArray();

        // The tallest line shows the full ink height of the font. One far taller than the
        // rest is an OCR box that swallowed something else.
        double usual = Median(lines.Select(l => l.Height));
        double height = lines.Select(l => l.Height).Where(h => h <= usual * 1.35).DefaultIfEmpty(usual).Max();

        // Medians, so the odd line that starts lower does not tilt the whole paragraph.
        double pitch = Median(Enumerable.Range(1, lines.Count - 1).Select(k => lines[k].Top - lines[k - 1].Top));
        double first = Median(Enumerable.Range(0, lines.Count).Select(k => lines[k].Top - k * pitch));
        // Text set tighter than its own height: a row must not reach into the next one,
        // or the fill behind one line would cover the bottom of the line above.
        if (pitch > 0) height = Math.Min(height, pitch);

        var rows = new (double Top, double Height)[lines.Count];
        for (int k = 0; k < rows.Length; k++)
            rows[k] = (first + k * pitch, height);
        return rows;
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    /// <summary>
    /// How far each line of a paragraph may reach sideways: to the paragraph's own edges,
    /// or up to other text on the same row (the time next to the last line of a chat
    /// message, a button after a label), whichever comes first. A translation longer than
    /// the original uses the empty end of a short line, and must not run under that.
    /// </summary>
    /// <param name="lines">The paragraph's lines.</param>
    /// <param name="others">Every other piece of text on screen.</param>
    /// <param name="gap">Space to keep clear before the other text.</param>
    public static (double Left, double Right)[] Reach(
        IReadOnlyList<OcrRect> lines, IEnumerable<OcrRect> others, double gap)
    {
        double left = lines.Min(l => l.X), right = lines.Max(l => l.X + l.Width);
        var reach = new (double Left, double Right)[lines.Count];
        Array.Fill(reach, (left, right));

        foreach (var o in others)
        {
            double oRight = o.X + o.Width, oBottom = o.Y + o.Height;
            if (oRight <= left || o.X >= right) continue;

            for (int k = 0; k < reach.Length; k++)
            {
                var l = lines[k];
                double shared = Math.Min(l.Y + l.Height, oBottom) - Math.Max(l.Y, o.Y);
                if (shared < 0.5 * Math.Min(l.Height, o.Height)) continue; // not on this row

                double lineRight = l.X + l.Width;
                if (o.X >= lineRight)
                    reach[k].Right = Math.Min(reach[k].Right, Math.Max(lineRight, o.X - gap));
                else if (oRight <= l.X)
                    reach[k].Left = Math.Max(reach[k].Left, Math.Min(l.X, oRight + gap));
            }
        }
        return reach;
    }

    /// <summary>
    /// Greedy fill. A line takes tokens while they stay within its soft limit; the first
    /// token of a line only has to respect the hard limit, so a word longer than the
    /// line's share still gets a line of its own.
    /// </summary>
    /// <returns>For each line the index after its last token, or null if tokens are left over.</returns>
    private static int[]? Fill(
        List<Token> tokens, double[] widths, double space,
        IReadOnlyList<double> soft, IReadOnlyList<double> hard, double scale)
    {
        var ends = new int[soft.Count];
        int i = 0;
        for (int k = 0; k < ends.Length; k++)
        {
            double used = 0;
            int start = i;
            while (i < tokens.Count)
            {
                double add = widths[i] + (i > start && tokens[i].SpaceBefore ? space : 0);
                double w = (used + add) * scale;
                if (w > soft[k] && !(i == start && w <= hard[k]))
                    break;
                used += add;
                i++;
            }
            ends[k] = i;
        }
        return i == tokens.Count ? ends : null;
    }

    private static int[] FillForced(
        List<Token> tokens, double[] widths, double space, IReadOnlyList<double> capacities, double scale)
    {
        var ends = new int[capacities.Count];
        int i = 0;
        for (int k = 0; k < ends.Length; k++)
        {
            double used = 0;
            int start = i;
            while (i < tokens.Count)
            {
                double add = widths[i] + (i > start && tokens[i].SpaceBefore ? space : 0);
                // Always place at least one token, or an oversized word would block the rest.
                if (i > start && (used + add) * scale > capacities[k])
                    break;
                used += add;
                i++;
            }
            ends[k] = i;
        }
        ends[^1] = tokens.Count;
        return ends;
    }

    private static string[] Lines(List<Token> tokens, int[] ends)
    {
        var lines = new string[ends.Length];
        var sb = new StringBuilder();
        int i = 0;
        for (int k = 0; k < ends.Length; k++)
        {
            sb.Clear();
            for (int start = i; i < ends[k]; i++)
            {
                if (i > start && tokens[i].SpaceBefore) sb.Append(' ');
                sb.Append(tokens[i].Text);
            }
            lines[k] = sb.ToString();
        }
        return lines;
    }

    /// <summary>
    /// Splits text at the places a line may break: at spaces, and between characters of
    /// scripts written without spaces (Chinese, Japanese, Thai, ...). Closing punctuation
    /// stays with what precedes it and opening punctuation with what follows, so a line
    /// never starts with "。" or ends with "「".
    /// </summary>
    internal static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var current = new StringBuilder();
        bool spaceBefore = false;      // whitespace seen before the token being built
        bool pendingSpace = false;     // whitespace seen since the last character
        bool pendingBreak = false;     // a zero-width space seen since the last character
        bool lastBreakable = false;    // the token ends in a character a line may break after
        bool lastOpens = false;        // the token ends in opening punctuation

        void Flush()
        {
            if (current.Length == 0) return;
            tokens.Add(new Token(current.ToString(), spaceBefore));
            current.Clear();
        }

        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var element = (string)elements.Current;
            if (string.IsNullOrWhiteSpace(element))
            {
                pendingSpace = true;
                continue;
            }
            if (element == "\u200B")
            {
                // Khmer and Myanmar text marks its word boundaries with these.
                pendingBreak = true;
                continue;
            }

            // Not GetRuneAt: OCR and translators do hand back the odd unpaired surrogate.
            Rune.DecodeFromUtf16(element, out var rune, out _);
            bool breakable = IsBreakable(rune);
            bool mayBreak = pendingBreak || breakable || lastBreakable || StartsSyllable(rune);
            bool glued = current.Length > 0 && !pendingSpace &&
                (IsCloser(rune) || lastOpens || !mayBreak);
            if (!glued)
            {
                Flush();
                spaceBefore = pendingSpace && tokens.Count > 0;
            }
            current.Append(element);
            pendingSpace = false;
            pendingBreak = false;
            lastBreakable = breakable || EndsSyllable(element[^1]);
            lastOpens = IsOpener(rune);
        }
        Flush();
        return tokens;
    }

    /// <summary>Characters a line may break before and after: Chinese and Japanese.</summary>
    private static bool IsBreakable(Rune r)
    {
        int c = r.Value;
        return c >= 0x4E00 && c <= 0x9FFF      // CJK ideographs
            || c >= 0x3400 && c <= 0x4DBF
            || c >= 0xF900 && c <= 0xFAFF
            || c >= 0x20000 && c <= 0x3FFFF
            || c >= 0x3040 && c <= 0x30FF      // kana
            || c >= 0x31F0 && c <= 0x31FF
            || c >= 0x3000 && c <= 0x303F      // CJK punctuation
            || c >= 0xFF00 && c <= 0xFFEF;     // full-width forms
    }

    // Thai and Lao put no space between words either, but finding the words takes a
    // dictionary. Without one a line is only broken where a syllable certainly starts
    // (before a vowel written ahead of its consonant) or ends (after one that closes it),
    // never inside one. Khmer and Myanmar break at their spaces and zero-width spaces.
    private static bool StartsSyllable(Rune r) =>
        r.Value is >= 0x0E40 and <= 0x0E44 or >= 0x0EC0 and <= 0x0EC4;

    private static bool EndsSyllable(char c) =>
        c is '\u0E30' or '\u0E33' or '\u0E46' or '\u0E2F' or '\u0EB0' or '\u0EB3' or '\u0EC6' or '\u0EAF';

    private const string Closers = "、。，．・：；！？）］｝〉》」』】〕〗〙〛’”ー々,.!?:;)]}%";
    private const string Openers = "（［｛〈《「『【〔〖〘〚‘“([{";

    private static bool IsCloser(Rune r) => r.IsBmp && Closers.Contains((char)r.Value);

    private static bool IsOpener(Rune r) => r.IsBmp && Openers.Contains((char)r.Value);
}
