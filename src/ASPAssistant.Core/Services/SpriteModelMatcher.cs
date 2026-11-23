using System.Text.Json;
using OpenCvSharp;
#if LINUX_PORT
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
#else
using OpenCvSharp.Dnn;
#endif

namespace ASPAssistant.Core.Services;

/// <summary>小人模型概率与逐名字模板最高分融合；归一化已包含在 ONNX 图内。</summary>
public sealed class SpriteModelMatcher : IDisposable
{
    public const double ModelWeight = .1;
    // 两局跨局预测分别校准为 .033005 / .096211，取较高值向上取整。
    public const double AutoFillGap = .09622;
#if LINUX_PORT
    private static int _ortResolverInstalled;
    private readonly InferenceSession _session;
    private readonly string _inputName;
#else
    private readonly ThreadLocal<Net> _nets;
#endif
    private readonly double _modelWeight;
    private readonly Dictionary<string, int> _indices;

#if LINUX_PORT
    private static void EnsureOnnxRuntimeResolver()
    {
        if (Interlocked.Exchange(ref _ortResolverInstalled, 1) != 0) return;
        NativeLibrary.SetDllImportResolver(typeof(InferenceSession).Assembly, (name, assembly, searchPath) =>
        {
            if (name != "onnxruntime") return IntPtr.Zero;
            string[] candidates =
            [
                Path.Combine(AppContext.BaseDirectory, "runtimes", "linux-x64", "native", "libonnxruntime.so.1"),
                Path.Combine(AppContext.BaseDirectory, "runtimes", "linux-x64", "native", "libonnxruntime.so"),
                Path.Combine(PackageLayout.Root, "runtimes", "linux-x64", "native", "libonnxruntime.so.1"),
                "libonnxruntime.so.1",
                "libonnxruntime.so"
            ];
            foreach (var c in candidates)
            {
                if (NativeLibrary.TryLoad(c, out var handle))
                    return handle;
            }
            return IntPtr.Zero;
        });
    }
#endif

    public SpriteModelMatcher(string modelPath, string classesPath, double modelWeight = ModelWeight)
    {
        _modelWeight = modelWeight;
        _indices = JsonSerializer.Deserialize<string[]>(File.ReadAllText(classesPath))!
            .Select((name, index) => (name, index)).ToDictionary(p => p.name, p => p.index);
        // 读成字节再交给推理引擎：避免路径编码问题。
        var model = File.ReadAllBytes(modelPath);
#if LINUX_PORT
        EnsureOnnxRuntimeResolver();
        using var opts = new SessionOptions { IntraOpNumThreads = 1, InterOpNumThreads = 1 };
        _session = new InferenceSession(model, opts);
        _inputName = _session.InputNames[0];
#else
        _nets = new(() =>
        {
            var net = CvDnn.ReadNetFromOnnx(model) ?? throw new InvalidDataException("小人模型无法加载。");
            net.SetPreferableBackend(Backend.OPENCV);
            net.SetPreferableTarget(Target.CPU);
            return net;
        }, true);
#endif
    }

    public unsafe float[] Predict(Mat crop)
    {
        using var timing = FieldRecognitionTiming.Measure("model");
        if (crop.Empty() || crop.Width != 180 || crop.Height != 204 || crop.Type() != MatType.CV_8UC3)
            throw new ArgumentException("小人模型输入必须为 180×204 BGR 图块。", nameof(crop));
#if LINUX_PORT
        const int targetW = 160;
        const int targetH = 192;
        using var resized = new Mat();
        Cv2.Resize(crop, resized, new Size(targetW, targetH), 0, 0, InterpolationFlags.Linear);
        var inputData = new float[3 * targetH * targetW];
        const int planeSize = targetH * targetW;
        const float inv255 = 1f / 255f;
        byte* basePtr = (byte*)resized.DataPointer;
        long step = resized.Step();
        for (int y = 0; y < targetH; y++)
        {
            byte* row = basePtr + y * step;
            int rowOffset = y * targetW;
            for (int x = 0; x < targetW; x++)
            {
                int idx = rowOffset + x;
                // swapRB: BGR -> RGB, NCHW layout [1, 3, 192, 160]
                inputData[idx] = row[x * 3 + 2] * inv255;
                inputData[planeSize + idx] = row[x * 3 + 1] * inv255;
                inputData[planeSize * 2 + idx] = row[x * 3 + 0] * inv255;
            }
        }
        var tensor = new DenseTensor<float>(inputData, [1, 3, targetH, targetW]);
        using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_inputName, tensor)]);
        return results[0].AsEnumerable<float>().ToArray();
