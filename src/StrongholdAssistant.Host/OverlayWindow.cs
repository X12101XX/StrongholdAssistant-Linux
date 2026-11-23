// Modified for ASP Assistant Regulation on 2026-11-09; see NOTICE.
// 2026-11-11：整卡标记改用共享绘制层，按固定卡位定位。
// 2026-11-13：加敌人路线绘制层（RouteLayer），画在商店标记下面。
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using ASPAssistant.Core.Interop;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;

namespace StrongholdAssistant.Host;

public sealed class OverlayWindow : Window
{
    private readonly ShopMarkerLayer _canvas = new();
    private readonly RouteLayer _routes = new();
    private readonly PendingLayer _pending = new();
    private readonly PlacementLayer _placement = new();
    private readonly UpgradeLayer _upgrade = new();
    private System.Drawing.RectangleF? _upgradeButton;
    private int? _upgradeLevel;
    private FieldState? _field;
    private FieldOverlayGeometry? _fieldGeometry;
    private OverlayPlacementPlan? _placementPlan;
    private OverlayLayerSettings _layerSettings = new();
    private BoardRoutes? _boardRoutes;
    private RouteOverlaySettings _routeSettings = new();
    private IReadOnlyList<ShopItem> _items = [];
    private MarkerStyles _markerStyles = new();
    private RECT? _client;
    public event Action? PlacementChanged;
    public bool HasMarkers => _canvas.Children.Count > 0;
    public bool HasRoutes => _routes.HasRoutes;
    public bool HasOverlayContent => HasMarkers || HasRoutes || _pending.Children.Count > 0 || _placement.Children.Count > 0 || _upgrade.Children.Count > 0;

    public OverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        Content = new System.Windows.Controls.Grid { Children = { _routes, _placement, _pending, _canvas, _upgrade } };
        // 任一层仍有内容时保留窗口。
        _canvas.Emptied += () => { if (!HasOverlayContent) Hide(); };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            User32.SetWindowLong(hwnd, User32.GWL_EXSTYLE,
                User32.GetWindowLong(hwnd, User32.GWL_EXSTYLE)
                | User32.WS_EX_TRANSPARENT | User32.WS_EX_TOOLWINDOW | User32.WS_EX_NOACTIVATE);
        };
        SizeChanged += (_, _) =>
        {
            UpdateShopItems(_items);
            _routes.Resize(ActualWidth, ActualHeight);
            RedrawFieldLayers();
        };
    }

    public void UpdatePosition(RECT client)
    {
        _client = client;
        User32.SetWindowBounds(new WindowInteropHelper(this).EnsureHandle(), client);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        // 等 WPF 应用建议矩形后，以最近的游戏客户区恢复物理尺寸。
        Dispatcher.BeginInvoke(() =>
        {
            if (_client is { } client) UpdatePosition(client);
            UpdateShopItems(_items);
            _routes.Resize(ActualWidth, ActualHeight);
            RedrawFieldLayers();
        });
    }

    public void UpdateShopItems(IReadOnlyList<ShopItem> items, bool animate = true, MarkerStyles? styles = null)
    {
        _items = items;
        if (styles is not null) _markerStyles = styles;
        _canvas.Update(items, ActualWidth, ActualHeight, animate, _markerStyles);
    }

    public void UpdateRoutes(BoardRoutes? routes, RouteOverlaySettings settings)
    {
        _boardRoutes = routes;
        _routeSettings = settings;
        _routes.Update(routes, settings, ActualWidth, ActualHeight);
    }

    public void UpdateField(FieldState? field, FieldOverlayGeometry? geometry)
    {
        _field = field;
        _fieldGeometry = geometry;
        RedrawFieldLayers();
    }

    public void UpdatePlacement(OverlayPlacementPlan? plan)
    {
        _placementPlan = plan;
        RedrawFieldLayers();
        PlacementChanged?.Invoke();
    }

    public void UpdateLayerSettings(OverlayLayerSettings settings)
    {
        if (_layerSettings == settings) return;
        _layerSettings = settings;
        RedrawFieldLayers();
    }

    public void UpdateUpgrade(System.Drawing.RectangleF? button, int? level)
    {
        _upgradeButton = button;
        _upgradeLevel = level;
        RedrawFieldLayers();
    }

    private void RedrawFieldLayers()
    {
        // 首次布局前及客户区暂时失效时，不向绘制层传递无效尺寸。
        if (!double.IsFinite(ActualWidth) || !double.IsFinite(ActualHeight) || ActualWidth <= 0 || ActualHeight <= 0)
        {
            _pending.Children.Clear();
            _placement.Children.Clear();
            _upgrade.Children.Clear();
            return;
        }
        _pending.Update(_field, _fieldGeometry, _layerSettings.Pending, ActualWidth, ActualHeight);
        _placement.Update(_placementPlan, _field, _fieldGeometry?.Projection, _layerSettings.Placement, ActualWidth, ActualHeight);
        _upgrade.Update(_upgradeButton, _layerSettings.Upgrade ? _upgradeLevel : null, ActualWidth, ActualHeight);
        if (!HasOverlayContent) Hide();
    }

    public void UpdateRouteSettings(RouteOverlaySettings settings)
    {
        if (settings != _routeSettings) UpdateRoutes(_boardRoutes, settings);
    }

    public void SetRouteTapOnly(bool value)
    {
        _routes.SetHotkey(false);
        _routes.TapOnly = value;
    }
    public void TapRouteHotkey()
    {
        ASPAssistant.Core.AppLogger.Info("Board", "系统热键 OverlayWindow → RouteLayer.TapHotkey");
        _routes.TapHotkey();
    }

    public void SetRouteHotkey(bool down)
    {
        ASPAssistant.Core.AppLogger.Info("Board", $"快捷键 OverlayWindow → RouteLayer.SetHotkey down={down} routes={HasRoutes} visible={IsVisible} display={_routeSettings.Display}");
        _routes.SetHotkey(down);
        ASPAssistant.Core.AppLogger.Info("Board", $"快捷键 RouteLayer.SetHotkey 已返回 down={down}");
    }
}
