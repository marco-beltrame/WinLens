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
using WinLens.Services.Translation;

namespace WinLens.Views;

public sealed class LanguageOption
{
    public string Code { get; init; } = "";
    public string Label { get; init; } = "";
}

/// <summary>Outcome of applying a shortcut pair: which side the OS refused, if any.</summary>
public readonly record struct HotkeyApplyResult(bool Ok, bool FullFailed, bool RegionFailed);

public partial class SettingsWindow : Window
{
    private readonly SettingsService _settings;
    private readonly Action _onTranslate;

    /// <summary>
    /// Applies a shortcut pair and reports whether the OS accepted it. A refused change is
    /// rolled back and the result says which side failed, so the UI can name the right
    /// combination instead of showing a binding that does nothing.
    /// </summary>
    private readonly Func<HotkeyModifiers, Key, HotkeyModifiers, Key, HotkeyApplyResult> _onApplyHotkeys;

    private readonly Action<bool> _onHotkeyCapturePaused;

    /// <summary>Applies the engine settings just saved.</summary>
    private readonly Action _onEngineChanged;

    /// <summary>Tries an engine configuration without making it the active one.</summary>
    private readonly Func<EngineOptions, Task<EngineTestResult>> _onTestEngine;

    private bool _suppressEvents;
    private bool _capturing;
    private bool _capturingRegion;

