using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Cli;
using NexusPipeline.Web;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Services;
using NexusPipeline.Utilities;

namespace NexusPipeline;

public static class Program
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    /// <summary>当前进程是否为「仅网页模式」（nexus-pipeline.exe web，v0.6.5+）：该模式不支持自动重启，仅常驻服务模式支持。</summary>
    internal static bool IsWebOnly { get; private set; }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    [STAThread]
    public static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Logger.Fatal($"未处理异常：{e.ExceptionObject}");
        Application.ThreadException += (_, e) => Logger.Fatal($"UI 线程异常：{e.Exception}");
        bool stdoutRedirected = Console.IsOutputRedirected;
        if (args.Length > 0 && !stdoutRedirected)
        {
            AttachConsole(-1);
        }
        try
        {
            Console.OutputEncoding = new UTF8Encoding(false);
        }
        catch
        {
        }
        // v0.6.3：stdout 重定向（管道/文件）下 Console.OutputEncoding 不生效（实测仍按系统 ANSI 代码页写 GBK），
        // 显式用 UTF-8 流包装 stdout，保证 CLI 管道输出中文正确（e2e CLI 断言依赖；控制台模式不受影响）。
        if (Console.IsOutputRedirected)
        {
            try
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
            }
            catch
            {
            }
        }
        try
        {
            Console.InputEncoding = new UTF8Encoding(false);
        }
        catch
        {
        }

        if (!IsAdministrator())
        {
            const string msg = "NexusPipeline 必须以管理员身份运行（脚本程序需要管理员权限才能被接管运行），当前实例未获得管理员权限，即将退出。请右键「以管理员身份运行」，或确认部署的是提权版（requireAdministrator）。";
            Logger.Fatal(msg);
            Console.Error.WriteLine($"[FATAL] {msg}");
            try
            {
                System.Windows.Forms.MessageBox.Show(msg, "NexusPipeline 需要管理员权限", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch
            {
            }
            return 2;
        }

        RuntimeContext ctx = RuntimeContext.Instance;
        MigrateLegacyConfig();
        // v0.6.6+：先加载约束（ConfigStore 历史保留天数上限随之同步），再加载设置（Normalize 使用 limits 上限）。
        Limits.Load();
        ctx.ReloadSettings();
        ctx.ReloadData();
        if (Limits.Fatals.Count > 0)
        {
            foreach (string fatal in Limits.Fatals)
            {
                Logger.Fatal(fatal);
                Console.Error.WriteLine(fatal);
            }
            Console.Error.WriteLine("约束配置存在致命错误，拒绝启动。请修正 config/limits.json 后重试。");
            return 1;
        }
        foreach (string warning in Limits.Warnings)
        {
            Logger.Warn(warning);
        }

        if (args.Length == 0)
        {
            RunService();
            return 0;
        }

        switch (args[0].ToLowerInvariant())
        {
            case "service":
                RunService();
                return 0;
            case "manage":
                MainMenu.Show();
                return 0;
            case "status":
                MainMenu.ShowStatus();
                return 0;
            case "web":
                return RunWebOnly(args.Skip(1).ToArray());
            case "restart":
                return RunRestart();
            case "run-script":
                return RunScriptCli(args.Skip(1).ToArray());
            case "run-queue":
                return RunQueueCli(args.Skip(1).ToArray());
            case "cancel":
                return CancelCli(args.Skip(1).ToArray());
            case "register":
                TaskRegistration.Register();
                return 0;
            case "unregister":
                TaskRegistration.Unregister();
                return 0;
            case "help":
            case "-h":
            case "--help":
                PrintUsage();
                return 0;
            default:
                Console.WriteLine($"[错误] 未知命令：{args[0]}");
                PrintUsage();
                return 1;
        }
    }

    private static void MigrateLegacyConfig()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ConfigDir);
            var pairs = new[]
            {
                (Legacy: Path.Combine(AppPaths.AppRoot, "settings.json"), New: AppPaths.ConfigPath, Name: "settings.json"),
                (Legacy: Path.Combine(AppPaths.AppRoot, "scripts.json"), New: AppPaths.ScriptsPath, Name: "scripts.json"),
                (Legacy: Path.Combine(AppPaths.AppRoot, "queues.json"), New: AppPaths.QueuesPath, Name: "queues.json"),
            };
            foreach ((string legacy, string dest, string name) in pairs)
            {
                if (File.Exists(legacy) && !File.Exists(dest))
                {
                    File.Move(legacy, dest);
                    Audit.Log(Audit.System, "迁移旧配置文件", $"{name} → config\\{name}");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 迁移旧配置文件失败：{ex.Message}");
        }
    }

    private static void RunService()
    {
        using Mutex? mutex = AcquireSingleInstanceMutex();
        if (mutex is null)
        {
            Logger.Info("检测到 NexusPipeline 已在运行，本次启动退出（可在托盘图标打开管理页面）。");
            TrayApp.OpenWeb();
            return;
        }

        RuntimeContext ctx = RuntimeContext.Instance;
        ctx.ReloadSettings();
        ctx.ReloadData();
        // v0.6.6+：崩溃恢复仅常驻服务执行（manage/web/CLI 由运行时自愈 RecoverIfNeeded 兜底），避免多进程并发恢复竞争文件。
        UserConfigManager.RecoverInterrupted();
        TaskRegistration.SyncWithSettings(ctx.Settings);
        Bootstrap.StartServices();

        WebServer? web = null;
        if (!ctx.Settings.LightweightMode)
        {
            web = Bootstrap.StartWebWithRetry(ctx.Settings.WebPort);
            if (web is not null)
            {
                Bootstrap.AfterWebStarted(web);
                if (ctx.Settings.AutoOpenBrowser)
                {
                    TrayApp.OpenWeb(web.Port);
                }
            }
        }
        else
        {
            Logger.Info("轻量运行模式：不启动 Web 服务与浏览器，仅命令行操作。");
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayApp());

        Bootstrap.Shutdown(web);
        Logger.Info("NexusPipeline 已退出。");
    }

    /// <summary>
    /// 创建单实例互斥体并取得所有权（v0.6.5+）：处理「服务被强杀后互斥体被遗弃」——构造函数会抛
    /// AbandonedMutexException（所有权已授予本线程），此时先打开同一互斥体释放遗弃所有权再重试一次，
    /// 避免强杀后首次启动即崩溃（曾需启动两次）。已有实例在运行时返回 null。
    /// </summary>
    private static Mutex? AcquireSingleInstanceMutex()
    {
        const string name = "NexusPipeline.SingleInstance";
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var mutex = new Mutex(true, name, out bool createdNew);
                if (createdNew)
                {
                    return mutex;
                }
                mutex.Dispose();
                return null;
            }
            catch (AbandonedMutexException ex)
            {
                Logger.Warn($"[警告] 接管上次异常退出残留的单实例互斥体（{ex.Message}），正在重试启动...");
                try
                {
                    using var stale = new Mutex(false, name);
                    stale.ReleaseMutex();
                }
                catch
                {
                }
            }
        }
        Logger.Error("[错误] 获取单实例互斥体失败（两次尝试均被遗弃状态占用）。");
        return null;
    }

    /// <summary>自动重启分支（v0.6.5+）：等待旧进程释放单实例互斥体（旧进程收到退出指令后 ~1 秒退出并释放，
    /// 强杀残留的遗弃互斥体视为已获得），随后进入常驻服务模式。</summary>
    private static int RunRestart()
    {
        Logger.Info("[重启] 正在等待旧进程退出...");
        try
        {
            using var probe = new Mutex(false, "NexusPipeline.SingleInstance");
            DateTime deadline = DateTime.Now.AddSeconds(30);
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
                        break;
                    }
                }
                catch (AbandonedMutexException)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[重启] 等待旧进程退出异常（继续启动）：{ex.Message}");
        }
        RunService();
        return 0;
    }

    private static int RunWebOnly(string[] args)
    {
        // v0.6.6+：web 模式同样抢单实例互斥——常驻服务已在运行时直接退出（防两实例双写配置/数据）。
        using Mutex? mutex = AcquireSingleInstanceMutex();
        if (mutex is null)
        {
            Logger.Info("检测到 NexusPipeline 已在运行，仅网页模式退出（可在托盘图标打开管理页面）。");
            Console.WriteLine("[错误] 检测到 NexusPipeline 已在运行，仅网页模式无法并存。请在托盘图标打开管理页面。");
            return 1;
        }
        IsWebOnly = true;
        RuntimeContext ctx = RuntimeContext.Instance;
        // v0.6.6+：崩溃恢复仅服务类进程执行（service/web 均含调度与配置交换能力；manage/status/CLI 由运行时自愈兜底）。
        UserConfigManager.RecoverInterrupted();
        Bootstrap.StartServices();
        WebServer? web = Bootstrap.StartWebWithRetry(ctx.Settings.WebPort);
        if (web is null)
        {
            Console.WriteLine("[错误] 无法启动 Web 服务（端口均被占用）。");
            return 1;
        }
        Bootstrap.AfterWebStarted(web);
        Console.WriteLine($"Web 界面：http://127.0.0.1:{web.Port}/（按回车停止）");
        if (ctx.Settings.AutoOpenBrowser)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"http://127.0.0.1:{web.Port}/")
                {
                    UseShellExecute = true,
                });
            }
            catch
            {
            }
        }
        // v0.6.6+：正常控制台按回车停止；stdin 重定向（管道/文件）EOF 时退出（修复永久挂起）；
        // 无效 stdin（spawn stdio:ignore，e2e 服务启动方式）Peek 抛异常 → 持续运行直到被外部终止。
        while (true)
        {
            int peek;
            try
            {
                peek = Console.In.Peek();
            }
            catch
            {
                Thread.Sleep(500);
                continue;
            }
            if (peek == -1)
            {
                break;
            }
            Console.ReadLine();
            break;
        }
        Bootstrap.Shutdown(web);
        return 0;
    }

    private static int RunScriptCli(string[] args)
    {
        string target = args.Length > 0 ? args[0] : "";
        string mode = "manual";
        string? userName = null;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i].Equals("-auto", StringComparison.OrdinalIgnoreCase))
            {
                mode = "auto";
            }
            if (args[i].Equals("-manual", StringComparison.OrdinalIgnoreCase))
            {
                mode = "manual";
            }
            if (args[i].Equals("-user", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    Console.WriteLine("[错误] -user 参数缺少用户名（用法：run-script <ID或名称> [-Auto|-Manual] [-user <用户名>]）");
                    return 1;
                }
                userName = args[i + 1];
                i++;
            }
        }
        ScriptInstance? script = RuntimeContext.Instance.FindScript(target)
            ?? RuntimeContext.Instance.Scripts.FirstOrDefault(item => string.Equals(item.Name, target, StringComparison.OrdinalIgnoreCase));
        if (script is null)
        {
            Console.WriteLine($"[错误] 未找到脚本实例：{target}");
            return 1;
        }
        return RunCliViaHttp("script", new { scriptId = script.Id, mode, userName }, script.Name);
    }

    private static int RunQueueCli(string[] args)
    {
        string target = args.Length > 0 ? args[0] : "";
        string mode = "manual";
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i].Equals("-auto", StringComparison.OrdinalIgnoreCase))
            {
                mode = "auto";
            }
            if (args[i].Equals("-manual", StringComparison.OrdinalIgnoreCase))
            {
                mode = "manual";
            }
        }
        DispatchQueue? queue = RuntimeContext.Instance.FindQueue(target)
            ?? RuntimeContext.Instance.Queues.FirstOrDefault(item => string.Equals(item.Name, target, StringComparison.OrdinalIgnoreCase));
        if (queue is null)
        {
            Console.WriteLine($"[错误] 未找到调度队列：{target}");
            return 1;
        }
        return RunCliViaHttp("queue", new { queueId = queue.Id, mode }, queue.Name);
    }

    /// <summary>
    /// 通过常驻服务 HTTP API 提交任务并轮询结果（v0.6.3+）：提交 POST /api/dispatch/{kind}，
    /// 成功后轮询 GET /api/dispatch/{runId} 直至结束，输出贴近原 WaitCli 风格；全部记录 success 返回 0。
    /// </summary>
    private static int RunCliViaHttp(string kind, object body, string displayName)
    {
        int? port = CliTransport.EnsureService();
        if (port is null)
        {
            return 1;
        }
        try
        {
            HttpResponseMessage resp = CliTransport.Post(port.Value, $"/api/dispatch/{kind}", body);
            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"[错误] {CliTransport.ReadError(resp)}");
                return 1;
            }
            string runId = ResolveRunId(resp);
            if (string.IsNullOrWhiteSpace(runId))
            {
                Console.WriteLine("[提示] 任务已提交成功，但未能解析服务响应中的运行 ID（无法轮询结果）。请通过 Web 界面或 manage 菜单查看运行状态。");
                return 1;
            }
            return PollCliRun(port.Value, runId);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[错误] 提交任务失败：{ex.Message}");
            return 1;
        }
    }

    /// <summary>从提交响应中解析运行 ID（v0.6.7+ 容错：响应非 JSON/字段缺失返回 null，交由调用方区分「已提交但无法轮询」）。</summary>
    private static string? ResolveRunId(HttpResponseMessage resp)
    {
        try
        {
            return JsonNode.Parse(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult())?["runId"]?.ToString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>轮询运行结果：每 1 秒查询一次，状态变化时打印进度；结束后输出各记录明细。连续 3 次网络失败退出 1；总时长上限 6 小时（v0.6.7+，防挂死）。</summary>
    private static int PollCliRun(int port, string runId)
    {
        string lastStatus = "";
        int consecutiveFailures = 0;
        JsonNode? node = null;
        DateTime deadline = DateTime.Now.AddHours(6);
        while (true)
        {
            if (DateTime.Now >= deadline)
            {
                Console.WriteLine("[错误] 轮询运行结果超过 6 小时上限（任务可能仍在运行）。请通过 Web 界面或 manage 菜单查看状态。");
                return 1;
            }
            try
            {
                HttpResponseMessage resp = CliTransport.Get(port, $"/api/dispatch/{runId}");
                consecutiveFailures = 0;
                if (!resp.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[错误] 查询运行状态失败：HTTP {(int)resp.StatusCode}（{CliTransport.ReadError(resp)}）");
                    return 1;
                }
                node = JsonNode.Parse(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            }
            catch (Exception)
            {
                consecutiveFailures++;
                if (consecutiveFailures >= 3)
                {
                    Console.WriteLine("[错误] 连续 3 次查询运行状态失败（服务可能已退出）。");
                    return 1;
                }
                Thread.Sleep(1000);
                continue;
            }
            string status = node?["status"]?.ToString() ?? "";
            string currentStatus = node?["currentStatus"]?.ToString() ?? "";
            string currentScriptName = node?["currentScriptName"]?.ToString() ?? "";
            int attempt = int.TryParse(node?["currentAttempt"]?.ToString(), out int a) ? a : 0;
            int maxAttempts = int.TryParse(node?["currentMaxAttempts"]?.ToString(), out int m) ? m : 0;
            if (!string.IsNullOrEmpty(currentStatus) && currentStatus != lastStatus)
            {
                lastStatus = currentStatus;
                Console.WriteLine($"  {currentScriptName}：{currentStatus}（第 {attempt}/{maxAttempts} 次）");
            }
            if (status != "running")
            {
                break;
            }
            Thread.Sleep(1000);
        }
        Console.WriteLine();
        if (node?["records"] is not JsonArray records || records.Count == 0)
        {
            Console.WriteLine("[提示] 服务未返回运行记录。");
            return 1;
        }
        foreach (JsonNode? record in records)
        {
            string name = record?["scriptName"]?.ToString() ?? "";
            string recStatus = record?["status"]?.ToString() ?? "";
            string detail = record?["resultDetail"]?.ToString() ?? "";
            Console.WriteLine($"===== {name} =====");
            Console.WriteLine($"状态：{recStatus}（{detail}）");
            Console.WriteLine($"开始：{FmtTime(record?["startTime"]?.ToString())}  结束：{FmtTime(record?["endTime"]?.ToString())}");
            if (record?["attemptDetails"] is JsonArray attempts)
            {
                foreach (JsonNode? attemptNode in attempts)
                {
                    Console.WriteLine($"  第 {attemptNode?["number"]?.ToString()} 次：{attemptNode?["status"]?.ToString()}（{attemptNode?["reason"]?.ToString()}）");
                }
            }
        }
        return records.All(record => record?["status"]?.ToString() == "success") ? 0 : 1;
    }

    /// <summary>ISO 时间字符串取 HH:mm:ss 段（DateTime 无时区，序列化无 Z 后缀）。</summary>
    private static string FmtTime(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso))
        {
            return "";
        }
        int t = iso.IndexOf('T');
        if (t >= 0 && t + 9 <= iso.Length)
        {
            return iso.Substring(t + 1, 8);
        }
        return iso;
    }

    private static int CancelCli(string[] args)
    {
        string runId = args.Length > 0 ? args[0] : "";
        if (string.IsNullOrWhiteSpace(runId))
        {
            Console.WriteLine("[错误] 用法：nexus-pipeline.exe cancel <运行ID>");
            return 1;
        }
        int? port = CliTransport.EnsureService();
        if (port is null)
        {
            return 1;
        }
        try
        {
            HttpResponseMessage resp = CliTransport.Post(port.Value, "/api/cancel", new { runId });
            if (resp.IsSuccessStatusCode)
            {
                Console.WriteLine("[OK] 已发送取消请求。");
                return 0;
            }
            Console.WriteLine($"[错误] {CliTransport.ReadError(resp)}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[错误] 提交取消请求失败：{ex.Message}");
            return 1;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("NexusPipeline 枢链（.NET 版）");
        Console.WriteLine();
        Console.WriteLine("用法：");
        Console.WriteLine("  nexus-pipeline.exe（无参数）");
        Console.WriteLine("     常驻服务模式：托盘图标 + Web 界面 + 调度器（开机自启用此模式）");
        Console.WriteLine("  nexus-pipeline.exe service");
        Console.WriteLine("     同上，显式启动常驻服务");
        Console.WriteLine("  nexus-pipeline.exe web");
        Console.WriteLine("     仅启动网页界面（默认不自动打开浏览器，可在设置中开启）");
        Console.WriteLine("  nexus-pipeline.exe restart");
        Console.WriteLine("     等待旧实例退出后重启常驻服务（由设置页「重启服务」自动调用）");
        Console.WriteLine("  nexus-pipeline.exe manage");
        Console.WriteLine("     打开交互式管理菜单（命令行操作）");
        Console.WriteLine("  nexus-pipeline.exe status");
        Console.WriteLine("     查看状态");
        Console.WriteLine("  nexus-pipeline.exe run-script <脚本ID或名称> [-Auto|-Manual] [-user <用户名>]");
        Console.WriteLine("     手动执行脚本实例并等待结果（需常驻服务运行，未运行时会自动拉起；-user 指定使用哪个用户的配置）");
        Console.WriteLine("  nexus-pipeline.exe run-queue <队列ID或名称> [-Auto|-Manual]");
        Console.WriteLine("     手动执行调度队列并等待结果（需常驻服务运行，未运行时会自动拉起）");
        Console.WriteLine("  nexus-pipeline.exe cancel <运行ID>");
        Console.WriteLine("     取消正在运行的脚本或队列（需常驻服务运行，未运行时会自动拉起）");
        Console.WriteLine("  nexus-pipeline.exe register / unregister");
        Console.WriteLine("     注册 / 取消开机自启动");
        Console.WriteLine();
        Console.WriteLine("提示：网页管理界面默认 http://127.0.0.1:58731/");
    }
}
