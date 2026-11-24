using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Extensions;
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
public sealed class MaaOriginalProcessRetryTests
{
    [Theory(DisplayName = "Installed Maa Adapters Restart The Original Offline Executables With Their Failed Task And Login Selected")]
    [InlineData("maaend")]
    [InlineData("maaend-pc")]
    [InlineData("maas")]
    public Task RestartsOriginalProgram(string scenario) => RunAsync(scenario);

    [Fact(DisplayName = "The Original Offline MaaStellaSora Replays Only Two Unfinished Repetitions And Restores Its Original Count")]
    public Task ContinuesOriginalRepetitions() => RunAsync("maas-repeat");

    internal static async Task RunAsync(string scenario)
    {
        var pcStartup = scenario == "maaend-pc";
        var repeated = scenario == "maas-repeat";
        var unstarted = scenario.EndsWith("-unstarted", StringComparison.Ordinal);
        var isEnd = scenario.StartsWith("maaend", StringComparison.Ordinal);
        var extension = isEnd ? "maaend" : "maas";
        var subject = scenario + "-offline-retry";
        var executable = isEnd ? "MaaEnd.exe" : "MFAAvalonia.exe";
        var sourceVariable = unstarted ? (isEnd ? "NEXUS_MAAEND_UNSTARTED_OFFLINE_FIXTURE" : "NEXUS_MAAS_UNSTARTED_OFFLINE_FIXTURE") :
            repeated ? "NEXUS_MAAS_REPEAT_OFFLINE_FIXTURE" : pcStartup ? "NEXUS_MAAEND_PC_OFFLINE_FIXTURE" :
            isEnd ? "NEXUS_MAAEND_OFFLINE_FIXTURE" : "NEXUS_MAAS_OFFLINE_FIXTURE";
        var fixture = Environment.GetEnvironmentVariable(sourceVariable) ?? throw new IOException(sourceVariable + "_required");
        var directory = Path.GetFullPath(fixture);
        var executableHash = isEnd ? "4ad4705b3dd02512b51fcd928ad69507ecd2d9569725fe2ed301edf460dd0c8f"
            : "311923812124fa66312f8b6cd6f4433a1ae84d035d477fe7e89381fe4ac5dd3b";
        Assert.Equal(executableHash,
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, executable)))));
        using var sourceIdentity = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "interface.json")));
        Assert.Equal("NexusOfflineWindow", Assert.Single(sourceIdentity.RootElement.GetProperty("controller").EnumerateArray())
            .GetProperty("name").GetString());
        Assert.Equal("OfflineFixture", Assert.Single(sourceIdentity.RootElement.GetProperty("resource").EnumerateArray())
            .GetProperty("name").GetString());

        using var inputs = new SpecializedPackageInputs(extension, isEnd ? "NEXUS_PACKAGED_MAAEND_RECEIPT" : "NEXUS_PACKAGED_MAAS_RECEIPT");
        using var catalog = new ExtensionCatalogFixture();
        using var installed = new InstallRuntimeFixture(catalog: catalog, originalManifest: inputs.Manifest,
            originalManifestBytes: inputs.ManifestBytes, packageArchive: inputs.Archive);
        await installed.Session().ApplyAsync(installed.Prepared, default);
        installed.Prepared.Dispose(); installed.Verified.Dispose(); installed.Root.Dispose();
        ExtensionProjectionPublication.PublishIfDirty(catalog.Instance, catalog.Database, catalog.Lease,
            catalog.Contracts, installed.Roots, catalog.Resources, catalog.Index);
        try
        {
            var seed = AutomationDefinitionSeed.Create(Path.GetDirectoryName(catalog.Instance.RuntimeRoot)!);
            var root = seed.Roots[0] with { Path = directory };
            string[] paths = isEnd ? ["config/mxu-MaaEnd.json"] : ["config/instances/one.json", "config/config.json", "appsettings.json"];
            string[] resources = isEnd ? ["main"] : ["main", "global", "settings"];
            var launcher = new AutomationPath(root.Id, "launcher/Launcher.exe");
            var all = new ConfigurationAccessGrant(new([]), true);
            var script = seed.Scripts[0] with
            {
                Kind = AutomationKind.Specialized, ExtensionId = extension, Executable = new(root.Id, executable),
                Arguments = isEnd ? ["--autostart", "--quit-after-run"] : ["--instance", "one"],
                ConfigurationPaths = paths.Select(path => new AutomationPath(root.Id, path)).ToImmutableArray(),
                LogPaths = isEnd ? unstarted ? [new(root.Id, "debug/maafw*.log")] : []
                    : repeated ? [new(root.Id, "logs/log-*.log"), new(root.Id, "debug/maafw*.log")]
                    : [new(root.Id, "logs/log-*.log")],
                Target = pcStartup ? new(AutomationTargetKind.Pc, false, 0, true,
                    new(launcher, [], launcher, AutomationPcReadiness.Process), null) : null,
                AdapterResources = resources.Select((id, index) => new AutomationAdapterResource(id, index, "", 1,
                    ConfigurationSyntax.Json, new(index == 0 ? [all] : [], [all], []))).ToImmutableArray(),
                Limits = new(2, 30, 120), Judgement = new(AutomationJudgementLanguage.Keywords,
                    isEnd ? [] : ["任务已全部完成！"], isEnd ? unstarted ? [] : ["Task failed: failed"] : ["任务运行失败！"], null)
            };
            var binding = seed.Bindings[0] with { IsEnabled = true };
            var changes = seed with { Roots = [root], Scripts = [script], Bindings = seed.Bindings.SetItem(0, binding) };
            var definitions = new AutomationDefinitionStore(catalog.Database);
            definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), subject, changes), changes, DateTimeOffset.UtcNow);
            var saved = definitions.Read(); script = saved.Scripts.Single(); binding = saved.Bindings.Single(item => item.Id == binding.Id);
            var original = paths.Select(path => File.ReadAllBytes(Path.Combine(directory, path))).ToArray();
            var blobs = new ImmutableBlobStore(catalog.Instance, catalog.Contracts);
            var revisions = new ConfigurationRevisionStore(catalog.Instance, catalog.Database, catalog.Contracts);
            for (var index = 0; index < paths.Length; index++)
            {
                var resource = script.AdapterResources[index];
                var tree = new FileTreeArchive(new(SnapshotBudget.Default), blobs).Capture(Path.Combine(directory, paths[index]), default).Snapshot;
                var revision = new ConfigurationRevisionChange(new(binding.Id, resource.ResourceId), 0, binding.Revision,
                    AdapterConfigurationPolicy.Contract(script, resource), tree, ConfigurationUpdateKind.Manual);
                revisions.Commit(ConfigurationRevisionStore.Identity(Guid.NewGuid(), subject, revision), revision, DateTimeOffset.UtcNow);
            }

            var adapters = new TaskAdapterService(catalog.Instance, catalog.Database, catalog.Lease, catalog.Resources,
                catalog.Contracts, installed.Roots, installed.Activator,
                new(catalog.Instance, catalog.Database, catalog.Lease, catalog.Resources));
            var inbox = new ExecutionAdmissionInbox();
            var admission = new ExecutionAdmissionService(catalog.Instance, catalog.Database, catalog.Contracts, catalog.Lease,
                catalog.Resources, catalog.Index, inbox, new(15, 15, 15), payloadRoots: installed.Roots,
                judgementRuntime: new(Environment.GetEnvironmentVariable("NEXUS_TEST_HOST_EXE")!, null));
            admission.ConfigureTaskAdapters(adapters);
            var runtime = new ExecutionRuntimeConsumer(catalog.Instance, catalog.Database, catalog.Contracts,
                catalog.Lease, catalog.Resources, catalog.Index, inbox);
            runtime.ConfigureTaskAdapters(adapters);
            var settings = new HostSettingsStore(catalog.Database).Read();
            var accepted = admission.Start(Guid.NewGuid(), subject, new(new BindingTarget(binding.Id), saved.Revision, settings.Revision), DateTimeOffset.UtcNow);
            var receipt = Assert.IsType<AttemptReceipt>(await runtime.RunNextAsync());
            Assert.Equal(RecoveryOutcome.Restored, receipt.Facts.Recovery);
            for (var index = 0; index < paths.Length; index++)
                Assert.Equal(original[index], File.ReadAllBytes(Path.Combine(directory, paths[index])));

            var state = AttemptWriterState.Read(catalog.Instance, catalog.Database, catalog.Contracts, accepted.JournalId);
            var retry = Assert.Single(ExecutionInvocationCodec.Read(state.Latest.Root, state.Total)).TaskRetry!;
            var failed = isEnd ? "maaend:one/failed" : "maas:one/2";
            var login = isEnd ? "maaend:one/login" : "maas:one/0";
            var pending = isEnd ? "maaend:one/pending" : "maas:one/3";
            Assert.Equal(unstarted ? new[] { failed, pending } : [failed], retry.Selection.Targets);
            Assert.Equal(unstarted ? new[] { login, failed, pending } : [login, failed], retry.Selection.SelectedIds);
            var result = Assert.Single(StepResultCodec.Read(state.Latest.Root, state.Total));
            var evidence = StepEvidenceCodec.Read(blobs, result.Binding, result.Latest.Artifact);
            var task = Assert.Single(state.Total.Steps);
            if (pcStartup) Assert.False(task.Target!.LaunchByHost);
            var observed = AdapterObservationCodec.Read(blobs, result.Binding, evidence.RawOutput!, task, evidence.TaskAdapter!);
            Assert.All(observed.Observations.Where(item => retry.Selection.SelectedIds.Contains(item.TaskId)),
                item => Assert.Equal(AdapterObservationState.Succeeded, item.State));
            if (repeated || unstarted)
            {
                var before = AdapterObservationCodec.Read(blobs, retry.PreviousBinding, retry.PreviousRaw, task, retry.PreviousObservation);
                if (repeated)
                {
                    Assert.Equal(1, before.Observations.Single(item => item.TaskId == failed).CompletedRepetitions);
                    Assert.Equal(3, observed.Observations.Single(item => item.TaskId == failed).CompletedRepetitions);
                }
                if (unstarted)
                {
                    var pendingBefore = before.Observations.Single(item => item.TaskId == pending);
                    Assert.Equal(AdapterObservationState.Unknown, pendingBefore.State);
                    Assert.Equal(AdapterCompletion.NotExecuted, pendingBefore.CompletionKind);
                    Assert.All(observed.Observations, item => Assert.Equal(AdapterObservationState.Succeeded, item.State));
                }
            }

            var closures = new JobClosureStore(catalog.Instance, catalog.Database, catalog.Contracts);
            var roots = new[] { retry.PreviousBinding, result.Binding }.Select(item =>
            {
                var closure = closures.ReadForExecutionEvidence(item.ParentJournalId, item.WriterId, item.JobName, item.SessionId);
                Assert.NotNull(closure);
                Assert.True(closure.WasExecuted);
                Assert.All(closure.Family, process => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(process)));
                var pc = pcStartup ? Assert.Single(closure.Family, process => string.Equals(process.ImagePath,
                    Path.Combine(directory, "launcher", "Launcher.exe"), StringComparison.OrdinalIgnoreCase)) : null;
                return (App: Assert.IsType<ProcessIdentity>(closure.Root), Pc: pc);
            }).ToArray();
            Assert.NotEqual(roots[0].App, roots[1].App);
            if (pcStartup) Assert.NotEqual(roots[0].Pc, roots[1].Pc);
            ConfigurationRunInventory.VerifyReadOnly(catalog.Instance, catalog.Database, catalog.Contracts);
            inputs.VerifyUnchanged();
        }
        finally { await installed.Activator.StopAllAsync(TimeSpan.FromSeconds(15)); }
    }
}
