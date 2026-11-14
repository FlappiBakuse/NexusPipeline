CREATE TABLE schema_info (
    singleton INTEGER PRIMARY KEY CHECK(singleton=1),
    format TEXT NOT NULL CHECK(format='nexus.state.domain'),
    schema_version INTEGER NOT NULL CHECK(schema_version=2),
    instance_id TEXT NOT NULL CHECK(length(instance_id)=36),
    domain_name TEXT NOT NULL CHECK(domain_name='notifications'),
    contract_digest TEXT NOT NULL CHECK(length(contract_digest)=64),
    created_at TEXT NOT NULL
) STRICT;

CREATE TABLE notification_catalog_current (
    singleton INTEGER PRIMARY KEY CHECK(singleton=1),
    revision INTEGER NOT NULL CHECK(revision BETWEEN 1 AND 9007199254740991),
    relative_path TEXT NOT NULL,
    size_bytes INTEGER NOT NULL CHECK(size_bytes BETWEEN 1 AND 2097152),
    sha256 TEXT NOT NULL CHECK(length(sha256)=64)) STRICT;

CREATE TABLE notification_task_ids (
    task_id TEXT PRIMARY KEY CHECK(length(task_id)=36),
    first_operation_id TEXT NOT NULL) STRICT;

CREATE TABLE notification_publications (
    operation_id TEXT PRIMARY KEY,
    relative_path TEXT NOT NULL,
    size_bytes INTEGER NOT NULL CHECK(size_bytes BETWEEN 1 AND 2097152),
    sha256 TEXT NOT NULL CHECK(length(sha256)=64),
    before_revision INTEGER NOT NULL CHECK(before_revision BETWEEN 0 AND 9007199254740990),
    after_revision INTEGER NOT NULL CHECK(after_revision=before_revision+1)) STRICT;

CREATE TABLE notification_publication_fields (
    parent_operation_id TEXT NOT NULL REFERENCES notification_publications(operation_id),
    field TEXT NOT NULL CHECK(field IN ('webhook-url','webhook-secret','feishu-app-secret','slack-bot-token','smtp-password')),
    child_operation_id TEXT NOT NULL UNIQUE,
    PRIMARY KEY(parent_operation_id,field)) STRICT;

CREATE TABLE notification_publication_outcomes (
    operation_id TEXT PRIMARY KEY REFERENCES notification_publications(operation_id),
    outcome TEXT NOT NULL CHECK(outcome IN ('published','aborted')),
    catalog_revision INTEGER UNIQUE,
    CHECK((outcome='published' AND catalog_revision BETWEEN 1 AND 9007199254740991) OR
          (outcome='aborted' AND catalog_revision IS NULL))) STRICT;

CREATE TABLE notification_deliveries (
    operation_id TEXT PRIMARY KEY,
    acceptance_id TEXT NOT NULL CHECK(length(acceptance_id)=36),
    source_attempt_id TEXT,
    task_id TEXT NOT NULL CHECK(length(task_id)=36),
    relative_path TEXT NOT NULL,
    size_bytes INTEGER NOT NULL CHECK(size_bytes BETWEEN 1 AND 2097152),
    sha256 TEXT NOT NULL CHECK(length(sha256)=64)) STRICT;

CREATE INDEX ix_notification_deliveries_acceptance ON notification_deliveries(acceptance_id);

CREATE TABLE notification_delivery_secrets (
    operation_id TEXT NOT NULL REFERENCES notification_deliveries(operation_id),
    field TEXT NOT NULL CHECK(field IN ('webhook-url','webhook-secret','feishu-app-secret','slack-bot-token','smtp-password')),
    revision INTEGER NOT NULL CHECK(revision BETWEEN 0 AND 9007199254740991),
    secret_id TEXT,
    PRIMARY KEY(operation_id,field)) STRICT;

CREATE TABLE notification_delivery_assets (
    operation_id TEXT PRIMARY KEY REFERENCES notification_deliveries(operation_id),
    run_id TEXT NOT NULL,
    digest TEXT NOT NULL,
    kind INTEGER NOT NULL CHECK(kind=0),
    size_bytes INTEGER NOT NULL CHECK(size_bytes BETWEEN 0 AND 1073741824)) STRICT;

CREATE TABLE notification_delivery_transitions (
    operation_id TEXT NOT NULL REFERENCES notification_deliveries(operation_id),
    ordinal INTEGER NOT NULL CHECK(ordinal BETWEEN 0 AND 2),
    document_json TEXT NOT NULL CHECK(length(document_json) BETWEEN 1 AND 2097152),
    PRIMARY KEY(operation_id,ordinal)) STRICT;

CREATE TABLE notification_catalog_tasks (
    task_id TEXT PRIMARY KEY REFERENCES notification_task_ids(task_id),
    revision INTEGER NOT NULL CHECK(revision BETWEEN 1 AND 9007199254740991),
    relative_path TEXT NOT NULL,
    size_bytes INTEGER NOT NULL CHECK(size_bytes BETWEEN 1 AND 2097152),
    sha256 TEXT NOT NULL CHECK(length(sha256)=64)) STRICT;

