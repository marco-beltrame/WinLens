using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Hardcodet.Wpf.TaskbarNotification;
using WinLens.Layout;
using WinLens.Models;
using WinLens.Native;
using WinLens.Services;
using WinLens.Services.Translation;
using DRectangle = System.Drawing.Rectangle;

namespace WinLens.Views;

public partial class MainWindow : Window, IDisposable
{
    // Distinct ids: both hotkeys are registered against this window's HWND and WM_HOTKEY
    // is dispatched by id, so they must not collide.
    private const int FullScreenHotkeyId = 0xB001;
    private const int RegionHotkeyId     = 0xB002;

    private readonly SettingsService _settings = new();
    private readonly OcrService _ocr = new();
    private readonly TranslationService _translator = new();
    private GlobalHotkey? _hotkey;
    private GlobalHotkey? _regionHotkey;
    private SettingsWindow? _settingsWindow;
    private OverlayWindow? _overlay;

    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        _settings.Load();
        _translator.Configure(_settings.Current.ToEngineOptions());
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SetTrayIcon();

        var (fullOk, regionOk) = RegisterHotkeys();
        if (!fullOk || !regionOk)
        {
            // A balloon rather than the modal dialog this used to show: a shortcut another
            // app owns is worth telling the user about, but not worth blocking startup —
            // every action is still reachable from the tray menu.
            var dead = !fullOk && !regionOk ? "shortcuts are"
                     : !fullOk ? "translate shortcut is"
                     : "region shortcut is";
            Tray.ShowBalloonTip("WinLens",
                $"The {dead} already in use by another app. Change it in WinLens settings, "
                + "or use this tray menu.", BalloonIcon.Warning);
        }

        if (_settings.HotkeysWereReset)
            Tray.ShowBalloonTip("WinLens",
                "A saved shortcut wasn't valid and was reset to its default.", BalloonIcon.Info);

        // If the app moved since "launch at startup" was enabled, the Run entry points
        // at a dead path; refresh it to the current executable.
        StartupRegistration.RefreshPathIfEnabled();

