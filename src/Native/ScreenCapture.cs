using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WinLens.Native;

/// <summary>
/// Screen capture in PHYSICAL pixels. The process is per-monitor v2 aware (see App's
/// static ctor), so every rectangle here — virtual screen, monitor bounds, cursor
/// position — is already in real device pixels and needs no DPI scaling.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ScreenCapture
{
    /// <summary>
    /// A captured image plus the virtual-screen rectangle it was taken from.
    /// <paramref name="Bounds"/> is the origin every downstream coordinate is relative to.
    /// </summary>
    public readonly record struct CaptureResult(Bitmap Bitmap, Rectangle Bounds);

    private const int SM_XVIRTUALSCREEN  = 76;
    private const int SM_YVIRTUALSCREEN  = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprcMonitor, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr data);

    /// <summary>Bounding box of every monitor, in physical pixels.</summary>
    public static Rectangle VirtualScreenBounds => new(
        GetSystemMetrics(SM_XVIRTUALSCREEN),
        GetSystemMetrics(SM_YVIRTUALSCREEN),
        GetSystemMetrics(SM_CXVIRTUALSCREEN),
        GetSystemMetrics(SM_CYVIRTUALSCREEN));

    /// <summary>Current cursor position in physical pixels, or the virtual-screen origin if unavailable.</summary>
    public static Point CursorPosition =>
        GetCursorPos(out var p) ? new Point(p.X, p.Y) : VirtualScreenBounds.Location;

    /// <summary>Bounds of the monitor containing <paramref name="pt"/> (nearest if outside all of them).</summary>
    public static Rectangle MonitorBoundsAt(Point pt)
    {
        var native = new POINT { X = pt.X, Y = pt.Y };
        return BoundsOf(MonitorFromPoint(native, MONITOR_DEFAULTTONEAREST));
    }

    /// <summary>Bounds of every monitor, in physical pixels.</summary>
    public static IReadOnlyList<Rectangle> AllMonitorBounds()
    {
        var result = new List<Rectangle>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr _, IntPtr _, ref RECT r, IntPtr _) =>
        {
            result.Add(new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static Rectangle BoundsOf(IntPtr hMonitor)
    {
        if (hMonitor != IntPtr.Zero)
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(hMonitor, ref info))
            {
                var r = info.rcMonitor;
                return new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            }
        }
        // No monitor info (monitor detached mid-call): the whole desktop is always a
        // valid answer and keeps callers from having to handle an empty rectangle.
        return VirtualScreenBounds;
    }

    /// <summary>
    /// Captures an arbitrary rectangle of the virtual screen. The rectangle is clamped to
    /// the desktop first, so an off-screen or partly off-screen request still yields a
    /// valid image; the returned <see cref="CaptureResult.Bounds"/> is the CLAMPED
    /// rectangle, which is what downstream coordinates must be relative to.
    /// </summary>
    /// <exception cref="InvalidOperationException">The clamped rectangle is empty.</exception>
    public static CaptureResult CaptureRegion(Rectangle regionPx)
    {
        var bounds = Rectangle.Intersect(regionPx, VirtualScreenBounds);
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new InvalidOperationException("The selected area is not on any screen.");

        var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        try
        {
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(bounds.X, bounds.Y, 0, 0,
                new Size(bounds.Width, bounds.Height), CopyPixelOperation.SourceCopy);
        }
        catch
        {
            bmp.Dispose(); // never leak the bitmap when the blit fails
            throw;
        }
        return new CaptureResult(bmp, bounds);
    }
}