CREATE TABLE notification_receipt_sources (
    operation_id TEXT PRIMARY KEY,
    run_id TEXT NOT NULL UNIQUE,
    attempt_id TEXT NOT NULL UNIQUE,
    route_count INTEGER NOT NULL CHECK(route_count BETWEEN 0 AND 4096),
    relative_path TEXT NOT NULL,
    size_bytes INTEGER NOT NULL CHECK(size_bytes BETWEEN 1 AND 2097152),
    sha256 TEXT NOT NULL CHECK(length(sha256)=64)) STRICT;

CREATE TABLE notification_receipt_progress (
    operation_id TEXT PRIMARY KEY REFERENCES notification_receipt_sources(operation_id),
    next_ordinal INTEGER NOT NULL CHECK(next_ordinal BETWEEN 0 AND 4096),
    route_count INTEGER NOT NULL CHECK(route_count BETWEEN 0 AND 4096),
    CHECK(next_ordinal<=route_count)) STRICT;

CREATE INDEX ix_notification_receipt_pending ON notification_receipt_progress(operation_id) WHERE next_ordinal<route_count;

CREATE TABLE notification_receipt_secrets (
    operation_id TEXT NOT NULL REFERENCES notification_receipt_sources(operation_id),
    task_id TEXT NOT NULL CHECK(length(task_id)=36),
    field TEXT NOT NULL CHECK(field IN ('webhook-url','webhook-secret','feishu-app-secret','slack-bot-token','smtp-password')),
    revision INTEGER NOT NULL CHECK(revision BETWEEN 0 AND 9007199254740991),
    secret_id TEXT,
    PRIMARY KEY(operation_id,task_id,field)) STRICT;

CREATE TABLE notification_receipt_routes (
    operation_id TEXT NOT NULL REFERENCES notification_receipt_sources(operation_id),
    ordinal INTEGER NOT NULL CHECK(ordinal BETWEEN 0 AND 4095),
    relative_path TEXT NOT NULL,
    size_bytes INTEGER NOT NULL CHECK(size_bytes BETWEEN 1 AND 2097152),
    sha256 TEXT NOT NULL CHECK(length(sha256)=64),
    PRIMARY KEY(operation_id,ordinal)) STRICT;

CREATE TABLE notification_receipt_deliveries (
    operation_id TEXT NOT NULL,
    route_ordinal INTEGER NOT NULL,
    delivery_operation_id TEXT NOT NULL UNIQUE REFERENCES notification_deliveries(operation_id),
    PRIMARY KEY(operation_id,route_ordinal,delivery_operation_id),
    FOREIGN KEY(operation_id,route_ordinal) REFERENCES notification_receipt_routes(operation_id,ordinal)) STRICT;

CREATE TABLE notification_delivery_pending (
    operation_id TEXT PRIMARY KEY REFERENCES notification_deliveries(operation_id),
    source_operation_id TEXT,
    state TEXT NOT NULL CHECK(state IN ('queued','sending','unknown'))) STRICT;

CREATE INDEX notification_delivery_pending_state ON notification_delivery_pending(state,operation_id);

CREATE INDEX notification_delivery_pending_source ON notification_delivery_pending(source_operation_id,state,operation_id);

CREATE TABLE notification_saved_tests (
    operation_id TEXT PRIMARY KEY,
    delivery_operation_id TEXT NOT NULL UNIQUE REFERENCES notification_deliveries(operation_id) DEFERRABLE INITIALLY DEFERRED,
    relative_path TEXT NOT NULL,
    size_bytes INTEGER NOT NULL CHECK(size_bytes BETWEEN 1 AND 2097152),
    sha256 TEXT NOT NULL CHECK(length(sha256)=64)) STRICT;

CREATE TABLE notification_scheduled_sources(
    operation_id TEXT PRIMARY KEY NOT NULL,
    occurrence_id TEXT NOT NULL UNIQUE,
    relative_path TEXT NOT NULL,
    size_bytes INTEGER NOT NULL CHECK(size_bytes BETWEEN 1 AND 2097152),
    sha256 TEXT NOT NULL) STRICT;

CREATE TABLE notification_scheduled_deliveries(
    operation_id TEXT NOT NULL REFERENCES notification_scheduled_sources(operation_id),
    task_id TEXT NOT NULL,
    delivery_operation_id TEXT NOT NULL UNIQUE REFERENCES notification_deliveries(operation_id) DEFERRABLE INITIALLY DEFERRED,
    PRIMARY KEY(operation_id,task_id)) STRICT;

CREATE TABLE notification_manual_sources (
    operation_id TEXT PRIMARY KEY,
    invocation_operation_id TEXT NOT NULL UNIQUE,
    relative_path TEXT NOT NULL,
    size_bytes INTEGER NOT NULL CHECK(size_bytes BETWEEN 1 AND 2097152),
    sha256 TEXT NOT NULL CHECK(length(sha256)=64)) STRICT;

CREATE TABLE notification_manual_deliveries (
    operation_id TEXT NOT NULL REFERENCES notification_manual_sources(operation_id),
    task_id TEXT NOT NULL,
    delivery_operation_id TEXT NOT NULL UNIQUE REFERENCES notification_deliveries(operation_id) DEFERRABLE INITIALLY DEFERRED,
    PRIMARY KEY(operation_id,task_id)) STRICT;
