using Microsoft.Data.Sqlite;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.StorageTests;

internal static class ScheduleCapacityHistory
{
    internal static void Seed(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = DatabaseConnections.Command(connection, transaction, """
            WITH RECURSIVE numbers(n) AS (VALUES(1) UNION ALL SELECT n+1 FROM numbers WHERE n<12000)
            INSERT INTO main.operations(operation_id,subject_id,request_digest,operation_kind,status,created_at)
            SELECT printf('923cebdf-3954-448f-b378-%012d',n),'fixture:schedule-history',printf('%064d',n),
                   'execution.start','completed','2026-01-01T00:00:00Z' FROM numbers;
            WITH groups(n) AS (VALUES(1),(2),(3))
            INSERT INTO main.operations(operation_id,subject_id,request_digest,operation_kind,status,created_at)
            SELECT printf('923cebdf-3954-448f-b379-%012d',n),'fixture:schedule-history',printf('%064d',n),
                   'schedule.minute','completed','2026-01-01T00:00:00Z' FROM groups;
            WITH groups(n) AS (VALUES(1),(2),(3))
            INSERT INTO main.schedule_minute_claims
            SELECT printf('923cebdf-3954-448f-b379-%012d',n),'{}',4096,n+100,n FROM groups;
            WITH RECURSIVE numbers(n) AS (VALUES(1) UNION ALL SELECT n+1 FROM numbers WHERE n<12000)
            INSERT INTO main.scheduled_occurrences
            SELECT printf('923cebdf-3954-448f-b380-%012d',n),printf('923cebdf-3954-448f-b381-%012d',n),
                   printf('923cebdf-3954-448f-b378-%012d',n),printf('923cebdf-3954-448f-b382-%012d',n),
                   '2026-01-01',0,(n-1)/4096+1,printf('923cebdf-3954-448f-b379-%012d',(n-1)/4096+1),(n-1)%4096
            FROM numbers;
            """);
        command.ExecuteNonQuery();
    }
}
