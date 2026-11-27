using WinLens.Layout;
using WinLens.Services;

namespace WinLens.Core.Tests;

public class LineRoomsTests
{
    private const int White = 0xFFFFFF, Page = 0xF4F5F7, Ink = 0x202020, Blue = 0x2060C0;

    // Every character is 10 wide at full size, rows are 20 high, the fill reaches 3 past
    // the text sideways and 2 above and below.
    private static double Measure(string s) => s.Length * 10;
    private const double RowHeight = 20, PadX = 3, PadY = 2;

    /// <summary>A screen in the making: lines of text on a picture, as the OCR would report them.</summary>
    private sealed class Screen
    {
        private readonly List<(OcrRect Rect, double PadY)> _lines = new();
        private readonly double _scale, _offsetX, _offsetY;

        public Screen(int width, int height, int colour, double scale = 1, double offsetX = 0, double offsetY = 0)
        {
            Picture = new Picture(width, height, colour);
            (_scale, _offsetX, _offsetY) = (scale, offsetX, offsetY);
        }

        public Picture Picture { get; }
        public LineRooms Rooms { get; } = new();
        public PixelGrid Grid => new(Picture, _scale, _offsetX, _offsetY);

        /// <summary>Paints in layout units, wherever those fall on the picture.</summary>
        public Screen Paint(double x, double y, double width, double height, int colour)
        {
            int left = (int)Math.Round(x * _scale - _offsetX), top = (int)Math.Round(y * _scale - _offsetY);
            Picture.Paint(left, top, (int)Math.Round(width * _scale), (int)Math.Round(height * _scale), colour);
            return this;
        }

        /// <summary>A single line of text: its ink on the picture, and its box.</summary>
        public int Line(double x, double y, double width, double height = 16, double padY = PadY)
        {
            Paint(x, y, width, height, Ink);
            _lines.Add((new OcrRect(x, y, width, height), padY));
            Rooms.AddLine(_lines.Count - 1, _lines[^1].Rect, PadX, padY);
            return _lines.Count - 1;
        }

        /// <summary>A line of a paragraph, which may come to cover <paramref name="reach"/>.</summary>
        public int ParagraphLine(OcrRect line, OcrRect reach)
        {
            Paint(line.X, line.Y, line.Width, line.Height, Ink);
            _lines.Add((line, PadY));
            Rooms.AddParagraphLine(_lines.Count - 1, reach, PadX, PadY);
            return _lines.Count - 1;
        }

        public LineSpot Spot(int line, int colour = White) =>
            new(line, _lines[line].Rect, PadX, _lines[line].PadY, colour);

        public LineRoom Room(int line, int colour = White) => Rooms.Find(Grid, Spot(line, colour));

        public LinePlacement Place(int line, string text, int colour = White) =>
            Rooms.Place(Grid, Spot(line, colour), text, Measure, RowHeight, 0.4);
    }

    private static string Text(int characters) => new('x', characters);

    private static double Right(OcrRect r) => r.X + r.Width;
    private static double Bottom(OcrRect r) => r.Y + r.Height;

    [Fact]
    public void TextThatFits_KeepsTheBoxOfTheLine()
    {
        var screen = new Screen(600, 100, White);
        int line = screen.Line(100, 40, 80);

        var placed = screen.Place(line, Text(6));

        Assert.Equal(new OcrRect(100, 40, 80, 16), placed.Box);
        Assert.Equal(new[] { Text(6) }, placed.Rows);
        Assert.Equal(1, placed.Scale);
    }

    [Fact]
    public void LongerText_RunsOnOverTheFreeBackground()
    {
        var screen = new Screen(600, 100, White);
        int line = screen.Line(20, 40, 80);

        var placed = screen.Place(line, Text(20));

        // 200 wide and the 2 of slack, starting where the original started.
        Assert.Equal(new OcrRect(20, 40, 202, 16), placed.Box);
        Assert.Single(placed.Rows);
        Assert.Equal(1, placed.Scale);
    }

