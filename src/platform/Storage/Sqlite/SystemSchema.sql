CREATE TABLE schema_info (
    singleton INTEGER PRIMARY KEY CHECK(singleton=1),
    format TEXT NOT NULL CHECK(format='nexus.state.domain'),
    schema_version INTEGER NOT NULL CHECK(schema_version=2),
    instance_id TEXT NOT NULL CHECK(length(instance_id)=36),
    domain_name TEXT NOT NULL CHECK(domain_name='system'),
    contract_digest TEXT NOT NULL CHECK(length(contract_digest)=64),
    created_at TEXT NOT NULL
) STRICT;

CREATE TABLE "settings_snapshots" (
    revision INTEGER PRIMARY KEY NOT NULL CHECK(revision BETWEEN 0 AND 9007199254740991),
    auto_start INTEGER NOT NULL CHECK(auto_start IN (0,1)),
    desktop_mode INTEGER NOT NULL CHECK(desktop_mode BETWEEN 0 AND 1),
    minimize_to_tray INTEGER NOT NULL CHECK(minimize_to_tray IN (0,1)),
    open_desktop INTEGER NOT NULL CHECK(open_desktop IN (0,1)),
    web_port INTEGER NOT NULL CHECK(web_port=0 OR web_port BETWEEN 1024 AND 65535),
    remote_access INTEGER NOT NULL CHECK(remote_access IN (0,1)),
    mcp_enabled INTEGER NOT NULL CHECK(mcp_enabled IN (0,1)),
    mcp_write INTEGER NOT NULL CHECK(mcp_write IN (0,1)),
    mcp_port INTEGER NOT NULL CHECK(mcp_port BETWEEN 1024 AND 65535),
    config_repair INTEGER NOT NULL CHECK(config_repair IN (0,1)),
    retention_days INTEGER NOT NULL CHECK(retention_days BETWEEN 1 AND 180),
    log_level INTEGER NOT NULL CHECK(log_level BETWEEN 0 AND 4),
    locale INTEGER NOT NULL CHECK(locale BETWEEN 0 AND 1),
    proxy_mode INTEGER NOT NULL CHECK(proxy_mode BETWEEN 0 AND 2),
    proxy_url TEXT CHECK(proxy_url IS NULL OR length(proxy_url) BETWEEN 1 AND 2048),
    proxy_user TEXT NOT NULL CHECK(length(proxy_user)<=256),
    update_check INTEGER NOT NULL CHECK(update_check IN (0,1)),
    update_auto INTEGER NOT NULL CHECK(update_auto IN (0,1)),
    plugin_auto INTEGER NOT NULL CHECK(plugin_auto IN (0,1)),
    update_channel INTEGER NOT NULL CHECK(update_channel BETWEEN 0 AND 1),
    update_source TEXT CHECK(update_source IS NULL OR length(update_source) BETWEEN 1 AND 2048),
    plugin_source INTEGER NOT NULL CHECK(plugin_source BETWEEN 0 AND 1),
    retry_enabled INTEGER NOT NULL CHECK(retry_enabled IN (0,1)),
    retry_backoff_seconds INTEGER NOT NULL CHECK(retry_backoff_seconds BETWEEN 0 AND 86400)) STRICT;

CREATE TABLE settings_extensions (
    revision INTEGER NOT NULL REFERENCES settings_snapshots(revision),
    extension_id TEXT NOT NULL CHECK(length(extension_id) BETWEEN 1 AND 80),
    enabled INTEGER NOT NULL CHECK(enabled IN (0,1)),
    PRIMARY KEY(revision,extension_id)) STRICT;

CREATE TABLE settings_current (
    singleton INTEGER PRIMARY KEY NOT NULL CHECK(singleton=1),
    revision INTEGER NOT NULL REFERENCES settings_snapshots(revision)) STRICT;

CREATE TABLE settings_mutations (
    operation_id TEXT PRIMARY KEY NOT NULL,
    result_revision INTEGER NOT NULL UNIQUE REFERENCES settings_snapshots(revision)) STRICT;

