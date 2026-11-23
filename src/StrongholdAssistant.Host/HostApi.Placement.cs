using System.Text.Json;
using ASPAssistant.Core.Placement;

namespace StrongholdAssistant.Host;

public sealed partial class HostApi
{
    private PlacementAdapter _placementAdapter = null!;
    private readonly object _placementGate = new();
    private CancellationTokenSource? _placementStop;
    private string? _placementSignature;
    private int _placementGeneration;
    private PlacementResult? _placement;
    private Task _placementWork = Task.CompletedTask;
    private bool _publishingManualField;
    public PlacementResult? Placement { get { lock (_placementGate) return _placement; } }
    public event Action<OverlayPlacementPlan?>? PlacementOverlayChanged;

    public static OverlayPlacementPlan? ToOverlayPlacement(PlacementResult? result) => result is null ? null
        : new(result.BasedOn, result.Moves.Select(move =>
            new OverlayPlacementMove(move.Operator, move.From, move.To, move.Facing)).ToArray());

    private void NotifyPlacement(PlacementResult? result)
    {
        Changed?.Invoke("placement", result!);
        PlacementOverlayChanged?.Invoke(ToOverlayPlacement(result));
    }

    private async Task InitializePlacementAsync(string directory)
    {
        _placementAdapter = await PlacementAdapter.LoadAsync(directory);
        Changed += (kind, _) =>
        {
            if (!_publishingManualField && kind is "field" or "match" or "roster") SchedulePlacement();
        };
    }

    private void ResetPlacement(bool force = true)
    {
        lock (_placementGate)
        {
            if (!force && _placementSignature == "") return;
            _placementStop?.Cancel();
            _placementGeneration++;
            _placementSignature = "";
            _placement = null;
        }
        NotifyPlacement(null);
    }

    private void SchedulePlacement() => SchedulePlacementCore(false);

    private void SchedulePlacementCore(bool force)
    {
        if (!Settings.FieldAuto && !force) return;
        var field = Field;
        var round = force ? field?.Round : Match.Economy?.Round;
        if (field is null || field.MapId is null || round is not > 0 || !field.Units.Any(u => u.Zone == "board"))
        {
            ResetPlacement(force: force);
            return;
        }
        // 按协议读可选字段；168/171 的宿主实现合入后自动带入，不在这里补识别。
        var snapshot = JsonSerializer.SerializeToElement(field, JsonOptions).Deserialize<PlacementField>(JsonOptions)!;
        var match = Match;
        var matchJson = JsonSerializer.SerializeToElement(match, JsonOptions);
        var bar = matchJson.TryGetProperty("covenantBar", out var value) && value.ValueKind == JsonValueKind.Array
            ? value.Deserialize<PlacementLayers[]>(JsonOptions) : null;
        var input = new PlacementInput(snapshot, round!.Value, match.Mode?.Id, match.Solo,
            match.BossId, match.EnemyTypes, match.RoundEnemies?.Round == round
                ? match.RoundEnemies.Enemies.Select(e => e.Name).ToArray() : null,
            bar, match.StrategyId, ReadRoster(), Settings.OperatorDefaults.ToDictionary(p => p.Key, p => p.Value.Skill), Settings.PlacementGoal)
        {
            Modules = Settings.OperatorDefaults.ToDictionary(p => p.Key, p => p.Value.Module),
        };
        // sure 与 auto 对计算等价；来源、候选分数和事件时间不属于站位输入。
        var signatureField = snapshot with { UpdatedAt = "", Units = snapshot.Units
            .OrderBy(u => u.Zone).ThenBy(u => u.Row).ThenBy(u => u.Col)
            .Select(u => u with { Status = u.Status is "sure" or "auto" ? "sure" : u.Status,
                Equipment = u.Equipment?.OrderBy(e => e.Name).ThenBy(e => e.Elite).ToArray() }).ToArray() };
        var signature = JsonSerializer.Serialize(input with { Field = signatureField }, JsonOptions);
        lock (_placementGate)
        {
            if (!force && signature == _placementSignature)
            {
                // 只有日志或身份置信状态变化时复用结果，同时同步接口的快照时间。
                if (_placementStop is null && _placement is { } previous && previous.BasedOn != snapshot.UpdatedAt)
                {
                    _placement = previous with { BasedOn = snapshot.UpdatedAt };
                    var reused = _placement;
                    int reusedGeneration = _placementGeneration;
                    _ = Task.Run(() => PublishPlacement(reused, reusedGeneration));
                }
                return;
            }
            _placementSignature = signature;
            int generation = ++_placementGeneration;
            _placementStop?.Cancel();
            var stop = new CancellationTokenSource();
            _placementStop = stop;
            var timing = force ? ASPAssistant.Core.Services.FieldRecognitionTiming.Active : null;
            _placementWork = Task.Run(async () =>
            {
                try
                {
                    if (!force) await Task.Delay(1000, stop.Token);
                    var result = _placementAdapter.Compute(input, stop.Token);
                    PublishPlacement(result, generation, timing is null ? null : () =>
                    {
                        timing.Mark("placementReady");
                        Collect(DateTimeOffset.UtcNow, b => b.Log("recognize", new
                        {
                            requestId = timing.RequestId, phase = "placement", stages = timing.Snapshot(),
                            milliseconds = timing.Snapshot()["placementReady"], basedOn = snapshot.UpdatedAt
                        }));
                    });
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                finally
                {
                    lock (_placementGate)
                    {
                        if (_placementStop == stop) _placementStop = null;
                        stop.Dispose();
                    }
                }
            });
        }
    }

    private void PublishPlacement(PlacementResult? result, int generation, Action? published = null)
    {
        // 消息桥会同步切到窗口线程；不能持有计算锁等待它，否则 placement() 可能互锁。
        if (DialogOwner?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => PublishPlacement(result, generation, published));
            return;
        }
        lock (_placementGate)
        {
            if (generation != _placementGeneration) return;
            _placement = result is not null && Field is { } latest && latest.UpdatedAt != result.BasedOn
                ? result with { BasedOn = latest.UpdatedAt } : result;
            result = _placement;
        }
        NotifyPlacement(result);
        published?.Invoke();
    }
}
