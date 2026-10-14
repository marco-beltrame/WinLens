using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using WinLens.Models;

namespace WinLens.Views;

/// <summary>
/// Shared population for the target-language combos (settings + overlay). Items are
/// ComboBoxItems with Tag = language code, Content = label. When the Recent-languages
/// feature is on and there are recents, they appear as a pinned group at the top,
/// separated from the full alphabetical list. Type-to-jump still works for the rest.
/// </summary>
internal static class LanguagePicker
{
    private const int MaxRecent = 4;

    public static void Populate(ComboBox box, Style itemStyle, string currentCode, IEnumerable<string>? recentCodes)
    {
        box.Items.Clear();
        ComboBoxItem? toSelect = null;

        ComboBoxItem Make(string code)
        {
            var item = new ComboBoxItem { Content = LanguageList.LabelFor(code), Tag = code, Style = itemStyle };
            if (toSelect == null && string.Equals(code, currentCode, StringComparison.OrdinalIgnoreCase))
                toSelect = item;
            return item;
        }

        if (Features.RecentLanguages && recentCodes != null)
        {
            var recents = recentCodes
                .Where(c => LanguageList.Items.Any(i => string.Equals(i.Code, c, StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxRecent)
                .ToList();

            if (recents.Count > 0)
            {
                foreach (var c in recents) box.Items.Add(Make(c));
                // Disabled + unfocusable so arrow keys and type-to-jump skip over it.
                box.Items.Add(new Separator { IsEnabled = false, Focusable = false, IsHitTestVisible = false });
            }
        }

        foreach (var (code, _) in LanguageList.Items)
            box.Items.Add(Make(code));

        box.SelectedItem = toSelect ?? box.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    /// <summary>Move a chosen language to the front of the MRU list (deduped, capped).</summary>
    public static void RecordRecent(UserSettings s, string code)
    {
        if (!Features.RecentLanguages || string.IsNullOrEmpty(code)) return;
        s.RecentLanguages.RemoveAll(c => string.Equals(c, code, StringComparison.OrdinalIgnoreCase));
        s.RecentLanguages.Insert(0, code);
        if (s.RecentLanguages.Count > MaxRecent)
            s.RecentLanguages.RemoveRange(MaxRecent, s.RecentLanguages.Count - MaxRecent);
    }
}
