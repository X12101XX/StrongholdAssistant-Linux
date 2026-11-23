namespace StrongholdAssistant.Host;

public sealed partial class HostApi
{
    // 保留被移除的读到项在序列中的位置，恢复时仍能回到原来的相对顺序。
    private readonly List<string> _roundEnemyOrder = [];

    private Dictionary<string, string> EnemyIdsByName() => Catalog.GetProperty("enemies").EnumerateArray()
        .GroupBy(e => e.GetProperty("name").GetString()!)
        .ToDictionary(g => g.Key, g => g.First().GetProperty("id").GetString()!);

    private RoundEnemies RoundEnemiesFor(int round)
    {
        if (Match.RoundEnemies is { } previous && previous.Round == round) return previous;
        _roundEnemyOrder.Clear();
        return new(round, "", []);
    }

    private void InsertRoundEnemyOrder(List<RoundEnemy> enemies, int index, string name)
    {
        int position = index < enemies.Count ? _roundEnemyOrder.IndexOf(enemies[index].Name) : _roundEnemyOrder.Count;
        _roundEnemyOrder.Insert(position, name);
    }

    public MatchInfo SetRoundEnemy(string name, bool present)
    {
        lock (_historyGate)
        {
            if (Match.Economy is not { } economy)
                throw new InvalidOperationException("还不知道现在是第几回合，等助手读到回合数再补");
            var previous = RoundEnemiesFor(economy.Round);
            var enemies = previous.Enemies.ToList();
            var removed = previous.Removed.ToList();
            var existing = enemies.Find(e => e.Name == name);
            if (present && existing is null)
            {
                var back = removed.Find(e => e.Name == name);
                if (back is not null)
                {
                    removed.Remove(back);
                    int position = _roundEnemyOrder.IndexOf(name);
                    int insert = enemies.Count(e => _roundEnemyOrder.IndexOf(e.Name) < position);
                    enemies.Insert(insert, back);
                }
                else
                {
                    enemies.Add(new(EnemyIdsByName().GetValueOrDefault(name), name, "manual"));
                    _roundEnemyOrder.Add(name);
                }
            }
            else if (!present && existing is not null)
            {
                enemies.Remove(existing);
                if (existing.Source == "read") removed.Add(existing);
                else _roundEnemyOrder.Remove(name);
            }
            Match = Match with { RoundEnemies = previous with
                { At = DateTimeOffset.UtcNow.ToString("O"), Enemies = enemies.ToArray(), Removed = removed.ToArray() } };
            Changed?.Invoke("match", Match);
            return Match;
        }
    }

    public RoundEnemyOptions? GetRoundEnemyOptions()
    {
        var match = Match;
        if (match.Economy is not { } economy) return null;
        var streams = _placementAdapter.RoundStreams(economy.Round, match.Mode?.Id, match.Solo, match.EnemyTypes, match.BossId);
        if (streams.Count == 0) return null;
        var catalog = EnemyIdsByName();
        return new(economy.Round, streams.Select(s => new RoundEnemyGroup(s.Label, s.Weight,
            s.Spawns.GroupBy(e => e.Profile.Name).Select(g => new RoundEnemyOption(
                catalog.GetValueOrDefault(g.Key), g.Key, g.Count())).ToArray())).ToArray());
    }
}
