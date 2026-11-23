using System.Reflection;
using System.Text.Json;
using ASPAssistant.Core.Data;
using StrongholdAssistant.Host;

namespace StrongholdAssistant.Tests;

public sealed class UserJsonFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Environment.GetEnvironmentVariable("SAFE_FILES_TEST_ROOT") ?? Path.GetTempPath(),
        "UserJsonFileTests", Guid.NewGuid().ToString("N"));
    private Task<HostApi> CreateApi() => HostApi.CreateAsync(Path.Combine(AppContext.BaseDirectory, "data"), _directory);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ZeroFilledUserFilesAllowStartupAndPreserveEvidence(bool backup)
    {
        var files = new Dictionary<string, string>
        {
            ["history.json"] = "[{\"id\":\"saved\",\"time\":\"2026-11-15\",\"local\":null,\"skland\":null}]",
            ["history-deleted-skland.json"] = "[\"deleted\"]",
            ["base-money.json"] = "{\"mode_multi_hard|False|1\":12}",
            ["income-table.json"] = "{\"fingerprint\":0,\"updatedAt\":\"2026-11-15\",\"rows\":[]}",
            ["share-id.json"] = "\"saved-device\"",
            ["session.json"] = "{\"targetLineupId\":null,\"owned\":[],\"tracked\":[],\"trackedEquipment\":[],\"stage\":3}",
            ["settings.json"] = "{\"routeHotkeyMigrated\":true,\"shopMarkers\":false}",
            ["roster.json"] = "{\"readAt\":\"saved\",\"operators\":[],\"customSquad\":[]}",
            ["lineups/saved.json"] = "{\"id\":\"saved\",\"name\":\"保存的阵容\"}"
        };
        foreach (var (name, json) in files)
        {
            var path = Path.Combine(_directory, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[512]);
            if (backup) File.WriteAllText(path + ".bak", json);
        }
        var api = await CreateApi();
        Assert.Equal(backup ? 1 : 0, api.History().Length);
        Assert.Equal(backup ? "saved" : null, api.History().FirstOrDefault()?.Id);
        Assert.Equal(!backup, api.Settings.ShopMarkers);
        Assert.Equal(backup ? 3 : (int?)null, (await api.SessionAsync()).Stage);
        Assert.Equal(backup ? "saved" : null, api.ReadRoster().ReadAt);
        Assert.Equal(backup ? 1 : 0, (await api.LineupsAsync()).Length);
        var shareId = (string)typeof(HostApi).GetMethod("ShareId", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(api, null)!;
        if (backup) Assert.Equal("saved-device", shareId);
        else Assert.True(Guid.TryParse(shareId, out _));
        foreach (var name in files.Keys)
        {
            var path = Path.Combine(_directory, name);
            var broken = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".broken-*"));
            Assert.Equal(new byte[512], File.ReadAllBytes(broken));
            if (backup) Assert.Equal(files[name], File.ReadAllText(path));
        }
        // 再次构造仍能读到恢复的数据，不因坏文件已隔离而丢失备份内容。
        var reopened = await CreateApi();
        Assert.Equal(backup ? 1 : 0, reopened.History().Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(512)]
    public void InvalidPrimaryAndBackupStartEmpty(int length)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "history.json");
        File.WriteAllBytes(path, new byte[length]);
        File.WriteAllBytes(path + ".bak", new byte[length]);
        Assert.Null(UserJsonFile.Read(path, text => JsonSerializer.Deserialize<string[]>(text)));
        Assert.Single(Directory.GetFiles(_directory, "history.json.broken-*"));
        Assert.Single(Directory.GetFiles(_directory, "history.json.bak.broken-*"));
    }

    [Fact]
    public void WritesKeepPreviousContentAsBackup()
    {
        var path = Path.Combine(_directory, "history.json");
        UserJsonFile.Write(path, "[1]");
        UserJsonFile.Write(path, "[2]");
        Assert.Equal("[1]", File.ReadAllText(path + ".bak"));
        Assert.Equal("[2]", File.ReadAllText(path));
        UserJsonFile.Write(path, "[3]");
        Assert.Equal("[2]", File.ReadAllText(path + ".bak"));
        Assert.False(File.Exists(path + ".tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
