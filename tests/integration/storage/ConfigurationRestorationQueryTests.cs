using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class ConfigurationRestorationQueryTests
{
    private static readonly Guid ParentJournal = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid ParentOperation = Guid.Parse("30000000-0000-0000-0000-000000000002");
    private static readonly BlobReference Plan = new("plans/expected.json", 1, new('a', 64));

    [Fact(DisplayName = "Restoration Lookup Keeps Sql Work Bounded Across Eight Thousand Unrelated Committed Journals")]
    public void BoundsCommittedHistoryWork()
    {
        using var connection = Open(8192);
        using var transaction = connection.BeginTransaction();
        var progress = 0;
        SQLitePCL.raw.sqlite3_progress_handler(connection.Handle!, 100, _ => { progress++; return 0; }, null);
        try
        {
            Assert.True(Read(connection, transaction));
            Assert.InRange(progress, 0, 10);
        }
        finally { SQLitePCL.raw.sqlite3_progress_handler(connection.Handle!, 0, null, null); }
    }

    [Theory(DisplayName = "Restoration Rejects A Resource Whose Journal Is Bound To Another Plan")]
    [InlineData("path")]
    [InlineData("digest")]
    public void RejectsForeignJournalPlan(string changed)
    {
        using var connection = Open(1);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE main.operation_journals SET plan_path=$path,plan_digest=$digest WHERE journal_id='10000000-0000-0000-0000-000000000001';" +
            "UPDATE main.operation_journals SET " + (changed == "path" ? "plan_path='plans/foreign.json'" : "plan_digest=$foreign") +
            " WHERE journal_id='20000000-0000-0000-0000-000000000001';";
        command.Parameters.AddWithValue("$path", Plan.RelativePath); command.Parameters.AddWithValue("$digest", Plan.Sha256);
        command.Parameters.AddWithValue("$foreign", new string('b', 64)); command.ExecuteNonQuery();
        using var transaction = connection.BeginTransaction();
        Assert.False(Read(connection, transaction));
    }

    private static bool Read(SqliteConnection connection, SqliteTransaction transaction) =>
        ConfigurationRunInventory.IsRestoredInside(connection, transaction,
            new(ParentJournal, ParentOperation, JournalKind.RunAttempt, JournalPhase.Applying, 1, "plans/parent.json", new('c', 64), []),
            [("step", Plan, false)], requireCompletedGroups: true, stepId: "step");

    private static SqliteConnection Open(int history)
    {
        var connection = NexusPipeline.TestFixtures.DatabaseFixtureConnection.Open("Data Source=:memory:");
        using var command = connection.CreateCommand();
        command.CommandText = "ATTACH DATABASE ':memory:' AS configuration;" +
            "CREATE TABLE main.operation_journals(journal_id TEXT PRIMARY KEY,operation_id TEXT,operation_kind TEXT,phase TEXT,plan_path TEXT,plan_digest TEXT,created_at TEXT);" +
            "CREATE INDEX main.operation_journals_phase ON operation_journals(phase,created_at);" +
            "CREATE INDEX main.journals_plan_read ON operation_journals(plan_path,plan_digest);" +
            "CREATE TABLE configuration.configuration_run_sessions(session_id TEXT PRIMARY KEY,owner_operation_id TEXT,parent_journal_id TEXT,step_id TEXT,phase INTEGER,writer_gate INTEGER,closed_elapsed_milliseconds INTEGER,completion_operation_id TEXT);" +
            "CREATE INDEX configuration.configuration_runs_owner_read ON configuration_run_sessions(owner_operation_id,parent_journal_id,step_id);" +
            "CREATE TABLE configuration.configuration_run_resources(session_id TEXT,resource_id TEXT,operation_id TEXT UNIQUE,journal_id TEXT UNIQUE,plan_path TEXT,plan_digest TEXT,PRIMARY KEY(session_id,resource_id));" +
            "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<$history) " +
            "INSERT INTO main.operation_journals SELECT printf('10000000-0000-0000-0000-%012x',x),'history','configExchange','committed','history/'||x,$digest,'0' FROM n;" +
            "INSERT INTO main.operation_journals VALUES('20000000-0000-0000-0000-000000000001','child','configExchange','committed',$path,$digest,'1');" +
            "INSERT INTO configuration.configuration_run_sessions VALUES('session',$owner,$parent,'step',2,1,1,'completion');" +
            "INSERT INTO configuration.configuration_run_resources VALUES('session','resource','child','20000000-0000-0000-0000-000000000001',$path,$digest);";
        command.Parameters.AddWithValue("$history", history); command.Parameters.AddWithValue("$path", Plan.RelativePath);
        command.Parameters.AddWithValue("$digest", Plan.Sha256); command.Parameters.AddWithValue("$owner", ParentOperation.ToString("D"));
        command.Parameters.AddWithValue("$parent", ParentJournal.ToString("D")); command.ExecuteNonQuery();
        return connection;
    }
}
