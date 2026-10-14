using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using DRectangle = System.Drawing.Rectangle;

namespace WinLens.Views;

/// <summary>
/// Small, non-interactive spinner shown while a translation cycle runs (OCR + network).
/// Created after the screenshot is taken so it never appears in the captured image.
/// </summary>
public partial class LoadingOverlay : Window
{
    private const uint SWP_NOSIZE     = 0x0001;
    private const uint SWP_NOZORDER   = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    public LoadingOverlay()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Shows the spinner centred on <paramref name="areaPx"/> (virtual-screen pixels).
    /// It used to be WindowStartupLocation="CenterScreen", which always means the PRIMARY
    /// monitor — on a multi-monitor desk the spinner appeared on a different screen from
    /// the one being translated.
    /// </summary>
    public void ShowCenteredOn(DRectangle areaPx)
    {
        // Show first: SizeToContent means the size isn't known until it has been laid out.
        // Transparent until positioned, so a Manual-placement window can never paint one
        // frame at the desktop origin before being moved.
        Opacity = 0;
        Show();
        UpdateLayout();

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            Opacity = 1; // can't reposition; better visible in the default spot than invisible
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        double scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        double scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

        int widthPx  = (int)Math.Round(ActualWidth  * scaleX);
        int heightPx = (int)Math.Round(ActualHeight * scaleY);

        int x = areaPx.X + (areaPx.Width  - widthPx)  / 2;
        int y = areaPx.Y + (areaPx.Height - heightPx) / 2;

        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

        Opacity = 1;
    }
}
