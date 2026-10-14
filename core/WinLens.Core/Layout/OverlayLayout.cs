using System.Collections.Generic;
using System.Drawing;

namespace WinLens.Layout;

/// <summary>
/// Decides which part of the desktop the translation overlay covers. All rectangles are
/// in physical virtual-screen pixels. The overlay takes every click it covers (a click on
/// it dismisses it), so it must stay on the monitors the capture actually came from.
/// </summary>
public static class OverlayLayout
{
    /// <param name="capture">The rectangle that was actually captured.</param>
    /// <param name="monitors">Bounds of every monitor.</param>
    /// <param name="isRegion">True for a drag-to-select capture.</param>
    public static Rectangle WindowBounds(Rectangle capture, IEnumerable<Rectangle> monitors, bool isRegion)
    {
        // Full-screen and active-monitor captures already are whole monitors.
        if (!isRegion)
            return capture;

        // A region is grown to the monitors it touches, so the control bar has room and the
        // rest of those screens is dimmed. Monitors it doesn't touch stay usable. This used
        // to grow to the single "nearest" monitor and union it with the region, which left
        // the overlay covering an odd strip of the neighbouring screen.
        var bounds = capture;
        foreach (var m in monitors)
        {
            if (m.IntersectsWith(capture))
                bounds = Rectangle.Union(bounds, m);
        }
        return bounds;
    }
}
