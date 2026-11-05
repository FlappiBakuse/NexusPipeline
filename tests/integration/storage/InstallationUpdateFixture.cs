using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Installation;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Maintenance;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class InstallationUpdateFixture : IDisposable
{
    internal InstallerRegistrationFixture Product { get; } = new();
    internal InstallationIdentity Original { get; }
    internal RuntimeInstance Instance { get; }
    internal StateDatabase Database { get; }
    internal FrozenPayloadApply Plan { get; }
    internal HostUpdateSource Source { get; }
    internal InstallationUpdateSource Installation { get; }
    internal ProcessIdentity Host { get; }
    internal string Checkpoint => Product.Checkpoint;
    private readonly string? _previousRoot;

    internal InstallationUpdateFixture()
    {
        Product.Begin(); Product.InstallPayload();
        Original = Product.Owner.Register(Product.Application, Product.Transaction);
        _previousRoot = Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT");
        Environment.SetEnvironmentVariable("NEXUS_TEST_ROOT", Product.Root);
        Instance = new InstanceRoot(new()).OpenExisting(Path.Combine(Product.Application, "runtime"));
        using var lease = InstanceLease.Acquire(Instance, new());
        Database = StateDatabase.CreateFresh(Instance);
        _ = new StartupJournalRecovery(Instance, new(), Database,
            new(Product.Application, Path.Combine(Instance.RuntimeRoot, "extensions"))).Recover(lease);
        var blobs = new ImmutableBlobStore(Instance, new());
        var archive = new FileTreeArchive(new(SnapshotBudget.Default), blobs);
        var before = archive.CaptureApplication(Product.Application, default);
        archive.MaterializeNew(before, Path.Combine(Product.Root, "candidate-update"), default);
        var candidate = Path.Combine(Product.Root, "candidate-update");
        File.WriteAllText(Path.Combine(candidate, "README.md"), "Controlled new application payload");
        var plan = new FrozenPayloadApply(Instance.InstanceId, Guid.NewGuid(), Guid.NewGuid(), JournalKind.UpdateApply,
            PayloadAction.Upgrade, null, Product.Application, PhysicalObjectIdentity.Read(Product.Root), before,
            archive.CaptureApplication(candidate, default), ["maintenance:host-payload"]);
        Plan = UpdateSourceSeed.Bind(Instance, Database, plan, candidate, executableTestPayload: true, hostVersion: "0.18.1");
        Source = new UpdateSourceCodec(Instance, new()).ReadMetadata(Plan.MaintenanceSource!);
        Installation = new InstallationUpdateBinding(Instance, new()).Read(Source.Installation!, Plan.TargetPath, Plan.Before, "test");
        var current = ProcessIdentityReader.Read(Environment.ProcessId);
        Host = current with { ImagePath = Path.Combine(Plan.TargetPath, ApplicationTree.HostEntry),
            ImageSha256 = Plan.Candidate.Snapshot.Entries.Single(item => item.RelativePath == ApplicationTree.HostEntry).Sha256! };
    }

    internal void Switch()
    {
        var effects = new ApplicationTreeEffects(new(SnapshotBudget.Default), new(new(SnapshotBudget.Default), new(Instance, new())));
        var workspace = effects.CreateWorkspace(Plan);
        var scene = new PayloadTreeScene(Plan.OperationId, Plan.JournalId, "workspace-created", workspace, []);
        var candidate = effects.MaterializeCandidate(Plan, scene, default);
        scene = scene with { Stage = "original-preservation-started", CandidateObjects = candidate };
        effects.PreserveOriginal(Plan, scene);
        effects.InstallCandidate(Plan, scene with { Stage = "original-preserved" });
    }

    internal (BlobReference Reference, InstallationUpdateFrame Frame, MaintenanceCommitEntry Commit) Prepare()
    {
        var codec = new InstallationUpdateCheckpoint(Instance, new());
        var reference = codec.Prepare(Installation, Source, Plan, Host);
        var frame = codec.Read(reference, Installation, Source, Plan);
        var intent = new MaintenanceCommitIntent(MaintenanceCommitCodec.Format, 1, 7, Source.PayloadManifest) { Installation = reference };
        var commit = new MaintenanceCommitEntry(intent, Guid.NewGuid(), Guid.NewGuid(), Host,
            "Local\\NexusWorker-" + Guid.NewGuid().ToString("N"), Original.RootIdentity);
        return (reference, frame, commit);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("NEXUS_TEST_ROOT", _previousRoot);
        Product.Dispose();
    }
}
