using WinLens.Services;

namespace WinLens.Core.Tests;

public class ParagraphGrouperTests
{
    private const string Prose =
        "The translator works best when it can read a whole sentence at once, because the " +
        "meaning of a word often depends on what follows it several words later, and a " +
        "sentence cut at the edge of the screen loses exactly that.";

    private const string German =
        "Die Bundesregierung hat am Mittwoch beschlossen, dass die neuen Regeln für alle " +
        "Unternehmen ab dem kommenden Jahr gelten sollen und dass die Behörden mehr Personal " +
        "für die Kontrolle der Vorschriften erhalten werden.";

    private const string Chinese =
        "这个应用可以翻译屏幕上的任何文字，不管它是在游戏里还是在图片里，只要按下快捷键就可以看到翻译结果，" +
        "而且翻译会直接显示在原来的位置上";

    private static string[] Texts(IEnumerable<OcrParagraph> paragraphs) =>
        paragraphs.Select(p => p.Text).ToArray();

    private static List<OcrParagraph> Group(ScreenText screen) => ParagraphGrouper.Group(screen.Lines);

    // ------------------------------------------------------------------ joins

    [Fact]
    public void WrappedParagraph_BecomesOneParagraphWithItsOriginalText()
    {
        var screen = new ScreenText();
        var wrapped = screen.Paragraph(Prose, x: 100, y: 100, width: 420);
        Assert.True(wrapped.Count >= 4);

        var p = Assert.Single(Group(screen));
        Assert.Equal(Prose, p.Text);
        Assert.Equal(wrapped, p.Lines.Select(l => l.Text));
        Assert.Equal(ParagraphAlignment.Left, p.Alignment);
    }

    [Fact]
    public void ParagraphBounds_CoverEveryLine()
    {
        var screen = new ScreenText();
        screen.Paragraph(Prose, x: 100, y: 100, width: 420);

        var p = Assert.Single(Group(screen));
        foreach (var line in p.Lines)
        {
            Assert.True(line.Rect.X >= p.Bounds.X && line.Rect.Y >= p.Bounds.Y);
            Assert.True(line.Rect.X + line.Rect.Width <= p.Bounds.X + p.Bounds.Width + 1e-9);
            Assert.True(line.Rect.Y + line.Rect.Height <= p.Bounds.Y + p.Bounds.Height + 1e-9);
        }
    }

    [Fact]
    public void TwoParagraphs_SeparatedBySpacing_StayTwo()
    {
        var screen = new ScreenText();
        var first = screen.Paragraph(Prose, x: 100, y: 100, width: 420);
        // A paragraph gap: one empty line.
        screen.Paragraph(German, x: 100, y: 100 + (first.Count + 1) * 1.4 * 16, width: 420);

        Assert.Equal(new[] { Prose, German }, Texts(Group(screen)));
    }

    [Fact]
    public void SentenceEndingAtLineEnd_StartsANewParagraph()
    {
        var screen = new ScreenText();
        screen.Rows(new[]
        {
            "The first sentence happens to end right here.",
            "Then a second one starts on the line below it",
            "and carries on over the line break as usual.",
        }, x: 50, y: 50, pitch: 1.4);

        Assert.Equal(new[]
        {
            "The first sentence happens to end right here.",
            "Then a second one starts on the line below it and carries on over the line break as usual.",
        }, Texts(Group(screen)));
    }

    [Fact]
    public void AbbreviationAtLineEnd_DoesNotSplitTheSentence()
    {
        var screen = new ScreenText();
        screen.Rows(new[]
        {
            "The box weighs about three kilograms, i.e. approx.",
            "six and a half pounds with the packaging removed",
        }, x: 50, y: 50, pitch: 1.4);

        Assert.Single(Group(screen));
    }

    [Fact]
    public void CenteredHeadline_OverSmallerText_IsJoinedAndCentered()
    {
        // The case from the bug report: big thumbnail text "Not a" / "planet".
        var screen = new ScreenText();
        screen.Paragraph(Prose, x: 40, y: 400, width: 420, em: 14);
        screen.Paragraph(German, x: 40, y: 600, width: 420, em: 14);
        screen.Paragraph("Not a planet", x: 500, y: 100, width: 150, em: 48, lineHeight: 1.1, align: "center");

        var headline = Assert.Single(Group(screen), p => p.Text.StartsWith("Not"));
        Assert.Equal("Not a planet", headline.Text);
        Assert.Equal(2, headline.Lines.Count);
        Assert.Equal(ParagraphAlignment.Center, headline.Alignment);
    }

    [Fact]
    public void AllCapsCaption_SetTight_IsJoined()
    {
        var screen = new ScreenText();
        screen.Paragraph(Prose, x: 40, y: 400, width: 420, em: 14);
        screen.Paragraph(German, x: 40, y: 600, width: 420, em: 14);
        screen.Paragraph("ONE DOES NOT SIMPLY WALK INTO MORDOR", x: 500, y: 60, width: 520, em: 40,
            lineHeight: 1.1, align: "center");

        Assert.Contains("ONE DOES NOT SIMPLY WALK INTO MORDOR", Texts(Group(screen)));
    }

