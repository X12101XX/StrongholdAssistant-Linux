using System.Text.Json;
using ASPAssistant.Core.Placement;
using StrongholdAssistant.Host;

namespace StrongholdAssistant.Tests;

public sealed class RoundEnemyTests
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");
    private static Task<HostApi> Api() => HostApi.CreateAsync(Data, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

    private static void Round(HostApi api, int round)
    {
        api.ObserveEconomy(new(round, 10, 1, 4, true));
        api.ObserveEconomy(new(round, 10, 1, 4, true));
    }

    [Fact]
    public async Task ReadsManualEditsAndRestorationMergeInOrder()
    {
        var api = await Api();
        Round(api, 5);
        api.ApplyRoundEnemies(5, ["源石虫", "宿主士兵"]);
        var events = new List<MatchInfo>();
        api.Changed += (kind, value) => { if (kind == "match") events.Add((MatchInfo)value); };
        var added = Assert.IsType<MatchInfo>(await api.InvokeAsync("setRoundEnemy",
            [JsonSerializer.SerializeToElement("测试用不存在的敌人"), JsonSerializer.SerializeToElement(true)]));
        Assert.Same(added, Assert.Single(events));
        Assert.Equal("manual", added.RoundEnemies!.Enemies[^1].Source);
        Assert.NotNull(added.RoundEnemies.Enemies[0].Id);
        Assert.Null(added.RoundEnemies.Enemies[^1].Id);
        Assert.True(DateTimeOffset.TryParse(added.RoundEnemies.At, out _));
        api.ApplyRoundEnemies(5, ["宿主士兵", "未知敌人"]);
        Assert.Equal(new[] { "源石虫", "宿主士兵", "未知敌人", "测试用不存在的敌人" }, api.Match.RoundEnemies!.Enemies.Select(e => e.Name));
        Assert.Null(api.Match.RoundEnemies.Enemies[2].Id);
        api.SetRoundEnemy("源石虫", false);
        api.SetRoundEnemy("宿主士兵", false);
        api.ApplyRoundEnemies(5, ["源石虫", "宿主士兵"]);
        Assert.Equal(2, api.Match.RoundEnemies.Removed.Length);
        Assert.Equal(new[] { "未知敌人", "测试用不存在的敌人" }, api.Match.RoundEnemies.Enemies.Select(e => e.Name));
        api.SetRoundEnemy("宿主士兵", true);
        api.SetRoundEnemy("源石虫", true);
        Assert.Equal(new[] { "源石虫", "宿主士兵", "未知敌人", "测试用不存在的敌人" }, api.Match.RoundEnemies.Enemies.Select(e => e.Name));
        Assert.Empty(api.Match.RoundEnemies.Removed);
        api.SetRoundEnemy("测试用不存在的敌人", false);
        Assert.Empty(api.Match.RoundEnemies.Removed);
        api.SetRoundEnemy("测试用不存在的敌人", true);
        api.SetRoundEnemy("宿主流浪者", true);
        var before = api.Match.RoundEnemies.Enemies.Select(e => e.Name).ToArray();
        api.ApplyRoundEnemies(5, ["宿主流浪者"]);
        Assert.Equal(before, api.Match.RoundEnemies.Enemies.Select(e => e.Name));
        Assert.Equal("read", api.Match.RoundEnemies.Enemies[^1].Source);
        api.SetRoundEnemy("宿主流浪者", false);
        Assert.Equal("宿主流浪者", Assert.Single(api.Match.RoundEnemies.Removed).Name);
    }

    [Fact]
    public async Task RoundAndMatchChangesClearManualAndRemovedEntries()
    {
        var api = await Api();
        Round(api, 5);
        api.ApplyRoundEnemies(5, ["源石虫"]);
        api.SetRoundEnemy("源石虫", false);
        api.SetRoundEnemy("泥岩巨像", true);
        Round(api, 6);
        api.ApplyRoundEnemies(6, ["源石虫"]);
        Assert.Equal("源石虫", Assert.Single(api.Match.RoundEnemies!.Enemies).Name);
        Assert.Empty(api.Match.RoundEnemies.Removed);
        Round(api, 7);
        api.SetRoundEnemy("泥岩巨像", true);
        Assert.Equal(7, api.Match.RoundEnemies.Round);
        Assert.Equal("manual", Assert.Single(api.Match.RoundEnemies.Enemies).Source);
        api.ApplyRecognizedMatch(api.Match, [], opening: true);
        Assert.Null(api.Match.RoundEnemies);
        Round(api, 1);
        api.SetRoundEnemy("泥岩巨像", true);
        await api.ResetMatchAsync();
        Assert.Null(api.Match.RoundEnemies);
    }

    [Fact]
    public async Task UnknownRoundReturnsErrorAndNoOptions()
    {
        var api = await Api();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => api.InvokeAsync("setRoundEnemy",
            [JsonSerializer.SerializeToElement("源石虫"), JsonSerializer.SerializeToElement(true)]));
        Assert.Equal("还不知道现在是第几回合，等助手读到回合数再补", error.Message);
        Assert.Null(await api.InvokeAsync("roundEnemyOptions", []));
        Assert.Null(api.Match.RoundEnemies);
    }

    [Theory]
    [InlineData(5, "mode_multi_normal", false, null)]
    [InlineData(5, "mode_single_20", true, null)]
    [InlineData(15, "mode_multi_normal", false, "enemy_1521_dslily")]
    [InlineData(20, "mode_single_20", true, null)]
    public async Task OptionsUseTheSameStreamsAsPlacement(int round, string mode, bool solo, string? boss)
    {
        var api = await Api();
        api.ReadOpeningMode = () => Task.FromResult<ASPAssistant.Core.Models.GameModeInfo?>(new(mode, mode));
        if (solo) await api.HandleOpeningAsync(true);
        api.ApplyRecognizedMatch(api.Match with { Mode = new(mode, mode), Solo = solo, BossId = boss,
            EnemyTypes = ["FLY", "DOT"] }, []);
        Assert.Equal(solo, api.Match.Solo);
        Round(api, round);
        var adapter = await PlacementAdapter.LoadAsync(Data);
        var streams = adapter.RoundStreams(round, mode, solo, api.Match.EnemyTypes, boss);
        var response = await api.InvokeAsync("roundEnemyOptions", []);
        if (streams.Count == 0) { Assert.Null(response); return; }
        var options = Assert.IsType<RoundEnemyOptions>(response);
        Assert.Equal(round, options.Round);
        Assert.Equal(streams.Select(s => s.Label), options.Groups.Select(g => g.Label));
        Assert.Equal(streams.Select(s => s.Weight), options.Groups.Select(g => g.Weight));
        Assert.Equal(1, options.Groups.Sum(g => g.Weight), 8);
        foreach (var (stream, group) in streams.Zip(options.Groups))
        {
            Assert.Equal(stream.Spawns.Count, group.Enemies.Sum(e => e.Count));
            Assert.Equal(stream.Spawns.GroupBy(s => s.Profile.Name).Select(g => (g.Key, g.Count())),
                group.Enemies.Select(e => (e.Name, e.Count)));
            foreach (var enemy in group.Enemies)
            {
                var entry = api.Catalog.GetProperty("enemies").EnumerateArray()
                    .FirstOrDefault(e => e.GetProperty("name").GetString() == enemy.Name);
                Assert.Equal(entry.ValueKind == JsonValueKind.Undefined ? null : entry.GetProperty("id").GetString(), enemy.Id);
            }
        }
    }

    [Fact]
    public async Task BountyDoesNotInvalidateKnownEnemyFiltering()
    {
        var adapter = await PlacementAdapter.LoadAsync(Data);
        var streams = adapter.RoundStreams(5, "mode_multi_normal", false, [], null);
        const string bounty = "泥岩巨像";
        Assert.DoesNotContain(streams.SelectMany(s => s.Spawns), s => s.Profile.Name == bounty);
        string name = streams.SelectMany(s => s.Spawns).Select(s => s.Profile.Name).Distinct()
            .First(n => streams.Any(s => s.Spawns.All(e => e.Profile.Name != n)));
        var matching = streams.Where(s => s.Spawns.Any(e => e.Profile.Name == name)).Select(s => s.Label).ToArray();
        var input = new PlacementInput(new("m10", "now", [new("board", 2, 2, "塔露拉", "sure", false, [])]),
            5, "mode_multi_normal", false, null, [], [name, bounty], null, null, null,
            new Dictionary<string, string?>(), "balanced");
        var result = adapter.Compute(input)!;
        Assert.NotNull(result);
        Assert.Contains($"{bounty} 不在本回合怪组里，没算进模拟", result.Assumptions);
        Assert.DoesNotContain("本回合敌人名单没匹配到怪组，按本局敌人类型算", result.Assumptions);
        Assert.NotEmpty(result.Recommended!.Streams);
        Assert.All(result.Recommended.Streams, s => Assert.Contains(s.Label, matching));
        Assert.All(result.Current!.Streams, s => Assert.Contains(s.Label, matching));
        var onlyBounty = adapter.Compute(input with { RoundEnemyNames = [bounty] })!;
        Assert.DoesNotContain("本回合敌人名单没匹配到怪组，按本局敌人类型算", onlyBounty.Assumptions);
        Assert.NotEmpty(onlyBounty.Recommended!.Streams);
    }
}