        // First run: show the window so the app isn't an invisible tray icon.
        if (!_settings.LoadedFromDisk)
            OpenSettings();
    }

    private void SetTrayIcon()
    {
        try
        {
            var info = Application.GetResourceStream(
                new Uri("pack://application:,,,/assets/winlens.ico"));
            if (info != null)
                Tray.Icon = new System.Drawing.Icon(info.Stream);
        }
        catch { /* fall back to no icon */ }
    }

    // ---------------- Hotkeys ----------------

    /// <summary>
    /// (Re)registers both shortcuts from the current settings and reports which ones the
    /// OS accepted. Failure is not fatal and not shown here: the tray menu can trigger
    /// every action, so a shortcut another app already owns costs the user nothing.
    /// </summary>
    private (bool fullOk, bool regionOk) RegisterHotkeys()
    {
        _hotkey?.Dispose();
        _regionHotkey?.Dispose();

        var s = _settings.Current;

        _hotkey = new GlobalHotkey(this, FullScreenHotkeyId);
        _hotkey.Pressed += OnFullScreenHotkey;

        _regionHotkey = new GlobalHotkey(this, RegionHotkeyId);
        _regionHotkey.Pressed += OnRegionHotkey;

        return (TryRegister(_hotkey, s.HotkeyModifiers, s.HotkeyKey),
                TryRegister(_regionHotkey, s.RegionHotkeyModifiers, s.RegionHotkeyKey));
    }

    private static bool TryRegister(GlobalHotkey hotkey, HotkeyModifiers mods, Key key)
    {
        try
        {
            hotkey.Register(mods, key);
            return true;
        }
        catch
        {
            return false; // combination already owned by another app
        }
    }

    /// <summary>
    /// Applies a shortcut pair, saving it only once the OS has actually accepted it.
    /// Previously the settings window saved first and registered second, so picking a
    /// combination another app owns left the user with no working shortcut AND the dead
    /// combination persisted across restarts.
    /// </summary>
    /// <returns>Which side (if any) the OS refused; on failure everything was rolled back.</returns>
    public HotkeyApplyResult TryApplyHotkeys(
        HotkeyModifiers fullMods, Key fullKey,
        HotkeyModifiers regionMods, Key regionKey)
    {
        var s = _settings.Current;
        var previous = (s.HotkeyModifiers, s.HotkeyKey, s.RegionHotkeyModifiers, s.RegionHotkeyKey);

        bool fullChanged   = (s.HotkeyModifiers, s.HotkeyKey) != (fullMods, fullKey);
        bool regionChanged = (s.RegionHotkeyModifiers, s.RegionHotkeyKey) != (regionMods, regionKey);

        s.HotkeyModifiers = fullMods;
        s.HotkeyKey = fullKey;
        s.RegionHotkeyModifiers = regionMods;
        s.RegionHotkeyKey = regionKey;

        var (fullOk, regionOk) = RegisterHotkeys();

        // Only roll back a change the OS refused. A shortcut that was ALREADY dead before
        // this edit (another app took it while WinLens was running) must not block the
        // user from editing the other one.
        if ((!fullChanged || fullOk) && (!regionChanged || regionOk))
        {
            _settings.Save(s);
            return new HotkeyApplyResult(true, false, false);
        }

        (s.HotkeyModifiers, s.HotkeyKey, s.RegionHotkeyModifiers, s.RegionHotkeyKey) = previous;
        RegisterHotkeys();
        return new HotkeyApplyResult(false, fullChanged && !fullOk, regionChanged && !regionOk);
    }

    /// <summary>
    /// Suspends both shortcuts while the settings window is capturing a new one —
    /// otherwise WM_HOTKEY swallows the keystroke before WPF ever sees it, and the user
    /// could never re-bind a combination to itself or type the other action's combination.
    /// </summary>
    public void SetHotkeysPaused(bool paused)
    {
        if (!paused)
        {
            RegisterHotkeys();
            return;
        }

        _hotkey?.Dispose();
        _regionHotkey?.Dispose();
        _hotkey = null;
        _regionHotkey = null;
    }

    private async void OnFullScreenHotkey() => await RunTranslateAsync(regionSelect: false);

    private async void OnRegionHotkey() => await RunTranslateAsync(regionSelect: true);

    // ---------------- Entry points ----------------

    public void ShowSettings() => OpenSettings();

    public void TranslateNow() => _ = RunTranslateAsync(regionSelect: false);

    private void OnTrayDoubleClick(object sender, RoutedEventArgs e) => OpenSettings();

    private void OnOpenSettings(object sender, RoutedEventArgs e) => OpenSettings();

    private async void OnTranslateNow(object sender, RoutedEventArgs e)
        => await RunTranslateAsync(regionSelect: false);

    private async void OnTranslateRegion(object sender, RoutedEventArgs e)
        => await RunTranslateAsync(regionSelect: true);

    private void OpenSettings()
    {
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(
                _settings,
                // Settings hides itself before calling this; bring it back afterwards.
                onTranslate: () => _ = RunTranslateAsync(regionSelect: false, reopenSettings: true),
                onApplyHotkeys: TryApplyHotkeys,
                onHotkeyCapturePaused: SetHotkeysPaused,
                onEngineChanged: () => _translator.Configure(_settings.Current.ToEngineOptions()),
                onTestEngine: options => _translator.TestAsync(options, _settings.Current.TargetLanguage));
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        else
        {
            _settingsWindow.Show();
            if (_settingsWindow.WindowState == WindowState.Minimized)
                _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
        }
    }

    // ---------------- Capture + translate ----------------

    /// <param name="reopenSettings">
    /// The settings window was hidden by the caller and should come back once the cycle ends.
    /// </param>
    private async Task RunTranslateAsync(bool regionSelect, bool reopenSettings = false)
    {
        if (_busy)
        {
            if (reopenSettings) _settingsWindow?.Show();
            return;
        }
        _busy = true;
        LoadingOverlay? loading = null;
        OverlayWindow? overlay = null;
        System.Drawing.Bitmap? ownedBitmap = null;
        bool restoreSettings = reopenSettings;
        try
        {
            // A still-open overlay must not end up in the new screenshot — we'd OCR our
            // own translation. Close it and give it a beat to leave the screen.
            if (_overlay != null)
            {
                _overlay.Close();
                _overlay = null;
                await Task.Delay(160);
            }

            // Make sure our own settings window isn't captured in the screenshot.
            // Checked after the overlay closes, because closing it can bring the
            // settings window back (see the Closed handler below).
            if (_settingsWindow is { IsVisible: true })
            {
                restoreSettings = true;
                _settingsWindow.Hide();
                await Task.Delay(160);
            }

            DRectangle area;
            if (regionSelect)
            {
                var selector = new RegionSelectionWindow();
                selector.ShowDialog();
                if (selector.SelectedRegion is not DRectangle selected)
                    return; // cancelled, or the drag was too small to be meaningful

                // Let the selection chrome fully vanish before the screenshot, or its
                // dimming leaks into the captured pixels.
                await Task.Delay(120);
                area = selected;
            }
            else
            {
                // Always exactly one monitor — the one the cursor is on. Capturing the
                // whole desktop would put the overlay on top of screens the user is still
                // working on, which is the problem this whole path exists to avoid.
                area = ScreenCapture.MonitorBoundsAt(ScreenCapture.CursorPosition);
            }

            var capture = ScreenCapture.CaptureRegion(area);
            ownedBitmap = capture.Bitmap; // ours to dispose until the overlay takes it

            // Show the spinner only after the capture, so it isn't in the screenshot.
            loading = new LoadingOverlay();
            loading.ShowCenteredOn(capture.Bounds);

            var blocks = await _ocr.RecognizeAsync(capture.Bitmap, _settings.Current.SourceLanguage);
            if (blocks.Count == 0)
            {
                ownedBitmap.Dispose();
                ownedBitmap = null;
                MemoryHygiene.Trim();
                Tray.ShowBalloonTip("WinLens", "No text found in the captured area.", BalloonIcon.Info);
                return;
            }

            var target = _settings.Current.TargetLanguage;
            await TranslateBlocksAsync(blocks, target);

            // Never spills onto a monitor we did not capture (see OverlayLayout).
            var windowBounds = OverlayLayout.WindowBounds(
                capture.Bounds, ScreenCapture.AllMonitorBounds(), regionSelect);

            overlay = new OverlayWindow(
                capture.Bitmap,
                capture.Bounds,
                windowBounds,
                blocks,
                _translator,
                target,
                onTargetLangChanged: code =>
                {
                    _settings.Current.TargetLanguage = code;
                    LanguagePicker.RecordRecent(_settings.Current, code);
                    _settings.Save(_settings.Current);
                },
                recentTargets: _settings.Current.RecentLanguages);
            ownedBitmap = null; // the overlay disposes the screenshot on Closed

            bool bringSettingsBack = restoreSettings;
            var shown = overlay;
            shown.Closed += (_, _) =>
            {
                if (ReferenceEquals(_overlay, shown))
                    _overlay = null;
                if (bringSettingsBack)
                    _settingsWindow?.Show();
            };

            loading.Close();
            loading = null;
            _overlay = overlay;
            overlay.Show();

            WarnIfNothingChanged(blocks, target);
        }
        catch (Exception ex)
        {
            // Clear our own windows first: the spinner must not sit over the message, and an
            // overlay that failed while loading would otherwise stay open, invisible
            // (opacity 0) and holding the screenshot.
            loading?.Close();
            loading = null;
            if (overlay != null)
            {
                try { overlay.Close(); } catch { /* already torn down */ }
            }

            MessageBox.Show(ex.Message, "WinLens — error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ownedBitmap?.Dispose(); // reached only when the overlay never took ownership
            loading?.Close();
            if (restoreSettings && _overlay == null)
                _settingsWindow?.Show(); // no overlay took over (no text / error)
            _busy = false;
        }
    }

    private async Task TranslateBlocksAsync(System.Collections.Generic.List<OcrBlock> blocks, string target)
    {
        var translated = await _translator.TranslateManyAsync(
            blocks.Select(b => new TranslationRequest(b.OriginalText, b.DetectedLanguage)).ToArray(),
            target);
        for (int i = 0; i < blocks.Count; i++)
            blocks[i].TranslatedText = translated[i];
    }

    /// <summary>
    /// When both translation backends fail, TranslateAsync returns the input unchanged, so
    /// the overlay renders the ORIGINAL text and looks like it simply stopped working.
    /// Say so instead. Requires every line to be unchanged (a few identical lines are
    /// normal — numbers, names, punctuation) and enough lines to be meaningful.
    /// </summary>
    private void WarnIfNothingChanged(System.Collections.Generic.List<OcrBlock> blocks, string target)
    {
        if (blocks.Count < 4) return;
        if (blocks.Any(b => !string.Equals(b.TranslatedText, b.OriginalText, StringComparison.Ordinal)))
            return;

        Tray.ShowBalloonTip(
            "WinLens",
            $"Nothing changed — the text may already be in {LanguageList.LabelFor(target)}, "
            + "or the translation service is unreachable.",
            BalloonIcon.Warning);
    }

    private void OnExit(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    public void Dispose()
    {
        _hotkey?.Dispose();
        _regionHotkey?.Dispose();
        _translator.Dispose();
        Tray.Dispose();
    }
}
