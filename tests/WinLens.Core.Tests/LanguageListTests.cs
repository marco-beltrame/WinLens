using WinLens.Models;

namespace WinLens.Core.Tests;

public class LanguageListTests
{
    [Theory]
    [InlineData("en", "English")]
    [InlineData("EN", "English")]
    [InlineData("zh", "Chinese")]
    [InlineData("zh-TW", "Chinese (Traditional)")]
    [InlineData("zh-tw", "Chinese (Traditional)")]
    public void LabelFor_KnownCode_ReturnsLabel(string code, string expected) =>
        Assert.Equal(expected, LanguageList.LabelFor(code));

    [Fact]
    public void LabelFor_UnknownCode_ReturnsCodeUnchanged() =>
        Assert.Equal("xx-YY", LanguageList.LabelFor("xx-YY"));

    [Fact]
    public void Codes_AreUnique_IgnoringCase()
    {
        var dupes = LanguageList.Items
            .GroupBy(i => i.Code, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key);
        Assert.Empty(dupes);
    }

    [Fact]
    public void Items_AreSortedByLabel_SoTypeToJumpWorks()
    {
        var labels = LanguageList.Items.Select(i => i.Label).ToArray();
        Assert.Equal(labels.OrderBy(l => l, StringComparer.OrdinalIgnoreCase), labels);
    }
}
