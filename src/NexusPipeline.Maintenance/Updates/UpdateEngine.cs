using System.Text.Json.Nodes;
using NexusPipeline.Modules.Configuration.Recovery;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Storage;
using Logger = NexusPipeline.Maintenance.MaintenanceLog;
using Audit = NexusPipeline.Maintenance.MaintenanceAudit;
using NexusPipeline.Shared.Versioning;

namespace NexusPipeline.Modules.Updates;

/// <summary>更新状态机。每个网络操作带 generation 与 CTS，过期 worker 不得修改当前状态或清理新操作现场。</summary>
internal enum UpdateState
{
    Idle,
    Checking,
    Downloading,
    Cancelling,
    Ready,
    ApplyPending,
    Applying,
    RecoveryPending,
    ManualAdoptionPending,
}

/// <summary>
/// 内建更新服务：检查、下载校验、申请应用（立即/下次启动）。
/// 同一状态机由宿主准入 facade 和独立本地 session 使用；宿主业务生命周期留在 facade。
/// </summary>
internal class UpdateEngine
{
    private readonly Func<UpdateOptions> _settings;
    private readonly string _installDir;
    private readonly Func<string, string?, bool, UpdatePayloadIdentity> _freezePayload;
    private readonly UpdateApplyFailure? _lastApplyFailure;
    private readonly Func<Uri, TimeSpan, bool, HttpClient> _createClient;
    private readonly string _currentVersion;
    private readonly string? _installerToolsWarning;
    private readonly object _gate = new();

    private UpdateState _state = UpdateState.Idle;
    private ReleaseInfo? _latest;
    private string _error = "";
    private long _bytesRead;
    private long _bytesTotal;
    private long _generation;
    private UpdateOperation? _operation;
    private string? _readyStagingDir;
    private UpdatePayloadIdentity? _readyPayload;
    private string? _readyPackageHash;
    private string? _readyChecksumHash;
    private UpdateInventory? _readyInventory;
    private UpdateTransportReceipt? _readyTransportReceipt;
    private readonly bool _setupTransition;
    private readonly string? _sourceVersion;
    private bool _manualAdoptionRequired;
    private bool _hasChecked;
    private bool _discoveryInvalidationPending;
    private bool? _policyVerified;
    private bool _canDownload;
    private bool _manualUpdateRequired;
    private string? _updateBlockCode;
    private string? _barrierVersion;
    private string? _migrationUrl;
    private string? _policyError;
    private string? _pendingVersion;
    private string? _pendingStatus;
    private string? _pendingError;

