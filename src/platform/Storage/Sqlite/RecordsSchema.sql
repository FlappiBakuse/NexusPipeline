CREATE TABLE schema_info (
    singleton INTEGER PRIMARY KEY CHECK(singleton=1),
    format TEXT NOT NULL CHECK(format='nexus.state.domain'),
    schema_version INTEGER NOT NULL CHECK(schema_version=2),
    instance_id TEXT NOT NULL CHECK(length(instance_id)=36),
    domain_name TEXT NOT NULL CHECK(domain_name='records'),
    contract_digest TEXT NOT NULL CHECK(length(contract_digest)=64),
    created_at TEXT NOT NULL
) STRICT;

CREATE TABLE run_records (
    run_id TEXT PRIMARY KEY NOT NULL CHECK(length(run_id) = 36),
    automation_id TEXT NOT NULL CHECK(length(automation_id) = 36),
    user_id TEXT CHECK(user_id IS NULL OR length(user_id) = 36),
    ended_at_utc_ticks INTEGER NOT NULL CHECK(ended_at_utc_ticks > 0),
    duration_milliseconds INTEGER CHECK(duration_milliseconds BETWEEN 0 AND 31536000000),
    attempt_id TEXT NOT NULL UNIQUE CHECK(length(attempt_id) = 36),
    attempt_ordinal INTEGER NOT NULL CHECK(attempt_ordinal >= 1),
    engine_outcome INTEGER NOT NULL CHECK(engine_outcome BETWEEN 0 AND 5),
    business_outcome INTEGER NOT NULL CHECK(business_outcome BETWEEN 0 AND 5),
    lifecycle_outcome INTEGER NOT NULL CHECK(lifecycle_outcome BETWEEN 2 AND 6),
    recovery_outcome INTEGER NOT NULL CHECK(recovery_outcome IN (0,2,3)),
    reason_code TEXT NOT NULL CHECK(length(reason_code) BETWEEN 1 AND 80),
    duration_kind INTEGER NOT NULL CHECK(duration_kind BETWEEN 0 AND 2),
    end_time_kind INTEGER NOT NULL CHECK(end_time_kind BETWEEN 0 AND 2),
    history_retired_at_utc_ticks INTEGER CHECK(history_retired_at_utc_ticks>0),
    CHECK((duration_kind=2)=(duration_milliseconds IS NULL)),
    CHECK((duration_kind=0)=(end_time_kind=0))) STRICT;

CREATE INDEX run_records_ended ON run_records(ended_at_utc_ticks DESC,run_id DESC);

CREATE INDEX run_records_automation_ended ON run_records(automation_id,ended_at_utc_ticks DESC,run_id DESC);

CREATE INDEX run_records_user_ended ON run_records(user_id,ended_at_utc_ticks DESC,run_id DESC);

CREATE INDEX run_records_business_ended ON run_records(business_outcome,ended_at_utc_ticks DESC,run_id DESC);

CREATE TABLE account_run_records (
    ended_at_utc_ticks INTEGER NOT NULL,
    run_id TEXT NOT NULL REFERENCES run_records(run_id),
    user_id TEXT NOT NULL CHECK(length(user_id)=36),
    engine_outcome INTEGER NOT NULL CHECK(engine_outcome BETWEEN 0 AND 5),
    business_outcome INTEGER NOT NULL CHECK(business_outcome BETWEEN 0 AND 5),
    lifecycle_outcome INTEGER NOT NULL CHECK(lifecycle_outcome BETWEEN 2 AND 6),
    reason_code TEXT NOT NULL CHECK(length(reason_code) BETWEEN 1 AND 80),
    PRIMARY KEY(run_id,user_id)) STRICT;

CREATE INDEX account_run_records_user ON account_run_records(user_id,ended_at_utc_ticks,run_id);

CREATE VIEW account_history AS
    SELECT a.run_id,r.automation_id,a.user_id,a.ended_at_utc_ticks,r.duration_milliseconds,r.attempt_id,r.attempt_ordinal,
        a.engine_outcome,a.business_outcome,a.lifecycle_outcome,r.recovery_outcome,a.reason_code,r.duration_kind,r.end_time_kind,r.history_retired_at_utc_ticks
    FROM run_records r JOIN account_run_records a ON a.run_id=r.run_id
    UNION ALL SELECT r.* FROM run_records r WHERE NOT EXISTS(SELECT 1 FROM account_run_records a WHERE a.run_id=r.run_id);

