using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class LiveRetryTests
{
    [Theory(DisplayName = "The Actual Accepted Scope Does Not Turn An Unknown Or Foreign Recovery Projection Into Post Deferral Evidence")]
    [InlineData("current")]
    [InlineData("unknown")]
    [InlineData("foreignHold")]
    public async Task RejectsUnverifiedDeferral(string condition)
    {
        await using var fixture = await ProviderRuntimeFixture.CreateAsync("success");
        using var accepted = Assert.IsType<AcceptedExecutionAdmission>(fixture.Base.Inbox.Take(fixture.Accepted.Acceptance.OperationId));
        var source = fixture.Source;
        var step = accepted.Prepared.Total.Plan.ReadWire().Steps.Single();
        var scope = new StepWriterScope(source.Instance, source.Database, new(), source.Lease,
            accepted.Receipt.JournalId, step.StepId, provider: accepted.Prepared.Providers[step.StepId]);
        using var writer = new ConfigurationWriterProcess(source.Instance, source.Lease, scope);
        try
        {
            scope.EnterApplying();
            var state = source.Lease.PublishExecution(() => AttemptWriterState.Read(
                source.Instance, source.Database, new(), accepted.Receipt.JournalId));
            var bytes = state.Latest.Export();
            if (condition == "unknown") fixture.Base.Index.BlockUnknown("controlled_unknown");
            if (condition == "foreignHold")
                fixture.Base.Index.UpdateResourceHolds(RecoveryHoldOwner.ConfigurationOperations,
                    [new(Guid.NewGuid(), state.Journal.ResourceKeys[0], "controlled_foreign")]);
            if (condition == "current")
                LiveHookEvidence.Require(source.Database, fixture.Base.Resources, fixture.Base.Index, accepted, state);
            else
                Assert.Throws<IOException>(() => LiveHookEvidence.Require(
                    source.Database, fixture.Base.Resources, fixture.Base.Index, accepted, state));
            Assert.Equal(bytes, new NexusPipeline.Platform.Storage.Journals.JournalStore(source.Instance, new())
                .Read(accepted.Receipt.JournalId).Export());
        }
        finally { _ = await writer.StopAsync(TimeSpan.FromSeconds(15)); }
    }
}
