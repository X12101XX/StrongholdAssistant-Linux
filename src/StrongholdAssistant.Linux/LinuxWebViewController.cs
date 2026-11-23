using System.Diagnostics;
using System.Text;
using ASPAssistant.Core;

namespace StrongholdAssistant.Host;

/// <summary>
/// Manages the native GTK3 + WebKit2GTK-4.1 UI window helper (stronghold-webview).
/// </summary>
internal sealed class LinuxWebViewController : IDisposable
{
    private readonly object _gate = new();
    private Process? _proc;
    private StreamWriter? _stdin;
    private bool _visible = true;
    public event Action? Closed;

    public bool IsRunning => _proc is { HasExited: false };

    public bool Start(string url, string dataDir, int width = 410, int height = 920, bool devTools = false)
    {
        lock (_gate)
        {
            var bin = LinuxOverlayController.FindHelperBinary("stronghold-webview");
            if (bin is null)
            {
                AppLogger.Warn("WebView", "stronghold-webview binary not found.");
                return false;
            }
            var psi = new ProcessStartInfo(bin)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                CreateNoWindow = false
            };
            psi.ArgumentList.Add("--url");
            psi.ArgumentList.Add(url);
            psi.ArgumentList.Add("--title");
            psi.ArgumentList.Add("高效雅雅·卫戍协议助手");
            psi.ArgumentList.Add("--app-id");
            psi.ArgumentList.Add("stronghold-assistant");
            psi.ArgumentList.Add("--data-dir");
            psi.ArgumentList.Add(Path.Combine(dataDir, "WebKitGTK"));
            psi.ArgumentList.Add("--width");
            psi.ArgumentList.Add(width.ToString());
            psi.ArgumentList.Add("--height");
            psi.ArgumentList.Add(height.ToString());
            psi.ArgumentList.Add("--decorated");
            if (devTools) psi.ArgumentList.Add("--devtools");

            _proc = Process.Start(psi);
            if (_proc is null) return false;
            _proc.EnableRaisingEvents = true;
            _proc.Exited += (_, _) => Closed?.Invoke();
            _stdin = new StreamWriter(_proc.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true };
            AppLogger.Info("WebView", $"Started WebKitGTK UI ({bin}, pid={_proc.Id}, url={url})");
            return true;
        }
    }

    public void SendCommand(string command)
    {
        lock (_gate)
        {
            if (_stdin is null || _proc is not { HasExited: false }) return;
            try { _stdin.WriteLine(command); } catch { }
        }
    }

    public void ToggleVisibility()
    {
        lock (_gate)
        {
            _visible = !_visible;
            SendCommand(_visible ? "SHOW" : "HIDE");
        }
    }

    public void Hide()
    {
        lock (_gate)
        {
            _visible = false;
            SendCommand("HIDE");
        }
    }

    public void Show()
    {
        lock (_gate)
        {
            _visible = true;
            SendCommand("SHOW");
        }
    }

    public void Resize(int w, int h) => SendCommand($"RESIZE {w} {h}");
    public void Minimize() => SendCommand("MINIMIZE");
    public void Maximize() => SendCommand("MAXIMIZE");
    public void Restore() => SendCommand("RESTORE");
    public void Close() => SendCommand("CLOSE");

    public void Dispose()
    {
        lock (_gate)
        {
            try { _stdin?.WriteLine("QUIT"); _stdin?.Dispose(); } catch { }
            _stdin = null;
            if (_proc is { HasExited: false })
            {
                try { if (!_proc.WaitForExit(500)) _proc.Kill(); } catch { }
            }
            _proc?.Dispose();
            _proc = null;
        }
    }
}
