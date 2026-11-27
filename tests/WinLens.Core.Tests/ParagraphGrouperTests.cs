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
        "而且翻译会直接显示在原来的位置上。";

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

    // ------------------------------------------------------------------ lists that look like text

    [Fact]
    public void ChineseMenu_WithItemsOfSimilarLength_StaysApart()
    {
        // Every item is about as wide as the widest, so each looks like a full line.
        var items = new[] { "在新窗口中打开", "固定到快速访问", "授予访问权限", "还原以前的版本", "包含到库中", "发送到" };
        var screen = new ScreenText();
        screen.Rows(items, x: 30, y: 30, em: 12, pitch: 1.9, lang: "zh-Hans-CN");

        Assert.Equal(items, Texts(Group(screen)));
    }

    [Fact]
    public void JapaneseMenu_StaysApart()
    {
        var items = new[] { "新しいウィンドウで開く", "クイックアクセスにピン留め", "以前のバージョンの復元", "ライブラリに追加", "送る" };
        var screen = new ScreenText();
        screen.Rows(items, x: 30, y: 30, em: 12, pitch: 1.9, lang: "ja-JP");

        Assert.Equal(items, Texts(Group(screen)));
    }

    [Fact]
    public void ChineseHeadlines_OfTheSameLength_StayApart()
    {
        // News sites write their headlines to fill the column. Some have a comma in them.
        var items = new[]
        {
            "新款手机下月发布，价格曝光引关注",
            "全国大部分地区将迎来降温天气",
            "世界杯预选赛今晚开战球迷期待",
            "专家建议每天步行三十分钟以上",
        };
        var screen = new ScreenText();
        screen.Rows(items, x: 30, y: 30, em: 14, pitch: 1.9, lang: "zh-Hans-CN");

        Assert.Equal(items, Texts(Group(screen)));
    }

    [Fact]
    public void ChineseHeadline_WrappedOnItsOwn_IsJoined()
    {
        var screen = new ScreenText();
        var wrapped = screen.Paragraph("新一代高速列车开始试运行，最高时速可达四百五十公里",
            x: 30, y: 30, width: 260, lineHeight: 1.5, lang: "zh-Hans-CN");
        Assert.Equal(2, wrapped.Count);
        // The next headline sits further down than the lines of this one.
        screen.Add("多地发布寒潮预警", 30, 30 + 4 * 16, lang: "zh-Hans-CN");

        Assert.Equal(new[] { "新一代高速列车开始试运行，最高时速可达四百五十公里", "多地发布寒潮预警" },
            Texts(Group(screen)));
    }

    [Fact]
    public void ArabicMenu_StaysApart()
    {
        var items = new[] { "فتح في نافذة جديدة", "تثبيت في الوصول السريع", "استعادة الإصدارات السابقة", "إضافة إلى المكتبة الآن" };

        Assert.Equal(items, Texts(ParagraphGrouper.Group(RightAligned(items, em: 14, pitch: 1.9))));
    }

    [Fact]
    public void ArabicParagraph_IsJoinedAndRightAligned()
    {
        // The first two lines are nearly the same width, so their middles line up as well
        // as their right edges do.
        var lines = RightAligned(new[]
        {
            "هذا النص العربي يلتف على عدة أسطر في",
            "الفقرة الواحدة ويجب أن يترجم كله معا",
            "بدون أي انقطاع هنا.",
        }, em: 16, pitch: 1.5, widths: new[] { 300.0, 292.0, 150.0 });

        var paragraph = Assert.Single(ParagraphGrouper.Group(lines));
        Assert.Equal(ParagraphAlignment.Right, paragraph.Alignment);
    }

    private static List<OcrLine> RightAligned(string[] texts, double em, double pitch, double[]? widths = null)
    {
        const double right = 500;
        return texts.Select((t, i) =>
        {
            double w = widths?[i] ?? t.Length * 0.45 * em;
            return new OcrLine(t, new OcrRect(right - w, 40 + i * pitch * em, w, 0.92 * em), "ar");
        }).ToList();
    }

    [Fact]
    public void LowercaseChat_WithoutAWrappedMessage_StaysApart()
    {
        // The longest message is as wide as the column gets, which is all a full line is.
        // The short ones around it show that these are messages.
        var items = new[] { "ok", "so what time do we meet tomorrow", "around noon i guess", "cool see you then" };
        var screen = new ScreenText();
        screen.Rows(items, x: 80, y: 100, pitch: 1.4);

        Assert.Equal(items, Texts(Group(screen)));
    }

    [Fact]
    public void QuestionAndAnswer_InLowercase_StayApart()
    {
        var screen = new ScreenText();
        screen.Rows(new[] { "did you see what he just did?", "yeah that was insane" }, x: 80, y: 100, pitch: 1.4);

        Assert.Equal(2, Group(screen).Count);
    }

    [Fact]
    public void ChatWithNames_KeepsMessagesApart_AndJoinsTheWrappedOne()
    {
        // A stream chat: 13 px text in a 300 px column, messages only a little further
        // apart than the lines of one message.
        var messages = new[]
        {
            "moobot: please keep the chat friendly and respectful to everyone who is here today",
            "pogchamp_lover: that clutch was insane",
            "streamelements: thanks for the follow",
            "kappa123: LUL",
        };
        var screen = new ScreenText();
        double y = 10;
        foreach (var message in messages)
        {
            var wrapped = screen.Paragraph(message, x: 10, y: y, width: 300, em: 13, lineHeight: 1.23);
            y += wrapped.Count * 1.23 * 13 + 0.62 * 13;
        }

        Assert.Equal(messages, Texts(Group(screen)));
    }

    [Fact]
    public void LogLines_StayApart()
    {
        var items = new[]
        {
            "fa32d37 feat: translate wrapped lines as one paragraph",
            "8c55774 fix: keep the overlay on the right monitor",
            "b1e09aa docs: explain the settings file",
            "d4c2f10 chore: bump the test sdk",
        };
        var screen = new ScreenText();
        screen.Rows(items, x: 10, y: 10, em: 14, pitch: 1.3);

        Assert.Equal(items, Texts(Group(screen)));
    }

    [Theory]
    // Configuration: the two longest lines are as wide as the block.
    [InlineData("url = \"postgres://backup@db.internal:5432/app\"|schedule = \"0 3 * * *\" # every night at 03:00|bucket = \"acme-backups\"|retention_days = 30")]
    // A script without semicolons or braces.
    [InlineData("total = compute(first, second)|result = total * factor|print(result)|save(result, path)")]
    [InlineData("cd src/app|npm install --save-dev typescript|npm run build|git status")]
    [InlineData("\"name\": \"winlens\",|\"description\": \"reads the text on your screen\",|\"license\": \"MIT\"")]
    public void CodeAndConfiguration_StayApart(string block)
    {
        var items = block.Split('|');
        var screen = new ScreenText();
        screen.Rows(items, x: 30, y: 30, em: 14, pitch: 1.4);

        Assert.Equal(items, Texts(Group(screen)));
    }

    [Fact]
    public void SentenceThatNamesCode_IsStillJoined()
    {
        var screen = new ScreenText();
        screen.Rows(new[]
        {
            "Call render() again after every change, or set needs_redraw and",
            "wait for the next frame to pick it up.",
        }, x: 50, y: 50, pitch: 1.4);

        Assert.Single(Group(screen));
    }

    [Theory]
    [InlineData(14)]
    [InlineData(16)]
    public void TitleOverItsSubtitle_StaysApart(double subtitleSize)
    {
        // Two lines on their own, the first as wide as the second: nothing here says
        // "wrapped" except that, and a card or a notification looks the same.
        var screen = new ScreenText();
        screen.Add("Your package has been shipped today", 40, 40, em: 16);
        screen.Add("Track it with the link in the email", 40, 40 + 1.4 * 16, em: subtitleSize);

        Assert.Equal(2, Group(screen).Count);
    }

    [Fact]
    public void ListOfCapitalisedEntries_KeepsAWrappedOneTogether()
    {
        // Objectives in a quest log: entries start with a capital, so the line that does
        // not is the rest of the one above, short lines around it or not.
        var screen = new ScreenText();
        screen.Rows(new[]
        {
            "Find the missing lens in the flooded cellar",
            "beneath the old customs house",
            "Speak to Captain Ines at the harbour",
            "Light the beacon",
        }, x: 40, y: 40, em: 14, pitch: 1.5);

        Assert.Equal(new[]
        {
            "Find the missing lens in the flooded cellar beneath the old customs house",
            "Speak to Captain Ines at the harbour",
            "Light the beacon",
        }, Texts(Group(screen)));
    }

    [Fact]
    public void OptionsOfSimilarLength_StayApart()
    {
        // Most of these are within a word of the widest, and several follow each other.
        var items = new[]
        {
            "Display file size information in folder tips",
            "Display the full path in the title bar",
            "Hide empty drives",
            "Hide extensions for all known file types",
            "Launch folder windows in a separate process",
            "Restore previous folder windows at logon",
            "Show encrypted or compressed NTFS files in color",
            "Show pop-up description for folder and desktop items",
            "Show preview handlers in preview pane",
        };
        var screen = new ScreenText();
        screen.Rows(items, x: 40, y: 40, em: 12, pitch: 1.45);

        Assert.Equal(items, Texts(Group(screen)));
    }

    [Fact]
    public void TableOfContents_InTitleCase_StaysApart()
    {
        var items = new[]
        {
            "Getting Started With The Overlay",
            "Choosing Where The Text Comes From",
            "Picking The Translation Engine",
            "Running Your Own Model At Home",
        };
        var screen = new ScreenText();
        screen.Rows(items, x: 30, y: 30, pitch: 1.5);

        Assert.Equal(items, Texts(Group(screen)));
    }

    [Fact]
    public void ParagraphBesideAPicture_IsJoined_ThoughTheTextBelowIsWider()
    {
        // Text flowing around an infobox: the same column is narrower up here.
        const string second =
            "It was discovered in 1930 and was at once hailed as the ninth planet, but its " +
            "status was questioned for decades as more objects of its kind were found beyond it.";
        var screen = new ScreenText();
        var narrow = screen.Paragraph(Prose, x: 100, y: 100, width: 330);
        var wide = screen.Paragraph(second, x: 100, y: 100 + narrow.Count * 22.4 + 12, width: 680);
        Assert.True(narrow.Count >= 4 && wide.Count >= 2);

        Assert.Equal(new[] { Prose, second }, Texts(Group(screen)));
    }

    // ------------------------------------------------------------------ joining the text

    [Theory]
    [InlineData("typeset with hyphen-", "ation switched on", "typeset with hyphenation switched on")]
    [InlineData("Die Vor-", "und Nachteile sind klar", "Die Vor- und Nachteile sind klar")]
    [InlineData("pre-", "and post-war housing", "pre- and post-war housing")]
    [InlineData("Er lebt in Rheinland-", "Pfalz und arbeitet dort", "Er lebt in Rheinland-Pfalz und arbeitet dort")]
    [InlineData("the result -", "as expected", "the result - as expected")]
    [InlineData("ภาษาไทยไม่มีช่องว่าง", "ระหว่างคำ", "ภาษาไทยไม่มีช่องว่างระหว่างคำ")]
    [InlineData("翻译会直接显示", "在原来的位置上", "翻译会直接显示在原来的位置上")]
    public void JoinLines_PutsTheSentenceBackTogether(string first, string second, string expected) =>
        Assert.Equal(expected, ParagraphGrouper.JoinLines(new[]
        {
            new OcrLine(first, default, null), new OcrLine(second, default, null),
        }));

    [Fact]
    public void ManyLines_AreGroupedTheSameAsFew()
    {
        // The search for the line below only looks nearby; the answer must not depend on
        // how much else is on the screen.
        var one = new ScreenText();
        one.Paragraph(Prose, x: 100, y: 100, width: 420);
        var expected = Texts(Group(one));

        var many = new ScreenText();
        for (int column = 0; column < 4; column++)
            for (int block = 0; block < 60; block++)
                many.Paragraph(Prose, x: 100 + column * 500, y: 100 + block * 160, width: 420);

        var grouped = Texts(Group(many));
        Assert.Equal(240, grouped.Length);
        Assert.All(grouped, text => Assert.Equal(expected[0], text));
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
