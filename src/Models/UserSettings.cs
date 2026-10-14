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
}
