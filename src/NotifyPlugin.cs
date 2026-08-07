using System.Text;

namespace NexusPipeline;

public class NotifyPlugin : IPlugin
{
    public string Name => "notify";

    public string DisplayName => "通知推送";

    public string Description => "脚本实例与调度队列运行状态通知（Webhook / SMTP 邮件）";

    public string Version => "1.0.0";

    public bool IsBuiltIn => true;

    public void Initialize(PluginContext context)
    {
        NotifyRouter.ScriptNotify = NotifyScriptAsync;
        NotifyRouter.QueueNotify = NotifyQueueAsync;
        context.Log("通知推送已接管运行状态通知。");
    }

    public void Shutdown()
    {
        NotifyRouter.ScriptNotify = null;
        NotifyRouter.QueueNotify = null;
    }

    public static async Task NotifyScriptAsync(ScriptInstance script, RunRecord record)
    {
        AppSettings settings = RuntimeContext.Instance.Settings;
        if (!HasChannel(settings))
        {
            Logger.Log($"[通知] 脚本「{script.Name}」完成，但未配置通知渠道，跳过发送。");
            return;
        }
        Logger.Log($"======== 发送脚本运行状态通知：「{script.Name}」 ========");
        await NotifySender.SendAsync(settings, BuildScriptText(script, record)).ConfigureAwait(false);
    }

    public static async Task NotifyQueueAsync(DispatchQueue queue, List<RunRecord> records)
    {
        AppSettings settings = RuntimeContext.Instance.Settings;
        if (!HasChannel(settings))
        {
            Logger.Log($"[通知] 调度队列「{queue.Name}」完成，但未配置通知渠道，跳过发送。");
            return;
        }
        Logger.Log($"======== 发送调度队列汇总通知：「{queue.Name}」 ========");
        await NotifySender.SendAsync(settings, BuildQueueText(queue, records)).ConfigureAwait(false);
    }

    private static string BuildScriptText(ScriptInstance script, RunRecord record)
    {
        string mode = record.Mode == "auto" ? "自动运行" : "手动运行";
        string status = record.Status switch
        {
            "success" => $"运行成功（{record.ResultDetail}）",
            "cancelled" => "运行已取消",
            _ => $"运行失败（{record.ResultDetail}）",
        };
        var lines = new List<string>
        {
            $"[NexusPipeline] 脚本「{script.Name}」",
            $"运行方式：{mode}",
            $"开始时间：{record.StartTime:yyyy-MM-dd HH:mm:ss}",
        };
        if (record.EndTime is not null)
        {
            lines.Add($"结束时间：{record.EndTime:yyyy-MM-dd HH:mm:ss}");
        }
        lines.Add($"尝试次数：{record.Attempts}");
        lines.Add($"最终状态：{status}");
        return string.Join("\r\n", lines);
    }

    private static string BuildQueueText(DispatchQueue queue, List<RunRecord> records)
    {
        var lines = new List<string>
        {
            $"[NexusPipeline] 调度队列「{queue.Name}」运行汇总",
            $"任务总数：{records.Count}",
            "",
        };
        foreach (RunRecord record in records)
        {
            string status = record.Status switch
            {
                "success" => $"成功（{record.ResultDetail}）",
                "cancelled" => "已取消",
                _ => $"失败（{record.ResultDetail}）",
            };
            lines.Add($"· {record.ScriptName}：{status}");
        }
        return string.Join("\r\n", lines);
    }

    private static bool HasChannel(AppSettings settings)
    {
        (bool webhookOk, string _) = WebhookSender.Status(settings);
        (bool smtpOk, string _) = SmtpSender.Status(settings);
        return webhookOk || smtpOk;
    }
}
