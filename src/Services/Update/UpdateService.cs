using System.Text.Json.Nodes;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Services.Execution;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services.Update;

/// <summary>更新状态机。每个网络操作带 generation 与 CTS，过期 worker 不得修改当前状态或清理新操作现场。</summary>
internal enum UpdateState
{
    Idle,
    Checking,
    Downloading,
    Ready,
    ApplyPending,
    Applying,
    RecoveryPending,
}

/// <summary>
/// 内建更新服务：检查、下载校验、申请应用（立即/下次启动）。
/// Immediate Apply 使用 Host Maintenance Lease 原子冻结宿主准入；Defer Apply 只记录 journal，不要求当前空闲。
/// </summary>
internal sealed class UpdateService
{
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
    })
    {
        Timeout = TimeSpan.FromMinutes(10),
    };

    private readonly Func<AppSettings> _settings;
    private readonly string _installDir;
    private readonly Func<bool> _canApplyFallback;
    private readonly Func<bool> _requestExit;
    private readonly Func<(HostMaintenanceLease? Lease, string? Reason)> _acquireMaintenance;
    private readonly object _gate = new();

    private UpdateState _state = UpdateState.Idle;
    private ReleaseInfo? _latest;
    private string _error = "";
    private long _bytesRead;
    private long _bytesTotal;
    private long _generation;
    private UpdateOperation? _operation;
    private string? _readyStagingDir;
    private HostMaintenanceLease? _maintenanceLease;

    public UpdateService(
        Func<AppSettings> settings,
        string installDir,
        Func<bool> canApply,
        Func<bool> requestExit,
        Func<(HostMaintenanceLease? Lease, string? Reason)>? acquireMaintenance = null)
    {
        _settings = settings;
        _installDir = installDir;
        _canApplyFallback = canApply;
        _requestExit = requestExit;
        if (acquireMaintenance is not null)
        {
            _acquireMaintenance = acquireMaintenance;
        }
        else
        {
            _acquireMaintenance = () => _canApplyFallback()
                ? (new HostMaintenanceLease(), (string?)null)
                : ((HostMaintenanceLease?)null, "存在活动运行、编辑会话或待执行系统操作，暂不能应用更新");
        }
        _state = HasRecoveryArtifacts() ? UpdateState.RecoveryPending : UpdateState.Idle;
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
            return BuildSnapshotLocked();
        }
    }

    /* ---------------- 安装目录内的更新工作目录（测试可注入 installDir；生产 = AppRoot） ---------------- */

    private string UpdateDir => Path.Combine(_installDir, ".nxp-update");

    private string TaskFile => Path.Combine(UpdateDir, "task.json");

    private string BackupDir => Path.Combine(_installDir, ".nxp-backup", "previous");

    private string StagingDir(string version, long generation) => Path.Combine(UpdateDir, "staging", $"{version}.g{generation}");

    private bool HasRecoveryArtifacts()
    {
        return File.Exists(TaskFile)
            || Directory.Exists(BackupDir)
            || File.Exists(BackupDir)
            || File.Exists(Path.Combine(_installDir, ".nxp-version"));
    }

    /// <summary>检查更新：只允许 Idle 开始；Ready、ApplyPending、Applying 等状态不会被覆盖。</summary>
    public async Task<UpdateStatusSnapshot> CheckAsync(string auditSource)
    {
        UpdateOperation operation;
        lock (_gate)
        {
            if (_state != UpdateState.Idle)
            {
                return BuildSnapshotLocked();
            }
            operation = BeginOperationLocked(UpdateState.Checking);
            _readyStagingDir = null;
            _error = "";
        }

        try
        {
            AppSettings settings = _settings();
            string source = EffectiveSourceUrl(settings);
            string? validationError = UpdateCatalog.ValidateSource(source);
            if (validationError is not null)
            {
                throw new InvalidDataException(validationError);
            }
            UpdateSourcePolicy policy = new(source);
            string channel = EffectiveChannel(settings);
            (int, int, int) current = ParseCurrent();
            using HttpResponseMessage response = await policy.GetAsync(
                Http,
                policy.SourceUri,
                manifest: true,
                "NexusPipeline-update/" + CurrentVersion,
                operation.Cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new IOException($"清单请求失败：HTTP {(int)response.StatusCode}");
            }
            string json = await response.Content.ReadAsStringAsync(operation.Cts.Token).ConfigureAwait(false);
            operation.Cts.Token.ThrowIfCancellationRequested();
            ReleaseInfo? best = UpdateCatalog.PickRelease(JsonNode.Parse(json), channel, current);
            lock (_gate)
            {
                if (!IsCurrentLocked(operation))
                {
                    return BuildSnapshotLocked();
                }
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
        catch (OperationCanceledException) when (operation.Cts.IsCancellationRequested)
        {
            // CancelDownload 已经把当前 operation 转为 Idle；过期完成不再触碰新 operation。
        }
        catch (Exception ex)
        {
            if (FailOperation(operation, ex.Message))
            {
                Audit.Log(auditSource, "检查更新失败", ex.Message);
            }
        }
        finally
        {
            CompleteOperation(operation, UpdateState.Checking);
        }
        return GetStatus();
    }

    /// <summary>开始下载并校验到 staging（后台任务，进度经 GetStatus 轮询）。</summary>
    public string? StartDownload(string auditSource)
    {
        ReleaseInfo? latest;
        UpdateOperation operation;
        string version;
        string stagingDir;
        string zipPath;
        string shaPath;
        lock (_gate)
        {
            if (_state != UpdateState.Idle)
            {
                return "已有更新操作进行中";
            }
            latest = _latest;
            if (latest is null)
            {
                return "尚未检查到可用更新";
            }
            if (File.Exists(TaskFile))
            {
                return "已有更新事务待处理，请先完成启动恢复";
            }
            if (Directory.Exists(BackupDir) || File.Exists(BackupDir))
            {
                return "检测到未恢复的更新 backup，请先完成启动恢复";
            }
            version = latest.VersionText;
            long nextGeneration = checked(_generation + 1);
            stagingDir = StagingDir(version, nextGeneration);
            zipPath = Path.Combine(UpdateDir, AppPaths.UpdatePackageZipName(version) + $".g{nextGeneration}");
            shaPath = Path.Combine(UpdateDir, AppPaths.UpdatePackageShaName(version) + $".g{nextGeneration}");
            operation = BeginOperationLocked(UpdateState.Downloading, zipPath, shaPath, stagingDir);
            _readyStagingDir = null;
            _bytesRead = 0;
            _bytesTotal = 0;
            _error = "";
        }

        _ = Task.Run(async () =>
        {
            try
            {
                string source = EffectiveSourceUrl(_settings());
                string? sourceError = UpdateCatalog.ValidateSource(source);
                if (sourceError is not null)
                {
                    throw new InvalidDataException(sourceError);
                }
                UpdateSourcePolicy policy = new(source);
                var progress = new Progress<UpdateDownloadProgress>(value => UpdateProgress(operation, value));
                (bool ok, string? downloadError) = await UpdatePackage.DownloadAsync(
                    Http,
                    policy,
                    latest.ZipUrl,
                    latest.ShaUrl,
                    zipPath,
                    shaPath,
                    progress,
                    operation.Cts.Token).ConfigureAwait(false);
                operation.Cts.Token.ThrowIfCancellationRequested();
                if (!ok)
                {
                    throw new IOException(downloadError ?? "下载失败");
                }
                if (!UpdatePackage.VerifySha256(zipPath, shaPath, out string? verifyError))
                {
                    throw new IOException(verifyError ?? "SHA256 校验失败");
                }
                operation.Cts.Token.ThrowIfCancellationRequested();
                string? extractError = UpdatePackage.Extract(zipPath, stagingDir);
                operation.Cts.Token.ThrowIfCancellationRequested();
                if (extractError is not null)
                {
                    throw new IOException(extractError);
                }
                if (!TrySetReady(operation))
                {
                    return;
                }
                Audit.Log(auditSource, "更新下载完成", $"v{version}（SHA256 校验通过，已就绪）");
            }
            catch (OperationCanceledException) when (operation.Cts.IsCancellationRequested)
            {
                CleanupDownloadArtifacts(operation);
            }
            catch (Exception ex)
            {
                CleanupDownloadArtifacts(operation);
                if (FailOperation(operation, ex.Message))
                {
                    Audit.Log(auditSource, "更新下载失败", ex.Message);
                }
            }
            finally
            {
                CompleteOperation(operation, UpdateState.Downloading);
            }
        });
        Audit.Log(auditSource, "开始下载更新", $"v{version}（staging: {stagingDir}）");
        return null;
    }

    /// <summary>取消检查/下载。取消只释放当前状态，过期 worker 仍受 generation 和现场归属保护。</summary>
    public bool CancelDownload()
    {
        CancellationTokenSource? cts;
        UpdateOperation? operation;
        lock (_gate)
        {
            if (_state is not (UpdateState.Checking or UpdateState.Downloading) || _operation is null)
            {
                return false;
            }
            operation = _operation;
            cts = operation.Cts;
            _operation = null;
            _generation++;
            _state = UpdateState.Idle;
            _error = "已取消";
            _readyStagingDir = null;
            _bytesRead = 0;
            _bytesTotal = 0;
        }
        try
        {
            cts.Cancel();
        }
        catch
        {
        }
        Logger.Info($"[更新] 操作已取消（generation={operation.Generation}）。");
        return true;
    }

    /// <summary>
    /// 申请应用：Immediate 必须先取得维护租约并成功拉起 worker；Defer 只写入可恢复 journal，不要求当前空闲。
    /// </summary>
    public UpdateApplyResult RequestApply(bool defer, string auditSource)
    {
        ReleaseInfo? latest;
        string version;
        string stagingDir;
        lock (_gate)
        {
            latest = _latest;
            if (_state != UpdateState.Ready || latest is null)
            {
                return UpdateApplyResult.Busy("not-ready", "更新尚未就绪（请先检查并下载更新）");
            }
            if (File.Exists(TaskFile))
            {
                return UpdateApplyResult.Busy("transaction-pending", "已有更新事务待处理，请先完成启动恢复");
            }
            if (Directory.Exists(BackupDir) || File.Exists(BackupDir))
            {
                return UpdateApplyResult.Busy("recovery-pending", "检测到未恢复的更新 backup，请先完成启动恢复");
            }
            version = latest.VersionText;
            stagingDir = _readyStagingDir ?? "";
            if (string.IsNullOrWhiteSpace(stagingDir) || !File.Exists(Path.Combine(stagingDir, "nexus-pipeline.exe")))
            {
                return UpdateApplyResult.Busy("not-ready", "暂存文件不完整，请重新下载");
            }
        }

        if (defer)
        {
            try
            {
                lock (_gate)
                {
                    if (_state != UpdateState.Ready)
                    {
                        return UpdateApplyResult.Busy("busy", "更新状态已变化，请刷新后重试");
                    }
                    new UpdateTask("defer", version, stagingDir, UpdatePhase.Deferred, DateTimeOffset.UtcNow).Write(TaskFile);
                    _state = UpdateState.ApplyPending;
                }
                Audit.Log(auditSource, "申请下次启动更新", $"v{version}");
                Logger.Info($"[更新] 已登记「下次启动更新」（v{version}），退出后下次启动自动应用。");
                return UpdateApplyResult.Ok(true);
            }
            catch (Exception ex)
            {
                return UpdateApplyResult.Busy("journal-write-failed", $"登记下次启动更新失败：{ex.Message}");
            }
        }

        (HostMaintenanceLease? lease, string? leaseReason) = _acquireMaintenance();
        if (lease is null)
        {
            return UpdateApplyResult.Busy("busy", leaseReason ?? "宿主当前繁忙，暂不能应用更新");
        }
        lock (_gate)
        {
            if (_state != UpdateState.Ready)
            {
                lease.Dispose();
                return UpdateApplyResult.Busy("busy", "更新状态已变化，请刷新后重试");
            }
            _state = UpdateState.Applying;
            _maintenanceLease = lease;
        }

        bool workerLaunched = false;
        try
        {
            new UpdateTask("apply", version, stagingDir, UpdatePhase.ApplyRequested, DateTimeOffset.UtcNow).Write(TaskFile);
            if (!UpdateApply.LaunchApplyWorker(stagingDir))
            {
                throw new InvalidOperationException("apply-update 子进程未能拉起");
            }
            workerLaunched = true;
            Audit.Log(auditSource, "应用更新", $"v{version}（staging: {stagingDir}）");
            Logger.Info("[更新] apply-update 子进程已确认拉起，正在请求宿主退出。");
            bool exitRequested;
            try
            {
                exitRequested = _requestExit();
            }
            catch (Exception ex)
            {
                Logger.Warn($"[更新] 宿主退出请求抛出异常，但 apply-update 已拉起；保留 journal 与维护租约：{ex.Message}");
                exitRequested = false;
            }
            if (!exitRequested)
            {
                // 维护租约仍然有效；worker 会等待宿主稍后释放互斥体，当前宿主不会再准入新操作。
                Logger.Warn("[更新] 宿主退出请求未立即完成，更新事务与维护租约保留，等待下一次安全退出。");
            }
            return UpdateApplyResult.Ok(false);
        }
        catch (Exception ex)
        {
            Logger.Error($"[更新] 应用请求失败：{ex.Message}");
            if (workerLaunched)
            {
                // worker 已经 armed，任何后续通知/退出异常都不能删除它的 journal。
                return UpdateApplyResult.Ok(false);
            }
            lock (_gate)
            {
                if (_state == UpdateState.Applying)
                {
                    _state = UpdateState.Ready;
                }
                _maintenanceLease = null;
            }
            lease.Dispose();
            UpdateTask.Clear(TaskFile);
            return UpdateApplyResult.Busy("worker-launch-failed", $"无法启动更新切换：{ex.Message}");
        }
    }

    private UpdateOperation BeginOperationLocked(
        UpdateState state,
        string? zipPath = null,
        string? shaPath = null,
        string? stagingDir = null)
    {
        _generation++;
        _operation = new UpdateOperation(_generation, state, zipPath, shaPath, stagingDir);
        _state = state;
        return _operation;
    }

    private bool IsCurrentLocked(UpdateOperation operation)
    {
        return ReferenceEquals(_operation, operation) && _generation == operation.Generation;
    }

    private void UpdateProgress(UpdateOperation operation, UpdateDownloadProgress progress)
    {
        lock (_gate)
        {
            if (!IsCurrentLocked(operation) || _state != UpdateState.Downloading)
            {
                return;
            }
            _bytesRead = progress.BytesRead;
            _bytesTotal = progress.BytesTotal;
        }
    }

    private bool TrySetReady(UpdateOperation operation)
    {
        lock (_gate)
        {
            if (!IsCurrentLocked(operation) || _state != UpdateState.Downloading)
            {
                return false;
            }
            _state = UpdateState.Ready;
            _readyStagingDir = operation.StagingDir;
            _bytesRead = 0;
            _bytesTotal = 0;
            return true;
        }
    }

    private bool FailOperation(UpdateOperation operation, string message)
    {
        lock (_gate)
        {
            if (!IsCurrentLocked(operation))
            {
                return false;
            }
            _error = message;
            _latest = null;
            _state = UpdateState.Idle;
            _readyStagingDir = null;
            _bytesRead = 0;
            _bytesTotal = 0;
            return true;
        }
    }

    private void CompleteOperation(UpdateOperation operation, UpdateState expectedState)
    {
        lock (_gate)
        {
            if (!IsCurrentLocked(operation))
            {
                operation.Cts.Dispose();
                return;
            }
            if (_state == expectedState)
            {
                _state = UpdateState.Idle;
            }
            _operation = null;
            operation.Cts.Dispose();
        }
    }

    private void CleanupDownloadArtifacts(UpdateOperation operation)
    {
        lock (_gate)
        {
            // 每个 generation 使用独立 ZIP/SHA/staging 路径；持有状态锁让取消、启动和清理的归属判断保持一致。
            TryDeleteFile(operation.ZipPath);
            TryDeleteFile(operation.ShaPath);
            TryDeleteDirectory(operation.StagingDir);
        }
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[更新] 清理下载文件失败（{path}）：{ex.Message}");
        }
    }

    private static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[更新] 清理 staging 失败（{path}）：{ex.Message}");
        }
    }

    private (int Major, int Minor, int Patch) ParseCurrent()
    {
        return UpdateCatalog.TryParseTag(CurrentVersion, out (int Major, int Minor, int Patch) version)
            ? version
            : (0, 0, 0);
    }

    private UpdateStatusSnapshot BuildSnapshotLocked()
    {
        return new UpdateStatusSnapshot(
            _state,
            _state == UpdateState.Downloading ? BytesToPercent(_bytesRead, _bytesTotal) : null,
            _bytesRead,
            _bytesTotal,
            _error,
            CurrentVersion,
            _latest?.VersionText,
            _latest?.Prerelease,
            EffectiveChannel(_settings()),
            _latest is not null,
            _latest?.Notes ?? "");
    }

    private static int? BytesToPercent(long bytesRead, long bytesTotal)
    {
        return bytesTotal > 0 ? (int)Math.Clamp(bytesRead * 100 / bytesTotal, 0, 100) : null;
    }

    private sealed class UpdateOperation
    {
        public long Generation { get; }
        public UpdateState State { get; }
        public string? ZipPath { get; }
        public string? ShaPath { get; }
        public string? StagingDir { get; }
        public CancellationTokenSource Cts { get; } = new();

        public UpdateOperation(long generation, UpdateState state, string? zipPath, string? shaPath, string? stagingDir)
        {
            Generation = generation;
            State = state;
            ZipPath = zipPath;
            ShaPath = shaPath;
            StagingDir = stagingDir;
        }
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
    bool? LatestPrerelease,
    string Channel,
    bool Available,
    string Notes);

/// <summary>应用请求结果：Succeeded=true 表示已受理（Deferred 区分立即/下次启动）。</summary>
internal sealed record UpdateApplyResult(bool Succeeded, bool Deferred, string? Code, string? Error)
{
    public static UpdateApplyResult Ok(bool deferred) => new(true, deferred, null, null);

    public static UpdateApplyResult Busy(string code, string error) => new(false, false, code, error);
}
