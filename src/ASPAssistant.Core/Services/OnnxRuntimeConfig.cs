#if LINUX_PORT
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;

namespace ASPAssistant.Core.Services;

public sealed record OnnxRuntimeStatus(
    string RequestedMode,
    string ActiveProvider,
    bool CudaAvailable,
    string? LibraryPath,
    string Detail,
    IReadOnlyDictionary<string, string> Consumers);

/// <summary>
/// Central Linux ONNX Runtime provider manager with CUDA GPU acceleration and graceful CPU fallback.
/// Automatically discovers CUDA-enabled libonnxruntime.so (app-local cuda/ bundle or system
/// onnxruntime-opt-cuda / onnxruntime-cuda + cuDNN), preloads CUDA/cuDNN dependencies from
/// /opt/cuda/lib64 and system paths, and falls back cleanly to multi-threaded CPU execution.
/// </summary>
public static class OnnxRuntimeConfig
{
    private static readonly object Gate = new();
    private static int _resolverInstalled;
    private static string _requestedMode = ReadEnvMode() ?? "auto";
    private static int _deviceId = ReadEnvDeviceId();
    private static bool _cudaLibraryAvailable;
    private static string? _loadedLibraryPath;
    private static string _detail = "尚未初始化推理引擎";
    private static readonly ConcurrentDictionary<string, string> _consumers = new(StringComparer.Ordinal);

    public static string Mode
    {
        get { lock (Gate) return _requestedMode; }
    }

    public static int DeviceId
    {
        get { lock (Gate) return _deviceId; }
    }

    public static bool CudaLibraryAvailable
    {
        get { EnsureInitialized(); lock (Gate) return _cudaLibraryAvailable; }
    }

    public static void Configure(string? mode, int? deviceId = null)
    {
        var envMode = ReadEnvMode();
        var effective = NormalizeMode(envMode ?? mode ?? "auto");
        lock (Gate)
        {
            _requestedMode = effective;
            if (deviceId is >= 0) _deviceId = deviceId.Value;
        }
    }

    public static string NormalizeMode(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        "cuda" => "cuda",
        "cpu" => "cpu",
        _ => "auto"
    };

    public static bool ShouldTryCuda()
    {
        EnsureInitialized();
        lock (Gate)
            return _requestedMode is "auto" or "cuda" && _cudaLibraryAvailable;
    }

    public static OnnxRuntimeStatus GetStatus()
    {
        EnsureInitialized();
        lock (Gate)
        {
            var snapshot = new Dictionary<string, string>(_consumers, StringComparer.Ordinal);
            bool anyCuda = snapshot.Values.Any(v => v.StartsWith("CUDA", StringComparison.OrdinalIgnoreCase));
            string active = anyCuda
                ? "CUDA"
                : (_requestedMode == "cpu" ? "CPU" : (_cudaLibraryAvailable ? "CUDA (就绪)" : "CPU"));
            return new OnnxRuntimeStatus(
                _requestedMode,
                active,
                _cudaLibraryAvailable,
                _loadedLibraryPath,
                _detail,
                snapshot);
        }
    }

    public static void ReportConsumer(string consumer, string provider, string? detail = null)
    {
        _consumers[consumer] = provider;
        if (!string.IsNullOrWhiteSpace(detail))
        {
            lock (Gate) _detail = detail;
        }
    }

    public static void EnsureInitialized()
    {
        if (Interlocked.Exchange(ref _resolverInstalled, 1) != 0) return;

        // Pre-probe available CUDA ONNX Runtime & CUDA/cuDNN shared libraries.
        var (cudaLib, probeDetail) = ProbeCudaLibrary();
        lock (Gate)
        {
            _cudaLibraryAvailable = cudaLib is not null;
            _detail = probeDetail;
        }

        NativeLibrary.SetDllImportResolver(typeof(InferenceSession).Assembly, (name, _, _) =>
        {
            if (name != "onnxruntime") return IntPtr.Zero;
            return ResolveOnnxRuntimeLibrary();
        });
    }

