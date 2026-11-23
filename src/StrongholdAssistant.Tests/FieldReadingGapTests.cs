using System.Drawing;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Placement;
using ASPAssistant.Core.Services;
using Moq;

namespace StrongholdAssistant.Tests;

public sealed class FieldReadingGapTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingPhaseTextUsesOnlyCurrentReadyControl(bool ready)
    {
        var ocr = new Mock<IOcrEngine>();
        ocr.Setup(o => o.RecognizeRegionAsync(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((byte[] _, int x, int y, int w, int h) => x == 1620 && ready
                ? new OcrTextResult[] { new("准备就绪", (1700, 140, 100, 30)) } : []);
        ocr.Setup(o => o.RecognizeLineAsync(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(Array.Empty<OcrTextResult>());
        using var bitmap = new Bitmap(1920, 1080);
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        var reader = new EconomyReader(ocr.Object);
        Assert.Equal(ready, (await reader.ReadAsync(stream.ToArray(), 1920, 1080, 1)).IsRest);
    }

    [Fact]
    public void KnownMapFiltersUnknownProjectionAndResetForgetsIt()
    {
        var reader = new FieldReader(Path.Combine(AppContext.BaseDirectory, "data"), new Mock<IOcrEngine>().Object, []) { KnownMap = "m01" };
        using var bitmap = new Bitmap(1920, 1080);
        var blocked = new FieldCell("board", 2, 1);
        Assert.DoesNotContain(blocked, reader.ReadCells(bitmap, null, true).Keys);
        reader.Reset();
        Assert.Contains(blocked, reader.ReadCells(bitmap, null, true).Keys);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MapRecognitionRemovesPreviouslyUnknownBlockedCell(bool recognize)
    {
        var tracker = new FieldTracker();
        tracker.ConfigureMaps((await GarrisonBoardStore.LoadAsync(Path.Combine(AppContext.BaseDirectory, "data")))!);
        var cell = new FieldCell("board", 2, 1);
        var at = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        tracker.Observe(new(at, true, null, 1, new Dictionary<FieldCell, int> { [cell] = 1 }), force: true);
        Assert.Single(tracker.State!.Units);
        Assert.True(tracker.Observe(new(at.AddSeconds(1), true, "m01", 1, new Dictionary<FieldCell, int>()), recognize));
        Assert.Empty(tracker.State!.Units);
        tracker.Observe(new(at.AddSeconds(2), true, null, 1, new Dictionary<FieldCell, int> { [cell] = 1 }), force: true);
        Assert.Empty(tracker.State!.Units);
    }
}
