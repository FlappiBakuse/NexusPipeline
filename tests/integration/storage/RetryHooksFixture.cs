using System.Collections.Immutable;
using System.Runtime.Versioning;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Execution;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class RetryHooksFixture : IAsyncDisposable
{
    internal ProviderRuntimeFixture Provider { get; }
    internal string Marker => Provider.Marker + ".hooks";
    internal ExecutionRetryService Retries => new(Provider.Source.Instance, Provider.Source.Database, new(), Provider.Source.Lease, Provider.Service);

    private RetryHooksFixture(ProviderRuntimeFixture provider) => Provider = provider;

    internal static async Task<RetryHooksFixture> Create(bool configuration = false, int preExit = 0, int postExit = 0,
        bool preOnce = true, bool postFinal = true, string? mode = null)
    {
        var provider = await ProviderRuntimeFixture.PrepareAsync(mode ?? (configuration ? "ordinary-config-retry" : "ordinary-retry"),
            configuration: configuration, retrySafe: true, retryApproved: true, maximumAttempts: 2);
        try
        {
            var fixture = new RetryHooksFixture(provider);
            var source = provider.Source; var catalog = source.Definitions.Read();
            var native = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!;
            var root = new AutomationRoot(Guid.NewGuid(), 0, "Retry hook native", AutomationRootKind.Script, Path.GetDirectoryName(native)!);
            var binding = catalog.Bindings.Single(item => item.Id == source.OtherBinding) with
            {
                PreHook = new(new(root.Id, Path.GetFileName(native)), Args("pre", preExit)),
                PostHook = new(new(root.Id, Path.GetFileName(native)), Args("post", postExit)),
                PreOnceAfterSuccess = preOnce, PostFinalOnly = postFinal
            };
            var change = new AutomationChanges(catalog.Revision, [root], [], [], [binding], [], [], []);
            source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "provider-runtime-fixture", change), change, DateTimeOffset.UtcNow);
            provider.Start(); return fixture;
            ImmutableArray<string> Args(string role, int exit) =>
                ["execution-retry-hook", role, fixture.Marker, exit.ToString(System.Globalization.CultureInfo.InvariantCulture), "false"];
        }
        catch { await provider.DisposeAsync(); throw; }
    }

    internal string[] Calls => File.Exists(Marker + ".calls")
        ? File.ReadAllLines(Marker + ".calls").Select(line => line.Split('|')[0]).ToArray() : [];

    public ValueTask DisposeAsync() => Provider.DisposeAsync();
}
