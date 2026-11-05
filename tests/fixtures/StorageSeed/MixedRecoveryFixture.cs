using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Extensions;

namespace NexusPipeline.StorageTests;

internal sealed record MixedRecoveryFixture(AttemptTestFixture Attempt, PayloadApplyRoots Roots, RecoveryJournalRegistry Registry)
{
    internal static MixedRecoveryFixture Create(bool unknownLast)
    {
        var attempt = AttemptTestFixture.Create();
        attempt = attempt with { Plan = attempt.Plan with { JournalId = Id(4).ToString("D") } };
        var roots = new PayloadApplyRoots(Path.Combine(attempt.Root, "host-payload"), Path.Combine(attempt.Root, "extensions"));
        Directory.CreateDirectory(roots.ExtensionPayloadRoot);
        var fixture = new MixedRecoveryFixture(attempt, roots, new(attempt.Database));
        fixture.Extension(unknownLast);
        fixture.Configuration(JournalKind.ConfigExchange, 1);
        fixture.Configuration(JournalKind.ConfigEdit, 2);
        fixture.Cleanup();
        attempt.Accept();
        fixture.Update();
        return fixture;
    }

    internal IReadOnlyList<IJournalRecoveryHandler> Handlers() => [
        new ConfigurationRecoveryHandler(Attempt.Instance, new(), Attempt.Database, JournalKind.ConfigExchange, id => new(Attempt.Instance.InstanceId, id, true, WriterState.Stopped)),
        new ConfigurationRecoveryHandler(Attempt.Instance, new(), Attempt.Database, JournalKind.ConfigEdit, id => new(Attempt.Instance.InstanceId, id, true, WriterState.Stopped)),
        new CleanupRecoveryHandler(Attempt.Instance, new(), Attempt.Database, id => new(Attempt.Instance.InstanceId, id, true, WriterState.Stopped), payloadRoots: Roots),
        new PreparedRecoveryHandler(Attempt.Instance, new(), Attempt.Database, id => new(Attempt.Instance.InstanceId, id, true, WriterState.Stopped)),
        new PreparedPayloadHandler(Attempt.Instance, new(), Attempt.Database, Roots, JournalKind.UpdateApply, id => new(Attempt.Instance.InstanceId, id, true, WriterState.Stopped)),
        new PreparedPayloadHandler(Attempt.Instance, new(), Attempt.Database, Roots, JournalKind.ExtensionApply, id => new(Attempt.Instance.InstanceId, id, true, WriterState.Stopped)),
        new AssetRecoveryHandler(Attempt.Instance, new(), Attempt.Database, _ => new(true, WriterState.Stopped)),
        Secret()
    ];

