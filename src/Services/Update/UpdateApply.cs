using System.Diagnostics;
using System.Text.Json;
using NexusPipeline.Persistence;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services.Update;

internal static class UpdatePhase
{
    public const string Deferred = "Deferred";
    public const string ApplyRequested = "ApplyRequested";
    public const string BackupPreparing = "BackupPreparing";
    public const string BackupReady = "BackupReady";
    public const string SwapInProgress = "SwapInProgress";
    public const string SwapReady = "SwapReady";
    // 兼容读取旧版本 journal；v0.10.8 不再有插件保留阶段。
    public const string PreserveComplete = "PreserveComplete";
    public const string Committed = "Committed";
    public const string RollbackPending = "RollbackPending";
    public const string RollbackConfirmed = "RollbackConfirmed";
}

/// <summary>
/// 更新事务 journal。旧版只有 Mode/Version/StagedDir 时按兼容规则推导 Phase；新事务每个关键阶段原子写入。
/// </summary>
internal sealed record UpdateTask(
    string Mode,
    string Version,
    string StagedDir,
    string Phase = "",
    DateTimeOffset? CreatedAt = null)
{
    public static UpdateTask? Read(string? path = null)
    {
        string file = path ?? AppPaths.UpdateTaskFile;
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }
            string text = File.ReadAllText(file).Replace("\uFEFF", "");
            UpdateTask? task = JsonSerializer.Deserialize<UpdateTask>(text, JsonOpts.Default);
            if (task is null)
            {
                return null;
            }
            string phase = string.IsNullOrWhiteSpace(task.Phase)
                ? task.Mode switch
                {
                    "apply" => UpdatePhase.ApplyRequested,
                    "completed" => UpdatePhase.Committed,
                    _ => UpdatePhase.Deferred,
                }
                : task.Phase;
            return task with { Phase = phase };
        }
        catch (Exception ex)
        {
            Logger.Warn($"[更新] 读取任务标记失败：{ex.Message}");
            return null;
        }
    }

    public void Write(string? path = null)
    {
        string file = path ?? AppPaths.UpdateTaskFile;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        JsonUtil.WriteAtomic(file, JsonSerializer.Serialize(this with
        {
            CreatedAt = CreatedAt ?? DateTimeOffset.UtcNow,
        }, JsonOpts.Indented));
    }

    public static void Clear(string? path = null)
    {
        string file = path ?? AppPaths.UpdateTaskFile;
        try
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[更新] 清理任务标记失败：{ex.Message}");
        }
    }
}

