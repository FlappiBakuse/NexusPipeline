using System.Runtime.InteropServices;
using System.Text;
using NexusPipeline.Cli;
using NexusPipeline.Services;
using NexusPipeline.Services.Update;
using NexusPipeline.Utilities;

namespace NexusPipeline;

/// <summary>
/// 应用宿主：负责进程级启动、命令分发与服务生命周期编排。
/// 具体运行时数据初始化由 <see cref="RuntimeInitializer"/> 负责，命令业务由 CLI/Control API 适配层承载。
/// </summary>
internal static class ApplicationHost
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    /// <summary>当前进程是否为「仅网页模式」（nexus-pipeline.exe web）。</summary>
    internal static bool IsWebOnly { get; set; }

    [STAThread]
    public static int Run(string[] args)
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
        catch (Exception ex)
        {
            Logger.Debug($"设置控制台输出编码失败：{ex.Message}");
        }
        // 管道输出始终使用 UTF-8，便于脚本可靠消费 JSON 与中文消息。
        if (Console.IsOutputRedirected)
        {
            try
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
            }
            catch (Exception ex)
            {
                Logger.Warn($"stdout UTF-8 包装失败，管道输出中文可能乱码：{ex.Message}");
            }
        }
        try
        {
            Console.InputEncoding = new UTF8Encoding(false);
        }
        catch (Exception ex)
        {
            Logger.Debug($"设置控制台输入编码失败：{ex.Message}");
        }

        // machine mode 必须在初始化之前生效，stdout 从第一字节起只承载 JSON envelope。
        CliOutput.Configure(args);

        // 帮助只依赖参数契约，允许在未提权或运行时配置尚未建立时查询。
        if (args.Any(argument => argument is "--help" or "-h")
            || args.FirstOrDefault()?.Equals("help", StringComparison.OrdinalIgnoreCase) == true)
        {
            return CliCommandRouter.Run(args);
        }

        int initializationResult = RuntimeInitializer.Initialize();
        if (initializationResult != 0)
        {
            if (CliOutput.MachineMode && args.Length > 0)
            {
                string code = initializationResult == 2 ? "operation_forbidden" : "internal_error";
                string message = initializationResult == 2
                    ? "需要管理员权限才能执行 NexusPipeline CLI 命令"
                    : "NexusPipeline 运行时初始化失败";
                return CliOutput.WriteFailure(code, message);
            }
            return initializationResult;
        }

        if (args.Length == 0)
        {
            StartupPipeline.RunService();
            return 0;
        }

        switch (args[0].ToLowerInvariant())
        {
            case "service":
                StartupPipeline.RunService();
                return 0;
            case "manage":
                MainMenu.Show();
                return 0;
            case "status":
            case "run-script":
            case "run-queue":
            case "cancel":
            case "help":
            case "-h":
            case "--help":
                return CliCommandRouter.Run(args);
            case "web":
                return StartupPipeline.RunWebOnly(args.Skip(1).ToArray());
            case "restart":
                return StartupPipeline.RunRestart();
            case "apply-update":
                return RunUpdateApplyCli(args.Skip(1).ToArray());
            case "register":
                TaskRegistration.Register();
                return 0;
            case "unregister":
                TaskRegistration.Unregister();
                return 0;
            default:
                return CliCommandRouter.Run(args);
        }
    }

    /// <summary>更新工作进程入口，仅由宿主更新流程拉起。</summary>
    private static int RunUpdateApplyCli(string[] args)
    {
        string? staged = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--staged", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                staged = args[i + 1];
                i++;
            }
        }
        if (string.IsNullOrWhiteSpace(staged))
        {
            Console.WriteLine("[错误] 用法：nexus-pipeline.exe apply-update --staged <暂存目录>");
            return 1;
        }
        try
        {
            return UpdateApply.RunApplyWorker(staged);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[错误] 更新应用失败：{ex.Message}");
            return 1;
        }
    }
}
