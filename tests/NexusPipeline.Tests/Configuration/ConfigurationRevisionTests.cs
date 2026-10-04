using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Scripts.Resolution;
using NexusPipeline.Modules.Users;
using NexusPipeline.Modules.Users.Contracts;
using Xunit;

namespace NexusPipeline.Tests.Configuration;

public sealed class ConfigurationRevisionTests
{
    [Fact]
    public async Task RevisionMismatchBlocksExecutionBeforeAnyLaunchAndOrdinaryVersionsDoNotReset()
    {
        var plugins = new Capabilities();
        var resolver = new ScriptSpecResolver(plugins, plugins);
        var declaration = new ScriptInstance { Id = "revision-test", PluginType = "custom", RootPath = Path.GetTempPath() };
        var spec = resolver.Resolve(declaration);
        Assert.True(spec.Script.RequiresReconfiguration);
        Assert.Equal("", declaration.ConfigurationRevision);
        var user = new ResolvedScriptUser("account", "Account", new UserScriptBinding { ScriptInstanceId = declaration.Id }, spec);
        var coordinator = new ExecutionCoordinator(spec.Script, "manual", "", "", "Account", CancellationToken.None,
            null, null, null, null, null!, plugins, user, spec);
        var result = await coordinator.RunAsync();
        Assert.Equal("run.configuration_setup_required", result.ResultCode);
        Assert.Equal("failed", result.Status);
        declaration.ConfigurationRevision = "settings-v2";
        Assert.False(resolver.Resolve(declaration).Script.RequiresReconfiguration);
        var withoutAccount = new ExecutionCoordinator(resolver.Resolve(declaration).Script, "manual", "", "", null,
            CancellationToken.None, null, null, null, null, null!, plugins);
        Assert.Equal("run.configuration_setup_required", (await withoutAccount.RunAsync()).ResultCode);
        plugins.Version = "0.9.9";
        Assert.False(resolver.Resolve(declaration).Script.RequiresReconfiguration);
        plugins.Revision = "settings-v3";
        Assert.True(resolver.Resolve(declaration).Script.RequiresReconfiguration);
    }

    private sealed class Capabilities : IPluginCapabilityResolver, IPluginAvailability, IEmulatorSupportProviderResolver
    {
        internal string Revision = "settings-v2", Version = "0.1.0";
        public string ConfigurationRevision(string name) => Revision;
        public bool SupportsEmulator(string name) => false;
        public bool HasCapability(string name, string capability) => false;
        public ScriptProfile? ResolveProfile(string name, string root, IReadOnlyDictionary<string, string>? inputs = null) =>
            new() { PluginName = name, PluginVersion = Version, ConfigContractId = Revision };
        public IReadOnlyList<string> GetMissingConfigCandidates(string name, string root, IReadOnlyDictionary<string, string>? inputs) => [];
        public bool IsKnownPlugin(string name) => true;
        public bool IsDataSpecializedPlugin(string name) => true;
        public bool IsEnabled(string name) => true;
        public IReadOnlyList<EmulatorSupportProviderDescriptor> GetEmulatorSupportProviders() => [];
    }
}
