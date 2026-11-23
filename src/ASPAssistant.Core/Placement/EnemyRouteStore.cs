using System.Text.Json;

namespace ASPAssistant.Core.Placement;

// 复用产品路线查表；不应用参照分支的三张地图上路修正。
public sealed class EnemyRouteStore
{
    public static readonly IReadOnlyDictionary<string, string> MapIds = new Dictionary<string, string>
    {
        ["m01"] = "desert_crates", ["m02"] = "desert_cactus", ["m03"] = "desert_steel",
        ["m04"] = "desert_laser", ["m05"] = "city", ["m06"] = "desert_camo",
        ["m09"] = "snow", ["m10"] = "palace", ["m11"] = "snow_ice", ["m12"] = "palace_arrows"
    };
    private readonly ASPAssistant.Core.Data.BoardRouteStore _routes;
    private readonly Dictionary<string, string> _modes;
    private static readonly ASPAssistant.Core.Models.BoardProjection Projection = new([
        ((0, 0), (0, 0)), ((1, 0), (1, 0)), ((1, 1), (1, 1)), ((0, 1), (0, 1))]);

    private EnemyRouteStore(string directory)
    {
        _routes = new(directory);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "codex_extra.json")));
        _modes = doc.RootElement.GetProperty("modes").EnumerateArray()
            .ToDictionary(m => m.GetProperty("name").GetString()!, m => m.GetProperty("id").GetString()!);
    }
    public static EnemyRouteStore Load(string directory) => new(directory);
    private string ModeId(string? mode) => mode is null ? "mode_multi_normal" : _modes.GetValueOrDefault(mode, mode);
    public bool? IsSolo(string? mode) => mode is null ? null : ModeId(mode).StartsWith("mode_single_");
    public int RoundCount(string? mode) => _routes.LastRound(IsSolo(mode) == true, ModeId(mode)) ?? 15;
    public RoundRoutes? Find(string mapId, int round, string? mode, string? boss) => Find(mapId, round, mode, boss, out _);
    public RoundRoutes? Find(string mapId, int round, string? mode, string? boss, out string? reason)
    {
        var found = _routes.Find(new(mapId, ASPAssistant.Core.Models.BoardCamera.Collapsed, Projection, 1),
            round, IsSolo(mode) == true, boss, ModeId(mode), out reason);
        return found is null ? null : new(mapId, round, "", boss,
            found.Routes.Select(r => new EnemyRoute(r.Motion, r.Boss, r.Approx, r.Count,
                r.Segments.Select(s => (IReadOnlyList<RoutePoint>)s.Select(p => new RoutePoint(p.Col, p.Row)).ToArray()).ToArray(),
                r.Side, r.Patrol)).ToArray(), found.BossSpots.Select(p => new RoutePoint(p.Col, p.Row)).ToArray());
    }
}
