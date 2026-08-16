using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Services;
namespace NexusPipeline.Cli;

/// <summary>通知渠道子菜单：开关 / Webhook / SMTP / 测试通知。</summary>
internal static class ChannelsMenu
{
    public static void Show(RuntimeContext ctx)
    {
        while (true)
        {
            Ui.ClearScreen();
            AppSettings s = ctx.Settings;
            string[] options =
            {
                $"1. 通知渠道开关（Webhook {(s.WebhookEnabled ? "开" : "关")} / SMTP {(s.SmtpEnabled ? "开" : "关")}）",
                $"2. Webhook 地址（当前：{(string.IsNullOrWhiteSpace(s.WebhookUrl) ? "未设置" : "已设置")}）",
                $"3. Webhook 签名密钥（当前：{(SecretStore.TryDecrypt(s.WebhookSecret, out string? ws) && !string.IsNullOrWhiteSpace(ws) ? "已配置" : "未配置")}）",
                $"4. Webhook 类型（当前：{WebhookSender.TypeDisplay(s.WebhookType)}）",
                $"5. generic 自定义模板（当前：{(string.IsNullOrWhiteSpace(s.WebhookTemplate) ? "未设置" : "已设置")}）",
                $"6. SMTP 服务器（当前：{s.SmtpHost}:{s.SmtpPort} {s.SmtpSecure}）",
                $"7. SMTP 账号与授权码（当前：{s.SmtpUser} / {(SecretStore.TryDecrypt(s.SmtpPassword, out string? sp) && !string.IsNullOrWhiteSpace(sp) ? "已配置" : "未配置")}）",
                $"8. SMTP 收件人（当前：{(string.IsNullOrWhiteSpace(s.SmtpTo) ? "未设置" : s.SmtpTo)}）",
                "9. 发送测试通知",
                "0. 返回上级",
            };
            int width = options.Max(option => option.Length);
            var lines = new List<string>
            {
                new string('=', width),
                "通知渠道",
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
                    SetChannelToggles(ctx);
                    break;
                case "2":
                    SetSecret(ctx, "webhookUrl");
                    break;
                case "3":
                    SetSecret(ctx, "webhookSecret");
                    break;
                case "4":
                    SetWebhookType(ctx);
                    break;
                case "5":
                {
                    (EditResult result, string value) = Ui.PromptEdit("请输入 generic 模板（JSON，{text} 为占位符，回车=不变，Esc=清空）：");
                    if (result == EditResult.Entered)
                    {
                        s.WebhookTemplate = value.Trim();
                        Audit.Log(Audit.Manage, "修改通知渠道", "generic 模板");
                    }
                    else if (result == EditResult.Clear)
                    {
                        s.WebhookTemplate = "";
                        Audit.Log(Audit.Manage, "修改通知渠道", "generic 模板已清空");
                    }
                    if (Ui.TrySave(() => ConfigStore.Save(s), "通知渠道"))
                    {
                        Console.WriteLine("[完成] 已保存。");
                    }
                    break;
                }
                case "6":
                    SetSmtpServer(ctx);
                    break;
                case "7":
                {
                    (EditResult result, string value) = Ui.PromptEdit($"SMTP 账号（当前：{s.SmtpUser}，回车=不变，Esc=清空）：");
                    if (result == EditResult.Entered)
                    {
                        s.SmtpUser = value.Trim();
                        Audit.Log(Audit.Manage, "修改通知渠道", "SMTP 账号");
                    }
                    else if (result == EditResult.Clear)
                    {
                        s.SmtpUser = "";
                        Audit.Log(Audit.Manage, "修改通知渠道", "SMTP 账号已清空");
                    }
                    SetSecret(ctx, "smtpPassword");
                    break;
                }
                case "8":
                {
                    (EditResult result, string value) = Ui.PromptEdit($"SMTP 收件人（逗号分隔，回车=不变，Esc=清空）：");
                    if (result == EditResult.Entered)
                    {
                        s.SmtpTo = value.Trim();
                        Audit.Log(Audit.Manage, "修改通知渠道", "SMTP 收件人");
                    }
                    else if (result == EditResult.Clear)
                    {
                        s.SmtpTo = "";
                        Audit.Log(Audit.Manage, "修改通知渠道", "SMTP 收件人已清空");
                    }
                    if (Ui.TrySave(() => ConfigStore.Save(s), "通知渠道"))
                    {
                        Console.WriteLine("[完成] 已保存。");
                    }
                    break;
                }
                case "9":
                {
                    string text = $"[NexusPipeline] 通知测试\r\n时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n收到即配置正确。";
                    bool ok = NotifySender.SendAsync(s, text).GetAwaiter().GetResult();
                    Audit.Log(Audit.Manage, "发送测试通知", ok ? "成功" : "失败");
                    Console.WriteLine(ok ? "[OK] 测试通知发送成功。" : "[错误] 测试通知发送失败，详见日志。");
                    break;
                }
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

    private static void SetChannelToggles(RuntimeContext ctx)
    {
        string? webhookChoice = Ui.Prompt($"Webhook 开关（当前：{(ctx.Settings.WebhookEnabled ? "开" : "关")}，输入 1 开 / 2 关 / 回车不变）：");
        if (webhookChoice?.Trim() is "1" or "2")
        {
            ctx.Settings.WebhookEnabled = webhookChoice.Trim() == "1";
            Audit.Log(Audit.Manage, "修改通知渠道", $"Webhook 开关→{(ctx.Settings.WebhookEnabled ? "开" : "关")}");
        }
        string? smtpChoice = Ui.Prompt($"SMTP 开关（当前：{(ctx.Settings.SmtpEnabled ? "开" : "关")}，输入 1 开 / 2 关 / 回车不变）：");
        if (smtpChoice?.Trim() is "1" or "2")
        {
            ctx.Settings.SmtpEnabled = smtpChoice.Trim() == "1";
            Audit.Log(Audit.Manage, "修改通知渠道", $"SMTP 开关→{(ctx.Settings.SmtpEnabled ? "开" : "关")}");
        }
        if (Ui.TrySave(() => ConfigStore.Save(ctx.Settings), "通知渠道"))
        {
            Console.WriteLine($"[完成] Webhook {(ctx.Settings.WebhookEnabled ? "开" : "关")} / SMTP {(ctx.Settings.SmtpEnabled ? "开" : "关")}");
        }
    }

    private static void SetWebhookType(RuntimeContext ctx)
    {
        Console.WriteLine("Webhook 类型：");
        Console.WriteLine("  1. feishu（飞书）  2. dingtalk（钉钉）  3. wecom（企业微信）");
        Console.WriteLine("  4. slack          5. discord           6. generic（自定义模板）");
        string? choice = Ui.Prompt("请选择（直接回车不变）：");
        string? value = choice?.Trim() switch
        {
            "1" => "feishu",
            "2" => "dingtalk",
            "3" => "wecom",
            "4" => "slack",
            "5" => "discord",
            "6" => "generic",
            _ => null,
        };
        if (value is null)
        {
            Console.WriteLine("[提示] 无效选项。");
            return;
        }
        ctx.Settings.WebhookType = value;
        if (Ui.TrySave(() => ConfigStore.Save(ctx.Settings), "通知渠道"))
        {
            Audit.Log(Audit.Manage, "修改通知渠道", $"Webhook 类型→{value}");
            Console.WriteLine($"[完成] Webhook 类型：{WebhookSender.TypeDisplay(value)}");
        }
    }

    private static void SetSecret(RuntimeContext ctx, string key)
    {
        string label = key switch
        {
            "webhookUrl" => "Webhook 地址",
            "webhookSecret" => "Webhook 签名密钥",
            _ => "SMTP 授权码",
        };
        (EditResult result, string value) = Ui.PromptEditMasked($"请输入{label}（回车=不变，Esc=清空）：");
        if (result == EditResult.Entered)
        {
            ApplySecret(ctx.Settings, key, SecretStore.Encrypt(value));
            if (Ui.TrySave(() => ConfigStore.Save(ctx.Settings), "通知渠道"))
            {
                Audit.Log(Audit.Manage, "修改通知渠道", $"{label}已设置");
                Console.WriteLine($"[完成] {label}已加密保存（绑定当前电脑和用户）。");
            }
        }
        else if (result == EditResult.Clear)
        {
            ApplySecret(ctx.Settings, key, "");
            if (Ui.TrySave(() => ConfigStore.Save(ctx.Settings), "通知渠道"))
            {
                Audit.Log(Audit.Manage, "修改通知渠道", $"{label}已清除");
                Console.WriteLine($"[完成] {label}已清除。");
            }
        }
    }

    private static void ApplySecret(AppSettings settings, string key, string value)
    {
        switch (key)
        {
            case "webhookUrl":
                settings.WebhookUrl = value;
                break;
            case "webhookSecret":
                settings.WebhookSecret = value;
                break;
            case "smtpPassword":
                settings.SmtpPassword = value;
                break;
        }
    }

    private static void SetSmtpServer(RuntimeContext ctx)
    {
        AppSettings s = ctx.Settings;
        (EditResult hostResult, string hostValue) = Ui.PromptEdit($"SMTP 服务器地址（当前：{s.SmtpHost}，回车=不变，Esc=清空）：");
        if (hostResult == EditResult.Entered)
        {
            s.SmtpHost = hostValue.Trim();
        }
        else if (hostResult == EditResult.Clear)
        {
            s.SmtpHost = "";
        }
        (EditResult portResult, string portValue) = Ui.PromptEdit($"端口（当前：{s.SmtpPort}，回车=不变）：");
        if (portResult == EditResult.Entered && int.TryParse(portValue.Trim(), out int port) && port is >= 1 and <= 65535)
        {
            s.SmtpPort = port;
        }
        (EditResult secureResult, string secureValue) = Ui.PromptEdit($"加密方式（auto/ssl/starttls/none，当前：{s.SmtpSecure}，回车=不变）：");
        if (secureResult == EditResult.Entered && secureValue.Trim() is "auto" or "ssl" or "starttls" or "none")
        {
            s.SmtpSecure = secureValue.Trim();
        }
        if (Ui.TrySave(() => ConfigStore.Save(s), "通知渠道"))
        {
            Audit.Log(Audit.Manage, "修改通知渠道", $"SMTP 服务器={s.SmtpHost}:{s.SmtpPort} {s.SmtpSecure}");
            Console.WriteLine("[完成] 已更新 SMTP 服务器设置。");
        }
    }
}