    [Fact]
    public void LongerText_StopsShortOfTheEdgeOfItsCard()
    {
        // A white card from 50 to 300 on a page one shade darker.
        var screen = new Screen(600, 100, Page).Paint(50, 0, 250, 100, White);
        int line = screen.Line(60, 40, 80);

        var placed = screen.Place(line, Text(40));

        Assert.Equal(60, placed.Box.X);
        Assert.InRange(Right(placed.Box), 285, 295);   // a little clear of the edge at 300
        Assert.True(placed.Scale < 1);                  // and the rest by a smaller font
    }

    [Fact]
    public void LongerText_StopsAtAnythingInItsWay()
    {
        // An icon the OCR knows nothing about, 60 to the right of the line.
        var screen = new Screen(600, 100, White).Paint(160, 42, 12, 12, Blue);
        int line = screen.Line(20, 40, 80);

        var placed = screen.Place(line, Text(40));

        Assert.InRange(Right(placed.Box), 145, 155);
    }

    [Fact]
    public void OnABusyBackground_TheLineKeepsItsBox()
    {
        var screen = new Screen(600, 100, White);
        for (int x = 0; x < 600; x += 4) screen.Paint(x, 0, 2, 100, 0xD0E0F0);   // stripes
        int line = screen.Line(20, 40, 80);

        var placed = screen.Place(line, Text(20));

        Assert.Equal(new OcrRect(20, 40, 80, 16), placed.Box);
    }

    [Fact]
    public void LabelOfAButton_GrowsOnBothSides_AndStaysInTheMiddle()
    {
        // A button from 100 to 260; its label in the middle of it.
        var screen = new Screen(600, 100, White).Paint(100, 28, 160, 40, Blue);
        int line = screen.Line(150, 40, 60);

        var room = screen.Room(line, Blue);
        var placed = screen.Place(line, Text(11), Blue);

        Assert.True(room.Centred);
        Assert.Equal(112, placed.Box.Width);
        Assert.Equal(180, placed.Box.X + placed.Box.Width / 2);
        Assert.True(placed.Box.X > 100 && Right(placed.Box) < 260);
    }

    [Fact]
    public void LabelOfAButton_NeverLeavesTheButton()
    {
        var screen = new Screen(600, 100, White).Paint(100, 28, 160, 40, Blue);
        int line = screen.Line(150, 40, 60);

        var placed = screen.Place(line, Text(30), Blue);

        Assert.True(placed.Box.X - PadX > 100 && Right(placed.Box) + PadX < 260);
        Assert.Equal(180, placed.Box.X + placed.Box.Width / 2, 3);
    }

    [Fact]
    public void LineInAColumnOfLines_IsNotTakenForCentred()
    {
        // Three menu entries flush left in a panel; the middle one happens to be as far
        // from the right edge as from the left.
        var screen = new Screen(600, 140, Page).Paint(100, 0, 200, 140, White);
        screen.Line(130, 20, 60);
        int line = screen.Line(130, 50, 140);
        screen.Line(130, 80, 90);

        var room = screen.Room(line);

        Assert.False(room.Centred);
        Assert.Equal(0, room.Left);
    }

    [Fact]
    public void TwoLabelsSideBySide_ShareTheSpaceBetweenThem()
    {
        var screen = new Screen(600, 100, White);
        int first = screen.Line(20, 40, 60);
        int second = screen.Line(180, 40, 60);

        var a = screen.Place(first, Text(40));
        var b = screen.Room(second);

        // 100 between them; each may take half of what is left after a gap of 8.
        Assert.Equal(80 + 46, Right(a.Box));
        Assert.Equal(0, b.Left);   // not centred: it only grows onwards
    }

    [Fact]
    public void Line_LeavesAParagraphWhatItMayReach()
    {
        var screen = new Screen(600, 100, White);
        int line = screen.Line(20, 40, 60);
        // The short last line of a paragraph; its text may come to start at 200.
        screen.ParagraphLine(new OcrRect(260, 40, 100, 16), new OcrRect(200, 40, 300, 16));

        var placed = screen.Place(line, Text(40));

        Assert.Equal(200 - 8, Right(placed.Box));
    }

