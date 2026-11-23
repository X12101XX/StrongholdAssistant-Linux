using ASPAssistant.Core.Services;
using OpenCvSharp;

namespace StrongholdAssistant.Tests;

// 产品模板缺失时明确跳过；冻结算法回归另用随测试保存的旧版模板子集。
public sealed class SpriteTheoryAttribute : TheoryAttribute
{
    public SpriteTheoryAttribute()
    {
        if (!File.Exists(SpriteMatcherTests.PackagePath)) Skip = "未生成本地小人模板包。";
    }
}

public sealed class SpriteMatcherTests
{
    internal static string PackagePath => Path.Combine(AppContext.BaseDirectory, "data", "sprite-templates.bin.gz");

    [Theory]
    [InlineData("N24", 70, 1, "杰克|灰喉|褐果", "灰喉|褐果|杰克",
        new[] { .45242109202286773, .42724444711325216, .33755525219089844 }, false)]
    [InlineData("N29", 66, 4, "夜莺|霍尔海雅", "霍尔海雅|夜莺",
        new[] { .45915700264421166, .35375150845331305 }, true)]
    [InlineData("N30", 60, 2, "云迹|承曦格雷伊|星极|真理", "云迹|星极|真理|承曦格雷伊",
        new[] { .39160145307872807, .3674857146307534, .3660956417228661, .2937121979892254 }, false)]
    public void CroppedCellMatchesFrozenPython(string id, int anchorX, int tier, string names,
        string expectedNames, double[] expectedScores, bool autoFill)
    {
        using var matcher = new SpriteMatcher(Path.Combine(AppContext.BaseDirectory, "TestData", "sprite-templates-v1.bin.gz"));
        using var crop = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "TestData", $"sprite-{id}.png"));
        var result = matcher.MatchCrop(crop, true, tier, names.Split('|'), new Point(anchorX, 176));
        Assert.Equal(expectedNames.Split('|'), result.Candidates.Select(c => c.Name));
        for (int i = 0; i < expectedScores.Length; i++)
            Assert.InRange(Math.Abs(expectedScores[i] - result.Candidates[i].Score), 0, .01);
        Assert.Equal(result.Candidates[0].Score - result.Candidates[1].Score, result.Gap);
        Assert.Equal(autoFill, result.CanAutoFill);

        if (id == "N24")
        {
            // 冻结规则明确要求单候选不自动填；候选和阶数必须共同约束结果。
            var single = matcher.MatchCrop(crop, true, tier, ["灰喉", "夜莺"], new Point(anchorX, 176));
            Assert.Equal("灰喉", Assert.Single(single.Candidates).Name);
            Assert.Null(single.Gap);
            Assert.False(single.CanAutoFill);
        }
    }

    [SpriteTheory]
    [InlineData(100)]
    [InlineData(140)]
    public void ProductTemplatesScoreEveryCandidateInExpandedRegion(int cellWidth)
    {
        using var matcher = new SpriteMatcher(PackagePath);
        using var frame = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(40, 80, 120));
        var patch = FieldReader.CropSprite(frame, 900, 700, true)! with
        { Geometry = new(cellWidth, 900, 700, 900, 700) };
        var expanded = patch with { TemplateRegion = FieldReader.CropTemplateSprite(frame, patch) };
        using var original = Cv2.ImDecode(patch.Image, ImreadModes.Color);
        using var region = Cv2.ImDecode(expanded.TemplateRegion.Image, ImreadModes.Color);
        Assert.Equal(new Size(180, 204), original.Size());
        Assert.True(region.Width > 180 && region.Height > 204);
        // 覆盖全包各阶候选，包含此前超出裁块的铸铁、暴躁铁皮和克莱门莎。
        using var operators = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "operators.json")));
        foreach (var group in operators.RootElement.GetProperty("operators").EnumerateArray().GroupBy(o => o.GetProperty("tier").GetInt32()))
        {
            var names = group.Select(o => o.GetProperty("name").GetString()!).ToArray();
            var result = matcher.MatchCellWidth(expanded, group.Key, names);
            Assert.Equal(names.Order(), result.Candidates.Select(c => c.Name).Order());
            Assert.All(result.Candidates, c => Assert.True(double.IsFinite(c.Score)));
        }
    }
}
