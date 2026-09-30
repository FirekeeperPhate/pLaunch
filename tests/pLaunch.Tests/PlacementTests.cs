using pLaunch.Native;

namespace pLaunch.Tests;

public class PlacementTests
{
    static readonly PixelRect Monitor = new(0, 0, 1920, 1080);

    [Fact]
    public void BottomTaskbar_IsFoundFromTheWorkArea()
    {
        var (area, edge) = PopupPlacement.AvailableArea(Monitor, new(0, 0, 1920, 1032), null, 72);
        Assert.Equal(ScreenEdge.Bottom, edge);
        Assert.Equal(1032, area.Bottom);
    }

    [Theory]
    [InlineData(0, 48, 1920, 1080, ScreenEdge.Top)]
    [InlineData(62, 0, 1920, 1080, ScreenEdge.Left)]
    [InlineData(0, 0, 1858, 1080, ScreenEdge.Right)]
    public void OtherEdges_AreFoundFromTheWorkArea(int l, int t, int r, int b, ScreenEdge expected)
    {
        Assert.Equal(expected, PopupPlacement.AvailableArea(Monitor, new(l, t, r, b), null, 72).Edge);
    }

    [Fact]
    public void AutoHideTaskbar_UsesTheTaskbarSize()
    {
        // Hidden taskbar: moved almost off screen, same height
        var taskbar = new PixelRect(0, 1078, 1920, 1126);
        var (area, edge) = PopupPlacement.AvailableArea(Monitor, Monitor, taskbar, 72);
        Assert.Equal(ScreenEdge.Bottom, edge);
        Assert.Equal(1080 - 48, area.Bottom);
    }

    [Fact]
    public void AutoHideTaskbar_OnAnotherMonitor_FallsBackToBottom()
    {
        var taskbar = new PixelRect(-1920, 1032, 0, 1080);
        var (area, edge) = PopupPlacement.AvailableArea(Monitor, Monitor, taskbar, 72);
        Assert.Equal(ScreenEdge.Bottom, edge);
        Assert.Equal(1080 - 72, area.Bottom);
    }

    [Fact]
    public void CursorOnTaskbar_CentersOnCursor_AboveTheTaskbar()
    {
        var area = new PixelRect(0, 0, 1920, 1032);
        var (x, y) = PopupPlacement.Compute(area, ScreenEdge.Bottom, 900, 1050, 480, 600, 18);
        Assert.Equal(900 - 240, x);
        Assert.Equal(1032 - 18 - 600, y);
    }

    [Fact]
    public void CursorNearTheScreenEdge_KeepsThePopupInside()
    {
        var area = new PixelRect(0, 0, 1920, 1032);
        Assert.Equal(18, PopupPlacement.Compute(area, ScreenEdge.Bottom, 20, 1050, 480, 600, 18).X);
        Assert.Equal(1920 - 18 - 480, PopupPlacement.Compute(area, ScreenEdge.Bottom, 1910, 1050, 480, 600, 18).X);
    }

    [Fact]
    public void CursorAwayFromTheTaskbar_CentersInTheArea()
    {
        var area = new PixelRect(0, 0, 1920, 1032);
        var (x, _) = PopupPlacement.Compute(area, ScreenEdge.Bottom, 100, 300, 480, 600, 18);
        Assert.Equal(960 - 240, x);
    }

    [Fact]
    public void LeftTaskbar_OpensToTheRightOfIt()
    {
        var area = new PixelRect(62, 0, 1920, 1080);
        var (x, y) = PopupPlacement.Compute(area, ScreenEdge.Left, 30, 500, 480, 600, 18);
        Assert.Equal(62 + 18, x);
        Assert.Equal(500 - 300, y);
    }

    [Fact]
    public void PopupTallerThanTheArea_StaysAtTheTop()
    {
        var area = new PixelRect(62, 0, 1920, 400);
        var (_, y) = PopupPlacement.Compute(area, ScreenEdge.Left, 30, 200, 480, 600, 18);
        Assert.Equal(18, y);
    }

    // A menu to the right of the list, from y 100 to 700, its near edge at x 500
    static readonly PixelRect RightMenu = new(500, 100, 980, 700);

    [Theory]
    [InlineData(300, 400, 340, 420, true)]   // towards the menu, a bit downwards: still inside the triangle
    [InlineData(300, 400, 340, 350, true)]   // towards the menu, upwards (the top corner allows y >= 340 here)
    [InlineData(300, 400, 300, 440, false)]  // straight down: another row is meant
    [InlineData(300, 400, 260, 400, false)]  // away from the menu
    [InlineData(300, 400, 300, 400, false)]  // not moving: resting on the row
    [InlineData(300, 400, 340, 700, false)]  // much steeper than the menu's bottom corner
    public void HeadingForAnOpenMenu_IsTheTriangleToItsNearEdge(int fromX, int fromY, int toX, int toY, bool heading)
    {
        Assert.Equal(heading, PopupPlacement.IsHeadingFor((fromX, fromY), (toX, toY), RightMenu));
    }

    [Fact]
    public void HeadingForAMenuOnTheLeft_UsesItsRightEdge()
    {
        var leftMenu = new PixelRect(0, 100, 480, 700);
        Assert.True(PopupPlacement.IsHeadingFor((700, 400), (660, 410), leftMenu));
        Assert.False(PopupPlacement.IsHeadingFor((700, 400), (740, 410), leftMenu));
    }
}
