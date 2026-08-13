using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Jint;
using NexusPipeline.Models;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services;

/// <summary>判断脚本一次执行的结果。</summary>
internal class JudgeScriptResult
{
    /// <summary>success / failed；空字符串 = 未返回（继续运行）。</summary>
    public string Status { get; set; } = "";

    public string Reason { get; set; } = "";

    public string NotifyText { get; set; } = "";

    /// <summary>请求替换到 config 的配置文件（相对 script 目录的路径）；仅 status=failed 时由宿主应用。</summary>
    public List<string> ReplaceConfigs { get; set; } = new();

    /// <summary>非空 = 脚本执行错误（解释器缺失/语法错误/超时/异常），视为继续运行。</summary>
    public string? JudgeError { get; set; }
}

/// <summary>输入 JSON 中的单个文件清单项（只列路径元数据，不嵌内容）。</summary>
internal class JudgeScriptInputFile
{
    public string Root { get; set; } = "";

    public string Path { get; set; } = "";

    public string Abs { get; set; } = "";
}

/// <summary>
/// 自定义完成标志判断脚本执行器：
/// - 输入：脚本实例字段 + 当前用户 + config（运行时生效配置，只读）与 script（可读写）目录全递归文件清单 + 当前日志全文（超过 4MB 仅提供尾部并置 logTruncated=true）+ timeScale（测试加速因子，生产恒为 1），打包为 JSON；
/// - JavaScript 用内置 Jint 引擎（无 Node 库，注入 __NEXUS_INPUT__ / nexus.readFile / nexus.writeFile（限 script 目录）/ nexus.listFiles / console.log）；
/// - Python 用系统 python.exe（sys.argv[1] 为输入 JSON 路径；可读写约定由文档约束，进程权限无法技术限制）；
/// - 输出契约：stdout 最后一行 JSON {"status":"success|failed","reason":"...","notifyText":"可选","replaceConfigs":["相对script目录路径"]}，status/reason 必填；
/// - 单次执行 30 秒上限（NEXUS_TIME_SCALE 加速时按比例缩放）；任何执行错误均返回 JudgeError（继续运行，不误判失败）。
/// </summary>
internal static class JudgeScriptRunner
{
    private static int ScriptTimeoutSeconds => TestHooks.ScaledSeconds(30);

    private const long MaxReadFileBytes = 2 * 1024 * 1024;

    /// <summary>输入 JSON 中 log 字段的字符上限：超出仅提供尾部，并置 logTruncated=true（避免超大日志拖垮内置引擎解析导致 30 秒超时）。</summary>
    internal const int MaxJudgeLogChars = 4 * 1024 * 1024;

    private static readonly string[] JudgeExtensions = { ".js", ".py" };

    public static bool IsSupportedLanguage(string language)
    {
        return language is "javascript" or "python";
    }

