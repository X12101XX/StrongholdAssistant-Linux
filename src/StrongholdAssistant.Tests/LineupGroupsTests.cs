using System.Text.Json;
using ASPAssistant.Core.Data;
using ASPAssistant.Core.Models;
using StrongholdAssistant.Host;

namespace StrongholdAssistant.Tests;

public sealed class LineupGroupsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "StrongholdAssistant.Tests", Guid.NewGuid().ToString("N"));
    private Task<HostApi> CreateApi() => HostApi.CreateAsync(Path.Combine(AppContext.BaseDirectory, "data"), _directory);
    private static LineupDto Sample() => new("", "一组", [], "user", null, default)
    {
        Groups = [new(1, [new("能天使", Key: true), new("空弦"), new("灰喉")])],
        Upgrades = [new(2, 2), new(4, 3)]
    };

    [Fact]
    public async Task RejectsInvalidGroupsAndUpgradeOrder()
    {
        var api = await CreateApi();
        var valid = Sample();
        LineupDto[] invalid =
        [
            valid with { Groups = [new(1, [new("不存在"), new("能天使")])] },
            valid with { Members = [new("能天使")] },
            valid with { Groups = [.. valid.Groups!, new(1, [new("空弦"), new("初雪")])] },
            valid with { Groups = [new(1, [new("能天使")])] },
            valid with { Groups = [new(0, valid.Groups![0].Members)] },
            valid with { Groups = [new(3, valid.Groups![0].Members)] },
            valid with { Upgrades = [new(4, 2), new(2, 3)] },
            valid with { Upgrades = [new(2, 2), new(2, 3)] },
            valid with { Upgrades = [new(2, 3), new(4, 3)] },
            valid with { Upgrades = [new(2, 1)] },
            valid with { Upgrades = [new(2, 7)] }
        ];
        foreach (var lineup in invalid)
            await Assert.ThrowsAsync<ArgumentException>(() => api.SaveLineupAsync(lineup));
        Assert.Empty(await api.LineupsAsync());
    }

    [Fact]
    public async Task PreservesGroupsInStorageSyncAndBackupAndOmitsLegacyFields()
    {
        var api = await CreateApi();
        var input = Sample() with { Stages = [new("前期", 1, 3, [], [], []) { Groups = Sample().Groups }] };
        var saved = await api.SaveLineupAsync(input);
        var fresh = await CreateApi();
        Assert.Equal(JsonSerializer.Serialize(saved), JsonSerializer.Serialize(Assert.Single(await fresh.LineupsAsync())));
        await fresh.PutLineupsAsync([saved with { Upgrades = [new(3, 4)] }], []);
        var backup = Path.Combine(_directory, "backup.json");
        await fresh.ExportBackupAsync(backup);
        await fresh.DeleteLineupAsync(saved.Id);
        await fresh.ImportDataAsync((await fresh.ReadBackupAsync(backup)).Token, "replace");
        var imported = Assert.Single(await fresh.LineupsAsync());
        Assert.Equal(4, Assert.Single(imported.Upgrades!).Level);
        Assert.Equal(3, Assert.Single(imported.Groups!).Members.Length);
        Assert.Single(Assert.Single(imported.Stages!).Groups!);
        var old = new Lineup { Name = "旧阵容", Goals = [] };
        await new LineupStore(Path.Combine(_directory, "lineups")).SaveAsync(old);
        var legacy = (await fresh.LineupsAsync()).Single(l => l.Id == old.Id);
        var json = JsonSerializer.SerializeToElement(legacy, HostApi.JsonOptions);
        Assert.False(json.TryGetProperty("groups", out _));
        Assert.False(json.TryGetProperty("upgrades", out _));
    }

    [Fact]
    public async Task TracksOnlyUnfilledCurrentGroupsAndPreservesKeyAndEquipment()
    {
        var api = await CreateApi();
        var input = Sample();
        var equipment = api.Catalog.GetProperty("equipment")[0].GetProperty("name").GetString()!;
        input.Groups![0].Members[0] = input.Groups[0].Members[0] with { Equipment = [new(equipment, true)] };
        var saved = await api.SaveLineupAsync(input);
        await api.SaveSessionAsync(new(saved.Id, [], []));
        Assert.All(input.Groups[0].Members, m => Assert.Contains(m.Name, api.TrackedNames));
        Assert.Contains(equipment, api.TrackedNames);
        var items = new[] { new ShopItem { Name = "能天使" }, new ShopItem { Name = equipment } };
        api.UpdateShopTracking(items);
        Assert.All(items, i => Assert.True(i.IsKey));
        api.SetOperatorBanned("灰喉", true);
        Assert.DoesNotContain("灰喉", api.TrackedNames);
        var field = new FieldState(null, null, "", [], []) { Roster = [new("能天使", "manual", "", false)] };
        Assert.Empty(HostApi.TrackingGroupMembers(input.Groups, field, []));
        Assert.Equal(3, HostApi.TrackingGroupMembers(input.Groups, field with
            { Roster = [new("能天使", "manual", "", true)] }, []).Length);
        await api.SaveLineupAsync(saved with { Stages = [new("前期", 1, 3, [], [], [])] });
        await api.SaveSessionAsync(new(saved.Id, [], ["能天使", "空弦", "灰喉"]) { Stage = 0 });
        Assert.Empty(api.TrackedNames);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
