using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.StorageTests;

internal sealed class AttemptCompletionFixture : IDisposable
{
    internal ConfigurationRunFixture Source { get; } = new(additional: true);
    private readonly ResourceOperationLease _root;
    private readonly PreparedConfigurationRun _prepared;
    internal RunAttemptPlan Parent { get; }
    internal ConfigurationRunStatus Group { get; }
    internal Guid ParentJournalId => Guid.Parse(Parent.JournalId);

    internal AttemptCompletionFixture()
    {
        try
        {
            _root = Source.Acquire(Source.Request);
            _prepared = Source.Preparation.Prepare(_root, Source.Owner, Source.Execution.Plan, Source.StepId, Source.Request);
            Parent = Source.Parent(_prepared); Source.Accept(Parent);
            var identity = ConfigurationRunStore.BeginIdentity(Guid.NewGuid(), ConfigurationRunFixture.Subject, ParentJournalId, Source.StepId, _prepared);
            Group = new ConfigurationRunStore(Source.Source.Instance, Source.Source.Database, new())
                .Begin(identity, ParentJournalId, Source.StepId, _prepared, DateTimeOffset.UtcNow);
        }
        catch { _prepared?.Dispose(); _root?.Dispose(); Source.Dispose(); throw; }
    }

    internal void Seal()
    {
        var parent = new RecoveryJournalRegistry(Source.Source.Database).ReadSnapshot().Journals.Single(item => item.JournalId == ParentJournalId);
        new JournalEffectGate(Source.Source.Database).SealUnentered(parent, 1);
    }

    internal JournalRecoveryResult Recover()
    {
        var parent = new RecoveryJournalRegistry(Source.Source.Database).ReadSnapshot().Journals.Single(item => item.JournalId == ParentJournalId);
        return new PreparedRecoveryHandler(Source.Source.Instance, new(), Source.Source.Database, id =>
        {
            var observed = new JournalWriterStore(Source.Source.Database).ObserveForCurrentExecutor(id);
            return new(Source.Source.Instance.InstanceId, id, observed.IsComplete, observed.State);
        }).Prepare(parent).Execute();
    }

    internal string[] ParentBytes() => Directory.GetFiles(Path.Combine(Source.Source.Instance.RuntimeRoot, "journals", ParentJournalId.ToString("D")), "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => Path.GetFileName(path) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();

    public void Dispose()
    {
        try { Source.Dispose(); }
        finally { _prepared.Dispose(); _root.Dispose(); }
    }
}
