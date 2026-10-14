using System.Windows.Input;
using WinLens.Native;

namespace WinLens.Models;

public sealed class UserSettings
{
    public string TargetLanguage { get; set; } = "en";

    /// <summary>OCR source language tag, or "auto" to try all installed recognizers.</summary>
    public string SourceLanguage { get; set; } = "auto";

    /// <summary>Most-recently-used target language codes, most recent first (experimental).</summary>
    public System.Collections.Generic.List<string> RecentLanguages { get; set; } = new();

    public HotkeyModifiers HotkeyModifiers { get; set; } = HotkeyModifiers.Control | HotkeyModifiers.Alt;
    public Key HotkeyKey { get; set; } = Key.T;

    /// <summary>
    /// Shortcut for region (drag-to-select) capture. Default Ctrl+Shift+R — no Alt, to
    /// avoid the common NVIDIA / overlay-tool bindings.
    /// </summary>
    public HotkeyModifiers RegionHotkeyModifiers { get; set; } = HotkeyModifiers.Control | HotkeyModifiers.Shift;
    public Key RegionHotkeyKey { get; set; } = Key.R;

    /// <summary>
    /// Replaces null/unknown values (hand-edited or damaged file) with defaults, and
    /// shortcuts WinLens would no longer accept (e.g. a bare Shift+A saved by an older
    /// version) with the default pair.
    /// </summary>
    /// <returns>True if a shortcut had to be reset.</returns>
    public bool Sanitize()
    {
        TargetLanguage = SettingsRules.NormalizeTarget(TargetLanguage);
        SourceLanguage = SettingsRules.NormalizeSource(SourceLanguage);
        RecentLanguages = SettingsRules.NormalizeRecent(RecentLanguages, 4);

        bool reset = false;
        if (!SettingsRules.IsHotkeyUsable((uint)(HotkeyModifiers & ~HotkeyModifiers.NoRepeat), HotkeyKey.ToString()))
        {
            HotkeyModifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt;
            HotkeyKey = Key.T;
            reset = true;
        }
        if (!SettingsRules.IsHotkeyUsable((uint)(RegionHotkeyModifiers & ~HotkeyModifiers.NoRepeat), RegionHotkeyKey.ToString())
            || (RegionHotkeyModifiers, RegionHotkeyKey) == (HotkeyModifiers, HotkeyKey))
        {
            RegionHotkeyModifiers = HotkeyModifiers.Control | HotkeyModifiers.Shift;
            RegionHotkeyKey = Key.R;
            reset = true;
        }
        return reset;
    }
}
