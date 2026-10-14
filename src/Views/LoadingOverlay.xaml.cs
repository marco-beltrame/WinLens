using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WinLens.Native;
using DRectangle = System.Drawing.Rectangle;

namespace WinLens.Views;

/// <summary>
/// Small, non-interactive spinner shown while a translation cycle runs (OCR + network).
/// Created after the screenshot is taken so it never appears in the captured image.
/// </summary>
public partial class LoadingOverlay : Window
{
    private DRectangle? _areaPx;

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

        // If the HWND is missing it can't be moved; visible in the default spot still beats
        // invisible, so show it either way.
        _areaPx = areaPx;
        Recenter();
        Opacity = 1;
    }

    /// <summary>
    /// Moving the spinner onto a monitor with a different DPI rescales it, so the centre
    /// computed with the old size is off; centre it again once WPF has re-measured it.
    /// </summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (_areaPx is null) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Recenter());
    }

    private bool Recenter()
    {
        if (_areaPx is not DRectangle area) return false;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return false;

        var dpi = VisualTreeHelper.GetDpi(this);
        double scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        double scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

        int widthPx  = (int)Math.Round(ActualWidth  * scaleX);
        int heightPx = (int)Math.Round(ActualHeight * scaleY);

        WindowPlacement.MoveTo(hwnd,
            area.X + (area.Width  - widthPx)  / 2,
            area.Y + (area.Height - heightPx) / 2);
        return true;
    }
}