    private IJournalRecoveryHandler Secret()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return new NexusPipeline.Platform.Storage.Secrets.PreparationRecoveryHandler(Attempt.Instance, new(), Attempt.Database, _ => new(true, WriterState.Stopped));
    }

    private void Configuration(JournalKind kind, int ordinal)
    {
        var target = Path.Combine(Attempt.Root, "configuration-" + ordinal);
        File.WriteAllBytes(target, [20, (byte)ordinal]);
        var archive = new FileTreeArchive(new(SnapshotBudget.Default), Attempt.Source.Blobs);
        var before = archive.Capture(target, default);
        var plan = new FrozenConfigurationExchange(Attempt.Instance.InstanceId, Guid.NewGuid(), Id(ordinal), kind, target,
            PhysicalObjectIdentity.Read(Attempt.Root), before, before);
        using var prepared = new ConfigurationPlanCodec(Attempt.Instance, new(), Attempt.Source.Blobs).Freeze(plan);
        var registered = new RegisteredJournal(plan.JournalId, plan.OperationId, kind, JournalPhase.Prepared, 1,
            prepared.Reference.RelativePath, prepared.Reference.Sha256, ["configuration:" + ordinal]);
        Registry.AcceptPrepared(new(plan.OperationId, "fixture", prepared.Reference.Sha256, "fixture.configuration"), registered,
            [new(plan.JournalId, "install-candidate", FileEffectAction.InstallCandidate, FileEffectState.Pending, prepared.Reference.Sha256, null)], DateTimeOffset.UtcNow);
        Initial(registered);
    }

    private void Cleanup()
    {
        var source = Path.Combine(Attempt.Root, "unused-content");
        File.WriteAllBytes(source, [222, 111]);
        var snapshot = new FileTreeScanner(SnapshotBudget.Default).Capture(source, default);
        var blob = Attempt.Source.Blobs.PersistContent(source, snapshot.Entries.Single(), default);
        var plan = new CleanupPlanCodec(Attempt.Instance, new(), Attempt.Source.Blobs).Capture(Guid.NewGuid(), Id(3), [blob]);
        new FileCleanupAcceptance(Attempt.Instance, new(), Attempt.Database).Accept(
            new(plan.OperationId, "fixture", new string('c', 64), "fixture.cleanup"), plan, DateTimeOffset.UtcNow);
    }

    private void Update()
    {
        var target = Roots.HostPayloadRoot;
        Directory.CreateDirectory(target);
        File.WriteAllBytes(Path.Combine(target, "payload.bin"), [30, 5]);
        var archive = new FileTreeArchive(new(SnapshotBudget.Default), Attempt.Source.Blobs);
        var before = archive.Capture(target, default);
        var plan = new FrozenPayloadApply(Attempt.Instance.InstanceId, Guid.NewGuid(), Id(5), JournalKind.UpdateApply,
            PayloadAction.Upgrade, null, target, PhysicalObjectIdentity.Read(Path.GetDirectoryName(target)!), before, before,
            ["maintenance:host-payload"]);
        plan = UpdateSourceSeed.Bind(Attempt.Instance, Attempt.Database, plan, Path.Combine(Attempt.Root, "host-update-candidate"));
        new PayloadApplyAcceptance(Attempt.Instance, new(), Attempt.Database, Roots).Accept(
            new(plan.OperationId, "fixture", new string('d', 64), "fixture.payload"), plan, DateTimeOffset.UtcNow);
    }

    private void Extension(bool unknown)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var lease = InstanceLease.Acquire(Attempt.Instance, new());
        var index = new RecoveryIndex(); index.PublishVerified(0, 1, []);
        var lifecycle = new HostLifecycle(index, TimeProvider.System);
        var resources = new ResourceLeaseManager(lifecycle);
        using var catalog = new ExtensionCatalogFixture(Attempt.Instance, Attempt.Database, lease, index, resources, lifecycle);
        using var package = new BoundPackageFixture(kind: "data-specialized", catalog: catalog);
        package.Publish();
        var snapshot = Registry.ReadSnapshot(); _ = Registry.PublishVerified(snapshot.Revision);
        var target = Path.Combine(Roots.ExtensionPayloadRoot, "fixture-plugin");
        var claims = PhysicalResourceAdmission.Merge(new ResourceClaim[]
        {
            new("extension:fixture-plugin", ResourceClaimMode.Mutation), new("extensions:catalog", ResourceClaimMode.Reference),
            new("blobs:content", ResourceClaimMode.Reference), new("settings:host", ResourceClaimMode.Mutation)
        }.Concat(PhysicalResourceAdmission.Read(target)).Concat(PhysicalResourceAdmission.Read(
            ExtensionWorkspacePath.For(target, package.OperationId))));
        using var root = resources.TryAcquire(package.OperationId, claims).Lease ?? throw new IOException("fixture_resources_unavailable");
        using var verified = package.Prepare(root);
        var candidate = verified.CaptureCandidate(root, package.Blobs, default);
        using var prepared = PreparedExtensionPayload.PrepareFirstInstall(Attempt.Instance, Attempt.Database, lease,
            resources, index, root, candidate, Roots, new(), "fixture", default);
        if (!unknown)
        {
            new PayloadApplyAcceptance(Attempt.Instance, new(), Attempt.Database, Roots).AcceptPrepared(prepared, DateTimeOffset.UtcNow);
            return;
        }
        var plan = prepared.UseAcceptance(Attempt.Instance, Attempt.Database, Roots, (_, value, _) => value);
        using var frozen = new PayloadPlanCodec(Attempt.Instance, new(), Attempt.Source.Blobs, Roots).Freeze(plan);
        var document = JsonNode.Parse(File.ReadAllBytes(Path.Combine(Attempt.Instance.RuntimeRoot, frozen.Reference.RelativePath)))!;
        document["version"] = 19;
        var reference = Attempt.Source.Blobs.PersistPlan(JsonSerializer.SerializeToUtf8Bytes(document));
        var journal = new RegisteredJournal(plan.JournalId, plan.OperationId, plan.Kind, JournalPhase.Prepared, 1,
            reference.RelativePath, reference.Sha256, plan.ResourceKeys);
        Registry.AcceptPrepared(new(plan.OperationId, "fixture", reference.Sha256, "fixture.payload"), journal, [], DateTimeOffset.UtcNow);
        Initial(journal);
    }

    private void Initial(RegisteredJournal journal)
    {
        var wire = new ExecutionJournal
        {
            Format = "nexus.execution.journal", Version = 1, InstanceId = Attempt.Instance.InstanceId.ToString("D"),
            JournalId = journal.JournalId.ToString("D"), OperationId = journal.OperationId.ToString("D"),
            OperationKind = char.ToLowerInvariant(journal.Kind.ToString()[0]) + journal.Kind.ToString()[1..],
            Phase = "prepared", Revision = 1, UpdatedAt = "2026-10-19T00:00:00Z", PlanPath = journal.PlanPath,
            PlanDigest = journal.PlanDigest, ResourceKeys = journal.ResourceKeys, Participants = [], Receipts = []
        };
        new JournalStore(Attempt.Instance, new()).Create(JsonSerializer.SerializeToUtf8Bytes(wire));
    }

    private static Guid Id(int ordinal) => Guid.Parse($"00000000-0000-0000-0000-{ordinal:000000000000}");
}