CREATE TABLE secret_replay_key (
    singleton INTEGER PRIMARY KEY NOT NULL CHECK(singleton=1),
    key_id TEXT NOT NULL UNIQUE CHECK(length(key_id)=36),
    owner_sid TEXT NOT NULL,
    digest TEXT NOT NULL CHECK(length(digest)=64),
    size_bytes INTEGER NOT NULL CHECK(size_bytes BETWEEN 1 AND 4096)) STRICT;

CREATE TABLE secret_payloads (
    secret_id TEXT PRIMARY KEY NOT NULL CHECK(length(secret_id)=36),
    preparation_operation_id TEXT NOT NULL CHECK(length(preparation_operation_id)=36),
    protection_scheme TEXT NOT NULL,
    owner_sid TEXT NOT NULL,
    digest TEXT NOT NULL CHECK(length(digest)=64),
    size_bytes INTEGER NOT NULL CHECK(size_bytes BETWEEN 1 AND 1048576),
    state TEXT NOT NULL CHECK(state IN ('prepared','committed','retired'))) STRICT;

CREATE TABLE secret_fields (
    owner_kind TEXT NOT NULL CHECK(owner_kind IN ('extension','host','notificationTask')),
    owner_id TEXT NOT NULL,
    subject_id TEXT NOT NULL CHECK(length(subject_id)=36),
    field TEXT NOT NULL,
    revision INTEGER NOT NULL CHECK(revision>=0),
    secret_id TEXT UNIQUE REFERENCES secret_payloads(secret_id),
    PRIMARY KEY(owner_kind,owner_id,subject_id,field),
    CHECK(owner_kind!='host' OR (owner_id=subject_id AND field IN ('proxy-password','control-bearer'))),
    CHECK(owner_kind!='notificationTask' OR (length(owner_id)=36 AND field IN
        ('webhook-url','webhook-secret','feishu-app-secret','slack-bot-token','smtp-password')))) STRICT;

CREATE TABLE secret_preparations (
    operation_id TEXT PRIMARY KEY NOT NULL,
    journal_id TEXT NOT NULL UNIQUE,
    candidate_id TEXT NOT NULL UNIQUE REFERENCES secret_payloads(secret_id),
    state INTEGER NOT NULL CHECK(state BETWEEN 0 AND 3),
    replay_proof TEXT NOT NULL CHECK(length(replay_proof)=64)) STRICT;

CREATE TABLE secret_retirements (
    secret_id TEXT PRIMARY KEY NOT NULL REFERENCES secret_payloads(secret_id),
    operation_id TEXT NOT NULL,
    journal_id TEXT UNIQUE,
    state TEXT NOT NULL CHECK(state IN ('planned','unverified')),
    CHECK((state='planned' AND journal_id IS NOT NULL) OR (state='unverified' AND journal_id IS NULL))) STRICT;

CREATE TABLE secret_mutations (
    operation_id TEXT PRIMARY KEY NOT NULL,
    result_revision INTEGER NOT NULL CHECK(result_revision>=0),
    result_secret_id TEXT REFERENCES secret_payloads(secret_id),
    result_readable INTEGER NOT NULL CHECK(result_readable IN (0,1)),
    replay_proof TEXT CHECK(replay_proof IS NULL OR length(replay_proof)=64)) STRICT;

CREATE TABLE startup_registration_current (
    singleton INTEGER PRIMARY KEY CHECK(singleton=1),
    command_json BLOB CHECK(command_json IS NULL OR length(command_json) BETWEEN 1 AND 262144)) STRICT;

CREATE TABLE startup_registration_intents (
    operation_id TEXT PRIMARY KEY NOT NULL REFERENCES settings_mutations(operation_id),
    settings_revision INTEGER NOT NULL UNIQUE REFERENCES settings_snapshots(revision),
    document BLOB NOT NULL CHECK(length(document) BETWEEN 1 AND 262144),
    document_digest TEXT NOT NULL CHECK(length(document_digest)=64),
    phase INTEGER NOT NULL CHECK(phase BETWEEN 1 AND 4)) STRICT;

