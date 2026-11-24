using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;
using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Text.Json;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class ProviderLauncherStore : IDisposable
{
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly ContractReader _contracts;
    private readonly InstanceLease _lease;
    private readonly ResourceLeaseManager _resources;
    private readonly ITargetPersistenceObserver? _observer;
    private readonly ResourceOperationBorrow _root;
    private readonly ExecutionProviderRegistry.PreparedExecutionProvider _provider;
    private readonly Guid _journalId;
    private readonly Guid _launcherId;
    private readonly string _subject, _stepId;
    private readonly ImmutableArray<ResourceClaim> _references;
    private RetainedResourceReferences? _passive;
    private IDisposable? _package;
    private bool _transferred, _disposed;

    internal ProviderLauncherStore(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        InstanceLease lease, ResourceLeaseManager resources, AcceptedExecutionAdmission accepted, string stepId, ITargetPersistenceObserver? observer = null)
    {
        _instance = instance; _database = database; _contracts = contracts; _lease = lease; _resources = resources;
        _observer = observer;
        _journalId = accepted.Receipt.JournalId; _subject = accepted.Identity.SubjectId; _stepId = stepId;
        accepted.Prepared.RequireLive();
        var total = accepted.Prepared.Total.Plan.ReadWire();
        var step = total.Steps.Single(step => step.StepId == stepId);
        _provider = accepted.Prepared.Providers[stepId]; _launcherId = _provider.RetainedReservationId;
        _root = accepted.Root.Borrow(resources, accepted.Identity.OperationId, ExecutionPlanResources.Claims(total));
        try
        {
            _references = ProviderLauncherReferences.ReadLive(step, _provider);
            ProviderLauncherReferences.RequireSubset(total, _references, _provider.ExtensionId);
            _package = _provider.Use(package => package.Hold());
            _passive = accepted.Root.RetainReferences(resources, accepted.Identity.OperationId, _references);
            _provider.ClaimRetainedReservation();
        }
        catch { _passive?.Dispose(); _package?.Dispose(); _root.Dispose(); throw; }
    }

    internal ProviderLauncherBinding Register(OwnedWorkerJob job) => Use(() => _provider.Use(_ => _lease.PublishExecution(() =>
    {
        var state = ReadFresh();
        var prior = Current(state);
        if (prior is not null)
        {
            if (prior.Stage != "registered" || prior.Binding.JobName != job.Name ||
                prior.Binding.OwnerWriterId != JournalWriterStore.CurrentWriter.WriterId) throw Invalid();
            RequireEmpty(job); return prior.Binding;
        }
        RequireEmpty(job);
        if (state.IsDispatchClosed || state.Gate.State != EffectGateState.Entered ||
            state.Journal.Phase != JournalPhase.Applying || state.Latest.Revision != state.Journal.Revision ||
            state.Jobs.Any(item => item.JobName == job.Name)) throw Invalid();
        var step = state.Total.Steps.Single(step => step.StepId == _stepId);
        if (step.Provider?.Launcher is null || step.Provider.ActivationId != _provider.ActivationId.ToString("D") ||
            step.Provider.ExtensionId != _provider.ExtensionId) throw Invalid();
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var binding = new ProviderLauncherBinding(_instance.InstanceId, _journalId, state.Journal.OperationId,
            Guid.Parse(state.Attempt.AttemptId), _stepId, _launcherId, job.Name, JournalWriterStore.CurrentWriter.WriterId,
            process.SessionId, state.Attempt.ExecutionPlan.Sha256, _provider.ActivationId, _provider.ExtensionId, step.Provider.EntryId);
        Publish(state, new(ProviderLauncherCodec.Format, 1, binding, "registered", null));
        return binding;
    })));

    internal T Start<T>(ProviderLauncherBinding binding, OwnedWorkerJob job, Func<ConfigurationEditorCommand, T> start) =>
        Use(() => _provider.Use(_ =>
        {
            var state = ReadFresh(); var current = Required(state, binding);
            if (current.Stage != "registered" || current.Binding.JobName != job.Name || state.IsDispatchClosed) throw Invalid();
            var step = state.Total.Steps.Single(step => step.StepId == binding.StepId);
            var launcher = step.Provider!.Launcher!;
            var command = EditorCommandPolicy.Freeze(new(launcher.Image.Path, launcher.Image.Sha256,
                launcher.Arguments.ToImmutableArray(), step.WorkingDirectory));
            return start(command);
        }));

    internal void RecordRoot(ProviderLauncherBinding binding, OwnedWorkerJob job, OwnedWorkerIdentity root) =>
        Use(() => _provider.Use(_ => _lease.PublishExecution(() =>
        {
            var state = ReadFresh(); var current = Required(state, binding);
            var identity = new ProcessIdentity(root.ProcessId, root.CreatedAtUtc, root.ImagePath, root.ImageSha256, root.UserSid, root.IsElevated);
            if (root.InstanceId != binding.InstanceId || root.AttemptId != binding.AttemptId || current.Binding.JobName != job.Name) throw Invalid();
            if (current.Stage == "root")
            { if (ProviderLauncherRows.Root(_database, binding) != identity) throw Invalid(); return true; }
            if (current.Stage != "registered") throw Invalid();
            using var process = System.Diagnostics.Process.GetProcessById(root.ProcessId);
            if (!job.ContainsProcessHandle(process.SafeHandle) || ProcessIdentityReader.Read(root.ProcessId) != identity ||
                job.GetAccounting() is not { ActiveProcesses: 1, TotalProcesses: 1 }) throw Invalid();
            _database.Write((connection, transaction) =>
            { ProviderLauncherRows.InsertRootInside(connection, transaction, state, binding, identity); return true; });
            PublishArtifact(state, binding, "root", new ProviderLauncherRoot("nexus.execution.provider-launcher-root", 1, binding, identity));
            return true;
        })));

    internal void RecordStarted(PreparedLauncherStart proof) =>
        proof.Use(proof.Binding, (actualRoot, error) => Use(() => _lease.PublishExecution(() =>
        {
            var state = ReadFresh(); var current = Required(state, proof.Binding);
            if (actualRoot is null || error is not null) throw Invalid();
            var document = new ProviderLauncherStarted("nexus.execution.provider-launcher-started", 1, proof.Binding, actualRoot);
            if (current.Stage == "started")
            { if (!Same(ReadArtifact<ProviderLauncherStarted>(current.Artifact!), document)) throw Invalid(); return true; }
            if (current.Stage != "root" || ProviderLauncherRows.Root(_database, proof.Binding) != actualRoot) throw Invalid();
            PublishArtifact(state, proof.Binding, "started", document); return true;
        })));

    internal void RecordStopped(ProviderLauncherBinding binding, OwnedWorkerJob job, PreparedStoppedTarget stopped, PreparedLauncherStart? started) =>
        stopped.Use(job, inventory => Use(() => _lease.PublishExecution(() =>
        {
            var state = ReadFresh(); var current = Required(state, binding);
            var root = ProviderLauncherRows.Root(_database, binding);
            int? nativeError = null;
            if (started is not null) started.Use(binding, (actualRoot, error) =>
            { if (actualRoot is not null && actualRoot != root) throw Invalid(); nativeError = error; return true; });
            var document = new ProviderLauncherStopped("nexus.execution.provider-launcher-stopped", 1, binding, root, inventory,
                started?.Succeeded == true, nativeError);
            if (current.Stage == "stopped")
            { if (!Same(ReadArtifact<ProviderLauncherStopped>(current.Artifact!), document)) throw Invalid(); return true; }
            if (current.Stage == "retained") throw Invalid();
            var artifact = new ImmutableBlobStore(_instance, _contracts).PersistPlan(BoundedJson.Serialize(document, StepResultCodec.Options));
            using var held = new ImmutableBlobStore(_instance, _contracts).OpenVerified(artifact);
            Publish(state, new(ProviderLauncherCodec.Format, 1, binding, "stopped", artifact, LaunchEngine(document))); return true;
        })));

    internal PreparedRetentionIntent PrepareRetention(ProviderLauncherBinding binding, OwnedWorkerJob job, OwnedTargetInventory inventory) =>
        Use(() => _provider.Use(_ => _lease.PublishExecution(() =>
        {
            var state = ReadFresh(); var current = Required(state, binding);
            if (current.Stage is not ("started" or "retentionPrepared")) throw Invalid();
            RequireRetainInventory(state, binding, inventory);
            var necessary = LauncherBoundaryReader.Read(_instance, _database, _contracts, state, binding.StepId);
            var root = ProviderLauncherRows.Root(_database, binding) ?? throw Invalid();
            if (current.Stage == "retentionPrepared")
            {
                var prior = ReadArtifact<ProviderLauncherRetention>(current.Artifact!);
                if (Same(prior.Inventory, inventory) && prior.NecessaryClosures.SequenceEqual(necessary.Closures) &&
                    prior.PrimaryResult == necessary.Primary && prior.Terminal == necessary.Terminal)
                    return new PreparedRetentionIntent(new PreparedProviderRetention(this, job, binding, current.Artifact!));
            }
            if (ProviderLauncherCodec.ReadAll(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)
                .Count(item => item.Binding == binding && item.Stage == "retentionPrepared") >= ProviderLauncherCodec.MaximumRetentionIntents) throw Invalid();
            var document = new ProviderLauncherRetention("nexus.execution.provider-launcher-retention", 1, binding, root,
                inventory, necessary.Closures, necessary.Primary, necessary.Terminal, _references, state.Journal.Revision);
            var artifact = PublishArtifact(state, binding, "retentionPrepared", document);
            _observer?.Reached(TargetPersistencePoint.RetentionPrepared, binding);
            return new PreparedRetentionIntent(new PreparedProviderRetention(this, job, binding, artifact));
        })));

    internal T UseRetention<T>(ProviderLauncherBinding binding, OwnedWorkerJob job, BlobReference reference,
        OwnedTargetInventory inventory, Func<T> effect) => Use(() => _lease.PublishExecution(() =>
    {
        var state = ReadFresh(); var current = Required(state, binding);
        if (current.Stage != "retentionPrepared" || current.Artifact != reference || binding.JobName != job.Name) throw Invalid();
        var document = ReadArtifact<ProviderLauncherRetention>(reference);
        if (!Same(document.Inventory, inventory) || !document.References.SequenceEqual(_references)) throw Invalid();
        RequireRetainInventory(state, binding, inventory);
        var necessary = LauncherBoundaryReader.Read(_instance, _database, _contracts, state, binding.StepId);
        if (!necessary.Closures.SequenceEqual(document.NecessaryClosures) || necessary.Primary != document.PrimaryResult ||
            necessary.Terminal != document.Terminal) throw Invalid();
        return job.HasTargetLifetimeChange && job.CaptureTargetInventory().LimitFlags == 0 ? effect() : _provider.Use(_ => effect());
    }));

    internal ProviderLauncherLifetime RecordRetained(ProviderLauncherBinding binding, OwnedWorkerJob job,
        PreparedRetentionIntent intent, OwnedTargetHandoff handoff) => job.UseTargetHandoff(handoff, () => Use(() => _lease.PublishExecution(() =>
    {
        var state = ReadFresh(); var current = Required(state, binding);
        var document = new ProviderLauncherRetained("nexus.execution.provider-launcher-retained", 1, binding, intent.Reference, handoff);
        BlobReference artifact;
        if (current.Stage == "retained")
        { if (!Same(ReadArtifact<ProviderLauncherRetained>(current.Artifact!), document)) throw Invalid(); artifact = current.Artifact!; }
        else
        {
            if (current.Stage != "retentionPrepared" || current.Artifact != intent.Reference ||
                handoff.PreviousLimitFlags != 0x00002000 || handoff.EffectiveLimitFlags != 0 ||
                !Same(ReadArtifact<ProviderLauncherRetention>(intent.Reference).Inventory, handoff.Inventory)) throw Invalid();
            artifact = PublishArtifact(state, binding, "retained", document);
        }
        if (_transferred) throw Invalid();
        var lifetime = new ProviderLauncherLifetime(_instance, _database, _contracts, _lease, _resources,
            binding, _subject, artifact, job, handoff, _references, _passive!, _package!);
        _provider.KeepRetained(lifetime);
        _passive = null; _package = null; _transferred = true;
        return lifetime;
    })));

    internal ProviderLauncherBinding? FindRegistered(OwnedWorkerJob job) => Use(() => _lease.PublishExecution(() =>
    {
        var current = Current(ReadFresh());
        if (current is null) return null;
        if (current.Binding.JobName != job.Name || current.Binding.OwnerWriterId != JournalWriterStore.CurrentWriter.WriterId) throw Invalid();
        return current.Binding;
    }));

    private void RequireRetainInventory(AttemptWriterState state, ProviderLauncherBinding binding, OwnedTargetInventory inventory)
    {
        TargetJobEvidence.RequireInventory(inventory);
        var image = state.Total.Steps.Single(step => step.StepId == binding.StepId).Provider!.Launcher!.Image;
        var root = ProviderLauncherRows.Root(_database, binding) ?? throw Invalid();
        if (inventory.ActiveProcesses == 0 || inventory.LimitFlags != 0x00002000 || !inventory.AllFamily.Contains(root) ||
            inventory.LivingFamily.Any(member => member.UserSid != root.UserSid || member.IsElevated != root.IsElevated ||
                !StringComparer.OrdinalIgnoreCase.Equals(member.ImagePath, image.Path) || member.ImageSha256 != image.Sha256)) throw Invalid();
    }

    private ProviderLauncherState? Current(AttemptWriterState state) =>
        ProviderLauncherCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256).SingleOrDefault(item => item.Binding.StepId == _stepId);
    private static ProviderLauncherState Required(AttemptWriterState state, ProviderLauncherBinding binding) =>
        ProviderLauncherCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256).SingleOrDefault(item => item.Binding == binding) ?? throw Invalid();

    private AttemptWriterState ReadFresh()
    {
        _lease.VerifyHeld(_instance);
        var state = AttemptWriterState.Read(_instance, _database, _contracts, _journalId);
        if (state.Latest.Revision != state.Journal.Revision)
        { ExecutionAttemptProjection.Synchronize(_instance, _database, _contracts, state); state = AttemptWriterState.Read(_instance, _database, _contracts, _journalId); }
        return state;
    }
    private T Use<T>(Func<T> callback) => _root.Use(callback);
    private T ReadArtifact<T>(BlobReference reference) where T : class =>
        new TargetJobEvidence(_instance, _database, _contracts).Read<T>(reference);
    private BlobReference PublishArtifact<T>(AttemptWriterState state, ProviderLauncherBinding binding, string stage, T document) =>
        TargetJournalPublisher.PublishParticipant(_instance, _database, _contracts, state, document, artifact =>
            ProviderLauncherCodec.Participant(new(ProviderLauncherCodec.Format, 1, binding, stage, artifact),
                state.Total.Steps.Single(step => step.StepId == binding.StepId).ScriptId));
    private void Publish(AttemptWriterState state, ProviderLauncherIntent intent) =>
        TargetJournalPublisher.AppendParticipant(_instance, _database, _contracts, state,
            ProviderLauncherCodec.Participant(intent, state.Total.Steps.Single(step => step.StepId == _stepId).ScriptId));
    private static void RequireEmpty(OwnedWorkerJob job)
    { if (job.GetAccounting() is not { ActiveProcesses: 0, TotalProcesses: 0 } || job.ActiveProcessIds().Count != 0) throw Invalid(); }
    private static bool Same<T>(T first, T second) => JsonElement.DeepEquals(
        JsonSerializer.SerializeToElement(first, StepResultCodec.Options), JsonSerializer.SerializeToElement(second, StepResultCodec.Options));

    public void Dispose()
    {
        if (_disposed) return;
        var current = Current(ReadFresh());
        if (current is not null && current.Stage is not ("stopped" or "retained")) throw Invalid();
        if (current?.Stage == "retained" && !_transferred) throw Invalid();
        if (!_transferred) _provider.ReleaseRetainedReservation(_launcherId);
        _passive?.Dispose(); _package?.Dispose(); _root.Dispose(); _disposed = true;
    }
    internal static EngineOutcome LaunchEngine(ProviderLauncherStopped document) =>
        document.StartReturned ? EngineOutcome.Succeeded : document.Inventory.TotalProcesses != 0 ? EngineOutcome.Unknown :
            document.NativeStartErrorCode is not null ? EngineOutcome.Failed : EngineOutcome.NotStarted;
    private static IOException Invalid() => new("execution_provider_launcher_boundary_unverified");
}

