using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Scripts.Persistence;
using NexusPipeline.Modules.Scripts.Resolution;
using NexusPipeline.Modules.Scripts.Validation;
using NexusPipeline.Tests.Support;
using NexusPipeline.Plugin.Abstractions;
using System.Text.Json.Nodes;
using Xunit;

namespace NexusPipeline.Tests.Scripts;

public sealed class ExecutionProviderScriptStorageTests
{
    [Fact]
    public void ProviderHostActionsValidateTargetsWithoutRequiringLaunchForManualController()
    {
        string root = Path.Combine(Path.GetTempPath(), "provider-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var script = new ScriptInstance { RootPath = root, ExecutionProviderId = "maa-framework", ExecutionProviderConfigId = "p" };
            Assert.Null(ScriptPathPolicy.CheckScriptPaths(script, new NoCapabilities()));
            script.LaunchGame = true;
            Assert.NotNull(ScriptPathPolicy.CheckScriptPaths(script, new NoCapabilities()));
            script.GameExe = Environment.ProcessPath!;
            Assert.Null(ScriptPathPolicy.CheckScriptPaths(script, new NoCapabilities()));
            script.GameMode = "emulator"; script.GameExe = "127.0.0.1:5555";
            Assert.NotNull(ScriptPathPolicy.CheckScriptPaths(script, new NoCapabilities()));
            script.GameArgs = "am start -n fixture/.Activity";
            Assert.Null(ScriptPathPolicy.CheckScriptPaths(script, new NoCapabilities()));
            script.LaunchGame = false; script.ForceCloseGame = true;
            script.GameExe = "unknown";
            Assert.NotNull(ScriptPathPolicy.CheckScriptPaths(script, new NoCapabilities()));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ProviderReferencesRoundTripWithoutFabricatedMainExeAndMissingProviderFailsClosed()
    {
        string root = Path.Combine(Path.GetTempPath(), "provider-storage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var script = new ScriptInstance
            {
                Id = "direct", Name = "project", RootPath = root,
                ExecutionProviderId = "maa-framework", ExecutionProviderConfigId = "profile-1",
            };
            var storage = new ScriptStorage(root);
            storage.SaveScripts([script]);
            ScriptInstance restored = Assert.Single(storage.LoadScripts());
            Assert.Equal("maa-framework", restored.ExecutionProviderId);
            Assert.Equal("profile-1", restored.ExecutionProviderConfigId);
            Assert.Equal("", restored.MainExe);
            Assert.Null(ScriptPathPolicy.CheckScriptPaths(restored, new NoCapabilities()));
            var resolver = new ScriptSpecResolver(new NoCapabilities(),
                new PluginAvailabilityPolicyTestsFixture.FakePluginAvailability(), storage.JudgeScripts);
            Assert.False(resolver.Resolve(restored).Succeeded);
            var validResolver = new ScriptSpecResolver(new NoCapabilities(),
                new PluginAvailabilityPolicyTestsFixture.FakePluginAvailability(), storage.JudgeScripts,
                executionProviders: new FakeProviders(root));
            Assert.True(validResolver.Resolve(restored).Succeeded);
            restored.ExecutionProviderId = "";
            restored.ExecutionProviderConfigId = "";
            Assert.Contains("主程序", ScriptPathPolicy.CheckScriptPaths(restored, new NoCapabilities()));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class NoCapabilities : IPluginCapabilityResolver
    {
        public bool SupportsEmulator(string pluginName) => false;
        public bool HasCapability(string pluginName, string capabilityKey) => false;
        public ScriptProfile? ResolveProfile(string pluginName, string rootPath, IReadOnlyDictionary<string, string>? inputs = null) => null;
        public IReadOnlyList<string> GetMissingConfigCandidates(string pluginName, string rootPath, IReadOnlyDictionary<string, string>? inputs) => [];
    }

    private sealed class FakeProviders(string directory) : IPluginExecutionProviderResolver
    {
        public ExecutionProviderDescriptor? ResolveExecutionProvider(string providerId) => providerId == "maa-framework"
            ? new(providerId, "0.1.0", directory, new FakeProvider()) : null;
    }

    private sealed class FakeProvider : IPluginExecutionProvider
    {
        public string Id => "maa-framework";
        public ValueTask<PluginProviderInspection> InspectAsync(PluginProviderInspectRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new PluginProviderInspection("project", "1", [], new JsonObject()));
        public ValueTask<PluginProviderPlan> PrepareAsync(PluginProviderPrepareRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new PluginProviderPlan("plan", "revision-1", "fingerprint", [], [], new JsonObject()));
        public Task<PluginProviderRunResult> RunAsync(PluginProviderRunContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new PluginProviderRunResult("succeeded", "", true));
    }
}
