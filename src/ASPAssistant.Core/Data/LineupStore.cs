using System.Text.Json;
using ASPAssistant.Core.Models;

namespace ASPAssistant.Core.Data;

/// <summary>
/// 阵容文件持久化：每个阵容一个 &lt;id&gt;.json 文件，存于
/// %AppData%\ASPAssistant\lineups\，避免单文件并发风险。
/// </summary>
public class LineupStore
{
    private readonly string _dir;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public LineupStore(string lineupsDir)
    {
        _dir = lineupsDir;
    }

    public async Task<List<Lineup>> LoadAllAsync()
    {
        await _gate.WaitAsync();
        try { return await LoadAllCoreAsync(); }
        finally { _gate.Release(); }
    }

    private Task<List<Lineup>> LoadAllCoreAsync()
    {
        if (!Directory.Exists(_dir))
            return Task.FromResult(new List<Lineup>());

        var files = Directory.EnumerateFiles(_dir, "*.json");
        var result = new List<Lineup>();
        foreach (var file in files)
        {
            try
            {
                var lineup = UserJsonFile.Read(file, json => JsonSerializer.Deserialize<Lineup>(json, JsonOptions));
                if (lineup is null) continue;
                if (string.IsNullOrEmpty(lineup.Id))
                    lineup.Id = Path.GetFileNameWithoutExtension(file);
                result.Add(lineup);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("LineupStore", $"Failed to load {file}: {ex.Message}");
            }
        }
        return Task.FromResult(result.OrderByDescending(l => l.UpdatedAt).ToList());
    }

    public async Task SaveAsync(Lineup lineup, bool preserveUpdatedAt = false)
    {
        await _gate.WaitAsync();
        try { await SaveCoreAsync(lineup, preserveUpdatedAt); }
        finally { _gate.Release(); }
    }

    private Task SaveCoreAsync(Lineup lineup, bool preserveUpdatedAt)
    {
        Directory.CreateDirectory(_dir);
        if (string.IsNullOrEmpty(lineup.Id))
            lineup.Id = Guid.NewGuid().ToString("N");
        if (!preserveUpdatedAt) lineup.UpdatedAt = DateTime.UtcNow;

        var path = Path.Combine(_dir, $"{lineup.Id}.json");
        var json = JsonSerializer.Serialize(lineup, JsonOptions);
        UserJsonFile.Write(path, json);
        return Task.CompletedTask;
    }

    public async Task DeleteAsync(string id)
    {
        await _gate.WaitAsync();
        try
        {
            var path = Path.Combine(_dir, $"{id}.json");
            if (File.Exists(path)) File.Delete(path);
        }
        finally { _gate.Release(); }
    }
}
