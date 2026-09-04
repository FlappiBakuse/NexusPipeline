using NexusPipeline.Models;
using NexusPipeline.Services.Notification;
using Xunit;

namespace NexusPipeline.Tests;

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
            FinalStatus = "partial",
            ResultDetail = "部分任务未完成",
        };
        var queue = new DispatchQueue { Name = "示例队列" };

        Assert.Contains("运行部分完成（部分任务未完成）", NotificationFormatter.Script(script, record));
        Assert.Contains("部分完成（部分任务未完成）", NotificationFormatter.Queue(queue, [record]));
    }
}
