using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Extensions;
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
public sealed class MaaRetryTests
{
    [Fact(DisplayName = "Original MaaEnd Replays Safe Login After Failed Process Family Closure And Continues Failed And Proven Unstarted Business Tasks")]
    public Task RestartsEndPreparation() => RunAsync("maaend");

    [Fact(DisplayName = "Original MaaStellaSora Replays Safe Login After Failed Process Family Closure And Continues Failed And Proven Unstarted Business Tasks")]
    public Task RestartsStellaPreparation() => RunAsync("maas");

    [Fact(DisplayName = "Original MaaEnd Restarts Its PC Pretask Child After Failure And Retains Both Closed Process Families")]
    public Task RestartsPcPreparation() => RunAsync("maaend", pcStartup: true);

    [Fact(DisplayName = "Original MaaStellaSora Replays Login And Continues Only Remaining Repetitions After Native Failure")]
    public Task ContinuesStellaRepetitions() => RunAsync("maas", repeated: true);

    private static async Task RunAsync(string extension, bool repeated = false, bool pcStartup = false)
    {
        using var diagnostics = new MaaObservationDiagnostics();
        using var native = new SpecializedPackageInputs("maa-framework", "NEXUS_FORMAL_MAA_PACKAGE_RECEIPT", "managed-code", nativeArchive: true);
        using var adapter = new SpecializedPackageInputs(extension, extension == "maaend" ? "NEXUS_PACKAGED_MAAEND_RECEIPT" : "NEXUS_PACKAGED_MAAS_RECEIPT");
        using var project = new ProjectSourceFixture(configuration: true, file: true, enabled: true, taskProjection: true, taskRetry: true,
            configurationName: extension == "maaend" ? "local.json" : "config/instances/one.json", extensionId: extension);
        var catalog = project.Catalog; var marker = Path.Combine(project.Path, "controlled-retry");
        MaaRetrySource.Prepare(project, extension, native.NativeArchive!, marker, repeated, pcStartup);
        var original = File.ReadAllBytes(project.ConfigurationFile!);
        var providers = new ExecutionProviderRegistry();
        using var provider = new InstallRuntimeFixture(catalog: catalog, originalManifest: native.Manifest, originalManifestBytes: native.ManifestBytes,
            packageArchive: native.Archive, providers: providers);
        _ = await provider.Session().ApplyAsync(provider.Prepared, default);
        provider.Prepared.Dispose(); provider.Verified.Dispose(); provider.Root.Dispose();
        using var consumer = new InstallRuntimeFixture(catalog: catalog, originalManifest: adapter.Manifest, originalManifestBytes: adapter.ManifestBytes,
            packageArchive: adapter.Archive, payloadRoots: provider.Roots, existingActivator: provider.Activator, catalogSequence: 2);
        try
        {
            _ = await consumer.Session().ApplyAsync(consumer.Prepared, default);
            consumer.Prepared.Dispose(); consumer.Verified.Dispose(); consumer.Root.Dispose();
            ExtensionProjectionPublication.PublishIfDirty(catalog.Instance, catalog.Database, catalog.Lease,
                catalog.Contracts, provider.Roots, catalog.Resources, catalog.Index);
            var projects = new ProjectCompilationService(catalog.Instance, catalog.Database, catalog.Lease, catalog.Resources, new(), provider.Roots, provider.Activator);
            _ = await projects.SaveAsync(Guid.NewGuid(), "maa-retry", project.Binding, 1, 0, extension, MaaRetrySource.Choice(extension, pcStartup),
                () => { }, DateTimeOffset.UtcNow, default);
            var installed = new ExtensionInstalledEvidence(catalog.Instance, catalog.Database, new(), provider.Roots).ReadCurrent(extension)!;
            var adapters = new TaskAdapterService(catalog.Instance, catalog.Database, catalog.Lease, catalog.Resources, new(), provider.Roots,
                provider.Activator, new(catalog.Instance, catalog.Database, catalog.Lease, catalog.Resources));
            var discovery = await adapters.DiscoverAsync("maa-retry", new(Guid.NewGuid(), project.Binding, 1, extension, installed.Metadata.Revision,
                extension == "maas" ? [new("account", 3, ""), new("settings", 1, ""), new("global", 1, "")] : [new("account", 3, "")]), default);
            Assert.Equal(4, discovery.Tasks.Length);
            Assert.All(discovery.Tasks, task => Assert.True(task.IsRetrySafe));
            var inbox = new ExecutionAdmissionInbox();
            var admission = new ExecutionAdmissionService(catalog.Instance, catalog.Database, new(), catalog.Lease, catalog.Resources, catalog.Index,
                inbox, new(15, 15, 15), payloadRoots: provider.Roots,
                judgementRuntime: new(Environment.GetEnvironmentVariable("NEXUS_TEST_HOST_EXE")!, null), providers: providers);
            admission.ConfigureTaskAdapters(adapters);
            var definitions = new AutomationDefinitionStore(catalog.Database).Read(); var settings = new HostSettingsStore(catalog.Database).Read();
            var accepted = admission.Start(Guid.NewGuid(), "maa-retry", new(new BindingTarget(project.Binding), definitions.Revision, settings.Revision), DateTimeOffset.UtcNow);
            var runtime = new ExecutionRuntimeConsumer(catalog.Instance, catalog.Database, new(), catalog.Lease, catalog.Resources, catalog.Index, inbox,
                liveMedia: new ExecutionLiveMedia(catalog.Instance.InstanceId));
            runtime.ConfigureTaskAdapters(adapters);
            var receipt = Assert.IsType<AttemptReceipt>(await runtime.RunNextAsync());
            Assert.Equal(RecoveryOutcome.Restored, receipt.Facts.Recovery);
            var actual = File.ReadAllLines(marker + ".agent");
            var expected = repeated ? new[] { "login", "completed", "failed", "failed", "login", "failed", "failed", "pending" }
                : ["login", "completed", "failed", "login", "failed", "pending"];
            Assert.True(expected.SequenceEqual(actual),
                string.Join(",", actual) + "\n" + diagnostics.Describe());
            Assert.Equal(original, File.ReadAllBytes(project.ConfigurationFile!));
            var state = AttemptWriterState.Read(catalog.Instance, catalog.Database, new(), accepted.JournalId);
            var targetClosures = TargetJobCodec.ReadHistory(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)
                .Where(item => item.Stage == "stopped").ToArray();
            Assert.Equal(pcStartup ? 0 : 2, targetClosures.Length);
            Assert.Equal(pcStartup ? 0 : 2, targetClosures.Select(item => item.Binding.TargetId).Distinct().Count());
            foreach (var target in targetClosures)
                Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(TargetRootFacts.Read(catalog.Database, target.Binding)!));
            var invocation = Assert.Single(ExecutionInvocationCodec.Read(state.Latest.Root, state.Total));
            var ids = discovery.Tasks.Select(task => task.TaskId).ToArray();
            Assert.Equal(ids.Skip(2), invocation.TaskRetry!.Selection.Targets);
            Assert.Equal(new[] { ids[0], ids[2], ids[3] }, invocation.TaskRetry.Selection.SelectedIds);
            var result = Assert.Single(StepResultCodec.Read(state.Latest.Root, state.Total));
            var blobs = new ImmutableBlobStore(catalog.Instance, new());
            var evidence = StepEvidenceCodec.Read(blobs, result.Binding, result.Latest.Artifact);
            var observed = AdapterObservationCodec.Read(blobs, result.Binding, evidence.RawOutput!, Assert.Single(state.Total.Steps), evidence.TaskAdapter!);
            Assert.All(observed.Observations, item => Assert.Equal(AdapterObservationState.Succeeded, item.State));
            if (repeated)
            {
                Assert.Equal(3, observed.Observations.Single(item => item.TaskId == ids[2]).CompletedRepetitions);
                var retry = invocation.TaskRetry!;
                var before = AdapterObservationCodec.Read(blobs, retry.PreviousBinding, retry.PreviousRaw, Assert.Single(state.Total.Steps), retry.PreviousObservation);
                Assert.Equal(1, before.Observations.Single(item => item.TaskId == ids[2]).CompletedRepetitions);
            }
            if (pcStartup)
            {
                Assert.False(state.Total.Steps[0].Target!.LaunchByHost);
                var games = File.ReadAllLines(marker + ".pc-starts").Select(line => System.Text.Json.JsonSerializer.Deserialize<ProcessIdentity>(line)!).ToArray();
                Assert.Equal(2, games.Length); Assert.NotEqual(games[0], games[1]);
                Assert.All(games, game => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(game)));
            }
            var closure = new JobClosureStore(catalog.Instance, catalog.Database, new()).ReadForExecutionEvidence(result.Binding.ParentJournalId,
                result.Binding.WriterId, result.Binding.JobName, result.Binding.SessionId);
            Assert.NotNull(closure);
            Assert.All(closure.Family, member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
            ConfigurationRunInventory.VerifyReadOnly(catalog.Instance, catalog.Database, new());
            native.VerifyUnchanged(); adapter.VerifyUnchanged();
        }
        finally { await provider.Activator.StopAllAsync(TimeSpan.FromSeconds(15)); }
    }
}
