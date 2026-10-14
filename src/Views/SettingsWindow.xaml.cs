using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinLens.Models;
using WinLens.Native;
using WinLens.Services;

namespace WinLens.Views;

public sealed class LanguageOption
{
    public string Code { get; init; } = "";
    public string Label { get; init; } = "";
}

public partial class SettingsWindow : Window
{
    private readonly SettingsService _settings;
    private readonly Action _onTranslate;

    /// <summary>
    /// Applies a shortcut pair and reports whether the OS accepted it. Returns false when
    /// the change was refused and rolled back, so the UI can say so instead of showing a
    /// binding that does nothing.
    /// </summary>
    private readonly Func<HotkeyModifiers, Key, HotkeyModifiers, Key, bool> _onApplyHotkeys;

    private readonly Action<bool> _onHotkeyCapturePaused;

    private bool _suppressEvents;
    private bool _capturing;
    private bool _capturingRegion;

    public SettingsWindow(
        SettingsService settings,
        Action onTranslate,
        Func<HotkeyModifiers, Key, HotkeyModifiers, Key, bool> onApplyHotkeys,
        Action<bool> onHotkeyCapturePaused)
    {
        InitializeComponent();
        _settings = settings;
        _onTranslate = onTranslate;
        _onApplyHotkeys = onApplyHotkeys;
        _onHotkeyCapturePaused = onHotkeyCapturePaused;

        _suppressEvents = true;

        RebuildLanguageOptions();
        BuildSourceOptions();

        StartupToggle.IsChecked = StartupRegistration.IsEnabled();
        UpdateHotkeyText();
        UpdateRegionHotkeyText();

        _suppressEvents = false;

        PreviewKeyDown += OnWindowPreviewKeyDown;
        IsVisibleChanged += OnVisibleChanged;
        Closing += (_, _) =>
        {
            // Resume the global shortcuts if the window is closed mid-capture, otherwise
            // they would stay suspended for the rest of the session.
            if (_capturing || _capturingRegion)
                _onHotkeyCapturePaused(false);
        };
    }

