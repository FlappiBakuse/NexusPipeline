using Xunit;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.History;
using NexusPipeline.Modules.Scripts;
namespace NexusPipeline.Tests.Execution;


public sealed class ExecutionRunnerFailureTests
{

    [Fact]
    public void CoordinatorException_ProducesSyntheticFailedHistoryRecord()
    {
        var script = new ScriptInstance { Id = "script", Name = "脚本" };

        RunRecord record = ExecutionRunner.CreateHostErrorRecord(
            script,
            "manual",
            "queue",
            "队列",
            "user",
            new InvalidOperationException("协调器异常"));

        Assert.Equal("failed", record.Status);
        Assert.Equal("script", record.ScriptInstanceId);
        Assert.Equal("user", record.UserName);
        Assert.Contains("协调器异常", record.ResultDetail);
    }
}
