using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class SnapshotCompletionFixture : IDisposable
{
    internal ConfigurationRunFixture Run { get; } = new(additional: true);
    internal Guid SessionId { get; } = Guid.NewGuid();
    internal Guid ParentId { get; }
    internal RunCompletionService Completion { get; }

    internal SnapshotCompletionFixture(bool enterParent = false)
    {
        using (var root = Run.Acquire(Run.Request))
        using (var prepared = Run.Preparation.Prepare(root, Run.Owner, Run.Execution.Plan, Run.StepId, Run.Request))
        {
            var parent = Run.Parent(prepared); ParentId = Guid.Parse(parent.JournalId); Run.Accept(parent);
            var operation = ConfigurationRunStore.BeginIdentity(SessionId, ConfigurationRunFixture.Subject, ParentId, Run.StepId, prepared);
            new ConfigurationRunStore(Run.Source.Instance, Run.Source.Database, new()).Begin(operation, ParentId, Run.StepId, prepared, DateTimeOffset.UtcNow);
        }
        var registry = new RecoveryJournalRegistry(Run.Source.Database);
        var registered = registry.ReadSnapshot().Journals.Single(item => item.JournalId == ParentId);
        var gate = new JournalEffectGate(Run.Source.Database);
        if (enterParent) gate.Enter(registered, 1); else gate.SealUnentered(registered, 1);
        foreach (var resource in Session.Resources)
            Assert.Equal(ConfigurationRecoveryResult.NotExecuted, new ConfigurationEditExchange(Run.Source.Instance, Run.Source.Database, new()).Restore(resource.JournalId));
        Publish();
        Completion = new(Run.Source.Instance, Run.Source.Database, new(), Run.Lease, Run.Resources, Run.Index);
    }

    internal ConfigurationRunStatus Session => new ConfigurationRunStore(Run.Source.Instance, Run.Source.Database, new())
        .Read(SessionId, ConfigurationRunFixture.Subject)!;

    internal void Publish(bool completionEvidence = false)
    {
        var source = Run.Source;
        IRecoveryEvidence evidence = completionEvidence ? new SnapshotCompletionEvidence(source.Instance, source.Database, new())
            : new JournalRecoveryEvidence(source.Instance, new(source.Instance, new()));
        new RecoveryCoordinator(new RecoveryJournalRegistry(source.Database), evidence, Run.Index).VerifyAndPublish();
        ConfigurationRecoveryHolds.Publish(source.Database, Run.Index);
    }

    internal void Quarantine()
    {
        var source = Run.Source;
        new PreparedRecoverySession(source.Instance, ParentId, new(), source.Database, () =>
        {
            var observed = new JournalWriterStore(source.Database).ObserveForCurrentExecutor(ParentId);
            return new(source.Instance.InstanceId, ParentId, observed.IsComplete, observed.State);
        }).Quarantine("attempt_prepared_no_effect_unverified");
        Publish();
    }

    public void Dispose() => Run.Dispose();
}