CREATE UNIQUE INDEX startup_registration_single_pending ON startup_registration_intents((1)) WHERE phase<>3;

CREATE TABLE trusted_update_feeds (
    format TEXT NOT NULL CHECK(format='nexus.update.feed'),
    channel TEXT NOT NULL CHECK(channel IN ('stable','prerelease')),
    sequence INTEGER NOT NULL CHECK(sequence BETWEEN 1 AND 9007199254740991),
    feed_sha256 TEXT NOT NULL CHECK(length(feed_sha256)=64),
    key_id TEXT NOT NULL CHECK(length(key_id) BETWEEN 2 AND 80),
    feed_size INTEGER NOT NULL CHECK(feed_size BETWEEN 1 AND 2097152),
    signature_sha256 TEXT NOT NULL CHECK(length(signature_sha256)=64),
    signature_size INTEGER NOT NULL CHECK(signature_size BETWEEN 1 AND 2097152),
    first_operation_id TEXT NOT NULL UNIQUE,
    PRIMARY KEY(format,channel,sequence),
    UNIQUE(format,channel,sequence,feed_sha256)) STRICT;

CREATE TABLE trusted_update_feed_heads (
    format TEXT NOT NULL CHECK(format='nexus.update.feed'),
    channel TEXT NOT NULL CHECK(channel IN ('stable','prerelease')),
    sequence INTEGER NOT NULL CHECK(sequence BETWEEN 1 AND 9007199254740991),
    feed_sha256 TEXT NOT NULL CHECK(length(feed_sha256)=64),
    PRIMARY KEY(format,channel),
    FOREIGN KEY(format,channel,sequence,feed_sha256)
        REFERENCES trusted_update_feeds(format,channel,sequence,feed_sha256)) STRICT;

CREATE TABLE maintenance_operations (
    operation_id TEXT PRIMARY KEY NOT NULL,
    plan_sha256 TEXT NOT NULL CHECK(length(plan_sha256)=64),
    plan_size INTEGER NOT NULL CHECK(plan_size BETWEEN 1 AND 2097152),
    source_sha256 TEXT NOT NULL CHECK(length(source_sha256)=64),
    source_size INTEGER NOT NULL CHECK(source_size BETWEEN 1 AND 2097152),
    state INTEGER NOT NULL CHECK(state BETWEEN 0 AND 4),
    handoff_id TEXT UNIQUE CHECK(handoff_id IS NULL OR length(handoff_id)=36),
    terminal_code TEXT CHECK(terminal_code IS NULL OR length(terminal_code) BETWEEN 1 AND 80),
    CHECK((state IN (0,3))=(handoff_id IS NULL)),
    CHECK((state IN (2,3,4))=(terminal_code IS NOT NULL)),
    CHECK(state<>3 OR terminal_code='cancelled-before-handoff')) STRICT;

CREATE UNIQUE INDEX maintenance_single_active ON maintenance_operations((1)) WHERE state IN (0,1,4);

CREATE TABLE secret_document_publications (
    operation_id TEXT PRIMARY KEY,
    extension_id TEXT NOT NULL,
    document_name TEXT NOT NULL,
    relative_path TEXT NOT NULL,
    size_bytes INTEGER NOT NULL CHECK(size_bytes BETWEEN 1 AND 1048576),
    sha256 TEXT NOT NULL CHECK(length(sha256)=64)) STRICT;

CREATE TABLE secret_document_fields (
    parent_operation_id TEXT NOT NULL REFERENCES secret_document_publications(operation_id),
    child_operation_id TEXT NOT NULL UNIQUE,
    subject_id TEXT NOT NULL,
    field TEXT NOT NULL,
    PRIMARY KEY(parent_operation_id,subject_id,field)) STRICT;