    [Fact]
    public void TextTooLongForItsBubble_GoesOnTwoRows()
    {
        // A chat bubble sized to its one line of text.
        var screen = new Screen(600, 120, Page).Paint(100, 30, 260, 56, White);
        int line = screen.Line(116, 50, 228);

        var placed = screen.Place(line, "the network is working thank you so much!!");

        Assert.Equal(2, placed.Rows.Count);
        Assert.Equal(1, placed.Scale);
        Assert.Equal(40, placed.Box.Height);
        // Centred on the original line, and inside the bubble with its fill.
        Assert.Equal(58, placed.Box.Y + placed.Box.Height / 2);
        Assert.True(placed.Box.Y - PadY > 30 && Bottom(placed.Box) + PadY < 86);
        Assert.True(placed.Box.X - PadX > 100 && Right(placed.Box) + PadX < 360);
    }

    [Fact]
    public void Title_WithRoomOnlyBelow_GrowsDownwards()
    {
        // A rule right above the title, nothing below it.
        var screen = new Screen(600, 160, White).Paint(0, 34, 600, 1, 0xC0C0C0).Paint(250, 0, 350, 160, Page);
        int line = screen.Line(20, 40, 200);

        var placed = screen.Place(line, "aaaa bbbb cccc dddd eeee ffff gggg hhhh");

        Assert.Equal(2, placed.Rows.Count);
        Assert.Equal(1, placed.Scale);
        Assert.InRange(placed.Box.Y, 37, 40);                // no higher than the rule allows
        Assert.Equal(placed.Box.Y + 40, Bottom(placed.Box));
    }

    [Fact]
    public void LinesAboveEachOther_ShareTheSpaceBetweenThem()
    {
        // Two long entries of a list in a narrow panel, 24 apart.
        var screen = new Screen(600, 200, Page).Paint(10, 0, 240, 200, White);
        int first = screen.Line(20, 60, 200);
        int second = screen.Line(20, 100, 200);

        var a = screen.Place(first, "aaaa bbbb cccc dddd eeee ffff gggg hhhh");
        var b = screen.Place(second, "aaaa bbbb cccc dddd eeee ffff gggg hhhh");

        Assert.Equal(2, a.Rows.Count);
        Assert.Equal(2, b.Rows.Count);
        Assert.True(Bottom(a.Box) + PadY <= b.Box.Y - PadY);
    }

    [Fact]
    public void LinePlacedLater_KeepsOutOfWhatAnEarlierOneTook()
    {
        // The first line cannot grow sideways (a wall right after it), so it takes rows
        // below itself — where the second line, lower and to the left, would run on to.
        var screen = new Screen(600, 140, White).Paint(190, 0, 6, 60, Blue);
        int first = screen.Line(100, 40, 80);
        int second = screen.Line(20, 66, 60);

        var a = screen.Place(first, "aaaa bbbb cccc dd");
        var b = screen.Place(second, Text(30));

        Assert.Equal(2, a.Rows.Count);
        Assert.True(Bottom(a.Box) > 66);                       // it did come down beside the second
        Assert.True(Right(b.Box) + PadX <= a.Box.X - PadX);    // which stops before it
    }

    [Fact]
    public void ANewPass_GivesTheRoomBack()
    {
        var screen = new Screen(600, 140, White).Paint(190, 0, 6, 60, Blue);
        int first = screen.Line(100, 40, 80);
        int second = screen.Line(20, 66, 60);
        screen.Place(first, "aaaa bbbb cccc dd");

        // Another language: the first line's translation is short this time.
        screen.Rooms.NewPass();
        screen.Place(first, Text(5));
        var b = screen.Place(second, Text(30));

        Assert.Equal(20 + 302, Right(b.Box));
    }

    [Theory]
    [InlineData(1.5, 45, 30)]     // a region of the screen: the picture starts inside the window
    [InlineData(1.5, -30, -15)]
    [InlineData(1.25, 10, 5)]
    [InlineData(2, 0, 0)]
    public void OnAScaledScreen_TheRoomIsTheSame(double scale, double offsetX, double offsetY)
    {
        // The card of LongerText_StopsShortOfTheEdgeOfItsCard on a screen scaled up.
        var plain = new Screen(600, 100, Page).Paint(50, 0, 250, 100, White);
        var scaled = new Screen(1200, 200, Page, scale, offsetX, offsetY).Paint(50, 0, 250, 100, White);
        int a = plain.Line(60, 40, 80), b = scaled.Line(60, 40, 80);

        var expected = plain.Place(a, Text(40));
        var placed = scaled.Place(b, Text(40));

        Assert.Equal(expected.Box.X, placed.Box.X);
        Assert.Equal(Right(expected.Box), Right(placed.Box), 0);   // to the pixel
        Assert.Equal(expected.Rows, placed.Rows);
    }