    public static SessionOptions CreateSessionOptions(
        string consumer,
        int cpuThreads,
        out bool usingCuda,
        out string detail)
    {
        EnsureInitialized();
        int threads = Math.Clamp(cpuThreads, 1, Math.Max(1, Environment.ProcessorCount));
        string mode;
        int deviceId;
        bool hasCudaLib;
        string currentDetail;
        lock (Gate)
        {
            mode = _requestedMode;
            deviceId = _deviceId;
            hasCudaLib = _cudaLibraryAvailable;
            currentDetail = _detail;
        }

        if (mode is "auto" or "cuda" && hasCudaLib)
        {
            var cudaOpts = BuildBaseOptions(threads);
            try
            {
                PreloadCudaDependencies();
                using var providerOpts = new OrtCUDAProviderOptions();
                providerOpts.UpdateOptions(new Dictionary<string, string>
                {
                    ["device_id"] = deviceId.ToString(),
                    // Keep VRAM footprint conservative so Proton/DXVK Arknights never starves.
                    ["arena_extend_strategy"] = "kSameAsRequested",
                    ["cudnn_conv_algo_search"] = "HEURISTIC",
                    ["gpu_mem_limit"] = (512L * 1024 * 1024).ToString(),
                    ["do_copy_in_default_stream"] = "1"
                });
                cudaOpts.AppendExecutionProvider_CUDA(providerOpts);
                usingCuda = true;
                detail = $"CUDA GPU #{deviceId} 已启用";
                ReportConsumer(consumer, $"CUDA:{deviceId}", detail);
                AppLogger.Info("ONNX", $"[{consumer}] 使用 CUDAExecutionProvider (device={deviceId}, lib={_loadedLibraryPath ?? "default"})");
                return cudaOpts;
            }
            catch (Exception ex)
            {
                cudaOpts.Dispose();
                string reason = ex.GetBaseException().Message;
                detail = $"CUDA 初始化失败，已回落 CPU（{ShortReason(reason)}）";
                ReportConsumer(consumer, "CPU", detail);
                AppLogger.Warn("ONNX", $"[{consumer}] CUDAExecutionProvider 初始化失败，回落 CPU ({threads} 线程): {reason}");
            }
        }
        else
        {
            detail = mode == "cpu"
                ? $"已手动设为 CPU 多线程（{threads} 线程）"
                : currentDetail;
            ReportConsumer(consumer, "CPU", detail);
            AppLogger.Info("ONNX", $"[{consumer}] 使用 CPUExecutionProvider ({threads} 线程): {detail}");
        }

        usingCuda = false;
        return BuildBaseOptions(threads);
    }

    private static SessionOptions BuildBaseOptions(int cpuThreads)
    {
        var opts = new SessionOptions
        {
            IntraOpNumThreads = cpuThreads,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            EnableMemoryPattern = true,
            EnableCpuMemArena = true
        };
        return opts;
    }

    private static IntPtr ResolveOnnxRuntimeLibrary()
    {
        var (cudaLib, _) = ProbeCudaLibrary();
        var candidates = new List<string>();
        if (cudaLib is not null)
            candidates.Add(cudaLib);

        // Fallback to MaaFramework's bundled libonnxruntime.so.1 (CPU-only, guaranteed compatible).
        candidates.AddRange([
            Path.Combine(AppContext.BaseDirectory, "libonnxruntime.so.1"),
            Path.Combine(AppContext.BaseDirectory, "libonnxruntime.so"),
            Path.Combine(AppContext.BaseDirectory, "runtimes", "linux-x64", "native", "libonnxruntime.so.1"),
            Path.Combine(AppContext.BaseDirectory, "runtimes", "linux-x64", "native", "libonnxruntime.so"),
            Path.Combine(PackageLayout.Root, "libonnxruntime.so.1"),
            Path.Combine(PackageLayout.Root, "runtimes", "linux-x64", "native", "libonnxruntime.so.1"),
            "libonnxruntime.so.1",
            "libonnxruntime.so"
        ]);

        foreach (var path in candidates.Distinct(StringComparer.Ordinal))
        {
            if (path == cudaLib)
                PreloadCudaDependencies(Path.GetDirectoryName(path));
            if (NativeLibrary.TryLoad(path, out var handle))
            {
                lock (Gate)
                {
                    _loadedLibraryPath = path;
                    if (path != cudaLib && _cudaLibraryAvailable)
                    {
                        _cudaLibraryAvailable = false;
                        _detail = "CUDA ONNX Runtime 加载失败，已回落内置 CPU 版本";
                    }
                }
                AppLogger.Info("ONNX", $"已加载 ONNX Runtime 动态库: {path}");
                return handle;
            }
        }
        return IntPtr.Zero;
    }

    private static (string? LibraryPath, string Detail) ProbeCudaLibrary()
    {
        // 1. Explicit user override via STRONGHOLD_ORT_LIB
        var envLib = Environment.GetEnvironmentVariable("STRONGHOLD_ORT_LIB");
        if (!string.IsNullOrWhiteSpace(envLib) && File.Exists(envLib))
        {
            return HasCudaProvider(envLib)
                ? (envLib, $"检测到自定义 CUDA ONNX Runtime: {envLib}")
                : (null, $"STRONGHOLD_ORT_LIB 未包含 CUDA Provider: {envLib}");
        }

        // 2. App-local CUDA bundle or system CUDA ONNX Runtime (e.g. Arch onnxruntime-opt-cuda / onnxruntime-cuda)
        string[] dirs =
        [
            Path.Combine(AppContext.BaseDirectory, "cuda"),
            Path.Combine(AppContext.BaseDirectory, "runtimes", "linux-x64", "native", "cuda"),
            Path.Combine(PackageLayout.Root, "cuda"),
            "/usr/lib",
            "/usr/lib64",
            "/usr/local/lib",
            "/opt/onnxruntime/lib"
        ];

        string? foundLibWithoutCudnn = null;
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            string[] names = ["libonnxruntime.so", "libonnxruntime.so.1"];
            foreach (var name in names)
            {
                var full = Path.Combine(dir, name);
                if (!File.Exists(full)) continue;
                if (!HasCudaProvider(full)) continue;
                if (HasNvidiaGpu() && HasCudnnOrCudaRuntime(dir))
                    return (full, $"检测到 CUDA ONNX Runtime ({full})");
                foundLibWithoutCudnn ??= full;
            }
        }

