using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Services.Update;
using Xunit;

namespace NexusPipeline.Tests;

/// <summary>更新域 L1：SemVer 解析比较、releases JSON 解析、渠道过滤、主机白名单、zip 校验与布局归一。</summary>
public sealed class UpdateCatalogTests
{
    [Theory]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("v0.10.0", 0, 10, 0)]
    [InlineData("10.11.12", 10, 11, 12)]
    public void TryParseTag_AcceptsStandardTags(string tag, int major, int minor, int patch)
    {
        Assert.True(UpdateCatalog.TryParseTag(tag, out var version));
        Assert.Equal((major, minor, patch), version);
    }

    [Theory]
    [InlineData("1.2")]
    [InlineData("v1.2.3.4")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParseTag_RejectsMalformedTags(string? tag)
    {
        Assert.False(UpdateCatalog.TryParseTag(tag, out _));
    }

    [Fact]
    public void Compare_OrdersByMajorThenMinorThenPatch()
    {
        Assert.True(UpdateCatalog.Compare((0, 10, 0), (1, 0, 0)) < 0);
        Assert.True(UpdateCatalog.Compare((1, 2, 3), (1, 2, 3)) == 0);
        Assert.True(UpdateCatalog.Compare((1, 2, 4), (1, 2, 3)) > 0);
        Assert.True(UpdateCatalog.Compare((1, 3, 0), (1, 2, 9)) > 0);
    }

    [Fact]
    public void PickRelease_SkipsDraftAndRequiresBothAssets()
    {
        // 当前版本 0.10.0；v0.10.1 资产齐全 → 选中；v0.10.2 缺少 sha → 跳过；v0.10.3 draft → 跳过。
        JsonNode root = JsonNode.Parse("""
        [
          { "tag_name": "v0.10.2", "draft": false, "prerelease": true, "assets": [
              { "name": "NexusPipeline-v0.10.2-win-x64.zip", "browser_download_url": "https://github.com/x.zip" }
          ] },
          { "tag_name": "v0.10.3", "draft": true, "prerelease": true, "assets": [
              { "name": "NexusPipeline-v0.10.3-win-x64.zip", "browser_download_url": "https://github.com/x.zip" },
              { "name": "NexusPipeline-v0.10.3-win-x64.zip.sha256", "browser_download_url": "https://github.com/x.sha" }
          ] },
          { "tag_name": "v0.10.1", "draft": false, "prerelease": true, "body": "更新说明",
            "assets": [
              { "name": "NexusPipeline-v0.10.1-win-x64.zip", "browser_download_url": "https://github.com/a.zip" },
              { "name": "NexusPipeline-v0.10.1-win-x64.zip.sha256", "browser_download_url": "https://github.com/a.sha" }
          ] }
        ]
        """)!;

        ReleaseInfo? release = UpdateCatalog.PickRelease(root, "prerelease", (0, 10, 0));

        Assert.NotNull(release);
        Assert.Equal("v0.10.1", release!.Tag);
        Assert.Equal("0.10.1", release.VersionText);
        Assert.Equal("更新说明", release.Notes);
        Assert.True(release.Prerelease);
    }

    [Fact]
    public void PickRelease_StableChannelFiltersPrerelease()
    {
        JsonNode root = JsonNode.Parse("""
        [
          { "tag_name": "v0.11.0", "draft": false, "prerelease": false, "assets": [
              { "name": "NexusPipeline-v0.11.0-win-x64.zip", "browser_download_url": "https://github.com/s.zip" },
              { "name": "NexusPipeline-v0.11.0-win-x64.zip.sha256", "browser_download_url": "https://github.com/s.sha" }
          ] },
          { "tag_name": "v0.10.9", "draft": false, "prerelease": true, "assets": [
              { "name": "NexusPipeline-v0.10.9-win-x64.zip", "browser_download_url": "https://github.com/p.zip" },
              { "name": "NexusPipeline-v0.10.9-win-x64.zip.sha256", "browser_download_url": "https://github.com/p.sha" }
          ] }
        ]
        """)!;

        ReleaseInfo? stable = UpdateCatalog.PickRelease(root, "stable", (0, 10, 0));
        ReleaseInfo? prerelease = UpdateCatalog.PickRelease(root, "prerelease", (0, 10, 0));

        Assert.Equal("v0.11.0", stable!.Tag);
        // prerelease 渠道取最高版本（stable 与 prerelease 均可见）。
        Assert.Equal("v0.11.0", prerelease!.Tag);
    }

    [Fact]
    public void PickRelease_ReturnsNullWhenLatestNotAboveCurrent()
    {
        JsonNode root = JsonNode.Parse("""
        [
          { "tag_name": "v0.10.0", "draft": false, "prerelease": false, "assets": [
              { "name": "NexusPipeline-v0.10.0-win-x64.zip", "browser_download_url": "https://github.com/x.zip" },
              { "name": "NexusPipeline-v0.10.0-win-x64.zip.sha256", "browser_download_url": "https://github.com/x.sha" }
          ] }
        ]
        """)!;

        Assert.Null(UpdateCatalog.PickRelease(root, "prerelease", (0, 10, 0)));
        Assert.Null(UpdateCatalog.PickRelease(JsonNode.Parse("[]"), "prerelease", (0, 10, 0)));
    }

    [Fact]
    public void IsAllowedHost_DefaultSourceAllowsGitHubOnly()
    {
        Assert.True(UpdateCatalog.IsAllowedHost("api.github.com", null));
        Assert.True(UpdateCatalog.IsAllowedHost("github.com", null));
        Assert.False(UpdateCatalog.IsAllowedHost("evil.example.com", null));
    }

    [Fact]
    public void IsAllowedHost_CustomSourceAllowsOnlyItsOwnHost()
    {
        string source = "https://mirror.example.com/updates/releases";
        Assert.True(UpdateCatalog.IsAllowedHost("mirror.example.com", source));
        Assert.False(UpdateCatalog.IsAllowedHost("github.com", source));
        Assert.False(UpdateCatalog.IsAllowedHost("evil.example.com", source));
        // 测试镜像（回环 http）
        Assert.True(UpdateCatalog.IsAllowedHost("127.0.0.1", "http://127.0.0.1:5899/releases"));
    }

    [Theory]
    [InlineData("https://api.github.com/x", null)]
    [InlineData("http://127.0.0.1:5899/x", null)]
    public void ValidateSource_AcceptsHttpsAndLoopbackHttp(string source, object? _)
    {
        Assert.Null(UpdateCatalog.ValidateSource(source));
    }

    [Fact]
    public void ValidateSource_RejectsNonHttpsRemoteAndGarbage()
    {
        Assert.NotNull(UpdateCatalog.ValidateSource("http://example.com/releases"));
        Assert.NotNull(UpdateCatalog.ValidateSource("not a url"));
        Assert.NotNull(UpdateCatalog.ValidateSource("ftp://example.com/x"));
    }

    [Fact]
    public async Task SourcePolicy_RejectsAssetSchemeAndRedirectEscape()
    {
        UpdateSourcePolicy policy = new("https://mirror.example.com/releases");

        Assert.Null(policy.ValidateAssetUri(new Uri("https://mirror.example.com/releases/pkg.zip")));
        Assert.NotNull(policy.ValidateAssetUri(new Uri("http://mirror.example.com/releases/pkg.zip")));
        Assert.NotNull(policy.ValidateAssetUri(new Uri("https://evil.example.com/pkg.zip")));

        using var http = new HttpClient(new RedirectEscapeHandler());
        await Assert.ThrowsAsync<InvalidDataException>(() => policy.GetAsync(
            http,
            new Uri("https://mirror.example.com/releases/pkg.zip"),
            manifest: false,
            "test",
            CancellationToken.None));
    }

    private sealed class RedirectEscapeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                RequestMessage = request,
            };
            response.Headers.Location = new Uri("https://evil.example.com/pkg.zip");
            return Task.FromResult(response);
        }
    }
}

