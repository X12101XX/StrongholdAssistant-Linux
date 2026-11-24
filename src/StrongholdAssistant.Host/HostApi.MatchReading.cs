using ASPAssistant.Core;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;

namespace StrongholdAssistant.Host;

public sealed partial class HostApi
{
    private MatchStrategyReader _strategyReader = null!;
    private CustomPickReader _customPickReader = null!;
    private RoundEnemyReader _roundEnemyReader = null!;
    private CovenantBarReader _covenantBarReader = null!;
    private DateTimeOffset _covenantReadAt = DateTimeOffset.MinValue;
    private int? _covenantReadRound;
    private (string? Match, int? Round) _enemyReadKey;
    private int _matchReadingGeneration;
    // 诊断包用：本局敌人面板弹出几次、其中读到名字几次
    private int _enemyPanelSeen, _enemyPanelRead;
    private bool _enemyPanelCounted;

    /// <summary>扫描看到敌人面板新弹出一次：清掉读取器的画面缓存，这一次一定重读。</summary>
    public void CountEnemyPanel()
    {
        _enemyPanelSeen++;
        _enemyPanelCounted = false;
        _roundEnemyReader.Reset();
    }

    internal object EnemyPanelStats => new { seen = _enemyPanelSeen, read = _enemyPanelRead };

    public void InitializeMatchReading(string data, IOcrEngine ocr)
    {
        _collectionHud = new(ocr);
        var tactics = System.Text.Json.JsonSerializer.Deserialize<AdaptChoice[]>(
            File.ReadAllText(Path.Combine(data, "adapt_tactics.json")), JsonOptions)!;
        _adaptReader = new(ocr, Catalog, tactics);
        _strategyReader = new(ocr, data, Catalog.GetProperty("strategies").EnumerateArray()
            .ToDictionary(s => s.GetProperty("id").GetString()!, s => s.GetProperty("name").GetString()!));
        _roundEnemyReader = new(ocr);
        _covenantBarReader = new(ocr, data, Catalog.GetProperty("covenants").EnumerateArray()
            .ToDictionary(c => c.GetProperty("id").GetString()!, c => c.GetProperty("name").GetString()!));
        _customPickReader = new(ocr, _operators.Where(o => o.Diy).Select(o => o.Name).ToArray());
    }

    public async Task ObserveMatchFrameAsync(byte[] frame, int width, int height, EconomyReading? reading, double uiScale)
    {
        // 战斗中盟约读数延至下次休整，保留上一条记录。
        if (reading is { IsRest: false, Round: not null }) return;
        string? match = _activeRecord;
        int generation = _matchReadingGeneration;
        if (reading is { IsRest: true, Round: int round })
        {
            bool read;
            lock (_historyGate)
            {
                var now = DateTimeOffset.UtcNow;
                read = _covenantReadRound != round || now - _covenantReadAt >= TimeSpan.FromSeconds(3);
                if (read) { _covenantReadAt = now; _covenantReadRound = round; }
            }
            if (read)
            {
                var result = await _covenantBarReader.ReadAsync(frame, uiScale);
                lock (_historyGate)
                {
                    if (generation == _matchReadingGeneration && match == _activeRecord && _covenantReadRound == round
                        && !result.Skipped && (result.Items.Length > 0 || Match.CovenantBar.Any(c => c.Round == round)))
                        ApplyCovenantBar(result.Items, round);
                }
            }
        }
        // 经济扫描在非棋盘页也返回空字段对象，不能用 null 判断是否读取自选页。
        if (reading is not { IsRest: true })
        {
            var picks = await _customPickReader.ReadAsync(frame, width, height);
            await ObserveOtherTurn(frame, width, height, picks, generation);
            if (generation == _matchReadingGeneration && match == _activeRecord && picks is { IsFinal: true })
            {
                ApplyCustomPicks(picks.Cards.Where(c => c.Tier == 5).Select(c => c.Name!).ToArray(),
                    picks.Cards.Where(c => c.Tier == 6).Select(c => c.Name!).ToArray());
                CollectOpening(frame, "info");
            }
        }
        if (reading is { IsRest: true } && Match.StrategySource is null)
        {
            var id = _strategyReader.ReadPortrait(frame, uiScale).Id;
            if (generation == _matchReadingGeneration && match == _activeRecord && id != null)
                ApplyRecognizedStrategy(id);
        }
    }

    public async Task ObserveEnemyFrameAsync(byte[] frame, EconomyReading reading)
    {
        // 回合数字偶尔单帧漏读（如 Closure 字体 7 误读为 Z）时，允许沿用已确认的当前休整回合，避免整次悬停被静默丢弃。
        if (!reading.IsRest || Match.Economy is not { } economy || (reading.Round is { } r && r != economy.Round)) return;
        string? match = _activeRecord;
        int generation = _matchReadingGeneration;
        var key = (match, (int?)economy.Round);
        if (_enemyReadKey != key) { _roundEnemyReader.Reset(); _enemyReadKey = key; }
        var names = await _roundEnemyReader.ReadAsync(frame);
        if (names is not null)
        {
            AppLogger.Info("RoundEnemy", $"敌人名单读到 {names.Length} 个：{string.Join("、", names)}");
            if (names.Length > 0 && !_enemyPanelCounted) { _enemyPanelRead++; _enemyPanelCounted = true; }
        }
        if (generation == _matchReadingGeneration && match == _activeRecord && Match.Economy?.Round == economy.Round && names is { Length: > 0 })
            ApplyRoundEnemies(economy.Round, names);
    }

