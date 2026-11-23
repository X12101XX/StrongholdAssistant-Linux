using System.Drawing;
using System.Text.Json;
using ASPAssistant.Core.Models;

namespace ASPAssistant.Core.Services;

/// <summary>读取详情下方实际显示的装备条目；截断或名称不明确时整条弃用。</summary>
public sealed class FieldEquipmentReader
{
    private readonly IOcrEngine _ocr;
    private readonly Dictionary<string, Equipment> _equipment;

    public FieldEquipmentReader(string directory, IOcrEngine ocr)
    {
        _ocr = ocr;
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "equipment.json")));
        _equipment = json.RootElement.GetProperty("equipment").Deserialize<Equipment[]>()!
            .ToDictionary(e => e.Name);
    }

    public async Task<FieldEquipment[]?> ReadAsync(byte[] frame, string operatorName)
    {
        using var stream = new MemoryStream(frame);
        using var bitmap = new Bitmap(stream);
        double scale = bitmap.Width / 1920d;
        if (bitmap.Height < 1080 * scale - 2) return null;
        var rows = new List<int>();
        bool White(int x, int y)
        {
            var c = bitmap.GetPixel((int)(x * scale), (int)(y * scale));
            return c.R > 165 && c.G > 165 && c.B > 165;
        }
        // 图标白边宽约90像素。位置随条目描述高度排列，不假定六个固定槽位。
        for (int y = 700; y < 1078; y++)
        {
            bool edge = Enumerable.Range(35, 5).Any(x => White(x, y));
            if (!edge) continue;
            int start = y;
            while (y < 1078 && Enumerable.Range(35, 5).Any(x => White(x, y))) y++;
            if (y - start < 55) continue;
            if (y >= 1075 || y - start > 100) return null;
            if (!Enumerable.Range(121, 6).Any(x => White(x, start + 40))) return null;
            rows.Add(start);
        }
        if (rows.Count > (operatorName.Contains("暴躁铁皮") ? 6 : 2)) return null;
        // TODO(design): 录屏中的铁皮装备列表会超出屏幕，需补充完整六栏的实际入口样本。
        // 未能证明列表完整之前，不把可见的前两件写成全部装备。
        if (operatorName.Contains("暴躁铁皮") && rows.Count > 0) return null;
        var equipment = new List<FieldEquipment>();
        for (int i = 0; i < rows.Count; i++)
        {
            int y = rows[i];
            var texts = await _ocr.RecognizeRegionAsync(frame, (int)(140 * scale), (int)((y - 4) * scale),
                (int)(450 * scale), (int)(36 * scale));
            var matches = texts.Select(t => t.Text.Replace(" ", "")).Where(_equipment.ContainsKey).Distinct().ToArray();
            if (matches.Length != 1) return null;
            int bottom = Math.Min(y + 140, i + 1 < rows.Count ? rows[i + 1] - 4 : 1075);
            var description = await _ocr.RecognizeRegionAsync(frame, (int)(140 * scale), (int)((y + 30) * scale),
                (int)(450 * scale), (int)((bottom - y - 30) * scale));
            string text = string.Concat(description.OrderBy(t => t.Box.Y).ThenBy(t => t.Box.X).Select(t => t.Text));
            var item = _equipment[matches[0]];
            equipment.Add(new(item.Name, CompareQuality(text, item.Normal.EffectDescription, item.Elite.EffectDescription)));
        }
        return equipment.ToArray();
    }

    public static bool? CompareQuality(string text, string normal, string elite)
    {
        // 仅保留文字和数字，忽略空白、标点；同分或相似度不足时不猜品质。
        static string Normalize(string value) => string.Concat(value.Where(char.IsLetterOrDigit));
        text = Normalize(text); normal = Normalize(normal); elite = Normalize(elite);
        if (text.Length == 0) return null;
        static double Similarity(string a, string b)
        {
            var row = Enumerable.Range(0, b.Length + 1).ToArray();
            for (int i = 1; i <= a.Length; i++)
            {
                int diagonal = row[0]; row[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int previous = row[j];
                    row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                    diagonal = previous;
                }
            }
            return 1d - (double)row[b.Length] / Math.Max(a.Length, b.Length);
        }
        double normalScore = Similarity(text, normal), eliteScore = Similarity(text, elite);
        if (Math.Max(normalScore, eliteScore) < .8 || normalScore == eliteScore) return null;
        return eliteScore > normalScore;
    }
}
