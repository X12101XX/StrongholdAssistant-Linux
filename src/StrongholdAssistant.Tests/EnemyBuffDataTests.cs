using System.Text.Json;

namespace StrongholdAssistant.Tests;

public class EnemyBuffDataTests
{
    [Fact]
    public async Task UltimateRound13ContainsCumulativeMultipliers()
    {
        using var data = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "data", "enemy_buffs.json")));
        var round = data.RootElement.GetProperty("mode_multi_abyss")[12];
        Assert.Equal(10.23392268288, round.GetProperty("hp").GetDouble(), 10);
        Assert.Equal(1.9487171, round.GetProperty("atk").GetDouble(), 10);
        Assert.Equal(30, round.GetProperty("attackSpeed").GetDouble());
        Assert.Equal(1.3, round.GetProperty("speed").GetDouble());
    }
}
