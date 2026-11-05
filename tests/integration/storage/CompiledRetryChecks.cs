using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using Xunit;

namespace NexusPipeline.StorageTests;

internal static class CompiledRetryChecks
{
    internal static void Verify(ExtensionCatalogFixture catalog, ProjectSourceFixture project, AttemptWriterState state,
        AttemptReceipt receipt, byte[] live)
    {
        Assert.Equal(BusinessOutcome.Succeeded, receipt.Facts.Business);
        Assert.Equal(2, state.AllJobs.Count(job => job.Role == "process"));
        var invocation = Assert.Single(ExecutionInvocationCodec.Read(state.Latest.Root, state.Total));
        var retry = Assert.IsType<ExecutionTaskRetry>(invocation.TaskRetry);
        Assert.Equal(new[] { "fixture:taskB" }, retry.Selection.Targets.ToArray());
        Assert.Equal(new[] { "fixture:taskB" }, retry.Selection.SelectedIds.ToArray());
        var documents = File.ReadAllLines(Path.Combine(project.Path, "synthetic-worker-ready.tasks"));
        Assert.Equal(2, documents.Length);
        using var first = JsonDocument.Parse(documents[0]); using var second = JsonDocument.Parse(documents[1]);
        Assert.True(first.RootElement.GetProperty("taskA").GetBoolean());
        Assert.False(second.RootElement.GetProperty("taskA").GetBoolean());
        Assert.True(second.RootElement.GetProperty("taskB").GetBoolean());
        var result = Assert.Single(StepResultCodec.Read(state.Latest.Root, state.Total));
        var blobs = new ImmutableBlobStore(catalog.Instance, new());
        var evidence = StepEvidenceCodec.Read(blobs, result.Binding, result.Latest.Artifact);
        var observed = AdapterObservationCodec.Read(blobs, result.Binding, evidence.RawOutput!, state.Total.Steps.Single(), evidence.TaskAdapter!);
        Assert.All(observed.Observations, item => Assert.Equal(AdapterObservationState.Succeeded, item.State));
        Assert.Equal(AdapterCompletion.Satisfied, observed.Observations.Single(item => item.TaskId == "fixture:taskA").CompletionKind);
        Assert.Equal(AdapterObservationState.Unknown, observed.CurrentObservations!.Value.Single(item => item.TaskId == "fixture:taskA").State);
        Assert.Empty(observed.ProposedRetry);
        Assert.True(TaskRetryEvidence.Same(retry, observed.Retry));
        var retained = new StepEvidenceReader(catalog.Instance, catalog.Database, new()).ReadReferences(result.Binding, result.Latest.Artifact);
        Assert.Contains(retry.PreviousObservation, retained); Assert.Contains(retry.PreviousRaw, retained);
        RequireTamperRejection(catalog, state, observed, evidence, blobs);
        ConfigurationRunInventory.VerifyReadOnly(catalog.Instance, catalog.Database, new());
        var report = new ExecutionLogReader(catalog.Instance, catalog.Database, new()).ReadReport(receipt.Facts.RunId, receipt.Facts.AttemptId);
        Assert.All(Assert.Single(report.Steps).Tasks, task => Assert.Equal("succeeded", task.State));
        Assert.Equal("satisfied", Assert.Single(report.Steps).Tasks.Single(task => task.TaskId == "fixture:taskA").CompletionKind);
        var capacity = JournalCapacityPolicy.Measure(state.Total);
        Assert.All(new JournalStore(catalog.Instance, new()).ReadAllRevisions(state.Journal.JournalId),
            journal => Assert.True(journal.Export().LongLength <= capacity.MaximumDocumentBytes));
        Assert.Equal(live, File.ReadAllBytes(project.ConfigurationFile!));
    }

    private static void RequireTamperRejection(ExtensionCatalogFixture catalog, AttemptWriterState state,
        ExecutionAdapterObservation observed, StepEvidenceDocument evidence, ImmutableBlobStore blobs)
    {
        var step = Assert.Single(state.Total.Steps); var binding = observed.Binding; var retry = observed.Retry!;
        Assert.ThrowsAny<Exception>(() => AdapterObservationCodec.Write(blobs, binding, evidence.RawOutput!, step,
            observed.Observations, [], retry with { Selection = new(["fixture:taskA"], ["fixture:taskA"]) }, observed.CurrentObservations));
        Assert.ThrowsAny<Exception>(() => AdapterObservationCodec.Write(blobs, binding, evidence.RawOutput!, step,
            observed.Observations, [], retry with { PreviousBinding = retry.PreviousBinding with { PlanDigest = new string('a', 64) } }, observed.CurrentObservations));
        var forged = observed.Observations.SetItem(0, observed.Observations[0] with { State = AdapterObservationState.Failed });
        Assert.ThrowsAny<Exception>(() => AdapterObservationCodec.Write(blobs, binding, evidence.RawOutput!, step,
            forged, [], retry, observed.CurrentObservations));
        Assert.ThrowsAny<Exception>(() => AdapterObservationCodec.Write(blobs, binding, evidence.RawOutput!, step,
            observed.Observations, [], null, observed.CurrentObservations));
        Assert.ThrowsAny<Exception>(() => AdapterObservationCodec.Write(blobs, binding, evidence.RawOutput!, step,
            observed.Observations, [], retry, null));
        Assert.Throws<IOException>(() => TaskRetryEvidence.RequireInvocation(catalog.Instance, new(), state.Total,
            observed with { Retry = retry with { Selection = new(["fixture:taskB"], ["fixture:taskA", "fixture:taskB"]) } }));
        var invocation = Assert.Single(ExecutionInvocationCodec.Read(state.Latest.Root, state.Total));
        var child = invocation.ConfigurationPlans.Select(reference => new ConfigurationPlanCodec(catalog.Instance, new(), blobs)
            .ReadStructure(reference)).Single(plan => plan.RunState is { } reference &&
                new RunStateCodec(catalog.Instance, new(), blobs).ReadStructure(reference).Source.Address.ResourceId == "account");
        var saved = new RunStateCodec(catalog.Instance, new(), blobs).ReadStructure(child.RunState!);
        var captured = Assert.Single(saved.Tasks);
        saved = saved with { Tasks = [captured with { Rule = captured.Rule with { SelectedIds = ["fixture:taskA", "fixture:taskB"] } }] };
        Assert.Throws<IOException>(() => RunConfigurationBinding.Require(saved, step.StepId, child.TargetPath, state.Attempt,
            state.Total, blobs, new(), laterInvocation: true, taskRetry: retry));
    }
}
