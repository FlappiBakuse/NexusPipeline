namespace NexusPipeline.Platform.Storage.Sqlite;

internal static class EffectEpochSchema
{
    internal const string Sql = """

        CREATE TABLE work_effect_epochs (
            claim_id TEXT NOT NULL REFERENCES work_effect_claims(claim_id),
            ordinal INTEGER NOT NULL CHECK(ordinal BETWEEN 1 AND 4096),
            operation_id TEXT NOT NULL REFERENCES operations(operation_id),
            provider_id TEXT NOT NULL,
            task_id TEXT NOT NULL,
            run_id TEXT NOT NULL,
            attempt_id TEXT NOT NULL,
            secret_id TEXT NOT NULL,
            secret_revision INTEGER NOT NULL CHECK(secret_revision>0),
            PRIMARY KEY(claim_id,ordinal),
            UNIQUE(claim_id,operation_id)
        ) STRICT;
        CREATE TABLE work_effect_epoch_results (
            claim_id TEXT NOT NULL,
            ordinal INTEGER NOT NULL,
            operation_id TEXT NOT NULL REFERENCES operations(operation_id),
            run_id TEXT NOT NULL,
            attempt_id TEXT NOT NULL,
            PRIMARY KEY(claim_id,ordinal),
            FOREIGN KEY(claim_id,ordinal) REFERENCES work_effect_epochs(claim_id,ordinal)
        ) STRICT;
        """;

}
