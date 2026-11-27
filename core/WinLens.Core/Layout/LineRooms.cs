using System;
using System.Collections.Generic;
using System.Linq;
using WinLens.Services;

namespace WinLens.Layout;

/// <summary>
/// The screenshot under the coordinates text is laid out in. A position <c>u</c> lies on
/// pixel <c>u * PixelsPerUnit - Offset</c> (the layout works in device-independent units,
/// and the screenshot may sit somewhere inside the window).
/// </summary>
public readonly record struct PixelGrid(IPixels Pixels, double PixelsPerUnit, double OffsetX, double OffsetY)
{
    public double X(double unit) => unit * PixelsPerUnit - OffsetX;
    public double Y(double unit) => unit * PixelsPerUnit - OffsetY;
}

/// <summary>A single line as it is drawn: its box, and the fill behind it.</summary>
/// <param name="Block">Which block the line is.</param>
/// <param name="Line">The line as it was found.</param>
/// <param name="PadX">How far the fill behind the line goes past its text, sideways.</param>
/// <param name="PadY">The same, above and below.</param>
/// <param name="Colour">The background the line stands on, as 0xRRGGBB.</param>
public sealed record LineSpot(int Block, OcrRect Line, double PadX, double PadY, int Colour);

/// <summary>The room a single line has beside it for a translation longer than itself.</summary>
/// <param name="Left">How far the text may start before the original (only when centred).</param>
/// <param name="Right">How far it may run on past the original.</param>
/// <param name="Centred">The line sits in the middle of what holds it, and stays there.</param>
public sealed record LineRoom(LineSpot Spot, double Left, double Right, bool Centred);

/// <summary>Where the translation of a single line goes.</summary>
/// <param name="Box">The text's box. Never smaller than the original line.</param>
/// <param name="Rows">The text, row by row.</param>
/// <param name="Scale">
/// The font scale the rows fit at. Below 1 for a single row means it is still too wide
/// for the box and has to be shrunk into it.
/// </param>
/// <param name="Centred">
/// The text was too wide for its line, and the line sits in the middle of what holds it:
/// the box grew the same on both sides.
/// </param>
public sealed record LinePlacement(OcrRect Box, IReadOnlyList<string> Rows, double Scale, bool Centred);

/// <summary>
/// Finds room for translations that are longer than the line they replace. A single line
/// (a title, a menu entry, the label of a button, a chat message) used to keep its box,
/// and its translation was shrunk until it fitted: Chinese into English came out half the
/// size, or cut off. Here the text first runs on over the plain background beside the
/// line, then goes onto two or three rows where there is such background above or below,
/// and is only shrunk after that.
///
/// Room is background of the line's own colour with nothing on it (see
/// <see cref="Backdrop"/>), and never what other text may need: two single lines share
/// the space between them, what a paragraph can reach is left to the paragraph, and a
/// line keeps out of what the lines laid out before it have taken.
/// </summary>
public sealed class LineRooms
{
    // Space kept between a line and other text beside it, in line heights.
    private const double Gap = 0.5;
    // How far text stays from whatever ends its background (the edge of a button), in
    // line heights.
    private const double KeepClear = 0.35;
    // Width a line of text needs beyond its measured width.
    private const double Slack = 2;

    // Other text, with how far its own fill goes past it.
    private readonly record struct Obstacle(OcrRect Rect, int Block, bool Single, double PadX, double PadY);

    private readonly List<Obstacle> _obstacles = new();
    // The room of the lines that have needed one. The screenshot does not change, so it
    // holds for every pass.
    private readonly Dictionary<LineSpot, LineRoom> _found = new();
    // What the lines placed so far in this pass have taken beyond their own box, fill
    // included, by block.
    private readonly Dictionary<int, OcrRect> _claims = new();

    /// <summary>Forgets everything: the text is about to be laid out anew.</summary>
    public void Clear()
    {
        _obstacles.Clear();
        _found.Clear();
        _claims.Clear();
    }

    /// <summary>
    /// Starts a pass over the blocks (a first layout, or another language): what lines
    /// took on the last pass is free again.
    /// </summary>
    public void NewPass() => _claims.Clear();

    /// <summary>Text that is there: a single line, and how far its fill goes past it.</summary>
    public void AddLine(int block, OcrRect line, double padX, double padY) =>
        Add(new Obstacle(line, block, true, padX, padY));

    /// <summary>Text that is there: everything one line of a paragraph may come to cover.</summary>
    public void AddParagraphLine(int block, OcrRect reach, double padX, double padY) =>
        Add(new Obstacle(reach, block, false, padX, padY));

    private void Add(Obstacle obstacle)
    {
        _obstacles.Add(obstacle);
        _found.Clear();   // rooms found so far did not know of it
    }

