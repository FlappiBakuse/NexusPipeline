CREATE TABLE schema_info (
    singleton INTEGER PRIMARY KEY CHECK(singleton=1),
    format TEXT NOT NULL CHECK(format='nexus.state.domain'),
    schema_version INTEGER NOT NULL CHECK(schema_version=2),
    instance_id TEXT NOT NULL CHECK(length(instance_id)=36),
    domain_name TEXT NOT NULL CHECK(domain_name='configuration'),
    contract_digest TEXT NOT NULL CHECK(length(contract_digest)=64),
    created_at TEXT NOT NULL
) STRICT;

CREATE TABLE configuration_revisions (
    binding_id TEXT NOT NULL CHECK(length(binding_id)=36),
    resource_id TEXT NOT NULL CHECK(length(resource_id) BETWEEN 1 AND 120),
    revision INTEGER NOT NULL CHECK(revision BETWEEN 1 AND 9007199254740991),
    user_id TEXT NOT NULL CHECK(length(user_id)=36),
    automation_id TEXT NOT NULL CHECK(length(automation_id)=36),
    contract_revision INTEGER NOT NULL CHECK(contract_revision BETWEEN 1 AND 9007199254740991),
    contract_digest TEXT NOT NULL CHECK(length(contract_digest)=64),
    instance_key TEXT CHECK(instance_key IS NULL OR length(instance_key) BETWEEN 1 AND 256),
    requires_instance INTEGER NOT NULL CHECK(requires_instance IN (0,1)),
    manifest_path TEXT NOT NULL,
    manifest_digest TEXT NOT NULL CHECK(length(manifest_digest)=64),
    manifest_size INTEGER NOT NULL CHECK(manifest_size BETWEEN 1 AND 2097152),
    PRIMARY KEY(binding_id,resource_id,revision)) STRICT;

CREATE TABLE configuration_current (
    binding_id TEXT NOT NULL,
    resource_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    PRIMARY KEY(binding_id,resource_id),
    FOREIGN KEY(binding_id,resource_id,revision) REFERENCES configuration_revisions(binding_id,resource_id,revision)) STRICT;

CREATE TABLE configuration_revision_assets (
    binding_id TEXT NOT NULL,
    resource_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    digest TEXT NOT NULL,
    PRIMARY KEY(binding_id,resource_id,revision,digest),
    FOREIGN KEY(binding_id,resource_id,revision) REFERENCES configuration_revisions(binding_id,resource_id,revision)) STRICT;

CREATE INDEX configuration_revision_assets_digest ON configuration_revision_assets(digest);

CREATE TABLE configuration_mutations (
    operation_id TEXT PRIMARY KEY NOT NULL,
    binding_id TEXT NOT NULL,
    resource_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    has_changes INTEGER NOT NULL CHECK(has_changes IN (0,1)),
    source_request_digest TEXT CHECK(source_request_digest IS NULL OR length(source_request_digest)=64),
    FOREIGN KEY(binding_id,resource_id,revision) REFERENCES configuration_revisions(binding_id,resource_id,revision)) STRICT;

CREATE TABLE configuration_batch_results (
    operation_id TEXT PRIMARY KEY NOT NULL,
    resource_count INTEGER NOT NULL CHECK(resource_count BETWEEN 1 AND 32),
    source_request_digest TEXT CHECK(source_request_digest IS NULL OR length(source_request_digest)=64)) STRICT;

CREATE TABLE configuration_batch_items (
    operation_id TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal BETWEEN 0 AND 31),
    binding_id TEXT NOT NULL,
    resource_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    has_changes INTEGER NOT NULL CHECK(has_changes IN (0,1)),
    PRIMARY KEY(operation_id,ordinal),
    UNIQUE(operation_id,binding_id,resource_id),
    FOREIGN KEY(binding_id,resource_id,revision) REFERENCES configuration_revisions(binding_id,resource_id,revision)) STRICT;

CREATE TABLE configuration_edit_sessions (
    session_id TEXT PRIMARY KEY NOT NULL,
    revision INTEGER NOT NULL CHECK(revision BETWEEN 1 AND 9007199254740991),
    phase INTEGER NOT NULL CHECK(phase BETWEEN 0 AND 3),
    binding_id TEXT NOT NULL CHECK(length(binding_id)=36),
    expected_binding_revision INTEGER NOT NULL CHECK(expected_binding_revision BETWEEN 1 AND 9007199254740991),
    completion_operation_id TEXT UNIQUE,
    source_request_digest TEXT CHECK(source_request_digest IS NULL OR (length(source_request_digest)=64 AND source_request_digest NOT GLOB '*[^0-9a-f]*')),
    writer_gate INTEGER NOT NULL DEFAULT 0 CHECK(writer_gate IN (0,1)),
    CHECK((phase IN (0,1) AND completion_operation_id IS NULL) OR (phase IN (2,3) AND completion_operation_id IS NOT NULL))) STRICT;

