using System.Diagnostics;
using System.Text.Json;
using NexusPipeline.Persistence;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services.Update;

/// <summary>更新任务标记：mode=apply（宿主已请求应用）/ defer（下次启动应用）/ completed（交换成功待清理）。</summary>
internal sealed record UpdateTask(string Mode, string Version, string StagedDir)
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
            return JsonSerializer.Deserialize<UpdateTask>(text, JsonOpts.Default);
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
        JsonUtil.WriteAtomic(file, JsonSerializer.Serialize(this, JsonOpts.Indented));
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
/// 更新应用的切换与收尾：apply-update 子进程（备份→交换→标记→重拉）与新实例启动收尾
/// （完成清理 / 失败回滚 / defer 自动应用）。只替换 nexus-pipeline.exe、wwwroot/、plugins/，
/// config/、data/、history/、logs/ 一律不写入；plugins/ 中包内不存在的用户自加子目录保留。
/// </summary>
internal static class UpdateApply
{
    /// <summary>apply-update 等待旧实例释放单实例互斥体的上限（真实墙钟，不随加速缩放）。</summary>
    private const int MutexWaitSeconds = 120;

    private const string MutexName = "NexusPipeline.SingleInstance";

    private const int RetryCount = 3;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>apply-update 子进程入口：等待宿主退出 → 备份 → 交换 → 写 .nxp-version → 重新拉起宿主。</summary>
    public static int RunApplyWorker(string stagedDir)
    {
        Logger.Info("[更新] apply-update 进程启动，等待主实例退出...");
        Audit.Log(Audit.System, "更新切换", "apply-update 进程启动");
        try
        {
            if (!WaitForHostExit(TimeSpan.FromSeconds(MutexWaitSeconds)))
            {
                Logger.Error("[更新] 等待主实例退出超时（120 秒），更新取消。");
                Audit.Log(Audit.System, "更新切换失败", "等待主实例退出超时");
                CleanupAfterFailure();
                return 1;
            }
            string installDir = AppPaths.AppRoot;
            string stageExe = Path.Combine(stagedDir, "nexus-pipeline.exe");
            if (!Directory.Exists(stagedDir) || !File.Exists(stageExe))
            {
                Logger.Error($"[更新] staging 目录无效：{stagedDir}");
                CleanupAfterFailure();
                return 1;
            }
            UpdateTask? task = UpdateTask.Read();
            string targetVersion = task?.Version ?? Path.GetFileName(stagedDir.TrimEnd(Path.DirectorySeparatorChar));

            // 1. 备份旧版本（exe / wwwroot / plugins）。
            string backup = AppPaths.UpdateBackupDir;
            Retry(() => TryDeleteDir(backup));
            Directory.CreateDirectory(backup);
            string oldExe = Path.Combine(installDir, "nexus-pipeline.exe");
            string oldWww = Path.Combine(installDir, "wwwroot");
            string oldPlugins = Path.Combine(installDir, "plugins");
            MoveToBackup(oldExe, Path.Combine(backup, "nexus-pipeline.exe"));
            MoveToBackup(oldWww, Path.Combine(backup, "wwwroot"));
            MoveToBackup(oldPlugins, Path.Combine(backup, "plugins"));

            // 2. 从 staging 交换新版本。
            SwapInto(Path.Combine(stagedDir, "nexus-pipeline.exe"), oldExe);
            SwapInto(Path.Combine(stagedDir, "wwwroot"), oldWww);
            SwapInto(Path.Combine(stagedDir, "plugins"), oldPlugins);
            // 用户自加插件目录保留：包内不存在的旧 plugins 子目录移回。
            PreserveUserPlugins(Path.Combine(backup, "plugins"), oldPlugins);

            // 3. 写应用成功标记 → 标记完成 → 重新拉起宿主。
            WriteVersionFile(targetVersion);
            new UpdateTask("completed", targetVersion, stagedDir).Write();
            Audit.Log(Audit.System, "更新应用完成", $"v{targetVersion}（staging：{stagedDir}）");
            Logger.Info($"[更新] 文件交换完成（v{targetVersion}），正在重新拉起宿主。");
            LaunchService(installDir);
            return 0;
        }
        catch (Exception ex)
        {
            Logger.Error($"[更新] 应用失败：{ex.Message}");
            Audit.Log(Audit.System, "更新切换失败", ex.Message);
            try
            {
                Rollback();
            }
            catch (Exception rollbackEx)
            {
                Logger.Error($"[更新] 回滚失败（保留标记，下次启动重试）：{rollbackEx.Message}");
            }
            CleanupAfterFailure();
            return 1;
        }
    }

