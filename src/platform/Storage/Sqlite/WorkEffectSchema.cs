namespace NexusPipeline.Platform.Storage.Sqlite;

internal static class WorkEffectSchema
{
    internal const string Sql = """

        CREATE TABLE work_identity_versions (
            extension_id TEXT NOT NULL,
            field TEXT NOT NULL,
            identity_version TEXT NOT NULL,
            PRIMARY KEY(extension_id,field)
        ) STRICT;
        CREATE TABLE work_effect_claims (
            claim_id TEXT PRIMARY KEY,
            extension_id TEXT NOT NULL,
            field TEXT NOT NULL,
            activity_id TEXT NOT NULL,
            local_date TEXT NOT NULL,
            family_sha256 TEXT NOT NULL CHECK(length(family_sha256)=64),
            parent_id TEXT REFERENCES work_effect_claims(claim_id),
            role_id TEXT NOT NULL,
            server_id TEXT NOT NULL,
            operation_id TEXT NOT NULL REFERENCES operations(operation_id),
            provider_id TEXT NOT NULL,
            task_id TEXT NOT NULL,
            run_id TEXT NOT NULL,
            attempt_id TEXT NOT NULL,
            secret_id TEXT NOT NULL,
            secret_revision INTEGER NOT NULL CHECK(secret_revision>0),
            UNIQUE(extension_id,field,activity_id,local_date,family_sha256,role_id,server_id),
            CHECK((parent_id IS NULL AND role_id='' AND server_id='') OR (parent_id IS NOT NULL AND role_id<>''))
        ) STRICT;
        CREATE TABLE work_effect_events (
            claim_id TEXT NOT NULL REFERENCES work_effect_claims(claim_id),
            sequence INTEGER NOT NULL CHECK(sequence>0),
            operation_id TEXT NOT NULL REFERENCES operations(operation_id),
            run_id TEXT NOT NULL,
            attempt_id TEXT NOT NULL,
            kind TEXT NOT NULL CHECK(kind IN ('acquired','submitted','confirmed')),
            evidence_code TEXT,
            PRIMARY KEY(claim_id,sequence),
            CHECK((kind='confirmed' AND evidence_code IS NOT NULL) OR (kind<>'confirmed' AND evidence_code IS NULL))
        ) STRICT;
        CREATE INDEX work_effect_children ON work_effect_claims(parent_id);
        """;
}
