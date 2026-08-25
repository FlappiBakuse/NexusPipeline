using NexusPipeline.Models;
using NexusPipeline.Services;
namespace NexusPipeline.Cli;

/// <summary>命令行主菜单与状态查看（Program 的 manage/status 入口）。</summary>
internal static class MainMenu
{
    public static void Show()
    {
        while (true)
        {
            Ui.ClearScreen();
            RuntimeContext ctx = RuntimeContext.Instance;
            // 常驻服务运行中时菜单直写配置可能与 Web 端修改互相覆盖，顶部提示。
            bool serviceRunning = CliTransport.FindServicePort(ctx.Settings.WebPort) is not null;
            string[] options =
            {
                $"1. 脚本实例管理（当前：{ctx.Scripts.Count} 个）",
                $"2. 调度队列管理（当前：{ctx.Queues.Count} 个）",
                "3. 调度中心（手动执行脚本或队列）",
                "4. 历史记录",
                "5. 插件",
                "6. 设置",
                "7. 查看状态",
                "8. 维护",
                "0. 退出（关闭窗口）",
            };
            string title = "NexusPipeline 枢链 管理菜单";
            int width = Math.Max(title.Length, options.Max(option => option.Length));
            var lines = new List<string>
            {
                new string('=', width),
                title,
                new string('=', width),
            };
            if (serviceRunning)
            {
                lines.Add("[提示] 检测到常驻服务正在运行：菜单修改可能与 Web 端修改互相覆盖，建议通过 Web 界面操作。");
                width = Math.Max(width, lines[^1].Length);
            }
            lines.AddRange(options);
            lines.Add(new string('=', width));
            Ui.Block(lines);
            string? choice = Ui.Prompt("请选择：");
            if (choice is null)
            {
                return;
            }
            bool skipPause = false;
            switch (choice.Trim())
            {
                case "0":
                    return;
                case "1":
                    ScriptsMenu.Show(ctx);
                    skipPause = true;
                    break;
                case "2":
                    QueuesMenu.Show(ctx);
                    skipPause = true;
                    break;
                case "3":
                    DispatchMenu.Show(ctx);
                    break;
                case "4":
                    HistoryMenu.Show(ctx);
                    break;
                case "5":
                    PluginsMenu.Show(ctx);
                    break;
                case "6":
                    SettingsMenu.Show(ctx);
                    break;
                case "7":
                    ShowStatus();
                    break;
                case "8":
                    MaintenanceMenu.Show(ctx);
                    break;
                default:
                    Console.WriteLine("[提示] 无效选项。");
                    break;
            }
            if (!skipPause)
            {
                Console.WriteLine();
                Console.Write("按回车继续...");
                if (Console.ReadLine() is null)
                {
                    return;
                }
            }
        }
    }

    public static void ShowStatus()
    {
        Ui.ClearScreen();
        RuntimeContext ctx = RuntimeContext.Instance;
        AppSettings s = ctx.Settings;
        Console.WriteLine("===== NexusPipeline 枢链 状态 =====");
        Console.WriteLine($"脚本实例：{ctx.Scripts.Count} 个 | 调度队列：{ctx.Queues.Count} 个");
        Console.WriteLine($"开机自启动：{(TaskRegistration.IsRegistered() ? "已注册" : "未注册")} | 轻量模式：{(s.LightweightMode ? "开" : "关")}");
        int? actualPort = CliTransport.FindServicePort(s.WebPort);
        Console.WriteLine($"Web 界面：http://127.0.0.1:{actualPort ?? s.WebPort}/" + (actualPort is null ? "（未检测到服务）" : ""));
        Console.WriteLine($"日志级别：{s.LogLevel}");
        Console.WriteLine();
        List<RunningExecution> active = ctx.Center.Active.ToList();
        if (active.Count == 0)
        {
            Console.WriteLine("当前没有正在运行的任务。");
        }
        else
        {
            Console.WriteLine($"正在运行（{active.Count}）：");
            foreach (RunningExecution exec in active)
            {
                Console.WriteLine($"  {exec.TargetName}（{exec.Kind}）当前：{exec.CurrentScriptName} {exec.CurrentStatus}");
            }
        }
        Console.WriteLine();
        (bool webhookOk, string webhookReason) = WebhookSender.Status(s);
        (bool smtpOk, string smtpReason) = SmtpSender.Status(s);
        Console.WriteLine($"通知渠道：Webhook {webhookReason} | SMTP {smtpReason}");
        Console.WriteLine($"渠道开关：Webhook {(s.WebhookEnabled ? "开" : "关")} / SMTP {(s.SmtpEnabled ? "开" : "关")}");
        Console.WriteLine();
        foreach (DispatchQueue queue in ctx.Queues.OrderBy(queue => queue.Index))
        {
            Console.WriteLine($"队列「{queue.Name}」：{QueueRule.AutoRunModeDesc(queue.AutoRunMode)}，{queue.Tasks.Count} 个任务，完成操作={QueueRule.CompletionActionDesc(queue.CompletionAction)}");
            if (queue.AutoRunMode == "scheduled")
            {
                foreach (QueueTimeSet ts in queue.TimeSets.Where(ts => ts.Enabled))
                {
                    Console.WriteLine($"    定时：{Ui.DayDesc(ts.Days)} {ts.Time}");
                }
            }
        }
    }
}
