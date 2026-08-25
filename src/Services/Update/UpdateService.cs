using System.Text.Json.Nodes;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services.Update;

/// <summary>更新状态机状态：Idle → Checking → Idle；Idle → Downloading → Ready → Applying →（进程退出）；
/// 下载失败回 Idle（error 可读）；一次只允许一个操作，取消仅作用于 Checking/Downloading。</summary>
internal enum UpdateState
{
    Idle,
    Checking,
    Downloading,
    Ready,
    Applying,
}

/// <summary>
/// 内建更新服务：检查更新、下载校验、申请应用（立即/下次启动）。只自动检查，绝不自动下载/应用；
/// 下载与应用均为显式操作；应用前与「退出/重启」同一套安全门禁。单操作互斥经 _gate 收敛。
/// </summary>
internal sealed class UpdateService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(10),
    };

    private readonly Func<AppSettings> _settings;
    private readonly string _installDir;
    private readonly Func<bool> _canApply;
    private readonly Func<bool> _requestExit;
    private readonly object _gate = new();

    private UpdateState _state = UpdateState.Idle;
    private ReleaseInfo? _latest;
    private string _error = "";
    private long _bytesRead;
    private long _bytesTotal;
    private CancellationTokenSource? _downloadCts;
    private Task? _downloadTask;

    public UpdateService(
        Func<AppSettings> settings,
        string installDir,
        Func<bool> canApply,
        Func<bool> requestExit)
    {
        _settings = settings;
        _installDir = installDir;
        _canApply = canApply;
        _requestExit = requestExit;
    }

    public UpdateState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public ReleaseInfo? Latest
    {
        get
        {
            lock (_gate)
            {
                return _latest;
            }
        }
    }

    /// <summary>进程数版本（与 /api/status 同源）。</summary>
    public static string CurrentVersion => typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static string EffectiveChannel(AppSettings settings)
    {
        return settings.UpdateChannel is "stable" or "prerelease" ? settings.UpdateChannel : "prerelease";
    }

    /// <summary>当前有效更新源（测试环境变量覆盖 > 设置镜像源 > 默认 GitHub）。</summary>
    public static string EffectiveSourceUrl(AppSettings settings)
    {
        return TestHooks.UpdateSourceUrl ?? settings.UpdateSourceUrl ?? "";
    }

    public UpdateStatusSnapshot GetStatus()
    {
        lock (_gate)
        {
            return new UpdateStatusSnapshot(
                _state,
                _state == UpdateState.Downloading ? BytesToPercent(_bytesRead, _bytesTotal) : null,
                _bytesRead,
                _bytesTotal,
                _error,
                CurrentVersion,
                _latest?.VersionText,
                EffectiveChannel(_settings()),
                _latest is not null,
                _latest?.Notes ?? "");
        }
    }

    /* ---------------- 安装目录内的更新工作目录（测试可注入 installDir；生产 = AppRoot） ---------------- */

    private string UpdateDir => Path.Combine(_installDir, ".nxp-update");

    private string TaskFile => Path.Combine(UpdateDir, "task.json");

    private string StagingDir(string version) => Path.Combine(UpdateDir, "staging", version);

    /// <summary>检查更新：拉取清单、比较版本、构造 UpdateInfo；结束回到 Idle（error 可读）。</summary>
    public async Task<UpdateStatusSnapshot> CheckAsync(string auditSource)
    {
        UpdateStatusSnapshot snapshot;
        lock (_gate)
        {
            if (_state is UpdateState.Checking or UpdateState.Downloading)
            {
                return GetStatus();
            }
            _state = UpdateState.Checking;
            _error = "";
        }
        try
        {
            AppSettings settings = _settings();
            string source = EffectiveSourceUrl(settings);
            string? validationError = UpdateCatalog.ValidateSource(source);
            if (validationError is not null)
            {
                Fail(validationError);
                Audit.Log(auditSource, "检查更新失败", validationError);
            }
            else
            {
                string channel = EffectiveChannel(settings);
                (int, int, int) current = ParseCurrent();
                ReleaseInfo? best;
                using (var request = new HttpRequestMessage(HttpMethod.Get, string.IsNullOrWhiteSpace(source) ? UpdateCatalog.DefaultSourceUrl : source))
                {
                    request.Headers.TryAddWithoutValidation("User-Agent", "NexusPipeline-update/" + CurrentVersion);
                    request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
                    using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new IOException($"清单请求失败：HTTP {(int)response.StatusCode}");
                    }
                    string json = await response.Content.ReadAsStringAsync(CancellationToken.None).ConfigureAwait(false);
                    best = UpdateCatalog.PickRelease(JsonNode.Parse(json), channel, current);
                }
                lock (_gate)
                {
                    _latest = best;
                }
                Audit.Log(auditSource, best is null ? "检查更新" : "发现新版本", best is null
                    ? $"当前 v{CurrentVersion}，渠道 {channel}，无可用更新"
                    : $"v{CurrentVersion} → v{best.VersionText}（渠道 {channel}）");
                if (best is not null)
                {
                    Logger.Info($"[更新] 发现新版本：v{CurrentVersion} → v{best.VersionText}（{best.Name}）");
                }
            }
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
            Audit.Log(auditSource, "检查更新失败", ex.Message);
        }
        finally
        {
            lock (_gate)
            {
                if (_state == UpdateState.Checking)
                {
                    _state = UpdateState.Idle;
                }
                snapshot = new UpdateStatusSnapshot(
                    _state,
                    null,
                    0,
                    0,
                    _error,
                    CurrentVersion,
                    _latest?.VersionText,
                    EffectiveChannel(_settings()),
                    _latest is not null,
                    _latest?.Notes ?? "");
            }
        }
        return snapshot;
    }

    /// <summary>开始下载并校验到 staging（后台任务，进度经 GetStatus 轮询）。返回 null=已开始，否则拒绝原因。</summary>
    public string? StartDownload(string auditSource)
    {
        ReleaseInfo? latest;
        lock (_gate)
        {
            if (_state is UpdateState.Checking or UpdateState.Downloading or UpdateState.Applying)
            {
                return "已有更新操作进行中";
            }
            latest = _latest;
            if (latest is null)
            {
                return "尚未检查到可用更新";
            }
            _state = UpdateState.Downloading;
            _bytesRead = 0;
            _bytesTotal = 0;
            _error = "";
            _downloadCts = new CancellationTokenSource();
        }
        string version = latest.VersionText;
        string stagingDir = StagingDir(version);
        string zipPath = Path.Combine(UpdateDir, AppPaths.UpdatePackageZipName(version));
        string shaPath = Path.Combine(UpdateDir, AppPaths.UpdatePackageShaName(version));
        CancellationToken token = _downloadCts.Token;
        _downloadTask = Task.Run(async () =>
        {
            try
            {
                string source = EffectiveSourceUrl(_settings());
                (bool ok, string? downloadError) = await UpdatePackage.DownloadAsync(
                    Http, source, latest.ZipUrl, latest.ShaUrl, zipPath, shaPath, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (!ok)
                {
                    throw new IOException(downloadError ?? "下载失败");
                }
                if (!UpdatePackage.VerifySha256(zipPath, shaPath, out string? verifyError))
                {
                    throw new IOException(verifyError ?? "SHA256 校验失败");
                }
                token.ThrowIfCancellationRequested();
                string? extractError = UpdatePackage.Extract(zipPath, stagingDir);
                token.ThrowIfCancellationRequested();
                if (extractError is not null)
                {
                    throw new IOException(extractError);
                }
                Audit.Log(auditSource, "更新下载完成", $"v{version}（SHA256 校验通过，已就绪）");
                lock (_gate)
                {
                    _state = UpdateState.Ready;
                }
            }
            catch (OperationCanceledException)
            {
                // 取消：静默回到 Idle（由 CancelDownload 处理状态，这里只清理现场）。
                CleanupDownloadArtifacts();
            }
            catch (Exception ex)
            {
                CleanupDownloadArtifacts();
                Fail(ex.Message);
                Audit.Log(auditSource, "更新下载失败", ex.Message);
            }
            finally
            {
                lock (_gate)
                {
                    if (_state == UpdateState.Downloading)
                    {
                        _state = UpdateState.Idle;
                    }
                }
            }
        });
        Audit.Log(auditSource, "开始下载更新", $"v{version}（staging: {stagingDir}）");
        return null;
    }

    /// <summary>取消下载/清理 staging（仅 Checking/Downloading 有效）。</summary>
    public bool CancelDownload()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (_state is not (UpdateState.Checking or UpdateState.Downloading))
            {
                return false;
            }
            cts = _downloadCts;
            _downloadCts = null;
            _state = UpdateState.Idle;
            _error = "已取消";
        }
        try
        {
            cts?.Cancel();
        }
        catch
        {
        }
        Logger.Info("[更新] 下载已取消。");
        return true;
    }

    /// <summary>申请应用：立即（defer=false，门禁通过后拉起 apply-update 并请求宿主退出）或下次启动（defer=true）。
    /// 返回 InvokeResult.Error 时带结构化错误码供 API 映射。</summary>
    public UpdateApplyResult RequestApply(bool defer, string auditSource)
    {
        ReleaseInfo? latest;
        string error;
        lock (_gate)
        {
            latest = _latest;
            if (_state != UpdateState.Ready || latest is null)
            {
                error = "更新尚未就绪（请先检查并下载更新）";
                return UpdateApplyResult.Busy("not-ready", error);
            }
        }
        if (!_canApply())
        {
            error = "存在活动运行、编辑会话或待执行系统操作，暂不能应用更新";
            return UpdateApplyResult.Busy("busy", error);
        }
        string version = latest.VersionText;
        string stagingDir = StagingDir(version);
        if (!File.Exists(Path.Combine(stagingDir, "nexus-pipeline.exe")))
        {
            error = "暂存文件不完整，请重新下载";
            return UpdateApplyResult.Busy("not-ready", error);
        }
        if (!defer)
        {
            lock (_gate)
            {
                _state = UpdateState.Applying;
            }
            new UpdateTask("apply", version, stagingDir).Write(TaskFile);
            Audit.Log(auditSource, "应用更新", $"v{version}（staging: {stagingDir}）");
            // 先响应请求再执行切换：拉起子进程并请求宿主退出（退出由组合根注入的端口执行）。
            _ = Task.Run(() =>
            {
                try
                {
                    Thread.Sleep(TestHooks.ScaledMs(1000));
                    UpdateApply.LaunchApplyWorker(stagingDir);
                    Logger.Info("[更新] 已拉起 apply-update 子进程，即将退出当前进程。");
                }
                catch (Exception ex)
                {
                    Logger.Error($"[更新] 拉起 apply-update 子进程失败：{ex.Message}");
                    return;
                }
                try
                {
                    if (!_requestExit())
                    {
                        Logger.Warn("[更新] 当前进程仍有活动执行或编辑会话，已拒绝退出；新进程可能需要手动处理。");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"[更新] 退出当前进程失败：{ex.Message}");
                    try
                    {
                        Environment.Exit(0);
                    }
                    catch
                    {
                    }
                }
            });
            return UpdateApplyResult.Ok(false);
        }
        new UpdateTask("defer", version, stagingDir).Write(TaskFile);
        Audit.Log(auditSource, "申请下次启动更新", $"v{version}");
        Logger.Info($"[更新] 已登记「下次启动更新」（v{version}），退出后下次启动自动应用。");
        return UpdateApplyResult.Ok(true);
    }

    private void Fail(string message)
    {
        lock (_gate)
        {
            _error = message;
            _latest = null;
            if (_state == UpdateState.Downloading)
            {
                _state = UpdateState.Idle;
            }
            _bytesRead = 0;
            _bytesTotal = 0;
        }
    }

    private void CleanupDownloadArtifacts()
    {
        try
        {
            string dir = UpdateDir;
            if (Directory.Exists(dir))
            {
                foreach (string file in Directory.GetFiles(dir))
                {
                    string name = Path.GetFileName(file);
                    if (name.StartsWith("NexusPipeline-v", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith(".zip.sha256", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            File.Delete(file);
                        }
                        catch
                        {
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[更新] 清理下载残留失败：{ex.Message}");
        }
        try
        {
            string staging = Path.Combine(UpdateDir, "staging");
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[更新] 清理 staging 失败：{ex.Message}");
        }
    }

    private (int Major, int Minor, int Patch) ParseCurrent()
    {
        return UpdateCatalog.TryParseTag(CurrentVersion, out (int Major, int Minor, int Patch) version)
            ? version
            : (0, 0, 0);
    }

    private static int? BytesToPercent(long bytesRead, long bytesTotal)
    {
        return bytesTotal > 0 ? (int)Math.Clamp(bytesRead * 100 / bytesTotal, 0, 100) : null;
    }
}

/// <summary>更新状态快照（Web API 返回 camelCase 由匿名对象投影；本类型供内部/CLI 使用）。</summary>
internal sealed record UpdateStatusSnapshot(
    UpdateState State,
    int? Progress,
    long BytesRead,
    long BytesTotal,
    string Error,
    string Current,
    string? Latest,
    string Channel,
    bool Available,
    string Notes);

/// <summary>应用请求结果：Succeeded=true 表示已受理（Deferred 区分立即/下次启动）；busy/not-ready 为门禁拒绝。</summary>
internal sealed record UpdateApplyResult(bool Succeeded, bool Deferred, string? Code, string? Error)
{
    public static UpdateApplyResult Ok(bool deferred) => new(true, deferred, null, null);

    public static UpdateApplyResult Busy(string code, string error) => new(false, false, code, error);
}