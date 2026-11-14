using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Settings;

public enum StartupEffectBoundary { Saved, SnapshotPublished, Entered, NativeReturned, Completed }
public interface IStartupEffectObserver { void Reached(StartupEffectBoundary boundary, Guid operationId); }

[SupportedOSPlatform("windows")]
public sealed class StartupRegistrationCoordinator
{
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly InstanceLease _lease;
    private readonly RecoveryIndex _index;
    private readonly IStartupRegistrationEffects _effects;
    private readonly StartupRegistrationCommand _template;
    private readonly IStartupEffectObserver? _observer;

    public StartupRegistrationCoordinator(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
        RecoveryIndex index, IStartupRegistrationEffects effects, StartupRegistrationCommand template, IStartupEffectObserver? observer = null)
    {
        lease.VerifyHeld(instance);
        StartupRegistrationPolicy.Validate(template);
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot || template.Definition.InstanceId != instance.InstanceId)
            throw new IOException("startup_coordinator_instance_unbound");
        _instance = instance; _database = database; _lease = lease; _index = index;
        _effects = effects; _template = template; _observer = observer;
        PublishHolds(database, index);
    }

    internal StartupIntentDraft Prepare(OperationIdentity operation, HostSettingsSnapshot before, HostSettingsChange change)
    {
        _lease.VerifyHeld(_instance);
        _ = HostSettingsPolicy.Next(before, change.ExpectedRevision, change.Values);
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.System);
        var prior = StartupIntentRows.ReadCurrent(connection, null);
        if (StartupIntentRows.ReadAll(connection, null, _instance.InstanceId, pendingOnly: true).Count != 0)
            throw new HostSettingsException("settings_startup_recovery_required");
        var current = prior is null ? null : StartupIntentCodec.DecodeCommand(prior);
        var command = change.Values.AutoStart ? _template with { Definition = _template.Definition with
            { RegistrationId = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow } } : current ?? _template;
        var actual = _effects.Inspect(command.Definition);
        if (actual.State == StartupRegistrationState.Foreign) throw new HostSettingsException("startup_registration_foreign");
        if (change.Values.AutoStart)
        {
            if (current is not null) throw new HostSettingsException("startup_registration_requires_reconciliation");
            _effects.ValidateOnly(command.Definition);
        }
        return new(new("nexus.startup.intent", 1, _instance.InstanceId, operation.OperationId, before.Revision,
            change.Values.AutoStart, prior is null ? null : StartupIntentCodec.Hash(prior), command, ProcessIdentityReader.Read(Environment.ProcessId)));
    }

    internal void Saved(Guid operationId) { PublishHolds(_database, _index); _observer?.Reached(StartupEffectBoundary.Saved, operationId); }
    internal void SnapshotPublished(Guid operationId) => _observer?.Reached(StartupEffectBoundary.SnapshotPublished, operationId);

    public bool ReadRegisteredEnabled()
    {
        _lease.VerifyHeld(_instance);
        byte[]? current;
        using (var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.System))
            current = StartupIntentRows.ReadCurrent(connection, null);
        if (current is null) return false;
        var command = StartupIntentCodec.DecodeCommand(current);
        var observed = _effects.Inspect(command.Definition);
        if (observed.State == StartupRegistrationState.Foreign) throw new IOException("startup_registration_foreign");
        return observed.State == StartupRegistrationState.Matches;
    }

    public StartupEffectStatus? ReadPendingStatus()
    {
        _lease.VerifyHeld(_instance);
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.System);
        return StartupIntentRows.ReadAll(connection, null, _instance.InstanceId, pendingOnly: true).SingleOrDefault()?.Status;
    }

    internal StartupEffectStatus Execute(Guid operationId, ResourceOperationLease resources)
    {
        _lease.VerifyHeld(_instance);
        if (!resources.IsActive || resources.OperationId != operationId || !new[] { "settings:host", "system:startup" }.All(key =>
            resources.Claims.Any(claim => claim.ResourceKey == key && claim.Mode == ResourceClaimMode.Mutation)))
            throw new IOException("startup_effect_resource_lease_required");
        var intent = Read(_database, operationId);
        if (intent.Status.State != StartupEffectState.Prepared || intent.Document.Creator != ProcessIdentityReader.Read(Environment.ProcessId))
            throw new IOException("startup_effect_creator_unbound");
        Advance(_database, operationId, StartupEffectState.Prepared, StartupEffectState.Entered);
        _observer?.Reached(StartupEffectBoundary.Entered, operationId);
        try
        {
            if (intent.Document.Enable) _effects.CreateIfMissing(intent.Document.Command);
            else _effects.RemoveIfMatching(intent.Document.Command.Definition);
            _observer?.Reached(StartupEffectBoundary.NativeReturned, operationId);
            if (!Satisfied(_effects, intent.Document)) throw new IOException("startup_effect_outcome_unknown");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
            or System.Runtime.InteropServices.COMException or HostSettingsException)
        {
            Advance(_database, operationId, StartupEffectState.Entered, StartupEffectState.Quarantined);
            PublishHolds(_database, _index);
            return Read(_database, operationId).Status;
        }
        Advance(_database, operationId, StartupEffectState.Entered, StartupEffectState.Completed);
        PublishHolds(_database, _index);
        _observer?.Reached(StartupEffectBoundary.Completed, operationId);
        return Read(_database, operationId).Status;
    }

    public static void VerifyReadOnly(StateDatabase database)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.System);
        StartupIntentRows.Verify(connection, database.InstanceId);
        foreach (var intent in StartupIntentRows.ReadAll(connection, null, database.InstanceId, pendingOnly: true))
            if (WriterStopEvidence.ObserveProcess(intent.Document.Creator) != WriterState.Stopped)
                throw new IOException("startup_effect_creator_not_stopped");
    }

    public static void ReconcileStopped(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
        RecoveryIndex index, IStartupRegistrationInspector inspector)
    {
        lease.VerifyHeld(instance);
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot)
            throw new IOException("startup_reconciliation_instance_unbound");
        VerifyReadOnly(database);
        StoredStartupIntent[] pending;
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.System))
            pending = StartupIntentRows.ReadAll(connection, null, instance.InstanceId, pendingOnly: true).ToArray();
        foreach (var intent in pending)
        {
            var complete = false;
            try { complete = intent.Status.State != StartupEffectState.Prepared && Satisfied(inspector, intent.Document); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
                or System.Runtime.InteropServices.COMException or HostSettingsException) { }
            var target = complete ? StartupEffectState.Completed : StartupEffectState.Quarantined;
            if (target != intent.Status.State) Advance(database, intent.Status.OperationId, intent.Status.State, target);
        }
        PublishHolds(database, index);
    }

    private static bool Satisfied(IStartupRegistrationInspector inspector, StartupIntentDocument document)
    {
        var actual = inspector.Inspect(document.Command.Definition);
        return document.Enable ? actual.State == StartupRegistrationState.Matches : actual.State == StartupRegistrationState.Missing;
    }

    private static StoredStartupIntent Read(StateDatabase database, Guid operationId)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.System);
        return StartupIntentRows.Read(connection, null, operationId, database.InstanceId) ?? throw new IOException("startup_intent_missing");
    }

    private static void Advance(StateDatabase database, Guid operationId, StartupEffectState before, StartupEffectState after) =>
        database.Write((connection, transaction) =>
        {
            StartupIntentRows.Advance(connection, transaction, database.InstanceId, operationId, before, after);
            return true;
        });

    private static void PublishHolds(StateDatabase database, RecoveryIndex index)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.System);
        var holds = StartupIntentRows.ReadAll(connection, null, database.InstanceId, pendingOnly: true)
            .SelectMany(intent => new[] { "settings:host", "system:startup" }.Select(key =>
                new RecoveryResourceHold(intent.Status.OperationId, key, "startup_effect_pending"))).ToArray();
        index.UpdateResourceHolds(RecoveryHoldOwner.StartupRegistration, holds);
    }
}
