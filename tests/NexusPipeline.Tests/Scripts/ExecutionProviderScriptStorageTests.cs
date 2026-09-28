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
    public async Task RealPrepareDeadlineStopsTheActualFileReaderBeforeReturningFailure()
    {
        string root = Path.Combine(Path.GetTempPath(), "provider-deadline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var provider = new ReadingProvider(Path.Combine(root, "owned-resource.bin"));
        using (var file = File.Create(provider.Path)) file.SetLength(16 * 1024 * 1024);
        try
        {
            var resolver = new ScriptSpecResolver(new NoCapabilities(),
                new PluginAvailabilityPolicyTestsFixture.FakePluginAvailability(), new ScriptStorage(root).JudgeScripts,
                executionProviders: new FakeProviders(root, provider));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = await Task.Run(() => resolver.Resolve(new ScriptInstance { Id = "deadline", RootPath = root,
                ExecutionProviderId = "maa-framework", ExecutionProviderConfigId = "profile-1" }));
            Assert.False(result.Succeeded);
            Assert.InRange(watch.Elapsed.TotalSeconds, 29, 35);
            Assert.True(provider.Stopped.Task.IsCompletedSuccessfully);
            Assert.True(provider.BytesRead > 0);
            long stoppedBytes = provider.BytesRead;
            await Task.Delay(100);
            Assert.Equal(stoppedBytes, provider.BytesRead);
            Assert.False(provider.RunCalled);
        }
        finally { provider.Cleanup.Cancel(); await provider.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(2)); Directory.Delete(root, true); }
    }

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

    private sealed class FakeProviders(string directory, IPluginExecutionProvider? provider = null) : IPluginExecutionProviderResolver
    {
        public ExecutionProviderDescriptor? ResolveExecutionProvider(string providerId) => providerId == "maa-framework"
            ? new(providerId, "0.1.0", directory, provider ?? new FakeProvider()) : null;
    }

    private sealed class ReadingProvider(string path) : IPluginExecutionProvider
    {
        internal string Path { get; } = path;
        internal long BytesRead;
        internal bool RunCalled;
        internal CancellationTokenSource Cleanup { get; } = new();
        internal TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Id => "maa-framework";
        public ValueTask<PluginProviderInspection> InspectAsync(PluginProviderInspectRequest request, CancellationToken token) =>
            ValueTask.FromResult(new PluginProviderInspection("project", "1", [], new JsonObject()));
        public async ValueTask<PluginProviderPlan> PrepareAsync(PluginProviderPrepareRequest request, CancellationToken token)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, Cleanup.Token);
            try
            {
                using var file = File.OpenRead(Path);
                byte[] block = new byte[64 * 1024];
                while (true)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    int count = file.Read(block);
                    Interlocked.Add(ref BytesRead, count);
                    if (count == 0) file.Position = 0;
                    await Task.Delay(1, linked.Token);
                }
            }
            finally { Stopped.TrySetResult(); }
        }
        public Task<PluginProviderRunResult> RunAsync(PluginProviderRunContext context, CancellationToken token)
        { RunCalled = true; throw new InvalidOperationException("preparation must never run a worker"); }
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
