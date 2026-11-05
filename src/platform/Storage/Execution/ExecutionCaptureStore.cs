using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Media;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class ExecutionCaptureStore(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
    InstanceLease lease, ExecutionCaptureBinding binding, ExecutionTimeBudget? budget = null, ITargetPersistenceObserver? observer = null)
{
    private readonly ImmutableBlobStore _blobs = new(instance, contracts);
    private BlobReference? _sourceReference, _rootReference;
    private (AttemptWriterState Before, JournalDocument Appended)? _childAdmission;

    internal bool HasSource => _sourceReference is not null || lease.PublishExecution(() => Current(ReadFresh()) is not null);

    internal BlobReference Source(PcWindowFacts window, OwnedWorkerIdentity primary)
    {
        var reference = lease.PublishExecution(() =>
        {
            var state = ReadFresh();
            if (Current(state) is not null || window.Binding.StepId != binding.Primary.StepId) throw Invalid();
            RequireNewEffect(state, primary);
            return Publish(state, "source", new CaptureSourceDocument(ExecutionCaptureCodec.Format + "-source", 1,
                binding, window, state.Total.CaptureRuntime!.Broker, JournalWriterStore.CurrentWriter));
        });
        _sourceReference = reference;
        observer?.Reached(TargetPersistencePoint.CaptureSourcePublished, binding);
        return reference;
    }

    internal BlobReference Source(PreparedCaptureContext android, OwnedWorkerIdentity primary)
    {
        var reference = android.Use(facts => lease.PublishExecution(() =>
        {
            var state = ReadFresh();
            if (Current(state) is not null || android.Primary != binding.Primary) throw Invalid();
            RequireNewEffect(state, primary);
            var target = state.Total.Steps.Single(step => step.StepId == binding.Primary.StepId).Target?.Android ?? throw Invalid();
            var tool = target.Driver switch
            {
                "genericAdb" => target.Tool,
                "mumu" => state.Total.CaptureRuntime?.Broker ?? throw Invalid(),
                _ => throw Invalid()
            };
            return Publish(state, "source", new CaptureSourceDocument(ExecutionCaptureCodec.Format + "-source", 1,
                binding, null, tool, JournalWriterStore.CurrentWriter) { Android = facts });
        }));
        _sourceReference = reference;
        observer?.Reached(TargetPersistencePoint.CaptureSourcePublished, binding);
        return reference;
    }

    internal void Root(OwnedWorkerJob helper, OwnedWorkerIdentity native, OwnedWorkerIdentity primary)
    {
        _ = lease.PublishExecution(() =>
        {
            var state = ReadFresh(); var current = Current(state) ?? throw Invalid();
            if (current.Root is not null || current.Result is not null || helper.Name != binding.HelperJobName ||
                native.InstanceId != instance.InstanceId || native.AttemptId != binding.Primary.AttemptId) throw Invalid();
            var step = state.Total.Steps.Single(value => value.StepId == binding.Primary.StepId);
            var root = Identity(native); var broker = step.Target?.Android is { Driver: "genericAdb" } android
                ? android.Tool : state.Total.CaptureRuntime!.Broker;
            using var process = System.Diagnostics.Process.GetProcessById(native.ProcessId);
            if (!helper.ContainsProcessHandle(process.SafeHandle) || ProcessIdentityReader.Read(native.ProcessId) != root ||
                helper.GetAccounting() is not { ActiveProcesses: 1, TotalProcesses: 1 } ||
                !StringComparer.OrdinalIgnoreCase.Equals(root.ImagePath, broker.Path) || root.ImageSha256 != broker.Sha256)
                throw Invalid();
            RequireNewEffect(state, primary);
            _rootReference = Publish(state, "root", new CaptureRootDocument(ExecutionCaptureCodec.Format + "-root", 1,
                binding, current.Source, root));
            return true;
        });
        observer?.Reached(TargetPersistencePoint.CaptureRootPublished, binding);
    }

    internal (BlobReference Source, BlobReference Root) MuMuContext() =>
        _sourceReference is not null && _rootReference is not null ? (_sourceReference, _rootReference) : lease.PublishExecution(() =>
    {
        var current = Current(ReadFresh()) ?? throw Invalid();
        if (current.Root is null || current.Result is not null) throw Invalid();
        return (current.Source, current.Root);
    });

    internal BlobReference ChildRoot(OwnedWorkerJob helper, CaptureChildLease child, OwnedWorkerIdentity primary)
    {
        var reference = lease.PublishExecution(() =>
        {
            var state = ReadFresh(); var prior = ReadMetadata(state).Single(value => value.State.Binding == binding);
            if (child.Binding != binding || helper.Name != binding.HelperJobName || prior.State.Root is null ||
                prior.State.Result is not null || state.Jobs.Any(job => job.JobName == child.JobName)) throw Invalid();
            child.RequireAwaitingMember(helper);
            RequireNewEffect(state, primary);
            new MuMuCaptureEvidence(_blobs).RequirePredecessor(prior, child.Sequence);
            return Publish(state, MuMuCaptureSequence.Stage(child.Sequence, observed: false),
                new CaptureCommandRoot(ExecutionCaptureCodec.Format + "-command-root", 1, binding,
                    prior.State.Source, prior.State.Root, child.Sequence, MuMuCaptureSequence.Purpose(child.Sequence),
                    child.JobName, child.ProcessIdentity));
        });
        observer?.Reached(TargetPersistencePoint.CaptureCommandRootPublished, binding);
        return reference;
    }

    internal BlobReference ChildObserved(OwnedWorkerJob helper, MuMuCaptureClient.Observed evidence)
    {
        var reference = lease.PublishExecution(() =>
        {
            var state = ReadFresh(); var prior = ReadMetadata(state).Single(value => value.State.Binding == binding);
            if (prior.State.Result is not null || prior.Root is null || prior.Commands.IsEmpty) throw Invalid();
            return evidence.Use(helper, binding, prior.State.Source, prior.State.Root!, prior.Root.NativeRoot, observed =>
            {
                var command = prior.Commands[^1];
                if (observed.CommandRoot != command.RootArtifact || command.Observation is not null) throw Invalid();
                return Publish(state, MuMuCaptureSequence.Stage(command.Root.Sequence, observed: true), observed);
            });
        });
        observer?.Reached(TargetPersistencePoint.CaptureCommandObservedPublished, binding);
        return reference;
    }

    internal Task AdmitChildWrite(OwnedWorkerJob helper, CaptureChildLease child, OwnedWorkerIdentity primary,
        Func<CancellationToken, Task> write, TimeSpan remaining, CancellationToken cancellation)
    {
        if (remaining <= TimeSpan.Zero || remaining > TimeSpan.FromSeconds(5)) throw Invalid();
        lease.PublishExecution(() =>
        {
            var (state, prior) = ReadChildAdmission(); RequireNewEffect(state, primary);
            if (helper.Name != binding.HelperJobName || child.Binding != binding || prior.State.Result is not null ||
                prior.Commands.IsEmpty || prior.Commands[^1].Root.Sequence != child.Sequence ||
                prior.Commands[^1].Root.Process != child.ProcessIdentity || prior.Commands[^1].Observation is not null) throw Invalid();
            child.RequireAwaitingMember(helper);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(remaining);
            deadline.Token.ThrowIfCancellationRequested();
            // Stop belongs to the capture owner after this queue and the caller's Job gates have unwound.
            write(deadline.Token).WaitAsync(deadline.Token).GetAwaiter().GetResult();
        });
        return Task.CompletedTask;
    }

    internal BlobReference Collected(OwnedWorkerIdentity? root, OwnedTargetInventory stopped, BlobReference? png,
        string reason, IExecutionCaptureProcess owner, NexusPipeline.Platform.Media.CapturePngInfo? image = null,
        IReadOnlyList<CaptureFrameRecord>? frames = null) => lease.PublishExecution(() =>
    {
        frames ??= [];
        var state = ReadFresh(); var current = Current(state) ?? throw Invalid();
        if (current.Result is not null) return current.Result;
        var metadata = ReadMetadata(state).Single(value => value.State.Binding == binding);
        if (metadata.Root is not null && metadata.Root.NativeRoot != (root is null ? null : Identity(root))) throw Invalid();
        if (metadata.Root is null && (png is not null || frames.Count != 0) || png is not null && frames.Count != 0) throw Invalid();
        if (png is not null || frames.Count != 0)
        {
            if (budget is null || budget.Read().Remaining == TimeSpan.Zero)
            { png = null; frames = []; reason = "execution_capture_deadline"; }
        }
        if (png is not null || frames.Count != 0)
        {
            using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
            using var transaction = connection.BeginTransaction(deferred: true);
            if (ExecutionCancellationRows.Read(connection, transaction, state.Journal, state.Attempt) is not null)
            { png = null; frames = []; reason = "execution_capture_context_closed"; }
            else ExecutionCaptureProjection.RequireCapturedPublicationInside(connection, transaction, state, state.Latest, binding);
            transaction.Commit();
        }
        if (png is null && frames.Count == 0 && reason == "execution_capture_frame_pending") reason = "execution_capture_frame_rejected";
        var closure = new StepEvidenceReader(instance, database, contracts).ClosureReference(binding.Primary);
        var result = new CaptureResultDocument(ExecutionCaptureCodec.Format + "-result", 1, binding,
            current.Source, current.Root, png is null && frames.Count == 0 ? "unavailable" : "captured", reason, stopped, closure, png)
        { Image = png is null && frames.Count == 0 ? null : image, Frames = frames.Count == 0 ? null : frames.ToArray() };
        return Publish(state, "collected", result, owner);
    });

    internal void RecoverUnavailable() => lease.PublishExecution(() =>
    {
        var state = ReadFresh(); var current = Current(state) ?? throw Invalid();
        if (current.Result is not null) return true;
        if (!state.IsDispatchClosed || state.Gate.State != EffectGateState.Entered ||
            state.Latest.Phase is not (JournalPhase.Applying or JournalPhase.Quarantined)) throw Invalid();
        AttemptTerminalEvidence.RequireStopped(database, binding.Primary.ParentJournalId);
        var closure = new StepEvidenceReader(instance, database, contracts).ClosureReference(binding.Primary);
        _ = Publish(state, "collected", new CaptureResultDocument(ExecutionCaptureCodec.Format + "-result", 1,
            binding, current.Source, current.Root, "unavailable", "execution_capture_recovered_unavailable", null, closure, null));
        return true;
    });

    private BlobReference Publish<T>(AttemptWriterState state, string stage, T value, IExecutionCaptureProcess? owner = null)
    {
        using var paths = DirectoryAttributeScope.Hold(instance);
        var reference = _blobs.PersistPlan(BoundedJson.Serialize(value, StepResultCodec.Options));
        using var held = _blobs.OpenVerified(reference);
        var intent = new ExecutionCaptureIntent(ExecutionCaptureCodec.Format, 1, binding, stage, state.Latest.Revision, reference);
        var participant = ExecutionCaptureCodec.Participant(intent, state.Total.Steps.Single(step => step.StepId == binding.Primary.StepId).ScriptId);
        var node = JsonNode.Parse(state.Latest.Export())!.AsObject();
        node["revision"] = state.Latest.Revision + 1;
        node["updatedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'");
        node["participants"]!.AsArray().Add(participant);
        var next = JournalDocument.Read(JsonSerializer.SerializeToUtf8Bytes(node), contracts);
        var history = state.RequireValidatedRead().History.Append(next).ToArray();
        var reader = new CaptureEvidenceReader(instance, database, contracts);
        _ = reader.ReadMetadata(state.Journal, state.Attempt, state.Total, history);
        using var collection = stage == "collected" ? reader.HoldCollectedForPublication(
            state.Journal, state.Attempt, state.Total, history, binding, reference) : null;
        if (stage == "collected" && value is CaptureResultDocument { Outcome: "captured" } captured)
        {
            try { database.Write((connection, transaction) =>
            {
                (owner ?? throw Invalid()).RequireFramePublication();
                ExecutionCaptureProjection.RequireCapturedPublicationInside(connection, transaction, state, state.Latest, binding); return true;
            }); }
            catch (IOException error) when (error.Message == "execution_capture_deadline")
            {
                return Publish(state, stage, captured with
                {
                    Outcome = "unavailable", ReasonCode = "execution_capture_deadline", Png = null, Image = null, Frames = null
                }, owner);
            }
        }
        new JournalStore(instance, contracts).Append(state.Journal.JournalId, state.Latest.Revision, next.Export());
        if (stage == "collected") observer?.Reached(TargetPersistencePoint.CaptureCollectedAppended, binding);
        ExecutionAttemptProjection.SynchronizeCaptureAppend(instance, database, contracts, state, next);
        if (MuMuCaptureSequence.IsRoot(stage)) _childAdmission = (state, next);
        return reference;
    }

    private (AttemptWriterState State, CaptureSavedEvidence Capture) ReadChildAdmission()
    {
        using var paths = DirectoryAttributeScope.Hold(instance);
        lease.VerifyHeld(instance);
        var pending = _childAdmission ?? throw Invalid(); _childAdmission = null;
        var history = new JournalStore(instance, contracts).ReadAllRevisions(binding.Primary.ParentJournalId);
        var previous = pending.Before.RequireValidatedRead().History;
        if (history.Count != previous.Length + 1 || !history[^1].HasSameBytes(pending.Appended)) throw Invalid();
        for (var index = 0; index < previous.Length; index++)
            if (!history[index].HasSameBytes(previous[index])) throw Invalid();
        var state = pending.Before with
        {
            Journal = pending.Before.Journal with { Revision = pending.Appended.Revision },
            Latest = pending.Appended
        };
        AttemptWriterState.VerifySql(database, state.Journal, state.Latest, state.Total, state.Attempt.ExecutionPlan.Sha256, state.Gate);
        // Recheck current capture artifacts and the exact prefix; admission separately checks SQL cancellation and the suspended native child.
        var capture = new CaptureEvidenceReader(instance, database, contracts)
            .ReadMetadata(state.Journal, state.Attempt, state.Total, history).Single(value => value.State.Binding == binding);
        return (state, capture);
    }

    private void RequireNewEffect(AttemptWriterState state, OwnedWorkerIdentity primary) => database.Write((connection, transaction) =>
    {
        if (budget is null || budget.Read().Remaining == TimeSpan.Zero) throw new IOException("execution_capture_deadline");
        ExecutionCaptureProjection.RequireNewEffectInside(connection, transaction, state, state.Latest, binding);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT process_id,created_file_time,image_path,image_sha256,user_sid,elevated FROM journal_writer_job_roots " +
            "WHERE journal_id=$journal AND writer_id=$writer AND job_name=$job;";
        command.Parameters.AddWithValue("$journal", binding.Primary.ParentJournalId.ToString("D"));
        command.Parameters.AddWithValue("$writer", binding.Primary.WriterId.ToString("D"));
        command.Parameters.AddWithValue("$job", binding.Primary.JobName);
        using var row = command.ExecuteReader();
        if (primary.InstanceId != instance.InstanceId || primary.AttemptId != binding.Primary.AttemptId || !row.Read() ||
            Identity(primary) != new ProcessIdentity(row.GetInt32(0), DateTime.FromFileTimeUtc(row.GetInt64(1)),
                row.GetString(2), row.GetString(3), row.GetString(4), row.GetBoolean(5))) throw Invalid();
        return true;
    });

    private AttemptWriterState ReadFresh()
    {
        lease.VerifyHeld(instance);
        var state = AttemptWriterState.Read(instance, database, contracts, binding.Primary.ParentJournalId);
        if (state.Latest.Revision != state.Journal.Revision)
        { ExecutionAttemptProjection.SynchronizeFromValidatedRead(instance, database, contracts, state); state = AttemptWriterState.Read(instance, database, contracts, state.Journal.JournalId); }
        return state;
    }

    private ExecutionCaptureState? Current(AttemptWriterState state) =>
        ExecutionCaptureCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)
            .SingleOrDefault(value => value.Binding == binding);
    private System.Collections.Immutable.ImmutableArray<CaptureSavedEvidence> ReadMetadata(AttemptWriterState state) =>
        state.RequireValidatedRead().Captures;
    private static ProcessIdentity Identity(OwnedWorkerIdentity value) =>
        new(value.ProcessId, value.CreatedAtUtc, value.ImagePath, value.ImageSha256, value.UserSid, value.IsElevated);
    private static IOException Invalid() => new("execution_capture_owner_unbound");
}
