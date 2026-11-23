// 2026-11-15：从 CarlosDJY d3dc2f9 移植计算核心与测试，适配本项目输入和数据，保留原声明。
using System.Text.Json;
using ASPAssistant.Core.Placement;

namespace ASPAssistant.Core.Placement;

// 从参照加载器提取部署格、地形和基础路线；不加载识别资料。
public static class GarrisonBoardStore
{
    public static async Task<GarrisonBoardData?> LoadAsync(string dataDirectory)
    {
        var mapsPath = Path.Combine(dataDirectory, "garrison_maps.json");
        if (!File.Exists(mapsPath)) return null;
        using var maps = JsonDocument.Parse(await File.ReadAllTextAsync(mapsPath));
        return Parse(maps.RootElement);
    }

    public static GarrisonBoardData Parse(JsonElement maps)
    {
        int firstColumn = maps.GetProperty("columns")[0].GetInt32();

        var list = new List<GarrisonMap>();
        foreach (var m in maps.GetProperty("maps").EnumerateArray())
        {
            var rows = m.GetProperty("cells").EnumerateArray().Select(r => r.GetString() ?? "").ToList();
            int width = rows.Max(r => r.Length);
            var cells = new BoardCellType[rows.Count, width];
            for (int r = 0; r < rows.Count; r++)
                for (int c = 0; c < width; c++)
                    cells[r, c] = c < rows[r].Length ? CellType(rows[r][c]) : BoardCellType.Blocked;
            var routes = new List<IReadOnlyList<BoardCell>>();
            if (m.TryGetProperty("routes", out var rs))
                foreach (var route in rs.EnumerateArray())
                    routes.Add(route.GetProperty("cells").EnumerateArray()
                        .Select(c => new BoardCell(c[0].GetInt32(), c[1].GetInt32())).ToList());
            var terrain = new Dictionary<BoardCell, TerrainKind>();
            if (m.TryGetProperty("terrain", out var ts))
                foreach (var kind in ts.EnumerateObject())
                {
                    var k = kind.Name switch
                    {
                        "infection" => TerrainKind.Infection, "mire" => TerrainKind.Mire,
                        "hide" => TerrainKind.Hide, "speedup" => TerrainKind.Speedup, _ => TerrainKind.None,
                    };
                    if (k == TerrainKind.None) continue;
                    foreach (var c in kind.Value.EnumerateArray()) terrain[new BoardCell(c[0].GetInt32(), c[1].GetInt32())] = k;
                }
            list.Add(new GarrisonMap(m.GetProperty("id").GetString() ?? "", m.GetProperty("name").GetString() ?? "",
                firstColumn, cells, routes) { Terrain = terrain, CellRows = rows.ToArray() });
        }

        return new GarrisonBoardData { Maps = list };
    }

    private static BoardCellType CellType(char c) => c switch
    {
        'G' => BoardCellType.Ground,
        'T' => BoardCellType.Special,
        'H' => BoardCellType.High,
        'D' => BoardCellType.Defense,
        'S' => BoardCellType.Spawn,
        _ => BoardCellType.Blocked,
    };
}
