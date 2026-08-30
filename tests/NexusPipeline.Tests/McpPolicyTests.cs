using NexusPipeline.App.Contracts;
using NexusPipeline.Mcp;
using NexusPipeline.Models;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class McpPolicyTests
{
    [Theory]
    [InlineData("sleep")]
    [InlineData("reboot")]
    [InlineData("shutdown")]
    [InlineData("exit")]
    public void QueueExecutionPolicy_rejects_existing_system_completion_actions(string action)
    {
        OperationResult<DispatchQueue> result = McpPolicy.ValidateQueueExecution(new DispatchQueue
        {
            Name = "existing-queue",
            CompletionAction = action,
        });

        Assert.False(result.Succeeded);
        Assert.Equal("dangerous_completion_action", result.ErrorCode);
        Assert.Equal(OperationErrorKind.Forbidden, result.ErrorKind);
    }

    [Fact]
    public void QueueExecutionPolicy_accepts_existing_queue_without_completion_action()
    {
        OperationResult<DispatchQueue> result = McpPolicy.ValidateQueueExecution(new DispatchQueue
        {
            Name = "safe-existing-queue",
            CompletionAction = "none",
        });

        Assert.True(result.Succeeded);
        Assert.Equal("safe-existing-queue", result.Value?.Name);
    }
}
