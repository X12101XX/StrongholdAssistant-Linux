#if LINUX_PORT
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ASPAssistant.Core.Interop;

public sealed record NiriOutputInfo(
    string Name,
    int LogicalX, int LogicalY, int LogicalWidth, int LogicalHeight,
    double Scale,
    int PhysicalWidth, int PhysicalHeight);

public sealed record NiriWorkspaceInfo(
    ulong Id,
    int Idx,
    string? Output,
    bool IsActive,
    bool IsFocused,
    ulong? ActiveWindowId);

public sealed record NiriWindowInfo(
    ulong Id,
    string Title,
    string AppId,
    int? Pid,
    ulong? WorkspaceId,
    bool IsFocused,
    bool IsFloating,
    int WindowWidth,
    int WindowHeight,
    double TileWidth,
    double TileHeight,
    double? TilePosX,
    double? TilePosY,
    double OffsetInTileX,
    double OffsetInTileY,
    long FocusTimestampNanos = 0);

public sealed record NiriWindowGeometry(
    NiriWindowInfo Window,
    NiriOutputInfo Output,
    bool OnActiveWorkspace,
    /// <summary>Output-local logical rect (for Wayland screencopy region & layer-shell overlay).</summary>
    int LocalX, int LocalY, int LocalWidth, int LocalHeight,
    /// <summary>Global physical pixel rect (for Core WindowTracker / ScreenCapture compatibility).</summary>
    RECT PhysicalRect,
    RECT OutputPhysicalBounds);

/// <summary>
/// Direct Unix-domain-socket IPC client for niri ($NIRI_SOCKET).
/// </summary>
public static class NiriIpc
{
    private static readonly object SnapshotLock = new();
    private static long _snapshotAtMs;
    private static IReadOnlyDictionary<string, NiriOutputInfo> _outputs = new Dictionary<string, NiriOutputInfo>();
    private static IReadOnlyDictionary<ulong, NiriWorkspaceInfo> _workspaces = new Dictionary<ulong, NiriWorkspaceInfo>();
    private static IReadOnlyList<NiriWindowInfo> _windows = [];
    private static ulong _focusedWindowId;
    private const int CacheTtlMs = 40;

    public static string? SocketPath => Environment.GetEnvironmentVariable("NIRI_SOCKET");
    public static bool IsAvailable => !string.IsNullOrEmpty(SocketPath) && File.Exists(SocketPath);