    [Fact]
    public void LinesStartingWithCapitals_AreJoinedInRunningText()
    {
        // German nouns put a capital at the start of many wrapped lines.
        var screen = new ScreenText();
        var wrapped = screen.Paragraph(German, x: 100, y: 100, width: 380);
        Assert.Contains(wrapped.Skip(1), l => char.IsUpper(l[0]));

        Assert.Equal(new[] { German }, Texts(Group(screen)));
    }

    [Fact]
    public void ChineseParagraph_IsJoinedWithoutSpaces()
    {
        var screen = new ScreenText();
        var wrapped = screen.Paragraph(Chinese, x: 100, y: 100, width: 330, lineHeight: 1.8, lang: "zh-Hans-CN");
        Assert.True(wrapped.Count >= 3);

        Assert.Equal(new[] { Chinese }, Texts(Group(screen)));
    }

    [Fact]
    public void WordHyphenatedAtLineEnd_IsPutBackTogether()
    {
        var screen = new ScreenText();
        screen.Rows(new[]
        {
            "This sentence was typeset with hyphen-",
            "ation switched on, as a book would be.",
        }, x: 50, y: 50, pitch: 1.3);

        Assert.Equal("This sentence was typeset with hyphenation switched on, as a book would be.",
            Assert.Single(Group(screen)).Text);
    }

    [Fact]
    public void IndentedFirstLines_SplitParagraphsSetWithoutSpacing()
    {
        // Book and LaTeX style: no gap between paragraphs, the first line is set in.
        var screen = new ScreenText();
        var first = screen.Paragraph(Prose, x: 100, y: 100, width: 420, indent: 24);
        screen.Paragraph(German, x: 100, y: 100 + first.Count * 1.4 * 16, width: 420, indent: 24);

        Assert.Equal(new[] { Prose, German }, Texts(Group(screen)));
    }

    [Fact]
    public void BulletedItems_KeepTheirWrappedLinesAndStayApart()
    {
        var screen = new ScreenText();
        screen.Add("• Press the shortcut anywhere in Windows and the text", 60, 100);
        screen.Add("on the monitor under the cursor is translated", 74, 100 + 22.4);
        screen.Add("• Drag a rectangle to translate only that part of the", 60, 100 + 2 * 22.4);
        screen.Add("screen and leave the rest alone", 74, 100 + 3 * 22.4);

        Assert.Equal(new[]
        {
            "• Press the shortcut anywhere in Windows and the text on the monitor under the cursor is translated",
            "• Drag a rectangle to translate only that part of the screen and leave the rest alone",
        }, Texts(Group(screen)));
    }

    // ------------------------------------------------------------------ refusals

    [Fact]
    public void MenuItems_StayApart()
    {
        var items = new[] { "Open containing folder", "Copy path", "Rename", "Move to trash", "Properties" };
        var screen = new ScreenText();
        screen.Rows(items, x: 30, y: 30, em: 14, pitch: 1.7);

        Assert.Equal(items, Texts(Group(screen)));
    }

    [Fact]
    public void TightListOfCapitalisedItems_StaysApart()
    {
        var items = new[]
        {
            "Show hidden files and folders",
            "Hide extensions for known file types",
            "Launch folder windows in a separate process",
            "Restore previous folder windows at logon",
        };
        var screen = new ScreenText();
        screen.Rows(items, x: 30, y: 30, pitch: 1.4);

        Assert.Equal(items, Texts(Group(screen)));
    }

    [Fact]
    public void FileNames_StayApart()
    {
        var items = new[] { "readme", "license", "changelog", "notes", "todo" };
        var screen = new ScreenText();
        screen.Rows(items, x: 30, y: 30, pitch: 1.5);

        Assert.Equal(items, Texts(Group(screen)));
    }

    [Fact]
    public void ShortChatMessages_StayApart_WhileALongOneIsJoined()
    {
        // Messages of one author in a chat sit at the pitch of wrapped lines.
        var screen = new ScreenText();
        screen.Add("ok", 80, 100);
        var wrapped = screen.Paragraph(
            "so i was thinking we could go to the park tomorrow if the weather is nice enough",
            x: 80, y: 100 + 22.4, width: 400);
        Assert.Equal(2, wrapped.Count);
        screen.Add("what do you think", 80, 100 + 3 * 22.4);
        screen.Add("lol", 80, 100 + 4 * 22.4);
        screen.Add("yeah that was funny", 80, 100 + 5 * 22.4);
        screen.Add("idk", 80, 100 + 6 * 22.4);

        Assert.Equal(new[]
        {
            "ok",
            "so i was thinking we could go to the park tomorrow if the weather is nice enough",
            "what do you think",
            "lol",
            "yeah that was funny",
            "idk",
        }, Texts(Group(screen)));
    }