CREATE TABLE binding_task_records (
    run_id TEXT NOT NULL REFERENCES run_records(run_id),
    binding_id TEXT NOT NULL CHECK(length(binding_id)=36),
    user_id TEXT NOT NULL CHECK(length(user_id)=36),
    script_id TEXT NOT NULL CHECK(length(script_id)=36),
    tone TEXT NOT NULL CHECK(tone IN ('bad','warn','muted','ok')),
    reason TEXT NOT NULL CHECK(reason IN ('latest','no_task_report')),
    PRIMARY KEY(run_id,binding_id)) STRICT;

CREATE INDEX binding_task_records_owner ON binding_task_records(binding_id,user_id,script_id,run_id);

CREATE TABLE attempt_receipts (
    attempt_id TEXT PRIMARY KEY NOT NULL CHECK(length(attempt_id)=36),
    run_id TEXT NOT NULL CHECK(length(run_id)=36),
    ordinal INTEGER NOT NULL CHECK(ordinal BETWEEN 1 AND 99),
    journal_id TEXT NOT NULL UNIQUE,
    operation_id TEXT NOT NULL,
    plan_digest TEXT NOT NULL CHECK(length(plan_digest)=64),
    source_receipt_digest TEXT NOT NULL CHECK(length(source_receipt_digest)=64),
    engine_outcome INTEGER NOT NULL CHECK(engine_outcome BETWEEN 0 AND 5),
    business_outcome INTEGER NOT NULL CHECK(business_outcome BETWEEN 0 AND 5),
    lifecycle_outcome INTEGER NOT NULL CHECK(lifecycle_outcome BETWEEN 2 AND 6),
    recovery_outcome INTEGER NOT NULL CHECK(recovery_outcome IN (0,2,3)),
    reason_code TEXT NOT NULL CHECK(length(reason_code) BETWEEN 1 AND 80),
    UNIQUE(run_id,ordinal)) STRICT;

CREATE TABLE record_assets (
    run_id TEXT NOT NULL REFERENCES run_records(run_id),
    digest TEXT NOT NULL,
    kind INTEGER NOT NULL CHECK(kind BETWEEN 0 AND 2),
    PRIMARY KEY(run_id,digest,kind)) STRICT;

CREATE INDEX record_assets_digest ON record_assets(digest);

CREATE TABLE execution_log_catalog(
    attempt_id TEXT PRIMARY KEY NOT NULL REFERENCES attempt_receipts(attempt_id),
    run_id TEXT NOT NULL,
    journal_id TEXT NOT NULL UNIQUE,
    terminal_revision INTEGER NOT NULL CHECK(terminal_revision BETWEEN 0 AND 4095),
    terminal_sha256 TEXT NOT NULL,
    entry_count INTEGER NOT NULL CHECK(entry_count BETWEEN 0 AND 4096)) STRICT;

CREATE INDEX execution_log_catalog_run ON execution_log_catalog(run_id,attempt_id);

CREATE TABLE "execution_log_entries"(
    attempt_id TEXT NOT NULL REFERENCES execution_log_catalog(attempt_id),
    position INTEGER NOT NULL CHECK(position BETWEEN 0 AND 4095),
    step_id TEXT NOT NULL,
    role INTEGER NOT NULL CHECK(role BETWEEN 0 AND 2),
    raw_relative_path TEXT NOT NULL,
    raw_size_bytes INTEGER NOT NULL CHECK(raw_size_bytes BETWEEN 1 AND 2097152),
    raw_sha256 TEXT NOT NULL,
    observed_bytes INTEGER NOT NULL CHECK(observed_bytes BETWEEN 0 AND 1099511627776),
    recorded_bytes INTEGER NOT NULL CHECK(recorded_bytes BETWEEN 0 AND 67108864),
    discarded_bytes INTEGER NOT NULL CHECK(discarded_bytes>=0 AND observed_bytes-discarded_bytes=recorded_bytes),
    is_complete INTEGER NOT NULL CHECK(is_complete IN (0,1)),
    part_count INTEGER NOT NULL CHECK(part_count BETWEEN 0 AND 4096),
    PRIMARY KEY(attempt_id,position),
    UNIQUE(attempt_id,step_id,role)) STRICT;

CREATE TABLE record_content_retirements(
    operation_id TEXT PRIMARY KEY,
    run_id TEXT NOT NULL REFERENCES run_records(run_id),
    family_digest TEXT NOT NULL CHECK(length(family_digest)=64),
    cleanup_journal_id TEXT NOT NULL UNIQUE,
    relative_path TEXT NOT NULL,
    size_bytes INTEGER NOT NULL CHECK(size_bytes BETWEEN 1 AND 2097152),
    sha256 TEXT NOT NULL CHECK(length(sha256)=64)) STRICT;

