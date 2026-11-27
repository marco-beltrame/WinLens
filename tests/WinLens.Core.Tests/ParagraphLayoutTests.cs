using WinLens.Layout;
using WinLens.Services;

namespace WinLens.Core.Tests;

public class ParagraphLayoutTests
{
    // Every character is 10 wide, so widths can be read off the strings.
    private static double Measure(string s) => s.Length * 10;

    private static double[] Same(int lines, double width) => Enumerable.Repeat(width, lines).ToArray();

    private static ParagraphFit Flow(string text, double[] capacities, double[]? shares = null, double minScale = 0.4) =>
        ParagraphLayout.Flow(text, capacities, shares ?? capacities, Measure, minScale);

    private static string Rejoin(ParagraphFit fit) =>
        string.Join(' ', fit.Lines.Where(l => l.Length > 0));

    [Fact]
    public void TextThatFits_KeepsFullSize_AndEveryWord()
    {
        const string text = "one two three four five six seven eight";
        var fit = Flow(text, Same(3, 200));

        Assert.Equal(1, fit.Scale);
        Assert.Equal(3, fit.Lines.Count);
        Assert.Equal(text, Rejoin(fit));
        Assert.All(fit.Lines, l => Assert.True(Measure(l) <= 200));
    }

    [Fact]
    public void ShortTranslation_StillReachesTheLastLine()
    {
        // Would fit on one line; the original had two, so both get a share.
        var fit = Flow("aaaa bbbb cccc dddd", Same(2, 400));

        Assert.Equal(new[] { "aaaa bbbb", "cccc dddd" }, fit.Lines);
        Assert.Equal(1, fit.Scale);
    }

    [Fact]
    public void TextIsSpreadLikeTheOriginal_ShortLastLineStaysShort()
    {
        // Original: two full lines and a last line a quarter as long.
        var fit = Flow("aa bb cc dd ee ff gg hh ii", Same(3, 200), shares: new double[] { 200, 200, 50 });

        Assert.Equal(new[] { "aa bb cc dd", "ee ff gg hh", "ii" }, fit.Lines);
    }

    [Fact]
    public void LongerTranslation_ShrinksOnlyAsMuchAsNeeded()
    {
        // 59 characters = 590 wide; two lines of 200 hold 400.
        const string text = "aaaa bbbb cccc dddd eeee ffff gggg hhhh iiii jjjj kkkk llll";
        var fit = Flow(text, Same(2, 200));

        Assert.InRange(fit.Scale, 0.6, 0.72);
        Assert.Equal(text, Rejoin(fit));
        Assert.All(fit.Lines, l => Assert.True(Measure(l) * fit.Scale <= 200 + 1e-6));
        // No larger scale would have worked: at +5% some line overflows however it is split.
        Assert.True(Measure(text) * (fit.Scale + 0.05) > 400);
    }

    [Fact]
    public void SpareRoomOnAShortLine_IsUsedBeforeShrinking()
    {
        // The original's last line was short, but the line may grow to the full width.
        var fit = Flow("aaaa bbbb cccc dddd eeee ffff", Same(2, 150), shares: new double[] { 150, 40 });

        Assert.Equal(1, fit.Scale);
        Assert.Equal(new[] { "aaaa bbbb cccc", "dddd eeee ffff" }, fit.Lines);
    }

    [Fact]
    public void EachLineKeepsItsOwnCapacity()
    {
        // A first line that starts further right (an indent) has less room.
        var fit = Flow("aaaa bbbb cccc dddd eeee", new double[] { 90, 150 });

        Assert.Equal(new[] { "aaaa bbbb", "cccc dddd eeee" }, fit.Lines);
    }

    [Fact]
    public void WordLongerThanTheLine_Shrinks_ThenOverflowsAtTheMinimum()
    {
        var fit = Flow("supercalifragilistic", Same(2, 100), minScale: 0.5);
        Assert.Equal(0.5, fit.Scale, 3);
        Assert.Equal(new[] { "supercalifragilistic", "" }, fit.Lines);

        // Far too much text: everything is kept, the last line takes the excess.
        var crowded = Flow("aaaaaaaaaa bbbbbbbbbb cccccccccc dddddddddd eeeeeeeeee", Same(2, 110), minScale: 0.5);
        Assert.Equal(0.5, crowded.Scale, 3);
        Assert.Equal("aaaaaaaaaa bbbbbbbbbb", crowded.Lines[0]);
        Assert.Equal("cccccccccc dddddddddd eeeeeeeeee", crowded.Lines[1]);
    }

    [Fact]
    public void EmptyText_GivesEmptyLines()
    {
        var fit = Flow("   ", Same(3, 100));
        Assert.Equal(3, fit.Lines.Count);
        Assert.All(fit.Lines, l => Assert.Equal("", l));
        Assert.Equal(1, fit.Scale);
    }

    [Fact]
    public void FewerWordsThanLines_LeavesTheRestEmpty()
    {
        var fit = Flow("Planet", Same(2, 100));
        Assert.Equal(new[] { "Planet", "" }, fit.Lines);
    }

    [Fact]
    public void Chinese_BreaksBetweenCharacters_ButNotBeforeClosingPunctuation()
    {
        // 14 characters over two lines of seven: the comma may not start the second line.
        var fit = Flow("你好世界欢迎你，来到这里看看", Same(2, 80), shares: Same(2, 70));

        Assert.Equal(2, fit.Lines.Count);
        Assert.Equal("你好世界欢迎你，来到这里看看", string.Concat(fit.Lines));
        Assert.DoesNotContain(fit.Lines, l => l.StartsWith('，'));
        Assert.EndsWith("，", fit.Lines[0]);
    }

