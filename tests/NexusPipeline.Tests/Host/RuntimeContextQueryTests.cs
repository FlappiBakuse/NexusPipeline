using Xunit;
using NexusPipeline.Host.Composition;
using NexusPipeline.Modules.Queues.Queries;
using NexusPipeline.Modules.Scripts.Queries;
using NexusPipeline.Modules.Users.Queries;

namespace NexusPipeline.Tests.Host;

public sealed class RuntimeContextQueryTests
{
    [Fact]
    public void RuntimeContextResolvesStateBackedQueryServices()
    {
        HostCompositionRoot context = HostCompositionRoot.Instance;

        Assert.NotNull(context.Resolve<ScriptQueries>());
        Assert.NotNull(context.Resolve<QueueQueries>());
        Assert.NotNull(context.Resolve<UserQueries>());
    }
}
