using Xunit;
using NexusPipeline.Host.Composition;
using NexusPipeline.Modules.Queues.Queries;
using NexusPipeline.Modules.Scripts.Queries;
using NexusPipeline.Modules.Users.Queries;
using NexusPipeline.Tests.Support;

namespace NexusPipeline.Tests.Host;

public sealed class RuntimeContextQueryTests : IClassFixture<HostTestScope>
{
    private readonly HostCompositionRoot _context;

    public RuntimeContextQueryTests(HostTestScope host)
    {
        _context = host.Composition;
    }

    [Fact]
    public void RuntimeContextResolvesStateBackedQueryServices()
    {
        HostCompositionRoot context = _context;

        Assert.NotNull(context.Resolve<ScriptQueries>());
        Assert.NotNull(context.Resolve<QueueQueries>());
        Assert.NotNull(context.Resolve<UserQueries>());
    }
}
