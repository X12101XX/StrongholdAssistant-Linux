using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using ASPAssistant.Core.Models;
using OpenCvSharp;

namespace ASPAssistant.Core.Services;

public sealed partial class FieldReader
{
    private SummonDefinition[] _summonDefinitions = [];
    private SpriteMatcher? _summonMatcher;
    private SpriteMatcher? _summonOperatorMatcher;
    private readonly object _summonOperatorGate = new();
    private string _operatorPackage = "";
    private (string Name, int Tier)[] _operatorTiers = [];
    private readonly Dictionary<FieldCell, (double X, double Y)> _summonFeet = [];
    private readonly HashSet<FieldCell> _summonHints = [];
    // 按 (格子, 阶数, 角标标记, 排除提示) 缓存上一帧裁块指纹，画面未变的空格/老格跳过重复模板比对。
    private readonly ConcurrentDictionary<(FieldCell Cell, int Tier, bool Badge, bool Excluded), (ulong Hash, FieldSummon? Summon)> _summonCellCache = new();
    public FieldSummon[] Summons { get; private set; } = [];

    private static Mat NormalizeSpriteImage(System.Drawing.Bitmap bitmap)
    {
#if LINUX_PORT
        var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            using var bgra = Mat.FromPixelData(bitmap.Height, bitmap.Width, MatType.CV_8UC4, data.Scan0, data.Stride);
            if (bitmap.Width == 1920 && bitmap.Height == 1080)
            {
                var image = new Mat();
                Cv2.CvtColor(bgra, image, ColorConversionCodes.BGRA2BGR);
                return image;
            }
            using var bgr = new Mat();
            Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
            var resized = new Mat();
            Cv2.Resize(bgr, resized, new Size(1920, 1080));
            return resized;
        }
        finally { bitmap.UnlockBits(data); }
#else
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        using var raw = Cv2.ImDecode(stream.ToArray(), ImreadModes.Color);
        var image = new Mat();
        Cv2.Resize(raw, image, new Size(1920, 1080));
        return image;
#endif
    }

    private void ReadSummons(Mat image, Dictionary<FieldCell, int> cells)
    {
        Summons = [];
        if (_summonMatcher is null) return;
        var names = _summonDefinitions.Select(s => s.CharId).ToArray();
        var work = new List<(FieldCell Cell, int Tier, bool Badge, bool Excluded, bool Bench, FieldSpriteGeometry Geometry, Mat Crop, int Left, int Top, ulong Hash)>();
        var found = new ConcurrentBag<(FieldCell Cell, FieldSummon Summon)>();

        foreach (var (cell, tier) in cells.ToArray())
        {
            var g = _cellGeometry[cell];
            bool badge = _summonFeet.TryGetValue(cell, out var foot);
            if (!badge) foot = (g.FootX, g.FootY);
            // 无角标时先确认脚点上方确实有图像细节，纯色空白不运行身份比对。
            if (!badge)
            {
                var body = new Rect((int)foot.X - 35, (int)foot.Y - 110, 70, 90)
                    & new Rect(0, 0, image.Width, image.Height);
                if (body.Width == 0 || body.Height == 0) continue;
                using var region = new Mat(image, body);
                Cv2.MeanStdDev(region, out _, out Scalar deviation);
                if (Math.Max(deviation.Val0, Math.Max(deviation.Val1, deviation.Val2)) < 10) continue;
            }
            bool bench = cell.Zone == "bench";
            if (!CoversSprite(image, SpriteBounds(foot.X, foot.Y, bench))) continue;
            g = g with { BadgeFootX = foot.X, BadgeFootY = foot.Y };
            var crop = TemplateCrop(image, g, bench, out int left, out int top);
            bool excluded = _summonHints.Contains(cell);
            ulong hash = ComputeCropFingerprint(crop);
            var cacheKey = (cell, tier, badge, excluded);
            if (_summonCellCache.TryGetValue(cacheKey, out var cached) && cached.Hash == hash)
            {
                crop.Dispose();
                if (cached.Summon is not null) found.Add((cell, cached.Summon));
                continue;
            }
            work.Add((cell, tier, badge, excluded, bench, g, crop, left, top, hash));
        }

        if (work.Count > 0)
        {
            // 按格并行匹配，内部关闭嵌套并行避免线程池争用，并共享跨线程模板缓存。
            Parallel.ForEach(work, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, work.Count) }, item =>
            {
                try
                {
                    FieldSummon? matched = MatchSummonCell(item.Crop, item.Cell, item.Tier, item.Badge, item.Excluded, item.Bench, item.Geometry, item.Left, item.Top, names);
                    _summonCellCache[(item.Cell, item.Tier, item.Badge, item.Excluded)] = (item.Hash, matched);
                    if (matched is not null) found.Add((item.Cell, matched));
                }
                finally
                {
                    item.Crop.Dispose();
                }
            });
        }

        var result = new List<FieldSummon>(found.Count);
        foreach (var (cell, summon) in found.OrderBy(p => p.Cell.Zone).ThenBy(p => p.Cell.Row).ThenBy(p => p.Cell.Col))
        {
            cells[cell] = 0;
            result.Add(summon);
        }
        Summons = result.ToArray();
    }

    private FieldSummon? MatchSummonCell(Mat crop, FieldCell cell, int tier, bool badge, bool excluded, bool bench,
        FieldSpriteGeometry g, int left, int top, string[] names)
    {
        var quick = _summonMatcher!.MatchAtFoot(crop, bench, g, left, top, 0, names, searchRadius: 8, quick: true, parallel: false);
        if (quick.Candidates.FirstOrDefault() is not { Score: >= .5 }) return null;
        var candidates = quick.Candidates.Take(4).Select(c => c.Name).ToArray();
        var match = _summonMatcher.MatchAtFoot(crop, bench, g, left, top, 0, candidates, searchRadius: 8, parallel: false);
        var ranked = match.Candidates.GroupBy(c => _summonDefinitions.Single(s => s.CharId == c.Name).Name)
            .Select(group => (Name: group.Key, Score: group.Max(c => c.Score)))
            .OrderByDescending(c => c.Score).ToArray();
        if (ranked.Length == 0 || ranked[0].Score < (excluded ? .64 : badge ? .68 : .80)
            || ranked.Length > 1 && ranked[0].Score - ranked[1].Score < (excluded ? .06 : badge ? .12 : .16)) return null;
        if (tier > 0)
        {
            SpriteMatcher opMatcher;
            lock (_summonOperatorGate) opMatcher = _summonOperatorMatcher ??= new(_operatorPackage);
            var operators = opMatcher.MatchAtFoot(crop, bench, g, left, top, tier,
                _operatorTiers.Where(o => o.Tier == tier).Select(o => o.Name).ToArray(), parallel: false);
            if (operators.Candidates.FirstOrDefault() is { } best && ranked[0].Score < best.Score + .12) return null;
        }
        return new(cell.Zone, cell.Col, cell.Row, ranked[0].Name, null);
    }

    private static ulong ComputeCropFingerprint(Mat crop)
    {
        ulong h = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        int rows = crop.Rows, cols = crop.Cols;
        int stride = (int)crop.Step();
        byte[] rowBuf = new byte[cols * 3];
        for (int y = 2; y < rows; y += 6)
        {
            Marshal.Copy(crop.Data + y * stride, rowBuf, 0, rowBuf.Length);
            for (int x = 2; x < cols; x += 6)
            {
                int i = x * 3;
                h = (h ^ (byte)(rowBuf[i] >> 3)) * prime;
                h = (h ^ (byte)(rowBuf[i + 1] >> 3)) * prime;
                h = (h ^ (byte)(rowBuf[i + 2] >> 3)) * prime;
            }
        }
        return h;
    }
}
