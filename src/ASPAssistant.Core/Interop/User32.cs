// Modified for ASP Assistant Regulation on 2026-11-09; see NOTICE.
// 2026-11-12：参考上游 PR #5，增加 nemudisplay 子窗口及祖先查询。
// 2026-11-15：PC 游戏窗口改为枚举后按可见性和客户区面积选择。
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Text;

namespace ASPAssistant.Core.Interop;

public sealed record GameWindowCandidate(IntPtr Handle, bool Visible, int ClientWidth, int ClientHeight);

[StructLayout(LayoutKind.Sequential)]
public struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

public static class User32
{
#if LINUX_PORT
    public static string GetWindowClass(IntPtr hwnd)
        => NiriIpc.GetWindows().FirstOrDefault(w => (IntPtr)w.Id == hwnd)?.AppId ?? "";

    public static int DwmFlush() { Thread.Sleep(16); return 0; }

    public static uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId)
    {
        var pid = NiriIpc.GetWindows().FirstOrDefault(w => (IntPtr)w.Id == hwnd)?.Pid ?? 0;
        processId = (uint)Math.Max(0, pid);
        return processId;
    }

    public static bool IsWindowVisible(IntPtr hwnd)
        => hwnd != IntPtr.Zero && NiriIpc.GetWindows().Any(w => (IntPtr)w.Id == hwnd);

    public static IntPtr GetAncestor(IntPtr hwnd, uint flags) => hwnd;

    public static IntPtr WindowFromPoint(POINT point) => GetForegroundWindow();

    public static string GetWindowTitle(IntPtr hwnd)
        => NiriIpc.GetWindows().FirstOrDefault(w => (IntPtr)w.Id == hwnd)?.Title ?? "";

    public static IntPtr FindChildWindow(IntPtr parent, string title) => IntPtr.Zero;

    public static IntPtr FindWindow(string? lpClassName, string lpWindowName)
    {
        var match = NiriIpc.GetWindows().FirstOrDefault(w =>
            w.Title == lpWindowName || (!string.IsNullOrEmpty(lpClassName) && w.AppId.Equals(lpClassName, StringComparison.OrdinalIgnoreCase)));
        return match is null ? IntPtr.Zero : (IntPtr)match.Id;
    }

    public static bool GetWindowRect(IntPtr hWnd, out RECT lpRect)
    {
        var geom = NiriIpc.GetWindowGeometry((ulong)hWnd);
        if (geom is null) { lpRect = default; return false; }
        lpRect = geom.PhysicalRect;
        return true;
    }

    public static uint GetDpiForWindow(IntPtr hWnd)
    {
        var geom = NiriIpc.GetWindowGeometry((ulong)hWnd);
        if (geom is null) return 0;
        return (uint)Math.Max(96, Math.Round(96.0 * (geom.Output.Scale > 0 ? geom.Output.Scale : 1.0)));
    }

    public static IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd) => new(-4);
    public static IntPtr GetDpiAwarenessContextForProcess(IntPtr process) => new(-4);
    public static bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second) => first == second;
    public static string DescribeDpiAwareness(IntPtr context) => "WaylandPerOutputScale";

    public static RECT ReadWindowRect(IntPtr hwnd)
        => GetWindowRect(hwnd, out var rect) ? rect : default;

    public static double GetWindowDpiScale(IntPtr hwnd)
    {
        var geom = NiriIpc.GetWindowGeometry((ulong)hwnd);
        return geom is { Output.Scale: > 0 } ? geom.Output.Scale : 1.0;
    }

    public static void RaiseTopmost(IntPtr hwnd) { }
    public static void SetWindowBounds(IntPtr hwnd, RECT bounds) { }

    public static bool GetClientRect(IntPtr hWnd, out RECT lpRect)
    {
        var geom = NiriIpc.GetWindowGeometry((ulong)hWnd);
        if (geom is null) { lpRect = default; return false; }
        lpRect = new RECT { Left = 0, Top = 0, Right = geom.PhysicalRect.Width, Bottom = geom.PhysicalRect.Height };
        return true;
    }

    public static IntPtr GetForegroundWindow() => (IntPtr)NiriIpc.GetFocusedWindowId();

    public static bool SetForegroundWindow(IntPtr hWnd) => NiriIpc.FocusWindow((ulong)hWnd);

    public static bool IsWindow(IntPtr hWnd)
        => hWnd != IntPtr.Zero && NiriIpc.GetWindows().Any(w => (IntPtr)w.Id == hWnd);

    public static bool IsIconic(IntPtr hWnd)
    {
        var geom = NiriIpc.GetWindowGeometry((ulong)hWnd);
        return geom is null || !geom.OnActiveWorkspace;
    }

    public static bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags) => false;
    public const uint PW_RENDERFULLCONTENT = 2;

    public static int GetWindowLong(IntPtr hWnd, int nIndex) => 0;
    public static int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong) => 0;

    public static bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint)
    {
        var geom = NiriIpc.GetWindowGeometry((ulong)hWnd);
        if (geom is null) return false;
        lpPoint.X += geom.PhysicalRect.Left;
        lpPoint.Y += geom.PhysicalRect.Top;
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    public static IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam) => IntPtr.Zero;
    public static bool GetCursorPos(out POINT lpPoint) => LinuxPlatform.GetCursorPos(out lpPoint);
    public const uint WM_MOUSEWHEEL = 0x020A;
    public static bool SetCursorPos(int x, int y) => LinuxPlatform.SetCursorPos(x, y);

    public const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
    public static void SendScanKey(uint key, bool up) => LinuxPlatform.SendScanKey(key, up);
    public static void SendMouse(uint flags, int dx = 0, int dy = 0) => LinuxPlatform.SendMouse(flags, dx, dy);

    public static IntPtr FindArknightsWindow() => SelectArknightsWindow(EnumerateArknightsWindows());

    public static IReadOnlyList<GameWindowCandidate> EnumerateArknightsWindows()
    {
        var candidates = new List<(GameWindowCandidate Candidate, bool Focused, bool OnActive)>();
        foreach (var w in NiriIpc.GetWindows())
        {
            if (!NiriIpc.IsArknightsWindow(w)) continue;
            var geom = NiriIpc.GetWindowGeometry(w.Id);
            int width = geom?.PhysicalRect.Width ?? w.WindowWidth;
            int height = geom?.PhysicalRect.Height ?? w.WindowHeight;
            candidates.Add((
                new GameWindowCandidate((IntPtr)w.Id, true, Math.Max(1, width), Math.Max(1, height)),
                w.IsFocused,
                geom?.OnActiveWorkspace ?? false));
        }
        return candidates
            .OrderByDescending(x => x.Focused)
            .ThenByDescending(x => x.OnActive)
            .ThenByDescending(x => (long)x.Candidate.ClientWidth * x.Candidate.ClientHeight)
            .Select(x => x.Candidate)
            .ToList();
    }

    public static IntPtr SelectArknightsWindow(IEnumerable<GameWindowCandidate> candidates)
    {
#if LINUX_PORT
        return candidates.Where(window => window.Visible).FirstOrDefault()?.Handle ?? IntPtr.Zero;
#else
        return candidates.Where(window => window.Visible)
            .OrderByDescending(window => (long)window.ClientWidth * window.ClientHeight)
            .FirstOrDefault()?.Handle ?? IntPtr.Zero;
#endif
    }

    public static RECT? GetClientRectScreen(IntPtr hWnd)
        => NiriIpc.GetWindowGeometry((ulong)hWnd)?.PhysicalRect;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    public static bool IntersectsMonitor(RECT rect)
    {
        foreach (var o in NiriIpc.GetOutputs().Values)
        {
            int ox = (int)Math.Round(o.LogicalX * o.Scale);
            int oy = (int)Math.Round(o.LogicalY * o.Scale);
            if (rect.Left < ox + o.PhysicalWidth && rect.Right > ox && rect.Top < oy + o.PhysicalHeight && rect.Bottom > oy)
                return true;
        }
        return false;
    }

    public static bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk) => false;
    public static bool UnregisterHotKey(IntPtr hWnd, int id) => false;

    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT   = 0x0004;
    public const uint VK_OEM_2    = 0xBF;
    public const int  WM_HOTKEY   = 0x0312;
    public const int WM_ENTERSIZEMOVE = 0x0231;
    public const int WM_EXITSIZEMOVE = 0x0232;

    public static void ScrollWindow(IntPtr hwnd, int clicks) => LinuxPlatform.ScrollWindow(hwnd, clicks);

    public static RECT GetMonitorRect(int x, int y) => GetMonitorGeometry(x, y).Bounds;

    public static (RECT Bounds, RECT WorkArea) GetMonitorGeometry(int x, int y)
    {
        var outputs = NiriIpc.GetOutputs().Values;
        var match = outputs.FirstOrDefault(o =>
        {
            int ox = (int)Math.Round(o.LogicalX * o.Scale);
            int oy = (int)Math.Round(o.LogicalY * o.Scale);
            return x >= ox && x < ox + o.PhysicalWidth && y >= oy && y < oy + o.PhysicalHeight;
        }) ?? outputs.FirstOrDefault();

        if (match is null)
        {
            var fallback = new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
            return (fallback, fallback);
        }
        int left = (int)Math.Round(match.LogicalX * match.Scale);
        int top = (int)Math.Round(match.LogicalY * match.Scale);
        var bounds = new RECT { Left = left, Top = top, Right = left + match.PhysicalWidth, Bottom = top + match.PhysicalHeight };
        return (bounds, bounds);
    }
