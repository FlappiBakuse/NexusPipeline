using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.ControlPlane.Cli.Commands;
using NexusPipeline.ControlPlane.Cli;
using NexusPipeline.Host.Initialization;
using NexusPipeline.Host.Lifecycle;
using NexusPipeline.Host.Composition;
using NexusPipeline.Modules.Updates;
using NexusPipeline.Platform.Windows;
using NexusPipeline.Shared.Logging;

namespace NexusPipeline.Host;

/// <summary>
/// 应用宿主：负责进程级启动、命令分发与服务生命周期编排。
/// 具体运行时数据初始化由 <see cref="RuntimeInitializer"/> 负责，命令业务由 CLI/Control API 适配层承载。
/// </summary>
internal static class ApplicationHost
{
    internal const string KeepWebOnlyAliveArgument = "--keep-alive";

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    /// <summary>当前进程是否为「仅网页模式」（nexus-pipeline.exe web）。</summary>
    internal static bool IsWebOnly { get; set; }

    /// <summary>仅网页模式作为无人值守重启子进程运行时，不因继承到的 stdin EOF 退出。</summary>
    internal static bool KeepWebOnlyAlive { get; set; }

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
        // Installer metadata helpers act on the explicit instance root and never initialize a second Host.
        if (args.FirstOrDefault() == "installer-state") return RunInstallerStateCli(args.Skip(1).ToArray());

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
                    ? CliText.Get("error.admin_required", "需要管理员权限才能执行 NexusPipeline CLI 命令")
                    : CliText.Get("error.runtime_init_failed", "NexusPipeline 运行时初始化失败");
                return CliOutput.WriteFailure(code, message);
            }
            return initializationResult;
        }

        CliTransport.ConfigureStartupPort(() => RuntimeInitializer.InitialSettings.WebPort);
        HostRuntime runtime = HostCompositionRoot.Create(RuntimeInitializer.InitialSettings);
        try
        {
            if (args.Length == 0)
            {
                StartupPipeline.RunService(runtime);
                return 0;
            }

            switch (args[0].ToLowerInvariant())
            {
                case "service":
                    StartupPipeline.RunService(runtime);
                    return 0;
                case "manage":
                    MainMenu.Show();
                    return 0;
                case "status":
                case "help":
                case "-h":
                case "--help":
                    return CliCommandRouter.Run(args);
                case "web":
                    return StartupPipeline.RunWebOnly(runtime, args.Skip(1).ToArray());
                case "restart":
                    return StartupPipeline.RunRestart(
                        runtime,
                        ReadRestartHandoff(args),
                        ReadRestartWebOnly(args),
                        ReadRestartKeepWebOnlyAlive(args));
                case "apply-update":
                    return RunUpdateApplyCli(args.Skip(1).ToArray());
                case "installer-update":
                    return RunInstallerUpdateCli(runtime, args.Skip(1).ToArray());
                case "recover-update":
                    return UpdateApply.RunRecoveryWorker(ReadRestartWebOnly(args), StartupPipeline.SingleInstanceMutexName);
                case "register":
                    WindowsScheduledTaskRegistration.Register();
                    return 0;
                case "unregister":
                    WindowsScheduledTaskRegistration.Unregister();
                    return 0;
                default:
                    return CliCommandRouter.Run(args);
            }
        }
        finally
        {
            runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>读取重启交接标识；旧进程未传该参数时为空，按普通重启启动。</summary>
    internal static string? ReadRestartHandoff(string[] args)
    {
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i].Equals("--handoff", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return args[i + 1];
            }
        }
        return null;
    }

    internal static bool ReadRestartWebOnly(string[] args) => args.Any(argument =>
        argument.Equals("--web", StringComparison.OrdinalIgnoreCase));

    internal static bool ReadRestartKeepWebOnlyAlive(string[] args) =>
        ReadRestartWebOnly(args)
        && args.Any(argument => argument.Equals(KeepWebOnlyAliveArgument, StringComparison.OrdinalIgnoreCase));

    internal static string[] BuildRestartArguments(string handoffId, bool webOnly)
    {
        var arguments = new List<string> { "restart" };
        if (webOnly)
        {
            arguments.Add("--web");
            arguments.Add(KeepWebOnlyAliveArgument);
        }
        arguments.Add("--handoff");
        arguments.Add(handoffId);
        return arguments.ToArray();
    }

    /// <summary>更新工作进程入口，仅由宿主更新流程拉起。</summary>
    private static int RunUpdateApplyCli(string[] args)
    {
        string? staged = null;
        bool webOnly = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--staged", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                staged = args[i + 1];
                i++;
            }
            else if (args[i].Equals("--web", StringComparison.OrdinalIgnoreCase))
            {
                webOnly = true;
            }
        }
        if (string.IsNullOrWhiteSpace(staged))
        {
            Console.WriteLine(CliText.Get(
                "apply.usage",
                "用法：nexus-pipeline.exe apply-update --staged <暂存目录>"));
            return 1;
        }
        try
        {
            return UpdateApply.RunApplyWorker(staged, webOnly, StartupPipeline.SingleInstanceMutexName);
        }
        catch (Exception ex)
        {
            Console.WriteLine(CliText.Get(
                "apply.failed",
                "更新应用失败：{detail}",
                ("detail", ex.Message)));
            return 1;
        }
    }

    private static Dictionary<string, string> ReadInstallerOptions(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal) || !options.TryAdd(args[i], args[i + 1]))
                throw new InvalidDataException("installer.arguments");
        }
        return options;
    }

    private static int RunInstallerStateCli(string[] args)
    {
        try
        {
            if (args.Length == 0) throw new InvalidDataException("installer.command");
            var options = ReadInstallerOptions(args.Skip(1).ToArray());
            string root = options["--root"];
            switch (args[0])
            {
                case "register":
                    InstallationOwnership.Register(root, options["--version"], options["--manifest"]); break;
                case "uninstall":
                    using (var singleInstance = StartupPipeline.AcquireSingleInstanceMutex())
                    {
                        if (singleInstance is null) throw new IOException("installer.instance_running");
                        InstallationOwnership.Uninstall(root, options.GetValueOrDefault("--delete-data") == "true");
                    }
                    break;
                default: throw new InvalidDataException("installer.command");
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }

    private static int RunInstallerUpdateCli(HostRuntime runtime, string[] args)
    {
        try
        {
            var options = ReadInstallerOptions(args);
            string staged = options["--staged"], version = options["--version"], hash = options["--image-hash"], transaction = options["--transaction"];
            var request = new JsonObject { ["stagedDir"] = staged, ["version"] = version, ["imageHash"] = hash, ["transactionId"] = transaction };
            // The copied helper never occupies the replaceable program. An existing owning service keeps admission authority.
            using (var singleInstance = StartupPipeline.AcquireSingleInstanceMutex())
            {
                if (singleInstance is null)
                {
                    int? port = CliTransport.FindServicePort(runtime.Settings.WebPort);
                    if (port is null) throw new IOException("installer.owning_service_unavailable");
                    using var response = CliTransport.Send(port.Value, "POST", "/api/update/installer-apply", request);
                    if (!response.IsSuccessStatusCode) throw new IOException("installer.maintenance_rejected:" + response.StatusCode);
                }
                else
                {
                    var result = runtime.UpdateService.RequestInstallerApply(staged, version, hash, transaction, Audit.System);
                    if (!result.Succeeded) throw new IOException(result.Code + ":" + result.Error);
                }
            }
            DateTime deadline = DateTime.UtcNow.AddSeconds(240);
            string path = UpdateApply.TransactionResultPath(transaction);
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(path))
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(path));
                    if (document.RootElement.GetProperty("TransactionId").GetString() != transaction) throw new IOException("installer.result_identity");
                    if (document.RootElement.GetProperty("Succeeded").GetBoolean()) return 0;
                    throw new IOException(document.RootElement.GetProperty("Code").GetString());
                }
                Thread.Sleep(100);
            }
            throw new TimeoutException("installer.transaction_timeout: preserved journal/backup/staging");
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }
}
