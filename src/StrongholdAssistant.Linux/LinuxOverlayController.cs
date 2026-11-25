using System.Diagnostics;
using System.Globalization;
using System.Text;
using ASPAssistant.Core;
using ASPAssistant.Core.Interop;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;

namespace StrongholdAssistant.Host;

public sealed record OverlayPlacementMove(string Operator, FieldCell From, FieldCell To, string? Facing);
public sealed record OverlayPlacementPlan(string BasedOn, IReadOnlyList<OverlayPlacementMove> Moves);

/// <summary>
/// Manages the native GTK4 + gtk4-layer-shell transparent click-through overlay process
/// (stronghold-overlay) and renders ShopMarker, Placement, Route, Pending, and Upgrade layers.
/// </summary>
internal sealed class LinuxOverlayController : IDisposable
{
    private readonly object _gate = new();
    private Process? _proc;
    private StreamWriter? _stdin;
    private bool _enabled = true;
    private bool _visible;
    private bool _hiddenForCapture;
    private bool _routesHiddenByKey;

    private NiriWindowGeometry? _geom;
    private IReadOnlyList<ShopItem> _shopItems = [];
    private MarkerStyles _markerStyles = new();
    private BoardRoutes? _boardRoutes;
    private RouteOverlaySettings _routeSettings = new();
    private FieldState? _field;
    private FieldOverlayGeometry? _fieldGeometry;
    private OverlayPlacementPlan? _placementPlan;
    private OverlayLayerSettings _layerSettings = new();
    private System.Drawing.RectangleF? _upgradeButton;
    private int? _upgradeLevel;
    private string? _lastSentFrame;
    private readonly StringBuilder _frameBuilder = new(2048);
    private readonly Timer _routeCycleTimer;
    private int _routeCycleIndex;
    private int _routeCycleCount;
    private string? _lastRouteKey;

    public bool IsVisible => _visible;
    public event Action? PlacementChanged;

