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
    [ThreadStatic] private static float[]? _inputScratch;
    private readonly byte[] _modelBytes;
    private readonly object _sessionGate = new();
    private InferenceSession _session;
    private string _inputName;
    private bool _supportsBatch;
    private bool _usingCuda;
    public bool UsingCuda => _usingCuda;
#else
    private readonly ThreadLocal<Net> _nets;
#endif
    private readonly double _modelWeight;
    private readonly Dictionary<string, int> _indices;

    public SpriteModelMatcher(string modelPath, string classesPath, double modelWeight = ModelWeight)
    {
        _modelWeight = modelWeight;
        _indices = JsonSerializer.Deserialize<string[]>(File.ReadAllText(classesPath))!
            .Select((name, index) => (name, index)).ToDictionary(p => p.name, p => p.index);
        // 读成字节再交给推理引擎：避免路径编码问题。
        var model = File.ReadAllBytes(modelPath);
#if LINUX_PORT
        _modelBytes = model;
        (_session, _inputName, _supportsBatch, _usingCuda) = CreateSession(model);
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

#if LINUX_PORT
    private static (InferenceSession Session, string InputName, bool SupportsBatch, bool UsingCuda) CreateSession(byte[] model)
    {
        int cpuThreads = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
        using var opts = OnnxRuntimeConfig.CreateSessionOptions("sprite", cpuThreads, out var usingCuda, out _);
        InferenceSession session;
        try
        {
            session = new InferenceSession(model, opts);
        }
        catch (Exception ex) when (usingCuda)
        {
            AppLogger.Warn("ONNX", $"[sprite] CUDA 会话创建失败，回落 CPU: {ex.GetBaseException().Message}");
            using var fallback = new SessionOptions
            {
                IntraOpNumThreads = cpuThreads,
                InterOpNumThreads = 1,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
            };
            session = new InferenceSession(model, fallback);
            usingCuda = false;
            OnnxRuntimeConfig.ReportConsumer("sprite", "CPU", $"CUDA 会话失败，已回落 CPU（{cpuThreads} 线程）");
        }
        var inputName = session.InputNames[0];
        bool supportsBatch = session.InputMetadata.TryGetValue(inputName, out var meta)
            && meta.Dimensions.Length >= 4 && meta.Dimensions[0] <= 0;
        return (session, inputName, supportsBatch, usingCuda);
    }

    public void ReloadProvider()
    {
        lock (_sessionGate)
        {
            var old = _session;
            (_session, _inputName, _supportsBatch, _usingCuda) = CreateSession(_modelBytes);
            old.Dispose();
        }
    }

    private static unsafe void FillCropNchw(Mat crop, float[] buffer, int batchOffset)
    {
        const int targetW = 160;
        const int targetH = 192;
        const int planeSize = targetH * targetW;
        const float inv255 = 1f / 255f;
        using var resized = new Mat();
        Cv2.Resize(crop, resized, new Size(targetW, targetH), 0, 0, InterpolationFlags.Linear);
        byte* basePtr = (byte*)resized.DataPointer;
        long step = resized.Step();
        int rBase = batchOffset;
        int gBase = batchOffset + planeSize;
        int bBase = batchOffset + planeSize * 2;
        for (int y = 0; y < targetH; y++)
        {
            byte* row = basePtr + y * step;
            int rowOffset = y * targetW;
            for (int x = 0; x < targetW; x++)
            {
                int idx = rowOffset + x;
                int px = x * 3;
                // swapRB: BGR -> RGB, NCHW layout [N, 3, 192, 160]
                buffer[rBase + idx] = row[px + 2] * inv255;
                buffer[gBase + idx] = row[px + 1] * inv255;
                buffer[bBase + idx] = row[px] * inv255;
            }
        }
    }
#endif

    public float[][] PredictBatch(IReadOnlyList<Mat> crops)
    {
        if (crops.Count == 0) return [];
        if (crops.Count == 1) return [Predict(crops[0])];
#if LINUX_PORT
        if (_supportsBatch)
        {
            using var timing = FieldRecognitionTiming.Measure("model");
            const int targetW = 160;
            const int targetH = 192;
            const int sampleSize = 3 * targetH * targetW;
            int n = crops.Count;
            var batchBuffer = new float[n * sampleSize];
            for (int i = 0; i < n; i++)
            {
                var crop = crops[i];
                if (crop.Empty() || crop.Width != 180 || crop.Height != 204 || crop.Type() != MatType.CV_8UC3)
                    throw new ArgumentException("小人模型输入必须为 180×204 BGR 图块。", nameof(crops));
                FillCropNchw(crop, batchBuffer, i * sampleSize);
            }
            var tensor = new DenseTensor<float>(batchBuffer, [n, 3, targetH, targetW]);
            lock (_sessionGate)
            {
                using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_inputName, tensor)]);
                var flat = results[0].AsTensor<float>().ToArray();
                int classes = flat.Length / n;
                var output = new float[n][];
                for (int i = 0; i < n; i++)
                {
                    var row = new float[classes];
                    Array.Copy(flat, i * classes, row, 0, classes);
                    output[i] = row;
                }
                return output;
            }
        }
