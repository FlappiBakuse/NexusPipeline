using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using NexusPipeline.Cli;
using NexusPipeline.Web;

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
            if (web is not null && ctx.Settings.AutoOpenBrowser)
            {
                TrayApp.OpenWeb(web.Port);
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
        RuntimeContext.Instance.Plugins.LoadAll();
        RunningExecution exec;
        try
        {
            exec = RuntimeContext.Instance.Center.StartScript(script.Id, mode, Audit.Cli, userName);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[错误] {ex.Message}");
            return 1;
        }
        return WaitCli(exec);
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
        RuntimeContext.Instance.Plugins.LoadAll();
        string? blocked = DispatchCenter.QueueBlockedBy(queue);
        if (blocked is not null)
        {
            Console.WriteLine($"[错误] 队列「{queue.Name}」引用的脚本「{blocked}」正在运行，请先退出后再执行。");
            return 1;
        }
        RunningExecution exec;
        try
        {
            exec = RuntimeContext.Instance.Center.StartQueue(queue.Id, mode, Audit.Cli);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[错误] {ex.Message}");
            return 1;
        }
        return WaitCli(exec);
    }

    private static int WaitCli(RunningExecution exec)
    {
        string lastStatus = "";
        while (exec.Status == "running")
        {
            if (!string.IsNullOrEmpty(exec.CurrentStatus) && exec.CurrentStatus != lastStatus)
            {
                lastStatus = exec.CurrentStatus;
                Console.WriteLine($"  {exec.CurrentScriptName}：{lastStatus}（第 {exec.CurrentAttempt}/{exec.CurrentMaxAttempts} 次）");
            }
            Thread.Sleep(1000);
        }
        Console.WriteLine();
        foreach (RunRecord record in exec.Records)
        {
            Console.WriteLine($"===== {record.ScriptName} =====");
            Console.WriteLine($"状态：{record.Status}（{record.ResultDetail}）");
            Console.WriteLine($"开始：{record.StartTime:HH:mm:ss}  结束：{record.EndTime:HH:mm:ss}");
            foreach (RunAttempt attempt in record.AttemptDetails)
            {
                Console.WriteLine($"  第 {attempt.Number} 次：{attempt.Status}（{attempt.Reason}）");
            }
        }
        return exec.Records.Count > 0 && exec.Records.All(record => record.Status == "success") ? 0 : 1;
    }

    private static int CancelCli(string[] args)
    {
        string runId = args.Length > 0 ? args[0] : "";
        if (string.IsNullOrWhiteSpace(runId))
        {
            Console.WriteLine("[错误] 用法：nexus-pipeline.exe cancel <运行ID>");
            return 1;
        }
        try
        {
            RuntimeContext.Instance.Center.Cancel(runId, Audit.Cli);
            Console.WriteLine("[OK] 已发送取消请求。");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[错误] {ex.Message}");
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
        Console.WriteLine("     手动执行脚本实例并等待结果（-user 指定使用哪个用户的配置）");
        Console.WriteLine("  nexus-pipeline.exe run-queue <队列ID或名称> [-Auto|-Manual]");
        Console.WriteLine("     手动执行调度队列并等待结果");
        Console.WriteLine("  nexus-pipeline.exe cancel <运行ID>");
        Console.WriteLine("     取消正在运行的脚本或队列");
        Console.WriteLine("  nexus-pipeline.exe register / unregister");
        Console.WriteLine("     注册 / 取消开机自启动");
        Console.WriteLine();
        Console.WriteLine("提示：网页管理界面默认 http://127.0.0.1:58731/");
    }
}