#else
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int count);

    public static string GetWindowClass(IntPtr hwnd)
    {
        var text = new StringBuilder(256);
        GetClassName(hwnd, text, text.Capacity);
        return text.ToString();
    }

    [DllImport("dwmapi.dll")]
    public static extern int DwmFlush();

    private delegate bool EnumWindowProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT point);

    public static string GetWindowTitle(IntPtr hwnd)
    {
        var text = new StringBuilder(512);
        GetWindowText(hwnd, text, text.Capacity);
        return text.ToString();
    }

    public static IntPtr FindChildWindow(IntPtr parent, string title)
    {
        var found = IntPtr.Zero;
        EnumChildWindows(parent, (hwnd, _) =>
        {
            if (GetWindowTitle(hwnd) != title) return true;
            found = hwnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDpiAwarenessContextForProcess(IntPtr process);

    [DllImport("user32.dll")]
    public static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);

    public static string DescribeDpiAwareness(IntPtr context)
    {
        foreach (var (value, name) in new[] { (-4, "PerMonitorV2"), (-3, "PerMonitor"),
                     (-2, "System"), (-1, "Unaware"), (-5, "UnawareGdiScaled") })
            if (AreDpiAwarenessContextsEqual(context, new IntPtr(value))) return name;
        return "Unknown";
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int width, int height, uint flags);

    public static RECT ReadWindowRect(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var rect))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return rect;
    }

    public static double GetWindowDpiScale(IntPtr hwnd)
    {
        var dpi = GetDpiForWindow(hwnd);
        if (dpi == 0)
            throw new InvalidOperationException("无法读取窗口 DPI");
        return dpi / 96.0;
    }

    /// <summary>重新放到置顶层最上面，不抢焦点、不动位置（v1.2.5）。</summary>
    public static void RaiseTopmost(IntPtr hwnd) =>
        SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);

    /// <summary>顶层窗口的位置和尺寸均为物理像素，不改变焦点或 Z 序。</summary>
    public static void SetWindowBounds(IntPtr hwnd, RECT bounds)
    {
        var current = ReadWindowRect(hwnd);
        if (current.Left == bounds.Left && current.Top == bounds.Top
            && current.Right == bounds.Right && current.Bottom == bounds.Bottom)
            return;
        if (!SetWindowPos(hwnd, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x0004 | 0x0010))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    public const uint PW_RENDERFULLCONTENT = 2;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    public const uint WM_MOUSEWHEEL = 0x020A;

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    // 物资调配页的点击和拖动：全屏游戏只认像真鼠标一样的相对位移输入（2026-11-16 实机）。
    public const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int Dx, Dy; public uint Data, Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint Type; public InputData Data; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputData
    {
        [FieldOffset(0)] public MOUSEINPUT Mouse;
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort Vk, Scan; public uint Flags, Time; public IntPtr Extra; }
    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);
    public static void SendScanKey(uint key, bool up)
    {
        var keyboard = new KEYBDINPUT { Scan = (ushort)MapVirtualKey(key, 0), Flags = up ? 10u : 8u };
        if (SendInput(1, [new INPUT { Type = 1, Data = new() { Keyboard = keyboard } }], Marshal.SizeOf<INPUT>()) != 1)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    public static void SendMouse(uint flags, int dx = 0, int dy = 0)
    {
        if (SendInput(1, [new INPUT { Data = new() { Mouse = new() { Dx = dx, Dy = dy, Flags = flags } } }], Marshal.SizeOf<INPUT>()) != 1)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public static IntPtr FindArknightsWindow()
    {
        return SelectArknightsWindow(EnumerateArknightsWindows());
    }

    public static IReadOnlyList<GameWindowCandidate> EnumerateArknightsWindows()
    {
        var windows = new List<GameWindowCandidate>();
        EnumWindows((hwnd, _) =>
        {
            if (GetWindowTitle(hwnd) != "明日方舟") return true;
            GetClientRect(hwnd, out var rect);
            windows.Add(new(hwnd, IsWindowVisible(hwnd), rect.Width, rect.Height));
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    public static IntPtr SelectArknightsWindow(IEnumerable<GameWindowCandidate> candidates)
    {
#if LINUX_PORT
        return candidates.Where(window => window.Visible).FirstOrDefault()?.Handle ?? IntPtr.Zero;
#else
        return candidates.Where(window => window.Visible)
            .OrderByDescending(window => (long)window.ClientWidth * window.ClientHeight)
            .FirstOrDefault()?.Handle ?? IntPtr.Zero;
#endif
    }

    public static RECT? GetClientRectScreen(IntPtr hWnd)
    {
        if (!GetClientRect(hWnd, out var clientRect))
            return null;

        var topLeft = new POINT { X = 0, Y = 0 };
        if (!ClientToScreen(hWnd, ref topLeft))
            return null;

        return new RECT
        {
            Left = topLeft.X,
            Top = topLeft.Y,
            Right = topLeft.X + clientRect.Width,
            Bottom = topLeft.Y + clientRect.Height
        };
    }

    // Monitor APIs

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFO
    {
        public int    cbSize;
        public RECT   rcMonitor;
        public RECT   rcWork;
        public uint   dwFlags;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private const uint MonitorDefaultToNearest = 2;
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);

    public static bool IntersectsMonitor(RECT rect) => MonitorFromRect(ref rect, 0) != IntPtr.Zero;

    // Global hotkey APIs

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT   = 0x0004;
    public const uint VK_OEM_2    = 0xBF;  // '/' key
    public const int  WM_HOTKEY   = 0x0312;
    public const int WM_ENTERSIZEMOVE = 0x0231;
    public const int WM_EXITSIZEMOVE = 0x0232;

    /// <summary>
    /// Sends WM_MOUSEWHEEL to the game window to scroll it vertically.
    /// Positive <paramref name="clicks"/> scrolls up; negative scrolls down.
    /// One click equals one WHEEL_DELTA (120 units).
    /// The message is targeted at the window's center so the game receives it
    /// regardless of where the real mouse cursor is.
    /// </summary>
    public static void ScrollWindow(IntPtr hwnd, int clicks)
    {
        var clientRect = GetClientRectScreen(hwnd);
        if (clientRect == null) return;

        int cx = clientRect.Value.Left + clientRect.Value.Width  / 2;
        int cy = clientRect.Value.Top  + clientRect.Value.Height / 2;

        // wParam high-word = delta; lParam = screen coords of cursor position.
        int delta = clicks * 120; // WHEEL_DELTA = 120
        IntPtr wParam = (IntPtr)((uint)(delta << 16));
        IntPtr lParam = (IntPtr)((cy << 16) | (cx & 0xFFFF));
        SendMessage(hwnd, WM_MOUSEWHEEL, wParam, lParam);
    }

    /// <summary>
    /// Returns the bounding rectangle (in physical pixels) of the monitor
    /// that contains the given physical-pixel point, or the nearest monitor
    /// if the point lies outside all monitors.
    /// </summary>
    public static RECT GetMonitorRect(int x, int y)
        => GetMonitorGeometry(x, y).Bounds;

    public static (RECT Bounds, RECT WorkArea) GetMonitorGeometry(int x, int y)
    {
        var pt      = new POINT { X = x, Y = y };
        var hMon    = MonitorFromPoint(pt, MonitorDefaultToNearest);
        var info    = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(hMon, ref info))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return (info.rcMonitor, info.rcWork);
    }
#endif
}
