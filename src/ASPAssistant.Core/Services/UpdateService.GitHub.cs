// 2026-11-16：GitHub 源的 Token 支持（对位 MAA 的 GitHubToken 设计）。
// 匿名访问 api.github.com 每个 IP 每小时只有 60 次额度，提交版本号或反复检查更新时容易用完；
// 填上自己的 Token 后按 Token 计额度（每小时 5000 次），检查和下载都更稳。
using System.Globalization;
using System.Net;
using System.Net.Http;

namespace ASPAssistant.Core.Services;

/// <summary>Token 校验结果。<see cref="Message"/> 直接给用户看，<see cref="Valid"/> 为 true 时为空。</summary>
public sealed record GitHubTokenCheck(bool Valid, string? ExpiresAt, string Message);

public partial class UpdateService
{
    /// <summary>覆盖 GitHub API 地址的环境变量（只用于本地验证，正式用户不设置就走官方地址）。</summary>
    public const string GitHubApiOverrideVariable = "STRONGHOLD_GITHUB_API";

    private const string DefaultGitHubApi = "https://api.github.com";

    /// <summary>GitHub 接受的 API 版本头；不写也能用，写上让行为固定。</summary>
    private const string GitHubApiVersion = "2022-11-28";

    /// <summary>
    /// GitHub Token（可选，默认空）。只发给 GitHub API（检查更新、校验 Token），
    /// 不发给下载地址（github.com 的发布文件），避免把凭据带给文件服务器。
    /// </summary>
    public Func<string> GetGithubToken { get; set; } = () => "";

    /// <summary>这次检查拿到的 Token 到期时间（ISO 8601）；Token 永不过期、没填或响应里没带时为空。</summary>
    public string? TokenExpiresAt { get; private set; }

    /// <summary>Token 无效或已过期（GitHub 返回 401），界面提示重新填写。</summary>
    public bool TokenProblem { get; private set; }

    /// <summary>请求额度用完（匿名每小时 60 次，带 Token 每小时 5000 次）。</summary>
    public bool RateLimited { get; private set; }

    /// <summary>额度重置时间（本地时间 HH:mm）；响应里没带时为空。</summary>
    public string? RateLimitReset { get; private set; }

    /// <summary>生效的 GitHub API 地址（环境变量优先，其次官方地址）。</summary>
    public string GitHubApi => ResolveGitHubApi(
        githubApiUrl ?? Environment.GetEnvironmentVariable(GitHubApiOverrideVariable));

    public static string ResolveGitHubApi(string? overrideUrl)
        => string.IsNullOrWhiteSpace(overrideUrl) ? DefaultGitHubApi : overrideUrl.Trim().TrimEnd('/');

