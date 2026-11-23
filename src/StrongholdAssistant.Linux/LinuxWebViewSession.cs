using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ASPAssistant.Core;

namespace StrongholdAssistant.Host;

internal sealed record WebViewHttpEvent(string Method, string Url, int Status, string? Body);
internal sealed record WebViewLoadEvent(string Event, string Url, string? Error);
internal sealed record WebViewMessageEvent(string Url, string DataJson);

/// <summary>
/// Controls a stronghold-webview --session-mode child process over stdin/stdout JSON lines,
/// providing WebKit2GTK navigation, JS evaluation, cookie clearing, postMessage, and fetch/XHR interception.
/// </summary>
internal sealed class LinuxWebViewSession : IDisposable
{
    private readonly object _writeLock = new();
    private readonly Process _proc;
    private readonly StreamWriter _stdin;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _evalPending = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _clearPending = new();
    private int _disposed;

    public event Action<WebViewLoadEvent>? LoadChanged;
    public event Action<WebViewMessageEvent>? WebMessageReceived;
    public event Action<WebViewHttpEvent>? HttpResponseReceived;
    public event Action? Closed;

    public string CurrentUrl { get; private set; } = "";
    public bool IsClosed => Volatile.Read(ref _disposed) != 0 || _proc.HasExited;

    public LinuxWebViewSession(
        string title,
        string dataDir,
        int width,
        int height,
        bool visible,
        string? initialUrl = null,
        string? allowedHost = null,
        string? hint = null,
        bool externalNewWindow = false,
        string appId = "stronghold-assistant-auth")
    {
        var bin = LinuxOverlayController.FindHelperBinary("stronghold-webview")
            ?? throw new InvalidOperationException("找不到 stronghold-webview 组件，请先运行 scripts/build-linux.sh。");

        var psi = new ProcessStartInfo(bin)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = false
        };
        psi.ArgumentList.Add("--session-mode");
        psi.ArgumentList.Add("--title");
        psi.ArgumentList.Add(title);
        psi.ArgumentList.Add("--app-id");
        psi.ArgumentList.Add(appId);
        psi.ArgumentList.Add("--data-dir");
        psi.ArgumentList.Add(dataDir);
        psi.ArgumentList.Add("--width");
        psi.ArgumentList.Add(width.ToString());
        psi.ArgumentList.Add("--height");
        psi.ArgumentList.Add(height.ToString());
        if (!visible) psi.ArgumentList.Add("--hidden");
        if (!string.IsNullOrEmpty(initialUrl))
        {
            psi.ArgumentList.Add("--url");
            psi.ArgumentList.Add(initialUrl);
            CurrentUrl = initialUrl;
        }
        if (!string.IsNullOrEmpty(allowedHost))
        {
            psi.ArgumentList.Add("--allowed-host");
            psi.ArgumentList.Add(allowedHost);
        }
        if (!string.IsNullOrEmpty(hint))
        {
            psi.ArgumentList.Add("--hint");
            psi.ArgumentList.Add(hint);
        }
        if (externalNewWindow) psi.ArgumentList.Add("--external-new-window");

