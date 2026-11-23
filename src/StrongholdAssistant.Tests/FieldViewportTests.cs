using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using OpenCvSharp;

namespace StrongholdAssistant.Tests;

public sealed class FieldViewportTests
{
    [Theory]
    [InlineData("field-57.jpg", true, 64, 0)]
    [InlineData("prep-ready-1920x1080.png", false, 64, 0)]
    [InlineData("field-57.jpg", true, 0, 60)]
    [InlineData("prep-ready-1920x1080.png", false, 0, 60)]
    public async Task ViewportPreservesCellsSpritesAndMapsOverlayBack(string sample, bool shopVisible, int left, int top)
    {
        var data = Path.Combine(AppContext.BaseDirectory, "data");
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", sample));
        using var original = Cv2.ImDecode(bytes, ImreadModes.Color);
        using var canonical = new Mat();
        Cv2.Resize(original, canonical, new Size(1920, 1080));
        using var wide = new Mat();
        Cv2.CopyMakeBorder(canonical, wide, top, top, left, left, BorderTypes.Constant, Scalar.All(0));
        // 路线诊断直接读取原图，也必须使用同一棋盘画幅并还原投影。
        var recognizer = new BoardRecognizer(data);
        var canonicalMatch = recognizer.RecognizeDetailed(canonical.ImEncode(".png"));
        var wideMatch = recognizer.RecognizeDetailed(wide.ImEncode(".png"));
        Assert.Equal(canonicalMatch.Score, wideMatch.Score);
        Assert.Equal(canonicalMatch.Rejection, wideMatch.Rejection);
        Assert.Equal(wide.Width, wideMatch.Width);
        Assert.Equal(wide.Height, wideMatch.Height);
        if (canonicalMatch.Match is { } matched)
        {
            Assert.NotNull(wideMatch.Match);
            var point = matched.Projection.Project(0, 3);
            var restored = wideMatch.Match.Projection.Project(0, 3);
            Assert.Equal(point.X * 1920 + left, restored.X * wide.Width, 3);
            Assert.Equal(point.Y * 1080 + top, restored.Y * wide.Height, 3);
        }
        var normalReader = new FieldReader(data, new EmptyOcr(), []);
        var wideReader = new FieldReader(data, new EmptyOcr(), []);
        var at = DateTimeOffset.UtcNow;
        var normal = await normalReader.ReadAsync(canonical.ImEncode(".png"), at, new(1, null, null, null, true), true, shopVisible, []);
        var actual = await wideReader.ReadAsync(wide.ImEncode(".png"), at, new(1, null, null, null, true), true, shopVisible, []);
        Assert.NotEmpty(normal.Sprites!);
        Assert.Equal(normal.Cells.OrderBy(p => p.Key.ToString()), actual.Cells.OrderBy(p => p.Key.ToString()));
        foreach (var (cell, patch) in normal.Sprites!)
        {
            Assert.Equal(patch.Image, actual.Sprites![cell].Image);
            var a = normalReader.OverlayGeometry!.Anchors[cell];
            var b = wideReader.OverlayGeometry!.Anchors[cell];
            Assert.Equal(a.X * 1920 + left, b.X * wide.Width, 3);
            Assert.Equal(a.Y * 1080 + top, b.Y * wide.Height, 3);
            var badge = normalReader.OverlayGeometry.Badges[cell];
            var restored = wideReader.OverlayGeometry.Badges[cell];
            Assert.Equal(badge.X * 1920 + left, restored.X * wide.Width, 3);
            Assert.Equal(badge.Y * 1080 + top, restored.Y * wide.Height, 3);
            Assert.Equal(badge.Size * 1080, restored.Size * wide.Height, 3);
            var point = normalReader.OverlayGeometry.Projection.Project(cell.Col, cell.Row);
            var mapped = wideReader.OverlayGeometry.Projection.Project(cell.Col, cell.Row);
            Assert.Equal(point.X * 1920 + left, mapped.X * wide.Width, 3);
            Assert.Equal(point.Y * 1080 + top, mapped.Y * wide.Height, 3);
        }
        // 原画面中的鼠标遮挡必须落到转换后的同一格。
        var target = actual.Sprites!.Keys.First();
        var anchor = wideReader.OverlayGeometry!.Anchors[target];
        var obscured = await wideReader.ReadAsync(wide.ImEncode(".png"), at, new(1, null, null, null, true), true, shopVisible, [],
            new System.Drawing.RectangleF((float)anchor.X - .01f, (float)anchor.Y - .01f, .02f, .02f));
        Assert.DoesNotContain(target, obscured.Cells.Keys);
    }

    [Fact]
    public void TwoSameNameUnitsDoNotPromoteTheSecondTier()
    {
        var tracker = new FieldTracker();
        tracker.ConfigureIdentity([new() { Name = "甲", Tier = 1 }],
            (_, _, _) => new([new("甲", .9)], .9));
        var cells = new Dictionary<FieldCell, int> { [new("board", 1, 1)] = 1, [new("board", 2, 1)] = 1 };
        var observation = new FieldObservation(DateTimeOffset.UtcNow, true, "m01", 1, cells);
        tracker.Observe(observation, force: true);
        foreach (var cell in cells.Keys) tracker.SetOperator(cell, "甲", observation.At);
        tracker.Observe(observation with { At = observation.At.AddSeconds(1) }, force: true);
        Assert.Equal(2, tracker.State!.Units.Length);
        Assert.All(tracker.State.Units, u => { Assert.Equal("甲", u.Operator); Assert.Equal(1, u.Tier); });
    }

    private sealed class EmptyOcr : IOcrEngine
    {
        public Task<IReadOnlyList<OcrTextResult>> RecognizeRegionAsync(byte[] b, int x, int y, int w, int h)
            => Task.FromResult<IReadOnlyList<OcrTextResult>>([]);
        public Task<IReadOnlyList<OcrTextResult>> RecognizeLineAsync(byte[] b, int x, int y, int w, int h)
            => RecognizeRegionAsync(b, x, y, w, h);
        public Task<string?> FindCandidateInRegionAsync(byte[] b, int x, int y, int w, int h, IReadOnlyList<string> c)
            => Task.FromResult<string?>(null);
    }
}