#else
        using var blob = CvDnn.BlobFromImage(crop, 1d / 255, new Size(160, 192), swapRB: true, crop: false);
        var net = _nets.Value!;
        net.SetInput(blob);
        using var prediction = net.Forward();
        prediction.GetArray(out float[] values);
        return values;
#endif
    }

    internal IEnumerable<SpriteCandidate> LikelyOperators(Mat crop)
    {
        var logits = Predict(crop);
        double max = logits.Max();
        double total = logits.Sum(v => Math.Exp(v - max));
        return _indices.Select(p => new SpriteCandidate(p.Key, Math.Exp(logits[p.Value] - max) / total))
            .Where(p => p.Score >= .2).OrderByDescending(p => p.Score).Take(2);
    }

    public SpriteCandidate[] Fuse(Mat crop, IReadOnlyList<SpriteCandidate> templates)
    {
        if (templates.Count == 0) return [];
        return Fuse(Predict(crop), templates);
    }

    public string[] Shortlist(float[] logits, IEnumerable<string> names, int count)
        => names.Distinct().OrderByDescending(n => logits[_indices[n]]).Take(count).ToArray();

    internal string[] TemplateCandidates(float[] logits, string[] eligible, int count = 4)
    {
        var first = Shortlist(logits, eligible, count);
        // 被浮签、箭头盖住时，模型会把这些同阶外观排出前四；补比模板仍受本局候选池限制。
        return first.Concat(eligible.Where(name => name switch
        {
            "凯瑟琳" or "罗比菈塔" => first.Contains("风絮"),
            "慕斯" => first.Contains("陨星"),
            "砾" => first.Contains("瑰盐"),
            "地灵" => first.Contains("拜松") || first.Contains("慕斯"),
            _ => false
        })).Distinct().ToArray();
    }

    internal bool Confident(float[] logits, string[] eligible, string winner)
        => eligible.Length > 1 && eligible.Sum(n => Math.Exp((double)logits[_indices[n]] - logits[_indices[winner]])) <= 1 / .99;

    internal bool StronglySupports(float[] logits, SpriteCandidate[] templates, string name)
    {
        // 以本次实际参与融合的名字为分母，与 Fuse 的概率口径一致。
        double winner = logits[_indices[name]];
        return templates.Sum(c => Math.Exp(logits[_indices[c.Name]] - winner)) <= 1 / .9;
    }

    public SpriteCandidate[] Fuse(float[] logits, IReadOnlyList<SpriteCandidate> templates)
    {
        if (templates.Count == 0) return [];
        double max = templates.Max(c => (double)logits[_indices[c.Name]]);
        var weights = templates.Select(c => Math.Exp(logits[_indices[c.Name]] - max)).ToArray();
        double sum = weights.Sum();
        return templates.Select((c, i) => new SpriteCandidate(c.Name,
            (1 - _modelWeight) * c.Score + _modelWeight * weights[i] / sum))
            .OrderByDescending(c => c.Score).ToArray();
    }

    // 采集读取本次推理值；不额外运行模型。
    public double[] Probabilities(float[] logits, IReadOnlyList<SpriteCandidate> templates)
    {
        if (templates.Count == 0) return [];
        double max = templates.Max(c => (double)logits[_indices[c.Name]]);
        var weights = templates.Select(c => Math.Exp(logits[_indices[c.Name]] - max)).ToArray();
        double sum = weights.Sum();
        return weights.Select(w => w / sum).ToArray();
    }

#if LINUX_PORT
    public void Dispose() => _session.Dispose();
#else
    public void Dispose() { foreach (var net in _nets.Values) net.Dispose(); _nets.Dispose(); }
#endif
}
