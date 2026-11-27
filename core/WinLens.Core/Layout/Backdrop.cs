using System;

namespace WinLens.Layout;

/// <summary>The pixels of a screenshot, each as 0xRRGGBB.</summary>
public interface IPixels
{
    int Width { get; }
    int Height { get; }

    /// <summary>The caller keeps <paramref name="x"/> and <paramref name="y"/> inside the picture.</summary>
    int Rgb(int x, int y);
}

/// <summary>How far plain background goes on from a line of text.</summary>
/// <param name="Length">Pixels of background found.</param>
/// <param name="Ended">
/// True when something stopped it (other content, or the edge of the picture); false when
/// the search simply went as far as it was asked to.
/// </param>
public readonly record struct BackdropRun(int Length, bool Ended);

/// <summary>
/// What a line of text stands on: the colour its fill takes, and how far that same plain
/// background continues around it. A translation longer than the original may use that
/// room; anything else there (an icon, a border, text the OCR missed) ends it.
/// </summary>
public static class Backdrop
{
    /// <summary>
    /// How far a channel may differ and still count as the same background. Small: a card
    /// is often only a shade lighter than the page it lies on, and a fill that runs over
    /// its edge shows.
    /// </summary>
    public const int Tolerance = 4;

    // Samples this close to the middle value are averaged into the colour.
    private const int Near = 24;

    /// <summary>
    /// The background colour among samples taken around a line. Not their plain average:
    /// a few of them usually land on something else (the descenders of the line above, an
    /// icon, the edge of a button), and averaged in they tint the fill. On a flat
    /// background it is the one colour many samples share, exactly. Otherwise (a gradient,
    /// a picture) the middle value of each channel is what most samples agree on, and the
    /// ones close to it are averaged.
    /// </summary>
    public static int Colour(ReadOnlySpan<int> samples)
    {
        if (samples.Length == 0) return 0;
        if (Commonest(samples, out int flat) * 5 >= samples.Length * 2) return flat;

        Span<int> r = stackalloc int[256], g = stackalloc int[256], b = stackalloc int[256];
        foreach (int s in samples)
        {
            r[(s >> 16) & 0xFF]++;
            g[(s >> 8) & 0xFF]++;
            b[s & 0xFF]++;
        }
        int mr = Middle(r, samples.Length), mg = Middle(g, samples.Length), mb = Middle(b, samples.Length);

        long sr = 0, sg = 0, sb = 0, n = 0;
        foreach (int s in samples)
        {
            int cr = (s >> 16) & 0xFF, cg = (s >> 8) & 0xFF, cb = s & 0xFF;
            if (Math.Abs(cr - mr) > Near || Math.Abs(cg - mg) > Near || Math.Abs(cb - mb) > Near) continue;
            sr += cr; sg += cg; sb += cb; n++;
        }
        // The middle of each channel need not belong to one sample (a two-coloured backdrop).
        return n == 0 ? (mr << 16) | (mg << 8) | mb
                      : ((int)(sr / n) << 16) | ((int)(sg / n) << 8) | (int)(sb / n);
    }

    /// <returns>How many samples have the colour that occurs most often.</returns>
    private static int Commonest(ReadOnlySpan<int> samples, out int colour)
    {
        Span<int> sorted = samples.Length <= 512 ? stackalloc int[samples.Length] : new int[samples.Length];
        samples.CopyTo(sorted);
        sorted.Sort();

        colour = sorted[0];
        int most = 0;
        for (int i = 0, start = 0; i <= sorted.Length; i++)
        {
            if (i < sorted.Length && sorted[i] == sorted[start]) continue;
            if (i - start > most)
            {
                most = i - start;
                colour = sorted[start];
            }
            start = i;
        }
        return most;
    }

    private static int Middle(ReadOnlySpan<int> counts, int total)
    {
        int seen = 0;
        for (int v = 0; v < counts.Length; v++)
        {
            seen += counts[v];
            if (seen * 2 >= total) return v;
        }
        return counts.Length - 1;
    }

    /// <summary>True when two colours are the same background, give or take anti-aliasing and noise.</summary>
    public static bool Same(int a, int b) =>
        Math.Abs(((a >> 16) & 0xFF) - ((b >> 16) & 0xFF)) <= Tolerance &&
        Math.Abs(((a >> 8) & 0xFF) - ((b >> 8) & 0xFF)) <= Tolerance &&
        Math.Abs((a & 0xFF) - (b & 0xFF)) <= Tolerance;

