using ASPAssistant.Core.Services;
using Xunit.Abstractions;

namespace StrongholdAssistant.Tests;

public sealed class RosterScrollTests(ITestOutputHelper output)
{
    [Fact]
    public void MeasuredGridKeepsACompleteColumnAcrossEveryScrollPhase()
    {
        var samples = Environment.GetEnvironmentVariable("ROSTER_SAMPLES");
        var files = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "TestData"), "roster-*.jpg");
        if (samples is not null)
            files = files.Concat(Enumerable.Range(0, 37).Select(i => Path.Combine(samples, $"scan{i:00}.png")))
                .Concat(Directory.GetFiles(Path.Combine(samples, "mumu"), "*.png")).ToArray();
        foreach (var file in files)
        {
            var frame = File.ReadAllBytes(file);
            var (width, spacing) = RosterReader.MeasureScroll(frame);
            bool mumu = file.Contains("mumu");
            Assert.Equal(1920, width);
            Assert.InRange(spacing, mumu ? 343 : 308, mumu ? 347 : 312);
            var scroll = new RosterScroll();
            int first = scroll.NextDrag(frame, Reading(800, 1200));
            Assert.Equal((int)spacing, first);
            // 用实测 PC 比例，以及更大的模拟器位移比例检验校准；后者是离线压力条件。
            double ratio = mumu ? 1.3 : 1224.0 / 1240;
            int shift = (int)Math.Ceiling(first * ratio);
            int drag = scroll.NextDrag(frame, Reading(800 - shift, 1200 - shift));
            double movement = drag * ratio;
            Assert.InRange(movement, 1, width - 2 * spacing);
            // 完整卡需要左侧 60、右侧 205 个参考像素；遍历所有列相位。
            double scale = mumu ? 10.0 / 9 : 1;
            for (int phase = 0; phase < spacing; phase++)
                Assert.True(Enumerable.Range(0, 10).Any(i =>
                {
                    double x = phase + i * spacing;
                    return x >= movement + 60 * scale && x + 205 * scale < width;
                }), $"{Path.GetFileName(file)} 相位 {phase} 没有完整重叠列");
            // 末端截短位移不会把已校准的距离放大。
            Assert.Equal(drag, scroll.NextDrag(frame, Reading(790 - shift, 1190 - shift)));
            output.WriteLine($"{Path.GetFileName(file)} width={width} spacing={spacing} first={first} drag={drag} movement={movement:F2}");
        }
    }

    private static RosterReading Reading(int x1, int x2) => new(
        [Card("甲", x1), Card("乙", x2)], null, 0, []);
    private static RosterCard Card(string name, int x) => new(
        new(name, null, null, null, null, ""), false, x, 200, 0, 0, "", 0, null, null);
}