    /// <summary>
    /// 新实例启动收尾：返回 true 表示本进程应退出（defer 自动应用已拉起 apply-update 子进程）。
    /// 完成 → 清理 staging/backup/标记并审计；apply 不完整 → 从备份回滚；defer → 转 apply 并拉起子进程。
    /// </summary>
    public static bool RunStartupFinalization()
    {
        string? appliedVersion = ReadVersionFile();
        if (appliedVersion is not null)
        {
            UpdateTask? task = UpdateTask.Read();
            Logger.Info($"[更新] 检测到已完成更新（v{appliedVersion}），清理暂存与备份。");
            Audit.Log(Audit.System, "更新完成", task is null ? $"v{appliedVersion}" : $"v{task.Version} → v{appliedVersion}");
            CleanupAfterCompletion();
            return false;
        }
        UpdateTask? pending = UpdateTask.Read();
        if (pending is null)
        {
            return false;
        }
        if (pending.Mode == "defer")
        {
            // 下次启动应用：转 apply 并拉起子进程，本进程退出（互斥体随进程结束释放）。
            Logger.Info("[更新] 检测到「下次启动更新」标记，开始应用。");
            Audit.Log(Audit.System, "更新应用", $"v{pending.Version}（defer 启动）");
            new UpdateTask("apply", pending.Version, pending.StagedDir).Write();
            LaunchApplyWorker(pending.StagedDir);
            return true;
        }
        if (pending.Mode == "apply")
        {
            // apply 标记但无成功标记 = 上次切换未完成 → 回滚。
            Logger.Warn("[更新] 检测到未完成的更新切换，启动时回滚。");
            Audit.Log(Audit.System, "更新失败已回滚", $"v{pending.Version}（切换未完成）");
            try
            {
                Rollback();
            }
            catch (Exception ex)
            {
                Logger.Error($"[更新] 启动回滚失败（标记保留，下次启动重试）：{ex.Message}");
                return false;
            }
            CleanupAfterFailure();
            return false;
        }
        return false;
    }

    /* ---------------- 内部实现 ---------------- */

