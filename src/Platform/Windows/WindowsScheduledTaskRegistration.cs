using System.Diagnostics;
using System.Xml;
using System.Xml.Linq;
using NexusPipeline.Shared.Logging;

namespace NexusPipeline.Platform.Windows;

internal static class WindowsScheduledTaskRegistration
{
    private const string TaskName = "NexusPipeline " + Storage.InstallationGeneration.Id;
    internal enum ActionState { Legacy, Current, Unowned }

    public static bool IsRegistered() => RunSchTask(["/query", "/tn", TaskName]).ExitCode == 0;
    public static void Register() => Sync(true);
    public static void Unregister() => Sync(false);

    public static void Sync(bool autoStart)
    {
#if NEXUS_TEST_HOST
        if (Environment.GetEnvironmentVariable("NEXUS_SYSTEM_ACTION_DRYRUN") == "1") return;
#endif
        string? executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable)) return;
        try { Sync(autoStart, executable, RunSchTask); }
        catch (Exception exception) { Logger.Warn("startup_task_sync_failed: " + exception.GetType().Name); }
    }

    internal static void Sync(bool autoStart, string executable, Func<string[], (int ExitCode, string Output)> execute)
    {
        var query = execute(["/query", "/tn", TaskName, "/xml"]);
        bool exists = query.ExitCode == 0;
        ActionState action = exists ? InspectAction(query.Output, executable) : ActionState.Unowned;
        if (exists && action == ActionState.Unowned)
        {
            Logger.Warn("startup_task_unowned");
            return;
        }
        if (autoStart && (!exists || action == ActionState.Legacy))
        {
            // /f is restricted to a verified legacy action; failed queries must never overwrite an existing task.
            string[] args = ["/create", "/tn", TaskName, "/tr", $"\"{executable}\" service", "/sc", "onlogon", "/rl", "highest"];
            var result = execute(exists ? [.. args, "/f"] : args);
            if (result.ExitCode == 0) Audit.Log(Audit.System, "注册开机自启动（计划任务，最高权限）", executable);
            else Logger.Warn("startup_task_register_failed: " + result.ExitCode);
        }
        else if (!autoStart && exists)
        {
            var result = execute(["/delete", "/tn", TaskName, "/f"]);
            if (result.ExitCode == 0) Audit.Log(Audit.System, "取消开机自启动");
            else Logger.Warn("startup_task_delete_failed: " + result.ExitCode);
        }
    }

    internal static ActionState InspectAction(string xml, string executable)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 128 * 1024,
            });
            XElement root = XElement.Load(reader);
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            XElement[] actions = root.Element(ns + "Actions")?.Elements().ToArray() ?? [];
            if (root.Name != ns + "Task" || actions.Length != 1 || actions[0].Name != ns + "Exec") return ActionState.Unowned;
            string command = actions[0].Element(ns + "Command")?.Value.Trim().Trim('"') ?? "";
            if (!Path.IsPathFullyQualified(command) || !string.Equals(Path.GetFullPath(command), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase))
                return ActionState.Unowned;
            string arguments = actions[0].Element(ns + "Arguments")?.Value.Trim() ?? "";
            return arguments switch { "" => ActionState.Legacy, "service" => ActionState.Current, _ => ActionState.Unowned };
        }
        catch (Exception exception) when (exception is XmlException or ArgumentException or NotSupportedException)
        { return ActionState.Unowned; }
    }

    private static (int ExitCode, string Output) RunSchTask(string[] args)
    {
        var start = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using Process? process = Process.Start(start);
        if (process is null) return (-1, "无法创建 schtasks 进程");
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15000))
        {
            process.Kill(); process.WaitForExit();
            return (-1, "schtasks timeout");
        }
        return (process.ExitCode, output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
    }
}
