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

    // ------------------------------------------------------------------ rows

    [Fact]
    public void EvenRows_AreEquallyTallAndEvenlySpaced()
    {
        // Boxes that hug the ink: the middle line has no ascenders (starts lower, is
        // shorter), the last one no descenders.
        var rows = ParagraphLayout.EvenRows(new[] { (100.0, 15.0), (125.4, 11.6), (144.8, 11.5), (167.2, 15.0) });

        Assert.All(rows, r => Assert.Equal(15.0, r.Height));
        var steps = rows.Zip(rows.Skip(1), (a, b) => b.Top - a.Top).ToArray();
        Assert.All(steps, step => Assert.Equal(steps[0], step, 6));
        Assert.Equal(100.0, rows[0].Top, 0);
    }

    [Fact]
    public void EvenRows_AreNeverTallerThanThePitch()
    {
        // A headline set tighter than its own height: the boxes of the two lines overlap.
        var rows = ParagraphLayout.EvenRows(new[] { (149.0, 61.0), (205.0, 74.0) });

        Assert.Equal(56.0, rows[0].Height);
        Assert.True(rows[0].Top + rows[0].Height <= rows[1].Top);
    }

    [Fact]
    public void EvenRows_LeaveASingleLineAlone() =>
        Assert.Equal(new[] { (10.0, 20.0) }, ParagraphLayout.EvenRows(new[] { (10.0, 20.0) }));

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

    // ---- a single line that needs more room -------------------------------------

    // Rows 20 high at full size.
    private static ParagraphFit Rows(string text, double width, double height, double minScale = 0.4) =>
        ParagraphLayout.Rows(text, width, height, 20, Measure, minScale);

    [Fact]
    public void Rows_TextThatFits_StaysOnOneRow()
    {
        var fit = Rows("see you on Saturday", 200, 60);

        Assert.Equal(new[] { "see you on Saturday" }, fit.Lines);
        Assert.Equal(1, fit.Scale);
    }

    [Fact]
    public void Rows_TextTooWide_GoesOnTwoRowsAtFullSize_WhenThereIsHeight()
    {
        // 420 wide, in a bubble 260 wide with room for two rows.
        var fit = Rows("the network is working thank you so much!!", 260, 44);

        Assert.Equal(2, fit.Lines.Count);
        Assert.Equal(1, fit.Scale);
        Assert.Equal("the network is working thank you so much!!", string.Join(' ', fit.Lines));
        Assert.All(fit.Lines, l => Assert.True(Measure(l) <= 260));
        // Two rows of about the same length, not a full one and a leftover word.
        Assert.True(Math.Abs(Measure(fit.Lines[0]) - Measure(fit.Lines[1])) <= 60);
    }

    [Fact]
    public void Rows_WithoutHeight_ShrinksOnOneRow()
    {
        var fit = Rows("the network is working thank you so much!!", 260, 20);

        Assert.Single(fit.Lines);
        Assert.Equal(260.0 / 420, fit.Scale, 3);
    }

    [Fact]
    public void Rows_AreNotAdded_ForASlightlyLargerFont()
    {
        // One row needs 0.9; two rows fit only at 0.95 for lack of height. Not worth it.
        var fit = Rows("aaaaa bbbbb ccccc ddddd", 207, 38);

        Assert.Single(fit.Lines);
        Assert.Equal(0.9, fit.Scale, 3);
    }

    [Fact]
    public void Rows_UseAThird_WhenTwoStillShrinkTheText()
    {
        // 590 wide, rows of 200: two rows hold it at about 0.66, three at full size.
        var fit = Rows("aaaa bbbb cccc dddd eeee ffff gggg hhhh iiii jjjj kkkk llll", 200, 60);

        Assert.Equal(3, fit.Lines.Count);
        Assert.Equal(1, fit.Scale);
    }

    [Fact]
    public void Rows_WithLittleHeight_AreSmaller_ButLargerThanOneRow()
    {
        // Height for two rows only at 0.8; one row would need 0.5.
        var fit = Rows("aaaa bbbb cccc dddd eeee ffff gggg hhhh", 195, 32);

        Assert.Equal(2, fit.Lines.Count);
        Assert.Equal(0.8, fit.Scale, 3);
    }

    [Fact]
    public void Rows_OneLongWord_CannotBeSplit()
    {
        var fit = Rows("Donaudampfschifffahrtsgesellschaft", 170, 60);

        Assert.Single(fit.Lines);
        Assert.Equal(0.5, fit.Scale, 3);
    }

    [Fact]
    public void Rows_NeverGoBelowTheSmallestScale()
    {
        var fit = Rows("Donaudampfschifffahrtsgesellschaft", 34, 20, minScale: 0.4);

        Assert.Equal(0.4, fit.Scale);
    }

    [Fact]
    public void Rows_ChineseBreaksBetweenCharacters()
    {
        var fit = Rows("网络已经连上了，太谢谢你了！", 80, 44);

        Assert.Equal(2, fit.Lines.Count);
        Assert.Equal("网络已经连上了，太谢谢你了！", string.Concat(fit.Lines));
        Assert.DoesNotContain(fit.Lines, l => l.StartsWith('，') || l.StartsWith('！'));
    }

    [Fact]
    public void Rows_ThatShowAllOfTheText_BeatARowThatIsCutOff()
    {
        // 470 wide where there is 100: one row would be cut off even at the smallest scale.
        var fit = Rows("aaaaaaaaaaa bbbbbbbbbbb ccccccccccc ddddddddddd", 100, 20);

        Assert.Equal(2, fit.Lines.Count);
        Assert.InRange(fit.Scale, 0.4, 0.5);
        Assert.All(fit.Lines, l => Assert.True(Measure(l) * fit.Scale <= 100));
    }

    [Fact]
    public void Rows_ThatAreCutOffThemselves_AreNotTaken()
    {
        // The long word is too wide for a row whatever comes after it.
        var fit = Rows("Donaudampfschifffahrtsgesellschaftskapitaen ja", 100, 60);

        Assert.Single(fit.Lines);
        Assert.Equal(0.4, fit.Scale);
    }

    [Theory]
    [InlineData("the network is working thank you so much!!", 260)]
    [InlineData("请打开设置然后选择WinLens再试一次", 120)]              // no space where the scripts meet
    [InlineData("中文 中文 中文 中文 中文 中文", 90)]                    // Chinese with spaces of its own
    [InlineData("สวัสดี ครับ ขอบคุณ มาก ครับ", 120)]
    [InlineData("连上了。 Thank you so much", 110)]
    public void Wrap_ThenUnwrap_GivesTheTextBack(string text, double width)
    {
        var fit = Rows(text, width, 60);
        Assert.True(fit.Lines.Count > 1);

        string wrapped = ParagraphLayout.Wrap(text, fit.Lines);

        Assert.Equal(fit.Lines.Count - 1, wrapped.Count(c => c == '\n'));
        Assert.Equal(fit.Lines, wrapped.Split('\n').Select(row => row.TrimEnd(' ')));
        Assert.Equal(text, ParagraphLayout.Unwrap(wrapped));
    }

    [Fact]
    public void Unwrap_OfPartOfTheRows_IsThatPartOfTheText()
    {
        string text = "the network is working thank you so much!!";
        string wrapped = ParagraphLayout.Wrap(text, Rows(text, 260, 44).Lines);
        int from = wrapped.IndexOf("working", StringComparison.Ordinal);
        int to = wrapped.IndexOf("you", StringComparison.Ordinal) + 3;

        // As selected across the row break, with the line break Windows text boxes use.
        string selected = wrapped[from..to].Replace("\n", "\r\n");

        Assert.Contains('\n', selected);
        Assert.Equal("working thank you", ParagraphLayout.Unwrap(selected));
    }

    [Fact]
    public void Wrap_OfASingleRow_IsThatRow() =>
        Assert.Equal("one row", ParagraphLayout.Wrap("one row", new[] { "one row" }));
}
