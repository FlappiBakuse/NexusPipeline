using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Services;
using NexusPipeline.Utilities;
namespace NexusPipeline.Cli;

/// <summary>系统设置子菜单：自启动 / 轻量模式 / 历史保留 / Web 端口 / 浏览器 / 日志级别 / 清理。</summary>
internal static class SettingsMenu
{
    public static void Show(RuntimeContext ctx)
    {
        while (true)
        {
            Ui.ClearScreen();
            AppSettings s = ctx.Settings;
            (bool webhookOk, string webhookReason) = WebhookSender.Status(s);
            (bool smtpOk, string smtpReason) = SmtpSender.Status(s);
            string[] options =
            {
                $"1. 开机自启动（当前：{(s.AutoStart ? "开" : "关")}）",
                $"2. 轻量运行模式（当前：{(s.LightweightMode ? "开" : "关")}，重启生效）",
                $"3. 历史保留天数（当前：{s.HistoryRetentionDays} 天）",
                $"4. Web 端口（当前：{s.WebPort}）",
                $"5. 启动后自动打开浏览器（当前：{(s.AutoOpenBrowser ? "开" : "关")}）",
                $"6. 日志级别（当前：{s.LogLevel}，即时生效）",
                $"7. 通知渠道（Webhook：{webhookReason} | SMTP：{smtpReason} | 开关：Webhook {(s.WebhookEnabled ? "开" : "关")} / SMTP {(s.SmtpEnabled ? "开" : "关")}）",
                "8. 清理过期历史与日志",
                "0. 返回上级",
            };
            int width = options.Max(option => option.Length);
            var lines = new List<string>
            {
                new string('=', width),
                "设置",
                new string('=', width),
            };
            lines.AddRange(options);
            lines.Add(new string('=', width));
            Ui.Block(lines);
            string? choice = Ui.Prompt("请选择：");
            if (choice is null)
            {
                return;
            }
            switch (choice.Trim())
            {
                case "0":
                    return;
                case "1":
                    s.AutoStart = !s.AutoStart;
                    if (Ui.TrySave(() => ConfigStore.Save(s), "设置"))
                    {
                        TaskRegistration.SyncWithSettings(s);
                        Audit.Log(Audit.Manage, "修改设置", $"开机自启动→{(s.AutoStart ? "开" : "关")}");
                        Console.WriteLine($"[完成] 开机自启动已{(s.AutoStart ? "开启" : "关闭")}。");
                    }
                    break;
                case "2":
                    s.LightweightMode = !s.LightweightMode;
                    if (Ui.TrySave(() => ConfigStore.Save(s), "设置"))
                    {
                        Audit.Log(Audit.Manage, "修改设置", $"轻量运行模式→{(s.LightweightMode ? "开" : "关")}");
                        Console.WriteLine($"[完成] 轻量运行模式已{(s.LightweightMode ? "开启" : "关闭")}（重启生效）。");
                    }
                    break;
                case "3":
                {
                    (EditResult result, string value) = Ui.PromptEdit($"保留天数（当前：{s.HistoryRetentionDays}，回车=不变）：");
                    if (result == EditResult.Entered && int.TryParse(value.Trim(), out int days) && days >= 1)
                    {
                        s.HistoryRetentionDays = days;
                        if (Ui.TrySave(() => ConfigStore.Save(s), "设置"))
                        {
                            Audit.Log(Audit.Manage, "修改设置", $"历史保留天数→{days}");
                            Console.WriteLine("[完成] 已保存。");
                        }
                    }
                    break;
                }
                case "4":
                {
                    (EditResult result, string value) = Ui.PromptEdit($"Web 端口（当前：{s.WebPort}，回车=不变）：");
                    if (result == EditResult.Entered && int.TryParse(value.Trim(), out int port) && port is >= 1024 and <= 65535)
                    {
                        s.WebPort = port;
                        if (Ui.TrySave(() => ConfigStore.Save(s), "设置"))
                        {
                            Audit.Log(Audit.Manage, "修改设置", $"Web 端口→{port}");
                            Console.WriteLine("[完成] 已保存（重启生效）。");
                        }
                    }
                    break;
                }
                case "5":
                    s.AutoOpenBrowser = !s.AutoOpenBrowser;
                    if (Ui.TrySave(() => ConfigStore.Save(s), "设置"))
                    {
                        Audit.Log(Audit.Manage, "修改设置", $"自动打开浏览器→{(s.AutoOpenBrowser ? "开" : "关")}");
                        Console.WriteLine($"[完成] 自动打开浏览器已{(s.AutoOpenBrowser ? "开启" : "关闭")}。");
                    }
                    break;
                case "6":
                {
                    (EditResult result, string value) = Ui.PromptEdit($"日志级别（当前：{s.LogLevel}，输入 debug/info/warn/error/fatal，回车=不变）：");
                    if (result == EditResult.Entered && LogLevelUtil.IsValid(value.Trim().ToLowerInvariant()))
                    {
                        s.LogLevel = value.Trim().ToLowerInvariant();
                        if (Ui.TrySave(() => ConfigStore.Save(s), "设置"))
                        {
                            Audit.Log(Audit.Manage, "修改设置", $"日志级别→{s.LogLevel}");
                            Console.WriteLine("[完成] 已保存（即时生效）。");
                        }
                    }
                    break;
                }
                case "7":
                    ChannelsMenu.Show(ctx);
                    break;
                case "8":
                    ctx.History.Cleanup(s.HistoryRetentionDays);
                    Audit.Log(Audit.Manage, "清理过期历史", $"保留 {s.HistoryRetentionDays} 天");
                    break;
                default:
                    Console.WriteLine("[提示] 无效选项。");
                    break;
            }
            Console.WriteLine();
            Console.Write("按回车继续...");
            if (Console.ReadLine() is null)
            {
                return;
            }
        }
    }
}
