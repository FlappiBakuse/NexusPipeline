using Xunit;
using NexusPipeline.Host;
using NexusPipeline.Host.Composition;
using NexusPipeline.Modules.Settings;

namespace NexusPipeline.Tests.Support;

/// <summary>每个 xUnit 测试类显式持有并释放一棵独立 Host 对象图。</summary>
public sealed class HostTestScope : IAsyncLifetime
{
    private readonly HostRuntime _runtime;

    public HostTestScope()
    {
        _runtime = HostCompositionRoot.Create(new AppSettings());
    }

    internal HostCompositionRoot Composition => _runtime.Composition;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _runtime.DisposeAsync();
    }
}
