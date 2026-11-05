using Microsoft.Data.Sqlite;

namespace NexusPipeline.Platform.Storage.Sqlite;

public enum CheckpointMode { Passive, Truncate }
public sealed record DatabaseCheckpoint(int Busy, int LogFrames, int CheckpointedFrames);

internal static class DatabaseCheckpointReader
{
    internal static DatabaseCheckpoint Execute(SqliteConnection connection, CheckpointMode mode)
    {
        using var command = connection.CreateCommand();
        command.CommandText = mode switch
        {
            CheckpointMode.Passive => "PRAGMA wal_checkpoint(PASSIVE);",
            CheckpointMode.Truncate => "PRAGMA wal_checkpoint(TRUNCATE);",
            _ => throw new ArgumentException("checkpoint_mode_invalid")
        };
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new IOException("checkpoint_result_missing");
        return new(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
    }
}
