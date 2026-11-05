using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class BindingSuccessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainsEarlierSuccess(bool configuration)
    {
        using var fixture = new ExecutionSequentialFixture(configuration, judgement:
            new(AutomationJudgementLanguage.Keywords, ["ready"], ["failed"], null), captureFiles: false);
        await fixture.Create(0).Complete();
        Assert.Equal(BusinessOutcome.Succeeded, Assert.Single(StepResultCodec.Read(fixture.State.Latest.Root,
            fixture.State.Total)).Latest.Business);
        var second = fixture.Create(1);
        var binding = Guid.Parse(fixture.Steps[0].BindingId!);
        Assert.Equal(1, ExecutionBindingSuccesses.Today(fixture.Source.Database, DateTimeOffset.UtcNow).GetValueOrDefault(binding));
        var state = fixture.State;
        new ExecutionCancellationStore(fixture.Source.Instance, fixture.Source.Database, new(), fixture.Source.Lease).Cancel(
            ExecutionCancellationStore.Identity(Guid.NewGuid(), ExecutionSequentialFixture.Subject, fixture.ParentId,
                state.Journal.OperationId, Guid.Parse(state.Attempt.RunId), Guid.Parse(state.Attempt.AttemptId)), fixture.ParentId, DateTimeOffset.UtcNow);
        fixture.Terminal.CloseUndispatched(fixture.ParentId); await second.Stop(); await second.Collect(); await second.FinishConfiguration();
        fixture.Terminal.MarkApplied(fixture.ParentId);
        if (configuration)
        {
            var step = fixture.Steps[2].StepId; var sessionId = fixture.Sessions[step];
            var session = new ConfigurationRunStore(fixture.Source.Instance, fixture.Source.Database, new())
                .Read(sessionId, ExecutionSequentialFixture.Subject)!;
            var exchange = new ConfigurationEditExchange(fixture.Source.Instance, fixture.Source.Database, new());
            foreach (var resource in session.Resources) Assert.NotEqual(ConfigurationRecoveryResult.Quarantined, exchange.Restore(resource.JournalId));
            new RunCompletionService(fixture.Source.Instance, fixture.Source.Database, new(), fixture.Source.Lease,
                fixture.Base.Resources, fixture.Base.Index).CompleteOwnedInterrupted(Guid.NewGuid(), ExecutionSequentialFixture.Subject,
                    sessionId, fixture.Accepted.Prepared.Configurations[step]);
        }
        var receipt = fixture.Terminal.Commit(fixture.ParentId, LifecycleOutcome.Cancelled, DispatchIntentCodec.Reason);
        Assert.Equal(LifecycleOutcome.Cancelled, receipt.Facts.Lifecycle);
        Assert.Equal(1, ExecutionBindingSuccesses.Today(fixture.Source.Database, DateTimeOffset.UtcNow).GetValueOrDefault(binding));
        var history = new JournalStore(fixture.Source.Instance, new()).ReadAllRevisions(fixture.ParentId);
        fixture.Source.Database.Write((connection, transaction) =>
        {
            ExecutionBindingSuccesses.Project(connection, transaction, fixture.State, history[^1], history);
            ExecutionBindingSuccesses.Project(connection, transaction, fixture.State, history[^1], history);
            return true;
        });
        Assert.Equal(1, fixture.Base.Count("execution_binding_successes"));
        VerifyStartDay(fixture, history, binding);
    }

    private static void VerifyStartDay(ExecutionSequentialFixture fixture, IReadOnlyList<JournalDocument> history, Guid binding)
    {
        var yesterday = DateTimeOffset.UtcNow.AddDays(-1);
        var first = history.First(item => WriterIntentCodec.ReadJobs(item.Root, fixture.State.Total).Length != 0);
        var node = JsonNode.Parse(first.Export())!.AsObject(); node["updatedAt"] = yesterday.UtcDateTime.ToString("O");
        var temporalHistory = history.Select(item => item.Revision == first.Revision
            ? JournalDocument.Read(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(node), new()) : item).ToArray();
        fixture.Source.Database.Write((connection, transaction) =>
        {
            using var reset = connection.CreateCommand(); reset.Transaction = transaction;
            reset.CommandText = "DELETE FROM execution_binding_successes;"; reset.ExecuteNonQuery();
            ExecutionBindingSuccesses.Project(connection, transaction, fixture.State, history[^1], temporalHistory);
            return true;
        });
        Assert.Equal(1, ExecutionBindingSuccesses.Today(fixture.Source.Database, yesterday.AddMinutes(1)).GetValueOrDefault(binding));
        Assert.Equal(0, ExecutionBindingSuccesses.Today(fixture.Source.Database, DateTimeOffset.UtcNow).GetValueOrDefault(binding));
        Console.WriteLine("Start-day projection uses controlled journal input; native process clock was not changed.");
    }
}