    public LinuxOverlayController(bool enabled = true)
    {
        _enabled = enabled;
        _routeCycleTimer = new Timer(_ => OnRouteCycleTick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    private void OnRouteCycleTick()
    {
        lock (_gate)
        {
            if (!_enabled || _hiddenForCapture || _routesHiddenByKey || !_routeSettings.Enabled
                || _boardRoutes is null || _routeCycleCount <= 1
                || !string.Equals(_routeSettings.Overlap, "cycle", StringComparison.OrdinalIgnoreCase))
            {
                _routeCycleTimer.Change(Timeout.Infinite, Timeout.Infinite);
                return;
            }
            _routeCycleIndex = (_routeCycleIndex + 1) % _routeCycleCount;
            RedrawLocked();
        }
    }

    private bool EnsureProcessLocked()
    {
        if (!_enabled) return false;
        if (_proc is { HasExited: false } && _stdin is not null) return true;

        string? bin = FindHelperBinary("stronghold-overlay");
        if (bin is null)
        {
            AppLogger.Warn("Overlay", "stronghold-overlay binary not found; overlay disabled.");
            _enabled = false;
            return false;
        }
        try
        {
            var psi = new ProcessStartInfo(bin)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            _proc = Process.Start(psi);
            if (_proc is null) return false;
            var stderr = _proc.StandardError;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (await stderr.ReadLineAsync() is { } line)
                        if (!string.IsNullOrWhiteSpace(line))
                            AppLogger.Warn("Overlay", line);
                }
                catch { }
            });
            _stdin = new StreamWriter(_proc.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = false };
            AppLogger.Info("Overlay", $"Started gtk4-layer-shell overlay helper ({bin}, pid={_proc.Id})");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("Overlay", $"Failed to start stronghold-overlay: {ex.Message}");
            _enabled = false;
            return false;
        }
    }

    internal static string? FindHelperBinary(string name)
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, name),
            Path.Combine(PackageLayout.Root, name),
            Path.Combine(PackageLayout.Root, "native", name),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "native", name)
        ];
        foreach (var c in candidates)
        {
            var full = Path.GetFullPath(c);
            if (File.Exists(full)) return full;
        }
        return null;
    }

    private static bool SameGeometry(NiriWindowGeometry? a, NiriWindowGeometry? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        return a.OnActiveWorkspace == b.OnActiveWorkspace
            && a.LocalX == b.LocalX && a.LocalY == b.LocalY
            && a.LocalWidth == b.LocalWidth && a.LocalHeight == b.LocalHeight
            && string.Equals(a.Output.Name, b.Output.Name, StringComparison.Ordinal);
    }

    public void UpdateGeometry(NiriWindowGeometry? geom)
    {
        lock (_gate)
        {
            if (SameGeometry(_geom, geom)) return;
            _geom = geom;
            RedrawLocked();
        }
    }

    public void UpdateRefreshState(
        OverlayLayerSettings layerSettings,
        IReadOnlyList<ShopItem> shopItems,
        MarkerStyles markerStyles,
        System.Drawing.RectangleF? upgradeButton,
        int? upgradeLevel)
    {
        lock (_gate)
        {
            _layerSettings = layerSettings;
            _shopItems = shopItems;
            _markerStyles = markerStyles;
            _upgradeButton = upgradeButton;
            _upgradeLevel = upgradeLevel;
            RedrawLocked();
        }
    }

    public void SetCaptureHidden(bool hidden)
    {
        lock (_gate)
        {
            if (_hiddenForCapture == hidden) return;
            _hiddenForCapture = hidden;
            RedrawLocked();
        }
    }

    public void Hide()
    {
        lock (_gate)
        {
            _visible = false;
            _lastSentFrame = null;
            _routeCycleCount = 0;
            _routeCycleTimer.Change(Timeout.Infinite, Timeout.Infinite);
            SendLineLocked("HIDE");
        }
    }

    public void UpdateShopItems(IReadOnlyList<ShopItem> items, MarkerStyles? styles = null)
    {
        lock (_gate)
        {
            _shopItems = items;
            if (styles is not null) _markerStyles = styles;
            RedrawLocked();
        }
    }

    public void UpdateRoutes(BoardRoutes? routes, RouteOverlaySettings settings)
    {
        lock (_gate)
        {
            string? key = routes is null ? null : $"{routes.MapId}|{routes.Round}|{routes.BossId}";
            if (key != _lastRouteKey || settings != _routeSettings)
            {
                _lastRouteKey = key;
                _routeCycleIndex = 0;
            }
            _boardRoutes = routes;
            _routeSettings = settings;
            RedrawLocked();
        }
    }

    public void UpdateRouteSettings(RouteOverlaySettings settings)
    {
        lock (_gate)
        {
            if (_routeSettings == settings) return;
            _routeSettings = settings;
            _routeCycleIndex = 0;
            RedrawLocked();
        }
    }

    public void ToggleRoutesByHotkey()
    {
        lock (_gate)
        {
            _routesHiddenByKey = !_routesHiddenByKey;
            RedrawLocked();
        }
    }

    public void UpdateField(FieldState? field, FieldOverlayGeometry? geometry)
    {
        lock (_gate)
        {
            _field = field;
            _fieldGeometry = geometry;
            RedrawLocked();
        }
    }

    public void UpdatePlacement(OverlayPlacementPlan? plan)
    {
        lock (_gate)
        {
            _placementPlan = plan;
            RedrawLocked();
        }
        PlacementChanged?.Invoke();
    }

    public void UpdateLayerSettings(OverlayLayerSettings settings)
    {
        lock (_gate)
        {
            if (_layerSettings == settings) return;
            _layerSettings = settings;
            RedrawLocked();
        }
    }

    public void UpdateUpgrade(System.Drawing.RectangleF? button, int? level)
    {
        lock (_gate)
        {
            _upgradeButton = button;
            _upgradeLevel = level;
            RedrawLocked();
        }
    }

    private void RedrawLocked()
    {
        if (!_enabled) return;
        if (_hiddenForCapture || _geom is null || !_geom.OnActiveWorkspace || _geom.LocalWidth <= 0 || _geom.LocalHeight <= 0)
        {
            if (_routeCycleCount > 0)
            {
                _routeCycleCount = 0;
                _routeCycleTimer.Change(Timeout.Infinite, Timeout.Infinite);
            }
            if (_visible)
            {
                _visible = false;
                _lastSentFrame = null;
                SendLineLocked("HIDE");
            }
            return;
        }

        double w = _geom.LocalWidth;
        double h = _geom.LocalHeight;
        var cmds = new List<string>(64);

        // 1. Enemy routes (bottom layer)
        if (_boardRoutes is not null && !_routesHiddenByKey && _routeSettings.Enabled)
        {
            var shown = FilterRoutes(_boardRoutes, _routeSettings);
            bool cycling = string.Equals(_routeSettings.Overlap, "cycle", StringComparison.OrdinalIgnoreCase) && shown.Count > 1;
            int prevCount = _routeCycleCount;
            _routeCycleCount = cycling ? shown.Count : 0;
            if (cycling)
            {
                _routeCycleIndex %= shown.Count;
                if (prevCount <= 1)
                {
                    int ms = Math.Clamp((int)Math.Round(_routeSettings.CycleSeconds * 1000), 800, 10000);
                    _routeCycleTimer.Change(ms, ms);
                }
            }
            else if (prevCount > 1)
            {
                _routeCycleTimer.Change(Timeout.Infinite, Timeout.Infinite);
            }
            EmitRoutes(cmds, _boardRoutes, shown, cycling ? _routeCycleIndex : -1, w, h);
        }
        else if (_routeCycleCount > 0)
        {
            _routeCycleCount = 0;
            _routeCycleTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        // 2. Placement recommendations
        if (_layerSettings.Placement && _placementPlan is not null && _field is not null && _fieldGeometry?.Projection is { } proj)
            EmitPlacement(cmds, _placementPlan, proj, w, h);

        // 3. Pending (?) operator badges
        if (_layerSettings.Pending && _field is not null && _fieldGeometry is not null)
            EmitPending(cmds, _field, _fieldGeometry, w, h);

        // 4. Shop card highlight markers
        EmitShopMarkers(cmds, _shopItems, _markerStyles, w, h);

        // 5. Closure upgrade button highlight
        if (_upgradeButton is { } btn && _upgradeLevel is { } lvl)
            EmitUpgrade(cmds, btn, lvl, w, h);

        if (cmds.Count == 0)
        {
            if (_visible)
            {
                _visible = false;
                _lastSentFrame = null;
                SendLineLocked("HIDE");
            }
            return;
        }

        _frameBuilder.Clear();
        _frameBuilder.Append("SHOW ").Append(_geom.Output.Name).Append(' ')
            .Append(_geom.LocalX).Append(' ').Append(_geom.LocalY).Append(' ')
            .Append(_geom.LocalWidth).Append(' ').Append(_geom.LocalHeight).Append('\n');
        _frameBuilder.Append("CLEAR\n");
        foreach (var c in cmds) _frameBuilder.Append(c).Append('\n');
        _frameBuilder.Append("COMMIT\n");
        var payload = _frameBuilder.ToString();

        if (_visible && _proc is { HasExited: false } && string.Equals(payload, _lastSentFrame, StringComparison.Ordinal))
            return;

        if (!EnsureProcessLocked()) return;

        try
        {
            _stdin!.Write(payload);
            _stdin.Flush();
            _lastSentFrame = payload;
            _visible = true;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("Overlay", $"Failed to push overlay frame: {ex.Message}");
            _lastSentFrame = null;
            _visible = false;
        }
    }

    private void SendLineLocked(string line)
    {
        if (_stdin is null || _proc is not { HasExited: false }) return;
        try { _stdin.WriteLine(line); _stdin.Flush(); } catch { }
    }

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static (double R, double G, double B) ParseHex(string hex, (double, double, double) fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        hex = hex.Trim().TrimStart('#');
        if (hex.Length == 8) hex = hex[2..];
        if (hex.Length != 6) return fallback;
        if (byte.TryParse(hex[..2], NumberStyles.HexNumber, null, out var r)
            && byte.TryParse(hex[2..4], NumberStyles.HexNumber, null, out var g)
            && byte.TryParse(hex[4..6], NumberStyles.HexNumber, null, out var b))
            return (r / 255.0, g / 255.0, b / 255.0);
        return fallback;
    }

    private static void EmitShopMarkers(List<string> cmds, IReadOnlyList<ShopItem> items, MarkerStyles styles, double w, double h)
    {
        foreach (var item in items)
        {
            if (!item.IsTracked && !item.IsSecondary) continue;
            int kind = item.IsTracked ? (item.IsKey ? 2 : 1) : 0;
            var style = kind == 2 ? styles.Key : kind == 1 ? styles.Tracked : styles.Pickable;
            if (!style.Show) continue;

            var r = item.CardRegion;
            double cx = r.X * w, cy = r.Y * h, cw = r.Width * w, ch = r.Height * h;
            if (cw <= 2 || ch <= 2) continue;

            bool secondary = kind == 0;
            var (cr, cg, cb) = ParseHex(style.Color, kind == 2 ? (0.95, 0.70, 0.13) : kind == 1 ? (0.13, 0.89, 0.70) : (0.69, 0.56, 0.99));
            double Light(double c) => c + (1.0 - c) * 0.35;
            double Dark(double c) => c * 0.14;

            double offset = 0.012 * cw;
            double fx = cx - offset, fy = cy - offset, fw = cw + 2 * offset, fh = ch + 2 * offset;
            double thick = Math.Max(2.0, (secondary ? 0.012 : 0.017) * cw);

            // Outer dark contrast border (matches Windows ShopMarkerLayer)
            cmds.Add($"RECT {F(fx - 2)} {F(fy - 2)} {F(fw + 4)} {F(fh + 4)} 0 0 0 {F(secondary ? 0.65 : 0.75)} 2 0 0 0 0 0");
            // Inner dark contrast border
            double innerThick = Math.Max(1.5, 0.012 * cw);
            cmds.Add($"RECT {F(fx + thick)} {F(fy + thick)} {F(fw - 2 * thick)} {F(fh - 2 * thick)} 0 0 0 {F(secondary ? 0.70 : 0.80)} {F(innerThick)} 0 0 0 0 0");
            // Main colored border (zero interior fill so card art and OCR stay 100% untouched)
            cmds.Add($"RECT {F(fx)} {F(fy)} {F(fw)} {F(fh)} {F(cr)} {F(cg)} {F(cb)} 0.96 {F(thick)} {(secondary ? 1 : 0)} 0 0 0 0");

            // Bright corner L-brackets for primary (key / tracked) cards
            if (!secondary)
            {
                double cornerStroke = Math.Max(3.0, 0.029 * cw);
                double arm = 0.115 * cw;
                double left = fx + cornerStroke * 0.5, top = fy + cornerStroke * 0.5;
                double right = fx + fw - cornerStroke * 0.5, bottom = fy + fh - cornerStroke * 0.5;
                double lr = Light(cr), lg = Light(cg), lb = Light(cb);
                // Top-left L
                cmds.Add($"POLY 3 {F(left)} {F(top + arm)} {F(left)} {F(top)} {F(left + arm)} {F(top)} 0 0 0 0.85 {F(cornerStroke + 3)} 0 0 0 0 0 0");
                cmds.Add($"POLY 3 {F(left)} {F(top + arm)} {F(left)} {F(top)} {F(left + arm)} {F(top)} {F(lr)} {F(lg)} {F(lb)} 1 {F(cornerStroke)} 0 0 0 0 0 0");
                // Bottom-right L
                cmds.Add($"POLY 3 {F(right - arm)} {F(bottom)} {F(right)} {F(bottom)} {F(right)} {F(bottom - arm)} 0 0 0 0.85 {F(cornerStroke + 3)} 0 0 0 0 0 0");
                cmds.Add($"POLY 3 {F(right - arm)} {F(bottom)} {F(right)} {F(bottom)} {F(right)} {F(bottom - arm)} {F(lr)} {F(lg)} {F(lb)} 1 {F(cornerStroke)} 0 0 0 0 0 0");
            }

            if (styles.Labels)
            {
                string label = kind == 2 ? "★ 关键" : kind == 1 ? "★ 追踪" : "可抓";
                double tagH = Math.Clamp(0.13 * cw, 18, 30);
                double fontPx = Math.Clamp(0.08 * cw, 11, 18);
                double padX = Math.Clamp(0.05 * cw, 6, 12);
                double padY = 2.0;
                double tagX = fx;
                double tagY = fy - 4.0;
                if (secondary)
                {
                    double borderW = Math.Max(1.2, 0.008 * cw);
                    cmds.Add($"TAG {F(tagX)} {F(tagY)} {F(tagH)} {F(fontPx)} 0 {F(cr)} {F(cg)} {F(cb)} 1 {F(borderW)} 0.024 0.078 0.063 0.92 {F(padX)} {F(padY)} 2 {label}");
                }
                else
                {
                    cmds.Add($"TAG {F(tagX)} {F(tagY)} {F(tagH)} {F(fontPx)} 1 {F(Dark(cr))} {F(Dark(cg))} {F(Dark(cb))} 1 0 {F(cr)} {F(cg)} {F(cb)} 1 {F(padX)} {F(padY)} 2 {label}");
                }
            }
        }
    }

    private static void EmitPlacement(List<string> cmds, OverlayPlacementPlan plan, BoardProjection projection, double w, double h)
    {
        var moves = plan.Moves.Where(m => m.From != m.To).ToArray();
        var arrivals = moves.Select(m => m.To).Where(c => c.Zone == "board").ToHashSet();
        foreach (var move in moves.OrderByDescending(m => m.To.Zone == "bench"))
        {
            bool leaving = move.To.Zone == "bench";
            var cell = leaving ? move.From : move.To;
            if (cell.Zone != "board") continue;

            (double X, double Y) P(double col, double row)
            {
                var p = projection.Project(col, row);
                return (p.X * w, p.Y * h);
            }
            var p0 = P(cell.Col + 0.08, cell.Row + 0.08);
            var p1 = P(cell.Col + 0.92, cell.Row + 0.08);
            var p2 = P(cell.Col + 0.92, cell.Row + 0.92);
            var p3 = P(cell.Col + 0.08, cell.Row + 0.92);

            double r = leaving ? 1.0 : 0.133, g = leaving ? 0.541 : 0.890, b = leaving ? 0.502 : 0.698;
            // 同格换人时（撤下旧干员、放上新干员）：下场改为格子左上角小标签「撤 XX」，避免红框遮挡上场干员与朝向（v1.3.9）。
            if (leaving && arrivals.Contains(cell))
            {
                double tagFont = Math.Clamp(h * 0.0125, 10, 17);
                double tagH = tagFont + 6;
                double padX = Math.Clamp(h * 0.0045, 4, 8);
                cmds.Add($"TAG {F(p0.X)} {F(p0.Y + tagH)} {F(tagH)} {F(tagFont)} 0 {F(r)} {F(g)} {F(b)} 0.95 1 0 0 0 0.65 {F(padX)} 2 2 撤 {move.Operator}");
                continue;
            }
            double sw = Math.Max(2.0, h * 0.0022);
            cmds.Add($"POLY 4 {F(p0.X)} {F(p0.Y)} {F(p1.X)} {F(p1.Y)} {F(p2.X)} {F(p2.Y)} {F(p3.X)} {F(p3.Y)} {F(r)} {F(g)} {F(b)} 0.95 {F(sw)} {(leaving ? 1 : 0)} {F(r)} {F(g)} {F(b)} 0.15 1");

            double lx = (p2.X + p3.X) * 0.5;
            double ly = (p2.Y + p3.Y) * 0.5;
            double fontPx = Math.Clamp(h * 0.014, 11, 20);
            var text = move.Operator + (leaving ? " 下场" : "");
            cmds.Add($"TEXT {F(lx)} {F(ly)} {F(fontPx)} 1 1 1 1 1 0 0 0 0.72 6 2 1 {text}");

            if (!leaving && move.Facing is { } facing)
            {
                int edge = facing switch { "up" => 0, "right" => 1, "down" => 2, "left" => 3, _ => -1 };
                if (edge >= 0)
                {
                    (double X, double Y)[] pts = [p0, p1, p2, p3];
                    var a = pts[edge];
                    var c = pts[(edge + 1) % 4];
                    double mx = (a.X + c.X) * 0.5, my = (a.Y + c.Y) * 0.5;
                    double tx = c.X - a.X, ty = c.Y - a.Y;
                    double len = Math.Sqrt(tx * tx + ty * ty);
                    if (len > 1e-3)
                    {
                        tx /= len; ty /= len;
                        double ox = ty, oy = -tx;
                        double sz = h * 0.009;
                        cmds.Add($"POLY 3 {F(mx + ox * sz)} {F(my + oy * sz)} {F(mx - tx * sz * 0.5)} {F(my - ty * sz * 0.5)} {F(mx + tx * sz * 0.5)} {F(my + ty * sz * 0.5)} {F(r)} {F(g)} {F(b)} 1 0 0 {F(r)} {F(g)} {F(b)} 0.95 1");
                    }
                }
            }
        }
    }

    private static void EmitPending(List<string> cmds, FieldState field, FieldOverlayGeometry geometry, double w, double h)
    {
        double radius = h * 0.012;
        foreach (var unit in field.Units)
        {
            if (unit.Status != "pending") continue;
            var cell = unit.Cell;
            double x, y;
            if (geometry.Badges.TryGetValue(cell, out var badge))
            {
                x = badge.X * w;
                y = (badge.Y - badge.Size) * h - radius;
            }
            else if (geometry.Anchors.TryGetValue(cell, out var anchor))
            {
                x = anchor.X * w;
                y = anchor.Y * h - 40 * h / 1080.0 - radius;
            }
            else continue;

            cmds.Add($"CIRCLE {F(x)} {F(y)} {F(radius)} 1 0.87 0.53 0.9 2 0.953 0.702 0.133 0.95");
            cmds.Add($"TEXT {F(x)} {F(y)} {F(radius * 1.5)} 1 0.16 0.11 0.01 1 0 0 0 0 0 0 1 ?");
        }
    }

    private static void EmitUpgrade(List<string> cmds, System.Drawing.RectangleF box, int level, double w, double h)
    {
        double k = h / 1080.0;
        double bx = box.X * w, by = box.Y * h, bw = box.Width * w, bh = box.Height * h;
        cmds.Add($"RECT {F(bx)} {F(by)} {F(bw)} {F(bh)} 0.953 0.702 0.133 0.95 {F(2.5 * k)} 0 0.953 0.702 0.133 0.08");
        cmds.Add($"TEXT {F(bx + bw * 0.5)} {F(by - 14 * k)} {F(15 * k)} 1 0.953 0.702 0.133 1 0.063 0.071 0.082 0.86 {F(10 * k)} {F(4 * k)} 1 升本 → {level} 级");
    }

    private static List<EnemyRoute> FilterRoutes(BoardRoutes routes, RouteOverlaySettings settings)
    {
        var mainFly = routes.Routes.Where(r => r.Motion == "fly" && !r.Boss).MaxBy(r => r.Count);
        var sideSetting = settings.Side == "auto" ? routes.PlayerSide : settings.Side;
        return routes.Routes
            .Where(r => r.Boss || r.Motion != "fly" || settings.Fly == "all" || (settings.Fly == "main" && ReferenceEquals(r, mainFly)))
            .Where(r => r.Side is null || sideSetting is null || r.Side == sideSetting)
            .ToList();
    }

    private static void EmitRoutes(List<string> cmds, BoardRoutes routes, List<EnemyRoute> shown, int activeIndex, double w, double h)
    {
        double k = h / 1080.0;
        // 降低整体透明度，既减少遮挡干员与阶数角标，也降低截屏回读干扰；循环模式下先画淡化背景线，再画当前高亮线。
        for (int pass = 0; pass < 2; pass++)
        {
            for (int ri = 0; ri < shown.Count; ri++)
            {
                bool isActive = activeIndex < 0 || ri == activeIndex;
                if ((pass == 0 && isActive) || (pass == 1 && !isActive)) continue;

                var route = shown[ri];
                bool isFly = string.Equals(route.Motion, "fly", StringComparison.OrdinalIgnoreCase);
                bool isBoss = route.Boss;
                var (cr, cg, cb) = isBoss
                    ? (0.953, 0.702, 0.133)
                    : isFly ? (0.780, 0.608, 1.000)
                    : (1.000, 0.353, 0.306);
                double shiftX = isFly ? 0.2 : isBoss ? -0.17 : 0;
                double shiftY = isFly ? -0.26 : isBoss ? 0.2 : 0;

                double outlineAlpha = isActive ? (activeIndex >= 0 ? 0.26 : 0.22) : 0.07;
                double coreAlpha = isActive ? (activeIndex >= 0 ? 0.46 : 0.38) : 0.14;
                double outlineW = (isFly ? 3.4 : isBoss ? 4.8 : 4.2) * k;
                double coreW = (isFly ? 1.7 : isBoss ? 2.6 : 2.1) * k;

                foreach (var segment in route.Segments)
                {
                    if (segment.Count < 2) continue;
                    int n = Math.Min(segment.Count, 64);
                    var sbPts = new StringBuilder();
                    var pts = new (double X, double Y)[n];
                    for (int i = 0; i < n; i++)
                    {
                        var pt = segment[i];
                        var proj = routes.Projection.Project(pt.Col + 0.5 + shiftX, pt.Row + 0.5 + shiftY);
                        double px = proj.X * w, py = proj.Y * h;
                        pts[i] = (px, py);
                        if (i > 0) sbPts.Append(' ');
                        sbPts.Append(F(px)).Append(' ').Append(F(py));
                    }
                    // Translucent dark under-stroke + soft colored core
                    cmds.Add($"POLY {n} {sbPts} 0.08 0.04 0.04 {F(outlineAlpha)} {F(outlineW)} 0 0 0 0 0 0");
                    cmds.Add($"POLY {n} {sbPts} {F(cr)} {F(cg)} {F(cb)} {F(coreAlpha)} {F(coreW)} {(isFly ? 1 : 0)} 0 0 0 0 0");
                    if (!isActive) continue;

                    cmds.Add($"CIRCLE {F(pts[0].X)} {F(pts[0].Y)} {F(4.2 * k)} 0.08 0.04 0.04 {F(outlineAlpha + 0.06)} {F(1.4 * k)} {F(cr)} {F(cg)} {F(cb)} {F(coreAlpha + 0.06)}");

                    // Directional arrowhead at segment end (and midpoint for longer routes)
                    int[] arrowEnds = n >= 4 ? [n / 2, n - 1] : [n - 1];
                    foreach (int idx in arrowEnds)
                    {
                        double dx = pts[idx].X - pts[idx - 1].X;
                        double dy = pts[idx].Y - pts[idx - 1].Y;
                        double len = Math.Sqrt(dx * dx + dy * dy);
                        if (len < 6 * k) continue;
                        double ux = dx / len, uy = dy / len;
                        double tipX = pts[idx].X, tipY = pts[idx].Y;
                        double headL = 10 * k, headW = 5.2 * k;
                        double lx = tipX - ux * headL - uy * headW;
                        double ly = tipY - uy * headL + ux * headW;
                        double rx = tipX - ux * headL + uy * headW;
                        double ry = tipY - uy * headL - ux * headW;
                        cmds.Add($"POLY 3 {F(tipX)} {F(tipY)} {F(lx)} {F(ly)} {F(rx)} {F(ry)} 0.08 0.04 0.04 {F(outlineAlpha + 0.08)} {F(1.2 * k)} 0 {F(cr)} {F(cg)} {F(cb)} {F(coreAlpha + 0.10)} 1");
                    }
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _routeCycleTimer.Dispose();
            try
            {
                _stdin?.WriteLine("QUIT");
                _stdin?.Flush();
                _stdin?.Dispose();
            }
            catch { }
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
