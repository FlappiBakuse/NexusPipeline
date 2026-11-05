using System.Runtime.Versioning;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Scheduling;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
public sealed class ExecutionAdmissionService
{
    private readonly AdmissionCoordinator _coordinator;
    private readonly ExecutionPlanBuilder _builder;
    private readonly ExecutionAdmissionPort _port;
    private readonly ResourceLeaseManager _resources;
    private readonly Action<TaskAdapterService> _configureAdapters;

    public ExecutionAdmissionService(RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease,
        ResourceLeaseManager resources, RecoveryIndex index, ExecutionAdmissionInbox inbox, ExecutionPreparationLimits limits,
        IAttemptPersistenceObserver? observer = null, PayloadApplyRoots? payloadRoots = null, ExecutionJudgementRuntime? judgementRuntime = null,
        ExecutionProviderRegistry? providers = null)
    {
        lease.VerifyHeld(instance);
        if (!resources.UsesRecoveryIndex(index)) throw new IOException("execution_admission_recovery_index_unbound");
        inbox.Bind(instance, resources, index);
        _builder = new(instance, database, contracts, lease, limits, judgementRuntime, providers);
        _resources = resources;
        _configureAdapters = adapter => { adapter.RequireOwner(instance, database, lease, resources); _builder.ConfigureTaskAdapters(adapter); };
        _port = new(instance, database, contracts, lease, resources, index, inbox, _builder, observer,
            payloadRoots ?? new(PhysicalPathPolicy.RuntimeApplicationDirectory(), Path.Combine(instance.RuntimeRoot, "extensions")));
        _coordinator = new(resources, _port);
    }

    public void ConfigureTaskAdapters(ITaskAdapterPort adapters)
    {
        if (adapters is not TaskAdapterService installed) throw new IOException("adapter_service_owner_unbound");
        _configureAdapters(installed);
    }

    internal void ConfigureEmulators(NexusPipeline.Platform.Storage.Emulation.EmulatorTargetService emulators) => _builder.ConfigureEmulators(emulators);

    public ExecutionPlanDraft DryRun(ExecutionSelectionRequest request, DateTimeOffset createdAt) => _builder.Prepare(request, createdAt);

    public AttemptAcceptanceResult Start(Guid operationId, string subject, ExecutionSelectionRequest request, DateTimeOffset createdAt)
        => (AttemptAcceptanceResult)_coordinator.Start(operationId, subject, request, createdAt);

    internal AttemptAcceptanceResult StartScheduled(PreparedExecutionOrigin origin, DateTimeOffset createdAt)
    {
        var port = _port.WithScheduledOrigin(origin);
        var identity = port.Identity(origin.OperationId, origin.SubjectId, origin.Request);
        if (port.Lookup(identity) is AttemptAcceptanceResult replay) return replay;
        AcceptedExecutionRun queued;
        try { queued = origin.QueuedForAdmission(_resources); }
        catch (IOException error) when (error.Message == "execution_scheduled_already_accepted")
        { return port.Lookup(identity) as AttemptAcceptanceResult ?? throw new IOException("execution_scheduled_replay_unbound"); }
        catch (ExecutionSelectionException error) when (error.Message == "execution_host_unavailable")
        {
            if (port.Lookup(identity) is AttemptAcceptanceResult accepted) return accepted;
            throw;
        }
        return (AttemptAcceptanceResult)new AdmissionCoordinator(_resources, port)
            .ContinueAcceptedRun(queued, origin.OperationId, origin.SubjectId, origin.Request, createdAt);
    }

    internal AttemptAcceptanceResult StartRetry(PreparedExecutionRetry origin, DateTimeOffset createdAt)
    {
        var port = _port.WithRetry(origin);
        var identity = port.Identity(origin.Frozen.NextOperationId, origin.Intent.SubjectId, origin.Request);
        if (port.Lookup(identity) is AttemptAcceptanceResult replay) return replay;
        return (AttemptAcceptanceResult)new AdmissionCoordinator(_resources, port)
            .ContinueAcceptedRun(origin.AcceptedRun, origin.Frozen.NextOperationId, origin.Intent.SubjectId, origin.Request, createdAt);
    }

    internal ExecutionPlanDraft PreviewWithDiscoveries(ExecutionSelectionRequest request, DateTimeOffset createdAt,
        IReadOnlyDictionary<Guid, PreparedMuMuDiscovery> discoveries) => _builder.PrepareWithDiscoveries(request, createdAt, discoveries);

    internal AttemptAcceptanceResult StartWithDiscoveries(Guid operationId, string subject, ExecutionSelectionRequest request,
        DateTimeOffset createdAt, IReadOnlyDictionary<Guid, PreparedMuMuDiscovery> discoveries) =>
        (AttemptAcceptanceResult)new AdmissionCoordinator(_resources, _port.WithTargetDiscoveries(discoveries))
            .Start(operationId, subject, request, createdAt);
}
