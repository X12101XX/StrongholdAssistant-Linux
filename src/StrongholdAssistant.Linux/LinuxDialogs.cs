using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace StrongholdAssistant.Host;

internal static class LinuxSecretBox
{
    private static byte[] DeriveKey()
    {
        var machineId = File.Exists("/etc/machine-id") ? File.ReadAllText("/etc/machine-id").Trim() : Environment.MachineName;
        var material = $"StrongholdAssistant-Linux:{Environment.UserName}:{machineId}";
        return SHA256.HashData(Encoding.UTF8.GetBytes(material));
    }

    public static byte[] Protect(byte[] plain)
    {
        var key = DeriveKey();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, cipher, tag);
        var output = new byte[12 + 16 + cipher.Length];
        nonce.CopyTo(output, 0);
        tag.CopyTo(output, 12);
        cipher.CopyTo(output, 28);
        return output;
    }

    public static byte[] Unprotect(byte[] blob)
    {
        if (blob.Length < 28) throw new CryptographicException("Invalid secret payload.");
        var key = DeriveKey();
        var nonce = blob.AsSpan(0, 12);
        var tag = blob.AsSpan(12, 16);
        var cipher = blob.AsSpan(28);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }
}

internal static class ProcessElevation
{
    public static bool Current => Environment.IsPrivilegedProcess;
    public static bool ForWindow(nint hwnd) => false;
    public static void RestartElevated() => throw new PlatformNotSupportedException("Linux 无需以管理员身份运行。");
}

internal static class LinuxDialogs
{
    public static async Task<string?> SaveFileAsync(string title, string defaultFileName, string filterGlob)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop))
            desktop = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var defaultPath = Path.Combine(desktop, defaultFileName);

        var zenity = await TryRunToolAsync("zenity",
            ["--file-selection", "--save", "--confirm-overwrite", "--title", title, "--filename", defaultPath, "--file-filter", filterGlob]);
        if (zenity.Found) return zenity.Output;

        var kdialog = await TryRunToolAsync("kdialog", ["--getsavefilename", defaultPath, filterGlob, "--title", title]);
        if (kdialog.Found) return kdialog.Output;

        return defaultPath;
    }

    public static async Task<string?> OpenFileAsync(string title, string filterGlob)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var zenity = await TryRunToolAsync("zenity",
            ["--file-selection", "--title", title, "--filename", home + "/", "--file-filter", filterGlob]);
        if (zenity.Found) return zenity.Output;

        var kdialog = await TryRunToolAsync("kdialog", ["--getopenfilename", home, filterGlob, "--title", title]);
        if (kdialog.Found) return kdialog.Output;

        return null;
    }

    public static string[] ListSystemFonts()
    {
        try
        {
            var psi = new ProcessStartInfo("fc-list")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("--format=%{family}\n");
            using var proc = Process.Start(psi);
            if (proc is null) return ["Noto Sans CJK SC", "Sans"];
            var stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);
            var families = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .SelectMany(line => line.Split(',', StringSplitOptions.RemoveEmptyEntries))
                .Select(s => s.Trim())
                .Where(s => s.Length > 0 && !s.StartsWith('@'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), false))
                .ToArray();
            return families.Length > 0 ? families : ["Noto Sans CJK SC", "Sans"];
        }
        catch
        {
            return ["Noto Sans CJK SC", "Sans"];
        }
    }

    private static async Task<(bool Found, string? Output)> TryRunToolAsync(string tool, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(tool)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi);
            if (proc is null) return (false, null);
            var stdout = (await proc.StandardOutput.ReadToEndAsync()).Trim();
            await proc.WaitForExitAsync();
            return (true, proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(stdout) ? stdout : null);
        }
        catch
        {
            return (false, null);
        }
    }
}
