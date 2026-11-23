// 2026-11-15：从 CarlosDJY d3dc2f9 移植计算核心与测试，适配本项目输入和数据，保留原声明。
namespace ASPAssistant.Core.Placement;

/// <summary>What a deployment cell allows (from data/garrison_maps.json).</summary>
public enum BoardCellType
{
    /// <summary>Not deployable (obstacles, decoration, the enemy-side patterned tiles).</summary>
    Blocked,
    /// <summary>Ground: melee and ranged operators.</summary>
    Ground,
    /// <summary>Special ground: deployable like ground, with a map-specific effect.</summary>
    Special,
    /// <summary>High ground: ranged operators only.</summary>
    High,
    Defense,
    Spawn,
}

public readonly record struct BoardCell(int Col, int Row);

/// <summary>
/// A special tile (stage_table tileInfo): 活性源石 (attack and attack speed up, HP lost over time — for operators on it
/// and enemies that pass), 沼泽地段 (operators and passing enemies slowed), 隐匿模块 (units and enemies on it are
/// invisible), 优化模块 (operators attack faster, passing enemies move faster).
/// </summary>
public enum TerrainKind { None, Infection, Mire, Hide, Speedup }

/// <summary>One Garrison Protocol map: deployment cells and the fixed ground routes.</summary>
public sealed class GarrisonMap
{
    public GarrisonMap(string id, string name, int firstColumn, BoardCellType[,] cells,
        IReadOnlyList<IReadOnlyList<BoardCell>> routes)
    {
        Id = id;
        Name = name;
        FirstColumn = firstColumn;
        Cells = cells;
        Routes = routes;
    }

    public string Id { get; }
    public string Name { get; }
    /// <summary>Column of <c>Cells[row, 0]</c> (the grid starts left of column 0).</summary>
    public int FirstColumn { get; }
    /// <summary>[row, column - FirstColumn].</summary>
    public BoardCellType[,] Cells { get; }
    // 保留数据文件中的原始格子字符，供小棋盘使用。
    public string[]? CellRows { get; init; }
    /// <summary>Ordered ground routes, each from a spawn to the defense point.</summary>
    public IReadOnlyList<IReadOnlyList<BoardCell>> Routes { get; }
    /// <summary>The map's special tiles (garrison_maps.json terrain).</summary>
    public IReadOnlyDictionary<BoardCell, TerrainKind> Terrain { get; init; } = new Dictionary<BoardCell, TerrainKind>();

    public int RowCount => Cells.GetLength(0);
    public int ColumnCount => Cells.GetLength(1);

    public BoardCellType At(BoardCell cell)
    {
        int c = cell.Col - FirstColumn;
        return cell.Row < 0 || cell.Row >= RowCount || c < 0 || c >= ColumnCount
            ? BoardCellType.Blocked
            : Cells[cell.Row, c];
    }

    public IEnumerable<(BoardCell Cell, BoardCellType Type)> AllCells()
    {
        for (int r = 0; r < RowCount; r++)
            for (int c = 0; c < ColumnCount; c++)
                yield return (new BoardCell(c + FirstColumn, r), Cells[r, c]);
    }

    public static bool CanDeploy(BoardCellType type, bool melee) =>
        type is BoardCellType.Ground or BoardCellType.Special || type == BoardCellType.High && !melee;
}

// 只保留计算需要的地图，不移植识别指纹和投影。
public sealed class GarrisonBoardData
{
    public required IReadOnlyList<GarrisonMap> Maps { get; init; }
    public GarrisonMap? Find(string id) => Maps.FirstOrDefault(m => m.Id == id);
}
