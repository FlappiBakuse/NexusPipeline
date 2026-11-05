using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class ProviderLauncherLifetime
{
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly ContractReader _contracts;
    private readonly InstanceLease _lease;
    private readonly ResourceLeaseManager _resources;
    private readonly string _subject;
    private readonly OwnedWorkerJob _job;
    private readonly OwnedTargetHandoff _handoff;
    private readonly ImmutableArray<ResourceClaim> _claims;
    private readonly RetainedResourceReferences _references;
    private readonly IDisposable _package;
    private readonly Guid _endOperation = Guid.NewGuid();
    private bool _ended;
    internal ProviderLauncherBinding Binding { get; }
    internal BlobReference Retained { get; }
    internal Exception? LastObservationFailure { get; private set; }
    internal bool IsEnded => _ended;

    internal ProviderLauncherLifetime(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        InstanceLease lease, ResourceLeaseManager resources, ProviderLauncherBinding binding, string subject,
        BlobReference retained, OwnedWorkerJob job, OwnedTargetHandoff handoff, ImmutableArray<ResourceClaim> claims,
        RetainedResourceReferences references, IDisposable package)
    {
        _instance = instance; _database = database; _contracts = contracts; _lease = lease; _resources = resources;
        Binding = binding; _subject = subject; Retained = retained; _job = job; _handoff = handoff;
        _claims = claims; _references = references; _package = package;
    }

    internal bool TryPublishEnded()
    {
        if (_ended) return true;
        try
        {
            _references.VerifyHeld(_resources, Binding.ParentOperationId);
            var requested = PhysicalResourceAdmission.Merge(_claims.Append(new ResourceClaim("blobs:content", ResourceClaimMode.Reference)));
            var admission = _resources.TryAcquire(_endOperation, requested);
            if (admission.Lease is null) return false;
            using var write = admission.Lease;
            var ended = _job.UseTargetHandoff(_handoff, () => _references.Use(() =>
            {
                var inventory = _job.CaptureTargetInventory();
                TargetJobEvidence.RequireInventory(inventory);
                if (inventory.LimitFlags != 0 || inventory.ActiveProcesses != 0 || !inventory.LivingFamily.IsEmpty) return false;
                return _lease.PublishExecution(() =>
                {
                    _lease.VerifyHeld(_instance);
                    var state = AttemptWriterState.Read(_instance, _database, _contracts, Binding.ParentJournalId);
                    var retained = ProviderLauncherCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)
                        .SingleOrDefault(item => item.Binding == Binding);
                    if (retained?.Stage != "retained" || retained.Artifact != Retained) throw Invalid();
                    var document = new ProviderLauncherEnded("nexus.execution.provider-launcher-ended", 1, Binding, Retained, inventory);
                    var bytes = BoundedJson.Serialize(document, StepResultCodec.Options);
                    var blobs = new ImmutableBlobStore(_instance, _contracts);
                    var artifact = blobs.PersistPlan(bytes);
                    using var held = blobs.OpenVerified(artifact);
                    var previous = ProviderLauncherRows.Ended(_database, Binding, Retained);
                    if (previous is not null)
                    { if (previous != artifact) throw Invalid(); return true; }
                    var identity = new OperationIdentity(_endOperation, _subject,
                        Convert.ToHexStringLower(SHA256.HashData(bytes)), "execution.provider-launcher-ended");
                    _database.Write((connection, transaction) =>
                    {
                        ProviderLauncherRows.InsertEndedInside(connection, transaction, identity, Binding, Retained, artifact);
                        return true;
                    });
                    return true;
                });
            }));
            if (!ended) return false;
            _ended = true;
            try { _job.Dispose(); } finally { try { _package.Dispose(); } finally { _references.Dispose(); } }
            LastObservationFailure = null; return true;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or
            System.Text.Json.JsonException or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException)
        {
            LastObservationFailure = error;
            return false;
        }
    }

    private static IOException Invalid() => new("execution_provider_launcher_ended_unbound");
}