    [Fact]
    public void CodeLines_StayApart()
    {
        var items = new[]
        {
            "var total = compute(first, second);",
            "if (total > limit) {",
            "return total - limit;",
            "}",
        };
        var screen = new ScreenText();
        screen.Rows(items, x: 30, y: 30, pitch: 1.35);

        Assert.Equal(items, Texts(Group(screen)));
    }

    [Fact]
    public void LabelEndingInColon_IsNotJoinedToWhatFollows()
    {
        var screen = new ScreenText();
        screen.Rows(new[] { "You will need the following things:", "a screwdriver and some patience" },
            x: 30, y: 30, pitch: 1.4);

        Assert.Equal(2, Group(screen).Count);
    }

    [Fact]
    public void NumberedSteps_StayApart()
    {
        var items = new[]
        {
            "1. Download the installer from the releases page",
            "2. Run it and follow the steps on the screen",
            "3. Pick the language you want to translate into",
        };
        var screen = new ScreenText();
        screen.Rows(items, x: 30, y: 30, pitch: 1.4);

        Assert.Equal(items, Texts(Group(screen)));
    }

    [Fact]
    public void HeadingAboveParagraph_IsNotPartOfIt()
    {
        var screen = new ScreenText();
        screen.Add("Getting started with the overlay", 100, 60, em: 24);
        screen.Paragraph(Prose, x: 100, y: 100, width: 420);

        Assert.Equal(new[] { "Getting started with the overlay", Prose }, Texts(Group(screen)));
    }

    [Fact]
    public void TwoColumnsSideBySide_AreNotMixed()
    {
        var screen = new ScreenText();
        screen.Paragraph(Prose, x: 40, y: 100, width: 360);
        screen.Paragraph(German, x: 440, y: 100, width: 360);

        Assert.Equal(new[] { Prose, German }, Texts(Group(screen)).OrderBy(t => t == German));
    }

    [Fact]
    public void ChineseMenu_StaysApart()
    {
        var items = new[] { "首页", "推荐视频", "热门", "频道管理", "我的消息" };
        var screen = new ScreenText();
        screen.Rows(items, x: 30, y: 30, pitch: 1.8, lang: "zh-Hans-CN");

        Assert.Equal(items, Texts(Group(screen)));
    }

    [Fact]
    public void ChineseHeadlines_OfDifferentLengths_StayApart()
    {
        var items = new[]
        {
            "新款手机下月发布价格曝光",
            "本周末全国大部分地区将迎来降温天气",
            "世界杯预选赛今晚开战",
            "专家建议每天步行三十分钟",
        };
        var screen = new ScreenText();
        screen.Rows(items, x: 30, y: 30, pitch: 1.9, lang: "zh-Hans-CN");

        Assert.Equal(items, Texts(Group(screen)));
    }

    [Fact]
    public void DifferentScripts_AreNotJoined()
    {
        var screen = new ScreenText();
        screen.Add("the settings for this page are described", 30, 30);
        screen.Add("在下面的列表里可以找到所有的选项和说明", 30, 30 + 22.4, lang: "zh-Hans-CN");

        Assert.Equal(2, Group(screen).Count);
    }

    // ------------------------------------------------------------------ invariants

    [Fact]
    public void SingleLine_IsAParagraphOfItsOwn()
    {
        var line = new OcrLine("Hello", new OcrRect(1, 2, 30, 10), "en-US");
        var p = Assert.Single(ParagraphGrouper.Group(new[] { line }));
        Assert.Equal("Hello", p.Text);
        Assert.Same(line, Assert.Single(p.Lines));
        Assert.Equal(line.Rect, p.Bounds);
        Assert.Equal("en-US", p.Language);
    }

    [Fact]
    public void NoLines_NoParagraphs() =>
        Assert.Empty(ParagraphGrouper.Group(Array.Empty<OcrLine>()));

    [Fact]
    public void EveryLine_EndsUpInExactlyOneParagraph()
    {
        var random = new Random(7);
        for (int run = 0; run < 200; run++)
        {
            var lines = new List<OcrLine>();
            int count = random.Next(1, 40);
            for (int i = 0; i < count; i++)
            {
                const string alphabet = "abc DEF, gh. 你好ij-。:;?12 ()•";
                var text = new string(Enumerable.Range(0, random.Next(0, 30))
                    .Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
                lines.Add(new OcrLine(text,
                    new OcrRect(random.Next(0, 6) * 40, random.Next(0, 30) * 9, random.Next(0, 400), random.Next(0, 30)),
                    random.Next(3) == 0 ? null : "en-US"));
            }

            var grouped = ParagraphGrouper.Group(lines);
            var seen = grouped.SelectMany(p => p.Lines).ToList();
            Assert.Equal(lines.Count, seen.Count);
            Assert.Equal(lines.Count, seen.Distinct(ReferenceEqualityComparer.Instance).Count());
            Assert.All(grouped, p => Assert.NotEmpty(p.Lines));
        }
    }
}
