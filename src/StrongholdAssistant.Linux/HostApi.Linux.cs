using System.Diagnostics;
using System.Text.Json;
using ASPAssistant.Core;

namespace StrongholdAssistant.Host;

public sealed partial class HostApi
{
    public const string PlazaOrigin = "https://sa.lubeiluchen.com";
    private const string SklandBindingPath = "/api/v1/game/player/binding";

    internal Func<object?>? ReadLinuxDiagnosticCapture { get; set; }

    private static string[] ListFonts() => LinuxDialogs.ListSystemFonts();

    private string PlazaProfileDir => Path.Combine(DataDirectory, "PlazaWebKit");
    private string SklandProfileDir => Path.Combine(DataDirectory, "SklandWebKit");
    private string SklandAccountPath => Path.Combine(DataDirectory, "skland-account.json");

    // ─────────────────────────── Plaza Login / Logout ───────────────────────────

    private LinuxWebViewSession? _plazaSession;
    private Task<string?>? _plazaLoginTask;

    private void CleanupPlazaProfile()
    {
        var marker = Path.Combine(DataDirectory, "plaza-cleanup-pending");
        if (!File.Exists(marker)) return;
        try
        {
            if (Directory.Exists(PlazaProfileDir)) Directory.Delete(PlazaProfileDir, recursive: true);
            File.Delete(marker);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static string? ParsePlazaLogin(string source, string json)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)
            || uri.GetLeftPart(UriPartial.Authority) != PlazaOrigin) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                && type.GetString() == "plaza-login"
                && root.TryGetProperty("token", out var token) && token.ValueKind == JsonValueKind.String
                ? token.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private Task<string?> PlazaLoginAsync()
    {
        if (_plazaSession is { IsClosed: false } existing && _plazaLoginTask is not null)
        {
            existing.Show();
            return _plazaLoginTask;
        }

        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new LinuxWebViewSession(
            title: "登录阵容广场",
            dataDir: PlazaProfileDir,
            width: 440,
            height: 680,
            visible: true,
            initialUrl: PlazaOrigin + "/login?app=1",
            externalNewWindow: true,
            appId: "stronghold-plaza-login");

        _plazaSession = session;
        _plazaLoginTask = tcs.Task;

        session.WebMessageReceived += msg =>
        {
            var token = ParsePlazaLogin(msg.Url, msg.DataJson);
            if (token is null) return;
            try
            {
                new PlazaTokenStore(DataDirectory).Save(token);
                tcs.TrySetResult(token);
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
            session.Dispose();
        };

        session.Closed += () =>
        {
            if (_plazaSession == session)
            {
                _plazaSession = null;
                _plazaLoginTask = null;
            }
            tcs.TrySetResult(null);
            session.Dispose();
        };

        return tcs.Task;
    }

    private async Task PlazaLogoutAsync()
    {
        new PlazaTokenStore(DataDirectory).Delete();
        if (_plazaSession is { IsClosed: false } session)
        {
            await session.ClearDataAsync();
            session.Dispose();
            return;
        }
        Directory.CreateDirectory(DataDirectory);
        File.WriteAllText(Path.Combine(DataDirectory, "plaza-cleanup-pending"), "");
        CleanupPlazaProfile();
    }

    // ─────────────────────────── Skland Shared State ───────────────────────────

    private readonly object _sklandGate = new();
    private readonly List<SklandSessionHandle> _sklandSessions = [];
    private Action? _cancelSklandGuide;
    private LinuxWebViewSession? _sklandHistorySession;
    private Task<SklandRecord[]?>? _sklandHistory;
    private Task<SklandRecord[]?>? _silentSklandHistory;
    private bool _sklandLoginActive;
    private int _sklandLogoutGeneration;

    private LinuxWebViewSession? _sklandGuidesSession;
    private SklandGuideQueue? _sklandGuidesQueue;
    private JsonElement _sklandGuidesResources;
    private bool _sklandGuidesLoggedIn;
    private string? _sklandGuidesFirstPage;
    private string _sklandGuideStep = "validate";
    private readonly Dictionary<string, int> _sklandGuidePages = [];
    private SklandGuideQueue GuideQueue => _sklandGuidesQueue ??= new(CloseSklandGuides, TimeProvider.System, null);

    private sealed class SklandSessionHandle(Action cancel)
    {
        public LinuxWebViewSession? Session { get; set; }
        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action Cancel { get; } = cancel;
    }

    internal static bool? SklandHasRole(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("code", out var code) || !code.TryGetInt32(out var value) || value != 0
            || !root.TryGetProperty("data", out var data)
            || !data.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array) return null;
        foreach (var game in list.EnumerateArray())
        {
            if (!game.TryGetProperty("appCode", out var app) || app.GetString() != "arknights") continue;
            if (!game.TryGetProperty("bindingList", out var bindings) || bindings.ValueKind != JsonValueKind.Array) return null;
            return bindings.GetArrayLength() > 0;
        }
        return false;
    }