    [Fact]
    public void AtTheEdgeOfTheScreenshot_TheTextStaysInside()
    {
        var screen = new Screen(300, 60, White);
        int line = screen.Line(200, 20, 80);

        var placed = screen.Place(line, Text(30));

        Assert.True(Right(placed.Box) <= 300);
        Assert.True(placed.Box.Y >= 0 && Bottom(placed.Box) <= 60);
    }

    [Fact]
    public void TextThatFits_IsPlacedWithoutLookingAtTheScreen()
    {
        var screen = new Screen(600, 100, White);
        int line = screen.Line(100, 40, 80);

        screen.Place(line, Text(6));
        Assert.Equal(0, screen.Picture.Reads);

        screen.Place(line, Text(20));
        Assert.True(screen.Picture.Reads > 0);
    }

    [Fact]
    public void CellOfATable_DoesNotGrowAcrossTheRuleNextToIt()
    {
        // The rule stands two units from the text, inside what the fill covers anyway,
        // and the cell beyond it is empty.
        var screen = new Screen(600, 100, White).Paint(102, 0, 1, 100, 0xDDDDDD);
        int line = screen.Line(20, 40, 80);

        var placed = screen.Place(line, Text(20));

        Assert.Equal(new OcrRect(20, 40, 80, 16), placed.Box);
        Assert.Equal(0.4, placed.Scale);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void RowOfATable_DoesNotGrowAcrossTheRuleUnderIt(double scale)
    {
        // A rule one unit under the text; plenty of room beneath it, none above.
        var screen = new Screen(600, 300, White, scale).Paint(0, 57, 400, 1, 0xDDDDDD).Paint(0, 0, 400, 36, Page);
        int line = screen.Line(20, 40, 80);
        screen.Paint(104, 30, 2, 40, Blue);   // and no room beside it

        var placed = screen.Place(line, "aaaa bbbb cccc dddd eeee ffff gggg hhhh");

        Assert.True(Bottom(placed.Box) <= 56, $"box ends at {Bottom(placed.Box)}");
        Assert.True(placed.Box.Y >= 40 - 2);
    }

    [Fact]
    public void TallerLineJustBelow_KeepsItsFill()
    {
        // The small line could run on over the top of the large one's fill, which is
        // drawn later and would cover the bottom of its text.
        var screen = new Screen(600, 120, White);
        int small = screen.Line(20, 40, 60, 8);
        int large = screen.Line(140, 50, 200, 30, padY: 4);

        var placed = screen.Place(small, Text(30));

        // Half of what lies between them, less the gap: it stops well before the large line.
        Assert.True(Right(placed.Box) + PadX <= 140 - PadX, $"box ends at {Right(placed.Box)}");
        Assert.Equal(new OcrRect(140, 50, 200, 30), screen.Place(large, Text(10)).Box);
    }

    [Fact]
    public void SameLinePlacedTwiceInAPass_GetsTheSameRoom()
    {
        var screen = new Screen(600, 100, White);
        int line = screen.Line(20, 40, 80);

        var first = screen.Place(line, Text(20));
        var second = screen.Place(line, Text(20));

        Assert.Equal(new OcrRect(20, 40, 202, 16), first.Box);
        Assert.Equal(first.Box, second.Box);
        Assert.Equal(1, second.Scale);
    }

    [Fact]
    public void Placement_SaysWhenTheTextIsCentred()
    {
        var screen = new Screen(600, 100, Page).Paint(200, 30, 200, 36, Blue);
        int label = screen.Line(260, 40, 80);

        Assert.True(screen.Place(label, Text(11), Blue).Centred);
        Assert.False(screen.Place(label, Text(6), Blue).Centred);   // it fits: as it always was
    }
}
