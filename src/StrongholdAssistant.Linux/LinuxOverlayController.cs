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

    public bool IsVisible => _visible;
    public event Action? PlacementChanged;

    public LinuxOverlayController(bool enabled = true)
    {
        _enabled = enabled;
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

    public void UpdateGeometry(NiriWindowGeometry? geom)
    {
        lock (_gate)
        {
            _geom = geom;
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
            _boardRoutes = routes;
            _routeSettings = settings;
            RedrawLocked();
        }
    }

    public void UpdateRouteSettings(RouteOverlaySettings settings)
    {
        lock (_gate)
        {
            _routeSettings = settings;
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
            if (_visible)
            {
                _visible = false;
                SendLineLocked("HIDE");
            }
            return;
        }

        double w = _geom.LocalWidth;
        double h = _geom.LocalHeight;
        var cmds = new List<string>(64);

        // 1. Enemy routes (bottom layer)
        if (_boardRoutes is not null && !_routesHiddenByKey && _routeSettings.Enabled)
            EmitRoutes(cmds, _boardRoutes, w, h);

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
                SendLineLocked("HIDE");
            }
            return;
        }

        if (!EnsureProcessLocked()) return;

        try
        {
            _stdin!.WriteLine($"SHOW {_geom.Output.Name} {_geom.LocalX} {_geom.LocalY} {_geom.LocalWidth} {_geom.LocalHeight}");
            _stdin.WriteLine("CLEAR");
            foreach (var c in cmds) _stdin.WriteLine(c);
            _stdin.WriteLine("COMMIT");
            _stdin.Flush();
            _visible = true;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("Overlay", $"Failed to push overlay frame: {ex.Message}");
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
            var (cr, cg, cb) = ParseHex(style.Color, kind == 2 ? (0.95, 0.70, 0.13) : kind == 1 ? (0.13, 0.89, 0.70) : (0.55, 0.75, 1.0));
            double offset = 0.012 * cw;
            double fx = cx - offset, fy = cy - offset, fw = cw + 2 * offset, fh = ch + 2 * offset;
            double thick = Math.Max(2.0, (secondary ? 0.014 : 0.020) * cw);

            // Outer dark contrast border
            cmds.Add($"RECT {F(fx - 2)} {F(fy - 2)} {F(fw + 4)} {F(fh + 4)} 0 0 0 {F(secondary ? 0.65 : 0.75)} 2 0 0 0 0 0");
            // Main colored border
            cmds.Add($"RECT {F(fx)} {F(fy)} {F(fw)} {F(fh)} {F(cr)} {F(cg)} {F(cb)} 0.95 {F(thick)} {(secondary ? 1 : 0)} {F(cr)} {F(cg)} {F(cb)} {F(secondary ? 0.05 : 0.10)}");

            if (styles.Labels)
            {
                string label = kind == 2 ? "核心" : kind == 1 ? "目标" : "可选";
                double fontPx = Math.Clamp(cw * 0.095, 11, 18);
                cmds.Add($"TEXT {F(fx + fw * 0.5)} {F(fy - fontPx * 0.75)} {F(fontPx)} 1 {F(cr)} {F(cg)} {F(cb)} 1 0.06 0.07 0.09 0.88 8 3 1 {label}");
            }
        }
    }

    private static void EmitPlacement(List<string> cmds, OverlayPlacementPlan plan, BoardProjection projection, double w, double h)
    {
        foreach (var move in plan.Moves)
        {
            if (move.From == move.To) continue;
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

    private static void EmitRoutes(List<string> cmds, BoardRoutes routes, double w, double h)
    {
        double k = h / 1080.0;
        foreach (var route in routes.Routes)
        {
            bool isFly = string.Equals(route.Motion, "fly", StringComparison.OrdinalIgnoreCase);
            bool isBoss = route.Boss;
            var (cr, cg, cb) = isBoss
                ? (0.953, 0.702, 0.133)
                : isFly ? (0.780, 0.608, 1.000)
                : (1.000, 0.353, 0.306);
            double shiftX = isFly ? 0.2 : isBoss ? -0.17 : 0;
            double shiftY = isFly ? -0.26 : isBoss ? 0.2 : 0;

            foreach (var segment in route.Segments)
            {
                if (segment.Count < 2) continue;
                int n = Math.Min(segment.Count, 64);
                var sbPts = new StringBuilder();
                double firstX = 0, firstY = 0;
                for (int i = 0; i < n; i++)
                {
                    var pt = segment[i];
                    var proj = routes.Projection.Project(pt.Col + 0.5 + shiftX, pt.Row + 0.5 + shiftY);
                    double px = proj.X * w, py = proj.Y * h;
                    if (i == 0) { firstX = px; firstY = py; }
                    if (i > 0) sbPts.Append(' ');
                    sbPts.Append(F(px)).Append(' ').Append(F(py));
                }
                // Dark outline + bright core
                cmds.Add($"POLY {n} {sbPts} 0.08 0.04 0.04 0.78 {F(6.5 * k)} 0 0 0 0 0 0");
                cmds.Add($"POLY {n} {sbPts} {F(cr)} {F(cg)} {F(cb)} 0.85 {F(3.2 * k)} {(isFly ? 1 : 0)} 0 0 0 0 0");
                cmds.Add($"CIRCLE {F(firstX)} {F(firstY)} {F(5.5 * k)} 0.08 0.04 0.04 0.9 {F(2 * k)} {F(cr)} {F(cg)} {F(cb)} 0.95");
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
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