        _proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 WebKitGTK 窗口进程。");
        _stdin = new StreamWriter(_proc.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true };
        _ = ReadStdoutLoopAsync();
        _ = DrainStderrAsync();
    }

    public void Navigate(string url)
    {
        CurrentUrl = url;
        SendLine("NAV " + url);
    }

    public void Reload() => SendLine("RELOAD");
    public void Show() => SendLine("SHOW");
    public void Hide() => SendLine("HIDE");

    public async Task<string> EvaluateAsync(string script, CancellationToken ct = default)
    {
        if (IsClosed) throw new OperationCanceledException("WebView session closed.");
        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _evalPending[id] = tcs;
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
        SendLine($"EVAL {id} {b64}");
        try
        {
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        }
        finally
        {
            _evalPending.TryRemove(id, out _);
        }
    }

    public async Task ClearDataAsync(CancellationToken ct = default)
    {
        if (IsClosed) return;
        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _clearPending[id] = tcs;
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        SendLine($"CLEAR_DATA {id}");
        try
        {
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        }
        catch { }
        finally
        {
            _clearPending.TryRemove(id, out _);
        }
    }

    private void SendLine(string line)
    {
        lock (_writeLock)
        {
            if (IsClosed) return;
            try { _stdin.WriteLine(line); } catch { }
        }
    }

    private async Task ReadStdoutLoopAsync()
    {
        try
        {
            while (true)
            {
                var line = await _proc.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                DispatchJsonLine(line);
            }
        }
        catch { }
        finally
        {
            FailAllPending();
            Closed?.Invoke();
        }
    }

    private async Task DrainStderrAsync()
    {
        try
        {
            while (await _proc.StandardError.ReadLineAsync().ConfigureAwait(false) is { } err)
            {
                if (!string.IsNullOrWhiteSpace(err))
                    AppLogger.Info("WebViewSession", err);
            }
        }
        catch { }
    }

    private void DispatchJsonLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp)) return;
            var type = typeProp.GetString();
            switch (type)
            {
                case "load":
                    var ev = root.GetProperty("event").GetString() ?? "";
                    var url = root.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                    var err = root.TryGetProperty("error", out var e) ? e.GetString() : null;
                    if (!string.IsNullOrEmpty(url)) CurrentUrl = url;
                    LoadChanged?.Invoke(new WebViewLoadEvent(ev, url, err));
                    break;

                case "webmsg":
                    var mUrl = root.TryGetProperty("url", out var mu) ? mu.GetString() ?? "" : CurrentUrl;
                    var dataJson = root.TryGetProperty("data", out var md) ? md.GetRawText() : "null";
                    WebMessageReceived?.Invoke(new WebViewMessageEvent(mUrl, dataJson));
                    break;

                case "http":
                    var method = root.TryGetProperty("method", out var hm) ? hm.GetString() ?? "GET" : "GET";
                    var hUrl = root.TryGetProperty("url", out var hu) ? hu.GetString() ?? "" : "";
                    var status = root.TryGetProperty("status", out var hs) ? hs.GetInt32() : 0;
                    string? body = root.TryGetProperty("body", out var hb) && hb.ValueKind == JsonValueKind.String ? hb.GetString() : null;
                    HttpResponseReceived?.Invoke(new WebViewHttpEvent(method, hUrl, status, body));
                    break;

                case "eval":
                    var id = root.GetProperty("id").GetString() ?? "";
                    if (_evalPending.TryRemove(id, out var tcs))
                    {
                        bool ok = root.TryGetProperty("ok", out var okP) && okP.GetBoolean();
                        if (ok)
                        {
                            var resJson = root.TryGetProperty("result", out var rp) ? rp.GetRawText() : "null";
                            tcs.TrySetResult(resJson);
                        }
                        else
                        {
                            var errMsg = root.TryGetProperty("error", out var ep) ? ep.GetString() ?? "eval error" : "eval error";
                            tcs.TrySetException(new InvalidOperationException(errMsg));
                        }
                    }
                    break;

                case "cleared":
                    var cid = root.GetProperty("id").GetString() ?? "";
                    if (_clearPending.TryRemove(cid, out var ctcs))
                        ctcs.TrySetResult(true);
                    break;

                case "closed":
                    FailAllPending();
                    Closed?.Invoke();
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("WebViewSession", $"Failed to parse bridge line: {ex.Message}");
        }
    }

    private void FailAllPending()
    {
        foreach (var (_, tcs) in _evalPending) tcs.TrySetCanceled();
        _evalPending.Clear();
        foreach (var (_, tcs) in _clearPending) tcs.TrySetResult(false);
        _clearPending.Clear();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        FailAllPending();
        try
        {
            lock (_writeLock)
            {
                _stdin.WriteLine("QUIT");
                _stdin.Dispose();
            }
        }
        catch { }
        if (!_proc.HasExited)
        {
            try { if (!_proc.WaitForExit(600)) _proc.Kill(); } catch { }
        }
        _proc.Dispose();
    }
}
