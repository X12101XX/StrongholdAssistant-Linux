using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ASPAssistant.Core.Data;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Placement;
using ASPAssistant.Core.Services;
using StrongholdAssistant.Host;

namespace StrongholdAssistant.Tests;

public sealed class BoardPlacementWiringTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "StrongholdAssistant.Tests", Guid.NewGuid().ToString("N"));
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    [Fact]
    public async Task BoardReturnsOriginalCellsForNormalAndBossRoundsAndNullForMissingMap()
    {
        var api = await HostApi.CreateAsync(Data, _directory);
        var tracker = (FieldTracker)typeof(HostApi).GetField("_fieldTracker", PrivateInstance)!.GetValue(api)!;
        tracker.Observe(new(DateTimeOffset.UtcNow, true, "m01", 1, new Dictionary<FieldCell, int>()));
        api.ObserveEconomy(new(6, 0, 1, 0, true));
        api.ObserveEconomy(new(6, 0, 1, 0, true));
        var board = Assert.IsType<BoardView>(await api.InvokeAsync("board", []));
        Assert.NotNull(board.Cells);
        Assert.Equal(5, board.Cells.Length);
        Assert.All(board.Cells, row => Assert.Equal(12, row.Length));
        Assert.Equal("XXGGGGGGXSXX", board.Cells[0]);
        var json = JsonSerializer.SerializeToElement(board, HostApi.JsonOptions);
        Assert.Equal("XXGGGGGGXSXX", json.GetProperty("cells")[0].GetString());
        api.ObserveEconomy(new(14, 0, 1, 0, true));
        api.ObserveEconomy(new(14, 0, 1, 0, true));
        var boss = api.Board()!;
        Assert.True(boss.BossRound);
        Assert.Same(board.Cells, boss.Cells);

        // 构造只有路线、没有部署格的地图，验证 board 本身仍然存在。
        var routes = JsonNode.Parse(File.ReadAllText(Path.Combine(Data, "garrison_routes.json")))!;
        routes["maps"]!["missing"] = routes["maps"]!["m01"]!.DeepClone();
        File.WriteAllText(Path.Combine(_directory, "garrison_routes.json"), routes.ToJsonString());
        typeof(HostApi).GetField("_boardRouteStore", PrivateInstance)!.SetValue(api, new BoardRouteStore(_directory));
        tracker.Observe(new(DateTimeOffset.UtcNow, true, "missing", 14, new Dictionary<FieldCell, int>()));
        var missing = Assert.IsType<BoardView>(await api.InvokeAsync("board", []));
        Assert.Null(missing.Cells);
    }

    [Fact]
    public async Task PlacementPublicationAndResetNotifyOverlayWithMatchingMoves()
    {
        var api = await HostApi.CreateAsync(Data, _directory);
        var received = new List<OverlayPlacementPlan?>();
        var bridge = new List<object?>();
        api.PlacementOverlayChanged += received.Add;
        api.Changed += (name, value) => { if (name == "placement") bridge.Add(value); };
        var result = new PlacementResult("snapshot", "computed", "balanced",
            [new("芬", new("bench", 1, 0), new("board", 2, 3), "right"),
             new("芬", new("board", 3, 2), new("bench", 2, 0), null)],
            0, [], null, null, []);
        var publish = typeof(HostApi).GetMethod("PublishPlacement", PrivateInstance)!;
        publish.Invoke(api, [result, 0, null]);
        var overlay = Assert.IsType<OverlayPlacementPlan>(Assert.Single(received));
        Assert.Same(result, Assert.Single(bridge));
        Assert.Equal(result.BasedOn, overlay.BasedOn);
        Assert.Equal(result.Moves.Select(move => new OverlayPlacementMove(move.Operator, move.From, move.To, move.Facing)), overlay.Moves);
        // 启动时使用同一转换函数读取当前推荐。
        Assert.Equal(overlay.BasedOn, HostApi.ToOverlayPlacement(api.Placement)!.BasedOn);
        Assert.Equal(overlay.Moves, HostApi.ToOverlayPlacement(api.Placement)!.Moves);
        publish.Invoke(api, [null, 0, null]);
        Assert.Null(received[1]);
        Assert.Null(bridge[1]);
        publish.Invoke(api, [result, 0, null]);
        typeof(HostApi).GetMethod("ResetPlacement", PrivateInstance)!.Invoke(api, [true]);
        Assert.Equal(4, received.Count);
        Assert.Null(received[3]);
        Assert.Null(bridge[3]);
        Assert.Null(api.Placement);
    }

    [Fact]
    public async Task PlacementIgnoresIdentityMetadataAndKeepsPreviousResultDuringRecompute()
    {
        var api = await HostApi.CreateAsync(Data, _directory);
        api.ObserveEconomy(new(1, 10, 1, 0, true));
        api.ObserveEconomy(new(1, 10, 1, 0, true));
        var tracker = (FieldTracker)typeof(HostApi).GetField("_fieldTracker", PrivateInstance)!.GetValue(api)!;
        var fieldProperty = typeof(FieldTracker).GetProperty("State")!;
        // 场上自动识别暂时固定关闭（v1.2.4，读写设置都按 false）；这里验证的是留着的自动重算逻辑，只在内存里打开
        typeof(HostApi).GetProperty("Settings")!.SetValue(api, api.Settings with { FieldAuto = true });
        var schedule = typeof(HostApi).GetMethod("SchedulePlacement", PrivateInstance)!;
        var generation = typeof(HostApi).GetField("_placementGeneration", PrivateInstance)!;
        var state = new FieldState("m01", 1, "first", [new("board", 2, 2, 1, "芬", "sprite") { Status = "auto" }], []);
        fieldProperty.SetValue(tracker, state);
        schedule.Invoke(api, null);
        int initial = (int)generation.GetValue(api)!;
        fieldProperty.SetValue(tracker, state with { UpdatedAt = "confirmed", Units = [state.Units[0] with { Source = "manual", Status = "sure" }] });
        schedule.Invoke(api, null);
        Assert.Equal(initial, generation.GetValue(api));
        var result = new PlacementResult("confirmed", "computed", "balanced", [], 0, [], null, null, []);
        typeof(HostApi).GetMethod("PublishPlacement", PrivateInstance)!.Invoke(api, [result, initial, null]);
        fieldProperty.SetValue(tracker, state with { UpdatedAt = "equipped", Units = [state.Units[0] with { Equipment = [new("坚守盾牌", false)] }] });
        schedule.Invoke(api, null);
        Assert.Equal(initial + 1, generation.GetValue(api));
        Assert.Same(result, api.Placement);
        typeof(HostApi).GetMethod("ResetPlacement", PrivateInstance)!.Invoke(api, [true]);
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
