using Xunit;
using NexusPipeline.Modules.Queues;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Settings.Validation;
using NexusPipeline.Modules.Queues.Validation;
namespace NexusPipeline.Tests.Queues;


/// <summary>约束规则组件测试：把原先依赖 HTTP/浏览器才能观察的边界收敛到确定性调用。</summary>
public sealed class QueueMixPolicyTests
{

    [Fact]
    public void CheckQueueMixRejectsOnlyMixedLongAndNormalScripts()
    {
        var longScript = new ScriptInstance { Id = "long", LogStallTimeoutMinutes = -1, TotalTimeoutMinutes = 120 };
        var normalScript = new ScriptInstance { Id = "normal", LogStallTimeoutMinutes = 5, TotalTimeoutMinutes = 120 };
        var queue = new DispatchQueue
        {
            Tasks =
            [
                new QueueTask { ScriptInstanceId = longScript.Id, Index = 0 },
                new QueueTask { ScriptInstanceId = normalScript.Id, Index = 1 },
            ],
        };

        Assert.NotNull(QueueCompositionPolicy.CheckQueueMix([longScript, normalScript], queue));
        var singleQueue = new DispatchQueue
        {
            Tasks = [new QueueTask { ScriptInstanceId = longScript.Id }],
        };
        Assert.Null(QueueCompositionPolicy.CheckQueueMix([longScript], singleQueue));
    }
}
