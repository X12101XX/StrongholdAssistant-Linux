using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using ASPAssistant.Core.GameModes.GarrisonProtocol;
using ASPAssistant.Core.Services;
using Moq;

namespace ASPAssistant.Tests.Services;

public class OpeningAutomaticTests
{
    [Fact]
    public async Task CompleteFrameThenScrolledOpeningAndStrategyTriggersExactlyOnce()
    {
        // 日志时序：完整开局一帧，滚动后只剩标题，随后选策略、自选编队。
        // 纯色背景同时复现棋盘遮挡检查会提前丢弃开局帧的问题。
        using var bitmap = new Bitmap(1920, 1080);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.Gray);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        var frame = stream.ToArray();
        int step = 0;
        var ocr = new Mock<IOcrEngine>();
        ocr.Setup(e => e.RecognizeRegionAsync(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<int>(), It.IsAny<int>())).ReturnsAsync((byte[] _, int x, int y, int w, int h) =>
            {
                string? text = (x, y) switch
                {
                    (480, 0) => step < 6 ? "3 | 确认本局信息" : step == 6 ? "2/3 选择策略" : "3/3 确定自选编队",
                    (38, 140) when step == 0 => "核心盟约",
                    (38, 410) when step == 0 => "附加盟约",
                    _ => null
                };
                return (IReadOnlyList<OcrTextResult>)(text is null ? [] : [new(text, (x, y, w, h))]);
            });
        using var scanner = new OcrScannerService(new ScreenCaptureService(), new GarrisonOcrStrategy(),
            new ShopReader(ocr.Object, []), ocr.Object, new ASPAssistant.Core.GameState.GameState(), () => []);
        int openings = 0, fieldReads = 0;
        scanner.BanScreenDetected += _ => openings++;
        scanner.ObserveFieldAsync = (_, _, _, _, _, _) => { fieldReads++; return Task.CompletedTask; };
        var scan = typeof(OcrScannerService).GetMethod("ScanFrameAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        for (; step < 8; step++)
        {
            await (Task)scan.Invoke(scanner, [frame, 1920, 1080, DateTimeOffset.UtcNow, 0d, null, 0d])!;
            Assert.Equal(1, openings);
        }
        Assert.Same(frame, scanner.OpeningScreenshot);
        Assert.Equal(0, fieldReads);
    }
}
