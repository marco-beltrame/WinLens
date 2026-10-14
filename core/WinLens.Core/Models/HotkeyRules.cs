using System;

namespace WinLens.Models;

public enum HotkeyCheck
{
    Ok,
    /// <summary>No Ctrl, Alt or Win held — a bare key or Shift+key would hijack normal typing.</summary>
    NeedsModifier,
    /// <summary>A combination Windows or every app relies on (Ctrl+C, Alt+F4, Win+L, …).</summary>
    Reserved,
    /// <summary>The "key" is a modifier, IME placeholder or nothing at all.</summary>
    BadKey,
}

/// <summary>
/// Which shortcuts WinLens accepts. Operates on the Win32 RegisterHotKey modifier bits
/// (Alt=1, Ctrl=2, Shift=4, Win=8) and the WPF <c>Key</c> name, so it stays free of WPF.
/// </summary>
public static class HotkeyRules
{
    public const uint Alt = 0x1, Control = 0x2, Shift = 0x4, Win = 0x8;
    private const uint ModifierMask = Alt | Control | Shift | Win; // drops MOD_NOREPEAT

    // Deliberately small: only combinations that would break the system or every app.
    private static readonly (uint Mods, string Key)[] Reserved =
    {
        (Control, "C"), (Control, "V"), (Control, "X"), (Control, "Z"),
        (Control, "Y"), (Control, "A"), (Control, "S"),
        (Alt, "F4"), (Alt, "Tab"), (Alt, "Escape"),
        (Control | Alt, "Delete"),
        (Win, "L"), (Win, "D"), (Win, "R"), (Win, "E"),
    };

    public static HotkeyCheck Validate(uint modifiers, string? keyName)
    {
        if (!IsTriggerKey(keyName)) return HotkeyCheck.BadKey;

        modifiers &= ModifierMask;
        if ((modifiers & (Control | Alt | Win)) == 0) return HotkeyCheck.NeedsModifier;

        foreach (var (mods, key) in Reserved)
            if (mods == modifiers && string.Equals(key, keyName, StringComparison.OrdinalIgnoreCase))
                return HotkeyCheck.Reserved;

        return HotkeyCheck.Ok;
    }

    /// <summary>True for a real trigger key (not None, a modifier or an IME/dead-key marker).</summary>
    public static bool IsTriggerKey(string? keyName) =>
        !string.IsNullOrWhiteSpace(keyName) && !NonTriggerKeys.Contains(keyName);

    private static readonly System.Collections.Generic.HashSet<string> NonTriggerKeys =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "None", "LeftCtrl", "RightCtrl", "LeftAlt", "RightAlt", "LeftShift", "RightShift",
            "LWin", "RWin", "System", "ImeProcessed", "DeadCharProcessed",
        };
}
