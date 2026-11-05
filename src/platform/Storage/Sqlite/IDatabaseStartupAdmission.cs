namespace NexusPipeline.Platform.Storage.Sqlite;

public interface IDatabaseStartupAdmission
{
    void Verify(DatabaseInspection inspection);
}