    [Theory]
    [InlineData("你好world", new[] { "你", "好", "world" })]
    [InlineData("「你好」，世界", new[] { "「你", "好」，", "世", "界" })]
    [InlineData("Hello, world!", new[] { "Hello,", "world!" })]
    [InlineData("  spaced   out  ", new[] { "spaced", "out" })]
    // Thai: only where a syllable certainly starts or ends, never inside one.
    [InlineData("สวัสดี", new[] { "สวัสดี" })]
    [InlineData("ภาษาไทยง่ายนิดเดียว", new[] { "ภาษา", "ไทยง่ายนิด", "เดียว" })]
    [InlineData("จะไปแล้ว", new[] { "จะ", "ไป", "แล้ว" })]
    // Khmer marks its word boundaries with zero-width spaces.
    [InlineData("សួស្តី\u200Bពិភពលោក", new[] { "សួស្តី", "ពិភពលោក" })]
    public void Tokenize_SplitsWhereALineMayBreak(string text, string[] expected) =>
        Assert.Equal(expected, ParagraphLayout.Tokenize(text).Select(t => t.Text));

    [Fact]
    public void Tokenize_SurvivesAnUnpairedSurrogate()
    {
        // OCR and translation services do return them now and then.
        var tokens = ParagraphLayout.Tokenize("abc \uD83D def");
        Assert.Equal(3, tokens.Count);

        var fit = ParagraphLayout.Flow("abc \uD83D def", new double[] { 50, 50 }, new double[] { 50, 50 }, s => s.Length * 10, 0.5);
        Assert.Equal("abc \uD83D def", string.Join(" ", fit.Lines.Where(l => l.Length > 0)));
    }

    [Fact]
    public void Tokenize_RemembersWhereSpacesWere()
    {
        var tokens = ParagraphLayout.Tokenize("a b你 好");
        Assert.Equal(new[] { false, true, false, true }, tokens.Select(t => t.SpaceBefore));
    }

    [Fact]
    public void MismatchedArguments_Throw()
    {
        Assert.Throws<ArgumentException>(() => ParagraphLayout.Flow("a", Array.Empty<double>(), Array.Empty<double>(), Measure, 0.5));
        Assert.Throws<ArgumentException>(() => ParagraphLayout.Flow("a", new double[] { 1 }, new double[] { 1, 2 }, Measure, 0.5));
    }

    [Fact]
    public void AnyText_IsPlacedCompletely_InOrder_AtAScaleThatFits()
    {
        var random = new Random(11);
        for (int run = 0; run < 300; run++)
        {
            int lineCount = random.Next(1, 7);
            var caps = Enumerable.Range(0, lineCount).Select(_ => (double)random.Next(40, 400)).ToArray();
            var shares = caps.Select(c => c * (0.2 + 0.8 * random.NextDouble())).ToArray();
            var words = Enumerable.Range(0, random.Next(0, 40))
                .Select(_ => new string('x', random.Next(1, 12))).ToArray();
            var text = string.Join(' ', words);

            var fit = ParagraphLayout.Flow(text, caps, shares, Measure, 0.3);

            Assert.Equal(lineCount, fit.Lines.Count);
            Assert.Equal(text, Rejoin(fit));
            Assert.InRange(fit.Scale, 0.3 - 1e-9, 1);
            if (fit.Scale > 0.3 + 1e-9)
            {
                for (int k = 0; k < lineCount; k++)
                    Assert.True(Measure(fit.Lines[k]) * fit.Scale <= caps[k] + 1e-6);
            }
        }
    }

    // ------------------------------------------------------------------ reach

    [Fact]
    public void Reach_IsTheParagraphsWidth_WhenNothingElseIsOnItsRows()
    {
        var lines = new[] { new OcrRect(100, 100, 300, 16), new OcrRect(100, 122, 120, 16) };
        var above = new OcrRect(100, 60, 500, 16);

        var reach = ParagraphLayout.Reach(lines, new[] { above }, gap: 8);

        Assert.Equal(new[] { (100.0, 400.0), (100.0, 400.0) }, reach);
    }

    [Fact]
    public void Reach_StopsBeforeOtherTextOnTheSameRow()
    {
        // A chat message whose short last line is followed by the time it was sent.
        var lines = new[] { new OcrRect(100, 100, 300, 16), new OcrRect(100, 122, 120, 16) };
        var time = new OcrRect(360, 124, 36, 12);

        var reach = ParagraphLayout.Reach(lines, new[] { time }, gap: 8);

        Assert.Equal((100.0, 400.0), reach[0]);
        Assert.Equal((100.0, 352.0), reach[1]);
    }

    [Fact]
    public void Reach_NeverCutsIntoTheLineItself()
    {
        var lines = new[] { new OcrRect(100, 100, 300, 16), new OcrRect(100, 122, 120, 16) };
        var touching = new OcrRect(222, 122, 40, 16);   // closer than the gap
        var before = new OcrRect(40, 122, 40, 16);      // left of the paragraph: nothing to do with it
        var duplicate = new OcrRect(110, 122, 100, 16); // overlaps the line: OCR read it twice

        var reach = ParagraphLayout.Reach(lines, new[] { touching, before, duplicate }, gap: 8);

        Assert.Equal((100.0, 220.0), reach[1]);
    }

    [Fact]
    public void Reach_OnTheLeft_ForRightAlignedText()
    {
        var lines = new[] { new OcrRect(100, 100, 300, 16), new OcrRect(280, 122, 120, 16) };
        var label = new OcrRect(110, 122, 50, 16);

        var reach = ParagraphLayout.Reach(lines, new[] { label }, gap: 8);

        Assert.Equal((168.0, 400.0), reach[1]);
    }
}
