using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Operations;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.StorageTests;

internal sealed record AttemptTestFixture(ExecutionPlanFixture Source, StateDatabase Database, RunAttemptPlan Plan,
    FrozenConfigurationExchange Configuration, BlobReference ConfigurationReference)
{
    internal Guid JournalId => Guid.Parse(Plan.JournalId);
    internal RuntimeInstance Instance => Source.Instance;
    internal string Root => Source.Root;

    internal static AttemptTestFixture Create(long configurationRevision = 1)
    {
        var source = ExecutionPlanFixture.CreateManagedPaths();
        var stepId = "binding-" + source.Plan.Steps[0].BindingId + "-0000";
        var execution = source.Plan with {
            Selection = [source.Plan.Selection[0] with { StepId = stepId }],
            Steps = [source.Plan.Steps[0] with { StepId = stepId }],
            ConfigurationSlots = [source.Plan.ConfigurationSlots[0] with { StepId = stepId, ExpectedRevision = configurationRevision }]
        };
        execution = execution with { RetryPolicy = ExecutionRetryAuthority.Capture(execution,
            new Dictionary<string, int> { [execution.Steps[0].ScriptId] = 2 }, new(true, 0)) };
        execution = execution with { PhysicalClaims = ExecutionPlanResources.Capture(execution) };
        execution = execution with { ResourceKeys = ExecutionPlanResources.Derive(execution) };
        source = source with { Plan = execution };
        var database = source.Database;
        using var total = source.Codec.Freeze(source.Plan);
        var plan = AttemptPlanSeed.Create(source, total);
        var scanner = new FileTreeScanner(SnapshotBudget.Default);
        var archive = new FileTreeArchive(scanner, source.Blobs);
        var target = source.Plan.Steps[0].ConfigurationTargets.Single();
        var candidate = Path.Combine(source.Root, "candidate");
        Directory.CreateDirectory(candidate);
        File.WriteAllBytes(Path.Combine(candidate, "settings.bin"), [9, 10, 11]);
        var configuration = new FrozenConfigurationExchange(source.Instance.InstanceId, Guid.NewGuid(), Guid.NewGuid(), JournalKind.ConfigExchange,
            target, PhysicalObjectIdentity.Read(Path.GetDirectoryName(target)!), archive.Capture(target, default), archive.Capture(candidate, default));
        var step = source.Plan.Steps[0];
        var saved = new ConfigurationRevision(new(Guid.Parse(step.BindingId!), "main"), configurationRevision,
            Guid.Parse(step.UserId!), Guid.Parse(step.ScriptId), new(1, new('c', 64), null, false), configuration.Candidate.Snapshot);
        using var state = new RunStateCodec(source.Instance, new(), source.Blobs).Freeze(new(source.Instance.InstanceId,
            Guid.Parse(plan.OperationId), configuration.OperationId, saved, 1, true, []));
        configuration = configuration with { RunState = state.Reference };
        using var prepared = new ConfigurationPlanCodec(source.Instance, new(), source.Blobs).Freeze(configuration);
        var log = source.Plan.Steps[0].LogTargets.Single().ScopePath;
        var beforeLog = archive.Capture(log, default);
        plan = plan with
        {
            ConfigurationPlans = [new() { StepId = step.StepId, Plan = new() { RelativePath = prepared.Reference.RelativePath, SizeBytes = prepared.Reference.SizeBytes, Sha256 = prepared.Reference.Sha256 } }],
            InitialLogs = [new()
            {
                Path = log, ParentIdentity = Identity(PhysicalObjectIdentity.Read(Path.GetDirectoryName(log)!)), Before = Tree(beforeLog.Snapshot),
                OriginalObjects = beforeLog.OriginalObjects.Select(item => new FileTreePhysicalObject { RelativePath = item.RelativePath, Identity = Identity(item.Identity) }).ToArray()
            }]
        };
        return new(source, database, plan, configuration, prepared.Reference);
    }

    internal AttemptAcceptanceResult Accept(IAttemptPersistenceObserver? observer = null) =>
        new RunAttemptAcceptance(Instance, new(), Database, observer).Accept(
            new OperationIdentity(Guid.Parse(Plan.OperationId), "fixture-attempt", new string('b', 64), "fixture.attempt"), Plan, DateTimeOffset.UtcNow);

    internal JournalRecoveryResult Recover(Func<Guid, AttemptWriterObservation>? writers = null, IAttemptPersistenceObserver? observer = null)
    {
        using var lease = InstanceLease.Acquire(Instance, new());
        var journal = new RecoveryJournalRegistry(Database).ReadSnapshot().Journals.Single(item => item.JournalId == JournalId);
        return new PreparedRecoveryHandler(Instance, new(), Database,
            writers ?? (id => new(Instance.InstanceId, id, true, WriterState.Stopped)), observer).Prepare(journal).Execute();
    }

    private static FileTreePhysicalIdentity Identity(PhysicalObjectIdentity identity) => new() { VolumeSerial = identity.VolumeSerial, FileId = identity.FileId };
    private static FileTree Tree(FileTreeSnapshot tree) => new()
    {
        Shape = tree.Shape.ToString().ToLowerInvariant(), Entries = tree.Entries.Select(item => new FileTreeEntriesItem
            { RelativePath = item.RelativePath, Kind = item.Kind.ToString().ToLowerInvariant(), SizeBytes = item.SizeBytes, Sha256 = item.Sha256 }).ToArray()
    };
}
