using System.Text;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using Xunit;

namespace NexusPipeline.StorageTests;

internal sealed class ConfigurationCursorsFixture : IDisposable
{
    internal PlanBuilderFixture Source { get; } = new();
    internal ExecutionPlanDraft Draft { get; }
    internal PreparedExecutionPlan Total { get; }
    internal ResourceOperationLease Root { get; }
    internal PreparedConfigurationRun Configuration { get; }
    internal PreparedAttemptPlan Attempt { get; }
    internal OperationIdentity Identity { get; }
    internal ConfigurationRevision Saved { get; }
    internal FrozenRunState State { get; }
    internal RunAttemptAcceptance Acceptance => new(Source.Instance, new(), Source.Database);

    internal ConfigurationCursorsFixture()
    {
        try
        {
            var request = Source.Request(new BindingTarget(Source.BindingB));
            Identity = ExecutionStartIdentity.Create(Guid.NewGuid(), "configuration-cursors", request);
            Draft = Source.Builder.Prepare(request, PlanBuilderFixture.CreatedAt);
            Total = Source.Builder.Freeze(Draft);
            var total = Total.Plan.ReadWire(); var step = total.Steps.Single();
            var index = new StartupJournalRecovery(Source.Instance, new(), Source.Database,
                new(Path.Combine(Source.Root, "host"), Path.Combine(Source.Instance.RuntimeRoot, "extensions"))).Recover(Source.Lease);
            var resources = new ResourceLeaseManager(new(index, TimeProvider.System));
            Root = Assert.IsType<ResourceOperationLease>(resources.TryAcquire(Identity.OperationId, ExecutionPlanResources.Claims(total)).Lease);
            var slot = Assert.Single(total.ConfigurationSlots);
            Saved = new ConfigurationRevisionStore(Source.Instance, Source.Database, new()).Read(new(Guid.Parse(slot.BindingId), slot.ResourceId))!;
            var binding = Source.Definitions.Read().Bindings.Single(item => item.Id == Source.BindingB);
            var run = new ConfigurationRunRequest(binding.Revision, true, [new(Guid.NewGuid(), Guid.NewGuid(),
                new(Saved.Address, Saved.Revision, Saved.Contract, slot.TargetPath)
                    { WritePolicy = new([""], [Saved.Tree.Shape]) }, [])]);
            Configuration = new ConfigurationRunPreparation(Source.Instance, Source.Database, new(), Source.Lease, resources)
                .Prepare(Root, Identity.OperationId, Total.Plan, step.StepId, run);
            var candidate = Configuration.Use(children => BuildAttempt(total, children));
            Attempt = new AttemptPlanCodec(Source.Instance, new(), new(Source.Instance, new())).Freeze(candidate);
            State = Configuration.Use(children => new RunStateCodec(Source.Instance, new(), new(Source.Instance, new()))
                .ReadStructure(Assert.Single(children).Plan.RunState!));
        }
        catch { Attempt?.Dispose(); Configuration?.Dispose(); Root?.Dispose(); Total?.Dispose(); Draft?.Dispose(); Source.Dispose(); throw; }
    }

    private RunAttemptPlan BuildAttempt(ExecutionPlan total, IReadOnlyList<PreparedRunConfiguration> children)
    {
        var attemptId = Guid.NewGuid().ToString("D");
        var journalId = Guid.NewGuid();
        var start = new ImmutableBlobStore(Source.Instance, new()).PersistPlan(StartRequestCodec.Encode(
            Identity, Source.Request(new BindingTarget(Source.BindingB)), Guid.Parse(total.RunId), journalId));
        var archive = new FileTreeArchive(new(SnapshotBudget.Default), new(Source.Instance, new()));
        return new()
        {
            Format = "nexus.execution.run-attempt-plan", Version = 1, InstanceId = total.InstanceId, RunId = total.RunId,
            OperationId = Identity.OperationId.ToString("D"), JournalId = journalId.ToString("D"), AttemptId = attemptId, AttemptOrdinal = 1,
            StartRequest = new() { RelativePath = start.RelativePath, SizeBytes = start.SizeBytes, Sha256 = start.Sha256 },
            ExecutionPlan = new() { RelativePath = Total.Reference.RelativePath, SizeBytes = Total.Reference.SizeBytes, Sha256 = Total.Reference.Sha256 },
            ResourceKeys = total.ResourceKeys, OutputDirectory = $"runs/{total.RunId}/{attemptId}", LogDirectory = $"logs/{total.RunId}/{attemptId}",
            ConfigurationPlans = children.Select(child => new RunAttemptPlanConfigurationPlansItem { StepId = total.Steps.Single().StepId,
                Plan = new() { RelativePath = child.Reference.RelativePath, SizeBytes = child.Reference.SizeBytes, Sha256 = child.Reference.Sha256 } }).ToArray(),
            InitialLogs = total.Steps.SelectMany(step => step.LogTargets).Select(log => log.ScopePath).Select(path =>
            {
                var before = archive.Capture(path, default);
                return new RunAttemptPlanInitialLogsItem { Path = path,
                    ParentIdentity = FileTreeProtocol.ToWire(PhysicalObjectIdentity.Read(Path.GetDirectoryName(path)!)),
                    Before = FileTreeProtocol.ToWire(before.Snapshot), OriginalObjects = before.OriginalObjects.Select(item =>
                        new FileTreePhysicalObject { RelativePath = item.RelativePath, Identity = FileTreeProtocol.ToWire(item.Identity) }).ToArray() };
            }).ToArray()
        };
    }

    internal AttemptAcceptanceResult Accept() => Acceptance.AcceptStart(Identity, Draft, Attempt, DateTimeOffset.UtcNow);

    internal ConfigurationSaveResult SaveNext()
    {
        var blobs = new ImmutableBlobStore(Source.Instance, new());
        var content = blobs.PersistContent(Encoding.UTF8.GetBytes("{\"fixture\":false}"), default);
        var tree = new FileTreeSnapshot(FileObjectKind.File, [new("", FileObjectKind.File, content.SizeBytes, content.Sha256)]);
        var change = new ConfigurationRevisionChange(Saved.Address, Saved.Revision, State.BindingRevision, Saved.Contract, tree, ConfigurationUpdateKind.Manual);
        return new ConfigurationRevisionStore(Source.Instance, Source.Database, new()).Commit(
            ConfigurationRevisionStore.Identity(Guid.NewGuid(), "configuration-cursors", change), change, DateTimeOffset.UtcNow);
    }

    internal void Execute(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Source.Database.DatabasePath, Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }

    internal void ChangeSavedHeader(string assignment) => Execute("UPDATE configuration_revisions SET " + assignment +
        $" WHERE binding_id='{Saved.Address.BindingId:D}' AND resource_id='{Saved.Address.ResourceId}' AND revision={Saved.Revision};");

    internal void ReleasePreparation()
    {
        Attempt.Dispose(); Configuration.Dispose(); Total.Dispose(); Draft.Dispose(); Root.Dispose();
    }

    public void Dispose() { ReleasePreparation(); Source.Dispose(); }
}
