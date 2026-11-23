using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ASPAssistant.Core;

namespace StrongholdAssistant.Host;

/// <summary>
/// Serves the built Svelte UI over localhost HTTP and provides a WebSocket bridge (/ws)
/// that emulates window.chrome.webview for WebKitGTK and standard Linux browsers.
/// </summary>
internal sealed class UiBridgeServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly HttpClient _plazaHttp = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };
    private readonly string _uiRoot;
    private readonly string _controlDir;
    private readonly Func<string, Task<string>> _handleMessage;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<Guid, WebSocketClient> _clients = new();

    public int Port { get; }
    public string Url => $"http://127.0.0.1:{Port}/";

    private sealed class WebSocketClient(WebSocket socket)
    {
        public WebSocket Socket { get; } = socket;
        public SemaphoreSlim SendLock { get; } = new(1, 1);
    }

    private const string BridgeShimScript = """
<script>
(() => {
  if (window.chrome && window.chrome.webview) return;
  const listeners = new Set();
  const queue = [];
  let ws = null;
  let open = false;
  function connect() {
    const proto = location.protocol === 'https:' ? 'wss://' : 'ws://';
    ws = new WebSocket(proto + location.host + '/ws');
    ws.onopen = () => {
      open = true;
      while (queue.length > 0) ws.send(queue.shift());
    };
    ws.onmessage = (ev) => {
      try {
        const data = JSON.parse(ev.data);
        listeners.forEach((cb) => { try { cb({ data }); } catch (e) { console.error(e); } });
      } catch (e) { console.error('bridge parse error', e); }
    };
    ws.onclose = () => {
      open = false;
      setTimeout(connect, 500);
    };
    ws.onerror = () => { try { ws.close(); } catch {} };
  }
  connect();
  window.chrome = window.chrome || {};
  window.chrome.webview = {
    postMessage(msg) {
      const payload = typeof msg === 'string' ? msg : JSON.stringify(msg);
      if (open && ws && ws.readyState === 1) ws.send(payload);
      else queue.push(payload);
    },
    addEventListener(type, cb) {
      if (type === 'message' && typeof cb === 'function') listeners.add(cb);
    },
    removeEventListener(type, cb) {
      if (type === 'message') listeners.delete(cb);
    }
  };
  // Route Plaza API requests through local reverse proxy to preserve Origin: https://app.local
  const PLAZA_ORIGIN = 'https://sa.lubeiluchen.com';
  const origFetch = window.fetch.bind(window);
  window.fetch = (input, init) => {
    try {
      const url = typeof input === 'string' ? input : (input instanceof URL ? input.href : input && input.url);
      if (url && url.startsWith(PLAZA_ORIGIN + '/')) {
        const proxied = '/_plaza' + url.slice(PLAZA_ORIGIN.length);
        if (typeof input === 'string' || input instanceof URL) return origFetch(proxied, init);
        return origFetch(new Request(proxied, input), init);
      }
    } catch {}
    return origFetch(input, init);
  };
})();
</script>
""";

    public UiBridgeServer(string uiRoot, string controlDir, int preferredPort, Func<string, Task<string>> handleMessage)
    {
        _uiRoot = Path.GetFullPath(uiRoot);
        _controlDir = Path.GetFullPath(controlDir);
        _handleMessage = handleMessage;
        Port = preferredPort > 0 ? preferredPort : FindFreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = AcceptLoopAsync(_stop.Token);
    }

    private static int FindFreePort()
    {
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        int port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        return port;
    }

    public void BroadcastEvent(string eventName, object? data)
    {
        var json = JsonSerializer.Serialize(new { @event = eventName, data }, HostApi.JsonOptions);
        BroadcastRaw(json);
    }

    public void BroadcastRaw(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        foreach (var (id, client) in _clients)
        {
            if (client.Socket.State != WebSocketState.Open)
            {
                _clients.TryRemove(id, out _);
                continue;
            }
            _ = SendSafeAsync(id, client, bytes);
        }
    }

    private async Task SendSafeAsync(Guid id, WebSocketClient client, byte[] bytes)
    {
        await client.SendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (client.Socket.State == WebSocketState.Open)
                await client.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, _stop.Token).ConfigureAwait(false);
        }
        catch
        {
            _clients.TryRemove(id, out _);
        }
        finally { client.SendLock.Release(); }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch when (ct.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) { break; }
            catch (HttpListenerException) { break; }
            catch (Exception ex)
            {
                AppLogger.Warn("UiBridge", $"Accept error: {ex.Message}");
                continue;
            }
            _ = HandleContextAsync(ctx, ct);
        }
    }

    private async Task HandleContextAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var path = ctx.Request.Url?.AbsolutePath ?? "/";
        if (path == "/ws" && ctx.Request.IsWebSocketRequest)
        {
            await HandleWebSocketAsync(ctx, ct).ConfigureAwait(false);
            return;
        }
        if (path.StartsWith("/_plaza/", StringComparison.Ordinal))
        {
            await HandlePlazaProxyAsync(ctx, ct).ConfigureAwait(false);
            return;
        }
        await ServeStaticAsync(ctx, path, ct).ConfigureAwait(false);
    }

    private async Task HandlePlazaProxyAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            var rawPathAndQuery = ctx.Request.RawUrl ?? "/";
            var targetSuffix = rawPathAndQuery["/_plaza".Length..];
            var targetUrl = "https://sa.lubeiluchen.com" + targetSuffix;

            using var req = new HttpRequestMessage(new HttpMethod(ctx.Request.HttpMethod), targetUrl);
            req.Headers.TryAddWithoutValidation("Origin", "https://app.local");
            req.Headers.TryAddWithoutValidation("Referer", "https://app.local/");
            if (ctx.Request.Headers["Authorization"] is { Length: > 0 } auth)
                req.Headers.TryAddWithoutValidation("Authorization", auth);
            if (ctx.Request.Headers["Accept"] is { Length: > 0 } accept)
                req.Headers.TryAddWithoutValidation("Accept", accept);

            if (ctx.Request.HasEntityBody)
            {
                using var ms = new MemoryStream();
                await ctx.Request.InputStream.CopyToAsync(ms, ct).ConfigureAwait(false);
                var content = new ByteArrayContent(ms.ToArray());
                if (!string.IsNullOrEmpty(ctx.Request.ContentType))
                    content.Headers.TryAddWithoutValidation("Content-Type", ctx.Request.ContentType);
                req.Content = content;
            }

            using var resp = await _plazaHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            ctx.Response.StatusCode = (int)resp.StatusCode;
            if (resp.Content.Headers.ContentType is { } ctHeader)
                ctx.Response.ContentType = ctHeader.ToString();
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            ctx.Response.ContentLength64 = bytes.Length;
            if (bytes.Length > 0)
                await ctx.Response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
            ctx.Response.OutputStream.Close();
        }
        catch (Exception ex)
        {
            AppLogger.Warn("PlazaProxy", $"Forward failed: {ex.Message}");
            try
            {
                ctx.Response.StatusCode = 502;
                var err = Encoding.UTF8.GetBytes("{\"error\":\"proxy_error\",\"message\":\"连不上阵容广场\"}");
                ctx.Response.ContentType = "application/json; charset=utf-8";
                ctx.Response.ContentLength64 = err.Length;
                await ctx.Response.OutputStream.WriteAsync(err, ct).ConfigureAwait(false);
                ctx.Response.OutputStream.Close();
            }
            catch { }
        }
    }

    private async Task HandleWebSocketAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        WebSocketContext wsCtx;
        try { wsCtx = await ctx.AcceptWebSocketAsync(null).ConfigureAwait(false); }
        catch (Exception ex)
        {
            ctx.Response.StatusCode = 500;
            ctx.Response.Close();
            AppLogger.Warn("UiBridge", $"WebSocket upgrade failed: {ex.Message}");
            return;
        }

        var id = Guid.NewGuid();
        var client = new WebSocketClient(wsCtx.WebSocket);
        _clients[id] = client;
        var buffer = new byte[65536];
        using var ms = new MemoryStream();
        try
        {
            while (client.Socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                ms.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await client.Socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await client.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", ct).ConfigureAwait(false);
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text) continue;
                var msg = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                _ = Task.Run(async () =>
                {
                    var reply = await _handleMessage(msg).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(reply))
                        await SendSafeAsync(id, client, Encoding.UTF8.GetBytes(reply)).ConfigureAwait(false);
                }, ct);
            }
        }
        catch when (ct.IsCancellationRequested) { }
        catch { }
        finally
        {
            _clients.TryRemove(id, out _);
            client.Socket.Dispose();
        }
    }

    private async Task ServeStaticAsync(HttpListenerContext ctx, string urlPath, CancellationToken ct)
    {
        try
        {
            string baseDir = _uiRoot;
            string rel = urlPath.TrimStart('/');
            if (rel.StartsWith("_control/", StringComparison.Ordinal))
            {
                baseDir = _controlDir;
                rel = rel["_control/".Length..];
            }
            if (string.IsNullOrEmpty(rel)) rel = "index.html";
            rel = Uri.UnescapeDataString(rel).Replace('\\', '/');
            var fullPath = Path.GetFullPath(Path.Combine(baseDir, rel));
            if (!fullPath.StartsWith(baseDir, StringComparison.Ordinal) || !File.Exists(fullPath))
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }

            ctx.Response.ContentType = GetContentType(fullPath);
            ctx.Response.Headers["Cache-Control"] = "no-cache";

            if (Path.GetFileName(fullPath).Equals("index.html", StringComparison.OrdinalIgnoreCase) && baseDir == _uiRoot)
            {
                var html = await File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct).ConfigureAwait(false);
                int idx = html.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
                html = idx >= 0
                    ? html.Insert(idx + 6, "\n" + BridgeShimScript)
                    : BridgeShimScript + html;
                var bytes = Encoding.UTF8.GetBytes(html);
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
            }
            else
            {
                var bytes = await File.ReadAllBytesAsync(fullPath, ct).ConfigureAwait(false);
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
            }
            ctx.Response.OutputStream.Close();
        }
        catch
        {
            try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
        }
    }

    private static string GetContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "application/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".woff2" => "font/woff2",
        ".woff" => "font/woff",
        ".ico" => "image/x-icon",
        _ => "application/octet-stream"
    };

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch { }
        foreach (var (_, c) in _clients)
        {
            try { c.Socket.Dispose(); } catch { }
        }
        _clients.Clear();
        _plazaHttp.Dispose();
        _stop.Dispose();
    }
}
