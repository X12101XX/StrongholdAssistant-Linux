#if LINUX_PORT
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ASPAssistant.Core.Interop;

/// <summary>
/// Linux / Wayland / niri native backend (screencopy, virtual pointer/keyboard, clipboard, grim fallback).
/// </summary>
public static class LinuxPlatform
{
    private const string NativeLib = "stronghold_wayland";
    private const uint BtnLeft = 0x110;
    private static readonly object CaptureBufferLock = new();
    private static byte[] _captureScratch = [];
    private static bool _nativeInitAttempted;
    private static bool _nativeAvailable;
    private static User32.POINT _lastVirtualCursor;
    private static string _lastOutputName = "";

    static LinuxPlatform()
    {
        NativeLibrary.SetDllImportResolver(typeof(LinuxPlatform).Assembly, (name, assembly, searchPath) =>
        {
            if (name != NativeLib) return IntPtr.Zero;
            string[] candidates =
            [
                Path.Combine(AppContext.BaseDirectory, "libstronghold_wayland.so"),
                Path.Combine(PackageLayout.Root, "libstronghold_wayland.so"),
                Path.Combine(PackageLayout.Root, "native", "libstronghold_wayland.so"),
                Path.Combine(AppContext.BaseDirectory, "runtimes", "linux-x64", "native", "libstronghold_wayland.so"),
                "libstronghold_wayland.so"
            ];
            foreach (var c in candidates)
            {
                if (NativeLibrary.TryLoad(c, out var handle))
                    return handle;
            }
            return IntPtr.Zero;
        });
    }

    [DllImport(NativeLib)] private static extern int sh_wl_init();
    [DllImport(NativeLib)] private static extern int sh_wl_has_screencopy();
    [DllImport(NativeLib)] private static extern int sh_wl_has_virtual_pointer();
    [DllImport(NativeLib)] private static extern int sh_wl_has_virtual_keyboard();
    [DllImport(NativeLib)] private static extern int sh_wl_capture_region(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? outputName,
        int x, int y, int w, int h,
        IntPtr outBgra, out int outW, out int outH, int maxBytes);
    [DllImport(NativeLib)] private static extern int sh_wl_pointer_move_abs(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? outputName,
        uint x, uint y, uint xExtent, uint yExtent);
    [DllImport(NativeLib)] private static extern int sh_wl_pointer_move_rel(double dx, double dy);
    [DllImport(NativeLib)] private static extern int sh_wl_pointer_button(uint linuxBtn, int pressed);
    [DllImport(NativeLib)] private static extern int sh_wl_pointer_scroll(double valueY);
    [DllImport(NativeLib)] private static extern int sh_wl_keyboard_key(uint evdevKeycode, int pressed);

