using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using WinLens.Native;
using DRectangle = System.Drawing.Rectangle;

namespace WinLens.Views;

/// <summary>
/// Full-screen drag-to-select overlay. Returns the selected rectangle in
/// virtual-screen pixel coordinates via <see cref="SelectedRegion"/>, or
/// <c>null</c> if the user cancelled (Escape, right-click, or a too-small drag).
/// </summary>
public partial class RegionSelectionWindow : Window
{
    private const int MinSizePx = 10;

    private const uint SWP_NOZORDER   = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    private readonly DRectangle _virtualBounds;
    private double _pixelToDip;

    private Point? _dragStart;

    public DRectangle? SelectedRegion { get; private set; }

    public RegionSelectionWindow()
    {
        InitializeComponent();

        _virtualBounds = ScreenCapture.VirtualScreenBounds;

        uint dpi = GetDpiForSystem();
        if (dpi == 0) dpi = 96;
        _pixelToDip = 96.0 / dpi;

        Left   = _virtualBounds.X      * _pixelToDip;
        Top    = _virtualBounds.Y      * _pixelToDip;
        Width  = _virtualBounds.Width  * _pixelToDip;
        Height = _virtualBounds.Height * _pixelToDip;

        // The DIP math above uses the SYSTEM dpi, which on a mixed-DPI multi-monitor
        // setup is only right for the primary monitor. Pin the window in PHYSICAL pixels
        // once the HWND exists so it really covers every screen, and re-read the px→DIP
        // factor from the window's own DPI in Loaded — otherwise the rectangle the user
        // draws maps back to the wrong physical pixels and the capture is offset.
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowPos(hwnd, IntPtr.Zero,
                _virtualBounds.X, _virtualBounds.Y, _virtualBounds.Width, _virtualBounds.Height,
                SWP_NOZORDER | SWP_NOACTIVATE);
        };

        Loaded += (_, _) =>
        {
            var actual = VisualTreeHelper.GetDpi(this);
            if (actual.DpiScaleX > 0)
                _pixelToDip = 1.0 / actual.DpiScaleX;

            LayoutDimFullCover();
            Activate();
            Focus();
        };

        KeyDown += OnKeyDown;
        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        MouseRightButtonDown += (_, _) => Cancel();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Cancel();
    }

    private void Cancel()
    {
        SelectedRegion = null;
        Close();
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(Root);
        SelectionRect.Visibility = Visibility.Visible;
        SizeChip.Visibility = Visibility.Visible;
        Canvas.SetLeft(SelectionRect, _dragStart.Value.X);
        Canvas.SetTop(SelectionRect, _dragStart.Value.Y);
        SelectionRect.Width = 0;
        SelectionRect.Height = 0;
        CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not Point start) return;
        var current = e.GetPosition(Root);

        double x = Math.Min(start.X, current.X);
        double y = Math.Min(start.Y, current.Y);
        double w = Math.Abs(current.X - start.X);
        double h = Math.Abs(current.Y - start.Y);

        Canvas.SetLeft(SelectionRect, x);
        Canvas.SetTop(SelectionRect, y);
        SelectionRect.Width = w;
        SelectionRect.Height = h;

        LayoutDim(x, y, w, h);

        // Show pixel dimensions of the prospective capture (in physical px).
        int pxW = (int)Math.Round(w / _pixelToDip);
        int pxH = (int)Math.Round(h / _pixelToDip);
        SizeText.Text = $"{pxW} × {pxH}";
        SizeChip.UpdateLayout();
        double chipW = SizeChip.ActualWidth;
        double chipH = SizeChip.ActualHeight;

        // Place the size chip just below-right of the cursor, but keep it on screen.
        double chipX = Math.Min(ActualWidth  - chipW - 4, current.X + 12);
        double chipY = Math.Min(ActualHeight - chipH - 4, current.Y + 12);
        chipX = Math.Max(4, chipX);
        chipY = Math.Max(4, chipY);
        Canvas.SetLeft(SizeChip, chipX);
        Canvas.SetTop(SizeChip, chipY);
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart is null) return;
        ReleaseMouseCapture();
        _dragStart = null;

        double dipX = Canvas.GetLeft(SelectionRect);
        double dipY = Canvas.GetTop(SelectionRect);
        double dipW = SelectionRect.Width;
        double dipH = SelectionRect.Height;

        int pxW = (int)Math.Round(dipW / _pixelToDip);
        int pxH = (int)Math.Round(dipH / _pixelToDip);

        if (pxW < MinSizePx || pxH < MinSizePx)
        {
            SelectedRegion = null;
        }
        else
        {
            int pxX = _virtualBounds.X + (int)Math.Round(dipX / _pixelToDip);
            int pxY = _virtualBounds.Y + (int)Math.Round(dipY / _pixelToDip);
            SelectedRegion = new DRectangle(pxX, pxY, pxW, pxH);
        }
        Close();
    }

    // Sized from ActualWidth/ActualHeight rather than Width/Height: the window is
    // repositioned in physical pixels after construction, so the DIP values set in the
    // constructor are stale by the time these run.
    private void LayoutDimFullCover()
    {
        Canvas.SetLeft(DimTop, 0); Canvas.SetTop(DimTop, 0);
        DimTop.Width = ActualWidth; DimTop.Height = ActualHeight;
        DimBottom.Width = 0; DimBottom.Height = 0;
        DimLeft.Width = 0;   DimLeft.Height = 0;
        DimRight.Width = 0;  DimRight.Height = 0;
    }

    private void LayoutDim(double x, double y, double w, double h)
    {
        double right  = x + w;
        double bottom = y + h;

        Canvas.SetLeft(DimTop, 0);     Canvas.SetTop(DimTop, 0);
        DimTop.Width = ActualWidth;    DimTop.Height = Math.Max(0, y);

        Canvas.SetLeft(DimBottom, 0);  Canvas.SetTop(DimBottom, bottom);
        DimBottom.Width = ActualWidth; DimBottom.Height = Math.Max(0, ActualHeight - bottom);

        Canvas.SetLeft(DimLeft, 0);    Canvas.SetTop(DimLeft, y);
        DimLeft.Width = Math.Max(0, x); DimLeft.Height = h;

        Canvas.SetLeft(DimRight, right); Canvas.SetTop(DimRight, y);
        DimRight.Width = Math.Max(0, ActualWidth - right); DimRight.Height = h;
    }
}
