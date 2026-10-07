using System.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Windows;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Shared.Logging;
using NexusPipeline.Shared.Serialization;
using NexusPipeline.Shared.Versioning;

using NexusPipeline.Modules.Configuration.Recovery;

namespace NexusPipeline.Modules.Updates;

internal static class UpdatePhase
{
    public const string Deferred = "Deferred";
    public const string ApplyRequested = "ApplyRequested";
    public const string BackupPreparing = "BackupPreparing";
    public const string BackupReady = "BackupReady";
    public const string SwapInProgress = "SwapInProgress";
    public const string SwapReady = "SwapReady";
    public const string AwaitingStartup = "AwaitingStartup";
    public const string Committed = "Committed";
    public const string RollbackPending = "RollbackPending";
    public const string RollbackConfirmed = "RollbackConfirmed";
}

/// <summary>
/// 更新应用的切换与收尾：apply-update 子进程（备份→交换→标记→重拉）与新实例启动收尾。
/// 旧版本备份在 commit 前始终是 immutable snapshot；回滚失败时 backup、journal、staging 全部保留。
/// 只替换 Host、README、桌面树与应用清单；plugins、config、data、history、logs 均属于用户运行数据。
/// </summary>
internal static class UpdateApply
{
    private const int MutexWaitSeconds = 120;
    private const string BackupReadyMarker = ".backup-ready";
    private const string WorkerImagePrefix = "update-";
    private const int RequiredFileRetryCount = 10;
    private static readonly TimeSpan RequiredFileRetryDelay = TimeSpan.FromMilliseconds(200);
    private static int _startupRecoveryUnsafe;
    private static string TransactionMutexName => "NexusPipeline.Update." + Convert.ToHexString(SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(AppPaths.AppRoot).TrimEnd('\\').ToUpperInvariant())));

    internal static bool StartupRecoveryUnsafe => Volatile.Read(ref _startupRecoveryUnsafe) != 0;

    /// <summary>apply-update 子进程入口：等待宿主退出 → 建立不可变 backup → 交换 → commit → 重拉宿主。</summary>
    public static int RunApplyWorker(
        string stagedDir,
        Func<string, UpdateApplyResult?> verifyTarget,
        bool webOnly = false,
        string? mutexName = null)
    {
        // Running the replaceable image as its own worker can never release it.
        if (string.Equals(Environment.ProcessPath, Path.Combine(AppPaths.AppRoot, "NexusPipeline.exe"), StringComparison.OrdinalIgnoreCase))
        { Logger.Error("[更新] 请通过复制 worker 交接；原宿主映像不可执行交换。"); return 1; }
        using var transactionMutex = new Mutex(false, TransactionMutexName);
        bool acquired;
        try { acquired = transactionMutex.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) { Logger.Error("[更新] 已有更新 worker 持有本实例事务。"); return 1; }
        try { return RunApplyTransaction(stagedDir, verifyTarget, webOnly, mutexName); }
        catch (Exception ex) { Logger.Error($"[更新] 保留不支持的事务现场：{ex.Message}"); return 1; }
        finally { transactionMutex.ReleaseMutex(); }
    }

    private static int RunApplyTransaction(string stagedDir, Func<string, UpdateApplyResult?> verifyTarget, bool webOnly, string? mutexName)
    {
        Logger.Info("[更新] apply-update 进程启动，等待主实例退出...");
        Audit.Log(Audit.System, "更新切换", "apply-update 进程启动");
        UpdateTask? task = UpdateTask.Read();
        if (task is null) { Logger.Error("[更新] 缺少已创建的当前事务，拒绝从暂存路径推断更新。"); return 1; }
        if (!string.Equals(Path.GetFullPath(task.StagedDir), Path.GetFullPath(stagedDir), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("update staging identity mismatch");
        string targetVersion = task.Version;
        UpdateTask journal = task;
        bool staleBackupDetected = false;
        bool backupComplete = false;
        Process? candidateProcess = null;
        ProcessIdentity? candidateIdentity = null;
        try
        {
            ValidateStagedPath(stagedDir);
            if (!WaitForHostExit(TimeSpan.FromSeconds(MutexWaitSeconds), mutexName))
            {
                Logger.Error("[更新] 等待主实例退出超时（120 秒），更新取消。");
                Audit.Log(Audit.System, "更新切换失败", "等待主实例退出超时");
                WriteTransactionResult(journal, false, "host_release_timeout");
                AbortBeforeBackup(journal);
                return 1;
            }

            string installDir = AppPaths.AppRoot;
            if (journal.WorkerSha256 != ImageHash(Environment.ProcessPath!) || journal.WorkerPath is not null
                && !string.Equals(journal.WorkerPath, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("update.worker_bytes_changed");
            journal.StagingInventory!.RequireOwned(stagedDir);
            journal = journal with { TransactionId = journal.TransactionId ?? Guid.NewGuid().ToString("N"),
                TargetImageHash = journal.TargetImageHash ?? ImageHash(Path.Combine(stagedDir, "NexusPipeline.exe")),
                WorkerIdentity = ProcessIdentity.Capture(Process.GetCurrentProcess()), WorkerLaunchPending = false };
            journal.Write();
            UpdateApplyResult? policyFailure = verifyTarget(targetVersion);
            if (policyFailure is not null)
            {
                Logger.Error($"[更新] 应用前策略核验失败，保留事务：{policyFailure.Code} {policyFailure.Error}");
                WriteTransactionResult(journal, false, policyFailure.Code ?? "policy-unavailable");
                LaunchService(installDir, webOnly, journal).Dispose();
                return 1;
            }
            if (ConfigUpdateAdmission.HasPendingRecovery(AppPaths.DataDir))
            {
                Logger.Error("[更新] 配置恢复现场尚未清理，保留更新暂存和 journal，拒绝切换版本。");
                WriteTransactionResult(journal, false, "configuration_recovery_pending");
                return 1;
            }
            string stageExe = Path.Combine(stagedDir, "NexusPipeline.exe");
            if (!Directory.Exists(stagedDir) || !File.Exists(stageExe))
            {
                throw new InvalidDataException($"staging 目录无效：{stagedDir}");
            }

            string backup = AppPaths.UpdateBackupDir;
            journal = journal with { TransactionId = journal.TransactionId ?? Guid.NewGuid().ToString("N"),
                TargetImageHash = ImageHash(stageExe), WorkerIdentity = ProcessIdentity.Capture(Process.GetCurrentProcess()) };
            staleBackupDetected = Directory.Exists(backup) || File.Exists(backup);
            ApplicationPayload.VerifyFrozen(stagedDir, journal.TargetPayload!, targetVersion);
            if (journal.PackageSource == "download")
            {
                string suffix = Path.GetFileName(stagedDir)[targetVersion.Length..];
                string archive = Path.Combine(AppPaths.UpdateDir, AppPaths.UpdatePackageZipName(targetVersion) + suffix);
                if (ImageHash(archive) != journal.PackageSha256) throw new InvalidDataException("update.package_hash_changed");
            }
            var previous = ApplicationPayload.Freeze(installDir, installed: true);
            RequireDesktopFilesReleased(installDir);
            var backupInventory = UpdateInventory.Capture(installDir, ApplicationAssets)
                .WithFile(BackupReadyMarker, System.Text.Encoding.UTF8.GetBytes(journal.TransactionId + Environment.NewLine));
            if (InstallationOwnership.SnapshotBytesForUpdate(installDir) is { } installerBytes)
                backupInventory = backupInventory.WithFile(".installer-identity", installerBytes);
            journal = journal with { PreviousPayload = previous, DesktopStopped = true, BackupInventory = backupInventory };
            EnsureNoStaleBackup(backup);
            journal = journal with { Mode = "apply", Phase = UpdatePhase.BackupPreparing };
            journal.Write();
            CreateBackupSnapshot(installDir, backup, journal.TransactionId!);
            journal.BackupInventory.RequireOwned(backup);
            ApplicationPayload.VerifyFrozen(backup, journal.PreviousPayload!, installed: true);
            backupComplete = true;
            journal = journal with { Phase = UpdatePhase.BackupReady };
            journal.Write();
            PauseForFaultInjection(UpdatePhase.BackupReady);

            string oldExe = Path.Combine(installDir, "NexusPipeline.exe");
            ApplicationPayload.VerifyFrozen(stagedDir, journal.TargetPayload!, targetVersion);
            journal = journal with { Phase = UpdatePhase.SwapInProgress };
            journal.Write();
            foreach (string asset in ApplicationAssets)
                journal.BackupInventory.RequireOwned(installDir, asset);
            foreach (string asset in ApplicationAssets)
            {
                SwapInto(stagedDir, installDir, asset, journal);
                journal = journal with { SwappedAssetCount = journal.SwappedAssetCount + 1 };
                journal.Write();
            }
            ApplicationPayload.VerifyFrozen(installDir, journal.TargetPayload!, targetVersion, installed: true);
            journal = journal with { Phase = UpdatePhase.SwapReady };
            journal.Write();
            PauseForFaultInjection(UpdatePhase.SwapReady);

            journal = journal with { Phase = UpdatePhase.AwaitingStartup };
            journal.Write();
            candidateProcess = LaunchService(installDir, webOnly, journal);
            candidateIdentity = CaptureCandidateIdentity(candidateProcess, oldExe);
            if (candidateIdentity is null) throw new IOException("新宿主启动身份无法确认");
            WaitForStartupReceipt(journal, candidateProcess, candidateIdentity.Value);
            ApplicationPayload.VerifyFrozen(installDir, journal.TargetPayload!, targetVersion, installed: true);
            InstallationOwnership.RefreshAfterUpdate(installDir, targetVersion);
            WriteVersionFile(targetVersion);
            journal = journal with { Mode = "completed", Phase = UpdatePhase.Committed };
            journal.Write();
            Audit.Log(Audit.System, "更新应用完成", $"v{targetVersion}（staging：{stagedDir}）");
            Logger.Info($"[更新] 文件交换完成（v{targetVersion}），正在重新拉起宿主。");
            Audit.Log(Audit.System, "更新完成", $"v{targetVersion}（候选启动、映像已确认）");
            WriteTransactionResult(journal, true, "committed");
            return 0;
        }
        catch (Exception ex)
        {
            Logger.Error($"[更新] 应用失败：{ex.Message}");
            Audit.Log(Audit.System, "更新切换失败", ex.Message);
            UpdateTask? current = UpdateTask.Read();
            if (ReadVersionFile() is not null || string.Equals(current?.Phase, UpdatePhase.Committed, StringComparison.Ordinal))
            {
                WriteTransactionResult(journal, false, "committed_cleanup_pending");
                Logger.Error("[更新] 启动已核对，但提交收尾失败；保留 journal 与 backup。");
                return 1;
            }
            if (candidateProcess is not null && candidateIdentity is not null
                && !SystemActions.KillEditProcess(null, candidateIdentity, candidateProcess.Id,
                    Path.Combine(AppPaths.AppRoot, "NexusPipeline.exe"), "unqualified update candidate", rounds: 2, intervalMs: 100, stableSeconds: 1))
            {
                Logger.Error("[更新] 新宿主未通过启动核对且退出未确认；保留唯一备份，禁止覆盖。");
                WriteTransactionResult(journal, false, "candidate_stop_unconfirmed"); return 1;
            }
            WriteTransactionResult(journal, false, "apply_failed");
            if (staleBackupDetected)
            {
                // 旧 backup 的归属无法在 worker 内安全分类：严禁拿它作为本次回滚源，也严禁自动删除。
                Logger.Error("[更新] 检测到未分类旧 backup，保留 backup/journal，等待启动恢复或人工处理。");
                return 1;
            }
            if (backupComplete)
            {
                bool rolledBack = false;
                try
                {
                    Rollback(journal with { Phase = UpdatePhase.RollbackPending });
                    rolledBack = true;
                    try
                    {
                        CleanupAfterRollback();
                    }
                    catch (Exception cleanupEx)
                    {
                        Logger.Warn($"[更新] 已完成失败回滚；现场清理将在下次启动重试：{cleanupEx.Message}");
                    }
                }
                catch (Exception rollbackEx)
                {
                    Logger.Error($"[更新] 回滚失败（保留 backup/journal/staging，下次启动重试）：{rollbackEx.Message}");
                    TryWritePhase(journal, UpdatePhase.RollbackPending);
                }
                if (rolledBack)
                {
                    try
                    {
                        Logger.Warn("[更新] 正在重新拉起回滚后的宿主版本。");
                        LaunchService(AppPaths.AppRoot, webOnly, journal).Dispose();
                    }
                    catch (Exception launchEx)
                    {
                        Logger.Error($"[更新] 回滚已完成，但无法重新拉起宿主：{launchEx.Message}");
                    }
                }
            }
            else
            {
                AbortBeforeBackup(journal);
                if (!staleBackupDetected)
                {
                    try
                    {
                        Logger.Warn("[更新] 文件交换前更新失败，正在重新拉起现有宿主版本。");
                        LaunchService(AppPaths.AppRoot, webOnly, journal).Dispose();
                    }
                    catch (Exception launchEx)
                    {
                        Logger.Error($"[更新] 文件交换前更新失败，无法重新拉起宿主：{launchEx.Message}");
                    }
                }
            }
            return 1;
        }
        finally { candidateProcess?.Dispose(); }
    }

    /// <summary>
    /// 新实例启动收尾：完成 commit 后只在所有临时项清理成功时删除 version marker；
    /// apply/defer 启动失败保留 journal，rollback 失败保留 backup 与 journal。
    /// </summary>
    public static bool RunStartupFinalization(Func<string, UpdateApplyResult?> verifyTarget, bool webOnly = false, string? mutexName = null, string? restartHandoffId = null,
        Func<string, bool>? stopDesktop = null, Func<DesktopResumeIntent>? captureDesktopIntent = null,
        Action<string, DesktopResumeIntent>? abortDesktop = null)
    {
        Volatile.Write(ref _startupRecoveryUnsafe, 0);
        try { return FinalizeCurrentTransaction(verifyTarget, webOnly, mutexName, restartHandoffId, stopDesktop ?? (_ => true),
            captureDesktopIntent ?? (() => DesktopResumeIntent.FromVisible(false)), abortDesktop ?? ((_, _) => { })); }
        catch (Exception ex)
        {
            Volatile.Write(ref _startupRecoveryUnsafe, 1);
            Logger.Error($"[更新] 恢复现场已保留，停止自动更新：{ex.Message}");
            return false;
        }
    }

    private static bool FinalizeCurrentTransaction(Func<string, UpdateApplyResult?> verifyTarget, bool webOnly, string? mutexName, string? restartHandoffId,
        Func<string, bool> stopDesktop, Func<DesktopResumeIntent> captureDesktopIntent, Action<string, DesktopResumeIntent> abortDesktop)
    {
        string? appliedVersion = ReadVersionFile();
        UpdateTask? pending = UpdateTask.Read();
        if (pending is null)
        {
            if (appliedVersion is not null || Directory.Exists(AppPaths.UpdateBackupDir) || File.Exists(AppPaths.UpdateBackupDir))
                throw new InvalidDataException("update recovery proof missing");
            return false;
        }
        if (pending.WorkerLaunchPending) throw new IOException("update worker launch ownership unconfirmed");
        if (appliedVersion is not null)
        {
            if (pending.Version != appliedVersion || pending.Phase is not (UpdatePhase.Committed or UpdatePhase.AwaitingStartup))
                throw new InvalidDataException("update marker identity mismatch");
            if (pending.Phase == UpdatePhase.AwaitingStartup)
            {
                bool? workerAlive = ObserveWorker(pending.WorkerIdentity);
                if (workerAlive == true) return false;
                if (workerAlive is null) throw new IOException("update worker identity unconfirmed");
                ValidateCommitProof(pending);
                pending = pending with { Mode = "completed", Phase = UpdatePhase.Committed };
                pending.Write();
            }
        }
        if (pending.Phase == UpdatePhase.Committed)
        {
            CleanupAfterCompletion(pending);
            return false;
        }
        if (pending.Phase is UpdatePhase.Deferred or UpdatePhase.ApplyRequested)
        {
            if (pending.Phase == UpdatePhase.ApplyRequested && HasFailedBeforeBackup(pending))
            {
                AbortBeforeBackup(pending);
                return false;
            }
            UpdateApplyResult? policyFailure = verifyTarget(pending.Version);
            if (policyFailure is not null)
            {
                Logger.Warn($"[更新] 暂存更新未通过当前策略，保留现场：{policyFailure.Code} {policyFailure.Error}");
                return false;
            }
        }
        if (Guid.TryParseExact(restartHandoffId, "N", out _)
            && pending.RestartHandoffId != restartHandoffId)
        {
            try
            {
                UpdateTask handoff = pending with { RestartHandoffId = restartHandoffId };
                handoff.Write();
                pending = handoff;
            }
            catch (Exception ex)
            {
                Logger.Error($"[更新] 无法保存恢复交接标识，保留原 journal：{ex.Message}");
                Volatile.Write(ref _startupRecoveryUnsafe, 1);
                return false;
            }
        }
        if (pending.Phase == UpdatePhase.AwaitingStartup)
        {
            bool? alive = ObserveWorker(pending.WorkerIdentity);
            if (alive == true) return false; // backup remains immutable until actual services are ready
            if (alive is null) { Volatile.Write(ref _startupRecoveryUnsafe, 1); return false; }
            // A crashed worker has not committed. Existing rollback recovery owns this case.
        }

        if (pending.Phase == UpdatePhase.BackupPreparing)
        {
            // backup marker 尚未写入，旧安装仍应保持完整；只清理未完成的准备现场。
            AbortBeforeBackup(pending);
            return false;
        }

        if (pending.Phase is UpdatePhase.Deferred or UpdatePhase.ApplyRequested && !HasBackupData(AppPaths.UpdateBackupDir))
        {
            if (ConfigUpdateAdmission.HasPendingRecovery(AppPaths.DataDir))
            {
                Logger.Warn("[更新] 配置恢复尚未完成，保留下次启动更新，继续当前宿主的恢复流程。");
                return false;
            }
            if (!IsStagingValid(pending.StagedDir))
            {
                Logger.Error("[更新] defer staging 无效，保留 journal 供人工处理。");
                return false;
            }
            pending.StagingInventory!.RequireOwned(pending.StagedDir);
            ApplicationPayload.VerifyFrozen(pending.StagedDir, pending.TargetPayload!, pending.Version);
            Logger.Info("[更新] 检测到「下次启动更新」标记，开始应用。");
            Audit.Log(Audit.System, "更新应用", $"v{pending.Version}（defer 启动）");
            string transaction = pending.TransactionId ?? Guid.NewGuid().ToString("N");
            UpdateTask apply = pending with { Mode = "apply", Phase = UpdatePhase.ApplyRequested,
                TransactionId = transaction, TargetImageHash = pending.TargetImageHash ?? ImageHash(Path.Combine(pending.StagedDir, "NexusPipeline.exe")),
                DesktopResumeIntent = pending.DesktopResumeIntent ?? captureDesktopIntent() };
            bool desktopPrepared = false;
            UpdateWorkerLaunch launch = UpdateWorkerLaunch.NotStarted;
            try
            {
                apply.Write();
                if (!stopDesktop(transaction)) throw new IOException("desktop_stop_unconfirmed");
                desktopPrepared = true;
                apply = apply with { DesktopStopped = true };
                apply.Write();
                launch = LaunchApplyWorker(pending.StagedDir, webOnly);
                if (launch == UpdateWorkerLaunch.NotStarted)
                {
                    Logger.Error("[更新] 启动时无法拉起 apply-update，保留本事务 journal，当前进程继续运行。");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"[更新] defer 启动失败（保留 journal）：{ex.Message}");
                return false;
            }
            finally
            {
                if (desktopPrepared && launch == UpdateWorkerLaunch.NotStarted)
                    try { abortDesktop(transaction, apply.DesktopResumeIntent!); }
                    catch (Exception exception) { Logger.Warn("desktop_update_abort_failed: " + exception.GetType().Name); }
            }
        }

        if (pending.Mode == "apply" && pending.Phase != UpdatePhase.RollbackConfirmed)
        {
            if (ConfigUpdateAdmission.HasPendingRecovery(AppPaths.DataDir))
            {
                Logger.Warn("[更新] 配置恢复尚未完成，保留版本切换与回滚现场。");
                return false;
            }
            Logger.Warn($"[更新] 检测到未完成的更新切换（phase={pending.Phase}），启动时回滚。");
            Audit.Log(Audit.System, "更新失败已回滚", $"v{pending.Version}（切换未完成）");
            if (HasBackupExecutable(AppPaths.UpdateBackupDir))
            {
                if (!stopDesktop(pending.TransactionId!)) throw new IOException("desktop_stop_unconfirmed");
                if (LaunchRecoveryWorker(webOnly) != UpdateWorkerLaunch.NotStarted)
                {
                    return true;
                }
                Logger.Error("[更新] 无法拉起独立 recovery worker，继续尝试当前进程回滚。");
            }
            try
            {
                Rollback(pending with { Phase = UpdatePhase.RollbackPending });
                CleanupAfterRollback();
            }
            catch (Exception ex)
            {
                Logger.Error($"[更新] 启动回滚失败（保留 backup/journal，下次启动重试）：{ex.Message}");
                TryWritePhase(pending, UpdatePhase.RollbackPending);
                Volatile.Write(ref _startupRecoveryUnsafe, 1);
            }
        }
        else if (pending.Phase == UpdatePhase.RollbackConfirmed)
        {
            try
            {
                RequireWorkerExit(pending);
                CleanupAfterRollback();
            }
            catch (Exception ex)
            {
                Logger.Error($"[更新] 回滚后清理失败（保留 journal）：{ex.Message}");
            }
        }
        else
        {
            Logger.Warn($"[更新] 无法识别的更新 journal 状态：Mode={pending.Mode}, Phase={pending.Phase}；保留现场。");
            Volatile.Write(ref _startupRecoveryUnsafe, 1);
        }
        return false;
    }

    /// <summary>
    /// 独立 recovery worker 入口：等待持有当前 exe 的启动实例退出，再还原 immutable backup，
    /// 写入 RollbackConfirmed 并拉起旧版本。旧版本启动后负责最终删除 backup/journal。
    /// </summary>
    public static int RunRecoveryWorker(bool webOnly = false, string? mutexName = null)
    {
        using var transactionMutex = new Mutex(false, TransactionMutexName);
        bool acquired;
        try { acquired = transactionMutex.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) { Logger.Error("[更新] 已有更新 worker 持有本实例事务。"); return 1; }
        try { return RunRecoveryTransaction(webOnly, mutexName); }
        catch (Exception exception) { Logger.Error("[更新] recovery 事务保持原状：" + exception.Message); return 1; }
        finally { transactionMutex.ReleaseMutex(); }
    }

    private static int RunRecoveryTransaction(bool webOnly, string? mutexName)
    {
        Logger.Info("[更新] recovery worker 启动，等待当前宿主退出...");
        UpdateTask? pending = UpdateTask.Read();
        if (pending is null || !HasBackupExecutable(AppPaths.UpdateBackupDir))
        {
            Logger.Error("[更新] recovery worker 缺少可恢复的更新 journal 或 exe backup。");
            return 1;
        }
        if (!WaitForHostExit(TimeSpan.FromSeconds(MutexWaitSeconds), mutexName))
        {
            Logger.Error("[更新] recovery worker 等待当前宿主退出超时。");
            return 1;
        }

        if (ConfigUpdateAdmission.HasPendingRecovery(AppPaths.DataDir))
        {
            Logger.Error("[更新] 配置恢复现场尚未清理，拒绝用旧版本覆盖当前宿主。");
            return 1;
        }
        try
        {
            if (pending.WorkerSha256 != ImageHash(Environment.ProcessPath!) || pending.WorkerPath is not null
                && !string.Equals(pending.WorkerPath, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                throw new IOException("update.worker_identity_unconfirmed");
            Rollback(pending with { Mode = "apply", Phase = UpdatePhase.RollbackPending,
                WorkerIdentity = ProcessIdentity.Capture(Process.GetCurrentProcess()), WorkerLaunchPending = false });
            LaunchService(AppPaths.AppRoot, webOnly, pending).Dispose();
            return 0;
        }
        catch (Exception ex)
        {
            Logger.Error($"[更新] recovery worker 回滚失败（保留 backup/journal）：{ex.Message}");
            TryWritePhase(pending, UpdatePhase.RollbackPending);
            return 1;
        }
    }

    /* ---------------- 事务文件操作 ---------------- */

    private static bool WaitForHostExit(TimeSpan timeout, string? mutexName)
    {
        using var probe = new Mutex(false, mutexName ?? "NexusPipeline.SingleInstance");
        DateTime deadline = DateTime.UtcNow + timeout;
        bool acquired = false;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (probe.WaitOne(500))
                {
                    acquired = true;
                    break;
                }
            }
            catch (AbandonedMutexException)
            {
                // AbandonedMutexException 同样表示当前线程已取得互斥体所有权；
                // 仍需继续等待旧宿主的 EXE 映像句柄真正释放。
                acquired = true;
                break;
            }
        }
        if (!acquired)
        {
            return false;
        }

        try
        {
            return WaitForExecutableRelease(Path.Combine(AppPaths.AppRoot, "NexusPipeline.exe"), deadline);
        }
        finally
        {
            try
            {
                probe.ReleaseMutex();
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// 单实例互斥体释放与 Windows 解除宿主 EXE 映像映射之间存在极短的交接窗口。
    /// 只有目标文件可以以独占读写方式打开时，更新 worker 才能安全进入交换。
    /// </summary>
    private static bool WaitForExecutableRelease(string path, DateTime deadline)
    {
        while (DateTime.UtcNow < deadline)
        {
            if (!File.Exists(path))
            {
                return true;
            }
            try
            {
                using var probe = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    options: FileOptions.RandomAccess);
                return true;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            Thread.Sleep(50);
        }
        return false;
    }

    private static void EnsureNoStaleBackup(string backup)
    {
        if (Directory.Exists(backup) || File.Exists(backup))
        {
            throw new IOException($"检测到未分类的旧 backup：{backup}；请先完成启动恢复");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        Directory.CreateDirectory(backup);
    }

    private static readonly string[] ApplicationAssets = ["NexusPipeline.exe", "resources/desktop", "README.md", ApplicationPayload.ManifestPath];

    private static void CreateBackupSnapshot(string installDir, string backup, string transactionId)
    {
        foreach (string asset in ApplicationAssets)
        {
            PayloadPathSafety.RequireLinkFree(Path.Combine(installDir, asset));
            CopySnapshotItem(Path.Combine(installDir, asset), Path.Combine(backup, asset));
        }
        InstallationOwnership.SnapshotForUpdate(installDir, backup);
        WriteRequiredText(Path.Combine(backup, BackupReadyMarker), transactionId);
    }

    private static void EnsureOptionalAssetTargetIsSafe(string path)
    {
        if (Directory.Exists(path)) throw new IOException($"应用说明文件目标是目录，拒绝覆盖：{path}");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"应用说明文件目标是重解析点，拒绝覆盖：{path}");
    }

    private static void CopySnapshotItem(string source, string target)
    {
        if (!Directory.Exists(source) && !File.Exists(source))
        {
            return;
        }
        if (Directory.Exists(source))
        {
            CopyDirectory(source, target);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: false);
        }
    }

    private static void SwapInto(string staging, string installation, string asset, UpdateTask journal)
    {
        string source = UpdateInventory.Resolve(staging, asset), target = UpdateInventory.Resolve(installation, asset);
        if (!Directory.Exists(source) && !File.Exists(source))
        {
            throw new FileNotFoundException("更新 staging 缺少交换项", source);
        }
        void Guard() { if (UpdateTask.Read() != journal) throw new InvalidDataException("update journal changed during swap"); }
        journal.StagingInventory!.RequireOwned(staging, asset);
        journal.BackupInventory!.DeleteOwned(installation, Guard, asset);
        Guard();
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (Directory.Exists(source))
        {
            RetryRequired(() => Directory.Move(source, target), $"移动目录 {source} → {target}");
        }
        else
        {
            RetryRequired(() => File.Move(source, target), $"移动文件 {source} → {target}");
        }
    }

    private static void Rollback(UpdateTask journal)
    {
        string backup = AppPaths.UpdateBackupDir;
        if (!HasBackupData(backup))
        {
            throw new IOException("没有可恢复的完整更新 backup");
        }
        string installDir = AppPaths.AppRoot;
        ApplicationPayload.VerifyFrozen(backup, journal.PreviousPayload!, installed: true);
        journal.BackupInventory!.RequireOwned(backup);
        journal.Write();
        journal = UpdateRollbackProtection.Prepare(journal, installDir, AppPaths.UpdateTaskFile, ApplicationAssets);
        foreach (string asset in ApplicationAssets)
            _ = UpdateRollbackProtection.CurrentOwned(journal, installDir, asset);
        foreach (string asset in ApplicationAssets)
            RestoreFromBackup(backup, installDir, asset, journal);
        ApplicationPayload.VerifyFrozen(installDir, journal.PreviousPayload!, installed: true);
        InstallationOwnership.RestoreAfterRollback(installDir, backup);
        UpdateTask confirmed = journal with { Mode = "apply", Phase = UpdatePhase.RollbackConfirmed };
        confirmed.Write();
        Audit.Log(Audit.System, "更新回滚完成", "旧版本文件已从 immutable backup 还原");
    }

    private static void RestoreFromBackup(string backup, string installation, string asset, UpdateTask journal)
    {
        string backupItem = UpdateInventory.Resolve(backup, asset), target = UpdateInventory.Resolve(installation, asset);
        if (!Directory.Exists(backupItem) && !File.Exists(backupItem))
        {
            return;
        }
        var current = UpdateRollbackProtection.CurrentOwned(journal, installation, asset);
        current.DeleteOwned(installation, () => UpdateRollbackProtection.Guard(journal, AppPaths.UpdateTaskFile), asset);
        UpdateRollbackProtection.Guard(journal, AppPaths.UpdateTaskFile);
        CopySnapshotItem(backupItem, target);
    }

    private static void ValidateCommitProof(UpdateTask task)
    {
        var receipt = JsonSerializer.Deserialize<StartupReceipt>(File.ReadAllText(StartupReceiptPath(task.TransactionId!)))
            ?? throw new InvalidDataException("update startup proof missing");
        if (receipt.TransactionId != task.TransactionId || receipt.Version != task.Version
            || receipt.Payload != task.TargetPayload || receipt.ImageHash != task.TargetImageHash || receipt.ReadyAtUtc < task.CreatedAt
            || !string.Equals(receipt.Identity.ImageName, Path.Combine(AppPaths.AppRoot, "NexusPipeline.exe"), StringComparison.OrdinalIgnoreCase)
            || ImageHash(Path.Combine(AppPaths.AppRoot, "NexusPipeline.exe")) != task.TargetImageHash)
            throw new InvalidDataException("update startup proof mismatch");
    }

    private static void CleanupAfterCompletion(UpdateTask task) => UpdateCleanup.Complete(task, AppPaths.AppRoot, ObserveWorker);

    private static void CleanupAfterRollback()
    {
        var task = UpdateTask.Read() ?? throw new InvalidDataException("update rollback proof missing");
        UpdateCleanup.Complete(task, AppPaths.AppRoot, ObserveWorker);
    }

    private static void AbortBeforeBackup(UpdateTask journal)
    {
        try
        {
            UpdateCleanup.Abort(journal, AppPaths.AppRoot, ObserveWorker);
        }
        catch (Exception ex)
        {
            Logger.Error($"[更新] 交换尚未开始，但清理失败；保留 journal 供下次启动处理：{ex.Message}");
            TryWritePhase(journal, journal.Phase);
        }
    }

    private static bool HasFailedBeforeBackup(UpdateTask task)
    {
        if (task.TransactionId is null || task.WorkerIdentity is null || HasBackupData(AppPaths.UpdateBackupDir)) return false;
        string path = TransactionResultPath(task.TransactionId);
        if (!File.Exists(path)) return false;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > 64 * 1024)
            throw new InvalidDataException("update failure proof ownership");
        if (!IsFailedTransactionResult(task, File.ReadAllText(path))) return false;
        RequireWorkerExit(task);
        return true;
    }

    internal static bool IsFailedTransactionResult(UpdateTask task, string json)
    {
        using var document = JsonDocument.Parse(json);
        var value = document.RootElement;
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 6
            || value.GetProperty("TransactionId").GetString() != task.TransactionId
            || value.GetProperty("Version").GetString() != task.Version
            || value.GetProperty("Succeeded").ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || string.IsNullOrWhiteSpace(value.GetProperty("Code").GetString())
            || value.GetProperty("AtUtc").GetDateTimeOffset() < task.CreatedAt)
            throw new InvalidDataException("update failure proof identity");
        var intent = value.GetProperty("DesktopResumeIntent");
        if (intent.ValueKind != JsonValueKind.Object || intent.EnumerateObject().Count() != 2
            || intent.GetProperty("SchemaVersion").GetInt32() != task.DesktopResumeIntent?.SchemaVersion
            || intent.GetProperty("Mode").GetString() != task.DesktopResumeIntent?.Mode)
            throw new InvalidDataException("update failure proof desktop intent");
        return !value.GetProperty("Succeeded").GetBoolean();
    }

    private static void RequireWorkerExit(UpdateTask task)
    {
        // A terminal worker starts the preserved Host before its own process exits.
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (ObserveWorker(task.WorkerIdentity) == true && DateTime.UtcNow < deadline) Thread.Sleep(100);
        if (ObserveWorker(task.WorkerIdentity) != false) throw new IOException("update cleanup worker exit or identity unconfirmed");
    }

    private static bool HasBackupData(string backup)
    {
        return Directory.Exists(backup)
            && (File.Exists(Path.Combine(backup, BackupReadyMarker))
                || File.Exists(Path.Combine(backup, "NexusPipeline.exe"))
                || Directory.Exists(Path.Combine(backup, "resources", "desktop")));
    }

    private static bool HasBackupExecutable(string backup)
    {
        return File.Exists(Path.Combine(backup, "NexusPipeline.exe"));
    }

    private static bool IsStagingValid(string stagedDir)
    {
        return Directory.Exists(stagedDir) && File.Exists(Path.Combine(stagedDir, "NexusPipeline.exe"));
    }

    private static void RequireDesktopFilesReleased(string root)
    {
        foreach (var file in ApplicationPayload.Files(root).Where(item => item.Path.StartsWith("resources/desktop/", StringComparison.Ordinal)))
        {
            using var probe = new FileStream(Path.Combine(root, file.Path), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
    }

    private static void ValidateStagedPath(string stagedDir)
    {
        PayloadPathSafety.RequireLinkFree(stagedDir);
        if (!Path.IsPathRooted(stagedDir))
        {
            throw new InvalidDataException("staging 路径必须是绝对路径");
        }
        string full = Path.GetFullPath(stagedDir);
        string root = Path.GetFullPath(AppPaths.UpdateDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("staging 路径必须位于 .nxp-update 内");
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        if (Directory.Exists(target) || File.Exists(target))
        {
            throw new IOException($"目标路径已存在：{target}");
        }
        Directory.CreateDirectory(target);
        foreach (string directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: false);
        }
    }

    private static void WriteVersionFile(string version)
    {
        Directory.CreateDirectory(AppPaths.AppRoot);
        JsonUtil.WriteAtomic(AppPaths.UpdateVersionFile, version + Environment.NewLine);
    }

    internal static UpdateFileRead<string> ReadVersionState(string? path = null)
    {
        string file = path ?? AppPaths.UpdateVersionFile;
        try
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(file); }
            catch (FileNotFoundException) { return new(UpdateFileState.Missing, null); }
            catch (DirectoryNotFoundException) { return new(UpdateFileState.Missing, null); }
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 || new FileInfo(file).Length > 128)
                throw new InvalidDataException("invalid update marker file");
            string version = File.ReadAllText(file).Trim();
            if (!NexusVersion.TryParse(version, out _)) throw new InvalidDataException("invalid update marker version");
            return new(UpdateFileState.Current, version);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        { return new(UpdateFileState.Unsupported, null, ex.Message); }
    }

    private static string? ReadVersionFile()
    {
        var result = ReadVersionState();
        return result.State == UpdateFileState.Unsupported
            ? throw new InvalidDataException("unsupported_update_marker: " + result.Error) : result.Value;
    }

    private static void TryWritePhase(UpdateTask task, string phase)
    {
        try
        {
            (task with { Phase = phase }).Write();
        }
        catch (Exception ex)
        {
            Logger.Error($"[更新] 写入 recovery journal 失败（保留现有现场）：{ex.Message}");
        }
    }

    /// <summary>
    /// 测试宿主的外部故障注入暂停点。正式构建不包含该环境变量路径，系统测试可在 journal
    /// 已原子写入后从外部强杀 worker，验证启动恢复的真实现场。
    /// </summary>
    private static void PauseForFaultInjection(string phase)
    {
#if NEXUS_TEST_HOST
        string? requestedPhase = Environment.GetEnvironmentVariable("NEXUS_TEST_UPDATE_PAUSE_PHASE")?.Trim();
        string? signalPath = Environment.GetEnvironmentVariable("NEXUS_TEST_UPDATE_PAUSE_FILE")?.Trim();
        if (!string.Equals(requestedPhase, phase, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(signalPath))
        {
            return;
        }
        try
        {
            string? parent = Path.GetDirectoryName(signalPath);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }
            File.WriteAllText(signalPath, phase);
            DateTime deadline = DateTime.UtcNow.AddSeconds(30);
            while (File.Exists(signalPath) && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(20);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[更新] 测试故障注入暂停点失败：{ex.Message}");
        }
#endif
    }

    private static void RetryRequired(Action action, string description)
    {
        Exception? last = null;
        for (int attempt = 1; attempt <= RequiredFileRetryCount; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (IOException ex)
            {
                last = ex;
            }
            catch (UnauthorizedAccessException ex)
            {
                last = ex;
            }
            if (attempt < RequiredFileRetryCount)
            {
                Logger.Warn($"[更新] {description}遇到临时文件占用，等待重试（{attempt}/{RequiredFileRetryCount}）：{last.Message}");
                Thread.Sleep(RequiredFileRetryDelay);
            }
        }
        throw new IOException($"{description}重试失败", last);
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void WriteRequiredText(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content + Environment.NewLine);
    }

    /// <summary>拉起 apply-update 子进程，Process.Start 返回 null 也视为失败。</summary>
    public static UpdateWorkerLaunch LaunchApplyWorker(string stagedDir, bool webOnly)
    {
        if (LaunchApplyOverride is not null)
        {
            try { return LaunchApplyOverride(stagedDir) ? UpdateWorkerLaunch.Started : UpdateWorkerLaunch.NotStarted; }
            catch { return UpdateWorkerLaunch.Unconfirmed; }
        }
        string? ownedWorker = null;
        string? ownedHash = null;
        bool armed = false;
        bool startAttempted = false;
        UpdateTask? journal = null;
        try
        {
            string sourceExe = Environment.ProcessPath ?? Path.Combine(AppPaths.AppRoot, "NexusPipeline.exe");
            journal = UpdateTask.Read() ?? throw new InvalidDataException("update worker journal missing");
            if (journal.WorkerLaunchPending || journal.WorkerIdentity is not null && ObserveWorker(journal.WorkerIdentity) != false)
                return UpdateWorkerLaunch.Unconfirmed;
            RetireExitedWorker(journal);
            Directory.CreateDirectory(AppPaths.UpdateWorkersDir);
            PayloadPathSafety.RequireLinkFree(AppPaths.UpdateWorkersDir);
            string workerExe = Path.Combine(AppPaths.UpdateWorkersDir, $"{WorkerImagePrefix}{Guid.NewGuid():N}.exe");
            File.Copy(sourceExe, workerExe, overwrite: false);
            ownedWorker = workerExe;
            ownedHash = ImageHash(workerExe);
            (journal with { WorkerSha256 = ownedHash, WorkerPath = workerExe, WorkerLaunchPending = true }).Write();
            var startInfo = new ProcessStartInfo(workerExe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppPaths.AppRoot,
            };
            startInfo.ArgumentList.Add("apply-update");
            startInfo.ArgumentList.Add("--app-root"); startInfo.ArgumentList.Add(AppPaths.AppRoot);
            startInfo.ArgumentList.Add("--staged");
            startInfo.ArgumentList.Add(stagedDir);
            if (webOnly)
            {
                startInfo.ArgumentList.Add("--web");
            }
            startAttempted = true;
            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                throw new System.ComponentModel.Win32Exception("Process.Start 未返回子进程");
            }
            armed = true;
            (journal with { WorkerIdentity = ProcessIdentity.Capture(process) ?? throw new IOException("update.worker_identity_unconfirmed"),
                WorkerSha256 = ownedHash, WorkerPath = workerExe, WorkerLaunchPending = false }).Write();
            Logger.Info($"[更新] 已拉起独立 apply-update worker：{Path.GetFileName(workerExe)}。");
            return UpdateWorkerLaunch.Started;
        }
        catch (Exception ex)
        {
            Logger.Error($"[更新] 拉起 apply-update 子进程失败：{ex.Message}");
            if (armed || startAttempted && ex is not System.ComponentModel.Win32Exception) return UpdateWorkerLaunch.Unconfirmed;
            if (ownedWorker is not null && ownedHash is not null) TryDeleteOwnedWorker(ownedWorker, ownedHash);
            try { journal?.Write(); }
            catch (Exception failure) { Logger.Warn("update_worker_preparation_preserved: " + failure.GetType().Name); }
            return UpdateWorkerLaunch.NotStarted;
        }
    }

    /// <summary>启动独立 recovery worker，避免当前新版本进程锁住待还原的 exe。</summary>
    public static UpdateWorkerLaunch LaunchRecoveryWorker(bool webOnly)
    {
        if (LaunchRecoveryOverride is not null)
        {
            try { return LaunchRecoveryOverride() ? UpdateWorkerLaunch.Started : UpdateWorkerLaunch.NotStarted; }
            catch { return UpdateWorkerLaunch.Unconfirmed; }
        }
        string? ownedWorker = null;
        string? ownedHash = null;
        bool armed = false;
        bool startAttempted = false;
        UpdateTask? journal = null;
        try
        {
            string sourceExe = Environment.ProcessPath ?? Path.Combine(AppPaths.AppRoot, "NexusPipeline.exe");
            journal = UpdateTask.Read() ?? throw new InvalidDataException("update recovery journal missing");
            if (journal.WorkerLaunchPending || journal.WorkerIdentity is not null && ObserveWorker(journal.WorkerIdentity) != false)
                return UpdateWorkerLaunch.Unconfirmed;
            RetireExitedWorker(journal);
            Directory.CreateDirectory(AppPaths.UpdateWorkersDir);
            PayloadPathSafety.RequireLinkFree(AppPaths.UpdateWorkersDir);
            string workerExe = Path.Combine(AppPaths.UpdateWorkersDir, $"{WorkerImagePrefix}{Guid.NewGuid():N}.exe");
            File.Copy(sourceExe, workerExe, overwrite: false);
            ownedWorker = workerExe;
            ownedHash = ImageHash(workerExe);
            (journal with { WorkerSha256 = ownedHash, WorkerPath = workerExe, WorkerLaunchPending = true }).Write();
            var startInfo = new ProcessStartInfo(workerExe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppPaths.AppRoot,
            };
            startInfo.ArgumentList.Add("recover-update");
            startInfo.ArgumentList.Add("--app-root"); startInfo.ArgumentList.Add(AppPaths.AppRoot);
            if (webOnly)
            {
                startInfo.ArgumentList.Add("--web");
            }
            startAttempted = true;
            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                throw new System.ComponentModel.Win32Exception("Process.Start 未返回 recovery 子进程");
            }
            armed = true;
            (journal with { WorkerIdentity = ProcessIdentity.Capture(process) ?? throw new IOException("update.worker_identity_unconfirmed"),
                WorkerSha256 = ownedHash, WorkerPath = workerExe, WorkerLaunchPending = false }).Write();
            Logger.Info($"[更新] 已拉起独立 recovery worker：{Path.GetFileName(workerExe)}。");
            return UpdateWorkerLaunch.Started;
        }
        catch (Exception ex)
        {
            Logger.Error($"[更新] 拉起 recovery worker 失败：{ex.Message}");
            if (armed || startAttempted && ex is not System.ComponentModel.Win32Exception) return UpdateWorkerLaunch.Unconfirmed;
            if (ownedWorker is not null && ownedHash is not null) TryDeleteOwnedWorker(ownedWorker, ownedHash);
            try { journal?.Write(); }
            catch (Exception failure) { Logger.Warn("update_worker_preparation_preserved: " + failure.GetType().Name); }
            return UpdateWorkerLaunch.NotStarted;
        }
    }

    private static void RetireExitedWorker(UpdateTask journal)
    {
        if (journal.WorkerIdentity is not { } identity) return;
        if (ObserveWorker(identity) != false || !UpdateInventory.Hash(journal.WorkerSha256))
            throw new IOException("update previous worker ownership unconfirmed");
        string root = Path.GetFullPath(AppPaths.UpdateWorkersDir).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(identity.ImageName), root, StringComparison.OrdinalIgnoreCase))
            throw new IOException("update worker path unconfirmed");
        if (File.Exists(identity.ImageName)) VerifiedFileDeletion.Delete(identity.ImageName, new FileInfo(identity.ImageName).Length, journal.WorkerSha256!);
    }

    private static void TryDeleteOwnedWorker(string path, string hash)
    {
        try { VerifiedFileDeletion.Delete(path, new FileInfo(path).Length, hash); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Logger.Warn("update failed worker preserved: " + error.GetType().Name); }
    }

    /// <summary>测试注入点：L2 单测替换真实子进程拉起。</summary>
    internal static Func<string, bool>? LaunchApplyOverride;

    /// <summary>测试注入点：L2 单测替换 recovery worker 拉起。</summary>
    internal static Func<bool>? LaunchRecoveryOverride;

    internal static string[] BuildServiceArguments(UpdateTask journal, bool webOnly)
    {
        journal.Validate();
        if (journal.DesktopResumeIntent is null || journal.TransactionId is null)
            throw new InvalidDataException("update.launch_intent_missing");
        var arguments = new List<string> { "restart" };
        if (webOnly) { arguments.Add("--web"); arguments.Add("--keep-alive"); }
        if (journal.RestartHandoffId is { } handoff) { arguments.Add("--handoff"); arguments.Add(handoff); }
        arguments.Add("--update-transaction"); arguments.Add(journal.TransactionId);
        arguments.Add("--desktop-intent"); arguments.Add(journal.DesktopResumeIntent.Mode);
        return arguments.ToArray();
    }

    internal static DesktopResumeIntent ReadLaunchIntent(string transaction, string mode)
    {
        if (!Guid.TryParseExact(transaction, "N", out _)) throw new InvalidDataException("update.launch_transaction_invalid");
        var intent = new DesktopResumeIntent(1, mode);
        intent.Validate();
        var task = UpdateTask.Read();
        if (task is not null)
        {
            if (task.TransactionId == transaction && task.DesktopResumeIntent == intent) return intent;
            throw new InvalidDataException("update.launch_transaction_mismatch");
        }
        string result = TransactionResultPath(transaction);
        PayloadPathSafety.RequireLinkFree(result);
        if (!File.Exists(result) || new FileInfo(result).Length > 64 * 1024) throw new InvalidDataException("update.launch_receipt_missing");
        using var document = JsonDocument.Parse(File.ReadAllBytes(result));
        var value = document.RootElement;
        string[] fields = ["TransactionId", "Version", "Succeeded", "Code", "AtUtc", "DesktopResumeIntent"];
        if (value.ValueKind != JsonValueKind.Object || !value.EnumerateObject().Select(property => property.Name).Order().SequenceEqual(fields.Order())
            || value.GetProperty("TransactionId").GetString() != transaction
            || value.GetProperty("Succeeded").ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !value.GetProperty("AtUtc").TryGetDateTimeOffset(out _)
            || value.GetProperty("Version").ValueKind != JsonValueKind.String || value.GetProperty("Code").ValueKind != JsonValueKind.String)
            throw new InvalidDataException("update.launch_receipt_mismatch");
        JsonElement saved = value.GetProperty("DesktopResumeIntent");
        if (saved.ValueKind != JsonValueKind.Object || !saved.EnumerateObject().Select(property => property.Name).Order().SequenceEqual(new[] { "Mode", "SchemaVersion" })
            || saved.GetProperty("SchemaVersion").GetRawText() != "1" || saved.GetProperty("Mode").GetString() != mode)
            throw new InvalidDataException("update.launch_receipt_intent_mismatch");
        return intent;
    }

    private static Process LaunchService(string installDir, bool webOnly, UpdateTask journal)
    {
        string exePath = Path.Combine(installDir, "NexusPipeline.exe");
        var startInfo = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in BuildServiceArguments(journal, webOnly)) startInfo.ArgumentList.Add(argument);
        Process? process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException("重新拉起宿主失败：Process.Start 未返回进程");
        }
        Logger.Info(webOnly
            ? "[更新] 已重新拉起宿主（web 模式）。"
            : "[更新] 已重新拉起宿主（服务模式）。");
        return process;
    }

    internal static string ImageHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    internal static string TransactionResultPath(string transactionId)
    {
        if (!Guid.TryParseExact(transactionId, "N", out _)) throw new InvalidDataException("update.transaction_id");
        return Path.Combine(AppPaths.StateDir, "updates", transactionId + ".result.json");
    }

    private static string StartupReceiptPath(string transactionId) => TransactionResultPath(transactionId).Replace(".result.json", ".startup.json", StringComparison.Ordinal);
    private sealed record StartupReceipt(string TransactionId, string Version, string ImageHash, UpdatePayloadIdentity Payload, ProcessIdentity Identity, DateTimeOffset ReadyAtUtc);

    internal static bool RequiresStartupQualification
    {
        get
        {
            var read = UpdateTask.ReadState();
            if (read.State == UpdateFileState.Unsupported) Volatile.Write(ref _startupRecoveryUnsafe, 1);
            return read.Value?.Phase == UpdatePhase.AwaitingStartup;
        }
    }

    /// <summary>Called only after runtime, plugins and the owning Control API have started, under the existing maintenance lease.</summary>
    internal static bool ConfirmStartupReadiness()
    {
        UpdateTask? task = UpdateTask.Read();
        if (task?.Phase != UpdatePhase.AwaitingStartup) return true;
        try
        {
            if (task.TransactionId is null || ObserveWorker(task.WorkerIdentity) != true || task.Version != UpdateService.CurrentVersion)
                throw new IOException("update.startup_identity");
            using var current = Process.GetCurrentProcess();
            var identity = ProcessIdentity.Capture(current) ?? throw new IOException("update.startup_process");
            string image = Environment.ProcessPath ?? throw new IOException("update.startup_image");
            if (!string.Equals(image, Path.Combine(AppPaths.AppRoot, "NexusPipeline.exe"), StringComparison.OrdinalIgnoreCase)
                || ImageHash(image) != task.TargetImageHash) throw new IOException("update.startup_hash");
            ApplicationPayload.VerifyFrozen(AppPaths.AppRoot, task.TargetPayload!, task.Version, installed: true);
            Directory.CreateDirectory(Path.GetDirectoryName(StartupReceiptPath(task.TransactionId))!);
            JsonUtil.WriteAtomic(StartupReceiptPath(task.TransactionId), JsonSerializer.Serialize(
                new StartupReceipt(task.TransactionId, task.Version, task.TargetImageHash!, task.TargetPayload!, identity, DateTimeOffset.UtcNow)));
            DateTime deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                string result = TransactionResultPath(task.TransactionId);
                if (File.Exists(result))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(result));
                    bool succeeded = doc.RootElement.GetProperty("Succeeded").GetBoolean()
                        && doc.RootElement.GetProperty("TransactionId").GetString() == task.TransactionId;
                    if (!succeeded) return false;
                    DateTime workerDeadline = DateTime.UtcNow.AddSeconds(10);
                    while (ObserveWorker(task.WorkerIdentity) == true && DateTime.UtcNow < workerDeadline) Thread.Sleep(100);
                    var committed = UpdateTask.Read() ?? throw new IOException("update committed journal missing");
                    CleanupAfterCompletion(committed);
                    return true;
                }
                if (ObserveWorker(task.WorkerIdentity) != true) return false;
                Thread.Sleep(100);
            }
        }
        catch (Exception ex) { Logger.Error($"[更新] 启动核对失败，保留事务备份：{ex.Message}"); }
        return false;
    }

    private static void WaitForStartupReceipt(UpdateTask task, Process candidate, ProcessIdentity identity)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (candidate.HasExited) throw new IOException("update.candidate_exited:" + candidate.ExitCode);
            string path = StartupReceiptPath(task.TransactionId!);
            if (File.Exists(path))
            {
                var receipt = JsonSerializer.Deserialize<StartupReceipt>(File.ReadAllText(path));
                if (receipt is null) throw new InvalidDataException("update.startup_receipt_null");
                if (receipt.TransactionId != task.TransactionId) throw new InvalidDataException("update.startup_receipt_transaction");
                if (receipt.Version != task.Version) throw new InvalidDataException("update.startup_receipt_version");
                if (receipt.Payload != task.TargetPayload) throw new InvalidDataException("update.startup_receipt_payload");
                if (receipt.ImageHash != task.TargetImageHash) throw new InvalidDataException("update.startup_receipt_declared_hash");
                if (!receipt.Identity.Matches(identity))
                    throw new InvalidDataException($"update.startup_receipt_process:pid={receipt.Identity.Pid == identity.Pid},time={receipt.Identity.StartTime == identity.StartTime},image={string.Equals(receipt.Identity.ImageName, identity.ImageName, StringComparison.OrdinalIgnoreCase)}");
                if (task.CreatedAt is null || receipt.ReadyAtUtc < task.CreatedAt)
                    throw new InvalidDataException("update.startup_receipt_time");
                if (ImageHash(identity.ImageName) != task.TargetImageHash)
                    throw new InvalidDataException("update.startup_receipt_actual_hash");
                return;
            }
            Thread.Sleep(100);
        }
        throw new TimeoutException("update.startup_readiness_timeout");
    }

    private static ProcessIdentity? CaptureCandidateIdentity(Process candidate, string executable)
    {
        // Process.Start can return before MainModule is readable. Capture's
        // basename fallback is useful for diagnostics, but cannot qualify an update.
        DateTime deadline = DateTime.UtcNow.AddSeconds(2);
        do
        {
            var identity = ProcessIdentity.Capture(candidate);
            if (identity is not null && string.Equals(identity.Value.ImageName, executable, StringComparison.OrdinalIgnoreCase))
                return identity;
            if (candidate.HasExited) return null;
            Thread.Sleep(25);
        } while (DateTime.UtcNow < deadline);
        return null;
    }

    internal static bool? ObserveWorker(ProcessIdentity? expected)
    {
        if (expected is null) return null;
        try
        {
            using var process = Process.GetProcessById(expected.Value.Pid);
            if (process.HasExited) return false;
            var current = ProcessIdentity.Capture(process);
            return current is null ? null : expected.Value.Matches(current.Value);
        }
        catch (ArgumentException) { return false; }
        catch (Exception) { return null; }
    }

    private static void WriteTransactionResult(UpdateTask task, bool succeeded, string code)
    {
        if (task.TransactionId is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(TransactionResultPath(task.TransactionId))!);
        JsonUtil.WriteAtomic(TransactionResultPath(task.TransactionId), JsonSerializer.Serialize(new
        { task.TransactionId, task.Version, Succeeded = succeeded, Code = code, AtUtc = DateTimeOffset.UtcNow, task.DesktopResumeIntent }));
    }
}