#endif
        var single = new float[crops.Count][];
        for (int i = 0; i < crops.Count; i++)
            single[i] = Predict(crops[i]);
        return single;
    }

    public float[] Predict(Mat crop)
    {
        using var timing = FieldRecognitionTiming.Measure("model");
        if (crop.Empty() || crop.Width != 180 || crop.Height != 204 || crop.Type() != MatType.CV_8UC3)
            throw new ArgumentException("小人模型输入必须为 180×204 BGR 图块。", nameof(crop));
#if LINUX_PORT
        const int targetW = 160;
        const int targetH = 192;
        var inputData = _inputScratch ??= new float[3 * targetH * targetW];
        FillCropNchw(crop, inputData, 0);
        var tensor = new DenseTensor<float>(inputData, [1, 3, targetH, targetW]);
        lock (_sessionGate)
        {
            using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_inputName, tensor)]);
            return results[0].AsEnumerable<float>().ToArray();
        }
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

    internal string[] TemplateCandidates(float[] logits, string[] eligible, int count = 6)
    {
        var first = Shortlist(logits, eligible, count);
        // 被浮签、箭头盖住时，模型会把这些同阶外观排出前六；补比模板仍受本局候选池限制。
        return first.Concat(eligible.Where(name => name switch
        {
            "凯瑟琳" or "罗比菈塔" => first.Contains("风絮"),
            "慕斯" => first.Contains("陨星"),
            "砾" => first.Contains("瑰盐"),
            "地灵" => first.Contains("拜松") || first.Contains("慕斯"),
            _ => false
        })).Distinct().ToArray();
    }

    internal (string Name, double Probability)? TopPrediction(float[] logits, IEnumerable<string> eligible)
    {
        var list = eligible.Distinct().Where(_indices.ContainsKey).ToArray();
        if (list.Length == 0) return null;
        double max = list.Max(n => (double)logits[_indices[n]]);
        double sum = 0;
        string bestName = list[0];
        double bestExp = -1;
        foreach (var n in list)
        {
            double e = Math.Exp(logits[_indices[n]] - max);
            sum += e;
            if (e > bestExp) { bestExp = e; bestName = n; }
        }
        return sum > 0 ? (bestName, bestExp / sum) : null;
    }

    internal bool Confident(float[] logits, string[] eligible, string winner)
        => eligible.Length > 1 && eligible.Sum(n => Math.Exp((double)logits[_indices[n]] - logits[_indices[winner]])) <= 1 / .99;

    internal bool StronglySupports(float[] logits, SpriteCandidate[] templates, string name)
    {
        // 以本次实际参与融合的名字为分母，与 Fuse 的概率口径一致。
        double winner = logits[_indices[name]];
        return templates.Sum(c => Math.Exp(logits[_indices[c.Name]] - winner)) <= 1 / .9;
    }

    public SpriteCandidate[] Fuse(float[] logits, IReadOnlyList<SpriteCandidate> templates, double? weightOverride = null)
    {
        if (templates.Count == 0) return [];
        double w = weightOverride ?? _modelWeight;
        double max = templates.Max(c => (double)logits[_indices[c.Name]]);
        var weights = templates.Select(c => Math.Exp(logits[_indices[c.Name]] - max)).ToArray();
        double sum = weights.Sum();
        return templates.Select((c, i) => new SpriteCandidate(c.Name,
            (1 - w) * c.Score + w * weights[i] / sum))
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
