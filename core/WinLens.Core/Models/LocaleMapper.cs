using System;
using System.Collections.Generic;

namespace WinLens.Models;

/// <summary>
/// Maps a Windows/BCP-47 locale name to a <see cref="LanguageList"/> code. The list uses
/// Google's legacy codes (iw, jw, tl, no), so modern tags need aliasing.
/// </summary>
public static class LocaleMapper
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["he"] = "iw",
        ["jv"] = "jw",
        ["fil"] = "tl",
        ["nb"] = "no",
        ["nn"] = "no",
    };

    /// <summary>Always returns a code present in the list; unknown or empty input gives "en".</summary>
    public static string ToTargetCode(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale)) return SettingsRules.DefaultTarget;
        locale = locale.Trim();

        if (locale.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            bool traditional =
                locale.Contains("Hant", StringComparison.OrdinalIgnoreCase) ||
                locale.EndsWith("-TW", StringComparison.OrdinalIgnoreCase) ||
                locale.EndsWith("-HK", StringComparison.OrdinalIgnoreCase) ||
                locale.EndsWith("-MO", StringComparison.OrdinalIgnoreCase);
            return traditional ? "zh-TW" : "zh";
        }

        var baseTag = locale.Split('-', '_')[0];
        if (Aliases.TryGetValue(baseTag, out var alias)) baseTag = alias;

        foreach (var (code, _) in LanguageList.Items)
            if (string.Equals(code, baseTag, StringComparison.OrdinalIgnoreCase))
                return code;
        return SettingsRules.DefaultTarget;
    }
}