        if (foundLibWithoutCudnn is not null)
        {
            if (!HasNvidiaGpu())
                return (null, "未检测到 NVIDIA GPU 设备，使用 CPU 多线程推理");
            return (null, $"已发现 {foundLibWithoutCudnn}，但缺少 cuDNN 运行库（Arch 可执行 sudo pacman -S cudnn）");
        }

        if (HasNvidiaGpu())
            return (null, "检测到 NVIDIA GPU，但未安装 CUDA 版 ONNX Runtime（Arch 可执行 sudo pacman -S onnxruntime-opt-cuda cudnn，当前使用多线程 CPU）");

        return (null, "未检测到 NVIDIA GPU，使用多线程 CPU 推理");
    }

    private static bool HasCudaProvider(string ortLibraryPath)
    {
        var dir = Path.GetDirectoryName(ortLibraryPath);
        if (string.IsNullOrEmpty(dir)) return false;
        if (File.Exists(Path.Combine(dir, "libonnxruntime_providers_cuda.so")))
            return true;
        // Some custom monolithic builds link CUDA provider directly into libonnxruntime.so.
        try
        {
            var info = new FileInfo(ortLibraryPath);
            if (info.Length > 40 * 1024 * 1024)
            {
                using var fs = File.OpenRead(ortLibraryPath);
                var buf = new byte[(int)Math.Min(info.Length, 4 * 1024 * 1024)];
                int read = fs.Read(buf, 0, buf.Length);
                if (System.Text.Encoding.ASCII.GetString(buf, 0, read).Contains("OrtSessionOptionsAppendExecutionProvider_CUDA", StringComparison.Ordinal))
                    return true;
            }
        }
        catch { }
        return false;
    }

    private static bool HasNvidiaGpu()
    {
        return File.Exists("/dev/nvidiactl")
            || File.Exists("/dev/nvidia0")
            || File.Exists("/proc/driver/nvidia/version");
    }

    private static bool HasCudnnOrCudaRuntime(string? extraDir)
    {
        string[] searchDirs =
        [
            extraDir ?? "",
            "/opt/cuda/lib64",
            "/usr/local/cuda/lib64",
            "/usr/lib",
            "/usr/lib64",
            "/usr/lib/x86_64-linux-gnu"
        ];
        bool hasCudnn = false;
        foreach (var d in searchDirs)
        {
            if (string.IsNullOrEmpty(d) || !Directory.Exists(d)) continue;
            if (File.Exists(Path.Combine(d, "libcudnn.so.9"))
                || File.Exists(Path.Combine(d, "libcudnn.so.8"))
                || File.Exists(Path.Combine(d, "libcudnn.so")))
            {
                hasCudnn = true;
                break;
            }
        }
        return hasCudnn;
    }

    private static void PreloadCudaDependencies(string? extraDir = null)
    {
        string[] searchDirs =
        [
            extraDir ?? "",
            "/opt/cuda/lib64",
            "/usr/local/cuda/lib64",
            "/usr/lib",
            "/usr/lib64",
            "/usr/lib/x86_64-linux-gnu"
        ];
        string[] libs =
        [
            "libcudart.so.13", "libcudart.so.12", "libcudart.so",
            "libcublasLt.so.13", "libcublasLt.so.12", "libcublasLt.so",
            "libcublas.so.13", "libcublas.so.12", "libcublas.so",
            "libcudnn.so.9", "libcudnn.so.8", "libcudnn.so",
            "libonnxruntime_providers_shared.so"
        ];
        foreach (var lib in libs)
        {
            foreach (var dir in searchDirs)
            {
                if (string.IsNullOrEmpty(dir)) continue;
                var full = Path.Combine(dir, lib);
                if (File.Exists(full) && NativeLibrary.TryLoad(full, out _))
                    break;
            }
        }
    }

    private static string? ReadEnvMode()
    {
        var v = Environment.GetEnvironmentVariable("STRONGHOLD_ONNX_PROVIDER");
        return string.IsNullOrWhiteSpace(v) ? null : NormalizeMode(v);
    }

    private static int ReadEnvDeviceId()
    {
        return int.TryParse(Environment.GetEnvironmentVariable("STRONGHOLD_CUDA_DEVICE"), out int id) && id >= 0 ? id : 0;
    }

    private static string ShortReason(string msg)
    {
        var first = msg.Split('\n', '\r')[0].Trim();
        return first.Length > 120 ? first[..120] + "…" : first;
    }
}
#endif