    /// <summary>
    /// The room a single line has beside it: how far the background it stands on goes on
    /// with nothing in it, and not past what other text may need.
    /// </summary>
    public LineRoom Find(PixelGrid grid, LineSpot spot)
    {
        var line = spot.Line;
        double padX = spot.PadX, padY = spot.PadY;
        double scale = grid.PixelsPerUnit;
        double pixelLeft = grid.X(line.X), pixelRight = grid.X(line.X + line.Width);
        double pixelTop = grid.Y(line.Y), pixelBottom = grid.Y(line.Y + line.Height);
        double pixelHeight = pixelBottom - pixelTop;

        // The band the fill covers. Within the fill's own margin the last letter of a
        // tightly boxed line may still show; a rule or an edge there ends the room at once.
        int top = (int)Math.Floor(pixelTop - padY * scale);
        int bottom = (int)Math.Ceiling(pixelBottom + padY * scale) - 1;
        int margin = (int)Math.Ceiling(padX * scale);
        int limit = (int)Math.Max(2 * (pixelRight - pixelLeft), 16 * pixelHeight) + margin;
        int keep = (int)Math.Ceiling(Math.Max(3, pixelHeight * KeepClear));

        var right = Backdrop.Sideways(
            grid.Pixels, (int)Math.Ceiling(pixelRight), 1, top, bottom, spot.Colour, limit, margin);
        var left = Backdrop.Sideways(
            grid.Pixels, (int)Math.Floor(pixelLeft) - 1, -1, top, bottom, spot.Colour, limit, margin);

        double after = Usable(right, keep) / scale;
        double before = Usable(left, keep) / scale;
        bool centred = Backdrop.Centred(left, right) && !SharesLeftEdge(spot.Block, line);

        // Other text on these rows: its text reaches into this line's fill, or its fill
        // into this line's text.
        double gap = line.Height * Gap;
        foreach (var o in _obstacles)
        {
            if (o.Block == spot.Block) continue;
            double pad = Math.Max(padY, o.PadY);
            if (Bottom(o.Rect) + pad <= line.Y || o.Rect.Y - pad >= Bottom(line)) continue;

            // Apart by the gap, and by the two fills at least (the other line may be much taller).
            double apart = Math.Max(gap, padX + o.PadX);
            if (o.Rect.X >= Right(line))
                after = Math.Min(after, (o.Rect.X - Right(line) - apart) / (o.Single ? 2 : 1));
            else if (Right(o.Rect) <= line.X)
                before = Math.Min(before, (line.X - Right(o.Rect) - apart) / (o.Single ? 2 : 1));
            else
                before = after = 0;
        }

        before = Math.Max(0, before);
        after = Math.Max(0, after);
        // Centred text grows the same on both sides; anything else only onwards.
        if (centred) before = after = Math.Min(before, after);
        else before = 0;
        return new LineRoom(spot, before, after, centred);
    }

    /// <summary>
    /// Places the translation of a line: in its own box when it fits there (nothing is
    /// looked at then), otherwise in the room the line has. What it takes is kept out of
    /// reach of the lines placed after it in this pass.
    /// </summary>
    /// <param name="measure">Width of a string at full size. Must count a lone space.</param>
    /// <param name="rowHeight">Height of a row of text at full size.</param>
    /// <param name="minScale">Smallest font scale worth reading.</param>
    public LinePlacement Place(
        PixelGrid grid, LineSpot spot, string text,
        Func<string, double> measure, double rowHeight, double minScale, int maxRows = 3)
    {
        var line = spot.Line;
        _claims.Remove(spot.Block);   // placed again: what it took before is free

        double wanted = string.IsNullOrWhiteSpace(text) ? 0 : measure(text) + Slack;
        if (wanted <= line.Width)
            return new LinePlacement(line, new[] { text }, 1, false);

        if (!_found.TryGetValue(spot, out var room))
            _found[spot] = room = Find(grid, spot);

        double left = line.X, top = line.Y, height = line.Height;
        IReadOnlyList<string> rows = new[] { text };
        double scale = 1;

        var (before, after) = Beside(room);
        double width = Math.Min(wanted, line.Width + before + after);
        if (width > line.Width)
            left -= (width - line.Width) * before / (before + after);

        if (wanted > width)
        {
            var (above, below) = AboveAndBelow(grid, room, left, width, maxRows * rowHeight - line.Height);
            var fit = ParagraphLayout.Rows(
                text, width - Slack, line.Height + above + below, rowHeight, measure, minScale, maxRows);
            scale = fit.Scale;

            if (fit.Lines.Count > 1)
            {
                rows = fit.Lines;

                // The rows may not need all the width they could have had.
                double used = Math.Max(line.Width, rows.Max(measure) * scale + Slack);
                if (used < width)
                {
                    left += (width - used) * before / Math.Max(before + after, 1e-9);
                    width = used;
                }
                // Centred on where the original stood, or as near to that as the room
                // allows (a title has its room below, not above).
                height = Math.Max(line.Height, rows.Count * rowHeight * scale);
                top = Math.Clamp(line.Y - (height - line.Height) / 2,
                    line.Y - above, Math.Max(line.Y - above, Bottom(line) + below - height));
            }
        }

        if (width > line.Width || height > line.Height)
            _claims[spot.Block] = new OcrRect(
                left - spot.PadX, top - spot.PadY, width + 2 * spot.PadX, height + 2 * spot.PadY);
        return new LinePlacement(new OcrRect(left, top, width, height), rows, scale, room.Centred);
    }