    private static bool SklandNavigationAllowed(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0) return false;
        return uri.Host == "game.skland.com";
    }

    private static bool IsSklandResponse(string url, string endpoint) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https"
        && uri.Host == "zonai.skland.com" && uri.IsDefaultPort
        && uri.AbsolutePath == "/web/v1/game/garrison/guide/" + endpoint;

    private static SklandException SklandReadError(string message, Exception ex)
    {
        AppLogger.Info("Skland", $"读取失败 {ex.GetType().Name}: {ex.Message}");
        return new SklandException($"{message}\n{ex.GetType().Name}: {ex.Message}");
    }

    private SklandAccount? ReadSklandAccount() => File.Exists(SklandAccountPath)
        ? JsonSerializer.Deserialize<SklandAccount>(File.ReadAllText(SklandAccountPath), JsonOptions) : null;

    private void RememberSklandAccount(string json)
    {
        var account = SklandParser.ParseAccount(json);
        if (account is null)
        {
            if (File.Exists(SklandAccountPath)) File.Delete(SklandAccountPath);
        }
        else
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(SklandAccountPath, JsonSerializer.Serialize(account, JsonOptions));
        }
    }

    private void FinishSklandSession(SklandSessionHandle handle)
    {
        lock (_sklandGate) _sklandSessions.Remove(handle);
        handle.Closed.TrySetResult();
    }

    private void CleanupSklandProfile()
    {
        var marker = Path.Combine(DataDirectory, "skland-cleanup-pending");
        if (!File.Exists(marker)) return;
        try
        {
            if (Directory.Exists(SklandProfileDir)) Directory.Delete(SklandProfileDir, recursive: true);
            File.Delete(marker);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ─────────────────────────── Skland Login / Logout ───────────────────────────

    private async Task<SklandAccount?> SklandLoginAsync()
    {
        lock (_sklandGate)
        {
            if (_sklandLoginActive || _sklandSessions.Count > 0 || _sklandHistory is not null || _silentSklandHistory is not null)
                throw new SklandException("森空岛窗口已经开着");
            _sklandLoginActive = true;
        }
        var logoutGeneration = _sklandLogoutGeneration;
        try
        {
            if (_sklandGuidesQueue is not null) await _sklandGuidesQueue.CloseAsync();
            var json = await ReadSklandLoginSessionAsync(visible: false);
            if (logoutGeneration != _sklandLogoutGeneration) return null;
            if (json is null) json = await ReadSklandLoginSessionAsync(visible: true);
            if (json is null || logoutGeneration != _sklandLogoutGeneration) return null;
            var account = SklandParser.ParseAccount(json);
            if (account is not null) RememberSklandAccount(json);
            return account;
        }
        finally
        {
            lock (_sklandGate) _sklandLoginActive = false;
        }
    }

    private async Task<string?> ReadSklandLoginSessionAsync(bool visible)
    {
        const string loginUrl = "https://game.skland.com/arknights/stronghold-guide/edit?header=0";
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = new SklandSessionHandle(() => completion.TrySetResult(null));
        lock (_sklandGate) _sklandSessions.Add(handle);

        using var lifetime = new CancellationTokenSource();
        CancellationTokenSource? navigation = null;
        LinuxWebViewSession? session = null;
        var closed = false;

        try
        {
            session = new LinuxWebViewSession(
                title: "森空岛 · 登录",
                dataDir: SklandProfileDir,
                width: 480,
                height: 720,
                visible: visible,
                allowedHost: "game.skland.com",
                appId: "stronghold-skland-login");
            handle.Session = session;

            session.Closed += () =>
            {
                closed = true;
                lifetime.Cancel();
                completion.TrySetResult(null);
            };

            session.HttpResponseReceived += ev =>
            {
                if (closed || completion.Task.IsCompleted) return;
                if (!Uri.TryCreate(ev.Url, UriKind.Absolute, out var uri)
                    || uri.Scheme != "https" || uri.Host != "zonai.skland.com"
                    || uri.AbsolutePath != SklandBindingPath || ev.Method != "GET") return;

                if (ev.Status == 401)
                {
                    if (!visible) completion.TrySetResult(null);
                    return;
                }
                if (ev.Status != 200 || ev.Body is null) return;

                try
                {
                    if (SklandParser.ParseAccount(ev.Body) is not null)
                        completion.TrySetResult(ev.Body);
                    else if (SklandHasRole(ev.Body) == false)
                        completion.TrySetException(new SklandException("森空岛账号还没有绑定明日方舟角色，先在森空岛 App 里绑定"));
                    else if (!visible)
                        completion.TrySetResult(null);
                }
                catch (Exception ex)
                {
                    completion.TrySetException(SklandReadError("森空岛角色的数据结构已变化，暂时无法读取。", ex));
                }
            };

            session.LoadChanged += ev =>
            {
                if (closed) return;
                if (ev.Event == "started")
                {
                    navigation?.Cancel();
                    return;
                }
                if (ev.Event == "failed")
                {
                    completion.TrySetException(new SklandException($"打不开森空岛，检查一下网络再试。\n{ev.Error}"));
                    return;
                }
                if (ev.Event != "finished") return;

                navigation?.Cancel();
                navigation?.Dispose();
                navigation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                var token = navigation.Token;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        var waitingSince = DateTime.UtcNow;
                        var pageStarted = waitingSince;
                        while (!completion.Task.IsCompleted && !token.IsCancellationRequested)
                        {
                            var result = await session.EvaluateAsync("""
                                (() => ({
                                  login: [...document.querySelectorAll('input')].some(i =>
                                    i.getClientRects().length > 0 &&
                                    (i.type === 'password' || i.type === 'tel' || /手机号/.test((i.placeholder || '') + (i.parentElement?.textContent || '')))),
                                  loading: /\d+% COMPLETE/.test(document.body?.innerText || '')
                                }))()
                                """, token);
                            using var state = JsonDocument.Parse(result);
                            var loginForm = state.RootElement.TryGetProperty("login", out var lp) && lp.GetBoolean();
                            var loading = state.RootElement.TryGetProperty("loading", out var ldp) && ldp.GetBoolean();
                            if (!visible && loginForm) { completion.TrySetResult(null); return; }
                            if (SklandParser.HistoryWaitExpired(visible, loginForm, DateTime.UtcNow, ref waitingSince, loading, pageStarted))
                            {
                                completion.TrySetException(new SklandException("打不开森空岛，检查一下网络再试。\nTimeoutException: 15 秒内未收到角色绑定。"));
                                return;
                            }
                            await Task.Delay(500, token);
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        if (!token.IsCancellationRequested)
                            completion.TrySetException(ex is SklandException ? ex : SklandReadError("打不开森空岛，检查一下网络再试。", ex));
                    }
                }, token);
            };

            session.Navigate(loginUrl);
            return await completion.Task;
        }
        finally
        {
            closed = true;
            lifetime.Cancel();
            navigation?.Dispose();
            session?.Dispose();
            FinishSklandSession(handle);
        }
    }

    private async Task SklandLogoutAsync()
    {
        _sklandLogoutGeneration++;
        if (_sklandGuidesQueue is not null) await _sklandGuidesQueue.CloseAsync();
        SklandSessionHandle[] sessions;
        lock (_sklandGate) sessions = _sklandSessions.ToArray();
        try
        {
            foreach (var s in sessions)
            {
                if (s.Session is { IsClosed: false } wv)
                    await wv.ClearDataAsync();
                s.Cancel();
            }
            await Task.WhenAll(sessions.Select(s => s.Closed.Task));
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(Path.Combine(DataDirectory, "skland-cleanup-pending"), "");
            CleanupSklandProfile();
        }
        finally
        {
            if (File.Exists(SklandAccountPath)) File.Delete(SklandAccountPath);
        }
        if (_sklandHistory is { } history) try { await history; } catch { }
        if (_silentSklandHistory is { } silent) try { await silent; } catch { }
    }

    // ─────────────────────────── Skland Single Guide Import ───────────────────────────

    private async Task<SklandGuide> SklandGuideAsync(string url)
    {
        if (_sklandLoginActive) throw new SklandException("森空岛窗口已经开着");
        url = SklandParser.NormalizeGuideUrl(url);
        _cancelSklandGuide?.Invoke();
        var completion = new TaskCompletionSource<SklandGuide>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action cancel = () => completion.TrySetException(new SklandException("已取消上一次攻略读取。"));
        _cancelSklandGuide = cancel;
        var handle = new SklandSessionHandle(cancel);
        lock (_sklandGate) _sklandSessions.Add(handle);
        var logoutGeneration = _sklandLogoutGeneration;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var reg = timeout.Token.Register(() => completion.TrySetException(new SklandException(
            "20 秒内没读到攻略内容，可能是森空岛页面改版了，或者这篇攻略已被删除。\nTimeoutException: 20 秒内未收到攻略内容。")));

        LinuxWebViewSession? session = null;
        var closed = false;
        try
        {
            session = new LinuxWebViewSession(
                title: "森空岛 · 读取攻略",
                dataDir: SklandProfileDir,
                width: 800,
                height: 600,
                visible: false,
                allowedHost: "game.skland.com",
                appId: "stronghold-skland-guide");
            handle.Session = session;

            session.LoadChanged += ev =>
            {
                if (!closed && ev.Event == "failed")
                    completion.TrySetException(new SklandException($"打不开森空岛，检查一下网络再试。\n{ev.Error}"));
            };

            session.HttpResponseReceived += ev =>
            {
                if (closed || logoutGeneration != _sklandLogoutGeneration) return;
                if (!Uri.TryCreate(ev.Url, UriKind.Absolute, out var uri)) return;
                bool isBinding = uri.Host == "zonai.skland.com" && uri.AbsolutePath == SklandBindingPath;
                if (!isBinding && !IsSklandResponse(ev.Url, "info")) return;
                if (isBinding && ev.Status == 401)
                {
                    if (File.Exists(SklandAccountPath)) File.Delete(SklandAccountPath);
                    return;
                }
                if (ev.Method != "GET" || ev.Status != 200 || ev.Body is null) return;
                try
                {
                    if (isBinding) { RememberSklandAccount(ev.Body); return; }
                    completion.TrySetResult(SklandParser.ParseGuide(ev.Body, url));
                }
                catch (ArgumentException ex) { completion.TrySetException(ex); }
                catch (Exception ex) { completion.TrySetException(SklandReadError("森空岛攻略的数据结构已变化，暂时无法读取。", ex)); }
            };

            session.Navigate(url);
            return await completion.Task;
        }
        finally
        {
            closed = true;
            session?.Dispose();
            FinishSklandSession(handle);
            if (_cancelSklandGuide == cancel) _cancelSklandGuide = null;
        }
    }

    // ─────────────────────────── Skland History Import ───────────────────────────

    private Task<SklandRecord[]?> SklandHistoryAsync(bool silent = false)
    {
        if (_sklandLoginActive) throw new SklandException("森空岛窗口已经开着");
        if (silent) return _silentSklandHistory ??= ReadSilentSklandHistoryAsync();
        _sklandHistorySession?.Show();
        return _sklandHistory ??= ReadSklandHistoryAsync();
    }

    private async Task<SklandRecord[]?> ReadSilentSklandHistoryAsync()
    {
        try { return await ReadSklandHistorySessionAsync(visible: false, silent: true); }
        finally { _silentSklandHistory = null; }
    }

    private async Task<SklandRecord[]?> ReadSklandHistoryAsync()
    {
        var logoutGeneration = _sklandLogoutGeneration;
        try
        {
            var records = await ReadSklandHistorySessionAsync(visible: false);
            return records ?? (logoutGeneration == _sklandLogoutGeneration ? await ReadSklandHistorySessionAsync(visible: true) : null);
        }
        finally { _sklandHistory = null; }
    }

    private async Task<SklandRecord[]?> ReadSklandHistorySessionAsync(bool visible, bool silent = false)
    {
        const string editUrl = "https://game.skland.com/arknights/stronghold-guide/edit?header=0";
        var completion = new TaskCompletionSource<SklandRecord[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = new SklandSessionHandle(() => completion.TrySetResult(null));
        lock (_sklandGate) _sklandSessions.Add(handle);
        var logoutGeneration = _sklandLogoutGeneration;

        using var lifetime = new CancellationTokenSource();
        using var silentTimeout = new CancellationTokenSource();
        using var silentReg = silentTimeout.Token.Register(() => completion.TrySetResult(null));
        if (silent) silentTimeout.CancelAfter(TimeSpan.FromSeconds(60));

        var retries = 0;
        var historyResponseReceived = false;
        bool? hasRole = null;
        var roleDetails = $"GET {SklandBindingPath} HTTP 未收到 code=未知";
        var generation = 0;
        var closed = false;
        CancellationTokenSource? navigation = null;
        LinuxWebViewSession? session = null;

        try
        {
            session = new LinuxWebViewSession(
                title: "森空岛 · 导入历史战绩",
                dataDir: SklandProfileDir,
                width: 480,
                height: 720,
                visible: visible,
                allowedHost: "game.skland.com",
                hint: visible ? "没登录的话先在下面登录；登录后会自动点『导入历史战绩』，也可以手动点一下。" : null,
                appId: "stronghold-skland-history");
            handle.Session = session;
            if (visible) _sklandHistorySession = session;

            session.Closed += () =>
            {
                closed = true;
                if (_sklandHistorySession == session) _sklandHistorySession = null;
                lifetime.Cancel();
                completion.TrySetResult(null);
            };

            session.HttpResponseReceived += ev =>
            {
                if (closed || ev.Method == "OPTIONS") return;
                if (!Uri.TryCreate(ev.Url, UriKind.Absolute, out var uri)) return;
                var isHistory = IsSklandResponse(ev.Url, "history-log") && ev.Method == "GET";
                var isBinding = uri.Host == "zonai.skland.com" && uri.AbsolutePath == SklandBindingPath && ev.Method == "GET";
                if (!isHistory && !isBinding) return;

                var responseGen = generation;
                if (isHistory)
                {
                    historyResponseReceived = true;
                    AppLogger.Info("Skland", $"收到 history-log 响应 HTTP {ev.Status}");
                }
                if (closed || responseGen != generation || logoutGeneration != _sklandLogoutGeneration) return;

                try
                {
                    if (isBinding)
                    {
                        roleDetails = $"GET {SklandBindingPath} HTTP {ev.Status}";
                        if (ev.Status == 401 && File.Exists(SklandAccountPath)) File.Delete(SklandAccountPath);
                        else if (ev.Body is not null) RememberSklandAccount(ev.Body);
                        hasRole = ev.Status == 200 && ev.Body is not null ? SklandHasRole(ev.Body) : null;
                        if (hasRole == false)
                            completion.TrySetException(new SklandException("森空岛账号还没有绑定明日方舟角色，先在森空岛 App 里绑定"));
                    }
                    if (!isHistory || ev.Status != 200 || ev.Body is null) return;
                    var records = SklandParser.ParseHistory(ev.Body);
                    if (records is not null || !visible) completion.TrySetResult(records);
                }
                catch (ArgumentException ex) { completion.TrySetException(ex); }
                catch (Exception ex)
                {
                    if (isHistory) completion.TrySetException(SklandReadError("历史战绩的数据结构已变化，暂时无法读取。", ex));
                }
            };

            session.LoadChanged += ev =>
            {
                if (closed) return;
                if (ev.Event == "started")
                {
                    navigation?.Cancel();
                    historyResponseReceived = false;
                    hasRole = null;
                    roleDetails = $"GET {SklandBindingPath} HTTP 未收到 code=未知";
                    generation++;
                    return;
                }
                if (ev.Event == "failed")
                {
                    completion.TrySetException(new SklandException($"打不开森空岛，检查一下网络再试。\n{ev.Error}"));
                    return;
                }
                if (ev.Event != "finished") return;

                if (Uri.TryCreate(ev.Url, UriKind.Absolute, out var loadedUri)
                    && loadedUri.AbsolutePath != "/arknights/stronghold-guide/edit")
                {
                    if (visible) session.Navigate(editUrl);
                    return;
                }

                navigation?.Cancel();
                navigation?.Dispose();
                navigation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                var token = navigation.Token;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        var waitingSince = DateTime.UtcNow;
                        var pageStarted = waitingSince;
                        var buttonLogged = false;
                        while (!completion.Task.IsCompleted && !token.IsCancellationRequested)
                        {
                            var resJson = await session.EvaluateAsync("""
                                (() => {
                                  const button = [...document.querySelectorAll('button')]
                                    .find(b => b.textContent.trim() === '导入历史战绩' && !b.disabled && b.getClientRects().length);
                                  const login = [...document.querySelectorAll('input')].some(i =>
                                    i.getClientRects().length > 0 &&
                                    (i.type === 'password' || i.type === 'tel' || /手机号/.test((i.placeholder || '') + (i.parentElement?.textContent || ''))));
                                  return { button: !!button, login, loading: /\d+% COMPLETE/.test(document.body?.innerText || '') };
                                })()
                                """, token);
                            using var st = JsonDocument.Parse(resJson);
                            var loginForm = st.RootElement.TryGetProperty("login", out var lp) && lp.GetBoolean();
                            var loading = st.RootElement.TryGetProperty("loading", out var ldp) && ldp.GetBoolean();
                            var hasBtn = st.RootElement.TryGetProperty("button", out var bp) && bp.GetBoolean();
                            if (loginForm && logoutGeneration == _sklandLogoutGeneration && File.Exists(SklandAccountPath))
                                File.Delete(SklandAccountPath);
                            if (!visible && loginForm) { completion.TrySetResult(null); return; }
                            if (SklandParser.HistoryWaitExpired(visible, loginForm, DateTime.UtcNow, ref waitingSince, loading, pageStarted))
                                break;
                            if (hasBtn && !loginForm)
                            {
                                if (!buttonLogged) { AppLogger.Info("Skland", "找到导入历史战绩按钮"); buttonLogged = true; }
                                if (hasRole != true) { await Task.Delay(500, token); continue; }
                                var clicked = await session.EvaluateAsync("""
                                    (() => {
                                      const button = [...document.querySelectorAll('button')]
                                        .find(b => b.textContent.trim() === '导入历史战绩' && !b.disabled && b.getClientRects().length);
                                      if (!button) return false;
                                      button.click();
                                      return true;
                                    })()
                                    """, token);
                                if (clicked != "true") continue;
                                AppLogger.Info("Skland", "点击导入历史战绩按钮");
                                var deadline = DateTime.UtcNow.AddSeconds(8);
                                while (DateTime.UtcNow < deadline && !completion.Task.IsCompleted)
                                {
                                    await Task.Delay(500, token);
                                    var missing = await session.EvaluateAsync("document.body?.innerText?.includes('未查询到游戏角色') || false", token);
                                    if (missing != "true" || historyResponseReceived) continue;
                                    if (retries < (visible ? 2 : 1))
                                    {
                                        retries++;
                                        if (silent) silentTimeout.CancelAfter(TimeSpan.FromSeconds(25));
                                        session.Reload();
                                    }
                                    else if (!visible) completion.TrySetResult(null);
                                    else completion.TrySetException(new SklandException("森空岛没查到你的游戏角色。\n" + roleDetails));
                                    return;
                                }
                                if (!visible) completion.TrySetResult(null);
                                return;
                            }
                            await Task.Delay(500, token);
                        }
                        if (buttonLogged)
                        {
                            if (visible) completion.TrySetException(new SklandException("森空岛没查到你的游戏角色。\n" + roleDetails));
                            else completion.TrySetResult(null);
                        }
                        else if (!completion.Task.IsCompleted)
                            completion.TrySetException(new SklandException("打不开森空岛，检查一下网络再试。\nTimeoutException: 15 秒内未找到历史战绩导入按钮。"));
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        if (!token.IsCancellationRequested)
                            completion.TrySetException(SklandReadError("打不开森空岛，检查一下网络再试。", ex));
                    }
                }, token);
            };

            session.Navigate(editUrl);
            return await completion.Task;
        }
        finally
        {
            closed = true;
            if (_sklandHistorySession == session) _sklandHistorySession = null;
            lifetime.Cancel();
            navigation?.Dispose();
            session?.Dispose();
            FinishSklandSession(handle);
        }
    }

    // ─────────────────────────── Skland Guide List ───────────────────────────

    private Task<SklandGuidePage> SklandGuidesAsync(SklandGuideQuery query)
    {
        var generation = _sklandLogoutGeneration;
        return GuideQueue.RunAsync(async () =>
        {
            var watch = Stopwatch.StartNew();
            SklandGuidePage? page = null;
            _sklandGuideStep = "validate";
            var pageNumber = string.IsNullOrEmpty(query.PageToken) ? 1 : _sklandGuidePages.GetValueOrDefault(query.PageToken, 2);
            try
            {
                if (generation != _sklandLogoutGeneration) throw new SklandException("森空岛已退出登录，请重新读取攻略列表。");
                if (_sklandLoginActive) throw new SklandException("森空岛窗口已经开着");
                if (_sklandGuidesSession is null || _sklandGuidesSession.IsClosed) await OpenSklandGuidesAsync();
                _sklandGuideStep = "build-query";
                var request = SklandParser.GuideQuery(query, _sklandGuidesResources, Catalog);
                if (!_sklandGuidesLoggedIn && (query.Section != "all" || query.Sort == "latest" || !string.IsNullOrEmpty(query.PageToken)))
                    return page = new([], null, true);

                var first = _sklandGuidesFirstPage;
                _sklandGuidesFirstPage = null;
                _sklandGuideStep = "parse-first-page";
                if (first is not null && query.Section == "all" && query.Sort != "latest"
                    && string.IsNullOrEmpty(query.PageToken) && request.Parameters.Count == 4)
                {
                    page = SklandParser.ParseGuidePage(first, query.PageToken, !_sklandGuidesLoggedIn, decoded: false);
                }
                else
                {
                    var result = await GuideScriptAsync("""
                        const request = ARGUMENT;
                        return await window.__assistantGuideService[request.method](request.parameters);
                        """, request, "query");
                    _sklandGuideStep = "parse-page";
                    page = SklandParser.ParseGuidePage(result.GetRawText(), query.PageToken, !_sklandGuidesLoggedIn);
                }
                if (pageNumber == 1) _sklandGuidePages.Clear();
                if (page.PageToken is { } token) _sklandGuidePages[token] = pageNumber + 1;
                return page;
            }
            catch (Exception ex)
            {
                CloseSklandGuides();
                if (ex is SklandException or ArgumentException) throw;
                throw new SklandException($"森空岛攻略列表读不出来，稍后再试。\n{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                AppLogger.Info("SklandGuides", $"section={query.Section} sort={query.Sort} page={pageNumber} count={page?.Items.Length ?? 0} elapsedMs={watch.ElapsedMilliseconds}");
            }
        });
    }

    private void CloseSklandGuides()
    {
        _sklandGuidesSession?.Dispose();
        _sklandGuidesSession = null;
        _sklandGuidesFirstPage = null;
        _sklandGuidePages.Clear();
    }

    private async Task OpenSklandGuidesAsync()
    {
        _sklandGuideStep = "create-session";
        var session = new LinuxWebViewSession(
            title: "森空岛 · 攻略列表",
            dataDir: SklandProfileDir,
            width: 480,
            height: 800,
            visible: false,
            allowedHost: "game.skland.com",
            appId: "stronghold-skland-guides");
        _sklandGuidesSession = session;

        var navigated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstPage = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnLoad(WebViewLoadEvent ev)
        {
            if (ev.Event == "finished") navigated.TrySetResult();
            else if (ev.Event == "failed")
                navigated.TrySetException(new SklandException($"打不开森空岛，检查一下网络再试。\n{ev.Error}"));
        }
        void OnHttp(WebViewHttpEvent ev)
        {
            if (firstPage.Task.IsCompleted || ev.Method == "OPTIONS" || !IsSklandResponse(ev.Url, "index")) return;
            try
            {
                if (ev.Status != 200 || ev.Body is null)
                    throw new SklandException($"森空岛攻略列表读不出来，稍后再试。\nHTTP {ev.Status}");
                using var doc = JsonDocument.Parse(ev.Body);
                var code = doc.RootElement.GetProperty("code").GetInt32();
                if (code != 0) throw new SklandException($"森空岛攻略列表读不出来，稍后再试。\nServiceCode: {code}");
                firstPage.TrySetResult(ev.Body);
            }
            catch (Exception ex) { firstPage.TrySetException(ex); }
        }

        session.LoadChanged += OnLoad;
        session.HttpResponseReceived += OnHttp;
        try
        {
            _sklandGuideStep = "navigate";
            session.Navigate("https://game.skland.com/arknights/stronghold-guide");
            _sklandGuideStep = "wait-navigation";
            await navigated.Task.WaitAsync(TimeSpan.FromSeconds(30));
            _sklandGuideStep = "wait-first-page";
            _sklandGuidesFirstPage = await firstPage.Task.WaitAsync(TimeSpan.FromSeconds(60));
            _sklandGuidesResources = await GuideScriptAsync(GuideInitializeScript, null, "initialize");
            _sklandGuideStep = "read-login-state";
            _sklandGuidesLoggedIn = _sklandGuidesResources.GetProperty("loggedIn").GetBoolean();
        }
        finally
        {
            session.LoadChanged -= OnLoad;
            session.HttpResponseReceived -= OnHttp;
        }
    }

    private const string GuideInitializeScript = """
        const loaded = performance.getEntriesByType('resource').map(e => e.name);
        let service;
        for (const url of [...new Set(loaded)].filter(url => /^https:\/\/assets\.skland\.com\/_static_assets\/game-tools\/dist-[^/]+\.js$/.test(url))) {
            const module = await import(url);
            if (typeof module.t?.getGuideList === 'function') { service = module.t; break; }
        }
        if (!service) throw new Error('GuideServiceUnavailable');
        const resources = service.staticInfoCache.get(service.getCacheKey());
        if (!resources || !service.currentSeasonId || service.currentSeasonId === 'current') throw new Error('GuideResourceUnavailable');
        const entryUrl = loaded.find(url => /^https:\/\/assets\.skland\.com\/_static_assets\/game-tools\/entry_index-[^/]+\.js$/.test(url));
        if (!entryUrl) throw new Error('GuideAuthUnavailable');
        const auth = (await import(entryUrl)).n;
        await auth.ready();
        window.__assistantGuideService = service;
        return { seasonId: service.currentSeasonId, factions: Object.values(resources.factionMap),
                 strategyIds: resources.strategyIds, loggedIn: !!auth.isLogin };
        """;

    private async Task<JsonElement> GuideScriptAsync(string body, object? argument, string operation)
    {
        var session = _sklandGuidesSession!;
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = Guid.NewGuid().ToString("N");

        void OnMsg(WebViewMessageEvent msg)
        {
            if (!SklandNavigationAllowed(msg.Url)) return;
            try
            {
                using var doc = JsonDocument.Parse(msg.DataJson);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("assistantGuideRequest", out var key) || key.GetString() != id) return;
                if (root.TryGetProperty("error", out var error))
                    completion.TrySetException(new SklandException("森空岛攻略列表读不出来，稍后再试。\nServiceCode: " + error.GetInt64()));
                else
                    completion.TrySetResult(root.GetProperty("result").Clone());
            }
            catch { }
        }

        session.WebMessageReceived += OnMsg;
        try
        {
            var script = "(async (ARGUMENT) => { try { const result = await (async () => {" + body +
                "})(); window.chrome.webview.postMessage({assistantGuideRequest:'" + id + "',result}); } catch(e) {" +
                "window.chrome.webview.postMessage({assistantGuideRequest:'" + id + "',error:Number.isSafeInteger(e?.code)?e.code:-1}); } })(" +
                JsonSerializer.Serialize(argument, JsonOptions) + ");";
            _sklandGuideStep = operation + "-execute-script";
            _ = session.EvaluateAsync(script);
            _sklandGuideStep = operation + "-wait-result";
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            session.WebMessageReceived -= OnMsg;
        }
    }
}
