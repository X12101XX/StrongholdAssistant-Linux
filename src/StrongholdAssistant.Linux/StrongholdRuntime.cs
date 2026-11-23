using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ASPAssistant.Core;
using ASPAssistant.Core.GameModes.GarrisonProtocol;
using ASPAssistant.Core.Interop;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;

namespace StrongholdAssistant.Host;

internal sealed record LinuxLaunchOptions(
    bool Headless = false,
    bool EnableOverlay = true,
    bool ConnectGame = true,
    bool DevTools = false,
    int Port = 0,
    string? Profile = null,
    string? TriggerAction = null);

/// <summary>
/// Linux / Wayland / niri runtime coordinator replacing Windows MainWindow.
/// Wires HostApi, WindowTrackerService, ScreenCaptureService, MaaOcrEngine, OcrScannerService,
/// UiBridgeServer (HTTP + WebSocket), LinuxWebViewController (WebKitGTK), and LinuxOverlayController (gtk4-layer-shell).
/// </summary>
internal sealed class StrongholdRuntime : IDisposable
{
    private readonly LinuxLaunchOptions _options;
    private readonly ScreenCaptureService _capture = new();
    private readonly WindowTrackerService _tracker;
    private readonly LinuxOverlayController _overlay;
    private readonly LinuxWebViewController _webView = new();
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();

    private HostApi _api = null!;
    private UiBridgeServer? _bridge;
    private MaaOcrEngine? _ocr;
    private MaaBanIconMatcher? _iconMatcher;
    private OcrScannerService? _scanner;
    private Socket? _triggerSocket;
    private string? _triggerSocketPath;

    private IReadOnlyList<ShopItem> _shopItems = [];
    private System.Drawing.RectangleF? _upgradeButton;
    private byte[]? _automaticOpeningScreenshot;
    private RECT? _lastGameRect;
    private bool _closing;
    private bool _uiReady;

    public string? UiUrl => _bridge?.Url;

    public StrongholdRuntime(LinuxLaunchOptions options)
    {
        _options = options;
        _overlay = new LinuxOverlayController(enabled: options.EnableOverlay);
        _tracker = new WindowTrackerService(
            findWindow: _capture.Connection.FindGameWindow,
            getViewport: _capture.Connection.GetViewport);
    }

