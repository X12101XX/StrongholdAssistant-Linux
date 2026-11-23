using System.Text.Json;
using ASPAssistant.Core.Models;

namespace StrongholdAssistant.Host;

public sealed partial class HostApi
{
    private string _boardViewJson = "null";

    public BoardView? Board()
    {
        var board = _boardRouteStore.View(Field?.MapId, Match.Economy?.Round,
            Match.Solo, Match.BossId, Match.Mode?.Id,
            Settings.Routes.Side == "auto" ? _boardPlayerSide : Settings.Routes.Side);
        return board is null ? null : board with { Cells = _placementAdapter.GetCells(board.MapId) };
    }

    private void PublishBoardView()
    {
        var board = Board();
        string json = JsonSerializer.Serialize(board, JsonOptions);
        if (json == _boardViewJson) return;
        _boardViewJson = json;
        Changed?.Invoke("board", board!);
    }
}
