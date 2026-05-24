using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using WinLens.Views;

namespace WinLens;

public partial class App : Application
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);

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

        _main = new MainWindow();
        _main.Show();
        _main.Hide();

        // Allow a shortcut (or first launch) to open the settings window directly.
        if (e.Args.Any(a => string.Equals(a, "--settings", StringComparison.OrdinalIgnoreCase)))
            _main.ShowSettings();

        if (e.Args.Any(a => string.Equals(a, "--translate", StringComparison.OrdinalIgnoreCase)))
            _main.TranslateNow();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _main?.Dispose();
        base.OnExit(e);
    }
}
