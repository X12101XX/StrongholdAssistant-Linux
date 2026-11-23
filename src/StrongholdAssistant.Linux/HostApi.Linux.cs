using System.Text.Json;

namespace StrongholdAssistant.Host;

public sealed partial class HostApi
{
    internal Func<object?>? ReadLinuxDiagnosticCapture { get; set; }

    private static string[] ListFonts() => LinuxDialogs.ListSystemFonts();

    private void CleanupPlazaProfile() { }
    private void CleanupSklandProfile() { }

    private Task<string?> PlazaLoginAsync()
    {
        ASPAssistant.Core.Interop.LinuxPlatform.OpenExternal("https://sa.lubeiluchen.com");
        throw new InvalidOperationException("Linux 版暂不支持内置阵容广场登录弹窗，已在浏览器打开阵容广场页面。");
    }

    private Task PlazaLogoutAsync()
    {
        new PlazaTokenStore(DataDirectory).Delete();
        return Task.CompletedTask;
    }

    private SklandAccount? ReadSklandAccount() => null;

    private Task<SklandAccount?> SklandLoginAsync()
        => throw new InvalidOperationException("Linux 版暂未启用森空岛内置 WebView 登录。");

    private Task SklandLogoutAsync() => Task.CompletedTask;

    private Task<SklandGuide> SklandGuideAsync(string url)
        => throw new InvalidOperationException("Linux 版暂未启用森空岛攻略内置抓取，请直接复制攻略或使用阵容码导入。");

    private Task<SklandGuidePage> SklandGuidesAsync(SklandGuideQuery query)
        => throw new InvalidOperationException("Linux 版暂未启用森空岛攻略列表内置抓取。");

    private Task<SklandRecord[]?> SklandHistoryAsync(bool silent = false)
        => Task.FromResult<SklandRecord[]?>(null);
}
