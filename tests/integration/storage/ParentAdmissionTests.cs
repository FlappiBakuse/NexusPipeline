using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ParentAdmissionTests
{
    [Theory(DisplayName = "Actual Parent Access Rejects Changed Sql Native Facts Or Copied Bindings Before Calling Its Native Callback")]
    [InlineData("sql-pid")]
    [InlineData("sql-created")]
    [InlineData("sql-image")]
    [InlineData("sql-path")]
    [InlineData("sql-sid")]
    [InlineData("sql-elevated")]
    [InlineData("sql-session")]
    [InlineData("creator-image")]
    [InlineData("binding-run")]
    [InlineData("binding-attempt")]
    [InlineData("binding-writer")]
    [InlineData("binding-plan")]
    [InlineData("binding-session")]
    [InlineData("binding-step")]
    public async Task RechecksNativeFacts(string fault)
    {
        using var fixture = new StepWriterFixture();
        var native = await fixture.StartAsync();
        try
        {
            var state = fixture.Scope.VerifyReadOnly();
            var binding = StepResultCodec.Binding(state.Latest.Root, Assert.Single(state.Steps));
            Assert.Equal(native, fixture.Writer.UsePrimaryParent(binding, (_, root) => root));
            var candidate = fault switch
            {
                "binding-run" => binding with { RunId = Guid.NewGuid() },
                "binding-attempt" => binding with { AttemptId = Guid.NewGuid() },
                "binding-writer" => binding with { WriterId = Guid.NewGuid() },
                "binding-plan" => binding with { PlanDigest = new string('0', 64) },
                "binding-session" => binding with { SessionId = binding.SessionId + 1 },
                "binding-step" => binding with { StepId = "foreign-step" },
                _ => binding
            };
            var restore = fault.StartsWith("sql-", StringComparison.Ordinal) || fault == "creator-image"
                ? Corrupt(fixture, binding, fault) : static () => { };
            var called = false;
            try
            {
                Assert.Throws<IOException>(() => fixture.Writer.UsePrimaryParent(candidate, (_, _) => { called = true; return true; }));
                Assert.False(called);
            }
            finally { restore(); }
            Assert.Equal(native, fixture.Writer.UsePrimaryParent(binding, (_, root) => root));
        }
        finally
        {
            var stopped = await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10));
            fixture.AssertClosed(stopped, executed: true);
        }
    }

    private static Action Corrupt(StepWriterFixture fixture, StepEvidenceBinding binding, string fault)
    {
        var (table, column) = fault switch
        {
            "sql-pid" => ("journal_writer_job_roots", "process_id"),
            "sql-created" => ("journal_writer_job_roots", "created_file_time"),
            "sql-image" => ("journal_writer_job_roots", "image_sha256"),
            "sql-path" => ("journal_writer_job_roots", "image_path"),
            "sql-sid" => ("journal_writer_job_roots", "user_sid"),
            "sql-elevated" => ("journal_writer_job_roots", "elevated"),
            "sql-session" => ("journal_writer_jobs", "session_id"),
            "creator-image" => ("journal_writers", "image_sha256"),
            _ => throw new ArgumentException("Unknown controlled fault")
        };
        using var connection = Open(fixture); using var command = connection.CreateCommand();
        var where = " WHERE journal_id=$journal AND writer_id=$writer" + (table == "journal_writers" ? ";" : " AND job_name=$job;");
        command.Parameters.AddWithValue("$journal", binding.ParentJournalId.ToString("D"));
        command.Parameters.AddWithValue("$writer", binding.WriterId.ToString("D")); command.Parameters.AddWithValue("$job", binding.JobName);
        command.CommandText = "SELECT " + column + " FROM " + table + where;
        var original = command.ExecuteScalar() ?? throw new IOException("controlled_root_missing");
        object changed = column switch
        {
            "process_id" or "created_file_time" or "session_id" => Convert.ToInt64(original) + 1,
            "elevated" => 1 - Convert.ToInt64(original),
            "image_sha256" => new string('0', 64),
            _ => (string)original + "-changed"
        };
        command.CommandText = "UPDATE " + table + " SET " + column + "=$value" + where;
        command.Parameters.AddWithValue("$value", changed); Assert.Equal(1, command.ExecuteNonQuery());
        return () =>
        {
            using var restore = Open(fixture); using var update = restore.CreateCommand();
            update.CommandText = "UPDATE " + table + " SET " + column + "=$value" + where;
            update.Parameters.AddWithValue("$journal", binding.ParentJournalId.ToString("D"));
            update.Parameters.AddWithValue("$writer", binding.WriterId.ToString("D")); update.Parameters.AddWithValue("$job", binding.JobName);
            update.Parameters.AddWithValue("$value", original); Assert.Equal(1, update.ExecuteNonQuery());
        };
    }

    private static SqliteConnection Open(StepWriterFixture fixture)
    {
        var connection = NexusPipeline.TestFixtures.DatabaseFixtureConnection.Open(new SqliteConnectionStringBuilder
        { DataSource = fixture.Base.Source.Database.DatabasePath, Pooling = false }.ToString());
        connection.Open(); return connection;
    }
}