    private void ResetMatchReading()
    {
        _matchReadingGeneration++;
        _pendingAdapt = null;
        _enemyReadKey = default;
        _roundEnemyOrder.Clear();
        _covenantReadAt = DateTimeOffset.MinValue;
        _covenantReadRound = null;
        _enemyPanelSeen = _enemyPanelRead = 0;
        Match = Match with { CovenantBar = [], AdaptAdvice = null };
    }

    internal void ApplyCovenantBar(CovenantBarReading[] items, int? round)
    {
        lock (_historyGate)
        {
            string at = DateTimeOffset.UtcNow.ToString("O");
            // 同回合换位也会移除盟约，不能把上一帧已消失的层数当成本帧实读。
            var merged = Match.CovenantBar.Where(c => c.Round != round).ToDictionary(c => c.Id);
            foreach (var item in items) merged[item.Id] = new(item.Id, item.Level, item.Layers, round, at);
            Match = Match with { CovenantBar = merged.Values.ToArray() };
            Changed?.Invoke("match", Match);
        }
    }

    internal void ApplyRecognizedStrategy(string id) => UpdateMatchStrategy(id, "recognized");

    // 每次读到可信最终状态均覆盖，空数组表示读到了但未选择。
    internal void ApplyCustomPicks(string[] tier5, string[] tier6)
    {
        lock (_historyGate)
        {
            Match = Match with { CustomPicks = new() { [5] = tier5, [6] = tier6 } };
            Changed?.Invoke("match", Match);
        }
    }

    private string? ResolveRoundEnemyName(int round, string raw, Dictionary<string, string> catalog, string[] roundPool)
    {
        var clean = raw.Replace(" ", "")
            .Replace("茲", "兹").Replace("薩", "萨").Replace("榭", "谢").Replace("劍", "剑")
            .Replace("組", "组").Replace("長", "长").Replace("鋒", "锋").Replace("師", "师");
        if (clean.Length == 0) return null;
        if (catalog.ContainsKey(clean)) return clean;

        // 优先在本回合可能怪组中纠偏（单字/双字 OCR 笔误，如「萨卡兹大利手」->「萨卡兹大剑手」）。
        if (clean.Length >= 3 && roundPool.Length > 0)
        {
            var matched = BestFuzzyEnemy(clean, roundPool, allowLoose: true);
            if (matched is not null) return matched;
        }
        // 其次在全敌人图鉴中严格纠偏（避免把测试中的虚构名误匹配到无关敌人）。
        if (clean.Length >= 4)
        {
            var matched = BestFuzzyEnemy(clean, catalog.Keys, allowLoose: false);
            if (matched is not null) return matched;
        }
        // 保留非图鉴多字读数（兼容单元测试及新敌人），丢弃明显单字噪点。
        return clean.Length <= 1 ? null : clean;
    }

    private static string? BestFuzzyEnemy(string query, IEnumerable<string> candidates, bool allowLoose)
    {
        int maxDist = allowLoose
            ? (query.Length <= 3 ? 1 : query.Length <= 6 ? 2 : 3)
            : (query.Length <= 6 ? 1 : 2);
        string? best = null;
        int bestDist = int.MaxValue;
        bool tied = false;

        foreach (var cand in candidates)
        {
            if (Math.Abs(cand.Length - query.Length) > maxDist) continue;
            int d = EditDistance(query, cand);
            if (d > maxDist) continue;
            int maxLen = Math.Max(query.Length, cand.Length);
            double similarity = 1.0 - (double)d / maxLen;
            if (similarity < (allowLoose ? 0.55 : 0.72)) continue;
            if (d < bestDist)
            {
                bestDist = d;
                best = cand;
                tied = false;
            }
            else if (d == bestDist && cand != best)
            {
                tied = true;
            }
        }
        return tied ? null : best;
    }

    private static int EditDistance(string a, string b)
    {
        int n = a.Length, m = b.Length;
        Span<int> prev = m + 1 <= 64 ? stackalloc int[m + 1] : new int[m + 1];
        Span<int> curr = m + 1 <= 64 ? stackalloc int[m + 1] : new int[m + 1];
        for (int j = 0; j <= m; j++) prev[j] = j;
        for (int i = 1; i <= n; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= m; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            curr.CopyTo(prev);
        }
        return prev[m];
    }

    internal void ApplyRoundEnemies(int round, IEnumerable<string> names)
    {
        lock (_historyGate)
        {
            var previous = RoundEnemiesFor(round);
            var enemies = previous.Enemies.ToList();
            var catalog = EnemyIdsByName();
            var match = Match;
            var roundPool = _placementAdapter.RoundStreams(round, match.Mode?.Id, match.Solo, match.EnemyTypes, match.BossId)
                .SelectMany(s => s.Spawns).Select(e => e.Profile.Name).Distinct().ToArray();
            foreach (var raw in names)
            {
                var name = ResolveRoundEnemyName(round, raw, catalog, roundPool);
                if (name is null) continue;
                if (name != raw)
                    AppLogger.Info("RoundEnemy", $"敌人名纠偏：{raw} -> {name}");
                if (previous.Removed.Any(e => e.Name == name)) continue;
                int index = enemies.FindIndex(e => e.Name == name);
                if (index >= 0) enemies[index] = enemies[index] with { Source = "read" };
                else
                {
                    int insert = enemies.FindLastIndex(e => e.Source == "read") + 1;
                    InsertRoundEnemyOrder(enemies, insert, name);
                    enemies.Insert(insert, new(catalog.GetValueOrDefault(name), name));
                }
            }
            Match = Match with { RoundEnemies = previous with
                { At = DateTimeOffset.UtcNow.ToString("O"), Enemies = enemies.ToArray() } };
            Changed?.Invoke("match", Match);
        }
    }
}
