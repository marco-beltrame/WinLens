using WinLens.Services;

namespace WinLens.Core.Tests;

public class OcrPostProcessorTests
{
    private static OcrLine L(string t, double x, double y, double w, double h, string? lang = null) =>
        new(t, new OcrRect(x, y, w, h), lang);

    // --- MergeLineFragments (characterization of existing behaviour) ---

    [Fact]
    public void Merge_FewerThanTwo_ReturnsInput()
    {
        var one = new List<OcrLine> { L("a", 0, 0, 10, 10) };
        Assert.Same(one, OcrPostProcessor.MergeLineFragments(one));
    }

    [Fact]
    public void Merge_AdjacentFragmentsOnSameLine_JoinWithSpaceAndUnionRect()
    {
        var r = OcrPostProcessor.MergeLineFragments(new()
        {
            L("world", 12, 0, 40, 10, "en-US"),
            L("I", 0, 0, 10, 11, "en-US"),
        });
        var m = Assert.Single(r);
        Assert.Equal("I world", m.Text);
        Assert.Equal(new OcrRect(0, 0, 52, 11), m.Rect);
        Assert.Equal("en-US", m.Language);
    }

    [Fact]
    public void Merge_FarApartOrDifferentLine_NotMerged()
    {
        var r = OcrPostProcessor.MergeLineFragments(new()
        {
            L("left", 0, 0, 10, 10),
            L("far", 500, 0, 10, 10),     // gap > 1.2 * height
            L("below", 0, 30, 10, 10),    // different line
        });
        Assert.Equal(3, r.Count);
    }

    // --- DedupOverlaps ---

    [Fact]
    public void Dedup_OverlappingBlocks_KeepsLongerText()
    {
        var r = OcrPostProcessor.DedupOverlaps(new()
        {
            L("hi", 0, 0, 50, 10),
            L("hello there", 2, 0, 50, 10),
            L("elsewhere", 0, 100, 50, 10),
        });
        Assert.Equal(new[] { "hello there", "elsewhere" }, r.Select(x => x.Text));
    }

    [Fact]
    public void OverlapRatio_ZeroAreaIsZero_Disjoint_IsZero()
    {
        Assert.Equal(0, OcrPostProcessor.OverlapRatio(new(0, 0, 0, 0), new(0, 0, 5, 5)));
        Assert.Equal(0, OcrPostProcessor.OverlapRatio(new(0, 0, 5, 5), new(10, 10, 5, 5)));
        Assert.Equal(1, OcrPostProcessor.OverlapRatio(new(0, 0, 10, 10), new(2, 2, 4, 4)));
    }

    // --- IsAppropriate ---

    [Theory]
    [InlineData("Hello world", OcrScript.Latin, true)]
    [InlineData("你好世界", OcrScript.Latin, false)]
    [InlineData("你好世界", OcrScript.Chinese, true)]
    [InlineData("Hello world", OcrScript.Chinese, false)]
    [InlineData("12:30 - 4.5%", OcrScript.Latin, true)]    // no letters: always kept
    [InlineData("12:30 - 4.5%", OcrScript.Korean, true)]
    [InlineData("OK 确定", OcrScript.Latin, true)]          // tie goes to the engine
    public void IsAppropriate_Basic(string text, OcrScript s, bool expected) =>
        Assert.Equal(expected, OcrPostProcessor.IsAppropriate(text, s));

    [Theory]
    [InlineData("Привет мир", OcrScript.Latin, false)]      // Cyrillic is not Latin
    [InlineData("Привет мир", OcrScript.Cyrillic, true)]
    [InlineData("Hello мир, 123!", OcrScript.Latin, true)]  // 5 vs 3
    [InlineData("Hello мир, 123!", OcrScript.Cyrillic, false)]
    [InlineData("Καλημέρα", OcrScript.Greek, true)]
    [InlineData("Καλημέρα", OcrScript.Latin, false)]
    [InlineData("مرحبا", OcrScript.Arabic, true)]
    [InlineData("שלום", OcrScript.Hebrew, true)]
    [InlineData("नमस्ते", OcrScript.Devanagari, true)]
    [InlineData("สวัสดี", OcrScript.Thai, true)]
    [InlineData("안녕하세요", OcrScript.Korean, true)]
    [InlineData("안녕하세요", OcrScript.Japanese, true)]
    [InlineData("こんにちは漢字", OcrScript.Japanese, true)]
    [InlineData("こんにちは漢字", OcrScript.Chinese, true)]
    [InlineData("汉字", OcrScript.Japanese, true)]
    public void IsAppropriate_Scripts(string text, OcrScript s, bool expected) =>
        Assert.Equal(expected, OcrPostProcessor.IsAppropriate(text, s));

    // --- ScriptOfTag / bucketing ---

    [Theory]
    [InlineData("en-US", OcrScript.Latin)]
    [InlineData("it-IT", OcrScript.Latin)]
    [InlineData("ru-RU", OcrScript.Cyrillic)]
    [InlineData("uk", OcrScript.Cyrillic)]
    [InlineData("sr-Cyrl-RS", OcrScript.Cyrillic)]
    [InlineData("sr-Latn-RS", OcrScript.Latin)]
    [InlineData("el-GR", OcrScript.Greek)]
    [InlineData("ar-SA", OcrScript.Arabic)]
    [InlineData("he-IL", OcrScript.Hebrew)]
    [InlineData("hi-IN", OcrScript.Devanagari)]
    [InlineData("th-TH", OcrScript.Thai)]
    [InlineData("zh-Hans-CN", OcrScript.Chinese)]
    [InlineData("zh-TW", OcrScript.Chinese)]
    [InlineData("JA-jp", OcrScript.Japanese)]
    [InlineData("ko-KR", OcrScript.Korean)]
    [InlineData("xx-YY", OcrScript.Latin)]
    [InlineData("", OcrScript.Latin)]
    [InlineData(null, OcrScript.Latin)]
    public void ScriptOfTag_MapsTags(string? tag, OcrScript expected) =>
        Assert.Equal(expected, OcrPostProcessor.ScriptOfTag(tag));

    [Fact]
    public void Buckets_EnglishPlusRussian_AreTwo_And_CjkAreDistinct()
    {
        var tags = new[] { "en-US", "ru-RU", "ja-JP", "ko-KR", "zh-CN", "fr-FR" };
        var buckets = tags.Select(OcrPostProcessor.ScriptOfTag).Distinct().ToList();
        Assert.Equal(5, buckets.Count); // fr shares Latin with en
        Assert.Equal(2, new[] { "en-US", "ru-RU" }.Select(OcrPostProcessor.ScriptOfTag).Distinct().Count());
    }

    [Fact]
    public void ScriptCount_CoversAllScripts() =>
        Assert.Equal(Enum.GetValues<OcrScript>().Length, OcrPostProcessor.ScriptCount);
}
