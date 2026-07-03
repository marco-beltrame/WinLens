using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;
using WinLens.Native;
using WinLens.Services;

namespace WinLens.Views;

public partial class MainWindow : Window, IDisposable
{
    private readonly SettingsService _settings = new();
    private readonly OcrService _ocr = new();
    private readonly TranslationService _translator = new();
    private GlobalHotkey? _hotkey;
    private SettingsWindow? _settingsWindow;
    private OverlayWindow? _overlay;

    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        _settings.Load();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SetTrayIcon();
        RegisterHotkey();

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

    private void RegisterHotkey()
    {
        _hotkey?.Dispose();
        _hotkey = new GlobalHotkey(this);
        _hotkey.Pressed += OnHotkey;
        try
        {
            _hotkey.Register(_settings.Current.HotkeyModifiers, _settings.Current.HotkeyKey);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not register the shortcut.\n\n{ex.Message}",
                "WinLens", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OnHotkey() => await RunCaptureAndTranslateAsync();

    public void ShowSettings() => OpenSettings();

    public void TranslateNow() => _ = RunCaptureAndTranslateAsync();

    private void OnTrayDoubleClick(object sender, RoutedEventArgs e) => OpenSettings();

    private void OnOpenSettings(object sender, RoutedEventArgs e) => OpenSettings();

    private async void OnTranslateNow(object sender, RoutedEventArgs e)
        => await RunCaptureAndTranslateAsync();

    private void OpenSettings()
    {
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(
                _settings,
                onTranslate: () => _ = RunCaptureAndTranslateAsync(),
                onHotkeyChanged: RegisterHotkey);
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

    private async Task RunCaptureAndTranslateAsync()
    {
        if (_busy) return;
        _busy = true;
        LoadingOverlay? loading = null;
        System.Drawing.Bitmap? ownedBitmap = null;
        bool restoreSettings = false;
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
            if (_settingsWindow is { IsVisible: true })
            {
                restoreSettings = true;
                _settingsWindow.Hide();
                await Task.Delay(160);
            }

            var capture = ScreenCapture.CaptureVirtualScreen();
            ownedBitmap = capture.Bitmap; // ours to dispose until the overlay takes it

            // Show the spinner only after the capture, so it isn't in the screenshot.
            loading = new LoadingOverlay();
            loading.Show();

            var blocks = await _ocr.RecognizeAsync(capture.Bitmap, _settings.Current.SourceLanguage);
            if (blocks.Count == 0)
            {
                ownedBitmap.Dispose();
                ownedBitmap = null;
                MemoryHygiene.Trim();
                Tray.ShowBalloonTip("WinLens", "No text found on screen.", BalloonIcon.Info);
                return;
            }

            var target = _settings.Current.TargetLanguage;
            await Task.WhenAll(blocks.Select(async b =>
            {
                b.TranslatedText = await _translator.TranslateAsync(
                    b.OriginalText, target, b.DetectedLanguage);
            }));

            var overlay = new OverlayWindow(
                capture.Bitmap,
                capture.Bounds,
                blocks,
                _translator,
                _settings.Current.TargetLanguage,
                onTargetLangChanged: code =>
                {
                    _settings.Current.TargetLanguage = code;
                    LanguagePicker.RecordRecent(_settings.Current, code);
                    _settings.Save(_settings.Current);
                },
                recentTargets: _settings.Current.RecentLanguages);
            ownedBitmap = null; // the overlay disposes the screenshot on Closed

            bool bringSettingsBack = restoreSettings;
            overlay.Closed += (_, _) =>
            {
                if (ReferenceEquals(_overlay, overlay))
                    _overlay = null;
                if (bringSettingsBack)
                    _settingsWindow?.Show();
            };

            loading.Close();
            loading = null;
            _overlay = overlay;
            overlay.Show();
        }
        catch (Exception ex)
        {
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

    private void OnExit(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    public void Dispose()
    {
        _hotkey?.Dispose();
        _translator.Dispose();
        Tray.Dispose();
    }
}
