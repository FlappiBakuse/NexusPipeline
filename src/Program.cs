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
        ctx.ReloadSettings();
        ctx.ReloadData();
        Limits.Load();
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
        UserConfigManager.RecoverInterrupted();

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
        using var mutex = new Mutex(true, "NexusPipeline.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            Logger.Info("检测到 NexusPipeline 已在运行，本次启动退出（可在托盘图标打开管理页面）。");
            TrayApp.OpenWeb();
            return;
        }

        RuntimeContext ctx = RuntimeContext.Instance;
        ctx.ReloadSettings();
        ctx.ReloadData();
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

    private static int RunWebOnly(string[] args)
    {
        RuntimeContext ctx = RuntimeContext.Instance;
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
        while (true)
        {
            try
            {
                if (Console.KeyAvailable && Console.ReadLine() is not null)
                {
                    break;
                }
            }
            catch
            {
            }
            Thread.Sleep(200);
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
            if (args[i].Equals("-user", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
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
        int? port = EnsureCliService();
        if (port is null)
        {
            return 1;
        }
        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(5);
        try
        {
            HttpResponseMessage resp = client.PostAsync($"http://127.0.0.1:{port}/api/dispatch/{kind}",
                new StringContent(JsonSerializer.Serialize(body, JsonOpts.Default), Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"[错误] {ReadError(resp)}");
                return 1;
            }
            string runId = JsonNode.Parse(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult())?["runId"]?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(runId))
            {
                Console.WriteLine("[错误] 服务未返回运行 ID。");
                return 1;
            }
            return PollCliRun(client, port.Value, runId);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[错误] 提交任务失败：{ex.Message}");
            return 1;
        }
    }

    /// <summary>轮询运行结果：每 1 秒查询一次，状态变化时打印进度；结束后输出各记录明细。连续 3 次网络失败退出 1。</summary>
    private static int PollCliRun(HttpClient client, int port, string runId)
    {
        string lastStatus = "";
        int consecutiveFailures = 0;
        JsonNode? node = null;
        while (true)
        {
            try
            {
                HttpResponseMessage resp = client.GetAsync($"http://127.0.0.1:{port}/api/dispatch/{runId}").GetAwaiter().GetResult();
                consecutiveFailures = 0;
                if (!resp.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[错误] 查询运行状态失败：HTTP {(int)resp.StatusCode}（{ReadError(resp)}）");
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

    /// <summary>确保常驻服务可达：探测失败时轻量模式报错退出，否则自动拉起服务进程并等待（最多 30 秒）。返回实际端口或 null。</summary>
    private static int? EnsureCliService()
    {
        int port = RuntimeContext.Instance.Settings.WebPort;
        if (ProbeService(port, 2000))
        {
            return port;
        }
        if (RuntimeContext.Instance.Settings.LightweightMode)
        {
            Console.WriteLine("[错误] 服务处于轻量运行模式，未启动 Web 接口，无法提交任务");
            return null;
        }
        Console.WriteLine($"[提示] 常驻服务未运行，正在自动拉起（端口 {port}）...");
        try
        {
            string exePath = Environment.ProcessPath ?? "";
            Process.Start(new ProcessStartInfo(exePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[错误] 自动拉起常驻服务失败：{ex.Message}");
            return null;
        }
        DateTime deadline = DateTime.Now.AddSeconds(30);
        while (DateTime.Now < deadline)
        {
            Thread.Sleep(500);
            if (ProbeService(port, 2000))
            {
                return port;
            }
        }
        Console.WriteLine("[错误] 自动拉起常驻服务后仍无法连接（请查看管理器日志确认服务状态）。");
        return null;
    }

    /// <summary>GET /api/status 探测服务可达性（HTTP 2xx 视为可达）。</summary>
    private static bool ProbeService(int port, int timeoutMs)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
            using HttpResponseMessage resp = client.GetAsync($"http://127.0.0.1:{port}/api/status", cts.Token).GetAwaiter().GetResult();
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>读取响应体 {error} 字段（失败时回退原文/状态码）。</summary>
    private static string ReadError(HttpResponseMessage resp)
    {
        try
        {
            string text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonNode.Parse(text)?["error"]?.ToString() ?? text;
        }
        catch
        {
            return $"服务返回错误（HTTP {(int)resp.StatusCode}）";
        }
    }

    private static int CancelCli(string[] args)
    {
        string runId = args.Length > 0 ? args[0] : "";
        if (string.IsNullOrWhiteSpace(runId))
        {
            Console.WriteLine("[错误] 用法：nexus-pipeline.exe cancel <运行ID>");
            return 1;
        }
        int? port = EnsureCliService();
        if (port is null)
        {
            return 1;
        }
        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            HttpResponseMessage resp = client.PostAsync($"http://127.0.0.1:{port}/api/cancel",
                new StringContent(JsonSerializer.Serialize(new { runId }, JsonOpts.Default), Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
            if (resp.IsSuccessStatusCode)
            {
                Console.WriteLine("[OK] 已发送取消请求。");
                return 0;
            }
            Console.WriteLine($"[错误] {ReadError(resp)}");
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
