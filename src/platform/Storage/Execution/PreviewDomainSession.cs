using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class PreviewDomainSession
{
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly ContractReader _contracts;
    private readonly InstanceLease _lease;
    private readonly ResourceOperationBorrow _borrow;
    private readonly PreviewDomainSource _source;
    private bool _released;
    internal OwnedPreviewJob Job { get; }

    internal PreviewDomainSession(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        InstanceLease lease, AcceptedExecutionAdmission accepted, ResourceLeaseManager resources,
        StepEvidenceBinding binding, string totalSha256, OwnedWorkerIdentity primary)
    {
        if (!accepted.Root.IsActive || primary.InstanceId != binding.InstanceId || primary.AttemptId != binding.AttemptId ||
            accepted.Root.OperationId != accepted.Identity.OperationId) throw PreviewDomainCodec.Invalid();
        _instance = instance; _database = database; _contracts = contracts; _lease = lease;
        _borrow = accepted.Root.Borrow(resources, accepted.Root.OperationId, accepted.Root.Claims);
        _source = new(binding, totalSha256, PreviewDomainCodec.Name(binding, totalSha256),
            JournalWriterStore.CurrentWriter.Process, new(primary.ProcessId, primary.CreatedAtUtc, primary.ImagePath,
                primary.ImageSha256, primary.UserSid, primary.IsElevated), OwnedPreviewJob.LimitFlags,
            OwnedPreviewJob.MaximumActiveProcesses, OwnedPreviewJob.MaximumMemoryBytes);
        try { Job = new(_source.JobName); }
        catch { _borrow.Dispose(); throw; }
    }

    internal void Open() => _lease.PublishExecution(() => _borrow.Use(() =>
    {
        var state = ReadFresh();
        if (Current(state) is not null) throw PreviewDomainCodec.Invalid();
        _ = new JournalWriterStore(_database).RequireRegisteredExecutor(state.Journal.JournalId);
        Append(state, "open", null, null); return true;
    }));

    internal void RequireOpen() => _lease.PublishExecution(() => _borrow.Use(() =>
    {
        var state = ReadFresh(); var current = Current(state);
        if (current is null || current.Closed || current.Source != _source || state.IsDispatchClosed) throw PreviewDomainCodec.Invalid();
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        PreviewDomainEvidence.RequireSourceInside(connection, transaction, _source); transaction.Commit(); return true;
    }));

    internal Task<PreviewJobStop> StopAsync(TimeSpan timeout) => Job.StopAsync(timeout);

    internal void Close() => _lease.PublishExecution(() => _borrow.Use(() =>
    {
        var stopped = Job.RequireStopped(); var proof = PreparedPreviewClosure.FromStoppedOwner(Job, _source);
        var state = ReadFresh(proof); var current = Current(state);
        if (current is not null && current.Source != _source) throw PreviewDomainCodec.Invalid();
        if (current is { Closed: false }) Append(state, "closed", stopped.TotalProcesses,
            proof);
        // A failed durable open may leave no participant; its never-resumed empty native job still needs collection.
        Job.Dispose(); _released = true; return true;
    }));

    internal void ReleaseBorrow()
    {
        if (!_released) throw new IOException("execution_preview_domain_collection_pending");
        _borrow.Dispose();
    }

    private void Append(AttemptWriterState state, string stage, uint? total, PreparedPreviewClosure? closure)
    {
        var item = new PreviewDomainIntent(PreviewDomainCodec.Format, 1, _source, stage, state.Latest.Revision, total);
        Append(_instance, _database, _contracts, state, item, closure);
    }

    internal static void Append(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        AttemptWriterState state, PreviewDomainIntent item, PreparedPreviewClosure? closure)
    {
        var node = JsonNode.Parse(state.Latest.Export())!.AsObject(); node["revision"] = state.Latest.Revision + 1;
        node["updatedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'");
        node["participants"]!.AsArray().Add(PreviewDomainCodec.Participant(item,
            state.Total.Steps.Single(step => step.StepId == item.Source.Primary.StepId).ScriptId));
        new JournalStore(instance, contracts).Append(state.Journal.JournalId, state.Latest.Revision, JsonSerializer.SerializeToUtf8Bytes(node));
        ExecutionAttemptProjection.Synchronize(instance, database, contracts,
            AttemptWriterState.Read(instance, database, contracts, state.Journal.JournalId), previewClosure: closure);
    }

    private PreviewDomainState? Current(AttemptWriterState state) =>
        PreviewDomainCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)
            .SingleOrDefault(item => item.Source.Primary.StepId == _source.Primary.StepId);

    private AttemptWriterState ReadFresh(PreparedPreviewClosure? closure = null)
    {
        _lease.VerifyHeld(_instance);
        var state = AttemptWriterState.Read(_instance, _database, _contracts, _source.Primary.ParentJournalId);
        if (state.Latest.Revision != state.Journal.Revision)
        {
            ExecutionAttemptProjection.Synchronize(_instance, _database, _contracts, state, previewClosure: closure);
            state = AttemptWriterState.Read(_instance, _database, _contracts, _source.Primary.ParentJournalId);
        }
        return state;
    }
}