    /// <summary>
    /// Counts the columns of plain background beside a line: from column <paramref name="x"/>
    /// on, to the right (<paramref name="step"/> 1) or to the left (-1), every pixel between
    /// rows <paramref name="top"/> and <paramref name="bottom"/> must have the colour.
    /// </summary>
    /// <param name="limit">How many columns to look at, at most.</param>
    /// <param name="lead">
    /// How many of the first columns may still hold the end of the text itself (the box of
    /// a line can cut its last letter short). Such columns are passed over, up to the first
    /// clear one, and count as background. Not when what is in them reaches the top or the
    /// bottom row, the margin text leaves free: that is a rule or an edge, and it ends the
    /// run like anything further on does.
    /// </param>
    public static BackdropRun Sideways(
        IPixels pixels, int x, int step, int top, int bottom, int colour, int limit, int lead = 0)
    {
        // A band the picture cuts off has no margin on that side to tell text by.
        bool marginAbove = top >= 0, marginBelow = bottom < pixels.Height;
        top = Math.Max(0, top);
        bottom = Math.Min(pixels.Height - 1, bottom);
        if (top > bottom) return new BackdropRun(0, true);

        int passed = 0;
        for (; passed < lead && passed < limit; passed++)
        {
            int cx = x + passed * step;
            if (cx < 0 || cx >= pixels.Width) return new BackdropRun(passed, true);
            if (ColumnIsClear(pixels, cx, top, bottom, colour)) break;
            if (marginAbove && !Same(pixels.Rgb(cx, top), colour) ||
                marginBelow && !Same(pixels.Rgb(cx, bottom), colour))
                return new BackdropRun(passed, true);
        }
        x += passed * step;
        limit -= passed;

        // The middle row first: it finds most obstacles and spares reading whole columns
        // beyond them.
        int mid = (top + bottom) / 2;
        int reach = 0;
        bool ended = false;
        for (int cx = x; reach < limit; cx += step, reach++)
        {
            if (cx < 0 || cx >= pixels.Width || !Same(pixels.Rgb(cx, mid), colour))
            {
                ended = true;
                break;
            }
        }

        int length = 0;
        for (int cx = x; length < reach; cx += step, length++)
        {
            if (!ColumnIsClear(pixels, cx, top, bottom, colour))
                return new BackdropRun(passed + length, true);
        }
        return new BackdropRun(passed + length, ended);
    }

    /// <summary>
    /// Counts the rows of plain background above (<paramref name="step"/> -1) or below (1)
    /// a line: from row <paramref name="y"/> on, every pixel between columns
    /// <paramref name="left"/> and <paramref name="right"/> must have the colour.
    /// </summary>
    /// <param name="limit">How many rows to look at, at most.</param>
    /// <param name="lead">
    /// How many of the first rows may still hold something of the text itself (the foot of
    /// a letter its box cut short, its underline): see <see cref="Sideways"/>. Here it is
    /// what reaches the first or the last column that ends the run.
    /// </param>
    public static BackdropRun UpOrDown(
        IPixels pixels, int y, int step, int left, int right, int colour, int limit, int lead = 0)
    {
        bool marginLeft = left >= 0, marginRight = right < pixels.Width;
        left = Math.Max(0, left);
        right = Math.Min(pixels.Width - 1, right);
        if (left > right) return new BackdropRun(0, true);

        int passed = 0;
        for (; passed < lead && passed < limit; passed++)
        {
            int cy = y + passed * step;
            if (cy < 0 || cy >= pixels.Height) return new BackdropRun(passed, true);
            if (RowIsClear(pixels, cy, left, right, colour)) break;
            if (marginLeft && !Same(pixels.Rgb(left, cy), colour) ||
                marginRight && !Same(pixels.Rgb(right, cy), colour))
                return new BackdropRun(passed, true);
        }

        int length = passed;
        for (int cy = y + passed * step; length < limit; cy += step, length++)
        {
            if (cy < 0 || cy >= pixels.Height || !RowIsClear(pixels, cy, left, right, colour))
                return new BackdropRun(length, true);
        }
        return new BackdropRun(length, false);
    }

    private static bool ColumnIsClear(IPixels pixels, int x, int top, int bottom, int colour)
    {
        for (int y = top; y <= bottom; y++)
            if (!Same(pixels.Rgb(x, y), colour)) return false;
        return true;
    }

    private static bool RowIsClear(IPixels pixels, int y, int left, int right, int colour)
    {
        for (int x = left; x <= right; x++)
            if (!Same(pixels.Rgb(x, y), colour)) return false;
        return true;
    }

    /// <summary>
    /// True when a line sits in the middle of what holds it (the label of a button, a tab,
    /// a title): the background ends equally far away on both sides. Its translation then
    /// stays centred instead of starting where the original started.
    /// </summary>
    /// <param name="left">Distance from the line to whatever ends the background on its left.</param>
    /// <param name="right">The same on its right.</param>
    public static bool Centred(BackdropRun left, BackdropRun right)
    {
        if (!left.Ended || !right.Ended) return false;
        int near = Math.Min(left.Length, right.Length);
        return Math.Abs(left.Length - right.Length) <= Math.Max(4, near / 10);
    }
}
