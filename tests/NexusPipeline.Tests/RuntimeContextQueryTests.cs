using NexusPipeline.App.Queries;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class RuntimeContextQueryTests
{
    [Fact]
    public void RuntimeContextResolvesStateBackedQueryServices()
    {
        RuntimeContext context = RuntimeContext.Instance;

        Assert.NotNull(context.Resolve<ScriptQueries>());
        Assert.NotNull(context.Resolve<QueueQueries>());
        Assert.NotNull(context.Resolve<UserQueries>());
    }
}
