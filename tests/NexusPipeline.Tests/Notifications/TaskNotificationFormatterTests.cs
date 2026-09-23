using System.Text.Json.Nodes;
using NexusPipeline.Modules.Notifications;
using NexusPipeline.Shared.Localization;
using Xunit;

namespace NexusPipeline.Tests.Notifications;

public sealed class TaskNotificationFormatterTests
{
    private static JsonObject Report() => JsonNode.Parse("""
        {"runId":"history-fixture","originalPlan":{"tasks":[
         {"id":"parent","name":"Parent","enabled":true,"role":"business"},
         {"id":"child","name":"Child","parentId":"parent","enabled":true,"role":"business"},
         {"id":"done","name":"Done","enabled":true,"role":"business"}]},
         "finalTaskResults":[{"taskId":"parent","status":"partial"},{"taskId":"child","status":"failed","reasonCode":"fixture.error"},
         {"taskId":"done","status":"succeeded"}],"summary":{"counts":{"total":2,"succeeded":1,"skipped":0}}}
        """)!.AsObject();

    [Fact]
    public void FailurePathsAreNotDoubleListedAndDenominatorComesFromHost()
    {
        using var locale = LocaleContext.Push("zh-CN");
        var report = Report(); string original = report.ToJsonString();
        var lines = TaskNotificationFormatter.Format(report);
        Assert.Equal("运行成功任务：Done", lines[0]);
        Assert.Equal("运行失败任务：Parent／Child（fixture.error）", lines[1]);
        Assert.DoesNotContain(lines, line => line.StartsWith("部分失败任务"));
        Assert.Contains("业务完成：1/2", lines);
        Assert.Contains("历史记录：history-fixture", lines);
        Assert.Equal(original, report.ToJsonString());
    }

    [Theory]
    [InlineData("partial", "部分失败任务")]
    [InlineData("unknown", "无法判定任务")]
    [InlineData("blocked", "未执行任务")]
    [InlineData("cancelled", "已取消任务")]
    public void NestedUnfinishedStatusesRemainVisible(string status, string label)
    {
        using var locale = LocaleContext.Push("zh-CN");
        var report = Report(); report["finalTaskResults"]![1]!["status"] = status;
        Assert.Contains(TaskNotificationFormatter.Format(report), line => line.StartsWith(label + "：Parent／Child"));
    }

    [Fact]
    public void UnknownChildCannotHideConfirmedParentFailure()
    {
        using var locale = LocaleContext.Push("zh-CN");
        var report = Report();
        report["finalTaskResults"]![0]!["status"] = "failed";
        report["finalTaskResults"]![1]!["status"] = "unknown";
        var lines = TaskNotificationFormatter.Format(report);
        Assert.Equal("运行失败任务：Parent", lines[1]);
        Assert.Contains(lines, line => line.StartsWith("无法判定任务：Parent／Child"));
    }

    [Fact]
    public void IncidentResolutionIsLatestWithinAttemptAndDoesNotEraseAnotherAttempt()
    {
        using var locale = LocaleContext.Push("zh-CN");
        var report = Report();
        report["incidents"] = JsonNode.Parse("""
            [{"attemptId":"a1","incident":{"id":"i","taskId":"child","resolution":"open","reasonCode":"old"}},
             {"attemptId":"a1","incident":{"id":"i","taskId":"child","resolution":"recovered","reasonCode":"recovered"}},
             {"attemptId":"a2","incident":{"id":"i","taskId":null,"resolution":"terminal","reasonCode":"unowned"}}]
            """);
        var lines = TaskNotificationFormatter.Format(report);
        Assert.Contains("已恢复异常：Parent／Child：recovered", lines);
        Assert.Contains("未恢复异常：未归属异常：unowned", lines);
        Assert.DoesNotContain(lines, line => line.Contains(":old") || line.Contains("：old"));
    }

    [Fact]
    public void LongListsKeepFailureOmissionsAndHistoryIdentityWithoutControlCharacters()
    {
        using var locale = LocaleContext.Push("zh-CN");
        var report = Report(); var tasks = report["originalPlan"]!["tasks"]!.AsArray();
        var results = report["finalTaskResults"]!.AsArray(); tasks.Clear(); results.Clear();
        for (int i = 0; i < 30; i++)
        {
            tasks.Add(new JsonObject { ["id"] = "t" + i, ["name"] = "Task\n" + i, ["enabled"] = true, ["role"] = "business" });
            results.Add(new JsonObject { ["taskId"] = "t" + i, ["status"] = "failed" });
        }
        var lines = TaskNotificationFormatter.Format(report, "record-override");
        Assert.Contains("另 18 项", lines[1]);
        Assert.DoesNotContain('\n', lines[1]);
        Assert.True(lines[1].Length < 450);
        Assert.Contains("历史记录：record-override", lines);
        Assert.DoesNotContain(lines, line => line.Contains("history-fixture"));
    }
}
