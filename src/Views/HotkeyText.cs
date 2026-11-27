using System.Collections.Generic;
using System.Windows.Input;
using WinLens.Native;

namespace WinLens.Views;

/// <summary>How a shortcut is written out for the user: in the settings window and the tray menu.</summary>
internal static class HotkeyText
{
    /// <summary>The keys of a shortcut in the order they are held, e.g. Ctrl, Alt, T.</summary>
    public static IReadOnlyList<string> Parts(HotkeyModifiers mods, Key key)
    {
        var parts = new List<string>(5);
        if (mods.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(HotkeyModifiers.Alt))     parts.Add("Alt");
        if (mods.HasFlag(HotkeyModifiers.Shift))   parts.Add("Shift");
        if (mods.HasFlag(HotkeyModifiers.Win))     parts.Add("Win");
        parts.Add(KeyName(key));
        return parts;
    }

    /// <summary>The shortcut on one line, e.g. "Ctrl+Alt+T".</summary>
    public static string Format(HotkeyModifiers mods, Key key, string separator = "+") =>
        string.Join(separator, Parts(mods, key));

    /// <summary>
    /// The name printed on the key where the enum's own name is not it: the digit row is
    /// D0–D9 in <see cref="Key"/>, Enter is Return, Page Down is Next, and so on.
    /// </summary>
    private static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num " + (char)('0' + (key - Key.NumPad0)),
        Key.Return => "Enter",
        Key.Back => "Backspace",
        Key.Escape => "Esc",
        Key.Prior => "Page Up",
        Key.Next => "Page Down",
        Key.Capital => "Caps Lock",
        Key.Snapshot => "Print Screen",
        Key.Delete => "Del",
        Key.Insert => "Ins",
        _ => key.ToString(),
    };
}