    public static bool IsJudgeExtension(string path)
    {
        return JudgeExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());
    }

    public static string LanguageOfExtension(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".py" => "python",
            _ => "javascript",
        };
    }

    /// <summary>收集文件清单：configPath（运行时生效配置，文件或文件夹全递归）+ scriptDir（可读写目录，全递归）。</summary>
    public static List<JudgeScriptInputFile> CollectFiles(string configPath, string scriptDir)
    {
        var list = new List<JudgeScriptInputFile>();
        AddPath(list, "config", configPath);
        AddPath(list, "script", scriptDir);
        return list;
    }

    private static void AddPath(List<JudgeScriptInputFile> list, string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        try
        {
            if (File.Exists(path))
            {
                list.Add(new JudgeScriptInputFile { Root = root, Path = Path.GetFileName(path), Abs = path });
            }
            else if (Directory.Exists(path))
            {
                foreach (string file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        list.Add(new JudgeScriptInputFile { Root = root, Path = Path.GetRelativePath(path, file), Abs = file });
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 判断脚本输入：{root} 文件清单收集失败：{ex.Message}");
        }
    }

    /// <summary>构建输入 JSON：脚本字段 + 用户 + config/script 文件清单 + 日志全文（超限截断尾部）+ scriptDir。</summary>
    public static string BuildInput(ScriptInstance script, ScriptUser? user, List<JudgeScriptInputFile> files, string scriptDir, string logText, bool logTruncated)
    {
        return JsonSerializer.Serialize(new
        {
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
                script.GameExe,
                script.GameArgs,
                script.GameWaitSeconds,
                script.ForceCloseGame,
                script.MaxAttempts,
                script.LogStallTimeoutMinutes,
                script.TotalTimeoutMinutes,
                script.NotifyEnabled,
            },
            user = user is null ? null : new
            {
                user.Name,
                user.Enabled,
                user.PreRunScript,
                user.PreRunOnceOnly,
                user.PostRunScript,
                user.PostRunOnFinalOnly,
            },
            script.RootPath,
            script.ConfigPath,
            scriptDir,
            files = files.Select(file => new { file.Root, file.Path, file.Abs }).ToArray(),
            log = logText,
            logTruncated,
            timeScale = TestHooks.TimeScale,
        });
    }

    /// <summary>执行一次判断脚本；任何错误返回 JudgeError（继续运行）。</summary>
    public static async Task<JudgeScriptResult> ExecuteAsync(ScriptInstance script, string inputJson, List<JudgeScriptInputFile> allowedFiles, string configPath, string scriptDir, CancellationToken token)
    {
        return script.JudgeScriptLanguage == "python"
            ? await RunPythonAsync(script.JudgeScript, inputJson, token).ConfigureAwait(false)
            : await RunJsAsync(script.JudgeScript, inputJson, allowedFiles, configPath, scriptDir, token).ConfigureAwait(false);
    }

    private static async Task<JudgeScriptResult> RunJsAsync(string code, string inputJson, List<JudgeScriptInputFile> allowedFiles, string configPath, string scriptDir, CancellationToken token)
    {
        var result = new JudgeScriptResult();
        var outputs = new List<string>();
        try
        {
            await Task.Run(() =>
            {
                var engine = new Engine(options =>
                {
                    options.TimeoutInterval(TimeSpan.FromSeconds(ScriptTimeoutSeconds));
                    options.CancellationToken(token);
                });
                engine.SetValue("__NEXUS_INPUT__", inputJson);
                engine.SetValue("__nexusLog", new Action<object>(obj => outputs.Add(obj?.ToString() ?? "")));
                engine.SetValue("__nexusReadFile", new Func<object, object>(abs => ReadAllowedFile(configPath, scriptDir, abs?.ToString() ?? "") ?? (object)""));
                engine.SetValue("__nexusWriteFile", new Func<object, object, object>((rel, content) => WriteScriptFile(scriptDir, rel?.ToString() ?? "", content?.ToString() ?? "")));
                engine.SetValue("__nexusListFiles", new Func<object>(() => allowedFiles.Select(file => file.Abs).ToArray()));
                engine.Execute(EngineGlue);
                engine.Execute(code);
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result.JudgeError = $"判断脚本执行超时（{ScriptTimeoutSeconds} 秒）或被取消";
            return result;
        }
        catch (Exception ex)
        {
            result.JudgeError = "判断脚本执行失败：" + ex.Message;
            return result;
        }
        ParseOutput(outputs, result);
        return result;
    }

    /// <summary>预绑定注入 API：console.log 收集输出；nexus.readFile 只读 config/script 目录；nexus.writeFile 写 script 目录；nexus.listFiles 返回绝对路径数组。</summary>
    private const string EngineGlue = """
        const console = { log: (...args) => args.forEach(a => __nexusLog(typeof a === "string" ? a : JSON.stringify(a))) };
        const nexus = {
          readFile: (p) => __nexusReadFile(p) || null,
          writeFile: (p, c) => __nexusWriteFile(p, c),
          listFiles: () => __nexusListFiles(),
        };
        """;

    /// <summary>仅允许读取 configPath（运行时生效配置）与 scriptDir（可读写目录）范围内的文件，单文件 2MB 上限；失败返回 null。</summary>
    private static string? ReadAllowedFile(string configPath, string scriptDir, string abs)
    {
        if (string.IsNullOrWhiteSpace(abs) || !IsWithinAny(abs, configPath, scriptDir))
        {
            Logger.Warn($"[警告] 判断脚本尝试读取允许范围外的文件：{abs}");
            return null;
        }
        try
        {
            var info = new FileInfo(abs);
            if (!info.Exists || info.Length > MaxReadFileBytes)
            {
                Logger.Warn($"[警告] 判断脚本读取文件失败（不存在或超过 2MB 上限）：{abs}");
                return null;
            }
            return File.ReadAllText(abs, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 判断脚本读取文件失败：{abs}（{ex.Message}）");
            return null;
        }
    }

    /// <summary>写入 script 目录（相对路径，防逃逸）；失败返回 false。</summary>
    private static bool WriteScriptFile(string scriptDir, string relPath, string content)
    {
        string? target = ResolveWithin(scriptDir, relPath);
        if (target is null)
        {
            Logger.Warn($"[警告] 判断脚本写入被拒绝（非法路径）：{relPath}");
            return false;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, content, new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 判断脚本写入失败：{target}（{ex.Message}）");
            return false;
        }
    }

    /// <summary>相对路径解析到允许根目录内（禁止绝对路径与 ../ 逃逸）；非法返回 null。</summary>
    internal static string? ResolveWithin(string root, string relPath)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(relPath))
        {
            return null;
        }
        if (Path.IsPathRooted(relPath))
        {
            return null;
        }
        string rootFull = Path.GetFullPath(root);
        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(root, relPath));
        }
        catch (Exception)
        {
            return null;
        }
        return IsWithin(rootFull, full) ? full : null;
    }

    private static bool IsWithinAny(string abs, params string[] roots)
    {
        foreach (string root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }
            string rootFull = Path.GetFullPath(root);
            if (IsWithin(rootFull, abs))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>路径边界检查：full 必须等于 root 或位于 root 目录之下。
    /// 使用 GetRelativePath 判定，禁止前缀误匹配（如 data\script1evil 命中 data\script1）。</summary>
    private static bool IsWithin(string rootFull, string full)
    {
        if (string.Equals(rootFull, full, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        string rel;
        try
        {
            rel = Path.GetRelativePath(rootFull, full);
        }
        catch (Exception)
        {
            return false;
        }
        if (Path.IsPathRooted(rel))
        {
            return false;
        }
        return !rel.Equals("..", StringComparison.Ordinal)
            && !rel.StartsWith("..\\", StringComparison.Ordinal)
            && !rel.StartsWith("../", StringComparison.Ordinal);
    }

    private static async Task<JudgeScriptResult> RunPythonAsync(string code, string inputJson, CancellationToken token)
    {
        var result = new JudgeScriptResult();
        string pyPath = Path.Combine(Path.GetTempPath(), "nexus-judge-" + Guid.NewGuid().ToString("N") + ".py");
        string inputPath = Path.Combine(Path.GetTempPath(), "nexus-judge-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(pyPath, code, new UTF8Encoding(false));
            File.WriteAllText(inputPath, inputJson, new UTF8Encoding(false));
            var psi = new ProcessStartInfo("python.exe", $"\"{pyPath}\" \"{inputPath}\"")
            {
                WorkingDirectory = Path.GetTempPath(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            using Process? process = Process.Start(psi);
            if (process is null)
            {
                result.JudgeError = "无法启动 python.exe（未安装或不在 PATH）";
                return result;
            }
            var stdout = new List<string>();
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.Add(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stdout.Add(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(ScriptTimeoutSeconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                SystemActions.KillTree(process.Id);
                result.JudgeError = $"判断脚本执行超时（{ScriptTimeoutSeconds} 秒）或被取消";
                return result;
            }
            ParseOutput(stdout, result);
            return result;
        }
        catch (Exception ex)
        {
            result.JudgeError = "判断脚本执行失败：" + ex.Message;
            return result;
        }
        finally
        {
            TryDelete(pyPath);
            TryDelete(inputPath);
        }
    }

    /// <summary>从后向前找第一个合法结果行（对象、status 为 success/failed、reason 非空），其余视为未返回。</summary>
    private static void ParseOutput(List<string> lines, JudgeScriptResult result)
    {
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            string line = lines[i].Trim();
            if (line.Length == 0)
            {
                continue;
            }
            try
            {
                using JsonDocument doc = JsonDocument.Parse(line);
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                string status = root.TryGetProperty("status", out JsonElement s) ? s.GetString() ?? "" : "";
                string reason = root.TryGetProperty("reason", out JsonElement r) ? r.GetString() ?? "" : "";
                if (status is not ("success" or "failed") || reason.Length == 0)
                {
                    continue;
                }
                result.Status = status;
                result.Reason = reason;
                result.NotifyText = root.TryGetProperty("notifyText", out JsonElement n) ? n.GetString() ?? "" : "";
                result.ReplaceConfigs = root.TryGetProperty("replaceConfigs", out JsonElement rc) && rc.ValueKind == JsonValueKind.Array
                    ? rc.EnumerateArray().Select(item => item.GetString() ?? "").Where(text => text.Length > 0).ToList()
                    : new List<string>();
                return;
            }
            catch (JsonException)
            {
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
        }
    }
}
