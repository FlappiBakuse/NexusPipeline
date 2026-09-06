using NexusPipeline.App.Abstractions;
using NexusPipeline.Models;
using NexusPipeline.Services;
using NexusPipeline.Services.Execution;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class ExecutionValidationTests
{
    [Fact]
    public void ExecutionRequest_CanBeValidatedWithoutStartingRuntimeWork()
    {
        var script = new ScriptInstance
        {
            Id = "script-1",
            Name = "示例脚本",
        };
        var user = new NexusUser
        {
            Id = "user-1",
            Name = "user-1",
            Bindings =
            [
                new UserScriptBinding { ScriptInstanceId = script.Id, Enabled = true },
            ],
        };
        var validator = new ExecutionValidator(
            new TestScriptRepository(script),
            new TestQueueRepository(),
            new TestUserRepository(user),
            new AllowAllPluginAvailability());

        ExecutionResult accepted = validator.Validate(new ExecutionRequest("script", script.Id, "manual"));
        ExecutionResult rejected = validator.Validate(new ExecutionRequest("unknown", "missing", "manual"));

        Assert.True(accepted.Accepted);
        Assert.Same(script, accepted.Script);
        Assert.Equal(1, accepted.TotalTasks);
        Assert.False(rejected.Accepted);
        Assert.Contains("不支持的执行类型", rejected.Error);
    }

    private sealed class TestScriptRepository : IScriptRepository
    {
        private readonly ScriptInstance _script;

        public TestScriptRepository(ScriptInstance script)
        {
            _script = script;
        }

        public ScriptInstance? FindById(string id) => id == _script.Id ? _script : null;

        public IReadOnlyList<ScriptInstance> Snapshot() => new[] { _script };
    }

    private sealed class TestQueueRepository : IQueueRepository
    {
        public DispatchQueue? FindById(string id) => null;

        public IReadOnlyList<DispatchQueue> Snapshot() => Array.Empty<DispatchQueue>();
    }

    private sealed class TestUserRepository : CurrentModelUserRepository
    {
        public TestUserRepository(params NexusUser[] users) : base(users)
        {
        }
    }
}