CREATE TABLE configuration_edit_resources (
    session_id TEXT NOT NULL REFERENCES configuration_edit_sessions(session_id),
    resource_id TEXT NOT NULL CHECK(length(resource_id) BETWEEN 1 AND 120),
    expected_revision INTEGER NOT NULL CHECK(expected_revision BETWEEN 0 AND 9007199254740990),
    contract_revision INTEGER NOT NULL CHECK(contract_revision BETWEEN 1 AND 9007199254740991),
    contract_digest TEXT NOT NULL CHECK(length(contract_digest)=64),
    instance_key TEXT CHECK(instance_key IS NULL OR length(instance_key) BETWEEN 1 AND 256),
    requires_instance INTEGER NOT NULL CHECK(requires_instance IN (0,1)),
    journal_id TEXT NOT NULL UNIQUE,
    plan_path TEXT NOT NULL,
    plan_digest TEXT NOT NULL CHECK(length(plan_digest)=64),
    PRIMARY KEY(session_id,resource_id)) STRICT;

CREATE UNIQUE INDEX configuration_edit_one_open_binding ON configuration_edit_sessions(binding_id) WHERE phase IN (0,1);

CREATE TABLE configuration_run_sessions (
    session_id TEXT PRIMARY KEY NOT NULL,
    owner_operation_id TEXT NOT NULL,
    parent_journal_id TEXT NOT NULL,
    step_id TEXT NOT NULL CHECK(length(step_id) BETWEEN 1 AND 120),
    binding_id TEXT NOT NULL CHECK(length(binding_id)=36),
    expected_binding_revision INTEGER NOT NULL CHECK(expected_binding_revision BETWEEN 1 AND 9007199254740991),
    revision INTEGER NOT NULL CHECK(revision BETWEEN 1 AND 9007199254740991),
    phase INTEGER NOT NULL CHECK(phase IN (0,1,2,3)),
    writer_gate INTEGER NOT NULL DEFAULT 0 CHECK(writer_gate IN (0,1)),
    sync_ordinal INTEGER NOT NULL DEFAULT 0 CHECK(sync_ordinal BETWEEN 0 AND 9007199254740990),
    completion_operation_id TEXT,
    entered_running INTEGER NOT NULL DEFAULT 0 CHECK(entered_running IN (0,1)),
    closed_elapsed_milliseconds INTEGER CHECK(closed_elapsed_milliseconds BETWEEN 0 AND 922337203685477),
    primary_job_name TEXT CHECK(primary_job_name IS NULL OR length(primary_job_name)=50),
    invocation_ordinal INTEGER NOT NULL DEFAULT 1 CHECK(invocation_ordinal BETWEEN 1 AND 99),
    UNIQUE(parent_journal_id,step_id,invocation_ordinal)) STRICT;

CREATE TABLE configuration_run_resources (
    session_id TEXT NOT NULL REFERENCES configuration_run_sessions(session_id),
    resource_id TEXT NOT NULL CHECK(length(resource_id) BETWEEN 1 AND 120),
    operation_id TEXT NOT NULL UNIQUE,
    journal_id TEXT NOT NULL UNIQUE,
    expected_revision INTEGER NOT NULL CHECK(expected_revision BETWEEN 1 AND 9007199254740990),
    current_revision INTEGER NOT NULL CHECK(current_revision BETWEEN 1 AND 9007199254740991),
    contract_revision INTEGER NOT NULL CHECK(contract_revision BETWEEN 1 AND 9007199254740991),
    contract_digest TEXT NOT NULL CHECK(length(contract_digest)=64),
    instance_key TEXT CHECK(instance_key IS NULL OR length(instance_key) BETWEEN 1 AND 256),
    requires_instance INTEGER NOT NULL CHECK(requires_instance IN (0,1)),
    plan_path TEXT NOT NULL,
    plan_digest TEXT NOT NULL CHECK(length(plan_digest)=64),
    PRIMARY KEY(session_id,resource_id)) STRICT;

CREATE TABLE configuration_run_syncs (
    session_id TEXT NOT NULL REFERENCES configuration_run_sessions(session_id),
    ordinal INTEGER NOT NULL CHECK(ordinal BETWEEN 1 AND 9007199254740991),
    operation_id TEXT NOT NULL UNIQUE,
    elapsed_milliseconds INTEGER NOT NULL CHECK(elapsed_milliseconds BETWEEN 0 AND 922337203685477),
    PRIMARY KEY(session_id,ordinal)) STRICT;

CREATE TABLE configuration_run_sync_items (
    operation_id TEXT NOT NULL,
    resource_id TEXT NOT NULL,
    previous_revision INTEGER NOT NULL CHECK(previous_revision>=1),
    current_revision INTEGER NOT NULL CHECK(current_revision>=previous_revision),
    reason_code TEXT NOT NULL CHECK(length(reason_code) BETWEEN 1 AND 80),
    PRIMARY KEY(operation_id,resource_id)) STRICT;
