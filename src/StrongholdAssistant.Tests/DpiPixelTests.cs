using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using ASPAssistant.Core.Services;

namespace StrongholdAssistant.Tests;

public sealed class DpiPixelTests
{
    [Fact]
    public void PixelBuffersIgnoreDpiMetadata()
    {
        using var image = new Bitmap(1920, 1080, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.Clear(Color.Red);
            graphics.FillRectangle(Brushes.Blue, 1430, 840, 230, 180);
        }
        image.SetResolution(96, 96);
        var low = FieldPixels(image);
        using var lowRoster = RosterCoordinates.Normalize(image);
        var lowNormalized = ScreenshotNormalizer.EnsureAnalysable(Encode(image), 960, 540);
        image.SetResolution(144, 144);
        var high = FieldPixels(image);
        using var highRoster = RosterCoordinates.Normalize(image);
        var highNormalized = ScreenshotNormalizer.EnsureAnalysable(Encode(image), 960, 540);
        Assert.Equal(low, high);
        // 检查右下角确实来自原图，避免两个空缓冲区碰巧相等。
        int pixel = (900 * 1920 + 1500) * 3;
        Assert.Equal(new byte[] { 255, 0, 0 }, high[pixel..(pixel + 3)]);
        Assert.Equal(Bytes(lowRoster), Bytes(highRoster));
        using var lowDecoded = new Bitmap(new MemoryStream(lowNormalized.Png));
        using var highDecoded = new Bitmap(new MemoryStream(highNormalized.Png));
        Assert.Equal(Bytes(lowDecoded), Bytes(highDecoded));
    }

    [Theory]
    [InlineData("field-57.jpg", true)]
    [InlineData("prep-ready-1920x1080.png", false)]
    public async Task FieldEntryPreservesCellsAndSpritePixelsAcrossDpi(string sample, bool shopVisible)
    {
        string data = Path.Combine(AppContext.BaseDirectory, "data");
        using var image = new Bitmap(Path.Combine(AppContext.BaseDirectory, "TestData", sample));
        image.SetResolution(96, 96);
        var low = Encode(image);
        image.SetResolution(144, 144);
        var high = Encode(image);
        var at = DateTimeOffset.UtcNow;
        var a = await new FieldReader(data, new EmptyOcr(), []).ReadAsync(low, at, new(1, null, null, null, true), true, shopVisible, []);
        var b = await new FieldReader(data, new EmptyOcr(), []).ReadAsync(high, at, new(1, null, null, null, true), true, shopVisible, []);
        Assert.Contains(a.Cells, p => p.Value > 0);
        Assert.Equal(a.Cells.OrderBy(p => p.Key.ToString()), b.Cells.OrderBy(p => p.Key.ToString()));
        Assert.NotEmpty(a.Sprites!);
        foreach (var (cell, sprite) in a.Sprites!) Assert.Equal(sprite.Image, b.Sprites![cell].Image);
        var recognizer = new BoardRecognizer(data);
        var first = recognizer.RecognizeDetailed(low);
        var second = recognizer.RecognizeDetailed(high);
        Assert.Equal(first.Score, second.Score);
        Assert.Equal(first.MapId, second.MapId);
        Assert.Equal(first.Rejection, second.Rejection);
    }

    private static byte[] FieldPixels(Bitmap bitmap)
    {
        // 读取实际识别缓冲区，不为测试扩大产品接口。
        var type = typeof(FieldReader).GetNestedType("Pixels", BindingFlags.NonPublic)!;
        var pixels = Activator.CreateInstance(type, [bitmap]);
        return (byte[])type.GetField("_bytes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pixels)!;
    }

    private static byte[] Encode(Bitmap bitmap)
    {
        using var output = new MemoryStream();
        bitmap.Save(output, ImageFormat.Png);
        return output.ToArray();
    }

    private static byte[] Bytes(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        var bytes = new byte[data.Stride * bitmap.Height];
        Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
        bitmap.UnlockBits(data);
        return bytes;
    }

    private sealed class EmptyOcr : IOcrEngine
    {
        public Task<IReadOnlyList<OcrTextResult>> RecognizeRegionAsync(byte[] b, int x, int y, int w, int h) => Task.FromResult<IReadOnlyList<OcrTextResult>>([]);
        public Task<IReadOnlyList<OcrTextResult>> RecognizeLineAsync(byte[] b, int x, int y, int w, int h) => RecognizeRegionAsync(b, x, y, w, h);
        public Task<string?> FindCandidateInRegionAsync(byte[] b, int x, int y, int w, int h, IReadOnlyList<string> c) => Task.FromResult<string?>(null);
    }
}