    private static bool WaitForHostExit(TimeSpan timeout)
    {
        using var probe = new Mutex(false, MutexName);
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
                    catch (Exception)
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

    private static void Retry(Action action)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex)
            {
                if (attempt >= RetryCount)
                {
                    throw new IOException($"文件操作重试失败：{ex.Message}", ex);
                }
                Logger.Warn($"[更新] 文件操作失败，重试（{attempt}/{RetryCount}）：{ex.Message}");
                Thread.Sleep(RetryDelay);
            }
        }
    }

    private static void TryDeleteDir(string dir)
    {
        try
        {
            ConfigSwapPrimitives.TryDeleteDir(dir);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[更新] 清理目录失败（{dir}）：{ex.Message}");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[更新] 清理文件失败（{path}）：{ex.Message}");
        }
    }

    private static void MoveToBackup(string source, string target)
    {
        if (!Directory.Exists(source) && !File.Exists(source))
        {
            return;
        }
        Retry(() =>
        {
            if (Directory.Exists(source))
            {
                Directory.Move(source, target);
            }
            else
            {
                File.Move(source, target);
            }
        });
    }

    private static void SwapInto(string source, string target)
    {
        Retry(() =>
        {
            if (Directory.Exists(source))
            {
                TryDeleteDir(target);
                Directory.Move(source, target);
            }
            else
            {
                TryDeleteFile(target);
                File.Copy(source, target, overwrite: true);
            }
        });
    }

    private static void PreserveUserPlugins(string backupPlugins, string installPlugins)
    {
        if (!Directory.Exists(backupPlugins))
        {
            return;
        }
        foreach (string oldDir in Directory.GetDirectories(backupPlugins))
        {
            string name = Path.GetFileName(oldDir);
            string newDir = Path.Combine(installPlugins, name);
            if (!Directory.Exists(newDir))
            {
                Retry(() => Directory.Move(oldDir, newDir));
                Logger.Info($"[更新] 已保留用户自加插件目录：{name}");
            }
        }
    }

    private static void Rollback()
    {
        string backup = AppPaths.UpdateBackupDir;
        if (!Directory.Exists(backup))
        {
            return;
        }
        string installDir = AppPaths.AppRoot;
        RestoreFromBackup(Path.Combine(backup, "nexus-pipeline.exe"), Path.Combine(installDir, "nexus-pipeline.exe"));
        RestoreFromBackup(Path.Combine(backup, "wwwroot"), Path.Combine(installDir, "wwwroot"));
        RestoreFromBackup(Path.Combine(backup, "plugins"), Path.Combine(installDir, "plugins"));
        Retry(() => TryDeleteDir(backup));
        Audit.Log(Audit.System, "更新回滚完成", "旧版本文件已还原");
    }

    private static void RestoreFromBackup(string backupItem, string target)
    {
        if (!Directory.Exists(backupItem) && !File.Exists(backupItem))
        {
            return;
        }
        Retry(() =>
        {
            if (Directory.Exists(backupItem))
            {
                TryDeleteDir(target);
                Directory.Move(backupItem, target);
            }
            else
            {
                TryDeleteFile(target);
                File.Move(backupItem, target);
            }
        });
    }

    private static void CleanupAfterCompletion()
    {
        TryDeleteDir(AppPaths.UpdateDir);
        TryDeleteDir(AppPaths.UpdateBackupDir);
        TryDeleteDir(Path.GetDirectoryName(AppPaths.UpdateBackupDir)!);
        UpdateTask.Clear();
        DeleteVersionFile();
    }

    private static void CleanupAfterFailure()
    {
        TryDeleteDir(AppPaths.UpdateDir);
        UpdateTask.Clear();
        DeleteVersionFile();
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

    private static void DeleteVersionFile()
    {
        try
        {
            if (File.Exists(AppPaths.UpdateVersionFile))
            {
                File.Delete(AppPaths.UpdateVersionFile);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[更新] 清理版本标记失败：{ex.Message}");
        }
    }

    /// <summary>拉起 apply-update 子进程（继承管理员权限，detached）。</summary>
    public static bool LaunchApplyWorker(string stagedDir)
    {
        if (LaunchApplyOverride is not null)
        {
            return LaunchApplyOverride(stagedDir);
        }
        try
        {
            string exePath = Environment.ProcessPath ?? Path.Combine(AppPaths.AppRoot, "nexus-pipeline.exe");
            Process.Start(new ProcessStartInfo(exePath)
            {
                Arguments = $"apply-update --staged \"{stagedDir}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            Logger.Info("[更新] 已拉起 apply-update 子进程。");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"[更新] 拉起 apply-update 子进程失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>测试注入点：L2 单测替换真实子进程拉起（避免拉起 testhost）。生产保持 null。</summary>
    internal static Func<string, bool>? LaunchApplyOverride;

    private static void LaunchService(string installDir)
    {
        string exePath = Path.Combine(installDir, "nexus-pipeline.exe");
        Process.Start(new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        Logger.Info("[更新] 已重新拉起宿主（服务模式）。");
    }
}