    public SettingsWindow(
        SettingsService settings,
        Action onTranslate,
        Func<HotkeyModifiers, Key, HotkeyModifiers, Key, HotkeyApplyResult> onApplyHotkeys,
        Action<bool> onHotkeyCapturePaused,
        Action onEngineChanged,
        Func<EngineOptions, Task<EngineTestResult>> onTestEngine)
    {
        InitializeComponent();
        // From the assembly, so the footer can't fall behind the release again.
        var version = typeof(SettingsWindow).Assembly.GetName().Version;
        if (version != null) FooterText.Text = $"WinLens v{version.ToString(3)}";
        _settings = settings;
        _onTranslate = onTranslate;
        _onApplyHotkeys = onApplyHotkeys;
        _onHotkeyCapturePaused = onHotkeyCapturePaused;
        _onEngineChanged = onEngineChanged;
        _onTestEngine = onTestEngine;

        _suppressEvents = true;

        RebuildLanguageOptions();
        BuildSourceOptions();

        RefreshStartupToggle();
        UpdateHotkeyText();
        UpdateRegionHotkeyText();
        LoadEngineUi();

        _suppressEvents = false;

        PreviewKeyDown += OnWindowPreviewKeyDown;
        IsVisibleChanged += OnVisibleChanged;
        // Leaving the window (focus lost, minimized, hidden) must not leave the global
        // shortcuts suspended while a capture is half-done.
        Deactivated += (_, _) => EndCaptureIfActive();
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) EndCaptureIfActive(); };
        Closing += (_, _) =>
        {
            // Resume the global shortcuts if the window is closed mid-capture, otherwise
            // they would stay suspended for the rest of the session.
            if (_capturing || _capturingRegion)
                _onHotkeyCapturePaused(false);
            CommitEngineFields(); // a field still being typed in never lost focus
        };
    }

    // Refresh installed OCR languages each time the window is shown
    // (so a language added via Windows settings appears without restart).
    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true) { EndCaptureIfActive(); CommitEngineFields(); return; }
        _suppressEvents = true;
        RebuildLanguageOptions(); // float most-recent targets to the top
        BuildSourceOptions();
        RefreshStartupToggle(); // may have changed via Task Manager / Windows Settings
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

    // ---------------- Translation engine ----------------

    private string CurrentEngine =>
        (EngineCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? EngineNames.Google;

    private void LoadEngineUi()
    {
        var s = _settings.Current;
        var name = EngineNames.Normalize(s.TranslationEngine);
        foreach (ComboBoxItem item in EngineCombo.Items)
            if (item.Tag as string == name)
                EngineCombo.SelectedItem = item;
        FallbackToggle.IsChecked = s.EngineFallbackToGoogle;
        LoadEngineFields(name);
    }

    /// <summary>Shows the fields of <paramref name="engine"/>; each engine keeps its own address and key.</summary>
    private void LoadEngineFields(string engine)
    {
        var s = _settings.Current;
        bool llm = engine == EngineNames.LocalLlm;
        bool custom = engine != EngineNames.Google;

        EnginePanel.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        ModelPanel.Visibility = llm ? Visibility.Visible : Visibility.Collapsed;
        EngineStatus.Text = "";

        // Google needs no explanation; the other two say what they connect to.
        EngineCaption.Text = engine switch
        {
            EngineNames.LibreTranslate => "Your own server, or a hosted one with its API key.",
            EngineNames.LocalLlm => "Ollama, LM Studio or any OpenAI-compatible server on your PC.",
            _ => "",
        };
        EngineCaption.Visibility = EngineCaption.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        EngineUrlBox.Text = llm ? s.LlmUrl : s.LibreTranslateUrl;
        EngineUrlBox.Tag = llm ? "http://localhost:11434/v1" : "http://localhost:5000";
        EngineModelBox.Text = s.LlmModel;
        EngineKeyBox.Password = (llm ? SecretStore.Unprotect(s.LlmKeyProtected)
                                     : SecretStore.Unprotect(s.LibreTranslateKeyProtected)) ?? "";
    }

    private void OnEngineChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        var engine = CurrentEngine;
        _settings.Current.TranslationEngine = engine;
        _settings.Save(_settings.Current);

        _suppressEvents = true;
        LoadEngineFields(engine);
        _suppressEvents = false;
        _onEngineChanged();
    }

    /// <summary>Copies the visible fields into the settings of the selected engine.</summary>
    private bool StoreEngineFields()
    {
        var s = _settings.Current;
        var engine = CurrentEngine;
        if (engine == EngineNames.Google) return false;

        var url = EngineUrlBox.Text.Trim();
        var key = EngineKeyBox.Password.Trim();
        bool changed;
        if (engine == EngineNames.LocalLlm)
        {
            var model = EngineModelBox.Text.Trim();
            changed = url != s.LlmUrl || model != s.LlmModel || key != (SecretStore.Unprotect(s.LlmKeyProtected) ?? "");
            s.LlmUrl = url.Length == 0 ? "http://localhost:11434/v1" : url;
            s.LlmModel = model;
            if (changed) s.LlmKeyProtected = SecretStore.Protect(key);
        }
        else
        {
            changed = url != s.LibreTranslateUrl || key != (SecretStore.Unprotect(s.LibreTranslateKeyProtected) ?? "");
            s.LibreTranslateUrl = url;
            if (changed) s.LibreTranslateKeyProtected = SecretStore.Protect(key);
        }
        return changed;
    }

    private void OnEngineFieldCommitted(object sender, RoutedEventArgs e) => CommitEngineFields();

    private void CommitEngineFields()
    {
        if (_suppressEvents || !StoreEngineFields()) return;
        _settings.Save(_settings.Current);
        EngineStatus.Text = "";
        _onEngineChanged();
    }

    private void OnFallbackToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.Current.EngineFallbackToGoogle = FallbackToggle.IsChecked == true;
        _settings.Save(_settings.Current);
        _onEngineChanged();
    }

    private async void OnTestEngine(object sender, RoutedEventArgs e)
    {
        if (StoreEngineFields())
        {
            _settings.Save(_settings.Current);
            _onEngineChanged();
        }

        TestEngineButton.IsEnabled = false;
        EngineStatus.Foreground = (Brush)FindResource("TextSubtleBrush");
        EngineStatus.Text = "Testing…";
        try
        {
            var result = await _onTestEngine(_settings.Current.ToEngineOptions());
            if (result.Ok)
            {
                EngineStatus.Foreground = (Brush)FindResource("SuccessBrush");
                var model = result.Model != null && EngineModelBox.Text.Trim().Length == 0
                    ? $" Using {result.Model}." : "";
                EngineStatus.Text = $"Works: \u201c{result.Sample}\u201d.{model}";
            }
            else
            {
                EngineStatus.Foreground = (Brush)FindResource("DangerBrush");
                EngineStatus.Text = result.Message;
            }
        }
        catch (Exception ex)
        {
            EngineStatus.Foreground = (Brush)FindResource("DangerBrush");
            EngineStatus.Text = ex.Message;
        }
        finally
        {
            TestEngineButton.IsEnabled = true;
        }
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
        EndCaptureIfActive(); // resume the global shortcuts before leaving
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
        HotkeyMessage.Visibility = Visibility.Collapsed;
        RegionHotkeyMessage.Visibility = Visibility.Collapsed;

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

    private void EndCaptureIfActive()
    {
        if (_capturing || _capturingRegion) EndCapture();
    }

    private void EndCapture()
    {
        bool wasCapturing = _capturing || _capturingRegion;
        _capturing = false;
        _capturingRegion = false;
        HotkeyMessage.Visibility = Visibility.Collapsed;
        RegionHotkeyMessage.Visibility = Visibility.Collapsed;

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

        // Under an IME or after a dead key the real key hides in ImeProcessedKey /
        // DeadCharProcessedKey; the placeholder itself is not a usable shortcut key.
        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.DeadCharProcessed => e.DeadCharProcessedKey,
            _ => e.Key,
        };

        if (key == Key.Escape) { EndCapture(); return; }
        if (key == Key.None || IsModifier(key)) return; // wait for the trigger key

        var mods = CurrentModifiers();
        var check = HotkeyRules.Validate((uint)mods, key.ToString());
        if (check != HotkeyCheck.Ok)
        {
            ShowHotkeyMessage(check switch
            {
                HotkeyCheck.NeedsModifier => "Hold Ctrl, Alt or Win together with the key.",
                HotkeyCheck.Reserved => $"{FormatHotkey(mods, key)} is a common system shortcut. Try another.",
                _ => "That key can't be used. Try another.",
            });
            return; // keep capturing
        }

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

        var result = _onApplyHotkeys(fullMods, fullKey, regionMods, regionKey);
        EndCapture();

        if (!result.Ok)
        {
            // Name the combination the OS actually refused (with a swap it may be the
            // other action's, not the one just typed).
            var failed = new List<string>();
            if (result.FullFailed) failed.Add(FormatHotkey(fullMods, fullKey));
            if (result.RegionFailed) failed.Add(FormatHotkey(regionMods, regionKey));
            var name = failed.Count > 0 ? string.Join(" and ", failed) : FormatHotkey(mods, key);
            MessageBox.Show(
                this,
                $"Windows would not give WinLens {name} — another app is "
                + "already using it.\n\nThe previous shortcut is still active.",
                "WinLens", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ShowHotkeyMessage(string text)
    {
        // Under the shortcut being recorded, and scrolled into view: the window can be
        // taller than a laptop screen, and a message below the fold looks like nothing happened.
        var message = _capturingRegion ? RegionHotkeyMessage : HotkeyMessage;
        message.Text = text;
        message.Visibility = Visibility.Visible;
        message.UpdateLayout();
        message.BringIntoView();
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

    /// <summary>Syncs the toggle to the real registry state without firing the change handler.</summary>
    private void RefreshStartupToggle()
    {
        bool was = _suppressEvents;
        _suppressEvents = true;
        StartupToggle.IsChecked = StartupRegistration.IsEnabled();
        StartupMessage.Visibility = Visibility.Collapsed;
        _suppressEvents = was;
    }

    private void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        bool want = StartupToggle.IsChecked == true;
        if (StartupRegistration.SetEnabled(want))
        {
            StartupMessage.Visibility = Visibility.Collapsed;
            return;
        }

        // Registry refused (policy / access denied): show what's really set.
        RefreshStartupToggle();
        StartupMessage.Text = "Couldn't change this setting. Windows blocked the change.";
        StartupMessage.Visibility = Visibility.Visible;
    }
}
