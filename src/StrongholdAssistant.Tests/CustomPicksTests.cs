using System.Text.Json;
using StrongholdAssistant.Host;

namespace StrongholdAssistant.Tests;

public sealed class CustomPicksTests
{
    private static Task<HostApi> Api() => HostApi.CreateAsync(
        Path.Combine(AppContext.BaseDirectory, "data"),
        Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

    [Fact]
    public async Task LatestPicksReplaceBothRowsAndEmptyIsDifferentFromUnread()
    {
        var api = await Api();
        Assert.Null(api.Match.CustomPicks);
        MatchInfo? published = null;
        api.Changed += (name, value) => { if (name == "match") published = (MatchInfo)value; };
        api.ApplyCustomPicks(["凯尔希·思衡托"], ["凯尔希·思衡托"]);
        Assert.Equal(new[] { "凯尔希·思衡托" }, published!.CustomPicks![5]);
        Assert.Equal(new[] { "凯尔希·思衡托" }, published.CustomPicks[6]);
        api.ApplyCustomPicks([], []);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(api.Match,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var picks = json.RootElement.GetProperty("customPicks");
        Assert.Equal(0, picks.GetProperty("5").GetArrayLength());
        Assert.Equal(0, picks.GetProperty("6").GetArrayLength());
    }

    [Fact]
    public async Task SameMatchPreservesPicksAndNewMatchOrResetClearsThem()
    {
        var api = await Api();
        api.ApplyRecognizedMatch(api.Match, [], opening: true);
        api.ApplyCustomPicks(["凯尔希·思衡托"], []);
        api.ApplyRecognizedMatch(api.Match, []);
        Assert.Equal(new[] { "凯尔希·思衡托" }, api.Match.CustomPicks![5]);
        api.ApplyRecognizedMatch(api.Match, [], opening: true);
        Assert.Null(api.Match.CustomPicks);
        api.ApplyCustomPicks([], []);
        await api.ResetMatchAsync();
        Assert.Null(api.Match.CustomPicks);
    }
}
