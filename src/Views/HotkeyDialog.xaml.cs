using System.Windows;
using System.Windows.Input;
using WinLens.Native;

namespace WinLens.Views;

public partial class HotkeyDialog : Window
{
    public HotkeyModifiers SelectedModifiers { get; private set; }
    public Key? SelectedKey { get; private set; }

    public HotkeyDialog(HotkeyModifiers initialMods, Key initialKey)
    {
        InitializeComponent();
        SelectedModifiers = initialMods;
        SelectedKey = initialKey;
        UpdateDisplay();
        Loaded += (_, _) => Keyboard.Focus(HotkeyDisplay.Parent as IInputElement);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Capture system keys (Alt-modified) at preview level.
        HandleKey(e);
    }

    private void OnKeyDown(object sender, KeyEventArgs e) => HandleKey(e);

    private void HandleKey(KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Ignore pure modifier presses — wait for the trigger key.
        if (IsModifier(key))
        {
            SelectedModifiers = CurrentModifiers();
            UpdateDisplay();
            return;
        }

        SelectedModifiers = CurrentModifiers();
        SelectedKey = key;
        SaveButton.IsEnabled = SelectedModifiers != HotkeyModifiers.None;
        UpdateDisplay();
    }

    private static HotkeyModifiers CurrentModifiers()
    {
        var m = HotkeyModifiers.None;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) m |= HotkeyModifiers.Control;
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0)     m |= HotkeyModifiers.Alt;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)   m |= HotkeyModifiers.Shift;
        if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0) m |= HotkeyModifiers.Win;
        return m;
    }

    private static bool IsModifier(Key k) => k is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftAlt  or Key.RightAlt  or
        Key.LeftShift or Key.RightShift or
        Key.LWin or Key.RWin or
        Key.System;

    private void UpdateDisplay()
    {
        var parts = new System.Collections.Generic.List<string>();
        if (SelectedModifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (SelectedModifiers.HasFlag(HotkeyModifiers.Alt))     parts.Add("Alt");
        if (SelectedModifiers.HasFlag(HotkeyModifiers.Shift))   parts.Add("Shift");
        if (SelectedModifiers.HasFlag(HotkeyModifiers.Win))     parts.Add("Win");
        if (SelectedKey is { } k) parts.Add(k.ToString());

        HotkeyDisplay.Text = parts.Count == 0
            ? "(focus this box and press a key)"
            : string.Join(" + ", parts);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