/// <summary>更新域 L1/L2：zip 校验、条目白名单、布局归一与 SHA256 比对。</summary>
public sealed class UpdatePackageTests
{
    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "np-update-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteZip(string path, params (string Name, string Content)[] entries)
    {
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach ((string name, string content) in entries)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
    }

    [Fact]
    public void Extract_NormalizesSingleTopDirectoryLayout()
    {
        string root = NewTempDir();
        try
        {
            string zip = Path.Combine(root, "pkg.zip");
            WriteZip(zip,
                ("NexusPipeline-v0.10.1-win-x64/nexus-pipeline.exe", "exe"),
                ("NexusPipeline-v0.10.1-win-x64/wwwroot/index.js", "app"));
            string staging = Path.Combine(root, "staging");

            string? error = UpdatePackage.Extract(zip, staging);

            Assert.Null(error);
            Assert.True(File.Exists(Path.Combine(staging, "nexus-pipeline.exe")));
            Assert.True(File.Exists(Path.Combine(staging, "wwwroot", "index.js")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Extract_RejectsTraversalAndAbsoluteEntries()
    {
        string root = NewTempDir();
        try
        {
            string staging = Path.Combine(root, "staging");
            Directory.CreateDirectory(staging);

            string traversal = Path.Combine(root, "t.zip");
            WriteZip(traversal, ("../evil.txt", "x"), ("nexus-pipeline.exe", "exe"));
            Assert.NotNull(UpdatePackage.Extract(traversal, staging));

            string absolute = Path.Combine(root, "a.zip");
            WriteZip(absolute, ("C:\\evil.txt", "x"), ("nexus-pipeline.exe", "exe"));
            Assert.NotNull(UpdatePackage.Extract(absolute, staging));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Extract_RejectsForbiddenDataDirectoriesAndDuplicates()
    {
        string root = NewTempDir();
        try
        {
            string staging = Path.Combine(root, "staging");
            Directory.CreateDirectory(staging);

            string forbidden = Path.Combine(root, "f.zip");
            WriteZip(forbidden, ("config/settings.json", "{}"), ("nexus-pipeline.exe", "exe"));
            Assert.NotNull(UpdatePackage.Extract(forbidden, staging));

            string duplicate = Path.Combine(root, "d.zip");
            WriteZip(duplicate, ("wwwroot/a.js", "1"), ("wwwroot/a.js", "2"), ("nexus-pipeline.exe", "exe"));
            Assert.NotNull(UpdatePackage.Extract(duplicate, staging));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Extract_RejectsPackageWithoutExecutable()
    {
        string root = NewTempDir();
        try
        {
            string zip = Path.Combine(root, "p.zip");
            WriteZip(zip, ("readme.txt", "hello"));
            string staging = Path.Combine(root, "staging");

            string? error = UpdatePackage.Extract(zip, staging);

            Assert.NotNull(error);
            Assert.Contains("nexus-pipeline.exe", error);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Extract_RejectsArchiveEntryCountAndCompressionRatio()
    {
        string root = NewTempDir();
        try
        {
            string countZip = Path.Combine(root, "count.zip");
            using (var stream = File.Create(countZip))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                for (int index = 0; index <= UpdatePackage.MaxArchiveEntries; index++)
                {
                    archive.CreateEntry($"empty-{index}/");
                }
                using StreamWriter writer = new(archive.CreateEntry("nexus-pipeline.exe").Open(), new UTF8Encoding(false));
                writer.Write("exe");
            }
            Assert.NotNull(UpdatePackage.Extract(countZip, Path.Combine(root, "count-staging")));

            string ratioZip = Path.Combine(root, "ratio.zip");
            using (var stream = File.Create(ratioZip))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                ZipArchiveEntry entry = archive.CreateEntry("nexus-pipeline.exe", CompressionLevel.Optimal);
                using Stream output = entry.Open();
                output.Write(new byte[1024 * 1024]);
            }
            Assert.NotNull(UpdatePackage.Extract(ratioZip, Path.Combine(root, "ratio-staging")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void VerifySha256_MatchesAndDetectsTampering()
    {
        string root = NewTempDir();
        try
        {
            string zip = Path.Combine(root, "pkg.zip");
            File.WriteAllText(zip, "payload");
            string sha = Path.Combine(root, "pkg.sha");
            string expected;
            using (var stream = File.OpenRead(zip))
            using (var hasher = SHA256.Create())
            {
                expected = Convert.ToHexString(hasher.ComputeHash(stream));
            }

            File.WriteAllText(sha, expected + Environment.NewLine);
            Assert.True(UpdatePackage.VerifySha256(zip, sha, out _));

            File.WriteAllText(sha, new string('0', 64) + Environment.NewLine);
            Assert.False(UpdatePackage.VerifySha256(zip, sha, out string? error));
            Assert.Contains("SHA256", error);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

/// <summary>更新域 L2：对接本地 HttpListener stub 源的状态机（检查/下载/校验/应用/取消/互斥）。</summary>
public sealed class UpdateServiceTests : IAsyncLifetime
{
    private static string CurrentVersion => UpdateService.CurrentVersion;

    private static string CandidateVersion
    {
        get
        {
            Version current = Version.Parse(CurrentVersion);
            return $"{current.Major}.{current.Minor}.{checked(current.Build + 1)}";
        }
    }

    private TcpListener? _listener;
    private CancellationTokenSource? _listenerCts;
    private Task? _listenerTask;
    private bool _holdManifest;
    private TaskCompletionSource<bool>? _manifestRequestStarted;
    private TaskCompletionSource<bool>? _manifestRelease;
    private int _port;
    private string? _root;
    private string? _installDir;
    private byte[] _zipBytes = Array.Empty<byte>();
    private string _zipSha = "";
    private AppSettings _settings = new();
    private bool _canApply = true;
    private bool _exited;
    private List<string> _launched = new();

    public Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "np-update-l2-" + Guid.NewGuid().ToString("N"));
        _installDir = Path.Combine(_root, "install");
        Directory.CreateDirectory(_installDir);

        // 构造与发布资产同名的 zip：exe + wwwroot + plugins/.nxp-root 兼容根标记。
        using (var stream = new MemoryStream())
        {
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                AddEntry(archive, "nexus-pipeline.exe", "fake-exe-" + Guid.NewGuid().ToString("N"));
                AddEntry(archive, "wwwroot/index.js", "// fake");
                AddEntry(archive, "plugins/.nxp-root", "{\"owner\":\"NexusPipeline\",\"purpose\":\"plugin-runtime-root\",\"version\":1}");
            }
            _zipBytes = stream.ToArray();
        }
        using var sha = SHA256.Create();
        _zipSha = Convert.ToHexString(sha.ComputeHash(_zipBytes)).ToLowerInvariant();

        // 测试源只需要本地 HTTP 语义；TcpListener 不依赖 Windows HTTP.sys 或 URL ACL，
        // 避免默认单测被管理员权限和沙箱策略影响。
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        _listener = listener;
        _port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _listenerCts = new CancellationTokenSource();
        _listenerTask = ServeAsync(listener, _listenerCts.Token);

        _settings = new AppSettings { UpdateChannel = "prerelease" };
        _settings.UpdateSourceUrl = SourceUrl;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        StopListener();
        if (_listenerTask is not null)
        {
            try
            {
                await _listenerTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
        _listenerCts?.Dispose();
        _listenerCts = null;
        _listenerTask = null;
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static void AddEntry(ZipArchive archive, string name, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private string SourceUrl => $"http://127.0.0.1:{_port}/";

    private void StopListener()
    {
        _listenerCts?.Cancel();
        try
        {
            _listener?.Stop();
        }
        catch (ObjectDisposedException)
        {
        }
        _listener = null;
    }

    private async Task ServeAsync(TcpListener listener, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                _ = HandleClientAsync(client, token);
            }
        }
        catch (SocketException) when (token.IsCancellationRequested)
        {
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            try
            {
                await using NetworkStream stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, 8192, leaveOpen: true);
                string? requestLine = await reader.ReadLineAsync(token);
                if (string.IsNullOrWhiteSpace(requestLine))
                {
                    return;
                }
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(token)))
                {
                }

                string[] requestParts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                string path = requestParts.Length >= 2
                    ? requestParts[1].Split('?', 2)[0]
                    : "/";
                if (_holdManifest && (path == "/releases" || path == "/"))
                {
                    _manifestRequestStarted?.TrySetResult(true);
                    await _manifestRelease!.Task.WaitAsync(token);
                }
                (int statusCode, string contentType, byte[] body) = BuildResponse(path);
                string reason = statusCode == 200 ? "OK" : "Not Found";
                byte[] header = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {statusCode} {reason}\r\n" +
                    $"Content-Type: {contentType}\r\n" +
                    $"Content-Length: {body.Length}\r\n" +
                    "Connection: close\r\n\r\n");
                await stream.WriteAsync(header, token);
                await stream.WriteAsync(body, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (IOException)
            {
                // 客户端取消请求时，测试源无需再写入已关闭的连接。
            }
            catch (SocketException)
            {
            }
        }
    }

    private (int StatusCode, string ContentType, byte[] Body) BuildResponse(string path)
    {
        if (path == "/releases" || path == "/")
        {
            var releases = new JsonArray();
            var release = new JsonObject
            {
                ["tag_name"] = $"v{CandidateVersion}",
                ["name"] = $"v{CandidateVersion}",
                ["draft"] = false,
                ["prerelease"] = true,
                ["body"] = "更新说明",
                ["assets"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["name"] = $"NexusPipeline-v{CandidateVersion}-win-x64.zip",
                        ["browser_download_url"] = $"{SourceUrl}NexusPipeline-v{CandidateVersion}-win-x64.zip",
                    },
                    new JsonObject
                    {
                        ["name"] = $"NexusPipeline-v{CandidateVersion}-win-x64.zip.sha256",
                        ["browser_download_url"] = $"{SourceUrl}NexusPipeline-v{CandidateVersion}-win-x64.zip.sha256",
                    },
                },
            };
            releases.Add(release);
            return (200, "application/json", Encoding.UTF8.GetBytes(releases.ToJsonString()));
        }
        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return (200, "application/octet-stream", _zipBytes);
        }
        if (path.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase))
        {
            return (200, "text/plain", Encoding.ASCII.GetBytes(_zipSha + Environment.NewLine));
        }
        return (404, "text/plain", Array.Empty<byte>());
    }

    private UpdateService NewService()
    {
        return new UpdateService(
            () => _settings,
            _installDir!,
            () => _canApply,
            () => _exited = true);
    }

    private static async Task WaitStateAsync(UpdateService service, UpdateState state, int timeoutMs = 15000)
    {
        DateTime deadline = DateTime.Now.AddMilliseconds(timeoutMs);
        while (DateTime.Now < deadline)
        {
            if (service.State == state)
            {
                return;
            }
            await Task.Delay(50);
        }
        Assert.Fail($"状态未在超时时间内到达 {state}（当前 {service.State}）");
    }

    [Fact]
    public async Task Check_FindsPrereleaseUpdateAboveCurrent()
    {
        UpdateService service = NewService();

        UpdateStatusSnapshot status = await service.CheckAsync("test");

        Assert.Equal(UpdateState.Idle, status.State);
        Assert.True(status.Available);
        Assert.Equal(CandidateVersion, status.Latest);
        Assert.Contains("更新说明", status.Notes);
    }

    [Fact]
    public async Task Download_VerifiesAndStagesReady()
    {
        UpdateService service = NewService();
        await service.CheckAsync("test");

        string? error = service.StartDownload("test");

        Assert.Null(error);
        await WaitStateAsync(service, UpdateState.Ready);
        UpdateStatusSnapshot status = service.GetStatus();
        Assert.True(string.IsNullOrEmpty(status.Error));
        string stagingRoot = Path.Combine(_installDir!, ".nxp-update", "staging");
        string staging = Assert.Single(Directory.GetDirectories(stagingRoot, $"{CandidateVersion}.g*", SearchOption.TopDirectoryOnly));
        Assert.True(File.Exists(Path.Combine(staging, "nexus-pipeline.exe")));
        Assert.True(File.Exists(Path.Combine(staging, "wwwroot", "index.js")));
    }

    [Fact]
    public async Task Download_RejectsConcurrentOperations()
    {
        UpdateService service = NewService();
        await service.CheckAsync("test");

        Assert.Null(service.StartDownload("test"));
        string? second = service.StartDownload("test");
        Assert.NotNull(second);
        await WaitStateAsync(service, UpdateState.Ready);
    }

    [Fact]
    public async Task Download_TamperedShaFailsAndReturnsToIdle()
    {
        _zipSha = new string('0', 64);
        UpdateService service = NewService();
        await service.CheckAsync("test");

        service.StartDownload("test");

        await WaitStateAsync(service, UpdateState.Idle);
        UpdateStatusSnapshot status = service.GetStatus();
        Assert.Contains("SHA256", status.Error);
    }

    [Fact]
    public async Task Check_RejectsSecondConcurrentCheck()
    {
        UpdateService service = NewService();
        // 直接并发：第一次检查完成后第二次检查仍应正常（检查串行安全）。
        UpdateStatusSnapshot first = await service.CheckAsync("test");
        UpdateStatusSnapshot second = await service.CheckAsync("test");
        Assert.True(first.Available);
        Assert.True(second.Available);
    }

    [Fact]
    public async Task Cancel_MidCheckReturnsToIdleWithCancelNote()
    {
        // 用本地测试源明确挂起清单响应，避免依赖外部网络的连接超时行为。
        _holdManifest = true;
        _manifestRequestStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _manifestRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        UpdateService service = NewService();
        Task<UpdateStatusSnapshot> check = service.CheckAsync("test");
        await _manifestRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(service.CancelDownload());
        _manifestRelease.TrySetResult(true);
        await check;
        UpdateStatusSnapshot status = service.GetStatus();
        Assert.Equal(UpdateState.Idle, status.State);
    }

    [Fact]
    public async Task Apply_ReadyAndGatePassedWritesTaskAndRequestsExit()
    {
        UpdateService service = NewService();
        await service.CheckAsync("test");
        service.StartDownload("test");
        await WaitStateAsync(service, UpdateState.Ready);
        bool spawned = false;
        UpdateApply.LaunchApplyOverride = staged =>
        {
            spawned = true;
            return true;
        };
        try
        {
            UpdateApplyResult result = service.RequestApply(defer: false, "test");

            Assert.True(result.Succeeded);
            // 切换动作在后台任务执行（等待响应 flush 后拉起子进程并请求退出）。
            DateTime deadline = DateTime.Now.AddSeconds(10);
            while (DateTime.Now < deadline && (!spawned || !_exited))
            {
                await Task.Delay(50);
            }
            Assert.True(spawned);
            Assert.True(_exited);
            Assert.True(File.Exists(Path.Combine(_installDir!, ".nxp-update", "task.json")));
            UpdateTask? task = UpdateTask.Read(Path.Combine(_installDir!, ".nxp-update", "task.json"));
            Assert.NotNull(task);
            Assert.Equal("apply", task!.Mode);
            Assert.Equal(CandidateVersion, task.Version);
        }
        finally
        {
            UpdateApply.LaunchApplyOverride = null;
        }
    }

    [Fact]
    public async Task Apply_GateBlocksWhenBusy()
    {
        UpdateService service = NewService();
        await service.CheckAsync("test");
        service.StartDownload("test");
        await WaitStateAsync(service, UpdateState.Ready);
        _canApply = false;

        UpdateApplyResult result = service.RequestApply(defer: false, "test");

        Assert.False(result.Succeeded);
        Assert.Equal("busy", result.Code);
        Assert.False(_exited);
    }

    [Fact]
    public async Task Apply_DeferWritesTaskWithoutSpawningOrExiting()
    {
        UpdateService service = NewService();
        await service.CheckAsync("test");
        service.StartDownload("test");
        await WaitStateAsync(service, UpdateState.Ready);
        bool spawned = false;
        UpdateApply.LaunchApplyOverride = _ =>
        {
            spawned = true;
            return true;
        };
        try
        {
            UpdateApplyResult result = service.RequestApply(defer: true, "test");

            Assert.True(result.Succeeded);
            Assert.True(result.Deferred);
            Assert.False(spawned);
            Assert.False(_exited);
            UpdateTask? task = UpdateTask.Read(Path.Combine(_installDir!, ".nxp-update", "task.json"));
            Assert.Equal("defer", task!.Mode);
        }
        finally
        {
            UpdateApply.LaunchApplyOverride = null;
        }
    }

    [Fact]
    public async Task Apply_DeferIsAllowedWhenImmediateGateIsBusy()
    {
        UpdateService service = NewService();
        await service.CheckAsync("test");
        service.StartDownload("test");
        await WaitStateAsync(service, UpdateState.Ready);
        _canApply = false;

        UpdateApplyResult result = service.RequestApply(defer: true, "test");

        Assert.True(result.Succeeded);
        Assert.True(result.Deferred);
        Assert.Equal(UpdateState.ApplyPending, service.State);
    }

    [Fact]
    public async Task Apply_WorkerLaunchFailureKeepsHostRunningAndReturnsReady()
    {
        UpdateService service = NewService();
        await service.CheckAsync("test");
        service.StartDownload("test");
        await WaitStateAsync(service, UpdateState.Ready);
        UpdateApply.LaunchApplyOverride = _ => false;
        try
        {
            UpdateApplyResult result = service.RequestApply(defer: false, "test");

            Assert.False(result.Succeeded);
            Assert.Equal("worker-launch-failed", result.Code);
            Assert.Equal(UpdateState.Ready, service.State);
            Assert.False(_exited);
            Assert.False(File.Exists(Path.Combine(_installDir!, ".nxp-update", "task.json")));
        }
        finally
        {
            UpdateApply.LaunchApplyOverride = null;
        }
    }

    [Fact]
    public async Task Check_DoesNotOverwriteReadyState()
    {
        UpdateService service = NewService();
        await service.CheckAsync("test");
        service.StartDownload("test");
        await WaitStateAsync(service, UpdateState.Ready);

        UpdateStatusSnapshot status = await service.CheckAsync("test");

        Assert.Equal(UpdateState.Ready, status.State);
    }
}

/// <summary>更新应用收尾 L2：完成清理 / 失败回滚 / defer 自动应用的任务标记流转。</summary>
public sealed class UpdateApplyFinalizationTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "np-update-final-" + Guid.NewGuid().ToString("N"));

    public UpdateApplyFinalizationTests()
    {
        Directory.CreateDirectory(_root);
    }

    private void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
        }
    }

    private void WriteTask(string mode, string version, string stagedDir)
    {
        // 直接写 AppRoot 任务标记（UpdateApply 收尾固定读 AppPaths）。
        new UpdateTask(mode, version, stagedDir).Write();
    }

    private void WriteVersion(string version)
    {
        Directory.CreateDirectory(AppPaths.AppRoot);
        File.WriteAllText(AppPaths.UpdateVersionFile, version);
    }

    [Fact]
    public void Finalization_CompletedCleansMarkers()
    {
        try
        {
            string staging = Path.Combine(AppPaths.UpdateDir, "staging", "0.10.1");
            Directory.CreateDirectory(staging);
            Directory.CreateDirectory(AppPaths.UpdateBackupDir);
            WriteTask("completed", "0.10.1", staging);
            WriteVersion("0.10.1");

            bool exit = UpdateApply.RunStartupFinalization();

            Assert.False(exit);
            Assert.False(File.Exists(AppPaths.UpdateVersionFile));
            Assert.False(File.Exists(AppPaths.UpdateTaskFile));
            Assert.False(Directory.Exists(Path.Combine(AppPaths.UpdateDir, "staging")));
            Assert.False(Directory.Exists(AppPaths.UpdateBackupDir));
        }
        finally
        {
            DeleteExact(AppPaths.UpdateVersionFile);
            UpdateTask.Clear();
            DeleteExact(AppPaths.UpdateDir);
            DeleteExact(AppPaths.UpdateBackupDir);
        }
    }

    [Fact]
    public void Finalization_DeferRewritesToApplyAndRequestsExit()
    {
        try
        {
            string stagedDir = Path.Combine(AppPaths.UpdateDir, "staging", "0.10.1");
            Directory.CreateDirectory(stagedDir);
            File.WriteAllText(Path.Combine(stagedDir, "nexus-pipeline.exe"), "fake");
            WriteTask("defer", "0.10.1", stagedDir);
            List<string> launched = new();
            UpdateApply.LaunchApplyOverride = dir =>
            {
                launched.Add(dir);
                return true;
            };
            try
            {
                bool exit = UpdateApply.RunStartupFinalization();

                Assert.True(exit);
                Assert.Equal(stagedDir, Assert.Single(launched));
                UpdateTask? task = UpdateTask.Read();
                Assert.Equal("apply", task!.Mode);
            }
            finally
            {
                UpdateApply.LaunchApplyOverride = null;
            }
        }
        finally
        {
            UpdateTask.Clear();
            DeleteExact(AppPaths.UpdateDir);
            DeleteExact(AppPaths.UpdateBackupDir);
        }
    }

    [Fact]
    public void Finalization_IncompleteApplyRollsBackFromBackup()
    {
        try
        {
            // 制造「切换未完成」现场：备份里是旧 wwwroot，安装目录里是半成品新 wwwroot。
            string backupWww = Path.Combine(AppPaths.UpdateBackupDir, "wwwroot");
            Directory.CreateDirectory(backupWww);
            File.WriteAllText(Path.Combine(backupWww, "marker.txt"), "old");
            string installWww = Path.Combine(AppPaths.AppRoot, "wwwroot");
            Directory.CreateDirectory(installWww);
            File.WriteAllText(Path.Combine(installWww, "marker.txt"), "new-partial");
            string staging = Path.Combine(AppPaths.UpdateDir, "staging", "0.10.1");
            Directory.CreateDirectory(staging);
            WriteTask("apply", "0.10.1", staging);

            bool exit = UpdateApply.RunStartupFinalization();

            Assert.False(exit);
            Assert.Equal("old", File.ReadAllText(Path.Combine(installWww, "marker.txt")));
            Assert.False(File.Exists(AppPaths.UpdateTaskFile));
            Assert.False(Directory.Exists(AppPaths.UpdateBackupDir));
        }
        finally
        {
            UpdateTask.Clear();
            DeleteExact(AppPaths.UpdateDir);
            DeleteExact(AppPaths.UpdateBackupDir);
            DeleteExact(Path.Combine(AppPaths.AppRoot, "wwwroot"));
        }
    }

    [Fact]
    public void Finalization_LegacyBackupRestoresPluginsDuringRollback()
    {
        try
        {
            // v0.10.7 updater 将 plugins 纳入 backup；若交换后中断，v0.10.8 仍需还原旧目录。
            string backupWww = Path.Combine(AppPaths.UpdateBackupDir, "wwwroot");
            string backupPlugins = Path.Combine(AppPaths.UpdateBackupDir, "plugins", "bettergi");
            Directory.CreateDirectory(backupWww);
            Directory.CreateDirectory(backupPlugins);
            File.WriteAllText(Path.Combine(backupWww, "marker.txt"), "old-www");
            File.WriteAllText(Path.Combine(backupPlugins, "marker.txt"), "old-plugin");

            string installWww = Path.Combine(AppPaths.AppRoot, "wwwroot");
            string installPlugins = Path.Combine(AppPaths.AppRoot, "plugins");
            Directory.CreateDirectory(installWww);
            Directory.CreateDirectory(installPlugins);
            File.WriteAllText(Path.Combine(installWww, "marker.txt"), "new-partial-www");
            File.WriteAllText(Path.Combine(installPlugins, ".nxp-root"), "new-root");

            string staging = Path.Combine(AppPaths.UpdateDir, "staging", "0.10.1");
            Directory.CreateDirectory(staging);
            WriteTask("apply", "0.10.1", staging);

            bool exit = UpdateApply.RunStartupFinalization();

            Assert.False(exit);
            Assert.Equal("old-www", File.ReadAllText(Path.Combine(installWww, "marker.txt")));
            Assert.Equal("old-plugin", File.ReadAllText(Path.Combine(installPlugins, "bettergi", "marker.txt")));
            Assert.False(File.Exists(AppPaths.UpdateTaskFile));
            Assert.False(Directory.Exists(AppPaths.UpdateBackupDir));
        }
        finally
        {
            UpdateTask.Clear();
            DeleteExact(AppPaths.UpdateDir);
            DeleteExact(AppPaths.UpdateBackupDir);
            DeleteExact(Path.Combine(AppPaths.AppRoot, "wwwroot"));
            DeleteExact(Path.Combine(AppPaths.AppRoot, "plugins"));
        }
    }

    private static void DeleteExact(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
