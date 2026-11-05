using System.Text;
using System.Collections.Immutable;

namespace NexusPipeline.Platform.Storage.Notifications;

internal static class NotificationReceiptText
{
    internal static (string Title, string Body) Render(ReceiptReadContext context, int ordinal)
    {
        var route = context.Routes[ordinal];
        var receipt = context.Snapshot.Document.Receipt;
        var chinese = context.Snapshot.Document.Locale == NexusPipeline.Core.Settings.HostLocale.ZhCn;
        var labels = route.Kind == NexusPipeline.Core.Notifications.NotificationRoutingKind.Queue
            ? context.Bindings : context.Bindings.Where(item => item.BindingId == route.SourceId).ToImmutableArray();
        var title = labels.Length == 1 ? $"NexusPipeline · {labels[0].UserName} · {labels[0].ScriptName}" :
            chinese ? "NexusPipeline · 队列" : "NexusPipeline · Queue";
        var body = new StringBuilder();
        var facts = receipt.Facts;
        body.AppendLine(title);
        body.AppendLine(chinese ? $"执行：{Status(facts.Lifecycle.ToString())}；引擎：{Status(facts.Engine.ToString())}；业务：{Status(facts.Business.ToString())}；恢复：{Status(facts.Recovery.ToString())}" :
            $"Execution: {facts.Lifecycle}; Engine: {facts.Engine}; Result: {facts.Business}; Recovery: {facts.Recovery}");
        body.AppendLine($"Engine={facts.Engine}; Business={facts.Business}; Lifecycle={facts.Lifecycle}; Recovery={facts.Recovery}");
        body.AppendLine($"Reason={facts.ReasonCode}");
        foreach (var label in labels)
        {
            body.AppendLine($"{label.UserName} / {label.ScriptName}: {label.SelectionDisposition} ({label.SelectionReason})");
            foreach (var step in context.Snapshot.Document.OrderedFacts.Where(item => label.StepIds.Contains(item.StepId)))
                body.AppendLine($"  {step.StepId}: Engine={step.Engine}; Business={step.Business}; Reason={step.ReasonCode}");
        }
        return (Bound(title, 200), Bound(body.ToString().TrimEnd(), 4000));
    }

    private static string Status(string value) => value switch
    {
        "NotStarted" => "未开始", "Running" => "运行中", "Succeeded" => "成功", "Failed" => "失败",
        "Cancelled" => "已取消", "Unknown" => "未知", "Unverified" => "未核实", "Partial" => "部分成功",
        "NotApplicable" => "不适用", "Completed" => "已完成", "Interrupted" => "已中断", "Blocked" => "已阻止",
        "Skipped" => "已跳过", "NotRequired" => "无需恢复", "Pending" => "待恢复", "Restored" => "已恢复",
        "Quarantined" => "已隔离", _ => throw new IOException("notification_outcome_unrecognized")
    };

    private static string Bound(string text, int maximum)
    {
        if (text.Length <= maximum) return text;
        var length = maximum - 1;
        if (char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length] + "…";
    }
}
