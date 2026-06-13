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
    private readonly Action _onHotkeyChanged;

    private bool _suppressEvents;
    private bool _capturing;

    public SettingsWindow(SettingsService settings, Action onTranslate, Action onHotkeyChanged)
    {
        InitializeComponent();
        _settings = settings;
        _onTranslate = onTranslate;
        _onHotkeyChanged = onHotkeyChanged;

        _suppressEvents = true;

        RebuildLanguageOptions();
        BuildSourceOptions();
        FillEngineFields();

        StartupToggle.IsChecked = StartupRegistration.IsEnabled();
        UpdateHotkeyText();

        _suppressEvents = false;

        PreviewKeyDown += OnWindowPreviewKeyDown;
        IsVisibleChanged += OnVisibleChanged;
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

    // ---------------- Translation engine ----------------

    private void FillEngineFields()
    {
        var provider = _settings.Current.TranslationProvider;
        foreach (var item in EngineCombo.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string tag &&
                Enum.TryParse<TranslationProvider>(tag, out var p) && p == provider)
            {
                EngineCombo.SelectedItem = item;
                break;
            }
        }

        LlmBaseUrlBox.Text = _settings.Current.LlmBaseUrl;
        LlmApiKeyBox.Text  = _settings.Current.LlmApiKey;
        LlmModelBox.Text   = _settings.Current.LlmModel;
        UpdateLlmPanelVisibility();
    }

    private void UpdateLlmPanelVisibility() =>
        LlmPanel.Visibility =
            _settings.Current.TranslationProvider == TranslationProvider.OpenAiCompatible
                ? Visibility.Visible
                : Visibility.Collapsed;

    private void OnEngineChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (EngineCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
        if (!Enum.TryParse<TranslationProvider>(tag, out var provider)) return;
        _settings.Current.TranslationProvider = provider;
        _settings.Save(_settings.Current);
        UpdateLlmPanelVisibility();
    }

    private void OnLlmConfigChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.Current.LlmBaseUrl = LlmBaseUrlBox.Text.Trim();
        _settings.Current.LlmApiKey  = LlmApiKeyBox.Text.Trim();
        _settings.Current.LlmModel   = LlmModelBox.Text.Trim();
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
        BeginCapture();
    }

    private void BeginCapture()
    {
        _capturing = true;
        HotkeyText.Text = "Press keys…";
        HotkeyChip.BorderBrush = (Brush)FindResource("AccentBrush");
        ChangeHotkeyButton.Content = "Cancel";
        Focus();
    }

    private void EndCapture()
    {
        _capturing = false;
        HotkeyChip.BorderBrush = (Brush)FindResource("BorderBrush");
        ChangeHotkeyButton.Content = "Change";
        UpdateHotkeyText();
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturing) return;
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape) { EndCapture(); return; }
        if (IsModifier(key)) return; // wait for the trigger key

        var mods = CurrentModifiers();
        if (mods == HotkeyModifiers.None) return; // require at least one modifier

        _settings.Current.HotkeyModifiers = mods;
        _settings.Current.HotkeyKey = key;
        _settings.Save(_settings.Current);
        _onHotkeyChanged();
        EndCapture();
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

    private void UpdateHotkeyText()
    {
        var s = _settings.Current;
        var parts = new List<string>();
        if (s.HotkeyModifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (s.HotkeyModifiers.HasFlag(HotkeyModifiers.Alt))     parts.Add("Alt");
        if (s.HotkeyModifiers.HasFlag(HotkeyModifiers.Shift))   parts.Add("Shift");
        if (s.HotkeyModifiers.HasFlag(HotkeyModifiers.Win))     parts.Add("Win");
        parts.Add(s.HotkeyKey.ToString());
        HotkeyText.Text = string.Join("  +  ", parts);
    }

    // ---------------- Startup ----------------

    private void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        StartupRegistration.SetEnabled(StartupToggle.IsChecked == true);
    }
}