    public static string GetTriggerSocketPath(string? profile)
    {
        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrEmpty(runtimeDir)) runtimeDir = Path.GetTempPath();
        var suffix = string.IsNullOrEmpty(profile) ? "" : $"-{profile}";
        return Path.Combine(runtimeDir, $"stronghold-assistant{suffix}.sock");
    }

    public async Task RunAsync(CancellationToken externalCancel)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(externalCancel, _shutdown.Token);

        var dataDir = ResolveDataDirectory();
        var uiDir = ResolveUiDirectory();
        var userDir = AssistantProfile.Current.DirectoryPath;
        Directory.CreateDirectory(userDir);
        AppLogger.Initialize(Path.Combine(userDir, "logs"));
        CrashGuard.Initialize(userDir);

        AppLogger.Info("LinuxHost", $"Starting StrongholdAssistant Linux (Wayland={Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")}, Niri={NiriIpc.IsAvailable})");
        AppLogger.Info("LinuxHost", $"DataDir={dataDir}, UiDir={uiDir}, UserDir={userDir}");

        LinuxPlatform.EnsureNativeInitialized();

        await HostApi.CheckStartupDataAsync(dataDir, afterUpdate: false);
        _api = await HostApi.CreateAsync(dataDir, userDir);
        _api.ReadLinuxDiagnosticCapture = () => new
        {
            method = _capture.Connection.WindowCapture.Method,
            lastFallbackReason = _capture.Connection.WindowCapture.LastFallbackReason,
            niriAvailable = NiriIpc.IsAvailable
        };
        _ = _api.DownloadIncomeTableAsync();

        Directory.CreateDirectory(_api.ControlDirectory);
        _bridge = new UiBridgeServer(uiDir, _api.ControlDirectory, _options.Port, HandleBridgeMessageAsync);

        WireApiEvents();
        StartTriggerSocketServer(linked.Token);

        Console.WriteLine($"StrongholdAssistant UI: {_bridge.Url}");
        if (!_options.Headless)
        {
            _webView.Closed += () => _shutdown.Cancel();
            if (!_webView.Start(_bridge.Url, userDir, width: 410, height: 920, devTools: _options.DevTools))
            {
                Console.WriteLine($"WebKitGTK helper unavailable; open {_bridge.Url} in your browser.");
            }
        }

        if (_options.ConnectGame)
        {
            InitializeGameTracking();
            _tracker.Start();
            await InitializeGameAsync(dataDir);
            _api.ConfigureLocalApi();
            _scanner?.Start();
        }
        else
        {
            _api.ConfigureLocalApi();
        }

        try { await Task.Delay(Timeout.Infinite, linked.Token); }
        catch (OperationCanceledException) { }

        _closing = true;
        CrashGuard.NormalExit();
    }

    private void WireApiEvents()
    {
        _api.Changed += (name, value) =>
        {
            if (value is ScanStatus status && _scanner is not null)
            {
                _scanner.SetBanScreenDetectionPaused(status.Busy || _closing);
                if (status.Ok == true) _scanner.MarkInformationRecorded();
                _overlay.SetCaptureHidden(status.Busy);
            }
            _bridge?.BroadcastEvent(name, value);
        };
    }

    private void InitializeGameTracking()
    {
        _capture.Connection.Configure(
            _api.Settings.GameSource,
            _api.Settings.EmulatorSerial,
            _api.Settings.AdbAddress,
            _api.Settings.HideCaptureBorder);

        _capture.Connection.Changed += _ =>
        {
            _scanner?.InvalidateShop();
            PublishConnection();
        };

        _tracker.GameWindowMoved += rect => UpdateGame(rect);
        _tracker.GameWindowPolled += rect => UpdateGame(rect);
        _tracker.GameWindowLost += () =>
        {
            _scanner?.InvalidateShop();
            _lastGameRect = null;
            _shopItems = [];
            _overlay.UpdateShopItems([]);
            _overlay.UpdateGeometry(null);
            _overlay.Hide();
            PublishConnection();
        };
        _tracker.GameWindowMinimizedChanged += minimized =>
        {
            if (minimized) _overlay.Hide();
            else if (_tracker.CurrentGameRect is { } rect) UpdateGame(rect);
        };
    }

    private async Task InitializeGameAsync(string dataDir)
    {
        _api.InitializeBoardRoutes(dataDir);
        try
        {
            var sw = Stopwatch.StartNew();
            _ocr = await Task.Run(() => new MaaOcrEngine(dataDir));
            await _ocr.WarmUpAsync();
            AppLogger.Info("OCR", $"MaaOcrEngine initialized and warmed up in {sw.ElapsedMilliseconds} ms");

            var matcherSw = Stopwatch.StartNew();
            _iconMatcher = await Task.Run(() => new MaaBanIconMatcher(dataDir));
            AppLogger.Info("MaaBanIconMatcher", $"初始化完成，耗时 {matcherSw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            _api.RecognitionError = $"识别组件初始化失败：{ex.GetBaseException().Message}";
            AppLogger.Error("OCR", _api.RecognitionError, ex);
            PublishRecognitionError();
            return;
        }

        var scanOcr = new DiagnosticOcrEngine(_ocr);
        _scanner = new OcrScannerService(
            _capture,
            new GarrisonOcrStrategy(),
            _api.CreateShopReader(scanOcr),
            scanOcr,
            new ASPAssistant.Core.GameState.GameState(),
            () => _api.TrackedNames.Concat(_api.Settings.MarkerStyles.Pickable.Show ? _api.SecondaryNames : []).Distinct().ToArray(),
            iconMatcher: _iconMatcher,
            intervalMs: 800);

        _scanner.RecognitionFailed += (shot, reason, scale, steps) =>
        {
            var message = _api.ReportRecognitionFailure(shot, reason, scale, steps);
            _bridge?.BroadcastEvent("scan", new ScanStatus(false, message, false));
        };

        _scanner.GameStateUpdated += state =>
        {
            var items = state.ShopItems.ToArray();
            var generation = _scanner.ShopGeneration;
            if (generation != _scanner.ShopGeneration) return;
            _shopItems = items;
            RefreshOverlay();
        };

        _scanner.SetShopScanSpeed(_api.Settings.ShopScanSpeed);
        _overlay.PlacementChanged += RefreshOverlay;
        _api.ShopChanged += () =>
        {
            _scanner.SetShopScanSpeed(_api.Settings.ShopScanSpeed);
            _overlay.UpdateRouteSettings(_api.Settings.Routes);
            RefreshOverlay();
        };

        _scanner.EconomyRead += reading => _api.ObserveEconomy(reading);
        _scanner.ThemeDetected += reading => _api.ObserveTheme(reading);

        _api.InitializeField(dataDir, scanOcr);
        _api.InitializeMatchReading(dataDir, scanOcr);
        _scanner.ObserveOtherTurn = _api.ObserveOtherTurn;
        _scanner.ObserveAdaptFrameAsync = _api.ObserveAdaptFrameAsync;
        _scanner.HudObscured = () => _api.CoverMatchHud();
        _scanner.ObserveMatchFrameAsync = _api.ObserveMatchFrameAsync;
        _scanner.ObserveEnemyFrameAsync = _api.ObserveEnemyFrameAsync;
        _scanner.EnemyPanelSeen += _api.CountEnemyPanel;
        _scanner.ObserveFieldAsync = _api.ObserveFieldAsync;

        void ConfigureCollection()
        {
            _scanner.CollectionFrame = _api.Settings.Collect ? _api.ObserveCollectionFrame : null;
            _scanner.CollectionEnemyPanel = _api.Settings.Collect ? _api.CollectEnemyPanel : null;
            _scanner.CollectionScanCompleted = _api.Settings.Collect ? _api.CompleteCollectionScan : null;
            _capture.NativeFrame = _api.Settings.Collect ? _api.ObserveCollectionNative : null;
            _capture.NativeFrameRequested = () => _api.NeedsCollectionRaw;
        }
        _api.CollectionEnabledChanged += ConfigureCollection;
        ConfigureCollection();

        _api.CollectionEnvironment = () =>
        {
            var state = _capture.Connection.State;
            var rect = state.Window == 0 ? null : User32.GetClientRectScreen(state.Window);
            return new
            {
                source = state.Source,
                capture = _capture.Connection.WindowCapture.Method,
                rawWidth = state.Width > 0 ? state.Width : rect?.Width,
                rawHeight = state.Height > 0 ? state.Height : rect?.Height,
                windowWidth = rect?.Width,
                windowHeight = rect?.Height,
                dpi = state.Window == 0 ? 96 : User32.GetWindowDpiScale(state.Window) * 96
            };
        };

        _api.InitializeControl(_capture, _scanner, _ocr, dataDir);
        _api.ReadCurrentField = () => _scanner.ReadCurrentFieldAsync(CaptureInformationScreenAsync, _api.RecognizeFieldFrameAsync);
        _scanner.BoardRoutesRequested = () => _api.BoardRoutesEnabled;
        _scanner.BoardFrameReady += (frame, reading) =>
        {
            _upgradeButton = frame is not null && _scanner.BoardMainScreenVisible
                && _scanner.BoardShopVisible && !_scanner.BoardDetailVisible
                && reading is { IsRest: true } ? reading.UpgradeButton : null;
            RefreshOverlay();
            if (frame is null || !_scanner.BoardMainScreenVisible || reading is { IsRest: false })
                _api.HideFieldOverlay();
            _api.ObserveBoardFrame(frame, reading, _scanner.BoardShopVisible, _scanner.BoardMainScreenVisible);
        };

        _api.FieldOverlayChanged += (field, geometry) =>
        {
            _overlay.UpdateField(field, geometry);
            RefreshOverlay();
        };
        _api.PlacementOverlayChanged += plan =>
        {
            _overlay.UpdatePlacement(plan);
            RefreshOverlay();
        };
        _overlay.UpdatePlacement(HostApi.ToOverlayPlacement(_api.Placement));
        _api.BoardRoutesChanged += routes =>
        {
            _overlay.UpdateRoutes(routes, _api.Settings.Routes);
            RefreshOverlay();
        };
        _api.RouteHotkeyChanged += down =>
        {
            if (down) _overlay.ToggleRoutesByHotkey();
        };

        await _api.InitializeRecognitionAsync(dataDir, _ocr, async () =>
        {
            await _scanner.WaitForIdleAsync();
            if (_automaticOpeningScreenshot is { } opening)
            {
                _automaticOpeningScreenshot = null;
                return opening;
            }
            return await CaptureInformationScreenAsync();
        });

        _api.InitializeRoster(dataDir, _ocr, _capture.Connection, async paused =>
        {
            _scanner.SetBanScreenDetectionPaused(paused || _closing);
            if (paused) await _scanner.WaitForIdleAsync();
            _overlay.SetCaptureHidden(paused);
        });

        _scanner.AwaitingDetail = () => _api.Match.AwaitingDetail;
        _scanner.BanScreenDetected += solo => _ = RecordAutomaticallyAsync(solo);
        _scanner.DetailScreenDetected += () => _ = RecordDetailAutomaticallyAsync();
    }

    private async Task RecordAutomaticallyAsync(bool solo)
    {
        _automaticOpeningScreenshot = _scanner!.OpeningScreenshot;
        try { await _api.HandleOpeningAsync(solo); }
        catch (RecognitionException) { }
        finally { _automaticOpeningScreenshot = null; }
    }

    private async Task RecordDetailAutomaticallyAsync()
    {
        try { await _api.HandleDetailAsync(); }
        catch (RecognitionException) { }
    }

    private async Task<byte[]?> CaptureInformationScreenAsync()
    {
        if (!_options.ConnectGame || _closing) return null;
        if (_capture.Connection.IsAdb)
            return await Task.Run(() => _capture.CaptureScreen());

        _overlay.SetCaptureHidden(true);
        try
        {
            await Task.Delay(30);
            var frame = await Task.Run(() => _capture.CaptureWindowScreen());
            if (frame is not null) return frame;

            // If Arknights isn't currently on the active output view in niri, focus it briefly and capture.
            var game = _tracker.CurrentGameWindowHandle;
            if (game != IntPtr.Zero && User32.IsWindow(game))
            {
                User32.SetForegroundWindow(game);
                await Task.Delay(180);
                return await Task.Run(() => _capture.CaptureWindowScreen());
            }
            return null;
        }
        finally
        {
            _overlay.SetCaptureHidden(false);
        }
    }

    private void UpdateGame(RECT game)
    {
        if (_closing) return;
        if (!_lastGameRect.Equals(game)) _scanner?.InvalidateShop();
        _lastGameRect = game;

        var hwnd = _tracker.CurrentGameWindowHandle;
        var geom = hwnd == IntPtr.Zero ? null : NiriIpc.GetWindowGeometry((ulong)hwnd);
        _overlay.UpdateGeometry(geom);
        RefreshOverlay();
        PublishConnection();
    }

    private sealed record BridgeRequest(int Id, string Method, JsonElement[] Params);

    private void RefreshOverlay()
    {
        if (_closing) return;
        _api.UpdateShopTracking(_shopItems);
        var items = _api.Settings.ShopMarkers ? _shopItems : [];
        _overlay.UpdateLayerSettings(_api.Settings.OverlayLayers);
        _overlay.UpdateShopItems(items, _api.Settings.MarkerStyles);
        _overlay.UpdateUpgrade(_upgradeButton, _api.UpgradeTarget);
    }

    private void PublishConnection()
    {
        if (_closing || !_options.ConnectGame) return;
        var state = _capture.Connection.State;
        var viewport = _capture.Connection.GetViewport(state.Window);
        if (state.Source == "pc" && User32.IsIconic(state.Window)) _api.StopControl("lost");
        bool connected = state.Source == "pc" ? viewport is not null
            : _capture.Connection.IsAdb && state.Width > 0 && state.Problem is null;
        string? resolution = !connected ? null : _capture.Connection.IsAdb
            ? $"{state.Width}×{state.Height}" : $"{viewport!.Value.Width}×{viewport.Value.Height}";
        _api.UpdateConnection(new Connection(
            connected ? "connected" : "searching",
            resolution,
            connected ? _tracker.IsFullscreen : null,
            false,
            connected ? state.Source : null,
            state.Problem,
            connected ? "right" : null,
            false));
    }

    private void PublishRecognitionError()
    {
        if (_uiReady && _api.RecognitionError is { } message)
            _bridge?.BroadcastEvent("scan", new ScanStatus(false, message, false));
    }

    private async Task<string> HandleBridgeMessageAsync(string rawJson)
    {
        int? id = null;
        var locked = false;
        try
        {
            var request = JsonSerializer.Deserialize<BridgeRequest>(rawJson, HostApi.JsonOptions)!;
            id = request.Id;
            if (request.Method is not ("rescan" or "startRosterScan" or "stopRosterScan" or "plazaLogin" or "sklandGuide" or "sklandHistory" or "sklandLogin"))
            {
                await _requests.WaitAsync();
                locked = true;
            }

            object? result;
            if (request.Method == "windowCommand")
            {
                var cmd = request.Params[0].GetString();
                HandleWindowCommand(cmd);
                result = null;
            }
            else if (request.Method == "resizeWindow")
            {
                result = null;
            }
            else if (request.Method == "setRunAsAdmin")
            {
                _api.SaveRunAsAdmin(false);
                result = null;
            }
            else if (request.Method == "setHotkey")
            {
                _api.SaveHotkey(request.Params[0].GetString() ?? "");
                result = null;
            }
            else if (request.Method == "collapseUp")
            {
                PublishConnection();
                result = null;
            }
            else
            {
                result = await _api.InvokeAsync(request.Method, request.Params);
                if (request.Method == "saveSettings")
                {
                    _capture.Connection.Configure(_api.Settings.GameSource, _api.Settings.EmulatorSerial, _api.Settings.AdbAddress, _api.Settings.HideCaptureBorder);
                    _overlay.UpdateRouteSettings(_api.Settings.Routes);
                    RefreshOverlay();
                }
            }

            if (request.Method == "windowCommand" && request.Params[0].GetString() == "ready")
            {
                _uiReady = true;
                PublishConnection();
                PublishRecognitionError();
            }

            var json = JsonSerializer.Serialize(new { id = request.Id, result }, HostApi.JsonOptions);
            // Rewrite control.local URLs to local HTTP server path
            if (json.Contains("https://control.local/", StringComparison.Ordinal))
                json = json.Replace("https://control.local/", "/_control/", StringComparison.Ordinal);
            return json;
        }
        catch (Exception ex)
        {
            var detail = ex.Message;
            AppLogger.Warn("Bridge", $"Method failed: {ex.GetType().Name}: {detail}");
            return id is { } reqId
                ? JsonSerializer.Serialize(new { id = reqId, error = detail }, HostApi.JsonOptions)
                : "";
        }
        finally
        {
            if (locked) _requests.Release();
        }
    }

    private void HandleWindowCommand(string? cmd)
    {
        switch (cmd)
        {
            case "minimize": _webView.Minimize(); break;
            case "maximize": _webView.Maximize(); break;
            case "restore": _webView.Restore(); break;
            case "close": _shutdown.Cancel(); break;
            case "collapse":
                _webView.Resize(76, 480);
                PublishConnection();
                break;
            case "expand":
                _webView.Resize(410, 920);
                PublishConnection();
                break;
            case "workbench":
                _webView.Resize(1100, 820);
                PublishConnection();
                break;
            case "sidebar":
                _webView.Resize(410, 920);
                PublishConnection();
                break;
            case "ready":
                _uiReady = true;
                break;
        }
    }

    private void StartTriggerSocketServer(CancellationToken ct)
    {
        try
        {
            _triggerSocketPath = GetTriggerSocketPath(AssistantProfile.Current.Name);
            if (File.Exists(_triggerSocketPath)) File.Delete(_triggerSocketPath);
            _triggerSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _triggerSocket.Bind(new UnixDomainSocketEndPoint(_triggerSocketPath));
            _triggerSocket.Listen(8);
            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        using var client = await _triggerSocket.AcceptAsync(ct);
                        using var stream = new NetworkStream(client, false);
                        using var reader = new StreamReader(stream, Encoding.UTF8);
                        var action = (await reader.ReadLineAsync(ct))?.Trim().ToLowerInvariant();
                        HandleTriggerAction(action);
                    }
                    catch when (ct.IsCancellationRequested) { break; }
                    catch { }
                }
            }, ct);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("TriggerSocket", $"Failed to bind trigger socket: {ex.Message}");
        }
    }

    private void HandleTriggerAction(string? action)
    {
        AppLogger.Info("Trigger", $"Received trigger action: {action}");
        switch (action)
        {
            case "toggle":
                _webView.ToggleVisibility();
                break;
            case "rescan":
                _ = Task.Run(async () => { try { await _api.RecordInformationAsync(); } catch { } });
                break;
            case "route":
                _overlay.ToggleRoutesByHotkey();
                break;
            case "field":
                _ = Task.Run(async () => { try { await _api.RecognizeFieldAsync(); } catch { } });
                break;
            case "stop":
                _api.StopControl("hotkey");
                break;
        }
    }

    private static string ResolveDataDirectory()
    {
        string[] candidates =
        [
            Path.Combine(PackageLayout.Root, "data"),
            Path.Combine(AppContext.BaseDirectory, "data"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data"))
        ];
        return candidates.FirstOrDefault(Directory.Exists) ?? candidates[0];
    }

    private static string ResolveUiDirectory()
    {
        string[] candidates =
        [
            Path.Combine(PackageLayout.Root, "ui"),
            Path.Combine(AppContext.BaseDirectory, "ui"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ui", "dist"))
        ];
        return candidates.FirstOrDefault(d => File.Exists(Path.Combine(d, "index.html"))) ?? candidates[0];
    }

    public void Dispose()
    {
        _closing = true;
        _shutdown.Cancel();
        try { _scanner?.Dispose(); } catch { }
        try { _tracker.Dispose(); } catch { }
        try { _capture.Connection.Dispose(); } catch { }
        try { _ocr?.Dispose(); } catch { }
        try { _iconMatcher?.Dispose(); } catch { }
        try { _overlay.Dispose(); } catch { }
        try { _webView.Dispose(); } catch { }
        try { _bridge?.Dispose(); } catch { }
        try { _triggerSocket?.Dispose(); } catch { }
        if (_triggerSocketPath is not null && File.Exists(_triggerSocketPath))
            try { File.Delete(_triggerSocketPath); } catch { }
        _shutdown.Dispose();
    }
}
