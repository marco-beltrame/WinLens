using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
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
        try
        {
            // Make sure our own settings window isn't captured in the screenshot.
            if (_settingsWindow is { IsVisible: true })
            {
                _settingsWindow.Hide();
                await Task.Delay(160);
            }

            var capture = ScreenCapture.CaptureVirtualScreen();

            var blocks = await _ocr.RecognizeAsync(capture.Bitmap, _settings.Current.SourceLanguage);
            if (blocks.Count == 0)
            {
                capture.Bitmap.Dispose();
                MemoryHygiene.Trim();
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
                    _settings.Save(_settings.Current);
                });
            overlay.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "WinLens — error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
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
