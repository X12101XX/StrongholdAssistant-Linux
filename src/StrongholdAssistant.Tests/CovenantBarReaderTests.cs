using System.Drawing;
using System.Text.Json;
using ASPAssistant.Core.Services;
using ASPAssistant.Tests.Services;
using Xunit.Abstractions;

namespace StrongholdAssistant.Tests;

[Collection("Native OCR")]
public sealed class CovenantBarReaderTests(EnemyRecognitionFixture fixture, ITestOutputHelper output)
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");
    private CovenantBarReader Reader() => new(fixture.Engine, Data,
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(Data, "covenant_ids.json")))!
            .ToDictionary(p => p.Value, p => p.Key));

    [Theory]
    [InlineData(140, "doc:-:2,raid:-:0,empty:-:-")]
    [InlineData(500, "mira:1:3,preci:1:0,skillful:1:0,doc:-:2,visi:-:0,raid:-:0,empty:-:-")]
    [InlineData(1222, "mira:1:48,skillful:2:45,preci:2:41,doc:1:18,visi:-:12,empty:-:-,sunt:1:-")]
    [InlineData(1200, "skillful:1:8,preci:1:2,yan:-:8,stead:-:1,indom:-:1,mira:-:0,empty:-:-")]
    public async Task ReadsAcceptedCellsIncludingZeroAndSecondLevel(int second, string expected)
    {
        using var bar = new Bitmap(Path.Combine(AppContext.BaseDirectory, "TestData", $"covenant-bar-{second}.png"));
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var result = await Reader().ReadBarAsync(bar);
        output.WriteLine($"{second}: {timer.Elapsed.TotalMilliseconds:F1} ms");
        Assert.False(result.Skipped);
        Assert.Equal(expected, string.Join(",", result.Items.Select(i => $"{i.Id}:{i.Level?.ToString() ?? "-"}:{i.Layers?.ToString() ?? "-"}")));
    }

    [Theory]
    [InlineData(550)]
    [InlineData(1785)]
    [InlineData(2015)]
    public async Task SkipsCooperativeDefense(int second)
    {
        using var bar = new Bitmap(Path.Combine(AppContext.BaseDirectory, "TestData", $"covenant-bar-{second}.png"));
        var result = await Reader().ReadBarAsync(bar);
        Assert.True(result.Skipped);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ReportsOriginalAndTightNamesWithoutRejectingRecoveredCells()
    {
        using var bar = new Bitmap(Path.Combine(AppContext.BaseDirectory, "TestData", "covenant-bar-1200.png"));
        var diagnostics = new List<CovenantBarDiagnostic>();
        await Reader().ReadBarAsync(bar, diagnostics.Add);
        Assert.Contains(diagnostics, d => d.NameText == "不届" && d.TightNameText == "不屈" && d.Rejection is null);
        Assert.All(diagnostics, d => Assert.Null(d.Rejection));
        Assert.All(diagnostics.SelectMany(d => d.DigitMargins), margin => Assert.True(margin >= .025));
    }
}
