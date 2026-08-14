using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services;

/// <summary>配置交换会话标记：交换开始写入、完成删除；崩溃后可据此恢复（安全优先：原配置必还原）。</summary>
internal sealed class ConfigSessionMark
{
    public string ScriptId { get; set; } = "";

    public string UserName { get; set; } = "";

    public string ConfigPath { get; set; } = "";

    public string OriginalKind { get; set; } = "missing";

    public string Phase { get; set; } = "run";

    public DateTime StartedAt { get; set; } = DateTime.Now;

    /// <summary>本次编辑会话由宿主生成了配置模板（重启恢复时清理 config 位置的编辑产物，还原编辑前状态）。</summary>
    public bool GeneratedTemplate { get; set; }

    /// <summary>模板目录复制生成的文件清单（相对 configPath 父目录，v0.6.3+ 模板目录形态；cancel/重启恢复按清单精确清理）。</summary>
    public List<string> TemplateFiles { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static string MarkFile(string scriptId, string userName)
    {
        return Path.Combine(AppPaths.DataDir, scriptId, userName, ".session");
    }

    public void Write()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MarkFile(ScriptId, UserName))!);
        JsonUtil.WriteAtomic(MarkFile(ScriptId, UserName), JsonSerializer.Serialize(this, Options));
    }

    public static ConfigSessionMark? TryRead(string scriptId, string userName)
    {
        string file = MarkFile(scriptId, userName);
        if (!File.Exists(file))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<ConfigSessionMark>(File.ReadAllText(file), Options);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 读取配置会话标记失败（{file}）：{ex.Message}");
            return null;
        }
    }

    public static void Clear(string scriptId, string userName)
    {
        try
        {
            File.Delete(MarkFile(scriptId, userName));
        }
        catch
        {
        }
    }
}

/// <summary>编辑配置会话（WebServer 持有的进程句柄与标记）。</summary>
internal sealed class EditSession
{
    public required ScriptInstance Script { get; init; }

    public required ScriptUser User { get; init; }

    public Process? Process { get; set; }

    public ConfigSessionMark Mark { get; init; } = new();

    /// <summary>本次会话由宿主生成了配置模板（cancel 时需清理生成文件）。</summary>
    public bool GeneratedConfigTemplate { get; set; }
}

/// <summary>
/// 配置交换会话/恢复层（v0.5.0 从 UserConfigManager 拆出）：配置替换（replaceConfigs）、
/// .session 标记、操作前自愈 + 启动扫描恢复 + 后台延迟重试。文件原语见 <see cref="ConfigSwapPrimitives"/>，
/// 数据目录定位见 <see cref="ConfigSwapPaths"/>。
/// </summary>
internal static class ConfigSwapSession
{
    /* ---------------- 配置替换（插队替换 replaceConfigs） ---------------- */

    /// <summary>解析配置替换目标：config 为单文件时仅允许替换该文件本身（rel 须等于文件名，忽略大小写），目录时走相对路径边界解析。</summary>
    private static string? ResolveConfigTarget(string configPath, string rel)
    {
        if (File.Exists(configPath))
        {
            return string.Equals(Path.GetFileName(configPath), rel, StringComparison.OrdinalIgnoreCase)
                ? Path.GetFullPath(configPath)
                : null;
        }
        return JudgeScriptRunner.ResolveWithin(configPath, rel);
    }

