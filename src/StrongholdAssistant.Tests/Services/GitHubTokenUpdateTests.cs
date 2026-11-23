using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ASPAssistant.Core.Services;
using StrongholdAssistant.Host;

namespace StrongholdAssistant.Tests.Services;

/// <summary>
/// GitHub Token（v1.0.2）：只在 GitHub API 请求上带 Token，下载地址不带；
/// 401 提示换 Token，额度用完提示填 Token；到期时间记进设置。
/// </summary>
[Collection("Update environment")]
public sealed class GitHubTokenUpdateTests
{
    private const string Token = "ghp_testtoken0123456789abcdefghijkl";
    private const string Expiration = "2027-03-10 08:00:00 UTC";

    [Fact]
    public async Task TokenGoesToApiOnlyAndExpiryIsSaved()
    {
        using var fixture = new Fixture();
        fixture.ExtraHeaders["GitHub-Authentication-Token-Expiration"] = Expiration;
        var api = await fixture.CreateApi(Token);
        var status = JsonSerializer.SerializeToElement(await api.CheckUpdateAsync());
        Assert.Equal("available", status.GetProperty("state").GetString());
        Assert.Equal(DateTimeOffset.Parse(Expiration.Replace(" UTC", "+00:00")),
            DateTimeOffset.Parse(status.GetProperty("githubTokenExpiresAt").GetString()!));
        Assert.Equal(DateTimeOffset.Parse(Expiration.Replace(" UTC", "+00:00")),
            DateTimeOffset.Parse(api.SettingsStore.LoadSettings().GitHubTokenExpiresAt));

        var release = fixture.Server.Requests.Single(r => r.Path.StartsWith("/repos/"));
        Assert.Equal("Bearer " + Token, release.Headers["Authorization"]);
        Assert.Equal("application/vnd.github+json", release.Headers["Accept"]);

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        api.Changed += (name, value) =>
        {
            if (name != "update") return;
            if (JsonSerializer.SerializeToElement(value).GetProperty("state").GetString() is "ready" or "failed")
                ready.TrySetResult();
        };
        await api.ApplyUpdateAsync();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(api.UpdatePrepared);
        // 下载包、校验文件、更新说明走 github.com/CDN，不能把 Token 带过去。
        var downloads = fixture.Assets.Requests.ToArray();
        Assert.NotEmpty(downloads);
        Assert.All(downloads, request => Assert.False(request.Headers.ContainsKey("Authorization")));
    }

