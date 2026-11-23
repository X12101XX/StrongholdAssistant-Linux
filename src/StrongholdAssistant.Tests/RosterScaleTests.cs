using ASPAssistant.Core.Services;
using ASPAssistant.Tests.Services;
using Xunit.Abstractions;

namespace StrongholdAssistant.Tests;

[Collection("Native OCR")]
public sealed class RosterScaleTests(EnemyRecognitionFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public async Task PlayerFramesHaveCardsAndMeasuredColumns()
    {
        var samples = Environment.GetEnvironmentVariable("ROSTER_FAIL_SAMPLES");
        if (samples is null) return;
        var reader = new RosterReader(fixture.Engine, Path.Combine(AppContext.BaseDirectory, "data"));
        var files = Directory.GetFiles(samples, "*.png").Where(f => Path.GetFileName(f).StartsWith("161200-")
            || Path.GetFileName(f).StartsWith("172116-")).Order().ToArray();
        Assert.Equal(15, files.Length);
        foreach (var file in files)
        {
            var frame = await File.ReadAllBytesAsync(file);
            // 诊断包还包含一张战斗页，以及一张展开盟约筛选的物资页。
            if (Path.GetFileName(file).StartsWith("161200-20261116-075705211-"))
            {
                Assert.Null(await reader.ReadAsync(frame));
                output.WriteLine($"{Path.GetFileName(file)} | 0 | 不适用：战斗页");
                continue;
            }
            if (Path.GetFileName(file).StartsWith("161200-20261116-080415632-"))
            {
                var error = await Assert.ThrowsAsync<IOException>(() => reader.ReadAsync(frame));
                Assert.Equal("先关掉右侧的盟约筛选", error.Message);
                output.WriteLine($"{Path.GetFileName(file)} | 0 | 不适用：盟约筛选展开");
                continue;
            }
            var reading = await reader.ReadAsync(frame);
            Assert.NotNull(reading);
            Assert.NotEmpty(reading.Cards);
            var (width, spacing) = RosterReader.MeasureScroll(frame);
            Assert.Equal(1920, width);
            Assert.InRange(spacing, 308, 347);
            var sort = await reader.ReadSortAsync(frame);
            Assert.NotNull(sort);
            Assert.Equal("tier", sort.Mode);
            output.WriteLine($"{Path.GetFileName(file)} | {reading.Cards.Length} | {spacing} | {string.Join(",", reading.UnknownNames)}");
        }
    }

    [Fact]
    public async Task PreviousTierSampleStillHasTenCards()
    {
        var sample = Environment.GetEnvironmentVariable("ROSTER_TIER_SAMPLE");
        if (sample is null) return;
        var reader = new RosterReader(fixture.Engine, Path.Combine(AppContext.BaseDirectory, "data"));
        var frame = await File.ReadAllBytesAsync(sample);
        var reading = await reader.ReadAsync(frame);
        Assert.NotNull(reading);
        Assert.Equal(10, reading.Cards.Length);
        Assert.InRange(RosterReader.MeasureScroll(frame).ColumnSpacing, 308, 312);
        output.WriteLine($"239 样图：{reading.Cards.Length} 卡，列距 {RosterReader.MeasureScroll(frame).ColumnSpacing}");
    }

    [Theory]
    [InlineData(2560, 1440, 600)]
    [InlineData(2560, 1600, 680)]
    public void CoordinatesUseSameScaleForSortAndDrag(int width, int height, int dragY)
    {
        Assert.Equal(new System.Drawing.Point(2160, 80), RosterCoordinates.ToScreen(1620, 60, width, height));
        var (start, end) = RosterCoordinates.Drag(true, 345, width, height);
        Assert.Equal(new System.Drawing.Point(740, dragY), start);
        Assert.Equal(new System.Drawing.Point(280, dragY), end);
    }
}