    /// <summary>应用配置替换：把 script 目录内文件复制覆盖到 config 对应位置；首次替换前备份原始内容到 swap-backup（含 .meta 记录 configPath 与新增文件清单）。</summary>
    public static string? ApplyConfigReplacements(string scriptId, string? userName, string configPath, List<string> replacements)
    {
        string scriptDir = ConfigSwapPaths.ScriptDir(scriptId, userName);
        string backupDir = ConfigSwapPaths.ReplaceBackupDir(scriptId, userName);
        string metaPath = Path.Combine(backupDir, ".meta");
        List<string> newFiles = ReadMetaNewFiles(metaPath);
        foreach (string rel in replacements)
        {
            string? source = JudgeScriptRunner.ResolveWithin(scriptDir, rel);
            if (source is null || !File.Exists(source))
            {
                Logger.Warn($"[警告] 配置替换源文件无效（{rel}），跳过");
                continue;
            }
            string? target = ResolveConfigTarget(configPath, rel);
            if (target is null)
            {
                Logger.Warn($"[警告] 配置替换目标越界（{rel}），跳过");
                continue;
            }
            try
            {
                if (File.Exists(target))
                {
                    if (!newFiles.Contains(rel, StringComparer.OrdinalIgnoreCase))
                    {
                        string backupFile = Path.Combine(backupDir, rel);
                        if (!File.Exists(backupFile))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
                            File.Copy(target, backupFile, overwrite: true);
                        }
                    }
                }
                else
                {
                    newFiles.Add(rel);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, overwrite: true);
                Logger.Info($"[配置替换] 脚本「{scriptId}」已替换配置：{target} ← {rel}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[警告] 配置替换失败（{rel}）：{ex.Message}");
            }
        }
        if (newFiles.Count > 0 || (Directory.Exists(backupDir) && Directory.EnumerateFileSystemEntries(backupDir).Any()))
        {
            Directory.CreateDirectory(backupDir);
            File.WriteAllText(metaPath, JsonSerializer.Serialize(new { configPath, newFiles }));
        }
        return null;
    }

    /// <summary>读取 .meta 中记录的新增文件清单（多轮替换累积）。</summary>
    private static List<string> ReadMetaNewFiles(string metaPath)
    {
        var list = new List<string>();
        if (!File.Exists(metaPath))
        {
            return list;
        }
        try
        {
            JsonNode? node = JsonNode.Parse(File.ReadAllText(metaPath));
            if (node?["newFiles"] is JsonArray arr)
            {
                foreach (JsonNode? item in arr)
                {
                    string? text = item?.ToString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        list.Add(text);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($".meta 替换清单解析失败（{metaPath}），新增文件可能无法清理：{ex.Message}");
        }
        return list;
    }

    /// <summary>还原配置替换：从 swap-backup 恢复全部被替换文件（按 .meta 记录的 configPath），删除替换期间新增的文件，随后清理备份目录。</summary>
    public static void RestoreConfigReplacements(string scriptId, string? userName)
    {
        string backupDir = ConfigSwapPaths.ReplaceBackupDir(scriptId, userName);
        if (!Directory.Exists(backupDir))
        {
            return;
        }
        string metaPath = Path.Combine(backupDir, ".meta");
        string? configPath = null;
        var newFiles = new List<string>();
        if (File.Exists(metaPath))
        {
            try
            {
                JsonNode? node = JsonNode.Parse(File.ReadAllText(metaPath));
                configPath = node?["configPath"]?.ToString();
                if (node?["newFiles"] is JsonArray arr)
                {
                    foreach (JsonNode? item in arr)
                    {
                        string? text = item?.ToString();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            newFiles.Add(text);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[错误] 配置替换备份清单损坏（{metaPath}），已跳过还原并保留备份现场：{ex.Message}");
                return;
            }
        }
        if (string.IsNullOrWhiteSpace(configPath))
        {
            Logger.Error($"[错误] 配置替换备份清单缺少 configPath（{metaPath}），已跳过还原并保留备份现场。");
            return;
        }
        foreach (string file in Directory.GetFiles(backupDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(backupDir, file);
            if (rel.Equals(".meta", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            string? target = ResolveConfigTarget(configPath, rel);
            if (target is null)
            {
                continue;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[警告] 还原配置替换失败（{target}）：{ex.Message}");
            }
        }
        foreach (string rel in newFiles)
        {
            string? target = ResolveConfigTarget(configPath, rel);
            if (target is null || !File.Exists(target))
            {
                continue;
            }
            try
            {
                File.Delete(target);
                Logger.Info($"[配置替换] 已清理替换新增文件：{target}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[警告] 清理替换新增文件失败（{target}）：{ex.Message}");
            }
        }
        ConfigSwapPrimitives.TryDeleteDir(backupDir);
    }

    /* ---------------- 会话与恢复 ---------------- */

    /// <summary>操作前自愈：若存在未完成的交换标记且缓存区有内容，先完成还原（安全优先：原配置必还原）。失败交由后台重试。</summary>
    public static void RecoverIfNeeded(string scriptId, string userName, string configPath)
    {
        ConfigSessionMark? mark = ConfigSessionMark.TryRead(scriptId, userName);
        if (mark is null)
        {
            return;
        }
        string cache = ConfigSwapPaths.CacheDir(scriptId, userName);
        if (!Directory.Exists(cache) || !Directory.EnumerateFileSystemEntries(cache).Any())
        {
            // v0.6.9+（P2）：语义对齐 TryRecoverItem——GeneratedTemplate（编辑会话模板产物）仍需 DoRestore
            // 清理（恢复编辑前状态）；非模板会话 cache 空 = 现场已还原，仅清标记（避免窄窗口误删用户新写入的 config）。
            if (mark.GeneratedTemplate)
            {
                DoRestore(scriptId, userName, mark);
            }
            else
            {
                ConfigSessionMark.Clear(scriptId, userName);
            }
            return;
        }
        Logger.Info($"[恢复] 检测到脚本「{scriptId}」用户「{userName}」存在未完成的配置交换，正在还原。");
        try
        {
            DoRestore(scriptId, userName, mark);
            Audit.Log(Audit.System, "恢复配置交换", $"{mark.ConfigPath}（用户 {userName}）");
        }
        catch (Exception ex)
        {
            Audit.Log(Audit.System, "恢复配置交换失败", $"{mark.ConfigPath}（用户 {userName}）：{ex.Message}");
            EnqueuePendingRecover(scriptId, userName);
        }
    }

    /// <summary>启动恢复：扫描全部残留标记并还原（幂等；original 为空则仅清标记，不动现场）；同时恢复未还原的配置替换。
    /// 还原失败（如脚本孤儿进程仍占用配置目录）记入待办，由 <see cref="StartRecoveryRetry"/> 后台循环延迟重试。</summary>
    public static void RecoverInterrupted()
    {
        try
        {
            ConfigSwapPaths.MigrateLegacyLayout();
            if (!Directory.Exists(AppPaths.DataDir))
            {
                return;
            }
            foreach (string scriptDir in Directory.GetDirectories(AppPaths.DataDir))
            {
                string scriptId = Path.GetFileName(scriptDir);
                TryRecoverItem(scriptId, null);
                foreach (string userDir in Directory.GetDirectories(scriptDir))
                {
                    string userName = Path.GetFileName(userDir);
                    TryRecoverItem(scriptId, userName);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 扫描未完成配置交换失败：{ex.Message}");
        }
    }

    /* ---------------- 延迟恢复重试（崩溃后脚本孤儿进程退出后自动还原） ---------------- */

    private static readonly List<(string ScriptId, string? UserName)> PendingRecovers = new();

    private static readonly object PendingSync = new();

    private static CancellationTokenSource? _retryCts;

    /// <summary>尝试恢复一个脚本/用户的全部残留（配置替换 + 配置交换）；返回是否已完全恢复，失败记入待办。</summary>
    private static bool TryRecoverItem(string scriptId, string? userName)
    {
        // v0.6.6+：脚本进程仍在运行（如「强制关闭服务 + 先启动脚本再启动服务」场景）时跳过全部恢复动作，
        // 避免误删/误覆盖正在使用的配置；记入待办，进程退出后由后台重试循环自动完成恢复。
        if (ScriptProcessRunning(scriptId))
        {
            Logger.Info($"[恢复] 脚本 {scriptId} 进程仍在运行，等待其退出后恢复配置。");
            EnqueuePendingRecover(scriptId, userName);
            return false;
        }
        bool ok = true;
        if (HasBackupResidue(scriptId, userName) && !RecoverBackupQuiet(scriptId, userName))
        {
            ok = false;
        }
        if (ok && !string.IsNullOrWhiteSpace(userName))
        {
            ConfigSessionMark? mark = ConfigSessionMark.TryRead(scriptId, userName);
            if (mark is not null)
            {
                RestoreHiddenQuiet(scriptId, userName, mark.ConfigPath);
                string cache = ConfigSwapPaths.CacheDir(scriptId, userName);
                if (!Directory.Exists(cache) || !Directory.EnumerateFileSystemEntries(cache).Any())
                {
                    // v0.6.9+（P2）：与 RecoverIfNeeded 语义对齐——GeneratedTemplate（编辑会话模板产物）仍需
                    // DoRestore 清理（恢复编辑前状态，如重启后编辑会话恢复用例）；非模板会话 cache 空 =
                    // 现场已还原，仅清标记（此前一律 DoRestore，对 Missing 再执行会按「会话产物」删除
                    // config 位置当前文件，含崩溃后用户新写入的配置——窄窗口误删）。
                    if (mark.GeneratedTemplate)
                    {
                        DoRestore(scriptId, userName, mark);
                    }
                    else
                    {
                        ConfigSessionMark.Clear(scriptId, userName);
                    }
                }
                else if (!RecoverSwapQuiet(scriptId, userName, mark))
                {
                    ok = false;
                }
            }
        }
        if (!ok)
        {
            EnqueuePendingRecover(scriptId, userName);
        }
        return ok;
    }

    /// <summary>解析脚本运行时启动目标（含 Args 显式路径语义）并检测进程是否在运行；脚本已删除时返回 false（保持旧恢复行为）。</summary>
    private static bool ScriptProcessRunning(string scriptId)
    {
        ScriptInstance? script = RuntimeContext.Instance.FindScript(scriptId);
        if (script is null || string.IsNullOrWhiteSpace(script.MainExe))
        {
            return false;
        }
        string workingDir = string.IsNullOrWhiteSpace(script.RootPath)
            ? Path.GetDirectoryName(script.MainExe) ?? ""
            : script.RootPath;
        string launchExe = SystemActions.ResolveLaunchTarget(script.MainExe, workingDir, script.Args).ExePath;
        return SystemActions.IsExeRunning(launchExe);
    }

    private static bool HasBackupResidue(string scriptId, string? userName)
    {
        string dir = ConfigSwapPaths.ReplaceBackupDir(scriptId, userName);
        return Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any();
    }

    private static bool RecoverBackupQuiet(string scriptId, string? userName)
    {
        Logger.Info($"[恢复] 检测到未还原的配置替换，还原脚本 {scriptId} 用户 {userName ?? "(无用户)"} 的配置。");
        try
        {
            RestoreConfigReplacements(scriptId, userName);
            Audit.Log(Audit.System, "启动恢复配置替换", $"脚本 {scriptId} / 用户 {userName ?? "(无用户)"}");
            return true;
        }
        catch (Exception ex)
        {
            Audit.Log(Audit.System, "启动恢复配置替换失败", $"脚本 {scriptId}：{ex.Message}");
            return false;
        }
    }

    /// <summary>恢复编辑会话隐藏的配置（幂等）：编辑会话崩溃/重启后，把暂存在 edit-hidden 的配置移回 config 目录并清理目录。</summary>
    private static void RestoreHiddenQuiet(string scriptId, string userName, string configPath)
    {
        string hideDir = ConfigSwapPaths.HiddenConfigDir(scriptId, userName);
        if (!Directory.Exists(hideDir) || !Directory.EnumerateFileSystemEntries(hideDir).Any())
        {
            return;
        }
        string? dir = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[恢复] 重建配置目录失败（{dir}）：{ex.Message}");
                return;
            }
        }
        foreach (string file in Directory.GetFiles(hideDir))
        {
            try
            {
                File.Move(file, Path.Combine(dir, Path.GetFileName(file)), overwrite: true);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[恢复] 恢复隐藏配置失败（保持原样）：{file}（{ex.Message}）");
            }
        }
        try
        {
            if (Directory.Exists(hideDir) && !Directory.EnumerateFileSystemEntries(hideDir).Any())
            {
                Directory.Delete(hideDir);
            }
        }
        catch (Exception)
        {
        }
    }

    private static bool RecoverSwapQuiet(string scriptId, string? userName, ConfigSessionMark mark)
    {        Logger.Info($"[恢复] 上次会话中断，还原脚本 {scriptId} 用户 {userName} 的配置。");
        try
        {
            DoRestore(scriptId, userName!, mark);
            Audit.Log(Audit.System, "启动恢复配置交换", $"脚本 {scriptId} / 用户 {userName}（{mark.ConfigPath}）");
            return true;
        }
        catch (Exception ex)
        {
            Audit.Log(Audit.System, "启动恢复配置交换失败", $"脚本 {scriptId} / 用户 {userName}：{ex.Message}");
            return false;
        }
    }

    private static void EnqueuePendingRecover(string scriptId, string? userName)
    {
        lock (PendingSync)
        {
            if (!PendingRecovers.Any(item => item.ScriptId == scriptId && item.UserName == userName))
            {
                PendingRecovers.Add((scriptId, userName));
            }
        }
    }

    /// <summary>启动后台恢复重试循环：每 10 秒尝试还原待办项（孤儿进程退出/文件解锁后自动完成），直至全部成功或进程退出。</summary>
    public static void StartRecoveryRetry()
    {
        if (_retryCts is not null)
        {
            return;
        }
        var cts = new CancellationTokenSource();
        _retryCts = cts;
        _ = Task.Run(() => RecoveryRetryLoopAsync(cts.Token));
        Logger.Info("配置恢复重试循环已启动。");
    }

    public static void StopRecoveryRetry()
    {
        try
        {
            _retryCts?.Cancel();
        }
        catch
        {
        }
        _retryCts = null;
    }

    private static async Task RecoveryRetryLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                List<(string ScriptId, string? UserName)> pending;
                lock (PendingSync)
                {
                    pending = new List<(string, string?)>(PendingRecovers);
                }
                foreach ((string scriptId, string? userName) in pending)
                {
                    try
                    {
                        if (TryRecoverItem(scriptId, userName))
                        {
                            lock (PendingSync)
                            {
                                PendingRecovers.RemoveAll(item => item.ScriptId == scriptId && item.UserName == userName);
                            }
                            Logger.Info($"[恢复] 延迟重试成功：脚本 {scriptId} / 用户 {userName ?? "(无用户)"} 的配置已还原。");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"[恢复] 延迟重试异常（脚本 {scriptId}）：{ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[警告] 配置恢复重试循环异常：{ex.Message}");
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>执行还原：清 config（当前形态），original → config 还原原配置，随后清除标记。
    /// original 为空（首次会话）时：清理会话期间在 config 位置产生的文件/目录，还原为编辑前状态——
    /// ① 编辑会话生成的配置模板（GeneratedTemplate）；② 运行会话原配置形态为 Missing（运行前 config 位置不存在，
    /// 运行生效的 store 快照为会话产物，必须删除，否则残留污染 config 位置与后续快照）。</summary>
    public static void DoRestore(string scriptId, string userName, ConfigSessionMark mark)
    {
        string cache = ConfigSwapPaths.CacheDir(scriptId, userName);
        if (!Directory.Exists(cache) || !Directory.EnumerateFileSystemEntries(cache).Any())
        {
            bool restoreMissing = mark.GeneratedTemplate
                || PathKindUtil.Parse(mark.OriginalKind) == PathKind.Missing;
            if (restoreMissing)
            {
                PathKind current = PathKindUtil.KindOf(mark.ConfigPath);
                if (current != PathKind.Missing)
                {
                    // 模板目录形态（v0.6.3+）：先按清单删除复制生成的模板文件，再对 configPath 位置兜底清理（防残留）
                    if (mark.TemplateFiles.Count > 0)
                    {
                        string? baseDir = Path.GetDirectoryName(mark.ConfigPath);
                        if (!string.IsNullOrWhiteSpace(baseDir))
                        {
                            foreach (string rel in mark.TemplateFiles)
                            {
                                try
                                {
                                    string dest = Path.Combine(baseDir, rel);
                                    if (File.Exists(dest))
                                    {
                                        File.Delete(dest);
                                    }
                                }
                                catch
                                {
                                    // 删除失败保留标记，交由调用方（自愈/后台延迟重试）再次尝试
                                }
                            }
                        }
                    }
                    // 删除失败自然抛出（ClearPath 带重试），标记保留，交由调用方（自愈/后台延迟重试）再次尝试
                    ConfigSwapPrimitives.ClearPath(mark.ConfigPath, current);
                    Logger.Info($"[恢复] 已清理会话期间生成的配置（还原为不存在）：{mark.ConfigPath}");
                }
            }
            ConfigSessionMark.Clear(scriptId, userName);
            return;
        }
        PathKind currentState = PathKindUtil.KindOf(mark.ConfigPath);
        ConfigSwapPrimitives.ClearPath(mark.ConfigPath, currentState);
        ConfigSwapPrimitives.MoveAs(cache, mark.ConfigPath, ConfigSwapPrimitives.RestoreKind(mark));
        ConfigSessionMark.Clear(scriptId, userName);
    }
}
