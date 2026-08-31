using NexusPipeline.App.Abstractions;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services;

/// <summary>
/// 配置交换恢复：脚本/用户读取经组合根注入的委托完成，
/// 不再反向依赖组合根。仅改调用方式，磁盘协议与判定语义不变；
/// 实例由组合根装配（RuntimeInitializer），恢复重试循环为进程内单实例。
/// </summary>
internal sealed class ConfigSwapRecovery
{
    private readonly Func<string, ScriptInstance?> _findScript;
    private readonly Func<IReadOnlyList<NexusUser>> _snapshotUsers;

    public ConfigSwapRecovery(Func<string, ScriptInstance?> findScript, Func<IReadOnlyList<NexusUser>> snapshotUsers)
    {
        _findScript = findScript;
        _snapshotUsers = snapshotUsers;
    }

    /* ---------------- 会话与恢复 ---------------- */

    /// <summary>操作前自愈：若存在未完成的交换标记且缓存区有内容，先完成还原（安全优先：原配置必还原）。失败交由后台重试。</summary>
    public void RecoverIfNeeded(string scriptId, string userName, string configPath)
    {
        ConfigSessionMark? mark = ConfigSessionMark.TryRead(scriptId, userName);
        if (mark is null)
        {
            return;
        }
        string cache = ConfigSwapPaths.CacheDir(scriptId, userName);
        if (!Directory.Exists(cache) || !Directory.EnumerateFileSystemEntries(cache).Any())
        {
            // （P2）：语义对齐 TryRecoverItem——GeneratedTemplate（编辑会话模板产物）仍需 DoRestore
            // 清理（恢复编辑前状态）；非模板会话 cache 空 = 现场已还原，仅清标记（避免窄窗口误删用户新写入的 config）。
            if (mark.GeneratedTemplate)
            {
                DoRestore(scriptId, userName, mark);
            }
            else
            {
                ConfigSessionMark.Clear(scriptId, userName);
            }
            ConfigSwapPrimitives.TryDeleteDir(ConfigSwapPaths.RetryStoreDir(scriptId, userName));
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

    /// <summary>启动恢复：只处理当前全局用户绑定对应的 UserId 目录与脚本级目录。</summary>
    public void RecoverInterrupted(IReadOnlyList<NexusUser>? users = null)
    {
        try
        {
            Dictionary<string, HashSet<string>> userKeysByScript = BuildRecoveryUserKeys(users);
            RecoverStoreTransactions(userKeysByScript);
            if (!Directory.Exists(AppPaths.DataDir))
            {
                return;
            }
            foreach (string scriptDir in Directory.GetDirectories(AppPaths.DataDir))
            {
                string scriptId = Path.GetFileName(scriptDir);
                TryRecoverItem(scriptId, null);
                if (!userKeysByScript.TryGetValue(scriptId, out HashSet<string>? userKeys))
                {
                    continue;
                }
                foreach (string userKey in userKeys)
                {
                    TryRecoverItem(scriptId, userKey);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 扫描未完成配置交换失败：{ex.Message}");
        }
    }

    /// <summary>恢复自动更新配置的目录事务：store 缺失时提升 store.previous，临时目录只作为未完成事务清理。</summary>
    private Dictionary<string, HashSet<string>> BuildRecoveryUserKeys(IReadOnlyList<NexusUser>? users)
    {
        IEnumerable<NexusUser> source = users ?? _snapshotUsers();
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (NexusUser user in source)
        {
            if (string.IsNullOrWhiteSpace(user.Id))
            {
                continue;
            }
            foreach (UserScriptBinding binding in user.Bindings)
            {
                if (string.IsNullOrWhiteSpace(binding.ScriptInstanceId))
                {
                    continue;
                }
                if (!result.TryGetValue(binding.ScriptInstanceId, out HashSet<string>? keys))
                {
                    keys = new HashSet<string>(StringComparer.Ordinal);
                    result[binding.ScriptInstanceId] = keys;
                }
                keys.Add(user.Id);
            }
        }
        return result;
    }

    private void RecoverStoreTransactions(IReadOnlyDictionary<string, HashSet<string>> userKeysByScript)
    {
        if (!Directory.Exists(AppPaths.DataDir))
        {
            return;
        }
        foreach (string scriptDir in Directory.GetDirectories(AppPaths.DataDir))
        {
            string scriptId = Path.GetFileName(scriptDir);
            var allowedDirectories = new List<string> { scriptDir };
            if (userKeysByScript.TryGetValue(scriptId, out HashSet<string>? userKeys))
            {
                allowedDirectories.AddRange(userKeys.Select(userKey => Path.Combine(scriptDir, userKey)));
            }
            foreach (string allowedDirectory in allowedDirectories.Where(Directory.Exists))
            {
                foreach (string temp in Directory.GetDirectories(allowedDirectory, "store.tmp", SearchOption.TopDirectoryOnly))
                {
                    string store = temp[..^4];
                    string previous = store + ".previous";
                    try
                    {
                        if (!Directory.Exists(store) && Directory.Exists(previous))
                        {
                            Directory.Move(previous, store);
                            Logger.Warn($"[恢复] 自动更新配置事务中断，已恢复用户快照：{store}");
                        }
                        ConfigSwapPrimitives.TryDeleteDir(temp);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"[警告] 清理自动更新配置临时事务失败（{temp}）：{ex.Message}");
                    }
                }
            }
        }
    }

    /* ---------------- 延迟恢复重试（崩溃后脚本孤儿进程退出后自动还原） ---------------- */

    private readonly List<(string ScriptId, string? UserName)> _pendingRecovers = new();

    private readonly object _pendingSync = new();

    private CancellationTokenSource? _retryCts;

    /// <summary>尝试恢复一个脚本/用户的全部残留（配置替换 + 配置交换）；返回是否已完全恢复，失败记入待办。</summary>
    private bool TryRecoverItem(string scriptId, string? userName)
    {
        // 脚本进程仍在运行（如「强制关闭服务 + 先启动脚本再启动服务」场景）时跳过全部恢复动作，
        // 避免误删/误覆盖正在使用的配置；记入待办，进程退出后由后台重试循环自动完成恢复。
        bool hasRecoveryResidue = HasBackupResidue(scriptId, userName)
            || (!string.IsNullOrWhiteSpace(userName)
                && File.Exists(ConfigSessionMark.MarkFile(scriptId, userName)));
        if (hasRecoveryResidue && ScriptProcessRunning(scriptId))
        {
            Logger.Info($"[恢复] 脚本 {scriptId} 进程仍在运行，等待其退出后恢复配置。");
            EnqueuePendingRecover(scriptId, userName);
            return false;
        }
        bool ok = true;
        if (hasRecoveryResidue && HasBackupResidue(scriptId, userName) && !RecoverBackupQuiet(scriptId, userName))
        {
            ok = false;
        }
        if (ok && !string.IsNullOrWhiteSpace(userName))
        {
            ConfigSessionMark? mark = ConfigSessionMark.TryRead(scriptId, userName);
            if (mark is null && File.Exists(ConfigSessionMark.MarkFile(scriptId, userName)))
            {
                ok = RecoverCorruptMark(scriptId, userName);
            }
            mark = ok ? ConfigSessionMark.TryRead(scriptId, userName) : null;
            if (mark is not null)
            {
                RestoreHiddenQuiet(scriptId, userName, mark.ConfigPath);
                string cache = ConfigSwapPaths.CacheDir(scriptId, userName);
                if (!Directory.Exists(cache) || !Directory.EnumerateFileSystemEntries(cache).Any())
                {
                    // （P2）：与 RecoverIfNeeded 语义对齐——GeneratedTemplate（编辑会话模板产物）仍需
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

    /// <summary>会话标记损坏时，使用当前脚本固化的 ConfigPath 和 original 目录形态做保守恢复。</summary>
    private bool RecoverCorruptMark(string scriptId, string userName)
    {
        ScriptInstance? script = _findScript(scriptId);
        if (script is null || string.IsNullOrWhiteSpace(script.ConfigPath))
        {
            Logger.Error($"[错误] 配置会话标记损坏且无法找到脚本配置路径：脚本 {scriptId} / 用户 {userName}");
            EnqueuePendingRecover(scriptId, userName);
            return false;
        }
        string cache = ConfigSwapPaths.CacheDir(scriptId, userName);
        var mark = new ConfigSessionMark
        {
            ScriptId = scriptId,
            UserName = userName,
            ConfigPath = script.ConfigPath,
            OriginalKind = string.IsNullOrWhiteSpace(Path.GetExtension(script.ConfigPath)) ? "dir" : "file",
            Phase = "run",
        };
        try
        {
            if (Directory.Exists(cache) && Directory.EnumerateFileSystemEntries(cache).Any())
            {
                DoRestore(scriptId, userName, mark);
            }
            else
            {
                ConfigSessionMark.Clear(scriptId, userName);
            }
            Logger.Warn($"[恢复] 配置会话标记损坏，已按当前脚本配置路径完成保守恢复：脚本 {scriptId} / 用户 {userName}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"[错误] 配置会话标记损坏，保守恢复失败（脚本 {scriptId} / 用户 {userName}）：{ex.Message}");
            EnqueuePendingRecover(scriptId, userName);
            return false;
        }
    }

    /// <summary>解析脚本运行时启动目标（含 Args 显式路径语义）并检测进程是否在运行；脚本已删除时返回 false。</summary>
    private bool ScriptProcessRunning(string scriptId)
    {
        ScriptInstance? script = _findScript(scriptId);
        if (script is null || string.IsNullOrWhiteSpace(script.MainExe))
        {
            return false;
        }
        string workingDir = string.IsNullOrWhiteSpace(script.RootPath)
            ? Path.GetDirectoryName(script.MainExe) ?? ""
            : script.RootPath;
        string launchExe = SystemActions.ResolveLaunchTarget(script.MainExe, workingDir, script.Args).ExePath;
        // 启动扫描只在确有残留会话/备份时调用；当前没有进程即可继续恢复，
        // 避免每个普通脚本都串行等待完整稳定窗口阻塞 Web 服务接管。
        return !SystemActions.IsExeStoppedStable(launchExe, waitIfInitiallyStopped: false);
    }

    private bool HasBackupResidue(string scriptId, string? userName)
    {
        string dir = ConfigSwapPaths.ReplaceBackupDir(scriptId, userName);
        return Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any();
    }

    private bool RecoverBackupQuiet(string scriptId, string? userName)
    {
        Logger.Info($"[恢复] 检测到未还原的配置替换，还原脚本 {scriptId} 用户 {userName ?? "(无用户)"} 的配置。");
        try
        {
            bool restored = ConfigSwapSession.RestoreConfigReplacements(scriptId, userName);
            Audit.Log(Audit.System, "启动恢复配置替换", $"脚本 {scriptId} / 用户 {userName ?? "(无用户)"}");
            return restored;
        }
        catch (Exception ex)
        {
            Audit.Log(Audit.System, "启动恢复配置替换失败", $"脚本 {scriptId}：{ex.Message}");
            return false;
        }
    }

    /// <summary>恢复编辑会话隐藏的配置（幂等）：编辑会话崩溃/重启后，把暂存在 edit-hidden 的配置移回 config 目录并清理目录。</summary>
    private void RestoreHiddenQuiet(string scriptId, string userName, string configPath)
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

    private bool RecoverSwapQuiet(string scriptId, string? userName, ConfigSessionMark mark)
    {        Logger.Info($"[恢复] 上次会话中断，还原脚本 {scriptId} 用户 {userName} 的配置。");
        try
        {
            DoRestore(scriptId, userName!, mark);
            ConfigSwapPrimitives.TryDeleteDir(ConfigSwapPaths.RetryStoreDir(scriptId, userName!));
            Audit.Log(Audit.System, "启动恢复配置交换", $"脚本 {scriptId} / 用户 {userName}（{mark.ConfigPath}）");
            return true;
        }
        catch (Exception ex)
        {
            Audit.Log(Audit.System, "启动恢复配置交换失败", $"脚本 {scriptId} / 用户 {userName}：{ex.Message}");
            return false;
        }
    }

    private void EnqueuePendingRecover(string scriptId, string? userName)
    {
        lock (_pendingSync)
        {
            if (!_pendingRecovers.Any(item => item.ScriptId == scriptId && item.UserName == userName))
            {
                _pendingRecovers.Add((scriptId, userName));
            }
        }
    }

    /// <summary>启动后台恢复重试循环：每 10 秒尝试还原待办项（孤儿进程退出/文件解锁后自动完成），直至全部成功或进程退出。</summary>
    public void StartRecoveryRetry()
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

    public void StopRecoveryRetry()
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

    private async Task RecoveryRetryLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                List<(string ScriptId, string? UserName)> pending;
                lock (_pendingSync)
                {
                    pending = new List<(string, string?)>(_pendingRecovers);
                }
                foreach ((string scriptId, string? userName) in pending)
                {
                    try
                    {
                        if (TryRecoverItem(scriptId, userName))
                        {
                            lock (_pendingSync)
                            {
                                _pendingRecovers.RemoveAll(item => item.ScriptId == scriptId && item.UserName == userName);
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
    public void DoRestore(string scriptId, string userName, ConfigSessionMark mark)
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
                    // 模板目录形态：先按清单删除复制生成的模板文件，再对 configPath 位置兜底清理（防残留）
                    DeleteTemplateFiles(mark);
                    // 删除失败自然抛出（ClearPath 带重试），标记保留，交由调用方（自愈/后台延迟重试）再次尝试
                    ConfigSwapPrimitives.ClearPath(mark.ConfigPath, current);
                    Logger.Info($"[恢复] 已清理会话期间生成的配置（还原为不存在）：{mark.ConfigPath}");
                }
            }
            ConfigSessionMark.Clear(scriptId, userName);
            return;
        }
        // （台账外）：cache 非空路径同样先按清单删除模板兄弟文件——StartVisible 失败/CancelEdit 且原配置存在时，
        // 文件型 config 模板复制到父目录的非 ConfigPath 同名文件（如 maa_option.json）此前残留。
        DeleteTemplateFiles(mark);
        PathKind currentState = PathKindUtil.KindOf(mark.ConfigPath);
        ConfigSwapPrimitives.ClearPath(mark.ConfigPath, currentState);
        ConfigSwapPrimitives.MoveAs(cache, mark.ConfigPath, ConfigSwapPrimitives.RestoreKind(mark));
        ConfigSessionMark.Clear(scriptId, userName);
    }

    /// <summary>按 TemplateFiles 清单删除编辑会话生成的模板文件（相对 ConfigPath 父目录）；删除失败保留标记交自愈重试。</summary>
    private void DeleteTemplateFiles(ConfigSessionMark mark)
    {
        if (mark.TemplateFiles.Count == 0)
        {
            return;
        }
        string? baseDir = Path.GetDirectoryName(mark.ConfigPath);
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            return;
        }
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
