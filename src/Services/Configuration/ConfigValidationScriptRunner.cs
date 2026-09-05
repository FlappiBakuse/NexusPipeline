using System.Text;
using System.Text.Json;
using Jint;
using NexusPipeline.App.Abstractions;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Plugins;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services.Configuration;

/// <summary>配置校验脚本向前端请求的短消息。</summary>
internal sealed record ConfigValidationToast(string Message, string Kind);

/// <summary>配置校验脚本向前端请求的页面角落通知。</summary>
internal sealed record ConfigValidationNotification(string Title, string Body, string Kind);

/// <summary>一次配置校验的结构化结果；校验失败不会改变配置提交结果。</summary>
internal sealed record ConfigValidationResult(
    bool Ran,
    string Error,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<ConfigValidationToast> Toasts,
    IReadOnlyList<ConfigValidationNotification> Notifications)
{
    public static ConfigValidationResult Skipped => new(
        false,
        "",
        Array.Empty<string>(),
        Array.Empty<ConfigValidationToast>(),
        Array.Empty<ConfigValidationNotification>());
}

/// <summary>配置快照文件清单 DTO；只向 validator 暴露逻辑相对路径和大小。</summary>
internal sealed record ConfigValidationFile(string Path, long Size);

/// <summary>附加配置路径的只读快照视图：声明路径 + 该用户的 store-extra 快照目录。</summary>
internal sealed record ConfigValidationExtraSnapshot(string Path, string StoreDir);

