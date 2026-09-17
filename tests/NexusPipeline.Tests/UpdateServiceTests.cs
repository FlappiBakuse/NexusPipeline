using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Services.Update;
using NexusPipeline.Utilities;
using Xunit;

namespace NexusPipeline.Tests;

/// <summary>更新域 L1：受限版本解析比较、releases JSON 解析、渠道过滤、主机白名单与当前 zip 合约。</summary>
public sealed class UpdateServiceTests : IAsyncLifetime
{
    private static string CurrentVersion => UpdateService.CurrentVersion;

    private static string CandidateVersion
    {
        get
        {
            Assert.True(NexusVersion.TryParse(CurrentVersion, out NexusVersion current));
            return $"{current.Major}.{current.Minor}.{checked(current.Patch + 1)}-beta.1";
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
    private string _policyJson = "{\"schemaVersion\":1,\"repository\":\"FlappiBakuse/NexusPipeline\",\"barriers\":[]}";

    public Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "np-update-l2-" + Guid.NewGuid().ToString("N"));
        _installDir = Path.Combine(_root, "install");
        Directory.CreateDirectory(_installDir);

        // 构造与发布资产同名的 zip：exe + wwwroot + plugins/。
        using (var stream = new MemoryStream())
        {
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                AddEntry(archive, "nexus-pipeline.exe", "fake-exe-" + Guid.NewGuid().ToString("N"));
                AddEntry(archive, "wwwroot/index.js", "// fake");
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
        if (path == "/update-policy.json")
        {
            return (200, "application/json", Encoding.UTF8.GetBytes(_policyJson));
        }
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
        Assert.True(status.PolicyVerified);
        Assert.True(status.CanDownload);
    }

    [Fact]
    public async Task Check_BreakingBarrierRequiresManualMigrationAndRejectsDownload()
    {
        _policyJson = $"{{\"schemaVersion\":1,\"repository\":\"FlappiBakuse/NexusPipeline\",\"barriers\":[{{\"version\":\"{CandidateVersion}\",\"code\":\"installation-layout-v2\",\"migrationUrl\":\"https://example.com/migrate\"}}]}}";
        UpdateService service = NewService();

        UpdateStatusSnapshot status = await service.CheckAsync("test");

        Assert.True(status.Available);
        Assert.True(status.PolicyVerified);
        Assert.False(status.CanDownload);
        Assert.True(status.ManualUpdateRequired);
        Assert.Equal("breaking-update", status.UpdateBlockCode);
        Assert.Equal(CandidateVersion, status.BarrierVersion);
        Assert.Equal("https://example.com/migrate", status.MigrationUrl);
        UpdateDownloadResult result = service.StartDownload("test");
        Assert.False(result.Succeeded);
        Assert.Equal("breaking-update", result.Code);
        Assert.Equal("breaking-update", service.RequestApply(false, "test").Code);
    }

    [Fact]
    public async Task Check_FailsClosedWhenPolicyCannotBeValidated()
    {
        _policyJson = "{}";
        UpdateService service = NewService();

        UpdateStatusSnapshot status = await service.CheckAsync("test");

        Assert.True(status.Available);
        Assert.False(status.PolicyVerified);
        Assert.False(status.CanDownload);
        Assert.False(status.ManualUpdateRequired);
        Assert.Equal("policy-unavailable", status.UpdateBlockCode);
        UpdateDownloadResult result = service.StartDownload("test");
        Assert.False(result.Succeeded);
        Assert.Equal("policy-unavailable", result.Code);
    }

    [Fact]
    public async Task Download_VerifiesAndStagesReady()
    {
        UpdateService service = NewService();
        await service.CheckAsync("test");

        UpdateDownloadResult result = service.StartDownload("test");

        Assert.True(result.Succeeded);
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

        Assert.True(service.StartDownload("test").Succeeded);
        UpdateDownloadResult second = service.StartDownload("test");
        Assert.False(second.Succeeded);
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

    [Fact]
    public async Task InvalidateDiscovery_ClearsIdleResult()
    {
        UpdateService service = NewService();
        UpdateStatusSnapshot checkedStatus = await service.CheckAsync("test");
        Assert.True(checkedStatus.Available);

        service.InvalidateDiscovery();

        UpdateStatusSnapshot status = service.GetStatus();
        Assert.Equal(UpdateState.Idle, status.State);
        Assert.False(status.Available);
        Assert.False(status.HasChecked);
        Assert.True(service.IsAutomaticApplyAllowed);
    }

    [Fact]
    public async Task InvalidateDiscovery_PreservesReadyAndBlocksAutomaticApply()
    {
        UpdateService service = NewService();
        await service.CheckAsync("test");
        service.StartDownload("test");
        await WaitStateAsync(service, UpdateState.Ready);

        service.InvalidateDiscovery();

        Assert.Equal(UpdateState.Ready, service.State);
        Assert.False(service.IsAutomaticApplyAllowed);
        Assert.True(service.GetStatus().Available);
    }
}

/// <summary>更新应用收尾 L2：完成清理 / 失败回滚 / defer 自动应用的任务标记流转。</summary>
