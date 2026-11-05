using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Platform.Storage.Maintenance;

[SupportedOSPlatform("windows")]
internal sealed class MaintenanceEffectSession
{
    private readonly MaintenanceHandoffInputs _inputs;
    private readonly MaintenanceRegisteredActor _actor;
    private InstanceLease _lease;
    private readonly JournalWriterStore _writers;
    private int _attempted;
    private bool _applied;

    private MaintenanceEffectSession(MaintenanceHandoffInputs inputs,
        MaintenanceRegisteredActor actor, InstanceLease lease)
    { _inputs = inputs; _actor = actor; _lease = lease; _writers = new(inputs.Database); }

    internal static MaintenanceEffectSession Prepare(MaintenanceHandoffInputs inputs,
        MaintenanceRegisteredActor actor, InstanceLease lease)
    {
        var result = new MaintenanceEffectSession(inputs, actor, lease);
        result.RequireCurrent();
        var registered = new RecoveryJournalRegistry(inputs.Database).ReadJournal(inputs.Plan.JournalId);
        var gate = new JournalEffectGate(inputs.Database).Read(inputs.Plan.JournalId);
        if (registered is not { Phase: JournalPhase.Prepared, Revision: 1 } ||
            gate is not { State: EffectGateState.Unentered, Revision: 1 }) throw Invalid();
        using var before = TreeReadLease.AcquireApplication(inputs.Plan.TargetPath, inputs.Plan.ParentIdentity, inputs.Plan.Before);
        return result;
    }

    internal MaintenancePhysicalRecord Apply(CancellationToken acceptedCancellation,
        IPayloadPersistenceObserver? observer = null)
    {
        if (Interlocked.Exchange(ref _attempted, 1) != 0) throw Invalid();
        using var paths = DirectoryAttributeScope.Hold(_inputs.Instance);
        RequireCurrent();
        try
        {
            var result = new MaintenancePhysicalJournal(_inputs, _actor, _lease, RequireCurrent, observer).Apply(acceptedCancellation);
            _applied = true;
            return result;
        }
        catch (Exception error)
        {
            var gate = new JournalEffectGate(_inputs.Database).Read(_inputs.Plan.JournalId);
            if (gate.State != EffectGateState.Entered) throw;
            var failure = FailedEffect.Capture(this);
            throw new MaintenanceApplyException(error, failure);
        }
    }

    internal FailedEffect FailStoppedStartup(InstanceLease lease, MaintenanceStartupJob actualJob, OwnedWorkerIdentity actualRoot)
    {
        var stopped = actualJob.RequireStoppedRoot(actualRoot);
        lease.VerifyHeld(_inputs.Instance); _actor.RequireCurrent(lease);
        if (!_applied || actualRoot.InstanceId != _inputs.Instance.InstanceId || actualRoot.AttemptId != _inputs.Operation.HandoffId) throw Invalid();
        using var connection = DatabaseConnections.Open(_inputs.Database.DatabasePath, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM journal_writers w JOIN journal_writer_jobs b USING(journal_id,writer_id) " +
            "WHERE w.journal_id=$journal AND b.job_name=$job AND w.process_id=$pid AND w.created_file_time=$created AND w.image_sha256=$hash);";
        command.Parameters.AddWithValue("$journal", _inputs.Plan.JournalId.ToString("D")); command.Parameters.AddWithValue("$job", actualJob.Name);
        command.Parameters.AddWithValue("$pid", stopped.ProcessId); command.Parameters.AddWithValue("$created", stopped.CreatedAtUtc.ToFileTimeUtc());
        command.Parameters.AddWithValue("$hash", stopped.ImageSha256);
        if ((long)command.ExecuteScalar()! != 1) throw Invalid();
        _lease = lease;
        RequireCurrent();
        return FailedEffect.Capture(this);
    }

    private void RequireCurrent()
    {
        _actor.RequireCurrent(_lease);
        if (!_writers.AreOthersStopped(_actor, _lease, _inputs.Plan.JournalId)) throw Invalid();
        _inputs.RequireCurrent();
        new MaintenanceInstallation(_inputs.Instance, _inputs.Contracts).RequireOriginal(_inputs.Source, _inputs.Plan, _inputs.Manifest.Profile);
    }

    private static IOException Invalid() => new("maintenance_payload_effect_not_prepared");

    internal sealed class FailedEffect
    {
        private readonly MaintenanceEffectSession _session;
        internal HelperFailureFacts Facts { get; }
        internal Guid JournalId => _session._inputs.Plan.JournalId;
        internal BlobReference Plan => _session._inputs.Operation.Plan;
        private FailedEffect(MaintenanceEffectSession session)
        {
            _session = session;
            Facts = new(session._inputs.Operation.HandoffId!.Value, session._actor.Writer, session._actor.JobName);
        }
        internal static FailedEffect Capture(MaintenanceEffectSession session)
        {
            if (session._attempted != 1) throw Invalid();
            session.RequireCurrent();
            return new(session);
        }
        internal void Require(RuntimeInstance instance, StateDatabase database, InstanceLease lease, Guid journal)
        {
            if (!ReferenceEquals(database, _session._inputs.Database) || !ReferenceEquals(lease, _session._lease) ||
                instance != _session._inputs.Instance || journal != JournalId ||
                Facts.Writer != _session._actor.Writer || Facts.JobName != _session._actor.JobName ||
                Facts.HandoffId != _session._inputs.Operation.HandoffId) throw Invalid();
            _session.RequireCurrent();
            if (new JournalEffectGate(database).Read(journal).State != EffectGateState.Entered) throw Invalid();
        }
    }
}

internal sealed record HelperFailureFacts(Guid HandoffId, JournalWriter Writer, string JobName);
[SupportedOSPlatform("windows")]
internal sealed class MaintenanceApplyException(Exception cause, MaintenanceEffectSession.FailedEffect failure)
    : IOException("maintenance_payload_apply_requires_restoration", cause)
{
    internal MaintenanceEffectSession.FailedEffect Failure { get; } = failure;
}
