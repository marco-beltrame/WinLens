using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DRectangle = System.Drawing.Rectangle;

namespace WinLens.Native;

/// <summary>
/// Places WPF windows in PHYSICAL pixels. WPF positions windows in DIPs scaled by one DPI,
/// which on a mixed-DPI multi-monitor desk is only right for one monitor, so the overlay,
/// the region selector and the spinner are moved with SetWindowPos instead.
/// </summary>
internal static class WindowPlacement
{
    private const int WM_DPICHANGED = 0x02E0;

    private const uint SWP_NOSIZE     = 0x0001;
    private const uint SWP_NOZORDER   = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    // A window straddling two monitors can flip between their DPIs each time it is
    // re-pinned; bound the re-pins so that can never turn into a loop.
    private const int MaxRepins = 3;

    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    /// <summary>px → DIP factor for the system (primary) DPI; a first guess before the HWND exists.</summary>
    public static double SystemPixelToDip()
    {
        uint dpi = GetDpiForSystem();
        return 96.0 / (dpi == 0 ? 96 : dpi);
    }

    /// <summary>px → DIP factor of the DPI WPF actually lays <paramref name="visual"/> out with.</summary>
    public static double PixelToDip(Visual visual, double fallback)
    {
        var dpi = VisualTreeHelper.GetDpi(visual);
        return dpi.DpiScaleX > 0 ? 1.0 / dpi.DpiScaleX : fallback;
    }

    /// <summary>
    /// Makes <paramref name="window"/> cover exactly <paramref name="bounds"/> (physical px)
    /// and keeps it there. Call from the constructor, before the window is shown.
    /// <para>
    /// Moving the HWND onto a monitor with another DPI makes Windows send WM_DPICHANGED,
    /// and WPF answers it by resizing the window to the OS-suggested rectangle — scaled by
    /// the DPI ratio, so the overlay would grow onto the next monitor or stop short of the
    /// screen edge. The bounds are re-applied after WPF has handled the message.
    /// </para>
    /// </summary>
    public static void PinToPhysicalBounds(Window window, DRectangle bounds)
    {
        // Initial DIP placement: decides which monitor WPF first associates the window with.
        double f = SystemPixelToDip();
        window.Left   = bounds.X      * f;
        window.Top    = bounds.Y      * f;
        window.Width  = bounds.Width  * f;
        window.Height = bounds.Height * f;

        window.SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(window) is not HwndSource source) return;
            var hwnd = source.Handle;
            Apply(hwnd, bounds);

            int repins = 0;
            source.AddHook((IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                // Don't handle it: WPF must still switch its rendering DPI. Re-pin once it
                // has applied its own suggested rectangle.
                if (msg == WM_DPICHANGED && repins < MaxRepins)
                {
                    repins++;
                    window.Dispatcher.BeginInvoke(DispatcherPriority.Send, () =>
                    {
                        if (!IsAt(h, bounds)) Apply(h, bounds);
                    });
                }
                return IntPtr.Zero;
            });
        };
    }

    /// <summary>Moves the window's top-left to (<paramref name="x"/>, <paramref name="y"/>) physical px, keeping its size.</summary>
    public static void MoveTo(IntPtr hwnd, int x, int y) =>
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

    private static void Apply(IntPtr hwnd, DRectangle b) =>
        SetWindowPos(hwnd, IntPtr.Zero, b.X, b.Y, b.Width, b.Height, SWP_NOZORDER | SWP_NOACTIVATE);

    private static bool IsAt(IntPtr hwnd, DRectangle b) =>
        GetWindowRect(hwnd, out var r) &&
        r.Left == b.Left && r.Top == b.Top && r.Right == b.Right && r.Bottom == b.Bottom;
}