    public static bool EnsureNativeInitialized()
    {
        if (_nativeInitAttempted) return _nativeAvailable;
        _nativeInitAttempted = true;
        try
        {
            _nativeAvailable = sh_wl_init() == 0;
            if (_nativeAvailable)
            {
                AppLogger.Info("LinuxPlatform",
                    $"Wayland native ready: screencopy={sh_wl_has_screencopy()}, vptr={sh_wl_has_virtual_pointer()}, vkbd={sh_wl_has_virtual_keyboard()}");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("LinuxPlatform", $"libstronghold_wayland.so unavailable (will use grim fallback): {ex.Message}");
            _nativeAvailable = false;
        }
        return _nativeAvailable;
    }

    public static Bitmap? CaptureWindow(nint windowHandle, out string method)
    {
        method = "wayland-screencopy";
        if (windowHandle == 0) return null;
        var geom = NiriIpc.GetWindowGeometry((ulong)windowHandle);
        if (geom is null || !geom.OnActiveWorkspace) return null;

        _lastOutputName = geom.Output.Name;

        // Primary: native wlr-screencopy SHM (~9ms, zero cursor, zero clipboard)
        if (EnsureNativeInitialized() && sh_wl_has_screencopy() != 0)
        {
            var bmp = CaptureViaWaylandShm(geom.Output.Name, geom.LocalX, geom.LocalY, geom.LocalWidth, geom.LocalHeight,
                geom.PhysicalRect.Width, geom.PhysicalRect.Height);
            if (bmp is not null)
            {
                method = "wayland-screencopy";
                return bmp;
            }
        }

        // Fallback: grim -o <output> -g "<x>,<y> <w>x<h>" -t ppm -
        method = "grim";
        return CaptureViaGrim(geom.Output.Name, geom.LocalX, geom.LocalY, geom.LocalWidth, geom.LocalHeight, geom.Output.LogicalX, geom.Output.LogicalY);
    }

    public static Bitmap? CaptureScreenRegion(int physX, int physY, int physW, int physH)
    {
        if (physW <= 0 || physH <= 0) return null;
        var outputs = NiriIpc.GetOutputs();
        // Prefer exact match on output physical bounds; disambiguate scaled outputs by checking logical coordinates too.
        NiriOutputInfo? target = null;
        foreach (var o in outputs.Values)
        {
            int ox = (int)Math.Round(o.LogicalX * o.Scale);
            int oy = (int)Math.Round(o.LogicalY * o.Scale);
            if (physX >= ox && physX < ox + o.PhysicalWidth && physY >= oy && physY < oy + o.PhysicalHeight)
            {
                if (target is null || (physX == ox && physY == oy))
                    target = o;
            }
        }
        target ??= outputs.Values.FirstOrDefault();

        if (target is null) return null;
        double scale = target.Scale > 0 ? target.Scale : 1.0;
        int outPhysX = (int)Math.Round(target.LogicalX * scale);
        int outPhysY = (int)Math.Round(target.LogicalY * scale);
        int lx = Math.Clamp((int)Math.Round((physX - outPhysX) / scale), 0, Math.Max(0, target.LogicalWidth - 1));
        int ly = Math.Clamp((int)Math.Round((physY - outPhysY) / scale), 0, Math.Max(0, target.LogicalHeight - 1));
        int lw = Math.Clamp((int)Math.Round(physW / scale), 1, Math.Max(1, target.LogicalWidth - lx));
        int lh = Math.Clamp((int)Math.Round(physH / scale), 1, Math.Max(1, target.LogicalHeight - ly));
        int estPhysW = (int)Math.Round(lw * scale);
        int estPhysH = (int)Math.Round(lh * scale);

        if (EnsureNativeInitialized() && sh_wl_has_screencopy() != 0)
        {
            var bmp = CaptureViaWaylandShm(target.Name, lx, ly, lw, lh, estPhysW, estPhysH);
            if (bmp is not null) return bmp;
        }
        return CaptureViaGrim(target.Name, lx, ly, lw, lh, target.LogicalX, target.LogicalY);
    }

    private static unsafe Bitmap? CaptureViaWaylandShm(string outputName, int lx, int ly, int lw, int lh, int estPhysW, int estPhysH)
    {
        lock (CaptureBufferLock)
        {
            // Fast path: write directly into the target SKBitmap pixels (zero intermediate copy, zero Erase memset)
            if (estPhysW > 0 && estPhysH > 0)
            {
                var directBmp = Bitmap.CreateUninitialized(estPhysW, estPhysH, PixelFormat.Format32bppArgb);
                var directData = directBmp.LockBits(new Rectangle(0, 0, estPhysW, estPhysH), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                int directStride = directData.Stride;
                int directMaxBytes = checked(estPhysW * estPhysH * 4);
                int rcDirect = -1;
                int dow = 0, doh = 0;
                try
                {
                    if (directStride == estPhysW * 4)
                    {
                        rcDirect = sh_wl_capture_region(outputName, lx, ly, lw, lh, directData.Scan0, out dow, out doh, directMaxBytes);
                    }
                }
                finally { directBmp.UnlockBits(directData); }

                if (rcDirect == 0 && dow == estPhysW && doh == estPhysH)
                    return directBmp;

                directBmp.Dispose();
                if (rcDirect != -5 && (dow <= 0 || doh <= 0)) return null;
                if (dow > 0 && doh > 0)
                {
                    estPhysW = dow;
                    estPhysH = doh;
                }
            }

            // Fallback when actual output dimensions differed from estimate
            int minBytes = Math.Max(checked(estPhysW * estPhysH * 4), 1920 * 1080 * 4);
            if (_captureScratch.Length < minBytes)
                _captureScratch = new byte[minBytes];

            int rc, ow, oh;
            fixed (byte* pScratch = _captureScratch)
            {
                rc = sh_wl_capture_region(outputName, lx, ly, lw, lh, (IntPtr)pScratch, out ow, out oh, _captureScratch.Length);
            }
            if (rc == -5 && ow > 0 && oh > 0)
            {
                _captureScratch = new byte[checked(ow * oh * 4)];
                fixed (byte* pScratch = _captureScratch)
                {
                    rc = sh_wl_capture_region(outputName, lx, ly, lw, lh, (IntPtr)pScratch, out ow, out oh, _captureScratch.Length);
                }
            }
            if (rc != 0 || ow <= 0 || oh <= 0) return null;

            var bmp = Bitmap.CreateUninitialized(ow, oh, PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, ow, oh), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int rowBytes = ow * 4;
                if (data.Stride == rowBytes)
                {
                    Marshal.Copy(_captureScratch, 0, data.Scan0, rowBytes * oh);
                }
                else
                {
                    for (int y = 0; y < oh; y++)
                        Marshal.Copy(_captureScratch, y * rowBytes, data.Scan0 + y * data.Stride, rowBytes);
                }
            }
            finally { bmp.UnlockBits(data); }
            return bmp;
        }
    }

    private static Bitmap? CaptureViaGrim(string outputName, int lx, int ly, int lw, int lh, int outLogicalX, int outLogicalY)
    {
        try
        {
            int gx = outLogicalX + lx;
            int gy = outLogicalY + ly;
            var psi = new ProcessStartInfo("grim")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-g");
            psi.ArgumentList.Add($"{gx},{gy} {lw}x{lh}");
            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add("png");
            psi.ArgumentList.Add("-");
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            using var ms = new MemoryStream();
            proc.StandardOutput.BaseStream.CopyTo(ms);
            proc.WaitForExit(3000);
            if (proc.ExitCode != 0 || ms.Length < 16) return null;
            ms.Position = 0;
            return new Bitmap(ms);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("LinuxPlatform", $"grim capture failed: {ex.Message}");
            return null;
        }
    }

    public static bool SetCursorPos(int physX, int physY)
    {
        _lastVirtualCursor = new User32.POINT { X = physX, Y = physY };
        if (!EnsureNativeInitialized() || sh_wl_has_virtual_pointer() == 0) return false;

        var outputs = NiriIpc.GetOutputs();
        var target = outputs.Values.FirstOrDefault(o =>
        {
            int ox = (int)Math.Round(o.LogicalX * o.Scale);
            int oy = (int)Math.Round(o.LogicalY * o.Scale);
            return physX >= ox && physX < ox + o.PhysicalWidth && physY >= oy && physY < oy + o.PhysicalHeight;
        }) ?? (string.IsNullOrEmpty(_lastOutputName) ? outputs.Values.FirstOrDefault() : outputs.GetValueOrDefault(_lastOutputName) ?? outputs.Values.FirstOrDefault());

        if (target is null) return false;
        double scale = target.Scale > 0 ? target.Scale : 1.0;
        int outPhysX = (int)Math.Round(target.LogicalX * scale);
        int outPhysY = (int)Math.Round(target.LogicalY * scale);
        uint rx = (uint)Math.Clamp(physX - outPhysX, 0, Math.Max(1, target.PhysicalWidth - 1));
        uint ry = (uint)Math.Clamp(physY - outPhysY, 0, Math.Max(1, target.PhysicalHeight - 1));
        return sh_wl_pointer_move_abs(target.Name, rx, ry, (uint)Math.Max(1, target.PhysicalWidth), (uint)Math.Max(1, target.PhysicalHeight)) == 0;
    }

    public static bool GetCursorPos(out User32.POINT pt)
    {
        // Wayland does not expose global pointer queries to background clients;
        // return the last synthetic pointer position (used by RosterGameControl/PcGameInput).
        pt = _lastVirtualCursor;
        return true;
    }

    public static void SendMouse(uint flags, int dx, int dy)
    {
        if (!EnsureNativeInitialized() || sh_wl_has_virtual_pointer() == 0) return;
        if ((flags & User32.MOUSEEVENTF_MOVE) != 0 && (dx != 0 || dy != 0))
        {
            sh_wl_pointer_move_rel(dx, dy);
            _lastVirtualCursor.X += dx;
            _lastVirtualCursor.Y += dy;
        }
        if ((flags & User32.MOUSEEVENTF_LEFTDOWN) != 0)
            sh_wl_pointer_button(BtnLeft, 1);
        if ((flags & User32.MOUSEEVENTF_LEFTUP) != 0)
            sh_wl_pointer_button(BtnLeft, 0);
    }

    public static void ScrollWindow(IntPtr hwnd, int clicks)
    {
        if (!EnsureNativeInitialized() || sh_wl_has_virtual_pointer() == 0) return;
        var rect = User32.GetClientRectScreen(hwnd);
        if (rect is { } r)
            SetCursorPos(r.Left + r.Width / 2, r.Top + r.Height / 2);
        // Wayland vertical scroll: positive = down, negative = up (opposite of Win32 WHEEL_DELTA)
        sh_wl_pointer_scroll(-clicks * 15.0);
    }

    public static void SendScanKey(uint vk, bool up)
    {
        if (!EnsureNativeInitialized() || sh_wl_has_virtual_keyboard() == 0) return;
        uint evdev = VkToEvdev(vk);
        if (evdev == 0) return;
        sh_wl_keyboard_key(evdev, up ? 0 : 1);
    }

    private static uint VkToEvdev(uint vk) => vk switch
    {
        0x1B => 1,  // ESC
        '1' => 2, '2' => 3, '3' => 4, '4' => 5, '5' => 6,
        '6' => 7, '7' => 8, '8' => 9, '9' => 10, '0' => 11,
        0x09 => 15, // TAB
        'Q' => 16, 'W' => 17, 'E' => 18, 'R' => 19, 'T' => 20,
        'Y' => 21, 'U' => 22, 'I' => 23, 'O' => 24, 'P' => 25,
        0x0D => 28, // ENTER
        'A' => 30, 'S' => 31, 'D' => 32, 'F' => 33, 'G' => 34,
        'H' => 35, 'J' => 36, 'K' => 37, 'L' => 38,
        'Z' => 44, 'X' => 45, 'C' => 46, 'V' => 47, 'B' => 48,
        'N' => 49, 'M' => 50,
        0x20 => 57, // SPACE
        _ => 0
    };

    public static void CopyToClipboard(string text)
    {
        try
        {
            var psi = new ProcessStartInfo("wl-copy")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return;
            proc.StandardInput.Write(text);
            proc.StandardInput.Close();
            proc.WaitForExit(1500);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("LinuxPlatform", $"wl-copy failed: {ex.Message}");
        }
    }

    public static void OpenExternal(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo("xdg-open", target) { UseShellExecute = false, CreateNoWindow = true });
        }
        catch (Exception ex)
        {
            AppLogger.Warn("LinuxPlatform", $"xdg-open failed for {target}: {ex.Message}");
        }
    }
}
#endif
