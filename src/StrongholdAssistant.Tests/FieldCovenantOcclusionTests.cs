using System.Drawing;
using System.Text.Json;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Placement;
using ASPAssistant.Core.Services;

namespace StrongholdAssistant.Tests;

public sealed class FieldCovenantOcclusionTests
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    [Theory]
    [InlineData("0626")]
    [InlineData("0751")]
    public void CovenantTextOverFirstRowStillReadsFifthTier(string second)
    {
        // 2026-11-15 险境录屏的原始游戏裁片，归上海鹰角网络所有；不含昵称。
        using var crop = new Bitmap(Path.Combine(AppContext.BaseDirectory, "TestData", $"field-covenant-{second}.png"));
        using var frame = new Bitmap(1920, 1080);
        using (var g = Graphics.FromImage(frame)) g.DrawImage(crop, new Rectangle(1110, 130, crop.Width, crop.Height), new Rectangle(0, 0, crop.Width, crop.Height), GraphicsUnit.Pixel);
        var reader = new FieldReader(Data, null!, []);
        var cells = reader.ReadCells(frame, null, true);
        Assert.Equal(5, cells[new("board", 5, 0)]);
        // 同一裁片还保留下一排未遮挡的三阶角标，不能将它当成空格。
        Assert.Equal(3, cells[new("board", 5, 1)]);
        Assert.Equal(2, cells.Count(p => p.Value > 0));
    }

    [Fact]
    public async Task DesertCratesForbiddenRockIsNeitherDeploymentNorReadAnchor()
    {
        var map = (await GarrisonBoardStore.LoadAsync(Data))!.Find("m01")!;
        // 关卡 buildableType=NONE/tile_forbidden；木箱实际只有 (2,1)、(2,2)、(3,2) 三格。
        Assert.Equal(new[] { "XXGGGGGGXSXX", "XXGGXGGGXXXX", "XXGGXXGGXXXX", "XDGGXXXGGSXX", "XXXXXXXXXXXX" }, map.CellRows);
        Assert.False(GarrisonMap.CanDeploy(map.At(new(2, 3)), melee: false));
        Assert.False(GarrisonMap.CanDeploy(map.At(new(2, 3)), melee: true));
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Data, "garrison_board_refs.json")));
        var projection = new BoardProjection(doc.RootElement.GetProperty("cameras").GetProperty("expanded")
            .EnumerateArray().Select(v => ((v[0][0].GetDouble(), v[0][1].GetDouble()),
                (v[1][0].GetDouble(), v[1][1].GetDouble()))).ToArray());
        using var frame = new Bitmap(1920, 1080);
        var reader = new FieldReader(Data, null!, []);
        var cells = reader.ReadCells(frame, new("m01", BoardCamera.Expanded, projection, 0), true);
        Assert.DoesNotContain(new FieldCell("board", 2, 3), cells.Keys);
        Assert.DoesNotContain(new FieldCell("board", 2, 3), reader.OverlayGeometry!.Anchors.Keys);
    }
}
