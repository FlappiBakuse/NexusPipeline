namespace NexusPipeline.Platform.Storage.Sqlite;

internal static class EffectRotationSchema
{
    internal const string Sql = """

        CREATE TABLE work_effect_rotations (
            claim_id TEXT NOT NULL REFERENCES work_effect_claims(claim_id),
            operation_id TEXT NOT NULL REFERENCES secret_document_publications(operation_id),
            source_operation_id TEXT NOT NULL REFERENCES operations(operation_id),
            run_id TEXT NOT NULL,
            attempt_id TEXT NOT NULL,
            before_secret_id TEXT NOT NULL,
            before_revision INTEGER NOT NULL CHECK(before_revision>0),
            after_secret_id TEXT NOT NULL,
            after_revision INTEGER NOT NULL CHECK(after_revision=before_revision+1),
            PRIMARY KEY(claim_id,operation_id),
            CHECK(before_secret_id<>after_secret_id)
        ) STRICT;
        """;
}
