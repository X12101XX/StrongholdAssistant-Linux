using System.Text.Json;

namespace ASPAssistant.Core.Services;

public sealed record SummonDefinition(string Name, string CharId, string[] Owners, bool OccupiesCell,
    int BaseBlock, bool DynamicBlock, double Hp, double Defense, double Resistance);

public static class SummonCatalog
{
    public static SummonDefinition[] Load(string directory)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "summons.json")));
        return doc.RootElement.GetProperty("summons").Deserialize<SummonDefinition[]>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
}