    [Fact]
    public async Task MissingTokenKeepsAnonymousRequest()
    {
        using var fixture = new Fixture();
        var api = await fixture.CreateApi("");
        var status = JsonSerializer.SerializeToElement(await api.CheckUpdateAsync());
        Assert.Equal("available", status.GetProperty("state").GetString());
        Assert.False(status.TryGetProperty("githubTokenExpiresAt", out _));
        var release = fixture.Server.Requests.Single(r => r.Path.StartsWith("/repos/"));
        Assert.False(release.Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task UnauthorizedTokenIsReported()
    {
        using var fixture = new Fixture();
        fixture.Status = 401;
        var api = await fixture.CreateApi(Token);
        var status = JsonSerializer.SerializeToElement(await api.CheckUpdateAsync());
        Assert.Equal("failed", status.GetProperty("state").GetString());
        Assert.True(status.GetProperty("githubTokenProblem").GetBoolean());
        Assert.Contains("Token", status.GetProperty("message").GetString());
    }

    [Fact]
    public async Task RateLimitWithoutTokenSuggestsOwnToken()
    {
        using var fixture = new Fixture();
        fixture.Status = 403;
        fixture.ExtraHeaders["X-RateLimit-Remaining"] = "0";
        fixture.ExtraHeaders["X-RateLimit-Reset"] = "2000000000";
        var api = await fixture.CreateApi("");
        var status = JsonSerializer.SerializeToElement(await api.CheckUpdateAsync());
        Assert.Equal("failed", status.GetProperty("state").GetString());
        Assert.True(status.GetProperty("githubRateLimited").GetBoolean());
        var message = status.GetProperty("message").GetString()!;
        Assert.Contains("每小时 60 次", message);
        Assert.Contains("5000", message);
        // 重置时间按本地时区显示，只要求带上具体的钟点。
        Assert.Matches(@"\d{2}:\d{2}", message);
    }

    [Fact]
    public async Task ValidationReportsExpiryAndRejectsWrongToken()
    {
        using var fixture = new Fixture();
        fixture.ExtraHeaders["GitHub-Authentication-Token-Expiration"] = Expiration;
        var api = await fixture.CreateApi(Token);
        var valid = JsonSerializer.SerializeToElement(await api.ValidateGithubTokenAsync(Token));
        Assert.True(valid.GetProperty("valid").GetBoolean());
        Assert.Equal(DateTimeOffset.Parse(Expiration.Replace(" UTC", "+00:00")),
            DateTimeOffset.Parse(valid.GetProperty("expiresAt").GetString()!));
        Assert.Contains(fixture.Server.Requests, r => r.Path == "/rate_limit" && r.Headers["Authorization"] == "Bearer " + Token);

        fixture.Status = 401;
        var invalid = JsonSerializer.SerializeToElement(await api.ValidateGithubTokenAsync(Token));
        Assert.False(invalid.GetProperty("valid").GetBoolean());
        Assert.Contains("无效", invalid.GetProperty("message").GetString());
    }

    [Fact]
    public async Task StoreEncryptsTokenOnDisk()
    {
        using var fixture = new Fixture();
        var api = await fixture.CreateApi(Token);
        var raw = await File.ReadAllTextAsync(Path.Combine(fixture.DirectoryPath, "settings.json"));
        Assert.DoesNotContain(Token, raw);
        Assert.Contains("dpapi:", raw);
        Assert.Equal(Token, api.SettingsStore.LoadSettings().GitHubToken);
    }

    [Fact]
    public async Task MalformedTokenAndBackupExportAreSafe()
    {
        using var fixture = new Fixture();
        var api = await fixture.CreateApi(Token);
        Assert.Throws<ArgumentException>(() => api.SaveSettings(api.Settings with { GitHubToken = "带空格的 token" }));

        var path = Path.Combine(fixture.DirectoryPath, "backup.json");
        await api.ExportBackupAsync(path);
        using var backup = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var settings = backup.RootElement.GetProperty("settings");
        Assert.True(settings.TryGetProperty("githubToken", out var tokenField), settings.GetRawText());
        Assert.Equal("", tokenField.GetString());
        Assert.Equal("", settings.GetProperty("githubTokenExpiresAt").GetString());
        Assert.DoesNotContain(Token, await File.ReadAllTextAsync(path));
    }

    private sealed record Request(string Path, Dictionary<string, string> Headers);

    private sealed record Reply(int Status, byte[] Body, string ContentType = "application/json",
        Dictionary<string, string>? Headers = null);

    /// <summary>只用环回地址的小 HTTP 服务，扮演 api.github.com 和下载地址。</summary>
    private sealed class StubServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        public string Origin { get; }
        public List<Request> Requests { get; } = [];
        public Func<Request, Reply> Handler { get; set; } = _ => new(404, "{}"u8.ToArray());

        public StubServer()
        {
            _listener.Start();
            Origin = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _loop = Task.Run(async () =>
            {
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                        await using var stream = client.GetStream();
                        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                        var line = await reader.ReadLineAsync(_stop.Token);
                        if (string.IsNullOrEmpty(line)) continue;
                        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        while (true)
                        {
                            var header = await reader.ReadLineAsync(_stop.Token);
                            if (string.IsNullOrEmpty(header)) break;
                            var colon = header.IndexOf(':');
                            if (colon > 0) headers[header[..colon].Trim()] = header[(colon + 1)..].Trim();
                        }
                        var request = new Request(line.Split(' ')[1], headers);
                        lock (Requests) Requests.Add(request);
                        var reply = Handler(request);
                        var head = new StringBuilder($"HTTP/1.1 {reply.Status} {(reply.Status == 200 ? "OK" : "Error")}\r\n")
                            .Append($"Content-Type: {reply.ContentType}\r\n")
                            .Append($"Content-Length: {reply.Body.Length}\r\n")
                            .Append("Connection: close\r\n");
                        foreach (var (key, value) in reply.Headers ?? []) head.Append($"{key}: {value}\r\n");
                        head.Append("\r\n");
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()));
                        await stream.WriteAsync(reply.Body);
                    }
                }
                catch (OperationCanceledException) { }
            });
        }

        public void Dispose()
        {
            _stop.Cancel();
            _loop.GetAwaiter().GetResult();
            _listener.Stop();
            _stop.Dispose();
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly byte[] _package;
        private readonly string _hash;
        /// <summary>扮演 api.github.com：带 Token 的请求只该发到这里。</summary>
        public StubServer Server { get; } = new();
        /// <summary>扮演 github.com 的发布下载地址：不该收到 Token。</summary>
        public StubServer Assets { get; } = new();
        public HttpClient Http { get; } = new(new LocalHandler());
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "StrongholdAssistant.Tests", Guid.NewGuid().ToString("N"));
        public int Status { get; set; } = 200;
        public Dictionary<string, string> ExtraHeaders { get; } = [];

        public Fixture()
        {
            Directory.CreateDirectory(DirectoryPath);
            using var memory = new MemoryStream();
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
            using (var writer = new StreamWriter(zip.CreateEntry("StrongholdAssistant.exe").Open()))
                writer.Write("测试包，不执行");
            _package = memory.ToArray();
            _hash = Convert.ToHexString(SHA256.HashData(_package));
            Server.Handler = Reply;
            Assets.Handler = request => request.Path switch
            {
                "/download/pkg" => new(200, _package, "application/zip"),
                "/download/sums" => new(200, Encoding.UTF8.GetBytes($"{_hash}  StrongholdAssistant-v9.9.9-win-x64.zip"), "text/plain"),
                _ => new(404, "{}"u8.ToArray())
            };
        }

        private Reply Reply(Request request)
        {
            if (request.Path.StartsWith("/repos/"))
            {
                if (Status != 200) return new(Status, "{}"u8.ToArray(), Headers: new(ExtraHeaders));
                var body = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    tag_name = "v9.9.9",
                    body = "",
                    html_url = "https://github.com/LBEILC/StrongholdAssistant/releases/tag/v9.9.9",
                    assets = new object[]
                    {
                        new { name = "StrongholdAssistant-v9.9.9-win-x64.zip", browser_download_url = Assets.Origin + "/download/pkg" },
                        new { name = "SHA256SUMS.txt", browser_download_url = Assets.Origin + "/download/sums" },
                        new { name = "release-notes.json", browser_download_url = Assets.Origin + "/download/notes" }
                    }
                });
                // 到期时间只在成功响应里给，模拟 GitHub 的行为。
                var headers = new Dictionary<string, string>(ExtraHeaders);
                return new(200, body, Headers: headers);
            }
            return request.Path switch
            {
                "/rate_limit" => new(Status, "{}"u8.ToArray(), Headers: new(ExtraHeaders)),
                _ => new(404, "{}"u8.ToArray())
            };
        }

        public async Task<HostApi> CreateApi(string token)
        {
            var api = await HostApi.CreateAsync(Path.Combine(AppContext.BaseDirectory, "data"), DirectoryPath);
            api.SaveSettings(api.Settings with { UpdateSource = "github", GitHubToken = token });
            api.Updater = new UpdateService(new Version(1, 0, 0), DirectoryPath,
                feedUrl: Server.Origin + "/repos/LBEILC/StrongholdAssistant/releases/latest",
                githubApiUrl: Server.Origin, httpClient: Http);
            return api;
        }

        public void Dispose()
        {
            Server.Dispose();
            Assets.Dispose();
            Http.Dispose();
            Directory.Delete(DirectoryPath, true);
        }
    }

    /// <summary>更新说明走固定 GitHub 地址；测试不依赖公网可达性。</summary>
    private sealed class LocalHandler : HttpClientHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => request.RequestUri!.IsLoopback ? base.SendAsync(request, ct)
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
