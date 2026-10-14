using System;
using System.Collections.Generic;

namespace WinLens.Models;

/// <summary>
/// Repairs values read from settings.json (hand-edited, older versions, null from a
/// partial write) so the rest of the app never sees a null or an unknown language.
/// </summary>
public static class SettingsRules
{
    public const string DefaultTarget = "en";
    public const string DefaultSource = "auto";

    /// <summary>Canonical LanguageList code for <paramref name="code"/>, or "en" if null/unknown.</summary>
    public static string NormalizeTarget(string? code)
    {
        if (!string.IsNullOrWhiteSpace(code))
            foreach (var (c, _) in LanguageList.Items)
                if (string.Equals(c, code.Trim(), StringComparison.OrdinalIgnoreCase))
                    return c;
        return DefaultTarget;
    }

    /// <summary>OCR source tag; null/blank becomes "auto" (tags are validated against installed engines elsewhere).</summary>
    public static string NormalizeSource(string? source) =>
        string.IsNullOrWhiteSpace(source) ? DefaultSource : source.Trim();

    /// <summary>Known, de-duplicated recents in their original order, capped at <paramref name="max"/>.</summary>
    public static List<string> NormalizeRecent(IEnumerable<string?>? recents, int max)
    {
        var result = new List<string>();
        if (recents == null) return result;
        foreach (var r in recents)
        {
            if (result.Count >= max) break;
            if (string.IsNullOrWhiteSpace(r)) continue;
            var canonical = NormalizeTarget(r);
            // NormalizeTarget falls back to "en" for unknown codes; only keep real matches.
            if (!string.Equals(canonical, r.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            if (!result.Contains(canonical, StringComparer.OrdinalIgnoreCase))
                result.Add(canonical);
        }
        return result;
    }

    /// <summary>Whether a stored shortcut is something WinLens would accept today.</summary>
    public static bool IsHotkeyUsable(uint modifiers, string? keyName) =>
        HotkeyRules.Validate(modifiers, keyName) == HotkeyCheck.Ok;
}
