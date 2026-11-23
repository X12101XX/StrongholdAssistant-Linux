namespace ASPAssistant.Core.Models;

public sealed record BoardView(string MapId, int? Round, bool BossRound, bool Solo,
    bool[][] Walkable, bool[][] BossWalkable, IReadOnlyList<EnemyRoute> Routes,
    IReadOnlyList<RoutePoint> BossSpots, string? Side)
{
    public string[]? Cells { get; init; }
}
