using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Platform.Extensions;
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
        string[] expectedTasks = ["fixture:taskA", "fixture:taskB"];
        Assert.Equal(expectedTasks, observed.Observations.Select(item => item.TaskId).Order(StringComparer.Ordinal));
        Assert.Equal(expectedTasks, observed.CurrentObservations!.Value.Select(item => item.TaskId).Order(StringComparer.Ordinal));
        Assert.All(observed.Observations, item => Assert.Equal(AdapterObservationState.Succeeded, item.State));
        Assert.Equal(AdapterCompletion.Satisfied, observed.Observations.Single(item => item.TaskId == "fixture:taskA").CompletionKind);
        Assert.Equal(AdapterObservationState.Unknown, observed.CurrentObservations!.Value.Single(item => item.TaskId == "fixture:taskA").State);
        Assert.Empty(observed.ProposedRetry);
        Assert.True(TaskRetryEvidence.Same(retry, observed.Retry));
        BoundsTerminalCatalogReads(catalog, state, receipt);
        ObservesReleasedTerminal(catalog, state, observed);
        RequireProtectedObservation(catalog, state, evidence, blobs);
        var retained = new StepEvidenceReader(catalog.Instance, catalog.Database, new()).ReadReferences(result.Binding, result.Latest.Artifact);
        Assert.Contains(retry.PreviousObservation, retained); Assert.Contains(retry.PreviousRaw, retained);
        RequireLinearReferences(catalog, state, observed, blobs);
        RequireTamperRejection(catalog, state, observed, evidence, blobs);
        ConfigurationRunInventory.VerifyReadOnly(catalog.Instance, catalog.Database, new());
        var report = new ExecutionLogReader(catalog.Instance, catalog.Database, new()).ReadReport(receipt.Facts.RunId, receipt.Facts.AttemptId);
        var tasks = Assert.Single(report.Steps).Tasks;
        Assert.Equal(expectedTasks, tasks.Select(task => task.TaskId).Order(StringComparer.Ordinal));
        Assert.All(tasks, task => Assert.Equal("succeeded", task.State));
        Assert.Equal("satisfied", tasks.Single(task => task.TaskId == "fixture:taskA").CompletionKind);
        var capacity = JournalCapacityPolicy.Measure(state.Total);
        Assert.All(new JournalStore(catalog.Instance, new()).ReadAllRevisions(state.Journal.JournalId),
            journal => Assert.True(journal.Export().LongLength <= capacity.MaximumDocumentBytes));
        Assert.Equal(live, File.ReadAllBytes(project.ConfigurationFile!));
    }

    private static void BoundsTerminalCatalogReads(ExtensionCatalogFixture catalog, AttemptWriterState state, AttemptReceipt receipt)
    {
        var instance = catalog.Instance;
        var database = catalog.Database;
        var contracts = new NexusPipeline.Platform.Serialization.ContractReader();
        var reader = new ExecutionLogReader(instance, database, contracts);
        _ = reader.CaptureCatalog(receipt, materialize: true);
        var historyBytes = (ulong)state.RequireValidatedRead().History.Sum(item => (long)item.ByteLength);
        var before = ReadBytes();
        var cold = reader.CaptureCatalog(receipt);
        var coldBytes = ReadBytes() - before;
        using var history = JournalReadScope.HoldAttempt(instance, contracts, database, state.Journal.JournalId);
        before = ReadBytes();
        var held = reader.CaptureCatalog(receipt);
        var heldBytes = ReadBytes() - before;
        Assert.Equal(cold.Entries.ToArray(), held.Entries.ToArray());
        Assert.True(coldBytes <= heldBytes + historyBytes + 32768,
            $"Terminal catalog reread history: cold={coldBytes}, held={heldBytes}, history={historyBytes}");
    }

    private static ulong ReadBytes()
    {
        if (!GetProcessIoCounters(new IntPtr(-1), out var counters)) throw new IOException("native_process_io_unavailable");
        return counters.ReadBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

    private static void ObservesReleasedTerminal(ExtensionCatalogFixture catalog, AttemptWriterState state, ExecutionAdapterObservation observed)
    {
        var instance = catalog.Instance;
        var database = catalog.Database;
        var contracts = new NexusPipeline.Platform.Serialization.ContractReader();
        var journal = state.Journal.JournalId;
        using var owner = catalog.Lease.BindAttemptReads(database).HoldTerminal(instance, database, contracts, journal)!;
        using var reader = JournalReadScope.HoldAttempt(instance, contracts, database, journal);
        Assert.NotNull(reader.ReadTerminal(state.Latest));
        Task.Run(owner.Dispose).GetAwaiter().GetResult();
        Assert.Null(reader.ReadTerminal(state.Latest));
        var path = Path.Combine(instance.RuntimeRoot, "journals", journal.ToString("D"), "0000000001.json");
        using var denied = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        denied.Lock(0, denied.Length);
        try
        {
            Assert.Throws<IOException>(() => TaskRetryEvidence.RequireInvocation(instance, contracts, state.Total,
                observed, reader.ReadTerminal(state.Latest)));
        }
        finally { denied.Unlock(0, denied.Length); }
    }

    private static void RequireProtectedObservation(ExtensionCatalogFixture catalog, AttemptWriterState state,
        StepEvidenceDocument evidence, ImmutableBlobStore blobs)
    {
        var reference = evidence.TaskAdapter!; var binding = evidence.Binding; var step = Assert.Single(state.Total.Steps);
        using var protection = blobs.OpenVerified(reference);
        var expected = AdapterObservationCodec.Read(blobs, binding, evidence.RawOutput!, step, reference);
        using var locked = new FileStream(Path.Combine(catalog.Instance.RuntimeRoot, reference.RelativePath),
            FileMode.Open, FileAccess.Read, FileShare.Read);
        locked.Lock(0, locked.Length);
        try
        {
            var actual = AdapterObservationCodec.Read(blobs, binding, evidence.RawOutput!, step, reference);
            Assert.Equal(expected.Observations.ToArray(), actual.Observations.ToArray());
            Assert.Throws<IOException>(() => AdapterObservationCodec.Read(blobs, binding with { AttemptId = Guid.NewGuid() },
                evidence.RawOutput!, step, reference));
        }
        finally { locked.Unlock(0, locked.Length); }
    }

    private static void RequireLinearReferences(ExtensionCatalogFixture catalog, AttemptWriterState state,
        ExecutionAdapterObservation observed, ImmutableBlobStore blobs)
    {
        var step = Assert.Single(state.Total.Steps); var original = observed.Retry!;
        var chain = new List<(ExecutionAdapterObservation Value, BlobReference Artifact, BlobReference Raw)>();
        using (BlobReadScope.Hold(catalog.Instance))
        {
            var previous = AdapterObservationCodec.Read(blobs, original.PreviousBinding, original.PreviousRaw, step, original.PreviousObservation);
            var reference = original.PreviousObservation; var raw = original.PreviousRaw;
            var tasks = step.Tasks.Select(TaskAdapterProtocol.ReadTask).ToImmutableArray();
            var failed = previous.Observations.Single(item => item.TaskId == "fixture:taskB");
            var fresh = observed.CurrentObservations!.Value.Select(item => item.TaskId == failed.TaskId ? failed : item).ToImmutableArray();
            for (var index = 0; index < 24; index++)
            {
                var binding = previous.Binding with { JobName = previous.Binding.JobName + "x", WriterId = Guid.NewGuid(), SessionId = previous.Binding.SessionId + 1 };
                var nextRaw = RawOutputCodec.Write(blobs, new(RawOutputCodec.Format, 1, binding, Guid.NewGuid(), true, 0, 0, 0, 1, 0, 2, null, []));
                var selection = AdapterRetryPolicy.Select(tasks, previous.Observations, previous.ProposedRetry);
                var retry = original with { PreviousBinding = previous.Binding, PreviousRaw = raw, PreviousObservation = reference, Selection = selection };
                var next = AdapterObservationCodec.Write(blobs, binding, nextRaw, step,
                    AdapterRetryPolicy.Merge(tasks, previous.Observations, selection, fresh), previous.ProposedRetry, retry, fresh);
                previous = AdapterObservationCodec.Read(blobs, binding, nextRaw, step, next);
                reference = next; raw = nextRaw; chain.Add((previous, next, nextRaw));
            }
            long Allocated(int index)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                var references = new List<BlobReference>(); var current = chain[index];
                _ = AdapterObservationCodec.Read(blobs, current.Value.Binding, current.Raw, step, current.Artifact,
                    references: references);
                Assert.Contains(original.PreviousObservation, references);
                return GC.GetAllocatedBytesForCurrentThread() - before;
            }
            _ = Allocated(11); _ = Allocated(23);
            var shorter = Allocated(11); var longer = Allocated(23);
            Assert.True(longer <= shorter * 2.75, $"Retry reference allocation grew superlinearly: 12={shorter}, 24={longer}");
        }
        var last = chain[^1];
        foreach (var reference in new[] { chain[11].Artifact, chain[11].Raw })
        {
            var path = Path.Combine(catalog.Instance.RuntimeRoot, reference.RelativePath); var bytes = File.ReadAllBytes(path);
            try
            {
                File.WriteAllBytes(path, [1]);
                Assert.Throws<IOException>(() => AdapterObservationCodec.Read(blobs, last.Value.Binding, last.Raw, step, last.Artifact));
            }
            finally { File.WriteAllBytes(path, bytes); }
        }
        Assert.Equal(last.Value.Observations.ToArray(), AdapterObservationCodec.Read(blobs, last.Value.Binding, last.Raw, step, last.Artifact).Observations.ToArray());
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
        var changedStep = step with { Tasks = step.Tasks.AsEnumerable().Reverse().ToArray() };
        Assert.Throws<IOException>(() => AdapterObservationCodec.Read(blobs, binding, evidence.RawOutput!, changedStep, evidence.TaskAdapter!));
        using (var previous = blobs.OpenVerified(retry.PreviousObservation))
        {
            var changed = JsonNode.Parse(previous)!.AsObject();
            changed["tasksDigest"] = new string('0', 64);
            var changedReference = blobs.PersistPlan(JsonSerializer.SerializeToUtf8Bytes(changed));
            Assert.Throws<IOException>(() => AdapterObservationCodec.Write(blobs, binding, evidence.RawOutput!, step,
                observed.Observations, [], retry with { PreviousObservation = changedReference }, observed.CurrentObservations));
        }
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
