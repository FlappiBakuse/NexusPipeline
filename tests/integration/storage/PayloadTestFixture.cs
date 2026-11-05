using NexusPipeline.Core.Configuration;
using System.Runtime.Versioning;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed record PayloadTestFixture(string Root, RuntimeInstance Instance, StateDatabase Database, FileTreeScanner Scanner,
    PayloadApplyRoots Roots, FrozenPayloadApply Plan, PayloadPlanCodec Codec) : IDisposable
{
    internal static PayloadTestFixture Create(JournalKind kind, PayloadAction action)
    {
        if (kind == JournalKind.ExtensionApply) return CreateExtension(action);
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "payload-plan-" + Guid.NewGuid().ToString("N"));
        var contracts = new ContractReader();
        var instance = new InstanceRoot(contracts).OpenOrCreate(Path.Combine(root, "runtime"));
        var database = StateDatabase.CreateFresh(instance);
        var roots = new PayloadApplyRoots(Path.Combine(root, "host-payload"), Path.Combine(root, "extensions"));
        Directory.CreateDirectory(roots.ExtensionPayloadRoot);
        var target = kind == JournalKind.UpdateApply ? roots.HostPayloadRoot : Path.Combine(roots.ExtensionPayloadRoot, "fixture-extension");
        if (action != PayloadAction.Install)
        {
            Directory.CreateDirectory(target);
            File.WriteAllBytes(Path.Combine(target, "README.md"), [0, 1, 255]);
        }
        var candidate = Path.Combine(root, "candidate");
        if (action != PayloadAction.Remove)
        {
            Directory.CreateDirectory(candidate);
            File.WriteAllBytes(Path.Combine(candidate, "README.md"), [3, 5, 7]);
        }
        var blobs = new ImmutableBlobStore(instance, contracts);
        var scanner = new FileTreeScanner(SnapshotBudget.Default);
        var archive = new FileTreeArchive(scanner, blobs);
        var plan = new FrozenPayloadApply(instance.InstanceId, Guid.NewGuid(), Guid.NewGuid(), kind, action,
            kind == JournalKind.UpdateApply ? null : "fixture-extension", target, PhysicalObjectIdentity.Read(Path.GetDirectoryName(target)!),
            archive.Capture(target, default), archive.Capture(candidate, default),
            [kind == JournalKind.UpdateApply ? "maintenance:host-payload" : "extension:fixture-extension"]);
        plan = UpdateSourceSeed.Bind(instance, database, plan, candidate);
        return new(root, instance, database, scanner, roots, plan, new(instance, contracts, blobs, roots));
    }

    private PreparedExtensionPayload? _prepared;
    private IDisposable? _owner;
    private InstanceLease? _heldLease;
    private ResourceOperationLease? _operationRoot;
    private ResourceLeaseManager? _resources;

    private static PayloadTestFixture CreateExtension(PayloadAction action)
    {
        if (action == PayloadAction.Install)
        {
            var source = new PayloadAcceptanceFixture(savedEnabled: false);
            try
            {
                var prepared = source.Prepare();
                var owner = source.Package.Catalog;
                var plan = prepared.UseAcceptance(owner.Instance, owner.Database, source.Roots, (_, value, _) => value);
                return new(source.Package.Root, owner.Instance, owner.Database, new(SnapshotBudget.Default), source.Roots,
                    plan, new(owner.Instance, owner.Contracts, source.Package.Blobs, source.Roots))
                { _prepared = prepared, _owner = source, _heldLease = owner.Lease, _operationRoot = source.Root, _resources = owner.Resources };
            }
            catch { source.Dispose(); throw; }
        }
        var upgrade = ExtensionUpgradeFixture.CreateAsync(enabled: false).GetAwaiter().GetResult();
        try
        {
            upgrade.Prepare(action);
            var owner = upgrade.Original.Package.Catalog;
            var plan = upgrade.Prepared!.UseAcceptance(owner.Instance, owner.Database, upgrade.Original.Roots, (_, value, _) => value);
            return new(upgrade.Next.Root, owner.Instance, owner.Database, new(SnapshotBudget.Default), upgrade.Original.Roots,
                plan, new(owner.Instance, owner.Contracts, new(owner.Instance, owner.Contracts), upgrade.Original.Roots))
            { _prepared = upgrade.Prepared, _owner = new AsyncOwner(upgrade), _heldLease = owner.Lease,
                _operationRoot = upgrade.Root, _resources = owner.Resources };
        }
        catch { upgrade.DisposeAsync().AsTask().GetAwaiter().GetResult(); throw; }
    }

    internal PayloadAcceptanceResult Accept(IPayloadPersistenceObserver? observer = null) =>
        _prepared is not null ? new PayloadApplyAcceptance(Instance, new(), Database, Roots, observer)
            .AcceptPrepared(_prepared, DateTimeOffset.UtcNow) :
        new PayloadApplyAcceptance(Instance, new(), Database, Roots, observer).Accept(
            new OperationIdentity(Plan.OperationId, "fixture-payload", new string('a', 64), "fixture.payload"), Plan, DateTimeOffset.UtcNow);

    internal JournalRecoveryResult Recover(Func<Guid, PayloadWriterObservation>? deny = null, IPayloadPersistenceObserver? observer = null)
    {
        _prepared?.Dispose();
        using var acquiredLease = _heldLease is null ? InstanceLease.Acquire(Instance, new()) : null;
        (_heldLease ?? acquiredLease!).VerifyHeld(Instance);
        using var borrow = _operationRoot is null ? null : _operationRoot.Borrow(_resources!, Plan.OperationId,
            new PayloadSourceCodec(Instance, new()).ReadMetadata(Plan.ExtensionSource!).Claims);
        JournalRecoveryResult Execute()
        {
            var journal = new RecoveryJournalRegistry(Database).ReadJournal(Plan.JournalId);
            PayloadWriterObservation Observe(Guid id)
            {
                var writers = new JournalWriterStore(Database);
                var native = writers.Observe(id, writers.RequireCurrentExecutor(id));
                var actual = new PayloadWriterObservation(Instance.InstanceId, id, native.IsComplete, native.State);
                var requested = deny?.Invoke(id);
                return requested is not null && (requested.InstanceId != Instance.InstanceId || requested.JournalId != id ||
                    !requested.IsComplete || requested.State != WriterState.Stopped) ? requested : actual;
            }
            return new PreparedPayloadHandler(Instance, new(), Database, Roots, Plan.Kind, Observe, observer)
                .Prepare(journal).Execute();
        }
        return borrow is null ? Execute() : borrow.Use(Execute);
    }

    public void Dispose() => _owner?.Dispose();
    private sealed class AsyncOwner(IAsyncDisposable value) : IDisposable
    {
        public void Dispose() => value.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
