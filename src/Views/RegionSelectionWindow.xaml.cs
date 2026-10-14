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

    private readonly DRectangle _virtualBounds;
    private double _pixelToDip;

    private Point? _dragStart;

    public DRectangle? SelectedRegion { get; private set; }

    public RegionSelectionWindow()
    {
        InitializeComponent();

        _virtualBounds = ScreenCapture.VirtualScreenBounds;

        // Pinned in PHYSICAL pixels so it really covers every screen, also when it starts
        // on a monitor scaled differently from the primary. The px→DIP factor is re-read
        // from the window's own DPI in Loaded / OnDpiChanged — otherwise the rectangle the
        // user draws maps back to the wrong physical pixels and the capture is offset.
        _pixelToDip = WindowPlacement.SystemPixelToDip();
        WindowPlacement.PinToPhysicalBounds(this, _virtualBounds);

        Loaded += (_, _) =>
        {
            _pixelToDip = WindowPlacement.PixelToDip(this, _pixelToDip);
            LayoutDimFullCover();
            Activate();
            Focus();
        };

        // The window can be resized after Loaded (re-pinned after a DPI change); keep the
        // dim layer covering all of it while nothing is being dragged.
        SizeChanged += (_, _) =>
        {
            if (IsLoaded && _dragStart is null) LayoutDimFullCover();
        };

        KeyDown += OnKeyDown;
        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        MouseRightButtonDown += (_, _) => Cancel();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (newDpi.DpiScaleX > 0) _pixelToDip = 1.0 / newDpi.DpiScaleX;

        // A drag in progress was measured in the old DIPs; drop it rather than capture
        // the wrong pixels. The user simply drags again.
        if (_dragStart is not null)
        {
            ReleaseMouseCapture();
            _dragStart = null;
            SelectionRect.Visibility = Visibility.Collapsed;
            SizeChip.Visibility = Visibility.Collapsed;
        }
        if (IsLoaded) LayoutDimFullCover();
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