internal static class ProviderLauncherRows
{
    private const string RootColumns = "step_id,job_name,writer_id,session_id,total_plan_sha256,activation_id,extension_id,entry_id," +
        "process_id,created_file_time,image_path,image_sha256,user_sid,elevated";

    internal static IEnumerable<Guid> ReadRecoveryParents(StateDatabase database)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        var after = "";
        while (true)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT DISTINCT r.journal_id FROM main.execution_provider_launcher_roots r " +
                "WHERE r.journal_id>$after AND NOT EXISTS(SELECT 1 FROM main.execution_provider_launcher_ended e " +
                "WHERE e.journal_id=r.journal_id AND e.launcher_id=r.launcher_id) ORDER BY r.journal_id LIMIT 64;";
            command.Parameters.AddWithValue("$after", after);
            var ids = new List<Guid>();
            using (var row = command.ExecuteReader()) while (row.Read()) ids.Add(Guid.ParseExact(row.GetString(0), "D"));
            foreach (var id in ids) yield return id;
            if (ids.Count < 64) yield break;
            after = ids[^1].ToString("D");
        }
    }

    internal static ProcessIdentity Creator(StateDatabase database, ProviderLauncherBinding binding)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT process_id,created_file_time,image_path,image_sha256,user_sid,elevated FROM main.journal_writers " +
            "WHERE journal_id=$journal AND writer_id=$writer;";
        command.Parameters.AddWithValue("$journal", binding.ParentJournalId.ToString("D")); command.Parameters.AddWithValue("$writer", binding.OwnerWriterId.ToString("D"));
        using var row = command.ExecuteReader();
        return row.Read() ? new(row.GetInt32(0), DateTime.FromFileTimeUtc(row.GetInt64(1)), row.GetString(2), row.GetString(3), row.GetString(4), row.GetBoolean(5)) : throw Invalid();
    }

    internal static ProcessIdentity? Root(StateDatabase database, ProviderLauncherBinding binding)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        using var transaction = DatabaseConnections.BeginRead(connection);
        var result = RootInside(connection, transaction, binding); transaction.Commit(); return result;
    }

    internal static ProcessIdentity? RootInside(SqliteConnection connection, SqliteTransaction transaction, ProviderLauncherBinding binding)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT " + RootColumns + " FROM main.execution_provider_launcher_roots " +
            "WHERE journal_id=$journal AND launcher_id=$launcher;";
        command.Parameters.AddWithValue("$journal", binding.ParentJournalId.ToString("D"));
        command.Parameters.AddWithValue("$launcher", binding.LauncherId.ToString("D"));
        using var row = command.ExecuteReader();
        return row.Read() ? LauncherRoot.Read(row, 0).Bind(binding) : null;
    }

    private readonly record struct LauncherRoot(string StepId, string JobName, string WriterId, int SessionId, string PlanSha256,
        string ActivationId, string ExtensionId, string EntryId, int ProcessId, long CreatedFileTime, string ImagePath,
        string ImageSha256, string UserSid, bool Elevated)
    {
        internal static LauncherRoot Read(SqliteDataReader row, int offset) => new(
            row.GetString(offset), row.GetString(offset + 1), row.GetString(offset + 2), row.GetInt32(offset + 3),
            row.GetString(offset + 4), row.GetString(offset + 5), row.GetString(offset + 6), row.GetString(offset + 7),
            row.GetInt32(offset + 8), row.GetInt64(offset + 9), row.GetString(offset + 10), row.GetString(offset + 11),
            row.GetString(offset + 12), row.GetBoolean(offset + 13));

        internal ProcessIdentity Bind(ProviderLauncherBinding binding)
        {
            if (StepId != binding.StepId || JobName != binding.JobName || WriterId != binding.OwnerWriterId.ToString("D") ||
                SessionId != binding.SessionId || PlanSha256 != binding.TotalPlanSha256 || ActivationId != binding.ActivationId.ToString("D") ||
                ExtensionId != binding.ExtensionId || EntryId != binding.EntryId) throw Invalid();
            return new(ProcessId, DateTime.FromFileTimeUtc(CreatedFileTime), ImagePath, ImageSha256, UserSid, Elevated);
        }
    }

    internal static void InsertRootInside(SqliteConnection connection, SqliteTransaction transaction, AttemptWriterState state,
        ProviderLauncherBinding binding, ProcessIdentity root)
    {
        var prior = RootInside(connection, transaction, binding);
        if (prior is not null) { if (prior != root) throw Invalid(); return; }
        if (!OperatingSystem.IsWindows() || binding.OwnerWriterId != JournalWriterStore.CurrentWriter.WriterId ||
            ProcessIdentityReader.Read(Environment.ProcessId) != JournalWriterStore.CurrentWriter.Process) throw Invalid();
        var current = ProviderLauncherCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)
            .SingleOrDefault(item => item.Binding == binding);
        var image = state.Total.Steps.Single(step => step.StepId == binding.StepId).Provider!.Launcher!.Image;
        if (current?.Stage != "registered" || !StringComparer.OrdinalIgnoreCase.Equals(root.ImagePath, image.Path) ||
            root.ImageSha256 != image.Sha256) throw Invalid();
        ProviderLauncherProjection.RequireNewEffectInside(connection, transaction, state, binding.StepId);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO main.execution_provider_launcher_roots(journal_id,launcher_id,step_id,job_name,writer_id,session_id," +
            "total_plan_sha256,activation_id,extension_id,entry_id,process_id,created_file_time,image_path,image_sha256,user_sid,elevated) " +
            "VALUES($journal,$launcher,$step,$job,$writer,$session,$plan,$activation,$extension,$entry,$pid,$created,$path,$sha,$sid,$elevated);";
        command.Parameters.AddWithValue("$journal", binding.ParentJournalId.ToString("D"));
        command.Parameters.AddWithValue("$launcher", binding.LauncherId.ToString("D")); command.Parameters.AddWithValue("$step", binding.StepId);
        command.Parameters.AddWithValue("$job", binding.JobName); command.Parameters.AddWithValue("$writer", binding.OwnerWriterId.ToString("D"));
        command.Parameters.AddWithValue("$session", binding.SessionId); command.Parameters.AddWithValue("$plan", binding.TotalPlanSha256);
        command.Parameters.AddWithValue("$activation", binding.ActivationId.ToString("D")); command.Parameters.AddWithValue("$extension", binding.ExtensionId);
        command.Parameters.AddWithValue("$entry", binding.EntryId); command.Parameters.AddWithValue("$pid", root.ProcessId);
        command.Parameters.AddWithValue("$created", root.CreatedAtUtc.ToFileTimeUtc()); command.Parameters.AddWithValue("$path", root.ImagePath);
        command.Parameters.AddWithValue("$sha", root.ImageSha256); command.Parameters.AddWithValue("$sid", root.UserSid);
        command.Parameters.AddWithValue("$elevated", root.IsElevated); command.ExecuteNonQuery();
    }

    internal static BlobReference? Ended(StateDatabase database, ProviderLauncherBinding binding, BlobReference retained)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT e.retained_path,e.retained_size,e.retained_sha256,e.artifact_path,e.artifact_size,e.artifact_sha256 " +
            "FROM main.execution_provider_launcher_ended e JOIN main.operations o ON o.operation_id=e.operation_id " +
            "JOIN main.operation_journals p ON p.journal_id=e.journal_id JOIN main.operations parent ON parent.operation_id=p.operation_id " +
            "WHERE e.journal_id=$journal AND e.launcher_id=$launcher AND o.subject_id=parent.subject_id " +
            "AND o.operation_kind='execution.provider-launcher-ended' AND o.status='completed' AND o.request_digest=e.artifact_sha256;";
        command.Parameters.AddWithValue("$journal", binding.ParentJournalId.ToString("D"));
        command.Parameters.AddWithValue("$launcher", binding.LauncherId.ToString("D"));
        using var row = command.ExecuteReader();
        if (!row.Read()) return null;
        if (new BlobReference(row.GetString(0), row.GetInt64(1), row.GetString(2)) != retained) throw Invalid();
        var artifact = new BlobReference(row.GetString(3), row.GetInt64(4), row.GetString(5));
        StepEvidenceCodec.Plan(artifact); return artifact;
    }

    internal static void InsertEndedInside(SqliteConnection connection, SqliteTransaction transaction,
        OperationIdentity identity, ProviderLauncherBinding binding, BlobReference retained, BlobReference artifact)
    {
        OperationRows.Accept(connection, transaction, identity, DateTimeOffset.UtcNow);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO main.execution_provider_launcher_ended(journal_id,launcher_id,operation_id," +
            "retained_path,retained_size,retained_sha256,artifact_path,artifact_size,artifact_sha256) " +
            "VALUES($journal,$launcher,$operation,$retained,$retainedSize,$retainedSha,$artifact,$artifactSize,$artifactSha) " +
            "ON CONFLICT(journal_id,launcher_id) DO NOTHING;";
        command.Parameters.AddWithValue("$journal", binding.ParentJournalId.ToString("D"));
        command.Parameters.AddWithValue("$launcher", binding.LauncherId.ToString("D"));
        command.Parameters.AddWithValue("$operation", identity.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$retained", retained.RelativePath); command.Parameters.AddWithValue("$retainedSize", retained.SizeBytes);
        command.Parameters.AddWithValue("$retainedSha", retained.Sha256); command.Parameters.AddWithValue("$artifact", artifact.RelativePath);
        command.Parameters.AddWithValue("$artifactSize", artifact.SizeBytes); command.Parameters.AddWithValue("$artifactSha", artifact.Sha256);
        command.ExecuteNonQuery();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM main.execution_provider_launcher_ended WHERE journal_id=$journal AND launcher_id=$launcher " +
            "AND operation_id=$operation AND retained_path=$retained AND retained_size=$retainedSize AND retained_sha256=$retainedSha " +
            "AND artifact_path=$artifact AND artifact_size=$artifactSize AND artifact_sha256=$artifactSha);";
        if ((long)command.ExecuteScalar()! != 1) throw Invalid();
        command.CommandText = "UPDATE main.operations SET status='completed' WHERE operation_id=$operation;"; command.ExecuteNonQuery();
    }

    internal static void Verify(StateDatabase database, System.Text.Json.JsonElement journal,
        NexusPipeline.Platform.Serialization.Protocol.ExecutionPlan total, string totalSha)
    {
        var jobs = ExecutionInvocationCodec.Views(journal).SelectMany(view => ProviderLauncherCodec.Read(view, total, totalSha))
            .GroupBy(item => item.Binding.LauncherId).Select(group => group.Last()).ToArray();
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        using var transaction = DatabaseConnections.BeginRead(connection);
        VerifyProjectionInside(connection, transaction, journal.GetProperty("journalId").GetString()!, total, jobs);
        transaction.Commit();
        VerifyRetainedEndings(database, connection, jobs);
    }

    internal static void VerifyProjectionInside(SqliteConnection connection, SqliteTransaction transaction,
        string journalId, NexusPipeline.Platform.Serialization.Protocol.ExecutionPlan total, IReadOnlyList<ProviderLauncherState> jobs)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT launcher_id," + RootColumns + " FROM main.execution_provider_launcher_roots WHERE journal_id=$journal LIMIT 1025;";
        command.Parameters.AddWithValue("$journal", journalId);
        var rows = new Dictionary<string, (Guid Id, LauncherRoot Root)>(StringComparer.Ordinal);
        using (var read = command.ExecuteReader())
            while (read.Read())
            {
                var key = read.GetString(0);
                rows.Add(key, (Guid.Parse(key), LauncherRoot.Read(read, 1)));
            }
        if (rows.Count != 0)
        {
            if (rows.Count > 1024) throw Invalid();
            var expected = jobs.ToDictionary(job => job.Binding.LauncherId);
            if (rows.Values.Any(row => !expected.TryGetValue(row.Id, out var job) || job.Binding.JobName != row.Root.JobName)) throw Invalid();
        }
        foreach (var job in jobs)
        {
            var root = rows.TryGetValue(job.Binding.LauncherId.ToString("D"), out var row) ? row.Root.Bind(job.Binding) : null;
            if (job.Stage is not ("registered" or "stopped" or "recoveredStopped") && root is null) throw Invalid();
            if (root is not null)
            {
                var image = total.Steps.Single(step => step.StepId == job.Binding.StepId).Provider!.Launcher!.Image;
                if (root.ImageSha256 != image.Sha256 || !StringComparer.OrdinalIgnoreCase.Equals(root.ImagePath, image.Path)) throw Invalid();
            }
        }
        command.CommandText = "SELECT launcher_id FROM main.execution_provider_launcher_ended WHERE journal_id=$journal LIMIT 1025;";
        using (var read = command.ExecuteReader())
            while (read.Read())
                if (!jobs.Any(job => job.Binding.LauncherId.ToString("D") == read.GetString(0) && job.Stage == "retained")) throw Invalid();
    }

    internal static void VerifyRetainedEndings(StateDatabase database, SqliteConnection connection, IReadOnlyList<ProviderLauncherState> jobs)
    {
        foreach (var job in jobs.Where(job => job.Stage == "retained"))
        {
            // Presence and semantic validity are distinct; a damaged row must not look like no ending fact.
            using var check = connection.CreateCommand();
            check.CommandText = "SELECT count(*) FROM main.execution_provider_launcher_ended WHERE journal_id=$journal AND launcher_id=$launcher;";
            check.Parameters.AddWithValue("$journal", job.Binding.ParentJournalId.ToString("D")); check.Parameters.AddWithValue("$launcher", job.Binding.LauncherId.ToString("D"));
            if ((long)check.ExecuteScalar()! > 0 && Ended(database, job.Binding, job.Artifact!) is null) throw Invalid();
        }
    }

    private static IOException Invalid() => new("execution_provider_launcher_sql_unbound");
}

[SupportedOSPlatform("windows")]
internal sealed class PreparedProviderRetention
{
    private readonly ProviderLauncherStore _store;
    private readonly OwnedWorkerJob _job;
    internal ProviderLauncherBinding Binding { get; }
    internal BlobReference Reference { get; }

    internal PreparedProviderRetention(ProviderLauncherStore store, OwnedWorkerJob job,
        ProviderLauncherBinding binding, BlobReference reference)
    { _store = store; _job = job; Binding = binding; Reference = reference; }

    internal T Use<T>(OwnedWorkerJob actualJob, OwnedTargetInventory inventory, Func<T> effect)
    {
        if (!ReferenceEquals(actualJob, _job)) throw new IOException("execution_provider_launcher_owner_unbound");
        return _store.UseRetention(Binding, actualJob, Reference, inventory, effect);
    }
}