/// <summary>
/// data-specialized 插件配置校验器。它与运行期 JudgeScriptRunner 分离，固定以用户 store 为唯一文件根，
/// 只提供受限的 UTF-8 文件 API 与当前请求内的 UI feedback 队列。
/// </summary>
internal static class ConfigValidationScriptRunner
{
    internal const int MaxExecutionSeconds = 5;
    internal const long MaxReadFileBytes = 2 * 1024 * 1024;
    internal const long MaxWriteFileBytes = 2 * 1024 * 1024;
    internal const int MaxListedFiles = 4096;
    internal const int MaxToastCount = 32;
    internal const int MaxToastMessageLength = 512;
    internal const int MaxNotificationCount = 32;
    internal const int MaxNotificationTitleLength = 128;
    internal const int MaxNotificationBodyLength = 2048;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>执行一次 JS validator；任何异常都转为结果错误并保留已经完成的文件写入。
    /// trigger 标识触发语境（config-edit/script-save）；extraSnapshots 提供附加配置路径的只读快照（@extra&lt;i&gt;/ 前缀访问）。</summary>
    internal static async Task<ConfigValidationResult> ExecuteAsync(
        ConfigValidatorDescriptor descriptor,
        ScriptInstance script,
        ResolvedScriptUser? user,
        string storeRoot,
        string trigger = "config-edit",
        IReadOnlyList<ConfigValidationExtraSnapshot>? extraSnapshots = null,
        CancellationToken token = default)
    {
        var changedFiles = new List<string>();
        var toasts = new List<ConfigValidationToast>();
        var notifications = new List<ConfigValidationNotification>();
        extraSnapshots ??= Array.Empty<ConfigValidationExtraSnapshot>();
        try
        {
            string inputJson = BuildInput(script, user, ListFiles(storeRoot), trigger, extraSnapshots);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(MaxExecutionSeconds));
            await Task.Run(() =>
            {
                var engine = new Engine(options =>
                {
                    options.TimeoutInterval(TimeSpan.FromSeconds(MaxExecutionSeconds));
                    options.MaxStatements(1_000_000);
                    options.CancellationToken(timeout.Token);
                });
                engine.SetValue("__NEXUS_INPUT__", inputJson);
                engine.SetValue("__nexusListFiles", new Func<object>(() =>
                    ListFiles(storeRoot)
                        .Select(file => file.Path)
                        .Concat(extraSnapshots.SelectMany((extra, index) =>
                            ListFiles(extra.StoreDir).Select(file => ExtraPrefix(index) + file.Path)))
                        .ToArray()));
                engine.SetValue("__nexusReadFile", new Func<object, object?>(path =>
                    ResolvePathRoot(storeRoot, path?.ToString() ?? "", extraSnapshots, out string? root, out string? relative)
                        ? ReadFile(root!, relative!)
                        : null));
                engine.SetValue("__nexusWriteFile", new Func<object, object, object>((path, content) =>
                {
                    string candidate = path?.ToString() ?? "";
                    if (IsExtraRef(candidate, out _, out _))
                    {
                        Logger.Warn($"[警告] 专项配置校验写入被拒绝（附加配置快照本版本只读）：{candidate}");
                        return false;
                    }
                    return WriteFile(storeRoot, candidate, content?.ToString() ?? "", changedFiles);
                }));
                engine.SetValue("__nexusExists", new Func<object, object>(path =>
                    ResolvePathRoot(storeRoot, path?.ToString() ?? "", extraSnapshots, out string? root, out string? relative)
                        ? Exists(root!, relative!)
                        : false));
                engine.SetValue("__nexusToast", new Func<object, object, object>((message, kind) =>
                    QueueToast(message?.ToString() ?? "", kind?.ToString() ?? "", toasts)));
                engine.SetValue("__nexusNotify", new Func<object, object, object, object>((title, body, kind) =>
                    QueueNotification(title?.ToString() ?? "", body?.ToString() ?? "", kind?.ToString() ?? "", notifications)));
                engine.Execute(EngineGlue);
                engine.Execute(descriptor.Script);
            }).ConfigureAwait(false);
            return new ConfigValidationResult(true, "", changedFiles, toasts, notifications);
        }
        catch (OperationCanceledException)
        {
            string error = $"JavaScript 执行超时或被取消（{MaxExecutionSeconds} 秒）";
            LogFailure(descriptor, error);
            return new ConfigValidationResult(true, error, changedFiles, toasts, notifications);
        }
        catch (Exception ex)
        {
            string error = IsExecutionLimit(ex)
                ? $"JavaScript 执行超时或达到执行限制（{MaxExecutionSeconds} 秒）"
                : "JavaScript 执行失败：" + ex.Message;
            LogFailure(descriptor, error);
            return new ConfigValidationResult(true, error, changedFiles, toasts, notifications);
        }
    }

    /// <summary>构建稳定输入 DTO；不把宿主对象或应用数据目录路径交给 Jint。
    /// trigger 标识触发语境；extras 只暴露附加配置的声明路径与快照文件清单（路径+大小）。</summary>
    internal static string BuildInput(
        ScriptInstance script,
        ResolvedScriptUser? user,
        IReadOnlyList<ConfigValidationFile> files,
        string trigger = "config-edit",
        IReadOnlyList<ConfigValidationExtraSnapshot>? extraSnapshots = null)
    {
        return JsonSerializer.Serialize(new
        {
            trigger,
            script = new
            {
                script.Id,
                script.Name,
                script.PluginType,
                script.RootPath,
                script.MainExe,
                script.Args,
                script.ConfigPath,
                script.LogPath,
                script.LaunchGame,
                script.GameMode,
                script.GameExe,
                script.GameArgs,
                script.GameWaitSeconds,
                script.ForceCloseGame,
                script.MaxAttempts,
                script.LogStallTimeoutMinutes,
                script.TotalTimeoutMinutes,
                script.AutoUpdateConfig,
            },
            user = user is null
                ? null
                : new
                {
                    user.UserId,
                    user.UserName,
                },
            snapshot = new
            {
                files = files.Select(file => new { file.Path, file.Size }).ToArray(),
            },
            extras = (extraSnapshots ?? Array.Empty<ConfigValidationExtraSnapshot>())
                .Select(extra => new
                {
                    extra.Path,
                    files = ListFiles(extra.StoreDir)
                        .Select(file => new { file.Path, file.Size })
                        .ToArray(),
                })
                .ToArray(),
        }, JsonOpts.Web);
    }

    /// <summary>@extra&lt;i&gt;/ 逻辑前缀：附加配置快照区在 validator 文件 API 中的命名空间。</summary>
    private static string ExtraPrefix(int index) => $"@extra{index}/";

    private static bool IsExtraRef(string candidate, out int index, out string relative)
    {
        index = -1;
        relative = "";
        string normalized = candidate.Replace('\\', '/');
        const string prefix = "@extra";
        if (!normalized.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }
        int separator = normalized.IndexOf('/', prefix.Length);
        if (separator <= prefix.Length
            || !int.TryParse(normalized[prefix.Length..separator], out index)
            || index < 0)
        {
            return false;
        }
        relative = normalized[(separator + 1)..];
        return relative.Length > 0;
    }

    /// <summary>把 validator 的逻辑路径解析为文件根与相对路径：@extra&lt;i&gt;/ 前缀指向附加配置快照（只读），
    /// 其余指向主配置 store；越界索引或非法前缀返回 false。</summary>
    private static bool ResolvePathRoot(
        string storeRoot,
        string candidate,
        IReadOnlyList<ConfigValidationExtraSnapshot> extraSnapshots,
        out string? root,
        out string? relative)
    {
        root = null;
        relative = null;
        if (IsExtraRef(candidate, out int index, out string extraRelative))
        {
            if (index >= extraSnapshots.Count)
            {
                Logger.Warn($"[警告] 专项配置校验路径索引超出附加配置清单：{candidate}");
                return false;
            }
            root = extraSnapshots[index].StoreDir;
            relative = extraRelative;
            return true;
        }
        root = storeRoot;
        relative = candidate;
        return candidate.Length > 0;
    }

    /// <summary>列出 store 内当前存在的文件，返回规范化相对路径；枚举失败只记录日志并返回已收集部分。</summary>
    internal static IReadOnlyList<ConfigValidationFile> ListFiles(string storeRoot)
    {
        var result = new List<ConfigValidationFile>();
        if (string.IsNullOrWhiteSpace(storeRoot) || !Directory.Exists(storeRoot))
        {
            return result;
        }
        try
        {
            foreach (string file in Directory.EnumerateFiles(storeRoot, "*", SearchOption.AllDirectories)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (result.Count >= MaxListedFiles)
                {
                    Logger.Warn($"[专项配置校验] 文件清单超过 {MaxListedFiles} 项，已截断：{storeRoot}");
                    break;
                }
                string canonical = Path.GetFullPath(file);
                if (!IsLexicallyWithin(Path.GetFullPath(storeRoot), canonical)
                    || !IsExistingPathWithin(Path.GetFullPath(storeRoot), canonical)
                    || !File.Exists(canonical))
                {
                    continue;
                }
                try
                {
                    var info = new FileInfo(canonical);
                    result.Add(new ConfigValidationFile(
                        NormalizeRelativePath(Path.GetRelativePath(storeRoot, canonical)),
                        info.Length));
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[专项配置校验] 文件清单读取失败（{file}）：{ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[专项配置校验] 文件清单收集失败（{storeRoot}）：{ex.Message}");
        }
        return result;
    }

    private static string? ReadFile(string storeRoot, string relativePath)
    {
        if (!TryResolveFilePath(storeRoot, relativePath, "读取", out string? target)
            || target is null)
        {
            return null;
        }
        try
        {
            var info = new FileInfo(target);
            if (!info.Exists || info.Length > MaxReadFileBytes)
            {
                Logger.Warn($"[警告] 专项配置校验读取被拒绝（不存在或超过 2MB）：{relativePath}");
                return null;
            }
            return File.ReadAllText(target, StrictUtf8);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 专项配置校验读取失败（{relativePath}）：{ex.Message}");
            return null;
        }
    }

    private static bool WriteFile(
        string storeRoot,
        string relativePath,
        string content,
        ICollection<string> changedFiles)
    {
        if (!TryResolveFilePath(storeRoot, relativePath, "写入", out string? target)
            || target is null)
        {
            return false;
        }
        byte[] bytes;
        try
        {
            bytes = StrictUtf8.GetBytes(content);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 专项配置校验 UTF-8 编码失败（{relativePath}）：{ex.Message}");
            return false;
        }
        if (bytes.LongLength > MaxWriteFileBytes)
        {
            Logger.Warn($"[警告] 专项配置校验写入被拒绝（超过 2MB）：{relativePath}");
            return false;
        }

        string? directory = Path.GetDirectoryName(target);
        if (string.IsNullOrWhiteSpace(directory))
        {
            Logger.Warn($"[警告] 专项配置校验写入缺少父目录：{relativePath}");
            return false;
        }
        string temp = target + ".nexus-validator-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            using (var stream = new FileStream(
                temp,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                options: FileOptions.WriteThrough | FileOptions.SequentialScan))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(target))
            {
                try
                {
                    File.Replace(temp, target, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Move(temp, target, overwrite: true);
                }
                catch (IOException)
                {
                    // 某些文件系统不支持 Replace；同卷 Move 仍保持单文件替换语义。
                    File.Move(temp, target, overwrite: true);
                }
            }
            else
            {
                File.Move(temp, target);
            }
            changedFiles.Add(NormalizeRelativePath(Path.GetRelativePath(storeRoot, target)));
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 专项配置校验写入失败（{relativePath}）：{ex.Message}");
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[警告] 专项配置校验临时文件清理失败（{temp}）：{ex.Message}");
            }
        }
    }

    private static bool Exists(string storeRoot, string relativePath)
    {
        if (!TryResolveCanonicalPath(storeRoot, relativePath, out string? target) || target is null)
        {
            return false;
        }
        return File.Exists(target) || Directory.Exists(target);
    }

    private static bool QueueToast(
        string message,
        string kind,
        ICollection<ConfigValidationToast> toasts)
    {
        if (!TryBoundedText(message, MaxToastMessageLength, "toast", out string bounded))
        {
            return false;
        }
        if (toasts.Count >= MaxToastCount)
        {
            Logger.Warn($"[专项配置校验] toast 数量超过 {MaxToastCount}，已忽略后续请求");
            return false;
        }
        toasts.Add(new ConfigValidationToast(bounded, NormalizeKind(kind)));
        return true;
    }

    private static bool QueueNotification(
        string title,
        string body,
        string kind,
        ICollection<ConfigValidationNotification> notifications)
    {
        if (!TryBoundedText(title, MaxNotificationTitleLength, "通知标题", out string boundedTitle)
            || !TryBoundedText(body, MaxNotificationBodyLength, "通知正文", out string boundedBody))
        {
            return false;
        }
        if (notifications.Count >= MaxNotificationCount)
        {
            Logger.Warn($"[专项配置校验] 通知数量超过 {MaxNotificationCount}，已忽略后续请求");
            return false;
        }
        notifications.Add(new ConfigValidationNotification(boundedTitle, boundedBody, NormalizeKind(kind)));
        return true;
    }

    private static bool TryBoundedText(string value, int maxLength, string label, out string bounded)
    {
        bounded = value ?? "";
        if (bounded.Length <= maxLength)
        {
            return true;
        }
        Logger.Warn($"[专项配置校验] {label}超过 {maxLength} 字符，已拒绝");
        return false;
    }

    private static string NormalizeKind(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "success" => "success",
            "warning" => "warning",
            "error" => "error",
            _ => "info",
        };
    }

    private static bool TryResolveFilePath(
        string storeRoot,
        string relativePath,
        string operation,
        out string? target)
    {
        if (!TryResolveCanonicalPath(storeRoot, relativePath, out target)
            || target is null
            || string.Equals(Path.GetFullPath(storeRoot), target, StringComparison.OrdinalIgnoreCase)
            || Directory.Exists(target))
        {
            Logger.Warn($"[警告] 专项配置校验{operation}被拒绝（非法路径或目标不是文件）：{relativePath}");
            target = null;
            return false;
        }
        return true;
    }

    private static bool TryResolveCanonicalPath(string storeRoot, string candidate, out string? target)
    {
        target = null;
        if (string.IsNullOrWhiteSpace(storeRoot)
            || string.IsNullOrWhiteSpace(candidate)
            || candidate.Contains('\0')
            || Path.IsPathRooted(candidate)
            || candidate.Contains(':', StringComparison.Ordinal))
        {
            Logger.Warn($"[警告] 专项配置校验拒绝非法路径：{candidate}");
            return false;
        }
        try
        {
            string root = Path.GetFullPath(storeRoot);
            string full = Path.GetFullPath(Path.Combine(root, candidate));
            if (!IsLexicallyWithin(root, full) || !IsExistingPathWithin(root, full))
            {
                Logger.Warn($"[警告] 专项配置校验路径超出快照目录：{candidate}");
                return false;
            }
            target = full;
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 专项配置校验路径解析失败（{candidate}）：{ex.Message}");
            return false;
        }
    }

    /// <summary>词法路径边界检查，避免 store 与 store-evil 的前缀误匹配。</summary>
    private static bool IsLexicallyWithin(string root, string full)
    {
        if (string.Equals(root, full, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        string relative = Path.GetRelativePath(root, full);
        return !Path.IsPathRooted(relative)
            && !relative.Equals("..", StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith("../", StringComparison.Ordinal);
    }

    /// <summary>对已经存在的路径逐段检查 reparse point，避免以符号链接绕出 store。</summary>
    private static bool IsExistingPathWithin(string root, string full)
    {
        string relative = Path.GetRelativePath(root, full);
        string current = root;
        foreach (string part in relative.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                break;
            }
            FileSystemInfo info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : new FileInfo(current);
            if ((info.Attributes & FileAttributes.ReparsePoint) == 0)
            {
                continue;
            }
            FileSystemInfo? resolved = info.ResolveLinkTarget(returnFinalTarget: true);
            if (resolved is null || !IsLexicallyWithin(root, Path.GetFullPath(resolved.FullName)))
            {
                return false;
            }
            current = Path.GetFullPath(resolved.FullName);
        }
        return true;
    }

    private static string NormalizeRelativePath(string value)
    {
        return value.Replace('\\', '/');
    }

    private static void LogFailure(ConfigValidatorDescriptor descriptor, string error)
    {
        Logger.Warn($"[专项配置校验:{descriptor.PluginName}] {error}");
    }

    private static bool IsExecutionLimit(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            string message = current.Message;
            if (message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
                || message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
                || message.Contains("statement", StringComparison.OrdinalIgnoreCase)
                || message.Contains("取消", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private const string EngineGlue = """
        const input = JSON.parse(__NEXUS_INPUT__);
        const nexus = {
          input,
          listFiles: () => __nexusListFiles(),
          readFile: (p) => __nexusReadFile(p),
          writeFile: (p, c) => __nexusWriteFile(p, c),
          exists: (p) => __nexusExists(p),
          toast: (m, k) => __nexusToast(m, k),
          notify: (t, b, k) => __nexusNotify(t, b, k),
        };
        """;
}
