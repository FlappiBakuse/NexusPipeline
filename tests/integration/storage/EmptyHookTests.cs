using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class EmptyHookTests
{
    [Theory(DisplayName = "A Real Unregistered Empty Hook Job Can Be Released Without Publishing Any Stop Proof")]
    [InlineData(false)]
    [InlineData(true)]
    public void ReleasesEmptyHooks(bool configuration)
    {
        using var fixture = new ExecutionHooksFixture(configuration);
        using var accepted = fixture.Base.Take(fixture.Accepted.Acceptance.OperationId);
        var source = fixture.Source;
        var step = Assert.Single(accepted.Prepared.Total.Plan.ReadWire().Steps);
        var parent = new StepWriterScope(source.Instance, source.Database, new(), source.Lease,
            accepted.Receipt.JournalId, step.StepId, "preHook");
        IConfigurationWriterScope scope = parent;
        if (configuration)
        {
            var prepared = accepted.Prepared.Configurations[step.StepId];
            var id = Guid.NewGuid();
            var runs = new ConfigurationRunStore(source.Instance, source.Database, new());
            runs.Begin(ConfigurationRunStore.BeginIdentity(id, ExecutionHooksFixture.Subject, accepted.Receipt.JournalId, step.StepId, prepared),
                accepted.Receipt.JournalId, step.StepId, prepared, DateTimeOffset.UtcNow);
            scope = new AttemptWriterScope(parent,
                new(source.Instance, source.Database, new(), id, ExecutionHooksFixture.Subject, parent));
        }
        using var writer = new ConfigurationWriterProcess(source.Instance, source.Lease, scope, deferRegistration: true);
        var before = source.Snapshot();
        writer.ReleaseEmptyJob();
        var after = source.Snapshot();
        Assert.Equal(before.Rows, after.Rows); Assert.Equal(before.Files, after.Files);
        Assert.Empty(parent.VerifyReadOnly().Jobs);
        Assert.Equal(0, fixture.Base.Count("journal_writer_jobs"));
        Assert.Equal(0, fixture.Base.Count("journal_writer_job_roots"));
        Assert.Equal(0, fixture.Base.Count("journal_writer_job_closures"));
        Assert.False(File.Exists(fixture.Marker + ".pre"));
    }

    [Fact(DisplayName = "A Real Registered Empty Job Cannot Use The Unregistered Release Path And Still Needs Its Native Closure")]
    public async Task RequiresRegisteredClosure()
    {
        using var fixture = new StepWriterFixture();
        var before = fixture.Base.Source.Snapshot();
        var error = Assert.Throws<IOException>(() => fixture.Writer.ReleaseEmptyJob());
        Assert.Equal("execution_unregistered_job_release_unverified", error.Message);
        var after = fixture.Base.Source.Snapshot();
        Assert.Equal(before.Rows, after.Rows); Assert.Equal(before.Files, after.Files);
        var stopped = await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10));
        fixture.AssertClosed(stopped, executed: false);
        Assert.Null(stopped.Closure.Root); Assert.Empty(stopped.Closure.Family);
    }
}
