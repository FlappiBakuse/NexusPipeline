using Xunit;
using System.Text.Json.Nodes;
using NexusPipeline.Modules.History;
using NexusPipeline.Modules.Notifications;
using NexusPipeline.Modules.Queues;
using NexusPipeline.Modules.Scripts;

namespace NexusPipeline.Tests.Notifications;

public sealed class NotificationFormatterTests
{
    [Fact]
    public void PartialStatusUsesDedicatedScriptAndQueueText()
    {
        var script = new ScriptInstance { Name = "示例脚本" };
        var record = new RunRecord
        {
            ScriptName = script.Name,
            Status = "partial",
            ResultDetail = "部分任务未完成",
        };
        var queue = new DispatchQueue { Name = "示例队列" };

        Assert.Contains("运行部分完成（部分任务未完成）", NotificationFormatter.Script(script, record));
        Assert.Contains("部分完成（部分任务未完成）", NotificationFormatter.Queue(queue, [record]));
    }

    [Fact]
    public void UnverifiedOnlyUsesDistinctStatusInScriptAndQueueNotifications()
    {
        var script = new ScriptInstance { Name = "示例脚本" };
        var record = new RunRecord
        {
            ScriptName = script.Name,
            Status = "partial",
            ResultCode = "tasks_unverified",
            ResultDetail = "incomplete",
            TaskReport = JsonNode.Parse("""{"summary":{"counts":{"unknown":2}}}""")!.AsObject(),
        };
        var queue = new DispatchQueue { Name = "示例队列" };

        Assert.Contains("最终状态：流程已结束 · 有 2 项未核验", NotificationFormatter.Script(script, record));
        Assert.Contains("示例脚本：流程已结束 · 有 2 项未核验", NotificationFormatter.Queue(queue, [record]));
    }
}