CREATE INDEX record_content_retirements_run ON record_content_retirements(run_id,operation_id);

CREATE TABLE record_retired_contents(
    operation_id TEXT NOT NULL REFERENCES record_content_retirements(operation_id),
    run_id TEXT NOT NULL REFERENCES run_records(run_id),
    digest TEXT NOT NULL CHECK(length(digest)=64),
    content_kind INTEGER NOT NULL CHECK(content_kind IN(0,1)),
    size_bytes INTEGER NOT NULL CHECK(
        (content_kind=0 AND size_bytes BETWEEN 0 AND 1048576) OR
        (content_kind=1 AND size_bytes BETWEEN 57 AND 8388608)),
    relative_path TEXT NOT NULL,
    PRIMARY KEY(operation_id,digest),
    UNIQUE(run_id,digest)) STRICT;

CREATE INDEX record_retired_contents_run_digest ON record_retired_contents(run_id,digest,operation_id);

CREATE TABLE execution_image_catalog(
    attempt_id TEXT PRIMARY KEY NOT NULL REFERENCES attempt_receipts(attempt_id),
    run_id TEXT NOT NULL,
    journal_id TEXT NOT NULL UNIQUE,
    terminal_revision INTEGER NOT NULL CHECK(terminal_revision BETWEEN 1 AND 4096),
    terminal_sha256 TEXT NOT NULL CHECK(length(terminal_sha256)=64),
    entry_count INTEGER NOT NULL CHECK(entry_count BETWEEN 0 AND 4096)) STRICT;

CREATE INDEX execution_image_catalog_run ON execution_image_catalog(run_id,attempt_id);

CREATE TABLE "execution_image_entries"(
    attempt_id TEXT NOT NULL REFERENCES execution_image_catalog(attempt_id),
    position INTEGER NOT NULL CHECK(position BETWEEN 0 AND 4095),
    step_id TEXT NOT NULL,
    capture_id TEXT NOT NULL CHECK(length(capture_id)=36),
    width INTEGER CHECK(width BETWEEN 1 AND 8192),
    height INTEGER CHECK(height BETWEEN 1 AND 8192 AND width*height<=33554432),
    outcome INTEGER NOT NULL CHECK(outcome IN (0,1)),
    reason_code TEXT NOT NULL,
    source_path TEXT NOT NULL,
    source_size INTEGER NOT NULL CHECK(source_size BETWEEN 1 AND 2097152),
    source_sha256 TEXT NOT NULL CHECK(length(source_sha256)=64),
    root_path TEXT,
    root_size INTEGER CHECK(root_size BETWEEN 1 AND 2097152),
    root_sha256 TEXT CHECK(length(root_sha256)=64),
    result_path TEXT NOT NULL,
    result_size INTEGER NOT NULL CHECK(result_size BETWEEN 1 AND 2097152),
    result_sha256 TEXT NOT NULL CHECK(length(result_sha256)=64),
    png_path TEXT,
    png_size INTEGER CHECK(png_size BETWEEN 57 AND 8388608),
    png_sha256 TEXT CHECK(length(png_sha256)=64),
    PRIMARY KEY(attempt_id,position),
    UNIQUE(attempt_id,capture_id),
    CHECK((width IS NULL)=(height IS NULL) AND (outcome<>0 OR width IS NOT NULL)),
    CHECK((root_path IS NULL)=(root_size IS NULL) AND (root_path IS NULL)=(root_sha256 IS NULL)),
    CHECK((png_path IS NULL)=(png_size IS NULL) AND (png_path IS NULL)=(png_sha256 IS NULL)),
    CHECK((outcome=0)=(png_path IS NOT NULL) AND (png_path IS NULL OR root_path IS NOT NULL))) STRICT;

CREATE INDEX execution_image_entries_png ON execution_image_entries(png_sha256,attempt_id) WHERE png_sha256 IS NOT NULL;

CREATE TABLE execution_image_asset_catalog(
    run_id TEXT PRIMARY KEY NOT NULL REFERENCES run_records(run_id),
    final_attempt_id TEXT NOT NULL REFERENCES attempt_receipts(attempt_id),
    family_sha256 TEXT NOT NULL CHECK(length(family_sha256)=64),
    image_count INTEGER NOT NULL CHECK(image_count BETWEEN 0 AND 405504)) STRICT;
