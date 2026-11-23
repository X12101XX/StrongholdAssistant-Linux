using ASPAssistant.Core.Interop;
using ASPAssistant.Core.Services;
using StrongholdAssistant.Host;

namespace StrongholdAssistant.Tests;

public class SidebarResizeTests
{
    [Theory]
    [InlineData(400, 600)]
    [InlineData(1200, 1000)]
    public void ManualHeightsUseDipAndFitAvailableArea(double height, int expected)
    {
        var area = new RECT { Right = 2000, Bottom = 1000 };
        var game = new RECT { Right = 1200, Bottom = 800 };
        Assert.Equal(expected, WindowGeometry.SidePanelBounds(game, area, 1.5, 380, height, false).Height);
        Assert.Equal(expected, WindowGeometry.OverlayBounds(area, 1.5, 380, null, manualHeightDip: height).Height);
    }

    [Theory]
    [InlineData(760, null)]
    [InlineData(840, null)]
    [InlineData(841, 841d)]
    public void HeightSnapsWithinFortyDip(double height, double? expected)
        => Assert.Equal(expected, WindowGeometry.SnapHeight(height, 800));

    [Theory]
    [InlineData(200, 320)]
    [InlineData(600, 600)]
    [InlineData(800, 720)]
    public void WidthUsesExpandedRange(double width, double expected)
        => Assert.Equal(expected, new DockSettings().WithWidth(width).Width);

    [Fact]
    public void HeightsPersistAndOldSettingsDefaultToNull()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, "{\"width\":600}");
            var store = new HostSettingsStore(path);
            Assert.Null(store.LoadDock().FollowHeightDip);
            Assert.Null(store.LoadDock().OverlayHeightDip);
            var dock = store.LoadDock() with { FollowHeightDip = 650, OverlayHeightDip = 550 };
            store.SaveDock(dock);
            Assert.Equal(dock, store.LoadDock());
            store.SaveDock(dock.Redock());
            Assert.Null(store.LoadDock().FollowHeightDip);
            Assert.Null(store.LoadDock().OverlayHeightDip);
            Assert.Equal(600, store.LoadDock().Width);
        }
        finally { Directory.Delete(directory, true); }
    }
}
