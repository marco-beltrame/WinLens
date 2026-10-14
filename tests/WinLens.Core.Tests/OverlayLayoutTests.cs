using System.Drawing;
using WinLens.Layout;

namespace WinLens.Core.Tests;

public class OverlayLayoutTests
{
    private static readonly Rectangle Primary = new(0, 0, 1920, 1080);
    private static readonly Rectangle LeftOfPrimary = new(-2560, -200, 2560, 1440);
    private static readonly Rectangle RightOfPrimary = new(1920, 0, 1920, 1080);
    private static readonly Rectangle[] ThreeMonitors = { LeftOfPrimary, Primary, RightOfPrimary };

    [Fact]
    public void FullScreen_CoversExactlyTheCapture()
    {
        Assert.Equal(Primary, OverlayLayout.WindowBounds(Primary, ThreeMonitors, isRegion: false));
    }

    [Fact]
    public void Region_InsideOneMonitor_GrowsToThatMonitorOnly()
    {
        var region = new Rectangle(100, 100, 400, 300);
        Assert.Equal(Primary, OverlayLayout.WindowBounds(region, ThreeMonitors, isRegion: true));
    }

    [Fact]
    public void Region_OnMonitorWithNegativeOrigin_GrowsToThatMonitor()
    {
        var region = new Rectangle(-2000, -100, 500, 400);
        Assert.Equal(LeftOfPrimary, OverlayLayout.WindowBounds(region, ThreeMonitors, isRegion: true));
    }

    [Fact]
    public void Region_SpanningTwoMonitors_CoversBothAndNotTheThird()
    {
        var region = new Rectangle(-300, 200, 500, 300);
        var expected = Rectangle.Union(LeftOfPrimary, Primary);
        var bounds = OverlayLayout.WindowBounds(region, ThreeMonitors, isRegion: true);

        Assert.Equal(expected, bounds);
        Assert.False(bounds.IntersectsWith(RightOfPrimary));
    }

    [Fact]
    public void Region_TouchingAMonitorEdge_DoesNotPullInTheNeighbour()
    {
        // Ends exactly at x = 1920, where the right-hand monitor starts.
        var region = new Rectangle(1500, 100, 420, 200);
        Assert.Equal(Primary, OverlayLayout.WindowBounds(region, ThreeMonitors, isRegion: true));
    }

    [Fact]
    public void Region_WithNoMonitorInfo_FallsBackToTheRegion()
    {
        var region = new Rectangle(100, 100, 400, 300);
        Assert.Equal(region, OverlayLayout.WindowBounds(region, [], isRegion: true));
    }
}