    private static double Right(OcrRect r) => r.X + r.Width;
    private static double Bottom(OcrRect r) => r.Y + r.Height;

    private static double Usable(BackdropRun run, int keep) =>
        run.Ended ? Math.Max(0, run.Length - keep) : run.Length;

    /// <summary>
    /// True when other text nearby starts at the same left edge: a column of lines set
    /// flush left, where one of them happening to sit in the middle means nothing.
    /// </summary>
    private bool SharesLeftEdge(int block, OcrRect line)
    {
        double near = 4 * line.Height;
        foreach (var o in _obstacles)
        {
            if (o.Block != block && Math.Abs(o.Rect.X - line.X) <= 2
                && o.Rect.Y < Bottom(line) + near && Bottom(o.Rect) > line.Y - near)
                return true;
        }
        return false;
    }

    /// <summary>The room beside a line that no line before it in this pass has taken.</summary>
    private (double Before, double After) Beside(LineRoom room)
    {
        var line = room.Spot.Line;
        double before = room.Left, after = room.Right;
        double gap = line.Height * Gap;
        foreach (var c in _claims.Values)
        {
            if (Bottom(c) <= line.Y - room.Spot.PadY || c.Y >= Bottom(line) + room.Spot.PadY) continue;

            if (c.X >= Right(line)) after = Math.Min(after, c.X - Right(line) - gap);
            else if (Right(c) <= line.X) before = Math.Min(before, line.X - Right(c) - gap);
            else before = after = 0;
        }

        before = Math.Max(0, before);
        after = Math.Max(0, after);
        if (room.Centred) before = after = Math.Min(before, after);
        return (before, after);
    }

    /// <summary>How far rows of text may reach above and below a line.</summary>
    /// <param name="left">Where the rows would start.</param>
    /// <param name="width">How wide they may become.</param>
    /// <param name="wanted">The most that is of use on either side.</param>
    private (double Above, double Below) AboveAndBelow(
        PixelGrid grid, LineRoom room, double left, double width, double wanted)
    {
        if (wanted <= 0) return (0, 0);

        var line = room.Spot.Line;
        double scale = grid.PixelsPerUnit;
        double padX = room.Spot.PadX, padY = room.Spot.PadY;
        double from = left - padX, to = left + width + padX;

        // The columns the fill would cover, on the screenshot. The rows of the fill's own
        // margin come first: the foot of a letter may show there, and a rule across them
        // ends the room at once.
        int x0 = (int)Math.Floor(grid.X(from));
        int x1 = (int)Math.Ceiling(grid.X(to)) - 1;
        int margin = (int)Math.Ceiling(padY * scale);
        int limit = (int)Math.Ceiling(wanted * scale) + 2 * margin + 1;
        int keep = Math.Max(2, margin);

        var up = Backdrop.UpOrDown(
            grid.Pixels, (int)Math.Floor(grid.Y(line.Y)) - 1, -1, x0, x1, room.Spot.Colour, limit, margin);
        var down = Backdrop.UpOrDown(
            grid.Pixels, (int)Math.Ceiling(grid.Y(Bottom(line))), 1, x0, x1, room.Spot.Colour, limit, margin);
        // Beyond the margin, which the fill of the rows takes itself.
        double above = (Usable(up, keep) - margin) / scale;
        double below = (Usable(down, keep) - margin) / scale;

        // Other text over or under these columns: two single lines each get half the
        // space between them, and none of what a paragraph may reach. Never into the
        // other line's own fill.
        double middle = line.Y + line.Height / 2;
        foreach (var o in _obstacles)
        {
            if (o.Block == room.Spot.Block || Right(o.Rect) <= from || o.Rect.X >= to) continue;

            bool over = o.Rect.Y + o.Rect.Height / 2 < middle;
            double between = over ? line.Y - Bottom(o.Rect) : o.Rect.Y - Bottom(line);
            double free = o.Single
                ? Math.Min(between / 2, between - o.PadY) - padY
                : between - padY - Math.Max(padY, o.PadY);
            if (over) above = Math.Min(above, free);
            else below = Math.Min(below, free);
        }
        foreach (var c in _claims.Values)
        {
            if (Right(c) <= from || c.X >= to) continue;

            if (c.Y + c.Height / 2 < middle) above = Math.Min(above, line.Y - padY - Bottom(c) - 1);
            else below = Math.Min(below, c.Y - Bottom(line) - padY - 1);
        }
        return (Math.Clamp(above, 0, wanted), Math.Clamp(below, 0, wanted));
    }
}
