using ASPAssistant.Core.Data;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.GameModes.GarrisonProtocol;
using ASPAssistant.Core.Services;
using ASPAssistant.Tests.Services;
using Xunit.Abstractions;
using StrongholdAssistant.Host;

namespace StrongholdAssistant.Tests.Services;

[Collection("Native OCR")]
public class UserInformationTests(EnemyRecognitionFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public async Task RecordsUserSampleThroughManualAndSingleDetailRead()
    {
        var data = Path.Combine(AppContext.BaseDirectory, "data");
        var directory = Path.Combine(Path.GetTempPath(), "StrongholdAssistant.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var api = await HostApi.CreateAsync(data, directory);
            int captures = 0;
            await api.InitializeRecognitionAsync(data, fixture.Engine, () => {
                captures++;
                return Task.FromResult<byte[]?>(File.ReadAllBytes(
                    Path.Combine(AppContext.BaseDirectory, "TestData", "info-user-window-1648x933.png")));
            });
            var match = await api.RecordInformationAsync();
            Assert.Equal(26, match.Covenants.Length);
            Assert.Equal(10, match.Covenants.Count(c => c.Incomplete));
            api.ReadOpeningMode = () => Task.FromResult<GameModeInfo?>(null);
            await api.HandleOpeningAsync(true);
            await api.HandleDetailAsync();
            await api.HandleDetailAsync();
            Assert.Equal(2, captures);
            Assert.True(api.Match.Solo);
            Assert.False(api.Match.AwaitingDetail);
            Assert.Equal("recognized", api.Match.Source);
            Assert.Equal(match.Covenants, api.Match.Covenants);
            Assert.False(Directory.Exists(Path.Combine(directory, "diagnostics")));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ReadsTwoAdditionalRowsAndBottomBanHeading()
    {
        var data = Path.Combine(AppContext.BaseDirectory, "data");
        var store = new JsonDataStore(data);
        var (covenants, _) = await store.LoadCovenantsAsync();
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "info-user-window-1648x933.png"));
        var (png, w, h) = ScreenshotNormalizer.EnsureAnalysable(bytes, 1648, 933);
        var strategy = new GarrisonOcrStrategy();
        var read = await BanCovenantReader.ReadAsync(fixture.Engine, strategy, png, w, h,
            covenants.Where(c => c.BondType == "SEASON").Select(c => c.Name).ToArray(),
            covenants.Where(c => c.BondType != "SEASON").Select(c => c.Name).ToArray());
        var enemy = await new EnemyRecognizer(fixture.Engine, strategy, fixture.Bosses).RecognizeAsync(png, w, h);
        var detector = new InformationScreenDetector(fixture.Engine, strategy);
        output.WriteLine($"核心={read.Core.Count}/{read.CoreSlots} 附加={read.Additional.Count}/{read.AdditionalSlots} 标题={read.SawCoreHeader}/{read.SawAdditionalHeader} 缩放={GameUiScale.Information(png):F3}");
        output.WriteLine(string.Join('|', read.Additional.Select(c => $"{c.Name}:{c.IsBanned}")));
        Assert.True(await detector.IsInformationScreenAsync(png, w, h));
        Assert.True(read.SawCoreHeader && read.SawAdditionalHeader);
        Assert.Equal(10, read.Core.Count);
        Assert.Equal(16, read.Additional.Count);
        Assert.Equal(new[] { "乌萨斯", "伊比利亚", "炎", "莱塔尼亚", "谢拉格" }.Order(), read.Core.Where(c => c.IsBanned).Select(c => c.Name).Order());
        Assert.Equal(new[] { "不屈", "助力", "坚守", "突袭", "聚合" }.Order(), read.Additional.Where(c => c.IsBanned).Select(c => c.Name).Order());
        Assert.Empty(read.UnnamedCore);
        Assert.Empty(read.UnnamedAdditional);
        Assert.Equal("假想敌：管", enemy?.BossName);
        Assert.Equal(new[] { "特训敌人·复苏", "特训敌人·隐匿", "特训敌人·元素" }, enemy?.EnemyTypes);
        var avatars = await new BanAvatarRecognizer(fixture.Engine, await store.LoadOperatorsAsync(), Path.Combine(data, "operator_avatars")).RecognizeAsync(bytes);
        Assert.Empty(avatars.Slots); // 禁用标题位于底部，头像尚未进入可视区。
        Assert.False(avatars.IsScrolledList);
    }
}
