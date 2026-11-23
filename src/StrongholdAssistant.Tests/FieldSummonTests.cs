using System.Drawing;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using ASPAssistant.Core.Placement;
using Moq;

namespace StrongholdAssistant.Tests;

public sealed class FieldSummonTests
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    [Fact]
    public void RecordedBirdIsNamedWithoutBecomingAnOperator()
    {
        using var reader = new FieldReader(Data, Mock.Of<IOcrEngine>(), []);
        using var sprite = new Bitmap(Path.Combine(AppContext.BaseDirectory, "TestData", "field-equipment-tier4.png"));
        using var frame = new Bitmap(1920, 1080);
        using (var g = Graphics.FromImage(frame))
        {
            g.Clear(Color.Black);
            g.DrawImage(sprite, new Rectangle(218, 560, sprite.Width, sprite.Height), new Rectangle(0, 0, sprite.Width, sprite.Height), GraphicsUnit.Pixel);
        }
        Assert.DoesNotContain(reader.ReadCells(frame, null, true), p => p.Value > 0);
        var summon = Assert.Single(reader.Summons);
        Assert.Equal("幻影", summon.Name);
        Assert.Equal(new FieldSummon("bench", 1, 0, "幻影", null), summon);
    }

    [Fact]
    public void SummonsPublishIndependentlyResolveOwnerAndRespectVisibility()
    {
        var tracker = new FieldTracker(); tracker.ConfigureSummons(SummonCatalog.Load(Data));
        var time = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var owner = new FieldCell("board", 2, 2); var cell = new FieldCell("board", 3, 2);
        var observation = new FieldObservation(time, true, "m01", 1, new Dictionary<FieldCell, int> { [owner] = 4, [cell] = 0 })
            { Summons = [new("board", 3, 2, "幻影", null)] };
        tracker.Observe(observation); tracker.SetOperator(owner, "夜莺", time);
        Assert.Equal("夜莺", Assert.Single(tracker.State!.Summons!).Owner);
        Assert.Single(tracker.State.Units);
        Assert.DoesNotContain(tracker.State.Roster, r => r.Operator == "幻影");
        tracker.Observe(observation with { At = time.AddSeconds(1), Cells = new Dictionary<FieldCell, int> { [owner] = 4 }, Summons = [] });
        Assert.Single(tracker.State.Summons!);
        tracker.Observe(observation with { At = time.AddSeconds(2), Summons = [] });
        Assert.Empty(tracker.State.Summons!);
    }

    [Fact]
    public async Task PlannerDoesNotPlaceOperatorsOnSummonsEvenWithStartingOrPinnedCells()
    {
        var store = new ASPAssistant.Core.Placement.JsonDataStore(Data);
        var ops = (await store.LoadOperatorsAsync()).ToDictionary(o => o.Name);
        var (covenants, _) = await store.LoadCovenantsAsync();
        var map = (await GarrisonBoardStore.LoadAsync(Data))!.Find("m01")!;
        var planner = new PlacementPlanner(ops, covenants);
        var occupied = map.AllCells().First(c => GarrisonMap.CanDeploy(c.Type, true)).Cell;
        var options = new PlacementOptions { FieldCount = 1, Simulate = false, Iterations = 50, Restarts = 1,
            OccupiedCells = new HashSet<BoardCell> { occupied },
            Start = new Dictionary<string, (BoardCell Cell, Facing Facing)> { ["角峰"] = (occupied, Facing.Right) },
            Pins = new Dictionary<string, PlacementPin> { ["角峰"] = new(occupied, Facing.Right) } };
        Assert.NotEqual(occupied, Assert.Single(planner.Plan(map, [new("角峰", false)], options).Placed).Cell);
        var bird = SummonCatalog.Load(Data).Single(s => s.Name == "幻影");
        Assert.Equal(0, bird.BaseBlock);
        Assert.Equal(2, SummonCatalog.Load(Data).Single(s => s.Name == "机动盾牌").BaseBlock);
    }
}
