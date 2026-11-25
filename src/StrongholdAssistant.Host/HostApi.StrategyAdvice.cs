using ASPAssistant.Core.Models;

namespace StrongholdAssistant.Host;

public sealed record StrategyAdviceItem(string StrategyId, double ClearRate, double ReachedRound);
public sealed record StrategyAdviceAverage(double ClearRate, double ReachedRound);
public sealed record StrategyAdvice(string Status, string? Reason, string Basis, StrategyAdviceItem[] Items,
    StrategyAdviceAverage Average, string[] Assumptions);

public sealed partial class HostApi
{
    private List<Strategy> _openingStrategies = [];
    private readonly object _adviceGate = new();
    private sealed record OpeningInput(string? Mode, bool Solo, string? Boss, string? Map, CovenantState[] Covenants, string[] Bans);
    private OpeningInput? _adviceInput;

    private void ResetStrategyAdvice()
    {
        lock (_adviceGate)
        {
            _adviceInput = null;
            _match = _match with { StrategyAdvice = null };
        }
    }

    private void ScheduleStrategyAdvice(bool mapOnly = false)
    {
        var map = Field?.MapId ?? _boardMap;
        lock (_adviceGate)
        {
            var last = _adviceInput;
            var match = _match;
            // 若本局已有信息但上次尚未产出有效推荐，即使 mapOnly 触发也允许补算。
            if (mapOnly && last is null && match.Source == "none") return;
            var covenants = match.Source == "none" ? [] : match.Covenants;
            if (match.Source == "none" && match.Mode is null && match.BossId is null && map is null) return;
            if (last is not null && last.Mode == match.Mode?.Id && last.Solo == match.Solo && last.Map == map && last.Boss == match.BossId
                && last.Covenants.SequenceEqual(covenants) && last.Bans.SequenceEqual(match.BannedOperators)
                && match.StrategyAdvice?.Status == "ok") return;

            var input = new OpeningInput(match.Mode?.Id, match.Solo, match.BossId, map, covenants, match.BannedOperators);
            _adviceInput = input;
            _ = ComputeStrategyAdviceAsync(input);
        }
    }

    private async Task ComputeStrategyAdviceAsync(OpeningInput input)
    {
        // 连续补录只计算最后一个快照。模型可能在计算时换局，发布前再次核对。
        await Task.Delay(50).ConfigureAwait(false);
        lock (_adviceGate)
            if (!ReferenceEquals(input, _adviceInput)) return;
        var state = new MatchState { GameDataVersion = TextFact(_gameDataVersion), ModeId = TextFact(input.Mode),
            MapId = TextFact(input.Map), BossId = TextFact(input.Boss),
            Covenants = ListFact(input.Covenants.Select(c => new MatchCovenant
                { Id = TextFact(c.Id), Incomplete = BoolFact(c.Incomplete) }).ToArray()),
            BannedOperators = ListFact(input.Bans.Select(name => new MatchEntity { Name = TextFact(name) }).ToArray()) };
        var result = BuildStrategyAdvice(state, input.Solo);

        void Publish()
        {
            MatchInfo match;
            lock (_adviceGate)
            {
                if (!ReferenceEquals(input, _adviceInput)) return;
                match = _match = _match with { StrategyAdvice = result };
            }
            Changed?.Invoke("match", match);
        }
#if LINUX_PORT
        Publish();
        await Task.CompletedTask;
#else
        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher) await dispatcher.InvokeAsync(Publish);
        else Publish();
#endif
    }

    internal StrategyAdvice BuildStrategyAdvice(MatchState state, bool soloFallback = false)
    {
        var mode = state.ModeId.Value;
        bool fallback = mode is null || StrategyBasis(mode) is null;
        var effectiveMode = fallback ? (soloFallback ? "mode_single_16" : "mode_multi_normal") : mode!;
        var basis = fallback
            ? (soloFallback ? "按默认规程等级16估" : "按默认险境模拟估")
            : (StrategyBasis(effectiveMode) ?? "");
        var candidates = _openingStrategies.Where(s => !s.InactiveModes.Contains(effectiveMode)).Select(s => s.Id).ToArray();
        var recommendation = RecommendOpeningStrategies(state, candidates, soloFallback);
        if (recommendation.Candidates.Length == 0)
            return new("unavailable", recommendation.Assumptions[0], basis, [], new(0, 0), []);
        var items = recommendation.Candidates.Select(s =>
            new StrategyAdviceItem(s.StrategyId, s.ClearProbability, s.ExpectedRound)).ToArray();
        return new("ok", null, basis, items, new(items.Average(s => s.ClearRate), items.Average(s => s.ReachedRound)),
            recommendation.Assumptions);
    }
}
