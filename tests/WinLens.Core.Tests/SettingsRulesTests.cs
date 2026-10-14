using WinLens.Models;

namespace WinLens.Core.Tests;

public class HotkeyRulesTests
{
    private const uint Alt = 1, Ctrl = 2, Shift = 4, Win = 8;

    [Theory]
    [InlineData(Ctrl | Alt, "T")]
    [InlineData(Ctrl | Shift, "R")]
    [InlineData(Win | Shift, "S")]
    [InlineData(Ctrl | Shift, "C")]
    [InlineData(Alt, "F5")]
    [InlineData(Ctrl | 0x4000, "Q")] // MOD_NOREPEAT is ignored
    public void Validate_Accepts(uint mods, string key) =>
        Assert.Equal(HotkeyCheck.Ok, HotkeyRules.Validate(mods, key));

    [Theory]
    [InlineData(0u, "A")]
    [InlineData(Shift, "A")]
    public void Validate_RequiresCtrlAltOrWin(uint mods, string key) =>
        Assert.Equal(HotkeyCheck.NeedsModifier, HotkeyRules.Validate(mods, key));

    [Theory]
    [InlineData(Ctrl, "C")]
    [InlineData(Ctrl, "v")]
    [InlineData(Ctrl, "Z")]
    [InlineData(Alt, "F4")]
    [InlineData(Alt, "Tab")]
    [InlineData(Win, "L")]
    [InlineData(Ctrl | Alt, "Delete")]
    public void Validate_RejectsSystemCombos(uint mods, string key) =>
        Assert.Equal(HotkeyCheck.Reserved, HotkeyRules.Validate(mods, key));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("None")]
    [InlineData("LeftCtrl")]
    [InlineData("ImeProcessed")]
    [InlineData("DeadCharProcessed")]
    public void Validate_RejectsNonTriggerKeys(string? key) =>
        Assert.Equal(HotkeyCheck.BadKey, HotkeyRules.Validate(Ctrl, key));
}

public class SettingsRulesTests
{
    [Theory]
    [InlineData(null, "en")]
    [InlineData("", "en")]
    [InlineData("xx", "en")]
    [InlineData("IT", "it")]
    [InlineData("zh-tw", "zh-TW")]
    public void NormalizeTarget(string? input, string expected) =>
        Assert.Equal(expected, SettingsRules.NormalizeTarget(input));

    [Fact]
    public void NormalizeSource_NullBecomesAuto() =>
        Assert.Equal("auto", SettingsRules.NormalizeSource(null));

    [Fact]
    public void NormalizeRecent_NullGivesEmpty() =>
        Assert.Empty(SettingsRules.NormalizeRecent(null, 4));

    [Fact]
    public void NormalizeRecent_DropsNullUnknownDuplicatesAndCaps()
    {
        var r = SettingsRules.NormalizeRecent(
            new string?[] { "it", null, "xx", "IT", "fr", "de", "es", "ja" }, 4);
        Assert.Equal(new[] { "it", "fr", "de", "es" }, r);
    }

    [Fact]
    public void IsHotkeyUsable_ChecksRules()
    {
        Assert.True(SettingsRules.IsHotkeyUsable(3, "T"));
        Assert.False(SettingsRules.IsHotkeyUsable(4, "T"));
        Assert.False(SettingsRules.IsHotkeyUsable(3, null));
    }
}

public class LocaleMapperTests
{
    [Theory]
    [InlineData("zh-Hant", "zh-TW")]
    [InlineData("zh-Hant-TW", "zh-TW")]
    [InlineData("zh-TW", "zh-TW")]
    [InlineData("zh-HK", "zh-TW")]
    [InlineData("zh-Hans", "zh")]
    [InlineData("zh-CN", "zh")]
    [InlineData("en-GB", "en")]
    [InlineData("it-IT", "it")]
    [InlineData("he-IL", "iw")]
    [InlineData("jv-ID", "jw")]
    [InlineData("fil-PH", "tl")]
    [InlineData("nb-NO", "no")]
    [InlineData("nn-NO", "no")]
    [InlineData("tlh", "en")]
    [InlineData("", "en")]
    [InlineData(null, "en")]
    public void ToTargetCode(string? locale, string expected) =>
        Assert.Equal(expected, LocaleMapper.ToTargetCode(locale));
}
