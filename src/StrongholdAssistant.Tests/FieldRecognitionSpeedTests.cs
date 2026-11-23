using System.Drawing;
using System.Drawing.Imaging;
using ASPAssistant.Core.Services;
using Moq;

namespace StrongholdAssistant.Tests;

public sealed class FieldRecognitionSpeedTests
{
    [Fact]
    public async Task ManualEconomyKeepsRoundAndPhaseWithoutReadingMoneyOrUpgradePanel()
    {
        var ocr = new Mock<IOcrEngine>();
        ocr.Setup(o => o.RecognizeRegionAsync(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((byte[] _, int x, int y, int w, int h) => y == 0
                ? new OcrTextResult[] { new("回合 12", (650, 10, 90, 30)) } : []);
        ocr.Setup(o => o.RecognizeLineAsync(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(new OcrTextResult[] { new("17", (1790, 807, 65, 38)) });
        using var bitmap = new Bitmap(1920, 1080);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        var result = await new EconomyReader(ocr.Object).ReadFieldAsync(stream.ToArray(), 1920, 1080, true);
        Assert.Equal(new EconomyReading(12, null, null, null, true), result);
        ocr.Verify(o => o.RecognizeRegionAsync(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()), Times.Once);
        ocr.Verify(o => o.RecognizeLineAsync(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task IdenticalShopInputReusesNamesButChangedInputReadsAgain()
    {
        var ocr = new Mock<IOcrEngine>();
        ocr.Setup(o => o.RecognizeRegionAsync(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(new OcrTextResult[] { new("文字", (0, 0, 20, 20)) });
        var reader = new ShopReader(ocr.Object, []) { ReadUntracked = true };
        using var bitmap = new Bitmap(1920, 1080);
        byte[] Frame()
        {
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
        await reader.ReadAsync(Frame(), 1920, 1080, []);
        await reader.ReadAsync(Frame(), 1920, 1080, []);
        ocr.Verify(o => o.RecognizeRegionAsync(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()), Times.Once);
        using (var g = Graphics.FromImage(bitmap)) g.FillRectangle(Brushes.White, 1000, 1020, 30, 20);
        await reader.ReadAsync(Frame(), 1920, 1080, []);
        ocr.Verify(o => o.RecognizeRegionAsync(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()), Times.Exactly(2));
    }
}