    private static string? SendRequest(string jsonRequest)
    {
        var path = SocketPath;
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.ReceiveTimeout = 1000;
            socket.SendTimeout = 1000;
            socket.Connect(new UnixDomainSocketEndPoint(path));
            using var stream = new NetworkStream(socket, ownsSocket: false);
            var payload = Encoding.UTF8.GetBytes(jsonRequest + "\n");
            stream.Write(payload, 0, payload.Length);
            stream.Flush();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            return reader.ReadLine();
        }
        catch (Exception ex)
        {
            AppLogger.Warn("NiriIpc", $"IPC request failed ({jsonRequest}): {ex.Message}");
            return null;
        }
    }

    public static void InvalidateCache()
    {
        lock (SnapshotLock) _snapshotAtMs = 0;
    }

    private static void RefreshIfNeeded(bool force = false)
    {
        var now = Environment.TickCount64;
        if (!force && now - _snapshotAtMs < CacheTtlMs) return;

        var outputsJson = SendRequest("\"Outputs\"");
        var workspacesJson = SendRequest("\"Workspaces\"");
        var windowsJson = SendRequest("\"Windows\"");

        var outputs = ParseOutputs(outputsJson);
        var workspaces = ParseWorkspaces(workspacesJson);
        var windows = ParseWindows(windowsJson);

        _outputs = outputs;
        _workspaces = workspaces;
        _windows = windows;
        _focusedWindowId = windows.FirstOrDefault(w => w.IsFocused)?.Id ?? 0;
        _snapshotAtMs = Environment.TickCount64;
    }

    public static IReadOnlyList<NiriWindowInfo> GetWindows(bool force = false)
    {
        lock (SnapshotLock)
        {
            RefreshIfNeeded(force);
            return _windows;
        }
    }

    public static IReadOnlyDictionary<string, NiriOutputInfo> GetOutputs(bool force = false)
    {
        lock (SnapshotLock)
        {
            RefreshIfNeeded(force);
            return _outputs;
        }
    }

    public static IReadOnlyDictionary<ulong, NiriWorkspaceInfo> GetWorkspaces(bool force = false)
    {
        lock (SnapshotLock)
        {
            RefreshIfNeeded(force);
            return _workspaces;
        }
    }

    public static ulong GetFocusedWindowId(bool force = false)
    {
        lock (SnapshotLock)
        {
            RefreshIfNeeded(force);
            return _focusedWindowId;
        }
    }

    public static bool FocusWindow(ulong windowId)
    {
        if (windowId == 0) return false;
        var reply = SendRequest($"{{\"Action\":{{\"FocusWindow\":{{\"id\":{windowId}}}}}}}");
        InvalidateCache();
        return reply is not null && reply.Contains("\"Ok\"", StringComparison.Ordinal);
    }

    public static bool IsArknightsWindow(NiriWindowInfo w)
    {
        var customTitle = Environment.GetEnvironmentVariable("STRONGHOLD_WINDOW_TITLE");
        var customAppId = Environment.GetEnvironmentVariable("STRONGHOLD_WINDOW_APP_ID");
        if (!string.IsNullOrWhiteSpace(customTitle) && w.Title.Contains(customTitle, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.IsNullOrWhiteSpace(customAppId) && w.AppId.Contains(customAppId, StringComparison.OrdinalIgnoreCase))
            return true;

        // Exclude StrongholdAssistant itself and Hypergryph Launcher
        if (w.AppId.Contains("stronghold", StringComparison.OrdinalIgnoreCase)) return false;
        if (w.Title.Contains("启动器", StringComparison.Ordinal) || w.Title.Contains("Launcher", StringComparison.OrdinalIgnoreCase))
            return false;

        if (w.Title == "明日方舟" || w.Title.Equals("Arknights", StringComparison.OrdinalIgnoreCase))
            return true;

        if (w.AppId.Equals("arknights.exe", StringComparison.OrdinalIgnoreCase)
            || w.AppId.Equals("arknights", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    public static NiriWindowGeometry? GetWindowGeometry(ulong windowId, bool force = false)
    {
        if (windowId == 0) return null;
        lock (SnapshotLock)
        {
            RefreshIfNeeded(force);
            var w = _windows.FirstOrDefault(x => x.Id == windowId);
            if (w is null) return null;

            NiriWorkspaceInfo? ws = w.WorkspaceId is { } wsId && _workspaces.TryGetValue(wsId, out var foundWs) ? foundWs : null;
            NiriOutputInfo? output = null;
            if (ws?.Output is { } outName && _outputs.TryGetValue(outName, out var foundOut))
                output = foundOut;
            output ??= _outputs.Values.FirstOrDefault();
            if (output is null) return null;

            bool onActiveWorkspace = ws?.IsActive ?? true;
            bool visibleOnScreen = false;
            if (onActiveWorkspace)
            {
                if (w.IsFocused || ws?.ActiveWindowId == w.Id)
                {
                    visibleOnScreen = true;
                }
                else if (w.IsFloating && w.TilePosX is not null && w.TilePosY is not null)
                {
                    visibleOnScreen = true;
                }
                else if (ws is not null)
                {
                    // If a floating window (e.g. assistant sidebar) is active on this workspace,
                    // check whether Arknights is the most recently focused tiling window on this workspace.
                    var activeWin = ws.ActiveWindowId is { } aid ? _windows.FirstOrDefault(x => x.Id == aid) : null;
                    if (activeWin is { IsFloating: true })
                    {
                        var topTiled = _windows
                            .Where(x => x.WorkspaceId == ws.Id && !x.IsFloating)
                            .OrderByDescending(x => x.FocusTimestampNanos)
                            .FirstOrDefault();
                        visibleOnScreen = topTiled?.Id == w.Id;
                    }
                }
            }
            double scale = output.Scale > 0 ? output.Scale : 1.0;

            int winW = Math.Max(1, w.WindowWidth);
            int winH = Math.Max(1, w.WindowHeight);
            int localX, localY;

            if (w.TilePosX is { } tx && w.TilePosY is { } ty)
            {
                localX = (int)Math.Round(tx + w.OffsetInTileX);
                localY = (int)Math.Round(ty + w.OffsetInTileY);
            }
            else if (winW >= output.LogicalWidth && winH >= output.LogicalHeight)
            {
                localX = 0;
                localY = 0;
            }
            else
            {
                // Tiled window in niri without explicit viewport position:
                // When focused or filling a column, niri centers/fits it cleanly inside the output.
                localX = Math.Max(0, (output.LogicalWidth - winW) / 2);
                localY = Math.Max(0, (output.LogicalHeight - winH) / 2);
            }

            // Clamp to output bounds
            localX = Math.Clamp(localX, 0, Math.Max(0, output.LogicalWidth - 1));
            localY = Math.Clamp(localY, 0, Math.Max(0, output.LogicalHeight - 1));
            int clampedW = Math.Clamp(winW, 1, Math.Max(1, output.LogicalWidth - localX));
            int clampedH = Math.Clamp(winH, 1, Math.Max(1, output.LogicalHeight - localY));

            // Physical coordinates: map output logical origin + scaled local offset
            int outPhysLeft = (int)Math.Round(output.LogicalX * scale);
            int outPhysTop = (int)Math.Round(output.LogicalY * scale);
            int physLeft = outPhysLeft + (int)Math.Round(localX * scale);
            int physTop = outPhysTop + (int)Math.Round(localY * scale);
            int physW = (int)Math.Round(clampedW * scale);
            int physH = (int)Math.Round(clampedH * scale);

            var physRect = new RECT
            {
                Left = physLeft,
                Top = physTop,
                Right = physLeft + physW,
                Bottom = physTop + physH
            };
            var outBounds = new RECT
            {
                Left = outPhysLeft,
                Top = outPhysTop,
                Right = outPhysLeft + output.PhysicalWidth,
                Bottom = outPhysTop + output.PhysicalHeight
            };

            return new NiriWindowGeometry(
                w, output, visibleOnScreen,
                localX, localY, clampedW, clampedH,
                physRect, outBounds);
        }
    }

    private static Dictionary<string, NiriOutputInfo> ParseOutputs(string? json)
    {
        var result = new Dictionary<string, NiriOutputInfo>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(json)) return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Ok", out var ok) || !ok.TryGetProperty("Outputs", out var outputs))
                return result;
            foreach (var prop in outputs.EnumerateObject())
            {
                var v = prop.Value;
                var name = v.TryGetProperty("name", out var n) ? n.GetString() ?? prop.Name : prop.Name;
                int lx = 0, ly = 0, lw = 1920, lh = 1080;
                double scale = 1.0;
                if (v.TryGetProperty("logical", out var log) && log.ValueKind == JsonValueKind.Object)
                {
                    lx = log.TryGetProperty("x", out var px) ? px.GetInt32() : 0;
                    ly = log.TryGetProperty("y", out var py) ? py.GetInt32() : 0;
                    lw = log.TryGetProperty("width", out var pw) ? pw.GetInt32() : 1920;
                    lh = log.TryGetProperty("height", out var ph) ? ph.GetInt32() : 1080;
                    scale = log.TryGetProperty("scale", out var ps) ? ps.GetDouble() : 1.0;
                }
                int physW = (int)Math.Round(lw * scale);
                int physH = (int)Math.Round(lh * scale);
                if (v.TryGetProperty("current_mode", out var cm) && cm.ValueKind == JsonValueKind.Number
                    && v.TryGetProperty("modes", out var modes) && modes.ValueKind == JsonValueKind.Array)
                {
                    int idx = cm.GetInt32();
                    if (idx >= 0 && idx < modes.GetArrayLength())
                    {
                        var mode = modes[idx];
                        physW = mode.TryGetProperty("width", out var mw) ? mw.GetInt32() : physW;
                        physH = mode.TryGetProperty("height", out var mh) ? mh.GetInt32() : physH;
                    }
                }
                result[name] = new NiriOutputInfo(name, lx, ly, lw, lh, scale, physW, physH);
            }
        }
        catch { }
        return result;
    }

    private static Dictionary<ulong, NiriWorkspaceInfo> ParseWorkspaces(string? json)
    {
        var result = new Dictionary<ulong, NiriWorkspaceInfo>();
        if (string.IsNullOrEmpty(json)) return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Ok", out var ok) || !ok.TryGetProperty("Workspaces", out var list))
                return result;
            foreach (var item in list.EnumerateArray())
            {
                ulong id = item.GetProperty("id").GetUInt64();
                int idx = item.TryGetProperty("idx", out var pIdx) ? pIdx.GetInt32() : 0;
                string? output = item.TryGetProperty("output", out var pOut) && pOut.ValueKind == JsonValueKind.String ? pOut.GetString() : null;
                bool active = item.TryGetProperty("is_active", out var pAct) && pAct.GetBoolean();
                bool focused = item.TryGetProperty("is_focused", out var pFoc) && pFoc.GetBoolean();
                ulong? activeWin = item.TryGetProperty("active_window_id", out var pAw) && pAw.ValueKind == JsonValueKind.Number ? pAw.GetUInt64() : null;
                result[id] = new NiriWorkspaceInfo(id, idx, output, active, focused, activeWin);
            }
        }
        catch { }
        return result;
    }

    private static List<NiriWindowInfo> ParseWindows(string? json)
    {
        var result = new List<NiriWindowInfo>();
        if (string.IsNullOrEmpty(json)) return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Ok", out var ok) || !ok.TryGetProperty("Windows", out var list))
                return result;
            foreach (var item in list.EnumerateArray())
            {
                ulong id = item.GetProperty("id").GetUInt64();
                string title = item.TryGetProperty("title", out var pT) && pT.ValueKind == JsonValueKind.String ? pT.GetString()! : "";
                string appId = item.TryGetProperty("app_id", out var pA) && pA.ValueKind == JsonValueKind.String ? pA.GetString()! : "";
                int? pid = item.TryGetProperty("pid", out var pP) && pP.ValueKind == JsonValueKind.Number ? pP.GetInt32() : null;
                ulong? wsId = item.TryGetProperty("workspace_id", out var pW) && pW.ValueKind == JsonValueKind.Number ? pW.GetUInt64() : null;
                bool isFocused = item.TryGetProperty("is_focused", out var pF) && pF.GetBoolean();
                bool isFloating = item.TryGetProperty("is_floating", out var pFl) && pFl.GetBoolean();

                int winW = 0, winH = 0;
                double tileW = 0, tileH = 0, ox = 0, oy = 0;
                double? tileX = null, tileY = null;
                if (item.TryGetProperty("layout", out var layout) && layout.ValueKind == JsonValueKind.Object)
                {
                    if (layout.TryGetProperty("window_size", out var ws) && ws.ValueKind == JsonValueKind.Array && ws.GetArrayLength() >= 2)
                    {
                        winW = ws[0].GetInt32();
                        winH = ws[1].GetInt32();
                    }
                    if (layout.TryGetProperty("tile_size", out var ts) && ts.ValueKind == JsonValueKind.Array && ts.GetArrayLength() >= 2)
                    {
                        tileW = ts[0].GetDouble();
                        tileH = ts[1].GetDouble();
                    }
                    if (layout.TryGetProperty("tile_pos_in_workspace_view", out var tp) && tp.ValueKind == JsonValueKind.Array && tp.GetArrayLength() >= 2)
                    {
                        tileX = tp[0].GetDouble();
                        tileY = tp[1].GetDouble();
                    }
                    if (layout.TryGetProperty("window_offset_in_tile", out var wo) && wo.ValueKind == JsonValueKind.Array && wo.GetArrayLength() >= 2)
                    {
                        ox = wo[0].GetDouble();
                        oy = wo[1].GetDouble();
                    }
                }
                long focusTs = 0;
                if (item.TryGetProperty("focus_timestamp", out var ft) && ft.ValueKind == JsonValueKind.Object)
                {
                    long secs = ft.TryGetProperty("secs", out var ps) ? ps.GetInt64() : 0;
                    long nanos = ft.TryGetProperty("nanos", out var pn) ? pn.GetInt64() : 0;
                    focusTs = secs * 1_000_000_000L + nanos;
                }
                result.Add(new NiriWindowInfo(id, title, appId, pid, wsId, isFocused, isFloating,
                    winW, winH, tileW, tileH, tileX, tileY, ox, oy, focusTs));
            }
        }
        catch { }
        return result;
    }
}
#endif
