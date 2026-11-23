using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using ASPAssistant.Tests.Services;

namespace StrongholdAssistant.Tests;

[Collection("Native OCR")]
public sealed class FieldEquipmentTests(EnemyRecognitionFixture fixture)
{
    private static byte[] Frame(string path)
    {
        using var panel = new Bitmap(path);
        using var frame = new Bitmap(1920, 1080);
        using (var g = Graphics.FromImage(frame)) g.DrawImage(panel, new Rectangle(0, 180, panel.Width, panel.Height), new Rectangle(0, 0, panel.Width, panel.Height), GraphicsUnit.Pixel);
        using var stream = new MemoryStream(); frame.Save(stream, ImageFormat.Png); return stream.ToArray();
    }

    [Theory]
    [InlineData(263, "暴躁铁皮", "")]
    [InlineData(269, "杰克", "")]
    [InlineData(2298, "云迹", "莱塔尼亚小鼓")]
    [InlineData(2139, "暴躁铁皮", null)]
    public async Task RecordedPanelsReadCompleteListOrRejectClipping(int time, string name, string? expected)
    {
        var reader = new FieldEquipmentReader(Path.Combine(AppContext.BaseDirectory, "data"), fixture.Engine);
        var result = await reader.ReadAsync(Frame(Path.Combine(AppContext.BaseDirectory, "TestData", $"equipment-panel-{time}.png")), name);
        if (expected is null) Assert.Null(result);
        else Assert.Equal(expected.Length == 0 ? [] : new[] { new FieldEquipment(expected, true) }, result);
    }

    [Fact]
    public void DescriptionsDistinguishNormalEliteAndUnknown()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "equipment.json")));
        foreach (var name in new[] { "维式重锤", "莱塔尼亚小鼓" })
        {
            var item = json.RootElement.GetProperty("equipment").EnumerateArray().Single(e => e.GetProperty("name").GetString() == name);
            string normal = item.GetProperty("normal").GetProperty("effectDescription").GetString()!;
            string elite = item.GetProperty("elite").GetProperty("effectDescription").GetString()!;
            Assert.Equal(false, FieldEquipmentReader.CompareQuality($"  {normal}。\n", normal, elite));
            Assert.Equal(true, FieldEquipmentReader.CompareQuality($"\n{elite}， ", normal, elite));
            Assert.Null(FieldEquipmentReader.CompareQuality("无关描述", normal, elite));
            Assert.Null(FieldEquipmentReader.CompareQuality(normal.Replace("15", "35").Replace("2", "3"), normal, elite));
        }
    }

    [Fact]
    public void EquipmentFollowsMoveAndAmbiguousPanelDoesNotWrite()
    {
        var t = new FieldTracker(); var a = new FieldCell("board", 1, 1); var b = new FieldCell("board", 2, 1);
        var now = DateTimeOffset.UtcNow;
        FieldObservation O(Dictionary<FieldCell, int> cells) => new(now, true, "m01", 1, cells);
        t.Observe(O(new() { [a] = 1 })); t.SetOperator(a, "杰克", now);
        FieldEquipment[] equipment = [new("维式重锤", false), new("坚守盾牌", null)];
        t.Observe(O(new()) with { PanelOperator = "杰克", Equipment = equipment });
        t.Observe(O(new() { [a] = 0, [b] = 1 }));t.Observe(O(new() { [a] = 0, [b] = 1 }));
        Assert.Equal(equipment, Assert.Single(t.State!.Units).Equipment);
        t.Observe(O(new() { [a] = 1, [b] = 1 }));t.Observe(O(new() { [a] = 1, [b] = 1 })); t.SetOperator(a, "杰克", now);
        t.Observe(O(new()) with { PanelOperator = "杰克", Equipment = [] });
        Assert.Null(t.State!.Units.Single(u => u.Cell == a).Equipment);
        Assert.Equal(2, t.State.Units.Single(u => u.Cell == b).Equipment!.Length);
    }

    [Fact]
    public void DragReadingAndMergeKeepEquipment()
    {
        var t = new FieldTracker(); var a = new FieldCell("board", 1, 1); var b = new FieldCell("board", 2, 1);
        t.ConfigureIdentity([new() { Name = "杰克", Tier = 1 }], (_, _, _) => new([], 0));
        var c = new FieldCell("bench", 1, 0); var d = new FieldCell("bench", 2, 0); var now = DateTimeOffset.UtcNow;
        FieldObservation O(Dictionary<FieldCell, int> cells) => new(now, true, "m01", 1, cells);
        t.Observe(O(new() { [a] = 1 }));
        FieldEquipment[] equipment = [new("坚守盾牌", true)];
        t.Observe(O(new()) with { DragOperator = "杰克", Equipment = equipment });
        t.Observe(O(new() { [a] = 0, [b] = 1 }));t.Observe(O(new() { [a] = 0, [b] = 1 }));
        Assert.Equal(equipment, Assert.Single(t.State!.Units).Equipment);
        t.Observe(O(new() { [c] = 1, [d] = 1 }));t.Observe(O(new() { [c] = 1, [d] = 1 })); t.SetOperator(c, "杰克", now); t.SetOperator(d, "杰克", now);
        t.Observe(O(new() { [b] = 2, [c] = 0, [d] = 0 }));t.Observe(O(new() { [b] = 2, [c] = 0, [d] = 0 }));
        Assert.Equal(equipment, Assert.Single(t.State!.Units).Equipment);
    }
}
