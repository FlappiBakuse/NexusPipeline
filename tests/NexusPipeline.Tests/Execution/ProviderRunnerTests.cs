using System.Text.Json.Nodes;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Scripts.Contracts;
using NexusPipeline.Modules.Users;
using NexusPipeline.Modules.Users.Contracts;
using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Tests.Support;
using Xunit;

namespace NexusPipeline.Tests.Execution;

public sealed class ProviderRunnerTests : IClassFixture<HostTestScope>
{
    public ProviderRunnerTests(HostTestScope host) { }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingRunnerPublishesHistoryAndRejectsProviderOnlySuccess(bool useWorker)
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-provider-runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            for (string? path = AppContext.BaseDirectory; path is not null; path = Path.GetDirectoryName(path))
            {
                string configuration = AppContext.BaseDirectory.Split(Path.DirectorySeparatorChar)
                    .FirstOrDefault(part => part is "Debug" or "Release") ?? throw new InvalidOperationException("test build configuration missing");
                string source = Path.Combine(path, "tests", "fixtures", "NexusPipeline.TestProviderWorker", "bin", configuration, "net8.0-windows");
                if (!Directory.Exists(source)) continue;
                foreach (string file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(root, Path.GetFileName(file)));
                break;
            }
            var script = new ScriptInstance { Id = "dummy-" + Guid.NewGuid().ToString("N"), Name = "Dummy provider", RootPath = root,
                ExecutionProviderId = "dummy", ExecutionProviderConfigId = "p", MaxAttempts = 1 };
            var plan = new PluginProviderPlan("plan", "revision", "authorization", [new("writable_root", root)],
                [new("task", "Task", 0)], new());
            var spec = new ResolvedScriptSpec(script, "1", new(false, "javascript", "provider", "", ""), "hash") { ProviderPlan = plan };
            var availability = new Providers(root, useWorker);
            var history = new PluginAvailabilityPolicyTestsFixture.CapturingHistoryStore();
            var runner = PluginAvailabilityPolicyTestsFixture.CreateRunner(history, availability);
            var user = new ResolvedScriptUser("user", "User", new UserScriptBinding { ScriptInstanceId = script.Id, Enabled = true }, spec);
            var execution = new RunningExecution { Kind = "script", TargetId = script.Id, Mode = "manual", TotalTasks = 1 };
            await runner.RunScriptAsync(execution, new(script, ["User"], ExecutionAdmissionProfile.ForScript(script, resolvedSpec: spec), 1, [user], spec));
            var record = Assert.Single(history.Records);
            Assert.Equal(useWorker ? "unverified" : "failed", record.Status);
            Assert.Equal("unverified", record.Outcomes!.BusinessVerification);
            if (useWorker)
            {
                Assert.Equal("succeeded", record.Outcomes.EngineStatus);
                Assert.Equal(1, record.TaskReport!["structuredEvidenceVersion"]!.GetValue<int>());
                Assert.NotEmpty(record.TaskReport["structuredEvidence"]!.AsArray());
                Assert.Empty(record.TaskReport["finalTaskResults"]![0]!["evidence"]!.AsArray());
                Assert.NotEmpty(record.TaskReport["finalTaskResults"]![0]!["structuredEvidenceRefs"]!.AsArray());
            }
            Assert.Single(execution.SnapshotRecords());
            Assert.NotNull(record.TaskReport);
            Assert.Equal("done", execution.Status);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class Providers(string root, bool useWorker) : IPluginAvailability, IPluginExecutionProviderResolver
    {
        public bool IsKnownPlugin(string name) => name == "dummy";
        public bool IsDataSpecializedPlugin(string name) => false;
        public bool IsEnabled(string name) => name == "dummy";
        public ExecutionProviderDescriptor? ResolveExecutionProvider(string id) => id == "dummy" ? new(id, "1", root, new Provider(root, useWorker)) : null;
    }
    private sealed class Provider(string root, bool useWorker) : IPluginExecutionProvider
    {
        public string Id => "dummy";
        public ValueTask<PluginProviderInspection> InspectAsync(PluginProviderInspectRequest request, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<PluginProviderPlan> PrepareAsync(PluginProviderPrepareRequest request, CancellationToken token) => throw new NotSupportedException();
        public async Task<PluginProviderRunResult> RunAsync(PluginProviderRunContext context, CancellationToken token)
        {
            if (useWorker) await context.Worker.RunAsync(new("NexusPipeline.TestProviderWorker.exe", [], root, new()), context.PublishEvent, token);
            else await context.PublishEvent(new("completed", 1, "task", "succeeded", new JsonObject()));
            return new("succeeded", "dummy completed", true);
        }
    }
}
