using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using WinLens.Views;

namespace WinLens;

public partial class App : Application
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);

    private static readonly string LogDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinLens");
    private static readonly string CrashLog = Path.Combine(LogDir, "crash.log");

    private MainWindow? _main;

    static App()
    {
        // Force per-monitor v2 DPI awareness before any HWND is created.
        // Without this, GetSystemMetrics(SM_*VIRTUALSCREEN) and CopyFromScreen
        // return DPI-scaled (logical) pixel counts and the overlay ends up
        // covering only a fraction of the physical screen.
        try { SetProcessDpiAwarenessContext(PerMonitorAwareV2); } catch { /* older OS */ }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Capture every escape route so a crash leaves a trace instead of vanishing
        // silently (the app otherwise writes no log on failure).
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // Show/Hide rather than EnsureHandle: the tray host needs its HWND, but it also
        // needs the Loaded event, which only fires once the window has actually been
        // shown — that is where the tray icon and the global hotkeys are set up.
        // The window is 1x1, transparent and Visibility=Hidden, so nothing appears.
        _main = new MainWindow();
        _main.Show();
        _main.Hide();

        // Allow a shortcut (or first launch) to open the settings window directly.
        if (e.Args.Any(a => string.Equals(a, "--settings", StringComparison.OrdinalIgnoreCase)))
            _main.ShowSettings();

        if (e.Args.Any(a => string.Equals(a, "--translate", StringComparison.OrdinalIgnoreCase)))
            _main.TranslateNow();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Log only — deliberately do NOT set e.Handled. Swallowing would let the app
        // keep running in an unknown state and could spam dialogs if the same exception
        // recurs. Per-translate failures are already caught where they happen; anything
        // reaching here is genuinely unhandled, so we record it and let WPF terminate
        // as it would have before (Windows still writes its own event too).
        WriteCrash("DispatcherUnhandledException", e.Exception);
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        => WriteCrash("AppDomain.UnhandledException", e.ExceptionObject as Exception);

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteCrash("UnobservedTaskException", e.Exception);
        e.SetObserved();
    }

    // Keep crash.log from growing without bound if a crash recurs every launch.
    private const long MaxCrashLogBytes = 512 * 1024;

    // An exception the dispatcher doesn't handle reaches AppDomain.UnhandledException
    // too as WPF tears down; log it once.
    private static Exception? _lastLogged;

    private static void WriteCrash(string source, Exception? ex)
    {
        if (ex != null && ReferenceEquals(ex, _lastLogged)) return;
        _lastLogged = ex;
        try
        {
            Directory.CreateDirectory(LogDir);
            try
            {
                var info = new FileInfo(CrashLog);
                if (info.Exists && info.Length > MaxCrashLogBytes)
                    File.Delete(CrashLog); // start fresh rather than grow forever
            }
            catch { /* size check is best-effort */ }

            var text =
                $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} [{source}] ===={Environment.NewLine}" +
                $"{ex}{Environment.NewLine}{Environment.NewLine}";
            File.AppendAllText(CrashLog, text);
        }
        catch { /* last-ditch; nothing else we can do */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _main?.Dispose();
        base.OnExit(e);
    }
}