    internal UpdateEngine(Func<UpdateOptions> settings, string currentVersion, string installDir,
        Func<Uri, TimeSpan, bool, HttpClient>? createClient = null,
        Func<string, string?, bool, UpdatePayloadIdentity>? freezePayload = null,
        bool setupTransition = false, string? sourceVersion = null)
    {
        _settings = settings; _currentVersion = currentVersion; _installDir = installDir;
        _setupTransition = setupTransition; _sourceVersion = sourceVersion;
        _freezePayload = freezePayload ?? ApplicationPayload.FreezeForUpdate;
        _createClient = createClient ?? ((_, timeout, redirects) => new HttpClient(new HttpClientHandler { AllowAutoRedirect = redirects }) { Timeout = timeout });
        _state = HasRecoveryArtifacts() ? UpdateState.RecoveryPending : UpdateState.Idle;
        if (_state == UpdateState.Idle && !setupTransition)
        {
            try { _manualAdoptionRequired = InstallationOwnership.PendingManualAdoption(installDir, _currentVersion) is not null; }
            catch (Exception error) when (error is IOException or System.Security.Cryptography.CryptographicException)
            { _manualAdoptionRequired = true; _error = error.Message; }
            if (_manualAdoptionRequired) _state = UpdateState.ManualAdoptionPending;
        }
        _lastApplyFailure = UpdateApplyFailure.ReadLatest(installDir, _currentVersion);
        try { if (InstallationOwnership.Read(installDir) is { SchemaVersion: 2 } owner) _installerToolsWarning = InstallationOwnership.InspectInstallerTools(owner); }
        catch (Exception error) when (error is IOException or System.Security.Cryptography.CryptographicException) { _installerToolsWarning = error.Message; }
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

    /// <summary>当前发现结果是否允许自动应用；策略未验证或跨越破坏性屏障时保持关闭。</summary>
    internal bool IsAutomaticApplyAllowed
    {
        get
        {
            lock (_gate)
            {
                return !_discoveryInvalidationPending
                    && (_latest is null || _policyVerified == true && _canDownload);
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

    public static string EffectiveChannel(UpdateOptions settings)
    {
        return settings.UpdateChannel is "stable" or "prerelease" ? settings.UpdateChannel : "prerelease";
    }

    /// <summary>当前有效更新源（测试环境变量覆盖 > 设置镜像源 > 默认 GitHub）。</summary>
    public static string EffectiveSourceUrl(UpdateOptions settings)
    {
        return settings.UpdateSourceUrl ?? "";
    }

    /// <summary>
    /// 启动对象图在更新收尾之前创建；收尾成功后重新确认恢复现场，避免已删除的 version marker
    /// 让本次安全启动永久停留在 RecoveryPending，导致更新后的宿主跳过下一次启动检查。
    /// </summary>
    internal void RefreshStartupRecoveryState()
    {
        lock (_gate)
        {
            if (_state == UpdateState.RecoveryPending && !HasRecoveryArtifacts())
            {
                _state = UpdateState.Idle;
                _error = "";
            }
        }
    }

    public UpdateStatusSnapshot GetStatus()
    {
        lock (_gate)
        {
            return BuildSnapshotLocked();
        }
    }

    /// <summary>控制面开始一次新的手动更新操作前清除上次启动尝试标记。</summary>
    internal void ClearStartupAttempt()
    {
        new StartupUpdateAttemptStore().Clear();
    }

    /// <summary>
    /// 标记当前发现结果需要重新验证。正在下载、已就绪或应用中的事务保留现场，
    /// 待事务回到 Idle 后再清除发现结果，避免留下无法继续应用的半成品状态。
    /// </summary>
    public void InvalidateDiscovery()
    {
        lock (_gate)
        {
            if (_state == UpdateState.Idle)
            {
                ClearDiscoveryLocked();
                return;
            }
            _discoveryInvalidationPending = true;
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
    public async Task<UpdateStatusSnapshot> CheckAsync(
        string auditSource,
        CancellationToken cancellationToken = default,
        bool reconcileManualAdoption = true)
    {
        UpdateOperation operation;
        lock (_gate)
        {
            if (_state is not (UpdateState.Idle or UpdateState.ManualAdoptionPending)
                || _manualAdoptionRequired && !reconcileManualAdoption)
            {
                return BuildSnapshotLocked();
            }
            operation = BeginOperationLocked(UpdateState.Checking, cancellationToken: cancellationToken);
            _readyStagingDir = null;
            _error = "";
            _latest = null;
            ClearPolicyLocked();
        }

        try
        {
            if (_manualAdoptionRequired) await ReconcileManualAdoptionAsync(operation).ConfigureAwait(false);
            UpdateOptions settings = _settings();
            string source = EffectiveSourceUrl(settings);
            string? validationError = UpdateCatalog.ValidateSource(source);
            if (validationError is not null)
            {
                throw new InvalidDataException(validationError);
            }
            UpdateSourcePolicy policy = new(source);
            string channel = EffectiveChannel(settings);
            NexusVersion current = ParseCurrent();
            using HttpClient http = _createClient(
                policy.SourceUri,
                TimeSpan.FromMinutes(10),
                false);
            UpdateDiscoveryResult discovery = await UpdateDiscovery.FetchAsync(http, policy, channel, current, operation.Cts.Token).ConfigureAwait(false);
            ReleaseInfo? best = discovery.Release;
            UpdatePolicyFetchResult policyResult = best is null
                ? new UpdatePolicyFetchResult(false, null, null)
                : await UpdatePolicy.FetchAsync(
                    policy,
                    http,
                    "NexusPipeline-update/" + _currentVersion,
                    Path.Combine(_installDir, ".nxp", "state"),
                    operation.Cts.Token).ConfigureAwait(false);
            UpdateBarrier? barrier = best is not null && policyResult.Verified
                ? UpdatePolicy.FindBarrier(policyResult.Policy!, current, best.Version)
                : null;
            lock (_gate)
            {
                if (!IsCurrentLocked(operation))
                {
                    return BuildSnapshotLocked();
                }
                _latest = best;
                _pendingVersion = discovery.PendingVersion;
                _pendingStatus = discovery.PendingStatus;
                _pendingError = discovery.PendingError;
                _policyVerified = best is null ? null : policyResult.Verified;
                _canDownload = best is not null && policyResult.Verified && barrier is null;
                _manualUpdateRequired = barrier is not null;
                _updateBlockCode = best is null
                    ? null
                    : policyResult.Verified
                        ? barrier is null ? null : "breaking-update"
                        : "policy-unavailable";
                _barrierVersion = barrier?.Version.ToString();
                _migrationUrl = barrier?.MigrationUrl;
                _policyError = policyResult.Verified ? null : policyResult.Error;
            }
            Audit.Log(auditSource, best is null ? "检查更新" : "发现新版本", best is null
                ? $"当前 v{_currentVersion}，渠道 {channel}，无可用更新"
                : $"v{_currentVersion} → v{best.VersionText}（渠道 {channel}）");
            if (best is not null)
            {
                Logger.Info($"[更新] 发现新版本：v{_currentVersion} → v{best.VersionText}（{best.Name}）");
                if (!policyResult.Verified)
                {
                    Logger.Warn($"[更新] 无法验证 update-policy.json，已禁止内置下载：{policyResult.Error}");
                }
                else if (barrier is not null)
                {
                    Logger.Warn($"[更新] v{best.VersionText} 跨越破坏性更新屏障 v{barrier.Version}，要求手动迁移。");
                }
            }
        }
        catch (OperationCanceledException) when (operation.Cts.IsCancellationRequested)
        {
            // The operation remains owned until its finally and cleanup complete.
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
            lock (_gate)
            {
                if (IsCurrentLocked(operation)) _hasChecked = true;
            }
            CompleteOperation(operation, UpdateState.Checking);
        }
        return GetStatus();
    }

    private async Task ReconcileManualAdoptionAsync(UpdateOperation operation)
    {
        using var lease = AcquireAdoptionAdmission();
        var owner = InstallationOwnership.PendingManualAdoption(_installDir, _currentVersion);
        if (owner is not null)
        {
            if (owner.SchemaVersion != 2) throw new IOException("installer.tools_migration_requires_setup");
            var policy = new UpdateSourcePolicy(null);
            using var http = _createClient(policy.SourceUri, TimeSpan.FromMinutes(10), false);
            string workspace = Path.Combine(_installDir, ".nxp", "state", "updates", "adoption-" + Guid.NewGuid().ToString("N"));
            var proof = await ManualAdoptionProof.VerifyAsync(http, _installDir, owner, _currentVersion, workspace, operation.Cts.Token).ConfigureAwait(false);
            lock (_gate)
            {
                operation.Cts.Token.ThrowIfCancellationRequested();
                if (!IsCurrentLocked(operation)) throw new OperationCanceledException();
                InstallationOwnership.CommitManualAdoption(_installDir, owner, proof);
                _manualAdoptionRequired = false;
            }
        }
        else lock (_gate) { if (IsCurrentLocked(operation)) _manualAdoptionRequired = false; }
    }

    /// <summary>开始下载并校验到 staging（后台任务，进度经 GetStatus 轮询）。</summary>
    public UpdateDownloadResult StartDownload(
        string auditSource,
        CancellationToken cancellationToken = default)
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
                return UpdateDownloadResult.Rejected("busy", "已有更新操作进行中");
            }
            latest = _latest;
            if (latest is null)
            {
                return UpdateDownloadResult.Rejected("not-available", "尚未检查到可用更新");
            }
            if (!_canDownload)
            {
                return _manualUpdateRequired
                    ? UpdateDownloadResult.Rejected("breaking-update", "当前更新跨越破坏性版本屏障，请手动下载最新安装包并迁移配置文件")
                    : UpdateDownloadResult.Rejected("policy-unavailable", "无法验证更新策略，暂不能使用内置更新");
            }
            if (File.Exists(TaskFile))
            {
                return UpdateDownloadResult.Rejected("transaction-pending", "已有更新事务待处理，请先完成启动恢复");
            }
            if (Directory.Exists(BackupDir) || File.Exists(BackupDir))
            {
                return UpdateDownloadResult.Rejected("recovery-pending", "检测到未恢复的更新 backup，请先完成启动恢复");
            }
            version = latest.VersionText;
            long nextGeneration = checked(_generation + 1);
            stagingDir = StagingDir(version, nextGeneration);
            zipPath = Path.Combine(UpdateDir, AppPaths.UpdatePackageZipName(version) + $".g{nextGeneration}");
            shaPath = Path.Combine(UpdateDir, AppPaths.UpdatePackageShaName(version) + $".g{nextGeneration}");
            operation = BeginOperationLocked(
                UpdateState.Downloading,
                zipPath,
                shaPath,
                stagingDir,
                cancellationToken);
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
                using HttpClient http = _createClient(
                    new Uri(latest.ZipUrl),
                    TimeSpan.FromMinutes(10),
                    false);
                var delivery = await UpdateDelivery.PrepareAsync(http, policy, latest, _installDir, _currentVersion,
                    zipPath, shaPath, stagingDir, _freezePayload, operation.Artifacts!, progress, operation.Cts.Token).ConfigureAwait(false);
                operation.Cts.Token.ThrowIfCancellationRequested();
                var frozenPayload = _freezePayload(stagingDir, version, false);
                string frozenPackageHash = operation.Artifacts!.FileHash(zipPath);
                var inventory = operation.Artifacts.Inventory(stagingDir);
                if (!TrySetReady(operation, frozenPayload, frozenPackageHash, inventory, operation.Artifacts.FileHash(shaPath), delivery.Receipt))
                {
                    CleanupDownloadArtifacts(operation);
                    return;
                }
                Audit.Log(auditSource, "更新下载完成", $"v{version}（{delivery.Transport}，下载 {delivery.NetworkBytes} 字节，复用 {delivery.ReusedBytes} 字节，{delivery.Reason}）");
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
        return UpdateDownloadResult.Started();
    }

    /// <summary>返回当前检查或下载任务的完成信号，供启动阶段在有限预算内等待。</summary>
    internal Task WaitForCurrentOperationAsync()
    {
        lock (_gate)
        {
            return _operation?.Completion.Task ?? Task.CompletedTask;
        }
    }

    public bool CancelDownload()
    {
        var (operation, requested) = CaptureCancellation();
        SignalCancellation(operation, requested);
        return requested;
    }

    internal async Task CancelAndWaitAsync()
    {
        var (operation, requested) = CaptureCancellation();
        SignalCancellation(operation, requested);
        if (operation is not null) await operation.Completion.Task.ConfigureAwait(false);
    }

    private (UpdateOperation? Operation, bool Requested) CaptureCancellation()
    {
        lock (_gate)
        {
            var operation = _operation;
            bool requested = operation is not null && _state is UpdateState.Checking or UpdateState.Downloading;
            if (requested)
            {
                _generation++;
                _state = UpdateState.Cancelling;
                _error = "正在取消并清理";
            }
            return (operation, requested);
        }
    }

    private static void SignalCancellation(UpdateOperation? operation, bool requested)
    {
        if (!requested) return;
        try { operation!.Cts.Cancel(); }
        catch (ObjectDisposedException) { /* The captured operation has already completed its finally. */ }
    }

    /// <summary>
    /// 申请应用：Immediate 必须先取得维护租约并成功拉起 worker；Defer 只写入可恢复 journal，不要求当前空闲。
    /// </summary>
    public UpdateApplyResult RequestApply(bool defer, string auditSource)
    {
        string verifiedVersion;
        string verifiedStaging;
        lock (_gate)
        {
            if (!TryGetReadyLocked(out verifiedVersion, out verifiedStaging, out UpdateApplyResult? failure))
            {
                return failure!;
            }
        }

        if (defer)
        {
            UpdateApplyResult? policyFailure = VerifyApplyTarget(verifiedVersion);
            if (policyFailure is not null) return policyFailure;
            string deferVersion;
            string deferStagingDir;
            try
            {
                lock (_gate)
                {
                    if (!TryGetReadyLocked(out deferVersion, out deferStagingDir, out UpdateApplyResult? failure))
                    {
                        return failure!;
                    }
                    if (deferVersion != verifiedVersion || deferStagingDir != verifiedStaging)
                        return UpdateApplyResult.Busy("update-changed", "待应用更新已变化，请重新申请");
                    FreezeJournal("defer", deferVersion, deferStagingDir).Write(TaskFile);
                    _state = UpdateState.ApplyPending;
                }
                Audit.Log(auditSource, "申请下次启动更新", $"v{deferVersion}");
                Logger.Info($"[更新] 已登记「下次启动更新」（v{deferVersion}），退出后下次启动自动应用。");
                return UpdateApplyResult.Ok(true);
            }
            catch (Exception ex)
            {
                return UpdateApplyResult.Busy("journal-write-failed", $"登记下次启动更新失败：{ex.Message}");
            }
        }

        var policyFailureNow = VerifyApplyTarget(verifiedVersion);
        if (policyFailureNow is not null) return policyFailureNow;
        lock (_gate)
        {
            if (!TryGetReadyLocked(out string readyVersion, out string readyStaging, out var failure)) return failure!;
            if (readyVersion != verifiedVersion || readyStaging != verifiedStaging) return UpdateApplyResult.Busy("update-changed", "待应用更新已变化，请重新申请");
            _state = UpdateState.Applying;
        }
        return StartImmediateApply(verifiedVersion, verifiedStaging, auditSource);
    }

    protected virtual IDisposable AcquireAdoptionAdmission() => new UpdateAdmissionLease();

    /// <summary>Explicit same-instance Setup handoff; all runtime admission uses the existing maintenance owner.</summary>
    internal UpdateApplyResult RequestInstallerApply(string stagingDir, string version, string imageHash, string transactionId, string auditSource, string packageHash)
    {
        if (!Guid.TryParseExact(transactionId, "N", out _) || packageHash.Length != 64 || !packageHash.All(char.IsAsciiHexDigitLower) || !NexusVersion.TryParse(version, out var target)
            || !NexusVersion.TryParse(_currentVersion, out var current) || target.CompareTo(current) < 0)
            return UpdateApplyResult.Busy("installer-input-invalid", "安装器版本或事务参数无效");
        try
        {
            if (InstallationOwnership.Read(_installDir, true) is null)
                throw new IOException("安装器实例归属无法确认");
            InstallerMetadataCheckpoint.ValidateTransition(_installDir, transactionId, version, imageHash);
            string full = Path.GetFullPath(stagingDir);
            string expected = Path.Combine(_installDir, ".nxp-update", "staging", version);
            if (!string.Equals(full, Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("安装器暂存不属于本实例");
            for (string? path = full; path is not null; path = Path.GetDirectoryName(path))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("安装器暂存包含链接");
                if (string.Equals(path.TrimEnd('\\'), _installDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) break;
            }
            if (UpdateApply.ImageHash(Path.Combine(full, "NexusPipeline.exe")) != imageHash)
                throw new InvalidDataException("安装器程序摘要不匹配");
            _ = _freezePayload(full, version, false);
        }
        catch (Exception ex) { return UpdateApplyResult.Busy("installer-staging-invalid", ex.Message); }
        UpdateApplyResult? policyFailure = VerifyApplyTarget(version);
        if (policyFailure is not null) return policyFailure;
        lock (_gate)
        {
            if (_state != UpdateState.Idle || HasRecoveryArtifacts()
                || ConfigUpdateAdmission.HasPendingRecovery(Path.Combine(_installDir, "data")))
            { return UpdateApplyResult.Busy("recovery-pending", "存在活动更新或恢复现场"); }
            _state = UpdateState.Applying;
        }
        return StartImmediateApply(version, stagingDir, auditSource, transactionId, imageHash, packageHash);
    }

    internal UpdateApplyResult? VerifyApplyTarget(string version)
    {
        try
        {
            if (!NexusVersion.TryParse(version, out NexusVersion target))
                return UpdateApplyResult.Busy("policy-unavailable", "无法核验目标版本");
            var source = new UpdateSourcePolicy(EffectiveSourceUrl(_settings()));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using HttpClient http = _createClient(source.SourceUri, TimeSpan.FromSeconds(30), false);
            var result = UpdatePolicy.FetchAsync(source, http, "NexusPipeline-update/" + _currentVersion,
                Path.Combine(_installDir, ".nxp", "state"), deadline.Token).GetAwaiter().GetResult();
            if (!result.Verified)
                return UpdateApplyResult.Busy("policy-unavailable", result.Error ?? "无法核验当前升级策略");
            var barrier = UpdatePolicy.FindBarrier(result.Policy!, ParseCurrent(), target);
            return barrier is null ? null : UpdateApplyResult.Busy("breaking-update", $"目标版本跨越升级屏障 {barrier.Version}，请使用全新目录安装");
        }
        catch (Exception ex) { return UpdateApplyResult.Busy("policy-unavailable", $"无法核验当前升级策略：{ex.Message}"); }
    }

    private UpdateApplyResult StartImmediateApply(string version, string stagingDir, string auditSource,
        string? transactionId = null, string? imageHash = null, string? packageHash = null)
    {
        UpdateApplyResult result;
        try
        {
            _ = _freezePayload(_installDir, null, true);
            var journal = FreezeJournal("apply", version, stagingDir, packageHash) with {
                TransactionId = transactionId ?? Guid.NewGuid().ToString("N"),
                TargetImageHash = imageHash ?? UpdateApply.ImageHash(Path.Combine(stagingDir, "NexusPipeline.exe")) };
            result = Handoff(journal, auditSource);
        }
        catch (Exception error) { result = UpdateApplyResult.Busy(UpdateApply.FailureCode(error), error.Message); }
        if (!result.Succeeded) lock (_gate) { if (_state == UpdateState.Applying) _state = transactionId is null ? UpdateState.Ready : UpdateState.Idle; }
        return result;
    }

    protected virtual UpdateApplyResult Handoff(UpdateTask task, string auditSource) => UpdateApply.HandoffLocal(task);

    private UpdateTask FreezeJournal(string mode, string version, string staging, string? installerPackageHash = null)
    {
        var payload = _freezePayload(staging, version, false);
        string archive = Path.Combine(UpdateDir, AppPaths.UpdatePackageZipName(version) + Path.GetFileName(staging)[version.Length..]);
        string packageHash = installerPackageHash ?? UpdateApply.ImageHash(archive);
        if (installerPackageHash is null && (_readyPayload != payload || _readyPackageHash != packageHash))
            throw new InvalidDataException("update.ready_payload_changed");
        string checksum = Path.Combine(UpdateDir, AppPaths.UpdatePackageZipName(version) + ".sha256" + Path.GetFileName(staging)[version.Length..]);
        var inventory = installerPackageHash is null ? _readyInventory ?? throw new InvalidDataException("update.ready_inventory_missing") : UpdateInventory.Capture(staging);
        inventory.RequireOwned(staging);
        if (installerPackageHash is null && (!File.Exists(checksum) || UpdateApply.ImageHash(checksum) != _readyChecksumHash))
            throw new InvalidDataException("update.ready_checksum_changed");
        bool expandedPayload = inventory.Entries.Any(entry => entry.Path == UpdateRecipe.UpdateEntry);
        var targetNames = expandedPayload ? ApplicationPayload.Files(staging).Select(file => file.Path).ToHashSet(StringComparer.Ordinal) : [];
        var removed = expandedPayload ? ApplicationPayload.Files(_installDir).Where(file => !targetNames.Contains(file.Path)).ToArray() : [];
        if (removed.Any(file => file.Path != ApplicationPayload.LegacyUpdateEntry)) throw new IOException("update.removal_scope_invalid");
        return UpdateTask.Create(mode, version, staging) with { TargetPayload = payload, PackageSha256 = packageHash,
            StagingInventory = inventory,
            SetupTransition = installerPackageHash is not null,
            PayloadSchemaVersion = expandedPayload ? 2 : 1,
            TransportReceipt = installerPackageHash is null ? _readyTransportReceipt : null,
            ReleaseEvidence = installerPackageHash is null ? UpdateReleaseEvidence.Freeze(_latest!, payload) : null,
            RemovedApplicationFiles = removed.Length == 0 ? null : new(removed),
            JournalSchemaVersion = expandedPayload ? removed.Length == 0 ? 3 : 4 : installerPackageHash is null && _latest?.TargetManifestAsset is not null ? 2 : 1,
            PackageChecksumSha256 = installerPackageHash is null ? _readyChecksumHash : null,
            PackageSource = installerPackageHash is null ? "download" : "installer" };
    }

    private bool TryGetReadyLocked(
        out string version,
        out string stagingDir,
        out UpdateApplyResult? failure)
    {
        version = "";
        stagingDir = "";
        if (_manualUpdateRequired)
        {
            failure = UpdateApplyResult.Busy("breaking-update", "当前更新跨越破坏性版本屏障，请手动下载最新安装包并迁移配置文件");
            return false;
        }
        if (_state != UpdateState.Ready || _latest is null)
        {
            failure = UpdateApplyResult.Busy("not-ready", "更新尚未就绪（请先检查并下载更新）");
            return false;
        }
        if (File.Exists(TaskFile))
        {
            failure = UpdateApplyResult.Busy("transaction-pending", "已有更新事务待处理，请先完成启动恢复");
            return false;
        }
        if (Directory.Exists(BackupDir) || File.Exists(BackupDir))
        {
            failure = UpdateApplyResult.Busy("recovery-pending", "检测到未恢复的更新 backup，请先完成启动恢复");
            return false;
        }
        version = _latest.VersionText;
        if (ConfigUpdateAdmission.HasPendingRecovery(Path.Combine(_installDir, "data")))
        {
            failure = UpdateApplyResult.Busy("configuration-recovery-pending", "存在未恢复的配置事务或编辑隔离现场，请先完成配置恢复");
            return false;
        }
        stagingDir = _readyStagingDir ?? "";
        if (string.IsNullOrWhiteSpace(stagingDir) || !File.Exists(Path.Combine(stagingDir, "NexusPipeline.exe")))
        {
            failure = UpdateApplyResult.Busy("not-ready", "暂存文件不完整，请重新下载");
            return false;
        }
        failure = null;
        return true;
    }

    private UpdateOperation BeginOperationLocked(
        UpdateState state,
        string? zipPath = null,
        string? shaPath = null,
        string? stagingDir = null,
        CancellationToken cancellationToken = default)
    {
        _generation++;
        _operation = new UpdateOperation(_generation, state, zipPath, shaPath, stagingDir, cancellationToken);
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

    private bool TrySetReady(UpdateOperation operation, UpdatePayloadIdentity payload, string packageHash, UpdateInventory inventory, string checksumHash,
        UpdateTransportReceipt? transportReceipt)
    {
        lock (_gate)
        {
            if (!IsCurrentLocked(operation) || _state != UpdateState.Downloading)
            {
                return false;
            }
            _state = UpdateState.Ready;
            _readyStagingDir = operation.StagingDir;
            _readyPayload = payload;
            _readyPackageHash = packageHash;
            _readyInventory = inventory;
            _readyChecksumHash = checksumHash;
            _readyTransportReceipt = transportReceipt;
            _bytesRead = 0;
            _bytesTotal = 0;
            return true;
        }
    }

    private bool FailOperation(UpdateOperation operation, string message)
    {
        lock (_gate)
        {
            if (!IsCurrentLocked(operation) || _state == UpdateState.RecoveryPending)
            {
                return false;
            }
            _error = message;
            _latest = null;
            ClearPolicyLocked();
            _state = _manualAdoptionRequired ? UpdateState.ManualAdoptionPending : UpdateState.Idle;
            _readyStagingDir = null;
            _bytesRead = 0;
            _bytesTotal = 0;
            if (_discoveryInvalidationPending)
            {
                ClearDiscoveryLocked();
            }
            return true;
        }
    }

    private void CompleteOperation(UpdateOperation operation, UpdateState expectedState)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_operation, operation))
            {
                if (_state == UpdateState.Cancelling || _state == expectedState)
                {
                    _state = _manualAdoptionRequired ? UpdateState.ManualAdoptionPending : UpdateState.Idle;
                    _error = operation.Cts.IsCancellationRequested ? "" : _error;
                    _bytesRead = 0; _bytesTotal = 0;
                    if (_discoveryInvalidationPending) ClearDiscoveryLocked();
                }
                _operation = null;
            }
            operation.Cts.Dispose();
            operation.Completion.TrySetResult(true);
        }
    }

    private void CleanupDownloadArtifacts(UpdateOperation operation)
    {
        try { operation.Artifacts?.Cleanup(); }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_operation, operation))
                {
                    _state = UpdateState.RecoveryPending;
                    _error = "update.cleanup_preserved: " + ex.Message;
                }
            }
            Logger.Warn($"[更新] 下载现场归属核验失败，保留文件：{ex.Message}");
        }
    }

    private NexusVersion ParseCurrent()
    {
        return UpdateCatalog.TryParseTag(_sourceVersion ?? _currentVersion, out NexusVersion version)
            ? version
            : NexusVersion.Stable(0, 0, 0);
    }

    private UpdateStatusSnapshot BuildSnapshotLocked()
    {
        return new UpdateStatusSnapshot(
            _state,
            _state is UpdateState.Downloading or UpdateState.Cancelling ? BytesToPercent(_bytesRead, _bytesTotal) : null,
            _bytesRead,
            _bytesTotal,
            _error,
            _currentVersion,
            _latest?.VersionText,
            _latest?.Prerelease,
            EffectiveChannel(_settings()),
            _latest is not null,
            _latest?.Notes ?? "",
            _hasChecked,
            _policyVerified,
            _canDownload,
            _manualUpdateRequired || _manualAdoptionRequired,
            _manualAdoptionRequired ? "manual-adoption-pending" : _updateBlockCode,
            _barrierVersion,
            _migrationUrl,
            _policyError,
            _lastApplyFailure,
            _pendingVersion,
            _pendingStatus,
            _pendingError) { InstallerToolsWarning = _installerToolsWarning };
    }

    private void ClearDiscoveryLocked()
    {
        _latest = null;
        _error = "";
        _hasChecked = false;
        _discoveryInvalidationPending = false;
        ClearPolicyLocked();
    }

    private void ClearPolicyLocked()
    {
        _policyVerified = null;
        _canDownload = false;
        _manualUpdateRequired = false;
        _updateBlockCode = null;
        _barrierVersion = null;
        _migrationUrl = null;
        _policyError = null;
        _pendingVersion = null;
        _pendingStatus = null;
        _pendingError = null;
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
        public CancellationTokenSource Cts { get; }
        public UpdateCreatedArtifacts? Artifacts { get; }
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public UpdateOperation(
            long generation,
            UpdateState state,
            string? zipPath,
            string? shaPath,
            string? stagingDir,
            CancellationToken cancellationToken = default)
        {
            Generation = generation;
            State = state;
            ZipPath = zipPath;
            ShaPath = shaPath;
            StagingDir = stagingDir;
            Artifacts = zipPath is null ? null : new UpdateCreatedArtifacts(Path.GetDirectoryName(zipPath)!);
            Cts = cancellationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : new CancellationTokenSource();
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
    string Notes,
    bool HasChecked,
    bool? PolicyVerified = null,
    bool CanDownload = false,
    bool ManualUpdateRequired = false,
    string? UpdateBlockCode = null,
    string? BarrierVersion = null,
    string? MigrationUrl = null,
    string? PolicyError = null,
    UpdateApplyFailure? LastApplyFailure = null,
    string? PendingVersion = null,
    string? PendingStatus = null,
    string? PendingError = null)
{
    public string? InstallerToolsWarning { get; init; }
    public string? ErrorCode => string.IsNullOrWhiteSpace(Error) ? null
        : Error.StartsWith("update.cleanup_preserved", StringComparison.Ordinal) ? "update.cleanup_preserved" : "update_failed";
}

/// <summary>下载请求结果：Started 表示后台下载已受理，Rejected 携带稳定的 API 错误码。</summary>
internal sealed record UpdateDownloadResult(bool Succeeded, string? Code, string? Error)
{
    public static UpdateDownloadResult Started() => new(true, null, null);

    public static UpdateDownloadResult Rejected(string code, string error) => new(false, code, error);
}

/// <summary>应用请求结果：Succeeded=true 表示已受理（Deferred 区分立即/下次启动）。</summary>
internal sealed record UpdateApplyResult(bool Succeeded, bool Deferred, string? Code, string? Error)
{
    public static UpdateApplyResult Ok(bool deferred) => new(true, deferred, null, null);

    public static UpdateApplyResult Busy(string code, string error) => new(false, false, code, error);
}
