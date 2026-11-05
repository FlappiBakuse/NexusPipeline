using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class MaaWorkerTests
{
    [Theory(DisplayName = "The Original Maa ZIP Installs Its Compiler And Owned Native Worker While Only The Exact Frozen Supported Controller Can Send Target Input")]
    [InlineData(false)]
    [InlineData(true)]
    public Task RunsOriginalWorker(bool blocked) => RunWorker(blocked, false);

    [Theory(DisplayName = "Explicit Initialization Executes Only The Frozen Pretask Once And Never Adds Business Success Or Automatic Retry")]
    [InlineData(false)]
    [InlineData(true)]
    public Task InitializesOriginalWorker(bool failed) => RunWorker(failed, true);

    [Fact(DisplayName = "Cancelling An Active Initialization Stops Its Owned Pretask Family And Restores Configuration Without Business Records")]
    public Task CancelsOriginalInitialization() => RunWorker(false, true, true);

    [Fact(DisplayName = "Host Shutdown Interrupts An Active Initialization And Restores Configuration Without Business Records")]
    public Task InterruptsOriginalInitialization() => RunWorker(false, true, interrupt: true);

    [Fact(DisplayName = "The Original Maa Worker Starts A Frozen Agent Server And Runs Its Actual Native Custom Action Before Graceful Family Drain")]
    public Task RunsOriginalAgent() => RunWorker(false, false, agent: true);

    [Fact(DisplayName = "The Original Maa Native Worker Uses Frozen Android ADB Input And Archives Native Screenshots Through The Host Device Channel")]
    public Task RunsOriginalAndroid() => RunWorker(false, false, android: true);

    [Fact(DisplayName = "Selected Startup Pretask Returns While Its Owned Game Child Remains Available To Native Tasks And Stops At Completion")]
    public Task RunsOwnedPreparation() => RunWorker(false, false, preparation: true);

    private static async Task RunWorker(bool blocked, bool initialize, bool cancel = false, bool agent = false, bool interrupt = false, bool android = false, bool preparation = false)
    {
        using var input = new SpecializedPackageInputs("maa-framework", "NEXUS_FORMAL_MAA_PACKAGE_RECEIPT", "managed-code", nativeArchive: agent);
        using var project = new ProjectSourceFixture(configuration: true, file: true, enabled: true);
        var catalog = project.Catalog; var marker = Path.Combine(project.Path, "controlled-window");
        MaaWorkerSource.Prepare(project, blocked, marker, agent, android, preparation);
        var providers = new ExecutionProviderRegistry();
        using var provider = new InstallRuntimeFixture(catalog: catalog, originalManifest: input.Manifest, originalManifestBytes: input.ManifestBytes,
            packageArchive: input.Archive, providers: providers);
        var applied = await provider.Session().ApplyAsync(provider.Prepared, default); Assert.True(applied.IsEnabled);
        provider.Prepared.Dispose(); provider.Verified.Dispose(); provider.Root.Dispose();
        if (initialize) MaaInitializationSource.Prepare(project, input.Archive, marker + ".initialized", blocked, cancel || interrupt);
        if (preparation) MaaPreparationSource.Prepare(project, input.Archive, marker);
        if (agent) MaaAgentSource.Prepare(project, input.NativeArchive!, marker + ".agent");
        var original = File.ReadAllBytes(project.ConfigurationFile!);
        var manifest = input.Manifest.DeepClone().AsObject(); manifest["extensionId"] = "maas"; manifest["artifactName"] = "ControlledMaaConsumer";
        manifest["kind"] = "data-specialized"; manifest.Remove("managed"); manifest.Remove("execution"); manifest.Remove("frontend");
        manifest["capabilities"] = new JsonArray("task.adapt", "configuration.edit");
        manifest["permissions"] = new JsonObject { ["fileAccess"] = new JsonArray("selected-script-root") };
        manifest["specialized"] = new JsonObject { ["adapterEntry"] = "adapter.js",
            ["compiler"] = new JsonObject { ["extensionId"] = "maa-framework", ["compilerId"] = "project" },
            ["executionProvider"] = new JsonObject { ["extensionId"] = "maa-framework", ["entryId"] = "main" } };
        using var consumer = new InstallRuntimeFixture(catalog: catalog, originalManifest: manifest,
            managedPayloadEdit: path => File.WriteAllText(Path.Combine(path, "adapter.js"), initialize
                ? "globalThis.nexusTaskAdapter = () => { throw new Error('initialization_must_not_invoke_business_adapter'); };"
                : preparation ? MaaPreparationSource.Adapter : MaaWorkerSource.Adapter),
            payloadRoots: provider.Roots, existingActivator: provider.Activator, catalogSequence: 2);
        try
        {
            _ = await consumer.Session().ApplyAsync(consumer.Prepared, default);
            consumer.Prepared.Dispose(); consumer.Verified.Dispose(); consumer.Root.Dispose();
            ExtensionProjectionPublication.PublishIfDirty(catalog.Instance, catalog.Database, catalog.Lease,
                catalog.Contracts, provider.Roots, catalog.Resources, catalog.Index);
            var projects = new ProjectCompilationService(catalog.Instance, catalog.Database, catalog.Lease, catalog.Resources, new(), provider.Roots, provider.Activator);
            var choice = JsonSerializer.SerializeToElement(new { InterfacePath = "assets/interface.json", ClientKind = "mxu", Language = "zh_cn",
                Runtime = initialize || agent || preparation ? new { NativeDirectory = "native", Executables = new Dictionary<string, string>(), Directories = Array.Empty<string>() } : null,
                Source = new { Kind = "mxu", Path = "local.json", InstanceId = "one" } });
            _ = await projects.SaveAsync(Guid.NewGuid(), "fixture", project.Binding, 1, 0, "maas", choice, () => { }, DateTimeOffset.UtcNow, default);
            var installed = new ExtensionInstalledEvidence(catalog.Instance, catalog.Database, new(), provider.Roots).ReadCurrent("maas")!;
            var adapters = new TaskAdapterService(catalog.Instance, catalog.Database, catalog.Lease, catalog.Resources, new(), provider.Roots,
                provider.Activator, new(catalog.Instance, catalog.Database, catalog.Lease, catalog.Resources));
            if (!initialize)
            {
                var discovery = await adapters.DiscoverAsync("fixture", new(Guid.NewGuid(), project.Binding, 1, "maas", installed.Metadata.Revision,
                    [new("account", preparation ? 3 : 2, "")]), default);
                Assert.Equal(preparation ? 3 : 2, discovery.Tasks.Length); Assert.NotEqual(discovery.Tasks[0].TaskId, discovery.Tasks[1].TaskId);
            }
            var inbox = new ExecutionAdmissionInbox();
            var admission = new ExecutionAdmissionService(catalog.Instance, catalog.Database, new(), catalog.Lease, catalog.Resources, catalog.Index,
                inbox, new(15, 15, 15), payloadRoots: provider.Roots,
                judgementRuntime: new(Environment.GetEnvironmentVariable("NEXUS_TEST_HOST_EXE")!, null), providers: providers);
            admission.ConfigureTaskAdapters(adapters);
            var definitions = new AutomationDefinitionStore(catalog.Database).Read(); var settings = new HostSettingsStore(catalog.Database).Read();
            var operation = Guid.NewGuid();
            var request = new ExecutionSelectionRequest(initialize ? new ProjectInitializationTarget(project.Binding) : new BindingTarget(project.Binding), definitions.Revision, settings.Revision);
            var accepted = admission.Start(operation, "fixture", request, DateTimeOffset.UtcNow);
            var duplicate = admission.Start(operation, "fixture", request, DateTimeOffset.UtcNow);
            Assert.False(duplicate.Acceptance.IsNew);
            Assert.Equal(accepted.JournalId, duplicate.JournalId); Assert.Equal(accepted.Plan, duplicate.Plan);
            var runtime = new ExecutionRuntimeConsumer(catalog.Instance, catalog.Database, new(), catalog.Lease, catalog.Resources, catalog.Index, inbox,
                liveMedia: new ExecutionLiveMedia(catalog.Instance.InstanceId)); runtime.ConfigureTaskAdapters(adapters);
            using var stopping = new CancellationTokenSource();
            var running = runtime.RunNextAsync(stopping.Token);
            if (cancel) await MaaInitializationSource.CancelWhenStarted(catalog, accepted.JournalId, marker + ".initialized", running);
            if (interrupt) await MaaInitializationSource.InterruptWhenStarted(marker + ".initialized", running, stopping);
            var receipt = Assert.IsType<AttemptReceipt>(await running);
            if (cancel) Assert.Equal(LifecycleOutcome.Cancelled, receipt.Facts.Lifecycle);
            else if (interrupt) Assert.Equal(LifecycleOutcome.Interrupted, receipt.Facts.Lifecycle);
            else Assert.Equal(blocked ? EngineOutcome.Failed : EngineOutcome.Succeeded, receipt.Facts.Engine);
            var state = catalog.Lease.PublishExecution(() => AttemptWriterState.Read(catalog.Instance, catalog.Database, new(), accepted.JournalId));
            if (!cancel && !interrupt)
            {
                var terminal = Assert.Single(ProviderTerminalCodec.Read(state.Latest.Root, state.Total));
                var transcript = new ProviderControlReader(catalog.Instance, catalog.Database, new()).Read(terminal.Binding, terminal.Artifact);
                Assert.Equal(initialize ? blocked ? "maa_pretask_failed" : "maa_initialization_completed" : blocked ? "maa_worker_input_rejected" : "maa_tasks_completed", transcript.Terminal!.ReasonCode);
                Assert.Equal("NexusPipeline.MaaWorker.exe", Path.GetFileName(transcript.Archive.Peer.ImagePath));
            }
            var result = Assert.Single(StepResultCodec.Read(state.Latest.Root, state.Total));
            var closure = new JobClosureStore(catalog.Instance, catalog.Database, new()).ReadForExecutionEvidence(result.Binding.ParentJournalId,
                result.Binding.WriterId, result.Binding.JobName, result.Binding.SessionId);
            Assert.NotNull(closure); Assert.True(closure.WasExecuted);
            Assert.All(closure.Family, member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
            if (cancel || interrupt) Assert.Contains(closure.Family, member => Path.GetFileName(member.ImagePath) == "run.exe");
            var lines = File.Exists(marker + ".input") ? File.ReadAllLines(marker + ".input").Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray() : [];
            Assert.Equal(blocked || initialize || agent || android ? 0 : 2, lines.Count(line => line.GetProperty("message").GetInt32() == 0x201));
            if (android) MaaAndroidSource.RequireClicks(project);
            if (agent)
            {
                Assert.Equal(new[] { "action", "action" }, File.ReadAllLines(marker + ".agent"));
                Assert.Equal("stopped", File.ReadAllText(marker + ".agent.stopped"));
                Assert.Contains(closure.Family, member => Path.GetFileName(member.ImagePath) == "run.exe");
            }
            if (initialize)
            {
                Assert.Equal(new[] { "initialized" }, File.ReadAllLines(marker + ".initialized"));
                Assert.Null(state.Total.Steps[0].Target);
                Assert.False(state.Total.RetryPolicy.HostApproved); Assert.Equal(1, state.Total.RetryPolicy.MaximumAttempts);
                Assert.Empty(ExecutionBindingSuccesses.Today(catalog.Database, DateTimeOffset.UtcNow));
                var finality = new ExecutionRunFinality(catalog.Instance, catalog.Database, new());
                var records = new ExecutionRecordProjection(catalog.Instance, catalog.Database, new(), catalog.Lease, finality);
                records.OnCommitted(receipt); records.ReconcileStopped(receipt); Assert.Null(records.ReconcileStoppedPage());
                using var connection = DatabaseConnections.Open(catalog.Database.DatabasePath, readOnly: true);
                using var command = connection.CreateCommand(); command.CommandText = "SELECT count(*) FROM run_records;";
                Assert.Equal(0L, command.ExecuteScalar());
            }
            else AssertNativeFacts(catalog, state, blocked, android);
            if (preparation) MaaPreparationSource.Verify(catalog, state, marker);
            Assert.Equal(original, File.ReadAllBytes(project.ConfigurationFile!));
            Assert.DoesNotContain(Process.GetCurrentProcess().Modules.Cast<ProcessModule>(), module => module.ModuleName == "MaaFramework.dll");
            input.VerifyUnchanged();
        }
        finally { await provider.Activator.StopAllAsync(TimeSpan.FromSeconds(10)); }
    }

    private static void AssertNativeFacts(ExtensionCatalogFixture catalog, AttemptWriterState state, bool blocked, bool android)
    {
        var result = Assert.Single(StepResultCodec.Read(state.Latest.Root, state.Total));
        var blobs = new ImmutableBlobStore(catalog.Instance, new());
        var evidence = StepEvidenceCodec.Read(blobs, result.Binding, result.Latest.Artifact);
        Assert.NotNull(evidence.WorkspaceOutput);
        var raw = RawOutputCodec.Read(blobs, result.Binding, evidence.RawOutput!);
        var events = new List<TaskLogEvent>();
        Assert.True(AdapterLogReader.Read(blobs, raw, (batch, _) => events.AddRange(batch)));
        var facts = events.Where(item => item.SourceId == "stdout" && item.Text.StartsWith("{\"format\":\"nexus.maa.native-fact\",", StringComparison.Ordinal))
            .Select(item => JsonSerializer.Deserialize<JsonElement>(item.Text)).ToArray();
        Assert.Equal(blocked ? 0 : 2, facts.Length);
        var step = Assert.Single(state.Total.Steps);
        foreach (var fact in facts)
        {
            Assert.Equal(3000, fact.GetProperty("status").GetInt32());
            Assert.Equal(result.Binding.RunId.ToString("D"), fact.GetProperty("runId").GetString());
            Assert.Equal(result.Binding.AttemptId.ToString("D"), fact.GetProperty("attemptId").GetString());
            Assert.Equal(result.Binding.PlanDigest, fact.GetProperty("planDigest").GetString());
            Assert.Equal(step.Adapter!.Compiled!.Sha256, fact.GetProperty("projectFingerprint").GetString());
            var task = step.Tasks.Single(task => task.TaskId == fact.GetProperty("taskId").GetString());
            Assert.StartsWith("consumer:", task.TaskId);
            Assert.Equal(task.ExecutionTaskId, fact.GetProperty("executionTaskId").GetString());
        }
        Assert.Equal(facts.Length, facts.Select(fact => fact.GetProperty("nativeTaskId").GetInt64()).Distinct().Count());
        if (!blocked) AssertScreenshots(catalog, blobs, result, evidence, android);
    }

    private static void AssertScreenshots(ExtensionCatalogFixture catalog, ImmutableBlobStore blobs, StepResultState result, StepEvidenceDocument evidence, bool android)
    {
        ProviderOutputArchive archive;
        using (var input = blobs.OpenVerified(evidence.WorkspaceOutput!))
            archive = JsonSerializer.Deserialize<ProviderOutputArchive>(input, StepResultCodec.Options)!;
        var images = archive.Tree.Snapshot.Entries.Where(entry => entry.RelativePath.StartsWith("screencap/", StringComparison.Ordinal) &&
            entry.RelativePath.EndsWith(".png", StringComparison.Ordinal)).ToArray();
        Assert.InRange(images.Length, 1, 2);
        var references = new StepEvidenceReader(catalog.Instance, catalog.Database, new()).ReadReferences(result.Binding, result.Latest.Artifact);
        foreach (var image in images)
        {
            var content = archive.Tree.Contents.Single(item => item.Sha256 == image.Sha256); Assert.Contains(content, references);
            using var input = blobs.OpenVerified(content); var header = new byte[24]; input.ReadExactly(header);
            Assert.Equal(android ? 3 : 640, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16)));
            Assert.Equal(android ? 2 : 480, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20)));
        }
        var grant = new ProviderControlReader(catalog.Instance, catalog.Database, new()).Bootstrap(result.Binding, evidence.ProviderControl!).Workspace!;
        Directory.Move(Path.Combine(grant.Path, "outputs"), Path.Combine(grant.Path, "retired-output"));
        Assert.Equal(result.Latest.Artifact, new StepEvidenceReader(catalog.Instance, catalog.Database, new())
            .Read(result.Binding, result.Latest.Artifact, true).Artifact);
    }
}