/// <summary>
/// 更新应用的切换与收尾：apply-update 子进程（备份→交换→标记→重拉）与新实例启动收尾。
/// 旧版本备份在 commit 前始终是 immutable snapshot；回滚失败时 backup、journal、staging 全部保留。
/// 只替换 nexus-pipeline.exe、wwwroot/；plugins、config、data、history、logs 均属于用户运行数据。
/// </summary>
internal static class UpdateApply
{
    private const int MutexWaitSeconds = 120;
    private const string BackupReadyMarker = ".backup-ready";
    private const string WorkerImagePrefix = ".nxp-update-worker-";
    private const int RequiredFileRetryCount = 10;
    private static readonly TimeSpan RequiredFileRetryDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>apply-update 子进程入口：等待宿主退出 → 建立不可变 backup → 交换 → commit → 重拉宿主。</summary>
    public static int RunApplyWorker(string stagedDir)
    {
        Logger.Info("[更新] apply-update 进程启动，等待主实例退出...");
        Audit.Log(Audit.System, "更新切换", "apply-update 进程启动");
        UpdateTask? task = UpdateTask.Read();
        if (File.Exists(AppPaths.UpdateTaskFile) && task is null)
        {
            Logger.Error("[更新] 更新 journal 无法读取，拒绝执行切换。");
            return 1;
        }
        string targetVersion = task?.Version ?? Path.GetFileName(stagedDir.TrimEnd(Path.DirectorySeparatorChar));
        UpdateTask journal = task ?? new UpdateTask("apply", targetVersion, stagedDir, UpdatePhase.ApplyRequested, DateTimeOffset.UtcNow);
        bool staleBackupDetected = false;
        bool backupComplete = false;
        try
        {
            ValidateStagedPath(stagedDir);
            if (!WaitForHostExit(TimeSpan.FromSeconds(MutexWaitSeconds)))
            {
                Logger.Error("[更新] 等待主实例退出超时（120 秒），更新取消。");
                Audit.Log(Audit.System, "更新切换失败", "等待主实例退出超时");
                AbortBeforeBackup(journal);
                return 1;
            }

            string installDir = AppPaths.AppRoot;
            string stageExe = Path.Combine(stagedDir, "nexus-pipeline.exe");
            if (!Directory.Exists(stagedDir) || !File.Exists(stageExe))
            {
                throw new InvalidDataException($"staging 目录无效：{stagedDir}");
            }

            string backup = AppPaths.UpdateBackupDir;
            staleBackupDetected = Directory.Exists(backup) || File.Exists(backup);
            EnsureNoStaleBackup(backup);
            journal = journal with { Mode = "apply", Phase = UpdatePhase.BackupPreparing };
            journal.Write();
            CreateBackupSnapshot(installDir, backup);
            backupComplete = true;
            journal = journal with { Phase = UpdatePhase.BackupReady };
            journal.Write();

            string oldExe = Path.Combine(installDir, "nexus-pipeline.exe");
            string oldWww = Path.Combine(installDir, "wwwroot");
            journal = journal with { Phase = UpdatePhase.SwapInProgress };
            journal.Write();
            SwapInto(Path.Combine(stagedDir, "nexus-pipeline.exe"), oldExe);
            SwapInto(Path.Combine(stagedDir, "wwwroot"), oldWww);
            journal = journal with { Phase = UpdatePhase.SwapReady };
            journal.Write();

            WriteVersionFile(targetVersion);
            journal = journal with { Mode = "completed", Phase = UpdatePhase.Committed };
            journal.Write();
            Audit.Log(Audit.System, "更新应用完成", $"v{targetVersion}（staging：{stagedDir}）");
            Logger.Info($"[更新] 文件交换完成（v{targetVersion}），正在重新拉起宿主。");
            LaunchService(installDir);
            return 0;
        }
        catch (Exception ex)
        {
            Logger.Error($"[更新] 应用失败：{ex.Message}");
            Audit.Log(Audit.System, "更新切换失败", ex.Message);
            UpdateTask? current = UpdateTask.Read();
            if (ReadVersionFile() is not null || string.Equals(current?.Phase, UpdatePhase.Committed, StringComparison.Ordinal))
            {
                // 新版本已经 commit：保留 marker/journal/backup，交给新实例启动收尾。
                Logger.Error("[更新] 新版本已提交但启动收尾未完成，保留 journal 与 backup 供下次启动处理。");
                return 1;
            }
            if (staleBackupDetected)
            {
                // 旧 backup 的归属无法在 worker 内安全分类：严禁拿它作为本次回滚源，也严禁自动删除。
                Logger.Error("[更新] 检测到未分类旧 backup，保留 backup/journal，等待启动恢复或人工处理。");
                return 1;
            }
            if (backupComplete)
            {
                try
                {
                    Rollback(journal with { Phase = UpdatePhase.RollbackPending });
                    CleanupAfterRollback();
                    Logger.Warn("[更新] 已完成失败回滚；更新现场清理将在确认成功后执行。");
                }
                catch (Exception rollbackEx)
                {
                    Logger.Error($"[更新] 回滚失败（保留 backup/journal/staging，下次启动重试）：{rollbackEx.Message}");
                    TryWritePhase(journal, UpdatePhase.RollbackPending);
                }
            }
            else
            {
                AbortBeforeBackup(journal);
            }
            return 1;
        }
    }

