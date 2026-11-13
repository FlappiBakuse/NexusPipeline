using Microsoft.Data.Sqlite;
using NexusPipeline.Platform.Storage.Configuration;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class ConfigurationCapacityTests
{
    [Theory(DisplayName = "Configuration History Pages Keep Every Session Beyond The Active Execution Capacity")]
    [InlineData(128)]
    [InlineData(129)]
    [InlineData(8193)]
    public void ReadsEverySession(int count)
    {
        using var connection = Open(count);
        using var transaction = connection.BeginTransaction();
        var actual = ConfigurationRunInventory.ReadSessions(connection, transaction).ToArray();
        var expected = Enumerable.Range(1, count).Select(index => Guid.Parse($"10000000-0000-0000-0000-{index:x12}")).ToArray();
        Assert.Equal(expected, actual.Select(item => item.Id));
        Assert.All(actual, item => { Assert.Equal("capacity", item.Subject); Assert.Equal("configuration.run.begin", item.Kind); Assert.Equal("completed", item.Status); });
    }

    [Fact(DisplayName = "A Missing Operation Beyond The First Sixty Four Pages Is Rejected Without Dropping Its Session")]
    public void RejectsUnknownTail()
    {
        using var connection = Open(8193);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM operations WHERE operation_id='10000000-0000-0000-0000-000000002001';";
        Assert.Equal(1, command.ExecuteNonQuery());
        using var transaction = connection.BeginTransaction();
        Assert.Equal("configuration_run_operation_unbound", Assert.Throws<IOException>(() =>
            ConfigurationRunInventory.ReadSessions(connection, transaction).ToArray()).Message);
        command.Transaction = transaction; command.CommandText = "SELECT count(*) FROM configuration_run_sessions;";
        Assert.Equal(8193L, (long)command.ExecuteScalar()!);
    }

    private static SqliteConnection Open(int count)
    {
        var connection = new SqliteConnection("Data Source=:memory:"); connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE operations(operation_id TEXT PRIMARY KEY,subject_id TEXT,operation_kind TEXT,status TEXT);" +
            "CREATE TABLE configuration_run_sessions(session_id TEXT PRIMARY KEY);" +
            "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<$count) " +
            "INSERT INTO operations SELECT printf('10000000-0000-0000-0000-%012x',x),'capacity','configuration.run.begin','completed' FROM n;" +
            "INSERT INTO configuration_run_sessions SELECT operation_id FROM operations;";
        command.Parameters.AddWithValue("$count", count); command.ExecuteNonQuery();
        return connection;
    }
}
