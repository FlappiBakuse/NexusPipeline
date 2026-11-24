using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class RetryBettergiTests
{
    [Fact(DisplayName = "The Actual Installed BetterGI Shares Its Manifest Across Three Bound Accounts And Checks Their Configuration")]
    public async Task UsesSharedAdapterManifest()
    {
        await using var fixture = new RetryBettergiFixture();
        await fixture.InstallAsync();
        var source = fixture.Source;
        var before = source.Definitions.Read();
        var added = new[] { new AutomationBinding(Guid.NewGuid(), 0, source.UserA, source.OtherScriptId, true, 0),
            new AutomationBinding(Guid.NewGuid(), 0, source.UserC, source.OtherScriptId, true, 0) };
        var changes = new AutomationChanges(before.Revision, [], [], [], [.. added],
            [before.Queues.Single() with { ScriptIds = [source.OtherScriptId], CompletionAction = AutomationCompletionAction.None }], [], []);
        source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "bettergi-shared-manifest", changes),
            changes, DateTimeOffset.UtcNow);
        var definitions = source.Definitions.Read();
        var script = definitions.Scripts.Single(item => item.Id == source.OtherScriptId);
        var resource = Assert.Single(script.AdapterResources);
        var contracts = new ContractReader();
        var blobs = new ImmutableBlobStore(source.Instance, contracts);
        var tree = new FileTreeArchive(new(SnapshotBudget.Default), blobs).Capture(fixture.Configuration, default).Snapshot;
        var revisions = new ConfigurationRevisionStore(source.Instance, source.Database, contracts);
        foreach (var item in added)
        {
            var binding = definitions.Bindings.Single(value => value.Id == item.Id);
            var saved = new ConfigurationRevisionChange(new(binding.Id, resource.ResourceId), 0, binding.Revision,
                AdapterConfigurationPolicy.Contract(script, resource), tree, ConfigurationUpdateKind.Manual);
            revisions.Commit(ConfigurationRevisionStore.Identity(Guid.NewGuid(), "bettergi-shared-manifest", saved), saved, DateTimeOffset.UtcNow);
        }

        var request = source.Request(new QueueTarget(source.QueueId));
        var counts = fixture.Execution.Counts();
        using var draft = fixture.Service.DryRun(request, DateTimeOffset.UtcNow);
        var preview = Assert.IsType<FrozenExecutionPlan>(draft.ReadPlan()).ReadWire();
        Assert.Equal(3, preview.Steps.Count);
        Assert.Equal(3, preview.Steps.Select(step => step.BindingId).Distinct().Count());
        Assert.All(preview.Steps, step => Assert.NotNull(step.Adapter));
        var manifest = Assert.Single(preview.Steps.Select(step => step.Adapter!.Manifest).Distinct());
        Assert.Equal(counts, fixture.Execution.Counts());
        var operation = Guid.NewGuid();
        _ = fixture.Service.Start(operation, "bettergi-shared-manifest", request, DateTimeOffset.UtcNow);
        using var accepted = fixture.Execution.Take(operation);
        var codec = new ExecutionPlanCodec(source.Instance, contracts, blobs);
        var stored = codec.Read(accepted.Prepared.Total.Reference).ReadWire();
        Assert.Equal(preview.Steps.Select(step => step.BindingId), stored.Steps.Select(step => step.BindingId));
        Assert.All(stored.Steps, step => Assert.Equal(manifest, step.Adapter!.Manifest));
        var end = stored.Steps[^1];
        var savedAdapter = Assert.IsType<NexusPipeline.Platform.Serialization.Protocol.ExecutionAdapter>(end.Adapter);
        var detached = Assert.Single(savedAdapter.Resources);
        var corrupted = end with { Adapter = savedAdapter with { Resources = [detached with { Revision = detached.Revision + 1 }] } };
        var invalid = stored with { Steps = [stored.Steps[0], stored.Steps[1], corrupted] };
        Assert.Equal("execution_adapter_frozen_source_unbound",
            Assert.Throws<IOException>(() => codec.Freeze(invalid).Dispose()).Message);
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.Configuration));
        fixture.Inputs.VerifyUnchanged();
    }

    [Fact(DisplayName = "Original BetterGI Package Restarts After Host Closes Failed Process Family With Only Failed Task Selected And Original Configuration Restored")]
    public async Task RetriesFailedTask()
    {
        await using var fixture = new RetryBettergiFixture();
        await fixture.InstallAsync();
        var accepted = fixture.Service.Start(Guid.NewGuid(), "bettergi-restart",
            fixture.Source.Request(new BindingTarget(fixture.Source.OtherBinding)), DateTimeOffset.UtcNow);
        var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        Assert.Equal(RecoveryOutcome.Restored, receipt.Facts.Recovery);
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.Configuration));
        var attempts = File.ReadAllLines(fixture.Log + ".attempts");
        Assert.Equal(2, attempts.Length);
        Assert.Equal(new[] { "mail", "resin" }, JsonSerializer.Deserialize<string[]>(attempts[0]));
        Assert.Equal(new[] { "resin" }, JsonSerializer.Deserialize<string[]>(attempts[1]));
        Assert.True(File.Exists(fixture.Log + ".restart-verified"));
        var state = AttemptWriterState.Read(fixture.Source.Instance, fixture.Source.Database, new(), accepted.JournalId);
        var invocation = Assert.Single(ExecutionInvocationCodec.Read(state.Latest.Root, state.Total));
        Assert.Equal(new[] { "bettergi:resin" }, invocation.TaskRetry!.Selection.Targets);
        Assert.Equal(new[] { "bettergi:resin" }, invocation.TaskRetry.Selection.SelectedIds);
        var result = Assert.Single(StepResultCodec.Read(state.Latest.Root, state.Total));
        var blobs = new ImmutableBlobStore(fixture.Source.Instance, new());
        var evidence = StepEvidenceCodec.Read(blobs, result.Binding, result.Latest.Artifact);
        var observed = AdapterObservationCodec.Read(blobs, result.Binding, evidence.RawOutput!, Assert.Single(state.Total.Steps), evidence.TaskAdapter!);
        Assert.All(observed.Observations.Where(item => item.TaskId is "bettergi:mail" or "bettergi:resin"),
            item => Assert.Equal(AdapterObservationState.Succeeded, item.State));
        ConfigurationRunInventory.VerifyReadOnly(fixture.Source.Instance, fixture.Source.Database, new());
        fixture.Inputs.VerifyUnchanged();
    }
}