    /// <summary>
    /// 新实例启动收尾：完成 commit 后只在所有临时项清理成功时删除 version marker；
    /// apply/defer 启动失败保留 journal，rollback 失败保留 backup 与 journal。
    /// </summary>
    public static bool RunStartupFinalization()
    {
        string? appliedVersion = ReadVersionFile();
        if (appliedVersion is not null)
        {
            UpdateTask? task = UpdateTask.Read();
            Logger.Info($"[更新] 检测到已完成更新（v{appliedVersion}），清理暂存与备份。");
            try
            {
                CleanupAfterCompletion();
                Audit.Log(Audit.System, "更新完成", task is null ? $"v{appliedVersion}" : $"v{task.Version} → v{appliedVersion}");
            }
            catch (Exception ex)
            {
                Logger.Error($"[更新] 完成收尾清理失败（保留 version marker/journal，启动时重试）：{ex.Message}");
            }
            CleanupWorkerImages();
            return false;
        }

        if (!File.Exists(AppPaths.UpdateTaskFile))
        {
            return false;
        }
        UpdateTask? pending = UpdateTask.Read();
        if (pending is null)
        {
            Logger.Error("[更新] journal 存在但无法读取，保留现场并停止自动更新。");
            return false;
        }

        if (pending.Phase == UpdatePhase.BackupPreparing)
        {
            // backup marker 尚未写入，旧安装仍应保持完整；只清理未完成的准备现场。
            AbortBeforeBackup(pending);
            return false;
        }

        if (pending.Mode == "defer" || pending.Phase == UpdatePhase.Deferred)
        {
            if (!IsStagingValid(pending.StagedDir))
            {
                Logger.Error("[更新] defer staging 无效，保留 journal 供人工处理。");
                return false;
            }
            Logger.Info("[更新] 检测到「下次启动更新」标记，开始应用。");
            Audit.Log(Audit.System, "更新应用", $"v{pending.Version}（defer 启动）");
            UpdateTask apply = pending with { Mode = "apply", Phase = UpdatePhase.ApplyRequested };
            try
            {
                apply.Write();
                if (!LaunchApplyWorker(pending.StagedDir))
                {
                    Logger.Error("[更新] defer 启动时无法拉起 apply-update，保留 defer journal，当前进程继续运行。");
                    pending.Write();
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"[更新] defer 启动失败（保留 journal）：{ex.Message}");
                return false;
            }
        }

        if (pending.Mode == "apply")
        {
            if (pending.Phase is UpdatePhase.ApplyRequested or UpdatePhase.Deferred
                && !HasBackupData(AppPaths.UpdateBackupDir))
            {
                // worker 可能在 launch 后、建立 backup 前崩溃；重试仍然安全。
                if (LaunchApplyWorker(pending.StagedDir))
                {
                    return true;
                }
                Logger.Error("[更新] 未完成 apply 无法重新拉起 worker，保留 journal。");
                return false;
            }

            Logger.Warn($"[更新] 检测到未完成的更新切换（phase={pending.Phase}），启动时回滚。");
            Audit.Log(Audit.System, "更新失败已回滚", $"v{pending.Version}（切换未完成）");
            try
            {
                Rollback(pending with { Phase = UpdatePhase.RollbackPending });
                CleanupAfterRollback();
            }
            catch (Exception ex)
            {
                Logger.Error($"[更新] 启动回滚失败（保留 backup/journal，下次启动重试）：{ex.Message}");
                TryWritePhase(pending, UpdatePhase.RollbackPending);
            }
        }
        else if (pending.Phase == UpdatePhase.RollbackConfirmed)
        {
            try
            {
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
        }
        return false;
    }

    /* ---------------- 事务文件操作 ---------------- */

    private static bool WaitForHostExit(TimeSpan timeout)
    {
        using var probe = new Mutex(false, StartupPipeline.SingleInstanceMutexName);
        DateTime deadline = DateTime.Now + timeout;
        while (DateTime.Now < deadline)
        {
            try
            {
                if (probe.WaitOne(500))
                {
                    try
                    {
                        probe.ReleaseMutex();
                    }
                    catch
                    {
                    }
                    return true;
                }
            }
            catch (AbandonedMutexException)
            {
                return true;
            }
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

    private static void CreateBackupSnapshot(string installDir, string backup)
    {
        CopySnapshotItem(Path.Combine(installDir, "nexus-pipeline.exe"), Path.Combine(backup, "nexus-pipeline.exe"));
        CopySnapshotItem(Path.Combine(installDir, "wwwroot"), Path.Combine(backup, "wwwroot"));
        WriteRequiredText(Path.Combine(backup, BackupReadyMarker), DateTimeOffset.UtcNow.ToString("O"));
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

    private static void SwapInto(string source, string target)
    {
        if (!Directory.Exists(source) && !File.Exists(source))
        {
            throw new FileNotFoundException("更新 staging 缺少交换项", source);
        }
        DeletePathRequired(target);
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
        RestoreFromBackup(Path.Combine(backup, "nexus-pipeline.exe"), Path.Combine(installDir, "nexus-pipeline.exe"));
        RestoreFromBackup(Path.Combine(backup, "wwwroot"), Path.Combine(installDir, "wwwroot"));
        // 兼容 v0.10.7 更新 journal：旧 updater 会把 plugins 放入 backup，
        // v0.10.8 自身的 backup 不包含该目录，因此仅在旧现场实际存在时恢复。
        RestoreFromBackup(Path.Combine(backup, "plugins"), Path.Combine(installDir, "plugins"));
        UpdateTask confirmed = journal with { Mode = "apply", Phase = UpdatePhase.RollbackConfirmed };
        confirmed.Write();
        Audit.Log(Audit.System, "更新回滚完成", "旧版本文件已从 immutable backup 还原");
    }

    private static void RestoreFromBackup(string backupItem, string target)
    {
        if (!Directory.Exists(backupItem) && !File.Exists(backupItem))
        {
            return;
        }
        string temp = target + ".recovery-" + Guid.NewGuid().ToString("N");
        try
        {
            if (Directory.Exists(backupItem))
            {
                CopyDirectory(backupItem, temp);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
                File.Copy(backupItem, temp, overwrite: false);
            }
            DeletePathRequired(target);
            if (Directory.Exists(temp))
            {
                RetryRequired(() => Directory.Move(temp, target), $"恢复目录 {temp} → {target}");
            }
            else
            {
                RetryRequired(() => File.Move(temp, target), $"恢复文件 {temp} → {target}");
            }
        }
        catch
        {
            TryDeletePath(temp);
            throw;
        }
    }

    private static void CleanupAfterCompletion()
    {
        DeletePathRequired(AppPaths.UpdateDir);
        DeletePathRequired(AppPaths.UpdateBackupDir);
        DeleteEmptyDirectoryRequired(Path.GetDirectoryName(AppPaths.UpdateBackupDir)!);
        DeleteFileRequired(AppPaths.UpdateVersionFile);
    }

    private static void CleanupAfterRollback()
    {
        // 只有 RollbackConfirmed 才允许进入这里；backup/journal 在每一步失败时继续保留。
        DeletePathRequired(AppPaths.UpdateBackupDir);
        DeleteEmptyDirectoryRequired(Path.GetDirectoryName(AppPaths.UpdateBackupDir)!);
        // backup 清理成功后才删除 journal；如果 backup 删除失败，journal 继续驱动下一次 recovery。
        DeletePathRequired(AppPaths.UpdateDir);
    }

    private static void AbortBeforeBackup(UpdateTask journal)
    {
        try
        {
            DeletePathRequired(journal.StagedDir);
            DeletePathRequired(Path.Combine(AppPaths.UpdateDir, "staging"));
            DeletePathRequired(AppPaths.UpdateBackupDir);
            DeleteEmptyDirectoryRequired(Path.GetDirectoryName(AppPaths.UpdateBackupDir)!);
            // journal 最后删除，前面的任一步失败都会保留它供下次启动重试。
            DeleteFileRequired(AppPaths.UpdateTaskFile);
            TryDeleteEmptyDirectory(AppPaths.UpdateDir);
        }
        catch (Exception ex)
        {
            Logger.Error($"[更新] 交换尚未开始，但清理失败；保留 journal 供下次启动处理：{ex.Message}");
            TryWritePhase(journal, UpdatePhase.BackupPreparing);
        }
    }

    private static bool HasBackupData(string backup)
    {
        return Directory.Exists(backup)
            && (File.Exists(Path.Combine(backup, BackupReadyMarker))
                || File.Exists(Path.Combine(backup, "nexus-pipeline.exe"))
                || Directory.Exists(Path.Combine(backup, "wwwroot")));
    }

    private static bool IsStagingValid(string stagedDir)
    {
        return Directory.Exists(stagedDir) && File.Exists(Path.Combine(stagedDir, "nexus-pipeline.exe"));
    }

    private static void ValidateStagedPath(string stagedDir)
    {
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

    private static string? ReadVersionFile()
    {
        try
        {
            return File.Exists(AppPaths.UpdateVersionFile)
                ? File.ReadAllText(AppPaths.UpdateVersionFile).Trim()
                : null;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[更新] 读取版本标记失败：{ex.Message}");
            return null;
        }
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

    private static void DeletePathRequired(string path)
    {
        RetryRequired(() =>
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }, $"删除 {path}");
    }

    private static void DeleteFileRequired(string path)
    {
        RetryRequired(() =>
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }, $"删除文件 {path}");
    }

    private static void DeleteEmptyDirectoryRequired(string path)
    {
        RetryRequired(() =>
        {
            if (Directory.Exists(path))
            {
                if (Directory.EnumerateFileSystemEntries(path).Any())
                {
                    throw new IOException($"目录仍有未分类内容：{path}");
                }
                Directory.Delete(path);
            }
        }, $"删除空目录 {path}");
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

    private static void TryDeletePath(string path)
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

    /// <summary>
    /// 清理上一次更新留下的 worker 镜像。worker 与目标 exe 同根目录，故 AppPaths.AppRoot
    /// 仍然指向真实安装目录；镜像进程退出后由新实例完成最终删除。
    /// </summary>
    internal static void CleanupWorkerImages()
    {
        string currentProcess = Path.GetFullPath(Environment.ProcessPath ?? string.Empty);
        try
        {
            foreach (string worker in Directory.EnumerateFiles(
                AppPaths.AppRoot,
                WorkerImagePrefix + "*.exe",
                SearchOption.TopDirectoryOnly))
            {
                if (string.Equals(Path.GetFullPath(worker), currentProcess, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool deleted = false;
                for (int attempt = 1; attempt <= RequiredFileRetryCount; attempt++)
                {
                    try
                    {
                        File.Delete(worker);
                        deleted = !File.Exists(worker);
                        if (deleted)
                        {
                            break;
                        }
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                    if (attempt < RequiredFileRetryCount)
                    {
                        Thread.Sleep(RequiredFileRetryDelay);
                    }
                }
                if (!deleted && File.Exists(worker))
                {
                    Logger.Warn($"[更新] worker 镜像仍被占用，留待下次启动清理：{worker}");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[更新] 扫描 worker 镜像失败：{ex.Message}");
        }
    }

    /// <summary>拉起 apply-update 子进程，Process.Start 返回 null 也视为失败。</summary>
    public static bool LaunchApplyWorker(string stagedDir)
    {
        if (LaunchApplyOverride is not null)
        {
            return LaunchApplyOverride(stagedDir);
        }
        try
        {
            string sourceExe = Environment.ProcessPath ?? Path.Combine(AppPaths.AppRoot, "nexus-pipeline.exe");
            string workerExe = Path.Combine(AppPaths.AppRoot, $"{WorkerImagePrefix}{Guid.NewGuid():N}.exe");
            File.Copy(sourceExe, workerExe, overwrite: false);
            var startInfo = new ProcessStartInfo(workerExe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppPaths.AppRoot,
            };
            startInfo.ArgumentList.Add("apply-update");
            startInfo.ArgumentList.Add("--staged");
            startInfo.ArgumentList.Add(stagedDir);
            Process? process = Process.Start(startInfo);
            if (process is null)
            {
                throw new InvalidOperationException("Process.Start 未返回子进程");
            }
            process.Dispose();
            Logger.Info($"[更新] 已拉起独立 apply-update worker：{Path.GetFileName(workerExe)}。");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"[更新] 拉起 apply-update 子进程失败：{ex.Message}");
            CleanupWorkerImages();
            return false;
        }
    }

    /// <summary>测试注入点：L2 单测替换真实子进程拉起。</summary>
    internal static Func<string, bool>? LaunchApplyOverride;

    private static void LaunchService(string installDir)
    {
        string exePath = Path.Combine(installDir, "nexus-pipeline.exe");
        Process? process = Process.Start(new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (process is null)
        {
            throw new InvalidOperationException("重新拉起宿主失败：Process.Start 未返回进程");
        }
        process.Dispose();
        Logger.Info("[更新] 已重新拉起宿主（服务模式）。");
    }
}
