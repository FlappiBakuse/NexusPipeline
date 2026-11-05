using System.Runtime.Versioning;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class EvidenceContextTests
{
    [Theory(DisplayName = "A Real Collected Proof Cannot Hide Changed Sql Root Or Job Registration From The Captured Context")]
    [InlineData("root")]
    [InlineData("jobs")]
    public async Task RechecksSqlContext(string field)
    {
        using var fixture = new StepWriterFixture("tail",
            judgement: new(AutomationJudgementLanguage.Keywords, ["ready"], [], null));
        var source = fixture.Base.Source;
        var initial = fixture.Scope.VerifyReadOnly();
        var binding = StepResultCodec.Binding(initial.Latest.Root, Assert.Single(initial.Steps));
        using var collector = new StepEvidenceCollector(source.Instance, source.Database, new(), source.Lease, binding, fixture.Writer);
        fixture.Scope.EnterApplying();
        var root = collector.Start();
        Assert.Equal(37, await fixture.Writer.AwaitRootAsync(TimeSpan.FromSeconds(10)));
        await collector.ObserveExitAsync();
        var stopped = await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10));
        fixture.AssertClosed(stopped, executed: true);
        Assert.Equal(root.ProcessId, stopped.Closure.Root!.ProcessId);
        var proof = await collector.CollectAsync(stopped, TimeSpan.FromSeconds(10));
        var terminal = new AttemptTerminalStore(source.Instance, source.Database, new(), source.Lease);
        terminal.RecordCollected(fixture.JournalId, proof);
        var evidence = proof.Use(binding, value => value);
        Assert.Equal(EngineOutcome.Failed, evidence.Engine);
        Assert.Equal(BusinessOutcome.Succeeded, evidence.Business);
        var before = fixture.Scope.VerifyReadOnly();
        Assert.Equal(evidence.Artifact, Assert.Single(StepResultCodec.Read(before.Latest.Root, before.Total)).Latest.Artifact);

        var original = ChangeSqlField(fixture, binding, field);
        try
        {
            var damaged = source.Snapshot();
            var failure = Assert.Throws<IOException>(() =>
                AttemptWriterState.Read(source.Instance, source.Database, new(), fixture.JournalId));
            Assert.Equal("execution_step_evidence_unbound", failure.Message);
            var after = source.Snapshot();
            Assert.Equal(damaged.Rows, after.Rows);
            Assert.Equal(damaged.Files, after.Files);
        }
        finally { ChangeSqlField(fixture, binding, field, original); }

        var restored = fixture.Scope.VerifyReadOnly();
        Assert.Equal(before.Latest.Export(), restored.Latest.Export());
        Assert.Equal(evidence, new StepEvidenceReader(source.Instance, source.Database, new()).Read(binding, evidence.Artifact, true));
        Assert.Equal(evidence, proof.Use(binding, value => value));
    }

    private static long ChangeSqlField(StepWriterFixture fixture, StepEvidenceBinding binding, string field, long? replacement = null)
    {
        var (table, column) = field switch
        {
            "root" => ("journal_writer_job_roots", "created_file_time"),
            "jobs" => ("journal_writer_jobs", "session_id"),
            _ => throw new ArgumentException("fixture_field_unrecognized", nameof(field))
        };
        return fixture.Base.Source.Database.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"SELECT {column} FROM {table} WHERE journal_id=$journal AND writer_id=$writer AND job_name=$job;";
            command.Parameters.AddWithValue("$journal", fixture.JournalId.ToString("D"));
            command.Parameters.AddWithValue("$writer", binding.WriterId.ToString("D"));
            command.Parameters.AddWithValue("$job", binding.JobName);
            var previous = Convert.ToInt64(command.ExecuteScalar());
            command.CommandText = $"UPDATE {table} SET {column}=$value WHERE journal_id=$journal AND writer_id=$writer AND job_name=$job;";
            command.Parameters.AddWithValue("$value", replacement ?? checked(previous + 1));
            Assert.Equal(1, command.ExecuteNonQuery());
            return previous;
        });
    }
}
