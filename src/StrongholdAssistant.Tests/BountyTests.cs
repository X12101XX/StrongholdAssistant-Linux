using System.Text.Json;
using StrongholdAssistant.Host;

namespace StrongholdAssistant.Tests;

public sealed class BountyTests
{
    [Fact]
    public async Task CatalogIncludesBountyRewardsAndEveryReferencedEnemy()
    {
        var directory = Path.Combine(Path.GetTempPath(), "StrongholdAssistant.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var api = await HostApi.CreateAsync(Path.Combine(AppContext.BaseDirectory, "data"), directory);
            var catalog = (JsonElement)(await api.InvokeAsync("catalog", []))!;
            var bounties = catalog.GetProperty("bounties").EnumerateArray().ToDictionary(b => b.GetProperty("id").GetString()!);
            var enemies = catalog.GetProperty("enemies").EnumerateArray().ToDictionary(e => e.GetProperty("id").GetString()!);
            Assert.Equal(135, bounties.Count);
            Assert.Equal(117, bounties.Values.Select(b => b.GetProperty("enemyId").GetString()).Distinct().Count());
            Assert.All(bounties.Values, b => Assert.True(enemies.ContainsKey(b.GetProperty("enemyId").GetString()!)));

            var sample = bounties["enemyeffect_16_1"];
            Assert.Equal("泥岩巨像", sample.GetProperty("enemyName").GetString());
            Assert.Equal("泥岩巨像", enemies[sample.GetProperty("enemyId").GetString()!].GetProperty("name").GetString());
            Assert.Equal("kill", sample.GetProperty("kind").GetString());
            Assert.Equal(3, sample.GetProperty("coin").GetInt32());
            Assert.Equal(1, sample.GetProperty("rounds").GetInt32());
            Assert.Equal("特异", sample.GetProperty("tag").GetString());
            Assert.Equal(3, sample.GetProperty("grade").GetInt32());
            Assert.Equal(2, bounties["enemyeffect_10_5"].GetProperty("rounds").GetInt32());
            Assert.Equal(JsonValueKind.Null, bounties["enemyInitial_1"].GetProperty("rounds").ValueKind);
            Assert.Equal("perfect", bounties["enemyeffect_10_1"].GetProperty("kind").GetString());
            Assert.Equal("perfect", bounties["enemyeffect_1"].GetProperty("kind").GetString());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