    // Refresh installed OCR languages each time the window is shown
    // (so a language added via Windows settings appears without restart).
    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true) return;
        _suppressEvents = true;
        RebuildLanguageOptions(); // float most-recent targets to the top
        BuildSourceOptions();
        _suppressEvents = false;
    }

    private void OnAddLanguages(object sender, MouseButtonEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:regionlanguage") { UseShellExecute = true });
        }
        catch { /* Settings app unavailable */ }
    }

    private void RebuildLanguageOptions()
    {
        var itemStyle = (Style)FindResource("ModernComboBoxItem");
        LanguagePicker.Populate(LanguageCombo, itemStyle,
            _settings.Current.TargetLanguage, _settings.Current.RecentLanguages);
    }

    // ---------------- Title bar ----------------

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    // ---------------- Language ----------------

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (LanguageCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string code) return;
        _settings.Current.TargetLanguage = code;
        LanguagePicker.RecordRecent(_settings.Current, code);
        _settings.Save(_settings.Current);
    }

    // ---------------- Source (OCR) language ----------------

    private void BuildSourceOptions()
    {
        var opts = new List<LanguageOption> { new() { Code = "auto", Label = "Auto (detect)" } };
        foreach (var (tag, display) in OcrService.AvailableLanguages)
            opts.Add(new LanguageOption { Code = tag, Label = display });
        SourceCombo.ItemsSource = opts;

        var current = _settings.Current.SourceLanguage;
        SourceCombo.SelectedItem = opts.FirstOrDefault(
            o => string.Equals(o.Code, current, StringComparison.OrdinalIgnoreCase)) ?? opts[0];
    }

    private void OnSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (SourceCombo.SelectedItem is not LanguageOption opt) return;
        _settings.Current.SourceLanguage = opt.Code;
        _settings.Save(_settings.Current);
    }

    // ---------------- Translate ----------------

    private async void OnTranslateNow(object sender, RoutedEventArgs e)
    {
        Hide();
        await Task.Delay(160); // let the window vanish before the screenshot
        _onTranslate();
    }

    // ---------------- Hotkey capture ----------------

    private void OnChangeHotkey(object sender, RoutedEventArgs e)
    {
        if (_capturing) { EndCapture(); return; }
        BeginCapture(region: false);
    }

    private void OnChangeRegionHotkey(object sender, RoutedEventArgs e)
    {
        if (_capturingRegion) { EndCapture(); return; }
        BeginCapture(region: true);
    }

    private void BeginCapture(bool region)
    {
        EndCapture(); // cancel the other chip if it was already capturing

        _capturing = !region;
        _capturingRegion = region;

        // Suspend the global shortcuts so WM_HOTKEY doesn't eat the key being bound —
        // without this the user could never re-bind a combination to itself, or type the
        // other action's combination to swap them.
        _onHotkeyCapturePaused(true);

        var accent = (Brush)FindResource("AccentBrush");
        if (region)
        {
            RegionHotkeyText.Text = "Press keys…";
            RegionHotkeyChip.BorderBrush = accent;
            ChangeRegionHotkeyButton.Content = "Cancel";
        }
        else
        {
            HotkeyText.Text = "Press keys…";
            HotkeyChip.BorderBrush = accent;
            ChangeHotkeyButton.Content = "Cancel";
        }
        Focus();
    }

    private void EndCapture()
    {
        bool wasCapturing = _capturing || _capturingRegion;
        _capturing = false;
        _capturingRegion = false;

        var idle = (Brush)FindResource("BorderBrush");
        HotkeyChip.BorderBrush = idle;
        RegionHotkeyChip.BorderBrush = idle;
        ChangeHotkeyButton.Content = "Change";
        ChangeRegionHotkeyButton.Content = "Change";
        UpdateHotkeyText();
        UpdateRegionHotkeyText();

        if (wasCapturing)
            _onHotkeyCapturePaused(false);
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturing && !_capturingRegion) return;
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape) { EndCapture(); return; }
        if (IsModifier(key)) return; // wait for the trigger key

        var mods = CurrentModifiers();
        if (mods == HotkeyModifiers.None) return; // require at least one modifier

        var s = _settings.Current;
        var fullMods = s.HotkeyModifiers;
        var fullKey = s.HotkeyKey;
        var regionMods = s.RegionHotkeyModifiers;
        var regionKey = s.RegionHotkeyKey;

        // Pressing the combination the OTHER action already owns SWAPS the two rather
        // than failing — the same combination can't be registered twice, and swapping is
        // what someone trading the two shortcuts around actually means.
        if (_capturingRegion)
        {
            if (mods == fullMods && key == fullKey)
            {
                fullMods = regionMods;
                fullKey = regionKey;
            }
            regionMods = mods;
            regionKey = key;
        }
        else
        {
            if (mods == regionMods && key == regionKey)
            {
                regionMods = fullMods;
                regionKey = fullKey;
            }
            fullMods = mods;
            fullKey = key;
        }

        bool applied = _onApplyHotkeys(fullMods, fullKey, regionMods, regionKey);
        EndCapture();

        if (!applied)
        {
            MessageBox.Show(
                this,
                $"Windows would not give WinLens {FormatHotkey(mods, key)} — another app is "
                + "already using it.\n\nThe previous shortcut is still active.",
                "WinLens", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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
        Key.LWin or Key.RWin or Key.System;

    private void UpdateHotkeyText() =>
        HotkeyText.Text = FormatHotkey(_settings.Current.HotkeyModifiers, _settings.Current.HotkeyKey);

    private void UpdateRegionHotkeyText() =>
        RegionHotkeyText.Text = FormatHotkey(
            _settings.Current.RegionHotkeyModifiers, _settings.Current.RegionHotkeyKey);

    private static string FormatHotkey(HotkeyModifiers mods, Key key)
    {
        var parts = new List<string>();
        if (mods.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(HotkeyModifiers.Alt))     parts.Add("Alt");
        if (mods.HasFlag(HotkeyModifiers.Shift))   parts.Add("Shift");
        if (mods.HasFlag(HotkeyModifiers.Win))     parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("  +  ", parts);
    }

    // ---------------- Startup ----------------

    private void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        StartupRegistration.SetEnabled(StartupToggle.IsChecked == true);
    }
}
