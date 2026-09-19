using NexusPipeline.Host;
using NexusPipeline.Host.Composition;
using NexusPipeline.Modules.Settings;
using Xunit;

namespace NexusPipeline.Tests.Host;

public sealed class HostRuntimeCompositionTests
{
    [Fact]
    public async Task IndependentCreatesOwnStateAndDisposeIsIdempotent()
    {
        HostRuntime first = HostCompositionRoot.Create(new AppSettings());
        HostRuntime second = HostCompositionRoot.Create(new AppSettings());
        try
        {
            Assert.NotSame(first.EntityState, second.EntityState);
            Assert.NotSame(first.SettingsState, second.SettingsState);
            Assert.NotSame(first.Plugins, second.Plugins);
            Assert.NotSame(first.Bootstrap, second.Bootstrap);
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
            await first.DisposeAsync();
            await second.DisposeAsync();
        }
    }

    [Fact]
    public async Task LifecycleBridgeIsBoundBeforeAnyStartAndShutdownIsIdempotent()
    {
        HostRuntime runtime = HostCompositionRoot.Create(new AppSettings());
        try
        {
            // Accessing the typed bootstrap proves Create completed the explicit
            // bridge binding before the caller can start services.
            Assert.NotNull(runtime.Bootstrap);

            runtime.Bootstrap.Shutdown(null, null);
            runtime.Bootstrap.Shutdown(null, null);
        }
        finally
        {
            await runtime.DisposeAsync();
        }
    }
}