    /// <summary>只有发往 GitHub API 的请求才带 Token。</summary>
    private bool IsGitHubApiUrl(string url)
    {
        var api = GitHubApi;
        return string.Equals(url, api, StringComparison.OrdinalIgnoreCase)
            || url.StartsWith(api + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>当前 Token：去掉首尾空白；带了 HTTP 头里放不下的字符（空白、控制字符、非 ASCII）就当没填。</summary>
    private string CurrentToken()
    {
        var token = (GetGithubToken() ?? "").Trim();
        return token.Any(c => c <= ' ' || c >= '\u007f') ? "" : token;
    }

    private static HttpRequestMessage ApiRequest(string url, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", GitHubApiVersion);
        if (token.Length > 0) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        return request;
    }

    /// <summary>
    /// 发请求：GitHub API 带 Token 和 API 头，其他地址（下载包、更新说明）按普通请求发。
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(string url, HttpCompletionOption option, CancellationToken ct)
    {
        if (!IsGitHubApiUrl(url)) return await _http.GetAsync(url, option, ct);
        using var request = ApiRequest(url, CurrentToken());
        return await _http.SendAsync(request, option, ct);
    }

    private void ResetGitHubState()
    {
        TokenExpiresAt = null;
        TokenProblem = false;
        RateLimited = false;
        RateLimitReset = null;
    }

    /// <summary>从 GitHub 响应里读 Token 到期时间和额度重置时间（成功、失败都有）。</summary>
    private void ReadGitHubHeaders(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("GitHub-Authentication-Token-Expiration", out var expirations))
        {
            foreach (var raw in expirations)
            {
                if (!TryParseGitHubTime(raw, out var expires)) continue;
                TokenExpiresAt = expires.ToString("O");
                break;
            }
        }
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resets)
            && long.TryParse(resets.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix)
            && unix is > 0 and < 4102444800) // 2100 年之前，防脏数据
            RateLimitReset = DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime().ToString("HH:mm");
    }

    private static bool HasNoQuota(HttpResponseMessage response)
        => response.Headers.TryGetValues("X-RateLimit-Remaining", out var values)
           && values.FirstOrDefault()?.Trim() == "0";

    /// <summary>
    /// 把 GitHub 的失败响应翻译成用户能处理的话。返回 true 表示已经给出原因，调用方不用再抛。
    /// 401 是 Token 的问题；403/429 且额度为 0 是限流，没填 Token 时顺手提示去填。
    /// </summary>
    private bool DescribeGitHubProblem(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        var hasToken = CurrentToken().Length > 0;
        if (status == 401 || (status == 403 && hasToken && !HasNoQuota(response)))
        {
            TokenProblem = true;
            LastError = "GitHub Token 无效或已过期，请检查设置里填的 Token。";
            return true;
        }
        if (status is 403 or 429 && HasNoQuota(response))
        {
            RateLimited = true;
            var when = RateLimitReset is { } reset ? $"{reset} 之后" : "过一会儿";
            LastError = hasToken
                ? $"这个 GitHub Token 的请求额度也用完了，{when}再试。"
                : $"GitHub 匿名请求的额度用完了（每小时 60 次），{when}再试；"
                  + "在设置里填一个 GitHub Token，额度提高到每小时 5000 次。";
            return true;
        }
        if (status is 403 or 429)
        {
            LastError = hasToken
                ? "GitHub 拒绝了请求（HTTP 403），可能是 Token 权限不够或被限流。"
                : "GitHub 拒绝了请求（HTTP 403），可能是网络被拦截或匿名额度用完了。";
            return true;
        }
        if (status == 404)
        {
            LastError = "GitHub 上找不到这个发布（HTTP 404），可能还没发布或仓库改名了。";
            return true;
        }
        return false;
    }

    /// <summary>校验 Token：能读到额度信息就是有效，同时拿到 GitHub 给的到期时间。</summary>
    public async Task<GitHubTokenCheck> CheckGitHubTokenAsync(string token, CancellationToken ct = default)
    {
        token = (token ?? "").Trim();
        if (token.Length == 0) return new(false, null, "先填 Token 再验证。");
        if (token.Any(c => c <= ' ' || c >= '\u007f'))
            return new(false, null, "Token 里不能有空格或中文，检查是不是复制多了。");
        try
        {
            using var request = ApiRequest(GitHubApi + "/rate_limit", token);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return new(false, null, "Token 无效，请检查输入是否正确。");
            if (!response.IsSuccessStatusCode)
                return new(false, null, $"GitHub 返回 HTTP {(int)response.StatusCode}，稍后再试。");
            string? expires = null;
            if (response.Headers.TryGetValues("GitHub-Authentication-Token-Expiration", out var values))
            {
                foreach (var raw in values)
                {
                    if (!TryParseGitHubTime(raw, out var parsed)) continue;
                    expires = parsed.ToString("O");
                    break;
                }
            }
            return new(true, expires, "");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new(false, null, DescribeError(ex, GetUpdateProxy()));
        }
    }

    /// <summary>GitHub 的时间格式是 "2026-03-10 12:00:00 UTC"；也接受标准 ISO。</summary>
    public static bool TryParseGitHubTime(string? raw, out DateTimeOffset value)
    {
        value = default;
        var text = (raw ?? "").Trim();
        if (text.Length == 0) return false;
        if (text.EndsWith("UTC", StringComparison.OrdinalIgnoreCase))
            text = text[..^3].TrimEnd() + " +00:00";
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);
    }
}
