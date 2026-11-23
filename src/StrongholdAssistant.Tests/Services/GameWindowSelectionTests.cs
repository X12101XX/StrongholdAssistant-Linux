using ASPAssistant.Core.Interop;

namespace StrongholdAssistant.Tests.Services;

public sealed class GameWindowSelectionTests
{
    [Fact]
    public void HiddenSmallWindowBeforeVisibleGame_SelectsGame()
    {
        Assert.Equal((nint)2, User32.SelectArknightsWindow([
            new((nint)1, false, 234, 126), new((nint)2, true, 1920, 1080)]));
    }

    [Fact]
    public void OnlyHiddenWindows_ReturnsNoWindow()
    {
        Assert.Equal(nint.Zero, User32.SelectArknightsWindow([
            new((nint)1, false, 234, 126), new((nint)2, false, 1920, 1080)]));
    }

    [Fact]
    public void TwoVisibleWindows_SelectsLargestClientArea()
    {
        Assert.Equal((nint)2, User32.SelectArknightsWindow([
            new((nint)1, true, 2000, 500), new((nint)2, true, 1920, 1080)]));
    }
}
