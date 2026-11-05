CREATE TABLE schema_info (
    singleton INTEGER PRIMARY KEY CHECK(singleton = 1),
    format TEXT NOT NULL CHECK(format = 'nexus.state.database'),
    schema_version INTEGER NOT NULL CHECK(schema_version = 1),
    instance_id TEXT NOT NULL CHECK(length(instance_id) = 36),
    contract_digest TEXT NOT NULL CHECK(length(contract_digest) = 64),
    created_at TEXT NOT NULL
) STRICT;

CREATE TABLE operations (
    operation_id TEXT PRIMARY KEY NOT NULL CHECK(length(operation_id) = 36),
    subject_id TEXT NOT NULL CHECK(length(subject_id) BETWEEN 1 AND 256),
    request_digest TEXT NOT NULL CHECK(length(request_digest) = 64),
    operation_kind TEXT NOT NULL CHECK(length(operation_kind) BETWEEN 1 AND 120),
    status TEXT NOT NULL CHECK(status IN ('accepted', 'completed')),
    created_at TEXT NOT NULL,
    initial_journal_id TEXT REFERENCES operation_journals(journal_id)
) STRICT;

CREATE TABLE operation_journals (
    journal_id TEXT PRIMARY KEY NOT NULL CHECK(length(journal_id) = 36),
    operation_id TEXT NOT NULL REFERENCES operations(operation_id),
    operation_kind TEXT NOT NULL CHECK(operation_kind IN ('configExchange','configEdit','fileCleanup','runAttempt','updateApply','extensionApply','assetPrepare','secretPrepare')),
    phase TEXT NOT NULL CHECK(phase IN ('prepared','applying','applied','reverting','reverted','committed','quarantined')),
    revision INTEGER NOT NULL CHECK(revision >= 1),
    plan_path TEXT NOT NULL,
    plan_digest TEXT NOT NULL CHECK(length(plan_digest) = 64),
    created_at TEXT NOT NULL
) STRICT;

CREATE INDEX operation_journals_phase ON operation_journals(phase,created_at);

CREATE TABLE journal_writers (
    journal_id TEXT NOT NULL REFERENCES operation_journals(journal_id),
    writer_id TEXT NOT NULL CHECK(length(writer_id)=36),
    process_id INTEGER NOT NULL CHECK(process_id>0),
    created_file_time INTEGER NOT NULL CHECK(created_file_time>0),
    image_path TEXT NOT NULL CHECK(length(image_path) BETWEEN 1 AND 32767),
    image_sha256 TEXT NOT NULL CHECK(length(image_sha256)=64),
    user_sid TEXT NOT NULL CHECK(length(user_sid) BETWEEN 1 AND 256),
    elevated INTEGER NOT NULL CHECK(elevated IN (0,1)),
    PRIMARY KEY(journal_id,writer_id)
) STRICT;

CREATE TABLE journal_writer_jobs (
    journal_id TEXT NOT NULL,
    writer_id TEXT NOT NULL,
    job_name TEXT NOT NULL CHECK(length(job_name)=50),
    session_id INTEGER NOT NULL CHECK(session_id>=0),
    PRIMARY KEY(journal_id,writer_id,job_name),
    FOREIGN KEY(journal_id,writer_id) REFERENCES journal_writers(journal_id,writer_id)
) STRICT;

CREATE TABLE journal_effect_gates (
    journal_id TEXT PRIMARY KEY NOT NULL REFERENCES operation_journals(journal_id),
    state TEXT NOT NULL CHECK(state IN ('unentered','entered','sealed')),
    revision INTEGER NOT NULL CHECK(revision BETWEEN 1 AND 9007199254740991)
) STRICT;

CREATE TABLE journal_writer_job_roots (
    journal_id TEXT NOT NULL,
    writer_id TEXT NOT NULL,
    job_name TEXT NOT NULL,
    process_id INTEGER NOT NULL CHECK(process_id>0),
    created_file_time INTEGER NOT NULL CHECK(created_file_time>0),
    image_path TEXT NOT NULL,
    image_sha256 TEXT NOT NULL CHECK(length(image_sha256)=64),
    user_sid TEXT NOT NULL,
    elevated INTEGER NOT NULL CHECK(elevated IN (0,1)),
    PRIMARY KEY(journal_id,writer_id,job_name),
    FOREIGN KEY(journal_id,writer_id,job_name) REFERENCES journal_writer_jobs(journal_id,writer_id,job_name)
) STRICT;
CREATE TABLE execution_target_command_jobs (
    journal_id TEXT NOT NULL,
    command_id TEXT NOT NULL CHECK(length(command_id)=36),
    step_id TEXT NOT NULL CHECK(length(step_id) BETWEEN 1 AND 80),
    command_sequence INTEGER NOT NULL CHECK(command_sequence BETWEEN 0 AND 1023),
    job_name TEXT NOT NULL CHECK(length(job_name)=50),
    writer_id TEXT NOT NULL,
    document_json TEXT NOT NULL CHECK(length(document_json) BETWEEN 1 AND 2097152),
    PRIMARY KEY(journal_id,command_id),
    UNIQUE(journal_id,command_id,command_sequence),
    UNIQUE(journal_id,job_name),
    FOREIGN KEY(journal_id,writer_id) REFERENCES journal_writers(journal_id,writer_id)
) STRICT;

CREATE TABLE execution_target_job_roots (
    journal_id TEXT NOT NULL,
    target_id TEXT NOT NULL CHECK(length(target_id)=36),
    step_id TEXT NOT NULL CHECK(length(step_id) BETWEEN 1 AND 80),
    job_name TEXT NOT NULL CHECK(length(job_name)=50),
    writer_id TEXT NOT NULL,
    session_id INTEGER NOT NULL CHECK(session_id>=0),
    total_plan_sha256 TEXT NOT NULL CHECK(length(total_plan_sha256)=64),
    process_id INTEGER NOT NULL CHECK(process_id>0),
    created_file_time INTEGER NOT NULL CHECK(created_file_time>0),
    image_path TEXT NOT NULL CHECK(length(image_path) BETWEEN 1 AND 32767),
    image_sha256 TEXT NOT NULL CHECK(length(image_sha256)=64),
    user_sid TEXT NOT NULL CHECK(length(user_sid) BETWEEN 1 AND 256),
    elevated INTEGER NOT NULL CHECK(elevated IN (0,1)),
    command_sequence INTEGER CHECK(command_sequence BETWEEN 0 AND 1023),
    PRIMARY KEY(journal_id,target_id),
    UNIQUE(journal_id,job_name),
    FOREIGN KEY(journal_id,writer_id) REFERENCES journal_writers(journal_id,writer_id),
    FOREIGN KEY(journal_id,target_id,command_sequence)
        REFERENCES execution_target_command_jobs(journal_id,command_id,command_sequence)
) STRICT;
CREATE UNIQUE INDEX execution_pc_target_one_root
    ON execution_target_job_roots(journal_id,step_id) WHERE command_sequence IS NULL;
CREATE UNIQUE INDEX execution_android_command_one_root
    ON execution_target_job_roots(journal_id,step_id,command_sequence) WHERE command_sequence IS NOT NULL;

CREATE TABLE journal_writer_job_closures (
    journal_id TEXT NOT NULL,
    writer_id TEXT NOT NULL,
    job_name TEXT NOT NULL,
    plan_path TEXT NOT NULL,
    plan_digest TEXT NOT NULL CHECK(length(plan_digest)=64),
    plan_size INTEGER NOT NULL CHECK(plan_size BETWEEN 1 AND 2097152),
    PRIMARY KEY(journal_id,writer_id,job_name),
    FOREIGN KEY(journal_id,writer_id,job_name) REFERENCES journal_writer_jobs(journal_id,writer_id,job_name)
) STRICT;

CREATE TABLE journal_resources (
    journal_id TEXT NOT NULL REFERENCES operation_journals(journal_id),
    resource_key TEXT NOT NULL CHECK(length(resource_key) BETWEEN 1 AND 512),
    PRIMARY KEY(journal_id,resource_key)
) STRICT;

CREATE TABLE journal_resource_claims (
    journal_id TEXT NOT NULL,
    resource_key TEXT NOT NULL,
    mode INTEGER NOT NULL CHECK(mode IN (0,1)),
    PRIMARY KEY(journal_id,resource_key),
    FOREIGN KEY(journal_id,resource_key) REFERENCES journal_resources(journal_id,resource_key)
) STRICT;

CREATE TABLE file_effects (
    journal_id TEXT NOT NULL REFERENCES operation_journals(journal_id),
    step_id TEXT NOT NULL CHECK(length(step_id) BETWEEN 1 AND 80),
    action TEXT NOT NULL CHECK(action IN ('preserveOriginal','installCandidate','retireCandidate','restoreOriginal','removeOwned')),
    state TEXT NOT NULL CHECK(state IN ('pending','started','completed','notExecuted','quarantined','reverted')),
    intent_digest TEXT NOT NULL CHECK(length(intent_digest) = 64),
    receipt_digest TEXT CHECK(receipt_digest IS NULL OR length(receipt_digest) = 64),
    PRIMARY KEY(journal_id,step_id)
) STRICT;

CREATE TRIGGER file_effect_reverted_insert
BEFORE INSERT ON file_effects WHEN NEW.state='reverted'
BEGIN SELECT RAISE(ABORT,'file_effect_reverted_requires_original'); END;

CREATE TRIGGER file_effect_reverted_update
BEFORE UPDATE OF state ON file_effects
WHEN NEW.state='reverted' AND NOT (
    OLD.state IN ('pending','started') AND NEW.journal_id=OLD.journal_id AND NEW.step_id=OLD.step_id
    AND NEW.action=OLD.action AND NEW.intent_digest=OLD.intent_digest
    AND NEW.action IN ('preserveOriginal','installCandidate') AND NEW.receipt_digest IS NOT NULL
    AND EXISTS(SELECT 1 FROM operation_journals j JOIN journal_effect_gates g USING(journal_id)
        WHERE j.journal_id=NEW.journal_id AND j.operation_kind='extensionApply'
        AND j.phase='reverted' AND g.state='entered'))
BEGIN SELECT RAISE(ABORT,'file_effect_reverted_unbound'); END;

CREATE TRIGGER file_effect_reverted_journal
BEFORE UPDATE OF operation_kind,phase ON operation_journals
WHEN EXISTS(SELECT 1 FROM file_effects f WHERE f.journal_id=OLD.journal_id AND f.state='reverted')
    AND NOT (NEW.operation_kind='extensionApply' AND NEW.phase IN ('reverted','committed'))
BEGIN SELECT RAISE(ABORT,'file_effect_reverted_journal_unbound'); END;

CREATE TABLE resource_isolations (
    resource_key TEXT NOT NULL,
    journal_id TEXT NOT NULL REFERENCES operation_journals(journal_id),
    scope_quality TEXT NOT NULL CHECK(scope_quality IN ('verified','unknown')),
    reason_code TEXT NOT NULL,
    PRIMARY KEY(resource_key,journal_id)
) STRICT;

CREATE INDEX resource_isolations_journal ON resource_isolations(journal_id,resource_key);

CREATE TABLE recovery_index_state (
    singleton INTEGER PRIMARY KEY CHECK(singleton = 1),
    ready INTEGER NOT NULL CHECK(ready IN (0,1)),
    revision INTEGER NOT NULL CHECK(revision >= 1)
) STRICT;

INSERT INTO recovery_index_state VALUES(1,0,1);

CREATE TABLE automation_catalog_state (
    singleton INTEGER PRIMARY KEY CHECK(singleton=1),
    revision INTEGER NOT NULL CHECK(revision BETWEEN 0 AND 9007199254740991)
) STRICT;
INSERT INTO automation_catalog_state VALUES(1,0);

CREATE TABLE automation_roots (
    root_id TEXT PRIMARY KEY NOT NULL CHECK(length(root_id)=36),
    revision INTEGER NOT NULL CHECK(revision BETWEEN 1 AND 9007199254740991),
    name TEXT NOT NULL CHECK(length(name) BETWEEN 1 AND 120),
    kind INTEGER NOT NULL CHECK(kind IN (0,1)),
    path TEXT NOT NULL CHECK(length(path) BETWEEN 1 AND 32767)
) STRICT;

CREATE TABLE "automations" (
    automation_id TEXT PRIMARY KEY NOT NULL CHECK(length(automation_id)=36),
    revision INTEGER NOT NULL CHECK(revision BETWEEN 1 AND 9007199254740991),
    name TEXT NOT NULL CHECK(length(name) BETWEEN 1 AND 120),
    kind INTEGER NOT NULL CHECK(kind IN (0,1)),
    root_id TEXT NOT NULL REFERENCES automation_roots(root_id),
    extension_id TEXT CHECK(extension_id IS NULL OR length(extension_id) BETWEEN 1 AND 80),
    arguments_json TEXT NOT NULL CHECK(length(arguments_json)<=1048576),
    maximum_attempts INTEGER NOT NULL CHECK(maximum_attempts BETWEEN 1 AND 99),
    stall_timeout_seconds INTEGER NOT NULL CHECK(stall_timeout_seconds BETWEEN 0 AND 86400),
    total_timeout_seconds INTEGER NOT NULL CHECK(total_timeout_seconds BETWEEN 0 AND 604800),
    judgement_language INTEGER NOT NULL CHECK(judgement_language BETWEEN 0 AND 2),
    success_keywords_json TEXT NOT NULL CHECK(length(success_keywords_json)<=1048576),
    failure_keywords_json TEXT NOT NULL CHECK(length(failure_keywords_json)<=1048576),
    judgement_script TEXT CHECK(judgement_script IS NULL OR length(judgement_script)<=65536),
    update_configuration_at_finish INTEGER NOT NULL CHECK(update_configuration_at_finish IN (0,1)),
    target_json TEXT CHECK(target_json IS NULL OR length(target_json)<=1048576),
    adapter_resources_json TEXT NOT NULL CHECK(length(adapter_resources_json)<=1048576 AND json_valid(adapter_resources_json) AND json_type(adapter_resources_json)='array'),
    script_order INTEGER NOT NULL DEFAULT 0 CHECK(script_order BETWEEN 0 AND 4095),
    CHECK((kind=0 AND extension_id IS NULL) OR (kind=1 AND extension_id IS NOT NULL AND update_configuration_at_finish=1))
) STRICT;

CREATE TABLE automation_paths (
    automation_id TEXT NOT NULL REFERENCES automations(automation_id),
    role INTEGER NOT NULL CHECK(role BETWEEN 0 AND 2),
    ordinal INTEGER NOT NULL CHECK(ordinal BETWEEN 0 AND 63),
    root_id TEXT NOT NULL REFERENCES automation_roots(root_id),
    relative_path TEXT NOT NULL CHECK(length(relative_path) BETWEEN 1 AND 1024),
    PRIMARY KEY(automation_id,role,ordinal)
) STRICT;

CREATE TABLE automation_run_days_state (
    singleton INTEGER PRIMARY KEY CHECK(singleton=1),
    local_day INTEGER NOT NULL CHECK(local_day BETWEEN 0 AND 3652058)
) STRICT;

CREATE TABLE execution_binding_successes (
    run_id TEXT NOT NULL CHECK(length(run_id)=36),
    binding_id TEXT NOT NULL CHECK(length(binding_id)=36),
    journal_id TEXT NOT NULL REFERENCES operation_journals(journal_id),
    started_at_utc_ticks INTEGER NOT NULL CHECK(started_at_utc_ticks>0),
    PRIMARY KEY(run_id,binding_id)
) STRICT;
CREATE INDEX execution_binding_successes_day ON execution_binding_successes(binding_id,started_at_utc_ticks);

CREATE TABLE "users" (
    user_id TEXT PRIMARY KEY NOT NULL CHECK(length(user_id)=36),
    revision INTEGER NOT NULL CHECK(revision BETWEEN 1 AND 9007199254740991),
    name TEXT NOT NULL CHECK(length(name) BETWEEN 1 AND 120),
    user_order INTEGER NOT NULL CHECK(user_order BETWEEN 0 AND 4095),
    binding_overrides_json TEXT NOT NULL CHECK(json_valid(binding_overrides_json) AND json_type(binding_overrides_json)='object'),
    notes TEXT NOT NULL DEFAULT '' CHECK(length(notes)<=4000),
    avatar TEXT NOT NULL DEFAULT '' CHECK(length(avatar)<=65536)
) STRICT;

CREATE TABLE bindings (
    binding_id TEXT PRIMARY KEY NOT NULL CHECK(length(binding_id)=36),
    revision INTEGER NOT NULL CHECK(revision BETWEEN 1 AND 9007199254740991),
    user_id TEXT NOT NULL REFERENCES users(user_id),
    automation_id TEXT NOT NULL REFERENCES automations(automation_id),
    enabled INTEGER NOT NULL CHECK(enabled IN (0,1)),
    priority INTEGER NOT NULL CHECK(priority BETWEEN 0 AND 9999),
    pre_hook_json TEXT CHECK(pre_hook_json IS NULL OR json_valid(pre_hook_json) AND json_type(pre_hook_json)='object'),
    post_hook_json TEXT CHECK(post_hook_json IS NULL OR json_valid(post_hook_json) AND json_type(post_hook_json)='object'),
    pre_once_after_success INTEGER NOT NULL DEFAULT 0 CHECK(pre_once_after_success IN (0,1)),
    post_final_only INTEGER NOT NULL DEFAULT 0 CHECK(post_final_only IN (0,1)),
    notify_enabled INTEGER NOT NULL CHECK(notify_enabled IN (0,1)),
    notification_task_ids_json TEXT NOT NULL CHECK(json_valid(notification_task_ids_json) AND json_type(notification_task_ids_json)='array'),
    run_days INTEGER NOT NULL DEFAULT -1 CHECK(run_days BETWEEN -1 AND 36500),
    maximum_successful_runs_per_day INTEGER NOT NULL DEFAULT -1 CHECK(maximum_successful_runs_per_day=-1 OR maximum_successful_runs_per_day BETWEEN 1 AND 9999),
    CHECK(pre_once_after_success=0 OR pre_hook_json IS NOT NULL),
    CHECK(post_final_only=0 OR post_hook_json IS NOT NULL),
    UNIQUE(user_id,automation_id)
) STRICT;

CREATE TABLE "queues" (
    queue_id TEXT PRIMARY KEY NOT NULL CHECK(length(queue_id)=36),
    revision INTEGER NOT NULL CHECK(revision BETWEEN 1 AND 9007199254740991),
    name TEXT NOT NULL CHECK(length(name) BETWEEN 1 AND 120),
    completion_action INTEGER NOT NULL CHECK(completion_action BETWEEN 0 AND 4),
    run_on_startup INTEGER NOT NULL DEFAULT 0 CHECK(run_on_startup IN (0,1)),
    notify_enabled INTEGER NOT NULL CHECK(notify_enabled IN (0,1)),
    notification_task_ids_json TEXT NOT NULL CHECK(json_valid(notification_task_ids_json) AND json_type(notification_task_ids_json)='array'),
    success_dependencies_json TEXT NOT NULL CHECK(json_valid(success_dependencies_json) AND json_type(success_dependencies_json)='array'),
    queue_order INTEGER NOT NULL CHECK(queue_order BETWEEN 0 AND 4095),
    schedule_enabled INTEGER NOT NULL DEFAULT 1 CHECK(schedule_enabled IN (0,1))
) STRICT;

CREATE TABLE queue_items (
    queue_id TEXT NOT NULL REFERENCES queues(queue_id),
    ordinal INTEGER NOT NULL CHECK(ordinal BETWEEN 0 AND 4095),
    automation_id TEXT NOT NULL REFERENCES automations(automation_id),
    PRIMARY KEY(queue_id,ordinal),
    UNIQUE(queue_id,automation_id)
) STRICT;

CREATE TABLE "schedules" (
    schedule_id TEXT PRIMARY KEY NOT NULL CHECK(length(schedule_id)=36),
    revision INTEGER NOT NULL CHECK(revision BETWEEN 1 AND 9007199254740991),
    queue_id TEXT NOT NULL REFERENCES queues(queue_id),
    enabled INTEGER NOT NULL CHECK(enabled IN (0,1)),
    time_zone_id TEXT NOT NULL CHECK(length(time_zone_id) BETWEEN 1 AND 128),
    weekdays_json TEXT NOT NULL CHECK(length(weekdays_json) BETWEEN 1 AND 32),
    local_minute INTEGER NOT NULL CHECK(local_minute BETWEEN 0 AND 1439),
    schedule_order INTEGER NOT NULL DEFAULT 0 CHECK(schedule_order BETWEEN 0 AND 4095)
) STRICT;

CREATE TABLE automation_mutations (
    operation_id TEXT PRIMARY KEY NOT NULL REFERENCES operations(operation_id),
    catalog_revision INTEGER NOT NULL CHECK(catalog_revision BETWEEN 1 AND 9007199254740991)
) STRICT;

CREATE TABLE automation_imports (
    operation_id TEXT PRIMARY KEY NOT NULL REFERENCES operations(operation_id),
    journal_id TEXT NOT NULL UNIQUE REFERENCES operation_journals(journal_id),
    state INTEGER NOT NULL CHECK(state BETWEEN 0 AND 3),
    catalog_revision INTEGER CHECK(catalog_revision IS NULL OR catalog_revision BETWEEN 0 AND 9007199254740991),
    reason_code TEXT NOT NULL CHECK(length(reason_code) BETWEEN 1 AND 80),
    CHECK((state IN (0,3) AND catalog_revision IS NULL) OR (state IN (1,2) AND catalog_revision IS NOT NULL))
) STRICT;

CREATE TABLE automation_assets (
    asset_id TEXT PRIMARY KEY NOT NULL CHECK(length(asset_id)=36),
    import_id TEXT NOT NULL REFERENCES automation_imports(operation_id),
    package_path TEXT NOT NULL CHECK(length(package_path) BETWEEN 1 AND 240),
    digest TEXT NOT NULL REFERENCES blobs(digest),
    UNIQUE(import_id,package_path)
) STRICT;

CREATE TABLE automation_asset_owners (
    asset_id TEXT NOT NULL REFERENCES automation_assets(asset_id),
    automation_id TEXT NOT NULL REFERENCES automations(automation_id),
    PRIMARY KEY(asset_id,automation_id)
) STRICT;
CREATE INDEX automation_assets_digest ON automation_assets(digest);
CREATE INDEX automation_asset_owners_automation ON automation_asset_owners(automation_id);

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
    retry_backoff_seconds INTEGER NOT NULL CHECK(retry_backoff_seconds BETWEEN 0 AND 86400)
) STRICT;

CREATE TABLE settings_extensions (
    revision INTEGER NOT NULL REFERENCES settings_snapshots(revision),
    extension_id TEXT NOT NULL CHECK(length(extension_id) BETWEEN 1 AND 80),
    enabled INTEGER NOT NULL CHECK(enabled IN (0,1)),
    PRIMARY KEY(revision,extension_id)
) STRICT;

CREATE TABLE settings_current (
    singleton INTEGER PRIMARY KEY NOT NULL CHECK(singleton=1),
    revision INTEGER NOT NULL REFERENCES settings_snapshots(revision)
) STRICT;

CREATE TABLE settings_mutations (
    operation_id TEXT PRIMARY KEY NOT NULL REFERENCES operations(operation_id),
    result_revision INTEGER NOT NULL UNIQUE REFERENCES settings_snapshots(revision)
) STRICT;

CREATE TABLE secret_replay_key (
    singleton INTEGER PRIMARY KEY NOT NULL CHECK(singleton=1),
    key_id TEXT NOT NULL UNIQUE CHECK(length(key_id)=36),
    owner_sid TEXT NOT NULL,
    digest TEXT NOT NULL CHECK(length(digest)=64),
    size_bytes INTEGER NOT NULL CHECK(size_bytes BETWEEN 1 AND 4096)
) STRICT;

CREATE TABLE secret_payloads (
    secret_id TEXT PRIMARY KEY NOT NULL CHECK(length(secret_id)=36),
    preparation_operation_id TEXT NOT NULL CHECK(length(preparation_operation_id)=36),
    protection_scheme TEXT NOT NULL,
    owner_sid TEXT NOT NULL,
    digest TEXT NOT NULL CHECK(length(digest)=64),
    size_bytes INTEGER NOT NULL CHECK(size_bytes BETWEEN 1 AND 1048576),
    state TEXT NOT NULL CHECK(state IN ('prepared','committed','retired'))
) STRICT;

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
        ('webhook-url','webhook-secret','feishu-app-secret','slack-bot-token','smtp-password')))
) STRICT;

CREATE TABLE secret_preparations (
    operation_id TEXT PRIMARY KEY NOT NULL REFERENCES operations(operation_id),
    journal_id TEXT NOT NULL UNIQUE REFERENCES operation_journals(journal_id),
    candidate_id TEXT NOT NULL UNIQUE REFERENCES secret_payloads(secret_id),
    state INTEGER NOT NULL CHECK(state BETWEEN 0 AND 3),
    replay_proof TEXT NOT NULL CHECK(length(replay_proof)=64)
) STRICT;

CREATE TABLE secret_retirements (
    secret_id TEXT PRIMARY KEY NOT NULL REFERENCES secret_payloads(secret_id),
    operation_id TEXT NOT NULL REFERENCES operations(operation_id),
    journal_id TEXT UNIQUE REFERENCES operation_journals(journal_id),
    state TEXT NOT NULL CHECK(state IN ('planned','unverified')),
    CHECK((state='planned' AND journal_id IS NOT NULL) OR (state='unverified' AND journal_id IS NULL))
) STRICT;

CREATE TABLE secret_mutations (
    operation_id TEXT PRIMARY KEY NOT NULL REFERENCES operations(operation_id),
    result_revision INTEGER NOT NULL CHECK(result_revision>=0),
    result_secret_id TEXT REFERENCES secret_payloads(secret_id),
    result_readable INTEGER NOT NULL CHECK(result_readable IN (0,1)),
    replay_proof TEXT CHECK(replay_proof IS NULL OR length(replay_proof)=64)
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
    CHECK((duration_kind=0)=(end_time_kind=0))
) STRICT;

CREATE INDEX run_records_ended ON run_records(ended_at_utc_ticks DESC,run_id DESC);
CREATE INDEX run_records_automation_ended ON run_records(automation_id,ended_at_utc_ticks DESC,run_id DESC);
CREATE INDEX run_records_user_ended ON run_records(user_id,ended_at_utc_ticks DESC,run_id DESC);
CREATE INDEX run_records_business_ended ON run_records(business_outcome,ended_at_utc_ticks DESC,run_id DESC);

CREATE TABLE attempt_receipts (
    attempt_id TEXT PRIMARY KEY NOT NULL CHECK(length(attempt_id)=36),
    run_id TEXT NOT NULL CHECK(length(run_id)=36),
    ordinal INTEGER NOT NULL CHECK(ordinal BETWEEN 1 AND 99),
    journal_id TEXT NOT NULL UNIQUE REFERENCES operation_journals(journal_id),
    operation_id TEXT NOT NULL REFERENCES operations(operation_id),
    plan_digest TEXT NOT NULL CHECK(length(plan_digest)=64),
    source_receipt_digest TEXT NOT NULL CHECK(length(source_receipt_digest)=64),
    engine_outcome INTEGER NOT NULL CHECK(engine_outcome BETWEEN 0 AND 5),
    business_outcome INTEGER NOT NULL CHECK(business_outcome BETWEEN 0 AND 5),
    lifecycle_outcome INTEGER NOT NULL CHECK(lifecycle_outcome BETWEEN 2 AND 6),
    recovery_outcome INTEGER NOT NULL CHECK(recovery_outcome IN (0,2,3)),
    reason_code TEXT NOT NULL CHECK(length(reason_code) BETWEEN 1 AND 80),
    UNIQUE(run_id,ordinal)
) STRICT;

CREATE TABLE blobs (
    digest TEXT PRIMARY KEY NOT NULL CHECK(length(digest) = 64),
    size_bytes INTEGER NOT NULL CHECK(size_bytes BETWEEN 0 AND 1073741824),
    relative_path TEXT NOT NULL UNIQUE
) STRICT;

CREATE TABLE record_assets (
    run_id TEXT NOT NULL REFERENCES run_records(run_id),
    digest TEXT NOT NULL REFERENCES blobs(digest),
    kind INTEGER NOT NULL CHECK(kind BETWEEN 0 AND 2),
    PRIMARY KEY(run_id,digest,kind)
) STRICT;
CREATE INDEX record_assets_digest ON record_assets(digest);

CREATE TABLE startup_registration_current (
    singleton INTEGER PRIMARY KEY CHECK(singleton=1),
    command_json BLOB CHECK(command_json IS NULL OR length(command_json) BETWEEN 1 AND 262144)
) STRICT;

CREATE TABLE startup_registration_intents (
    operation_id TEXT PRIMARY KEY NOT NULL REFERENCES settings_mutations(operation_id),
    settings_revision INTEGER NOT NULL UNIQUE REFERENCES settings_snapshots(revision),
    document BLOB NOT NULL CHECK(length(document) BETWEEN 1 AND 262144),
    document_digest TEXT NOT NULL CHECK(length(document_digest)=64),
    phase INTEGER NOT NULL CHECK(phase BETWEEN 1 AND 4)
) STRICT;
CREATE UNIQUE INDEX startup_registration_single_pending ON startup_registration_intents((1)) WHERE phase<>3;

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
    PRIMARY KEY(binding_id,resource_id,revision)
) STRICT;
CREATE TABLE configuration_current (
    binding_id TEXT NOT NULL REFERENCES bindings(binding_id) ON DELETE CASCADE,
    resource_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    PRIMARY KEY(binding_id,resource_id),
    FOREIGN KEY(binding_id,resource_id,revision) REFERENCES configuration_revisions(binding_id,resource_id,revision)
) STRICT;
CREATE TABLE configuration_revision_assets (
    binding_id TEXT NOT NULL,
    resource_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    digest TEXT NOT NULL REFERENCES blobs(digest),
    PRIMARY KEY(binding_id,resource_id,revision,digest),
    FOREIGN KEY(binding_id,resource_id,revision) REFERENCES configuration_revisions(binding_id,resource_id,revision)
) STRICT;
CREATE INDEX configuration_revision_assets_digest ON configuration_revision_assets(digest);
CREATE TABLE configuration_mutations (
    operation_id TEXT PRIMARY KEY NOT NULL REFERENCES operations(operation_id),
    binding_id TEXT NOT NULL,
    resource_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    has_changes INTEGER NOT NULL CHECK(has_changes IN (0,1)),
    source_request_digest TEXT CHECK(source_request_digest IS NULL OR length(source_request_digest)=64),
    FOREIGN KEY(binding_id,resource_id,revision) REFERENCES configuration_revisions(binding_id,resource_id,revision)
) STRICT;

CREATE TABLE configuration_batch_results (
    operation_id TEXT PRIMARY KEY NOT NULL REFERENCES operations(operation_id),
    resource_count INTEGER NOT NULL CHECK(resource_count BETWEEN 1 AND 32),
    source_request_digest TEXT CHECK(source_request_digest IS NULL OR length(source_request_digest)=64)
) STRICT;

CREATE TABLE configuration_batch_items (
    operation_id TEXT NOT NULL REFERENCES operations(operation_id),
    ordinal INTEGER NOT NULL CHECK(ordinal BETWEEN 0 AND 31),
    binding_id TEXT NOT NULL,
    resource_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    has_changes INTEGER NOT NULL CHECK(has_changes IN (0,1)),
    PRIMARY KEY(operation_id,ordinal),
    UNIQUE(operation_id,binding_id,resource_id),
    FOREIGN KEY(binding_id,resource_id,revision) REFERENCES configuration_revisions(binding_id,resource_id,revision)
) STRICT;

CREATE TABLE configuration_edit_sessions (
    session_id TEXT PRIMARY KEY NOT NULL REFERENCES operations(operation_id),
    revision INTEGER NOT NULL CHECK(revision BETWEEN 1 AND 9007199254740991),
    phase INTEGER NOT NULL CHECK(phase BETWEEN 0 AND 3),
    binding_id TEXT NOT NULL CHECK(length(binding_id)=36),
    expected_binding_revision INTEGER NOT NULL CHECK(expected_binding_revision BETWEEN 1 AND 9007199254740991),
    completion_operation_id TEXT UNIQUE REFERENCES operations(operation_id),
    source_request_digest TEXT CHECK(source_request_digest IS NULL OR (length(source_request_digest)=64 AND source_request_digest NOT GLOB '*[^0-9a-f]*')),
    writer_gate INTEGER NOT NULL DEFAULT 0 CHECK(writer_gate IN (0,1)),
    CHECK((phase IN (0,1) AND completion_operation_id IS NULL) OR (phase IN (2,3) AND completion_operation_id IS NOT NULL))
) STRICT;
CREATE TABLE configuration_edit_resources (
    session_id TEXT NOT NULL REFERENCES configuration_edit_sessions(session_id),
    resource_id TEXT NOT NULL CHECK(length(resource_id) BETWEEN 1 AND 120),
    expected_revision INTEGER NOT NULL CHECK(expected_revision BETWEEN 0 AND 9007199254740990),
    contract_revision INTEGER NOT NULL CHECK(contract_revision BETWEEN 1 AND 9007199254740991),
    contract_digest TEXT NOT NULL CHECK(length(contract_digest)=64),
    instance_key TEXT CHECK(instance_key IS NULL OR length(instance_key) BETWEEN 1 AND 256),
    requires_instance INTEGER NOT NULL CHECK(requires_instance IN (0,1)),
    journal_id TEXT NOT NULL UNIQUE REFERENCES operation_journals(journal_id),
    plan_path TEXT NOT NULL,
    plan_digest TEXT NOT NULL CHECK(length(plan_digest)=64),
    PRIMARY KEY(session_id,resource_id)
) STRICT;
CREATE UNIQUE INDEX configuration_edit_one_open_binding ON configuration_edit_sessions(binding_id) WHERE phase IN (0,1);

CREATE TABLE configuration_run_sessions (
    session_id TEXT PRIMARY KEY NOT NULL REFERENCES operations(operation_id),
    owner_operation_id TEXT NOT NULL REFERENCES operations(operation_id),
    parent_journal_id TEXT NOT NULL REFERENCES operation_journals(journal_id),
    step_id TEXT NOT NULL CHECK(length(step_id) BETWEEN 1 AND 120),
    binding_id TEXT NOT NULL CHECK(length(binding_id)=36),
    expected_binding_revision INTEGER NOT NULL CHECK(expected_binding_revision BETWEEN 1 AND 9007199254740991),
    revision INTEGER NOT NULL CHECK(revision BETWEEN 1 AND 9007199254740991),
    phase INTEGER NOT NULL CHECK(phase IN (0,1,2,3)),
    writer_gate INTEGER NOT NULL DEFAULT 0 CHECK(writer_gate IN (0,1)),
    sync_ordinal INTEGER NOT NULL DEFAULT 0 CHECK(sync_ordinal BETWEEN 0 AND 9007199254740990),
    completion_operation_id TEXT REFERENCES operations(operation_id),
    entered_running INTEGER NOT NULL DEFAULT 0 CHECK(entered_running IN (0,1)),
    closed_elapsed_milliseconds INTEGER CHECK(closed_elapsed_milliseconds BETWEEN 0 AND 922337203685477),
    primary_job_name TEXT CHECK(primary_job_name IS NULL OR length(primary_job_name)=50),
    invocation_ordinal INTEGER NOT NULL DEFAULT 1 CHECK(invocation_ordinal BETWEEN 1 AND 99),
    UNIQUE(parent_journal_id,step_id,invocation_ordinal)
) STRICT;
CREATE TABLE configuration_run_resources (
    session_id TEXT NOT NULL REFERENCES configuration_run_sessions(session_id),
    resource_id TEXT NOT NULL CHECK(length(resource_id) BETWEEN 1 AND 120),
    operation_id TEXT NOT NULL UNIQUE REFERENCES operations(operation_id),
    journal_id TEXT NOT NULL UNIQUE REFERENCES operation_journals(journal_id),
    expected_revision INTEGER NOT NULL CHECK(expected_revision BETWEEN 1 AND 9007199254740990),
    current_revision INTEGER NOT NULL CHECK(current_revision BETWEEN 1 AND 9007199254740991),
    contract_revision INTEGER NOT NULL CHECK(contract_revision BETWEEN 1 AND 9007199254740991),
    contract_digest TEXT NOT NULL CHECK(length(contract_digest)=64),
    instance_key TEXT CHECK(instance_key IS NULL OR length(instance_key) BETWEEN 1 AND 256),
    requires_instance INTEGER NOT NULL CHECK(requires_instance IN (0,1)),
    plan_path TEXT NOT NULL,
    plan_digest TEXT NOT NULL CHECK(length(plan_digest)=64),
    PRIMARY KEY(session_id,resource_id)
) STRICT;

CREATE TABLE configuration_run_syncs (
    session_id TEXT NOT NULL REFERENCES configuration_run_sessions(session_id),
    ordinal INTEGER NOT NULL CHECK(ordinal BETWEEN 1 AND 9007199254740991),
    operation_id TEXT NOT NULL UNIQUE REFERENCES operations(operation_id),
    elapsed_milliseconds INTEGER NOT NULL CHECK(elapsed_milliseconds BETWEEN 0 AND 922337203685477),
    PRIMARY KEY(session_id,ordinal)
) STRICT;
CREATE TABLE configuration_run_sync_items (
    operation_id TEXT NOT NULL REFERENCES operations(operation_id),
    resource_id TEXT NOT NULL,
    previous_revision INTEGER NOT NULL CHECK(previous_revision>=1),
    current_revision INTEGER NOT NULL CHECK(current_revision>=previous_revision),
    reason_code TEXT NOT NULL CHECK(length(reason_code) BETWEEN 1 AND 80),
    PRIMARY KEY(operation_id,resource_id)
) STRICT;

CREATE TABLE execution_provider_launcher_roots (
    journal_id TEXT NOT NULL REFERENCES operation_journals(journal_id),
    launcher_id TEXT NOT NULL CHECK(length(launcher_id)=36),
    step_id TEXT NOT NULL,
    job_name TEXT NOT NULL CHECK(length(job_name)=50),
    writer_id TEXT NOT NULL,
    session_id INTEGER NOT NULL CHECK(session_id>=0),
    total_plan_sha256 TEXT NOT NULL CHECK(length(total_plan_sha256)=64),
    activation_id TEXT NOT NULL CHECK(length(activation_id)=36),
    extension_id TEXT NOT NULL,
    entry_id TEXT NOT NULL,
    process_id INTEGER NOT NULL CHECK(process_id>0),
    created_file_time INTEGER NOT NULL CHECK(created_file_time>0),
    image_path TEXT NOT NULL,
    image_sha256 TEXT NOT NULL CHECK(length(image_sha256)=64),
    user_sid TEXT NOT NULL,
    elevated INTEGER NOT NULL CHECK(elevated IN (0,1)),
    PRIMARY KEY(journal_id,launcher_id),
    UNIQUE(journal_id,job_name),
    FOREIGN KEY(journal_id,writer_id) REFERENCES journal_writers(journal_id,writer_id)
) STRICT;
CREATE TABLE execution_provider_launcher_ended (
    journal_id TEXT NOT NULL,
    launcher_id TEXT NOT NULL,
    operation_id TEXT NOT NULL UNIQUE REFERENCES operations(operation_id),
    retained_path TEXT NOT NULL,
    retained_size INTEGER NOT NULL CHECK(retained_size BETWEEN 1 AND 2097152),
    retained_sha256 TEXT NOT NULL CHECK(length(retained_sha256)=64),
    artifact_path TEXT NOT NULL,
    artifact_size INTEGER NOT NULL CHECK(artifact_size BETWEEN 1 AND 2097152),
    artifact_sha256 TEXT NOT NULL CHECK(length(artifact_sha256)=64),
    PRIMARY KEY(journal_id,launcher_id),
    FOREIGN KEY(journal_id,launcher_id) REFERENCES execution_provider_launcher_roots(journal_id,launcher_id)
) STRICT;

PRAGMA application_id = 1314411313;
PRAGMA user_version = 1;

CREATE TABLE scheduling_watermark (
    singleton INTEGER PRIMARY KEY CHECK(singleton=1),
    revision INTEGER NOT NULL CHECK(revision BETWEEN 0 AND 9007199254740991),
    highest_utc_minute INTEGER CHECK(highest_utc_minute IS NULL OR highest_utc_minute>=0)
) STRICT;
INSERT INTO scheduling_watermark(singleton,revision,highest_utc_minute) VALUES(1,0,NULL);

CREATE TABLE schedule_minute_claims (
    operation_id TEXT PRIMARY KEY NOT NULL REFERENCES operations(operation_id),
    document_json TEXT NOT NULL CHECK(length(document_json) BETWEEN 2 AND 2097152),
    occurrence_count INTEGER NOT NULL CHECK(occurrence_count BETWEEN 0 AND 4096),
    watermark_revision INTEGER UNIQUE NOT NULL CHECK(watermark_revision BETWEEN 0 AND 9007199254740991),
    utc_minute INTEGER UNIQUE NOT NULL CHECK(utc_minute>=0)
) STRICT;

CREATE TABLE scheduled_occurrences (
    occurrence_id TEXT PRIMARY KEY NOT NULL CHECK(length(occurrence_id)=36),
    run_id TEXT UNIQUE NOT NULL CHECK(length(run_id)=36),
    start_operation_id TEXT UNIQUE NOT NULL CHECK(length(start_operation_id)=36),
    schedule_id TEXT NOT NULL CHECK(length(schedule_id)=36),
    local_date TEXT NOT NULL CHECK(length(local_date)=10),
    local_minute INTEGER NOT NULL CHECK(local_minute BETWEEN 0 AND 1439),
    trigger_utc_minute INTEGER NOT NULL CHECK(trigger_utc_minute>=0),
    first_operation_id TEXT NOT NULL REFERENCES schedule_minute_claims(operation_id),
    position INTEGER NOT NULL CHECK(position BETWEEN 0 AND 4095),
    UNIQUE(schedule_id,local_date,local_minute),
    UNIQUE(first_operation_id,position)
) STRICT;
CREATE INDEX scheduled_occurrences_waiting ON scheduled_occurrences(trigger_utc_minute,occurrence_id);

CREATE TABLE schedule_minute_occurrences (
    operation_id TEXT NOT NULL REFERENCES schedule_minute_claims(operation_id),
    position INTEGER NOT NULL CHECK(position BETWEEN 0 AND 4095),
    occurrence_id TEXT NOT NULL REFERENCES scheduled_occurrences(occurrence_id),
    PRIMARY KEY(operation_id,position),
    UNIQUE(operation_id,occurrence_id)
) STRICT;

CREATE TABLE execution_run_budget_states (
    run_id TEXT PRIMARY KEY NOT NULL CHECK(length(run_id)=36),
    last_operation_id TEXT NOT NULL UNIQUE REFERENCES operations(operation_id),
    observation_count INTEGER NOT NULL CHECK(observation_count BETWEEN 1 AND 4096)
) STRICT;

CREATE TABLE execution_run_budget_observations (
    operation_id TEXT PRIMARY KEY NOT NULL REFERENCES operations(operation_id),
    run_id TEXT NOT NULL CHECK(length(run_id)=36),
    ordinal INTEGER NOT NULL CHECK(ordinal BETWEEN 0 AND 4095),
    document_json TEXT NOT NULL CHECK(length(document_json) BETWEEN 2 AND 2097152),
    journal_id TEXT REFERENCES operation_journals(journal_id),
    UNIQUE(run_id,ordinal)
) STRICT;

CREATE TABLE schedule_occurrence_outcomes (
    occurrence_id TEXT PRIMARY KEY NOT NULL REFERENCES scheduled_occurrences(occurrence_id),
    operation_id TEXT NOT NULL UNIQUE REFERENCES operations(operation_id),
    reason_code TEXT NOT NULL CHECK(reason_code IN ('schedule_source_unavailable','schedule_source_cancelled','schedule_budget_continuity_unknown'))
) STRICT;

CREATE TABLE execution_start_sources (
    operation_id TEXT PRIMARY KEY NOT NULL REFERENCES operations(operation_id),
    subject_id TEXT NOT NULL CHECK(length(subject_id) BETWEEN 1 AND 256),
    run_id TEXT NOT NULL CHECK(length(run_id)=36),
    journal_id TEXT NOT NULL UNIQUE REFERENCES operation_journals(journal_id),
    request_json TEXT NOT NULL CHECK(length(request_json) BETWEEN 2 AND 2097152),
    retry_source_operation_id TEXT REFERENCES execution_start_sources(operation_id),
    attempt_ordinal INTEGER NOT NULL CHECK(attempt_ordinal BETWEEN 1 AND 99),
    UNIQUE(run_id,attempt_ordinal),
    CHECK(retry_source_operation_id IS NULL OR retry_source_operation_id!=operation_id)
) STRICT;
CREATE INDEX execution_start_sources_run ON execution_start_sources(run_id,operation_id);

CREATE TABLE execution_retry_intents (
    operation_id TEXT PRIMARY KEY NOT NULL REFERENCES operations(operation_id),
    run_id TEXT NOT NULL CHECK(length(run_id)=36),
    previous_attempt_id TEXT NOT NULL UNIQUE REFERENCES attempt_receipts(attempt_id),
    previous_journal_id TEXT NOT NULL REFERENCES operation_journals(journal_id),
    next_operation_id TEXT NOT NULL UNIQUE CHECK(length(next_operation_id)=36),
    next_attempt_id TEXT NOT NULL UNIQUE CHECK(length(next_attempt_id)=36),
    next_journal_id TEXT NOT NULL UNIQUE CHECK(length(next_journal_id)=36),
    next_ordinal INTEGER NOT NULL CHECK(next_ordinal BETWEEN 2 AND 99),
    document_json TEXT NOT NULL CHECK(length(document_json) BETWEEN 2 AND 2097152),
    UNIQUE(run_id,next_ordinal)
) STRICT;
CREATE TABLE execution_retry_outcomes (
    retry_operation_id TEXT PRIMARY KEY NOT NULL REFERENCES execution_retry_intents(operation_id),
    operation_id TEXT NOT NULL UNIQUE REFERENCES operations(operation_id),
    reason_code TEXT NOT NULL CHECK(reason_code IN ('retry_budget_unknown','retry_budget_exhausted','retry_sources_unavailable','retry_authority_revoked','retry_run_cancelled'))
) STRICT;

CREATE TABLE startup_queue_batches (
    generation TEXT PRIMARY KEY CHECK(length(generation)=36),
    operation_id TEXT NOT NULL UNIQUE REFERENCES operations(operation_id),
    document_json TEXT NOT NULL CHECK(length(document_json) BETWEEN 1 AND 2097152)
) STRICT;
CREATE TABLE startup_queue_occurrences (
    occurrence_id TEXT PRIMARY KEY CHECK(length(occurrence_id)=36),
    generation TEXT NOT NULL REFERENCES startup_queue_batches(generation),
    queue_id TEXT NOT NULL CHECK(length(queue_id)=36),
    run_id TEXT NOT NULL UNIQUE CHECK(length(run_id)=36),
    start_operation_id TEXT NOT NULL UNIQUE CHECK(length(start_operation_id)=36),
    UNIQUE(generation,queue_id)
) STRICT;
CREATE TABLE startup_queue_outcomes (
    occurrence_id TEXT PRIMARY KEY REFERENCES startup_queue_occurrences(occurrence_id),
    reason_code TEXT NOT NULL CHECK(reason_code IN ('startup_owner_restarted','schedule_definition_changed','schedule_definition_deleted','schedule_settings_changed','schedule_inputs_unavailable','schedule_budget_continuity_unknown'))
) STRICT;

CREATE TABLE completion_candidates (
    run_id TEXT PRIMARY KEY CHECK(length(run_id)=36),
    operation_id TEXT NOT NULL UNIQUE REFERENCES operations(operation_id),
    attempt_id TEXT NOT NULL UNIQUE REFERENCES attempt_receipts(attempt_id),
    subject_id TEXT NOT NULL,
    intent_json TEXT NOT NULL CHECK(length(intent_json) BETWEEN 1 AND 2097152),
    total_plan_path TEXT NOT NULL,
    total_plan_size INTEGER NOT NULL CHECK(total_plan_size BETWEEN 1 AND 2097152),
    total_plan_sha256 TEXT NOT NULL CHECK(length(total_plan_sha256)=64),
    owner_generation TEXT NOT NULL CHECK(length(owner_generation)=36)
) STRICT;
CREATE TABLE completion_candidate_outcomes (
    run_id TEXT PRIMARY KEY REFERENCES completion_candidates(run_id),
    reason_code TEXT NOT NULL CHECK(length(reason_code) BETWEEN 1 AND 80)
) STRICT;
CREATE TABLE completion_actions (
    operation_id TEXT PRIMARY KEY REFERENCES operations(operation_id),
    subject_id TEXT NOT NULL,
    request_digest TEXT NOT NULL CHECK(length(request_digest)=64),
    frozen_json TEXT NOT NULL CHECK(length(frozen_json) BETWEEN 1 AND 2097152)
) STRICT;
CREATE TABLE completion_action_members (
    operation_id TEXT NOT NULL REFERENCES completion_actions(operation_id),
    run_id TEXT NOT NULL UNIQUE REFERENCES completion_candidates(run_id),
    PRIMARY KEY(operation_id,run_id)
) STRICT;
CREATE TABLE completion_action_transitions (
    operation_id TEXT NOT NULL REFERENCES completion_actions(operation_id),
    ordinal INTEGER NOT NULL CHECK(ordinal BETWEEN 0 AND 16),
    snapshot_json TEXT NOT NULL CHECK(length(snapshot_json) BETWEEN 1 AND 2097152),
    PRIMARY KEY(operation_id,ordinal)
) STRICT;

CREATE TABLE notification_catalog_current (
    singleton INTEGER PRIMARY KEY CHECK(singleton=1),
    revision INTEGER NOT NULL CHECK(revision BETWEEN 1 AND 9007199254740991),
    relative_path TEXT NOT NULL,
    size_bytes INTEGER NOT NULL CHECK(size_bytes BETWEEN 1 AND 2097152),
    sha256 TEXT NOT NULL CHECK(length(sha256)=64)
) STRICT;
CREATE TABLE notification_task_ids (
    task_id TEXT PRIMARY KEY CHECK(length(task_id)=36),
    first_operation_id TEXT NOT NULL REFERENCES operations(operation_id)
) STRICT;
CREATE TABLE notification_publications (
    operation_id TEXT PRIMARY KEY REFERENCES operations(operation_id),
    relative_path TEXT NOT NULL,
    size_bytes INTEGER NOT NULL CHECK(size_bytes BETWEEN 1 AND 2097152),
    sha256 TEXT NOT NULL CHECK(length(sha256)=64),
    before_revision INTEGER NOT NULL CHECK(before_revision BETWEEN 0 AND 9007199254740990),
    after_revision INTEGER NOT NULL CHECK(after_revision=before_revision+1)
) STRICT;
CREATE TABLE notification_publication_fields (
    parent_operation_id TEXT NOT NULL REFERENCES notification_publications(operation_id),
    field TEXT NOT NULL CHECK(field IN ('webhook-url','webhook-secret','feishu-app-secret','slack-bot-token','smtp-password')),
    child_operation_id TEXT NOT NULL UNIQUE REFERENCES operations(operation_id) DEFERRABLE INITIALLY DEFERRED,
    PRIMARY KEY(parent_operation_id,field)
) STRICT;
CREATE TABLE notification_publication_outcomes (
    operation_id TEXT PRIMARY KEY REFERENCES notification_publications(operation_id),
    outcome TEXT NOT NULL CHECK(outcome IN ('published','aborted')),
    catalog_revision INTEGER UNIQUE,
    CHECK((outcome='published' AND catalog_revision BETWEEN 1 AND 9007199254740991) OR
          (outcome='aborted' AND catalog_revision IS NULL))
) STRICT;

CREATE TABLE notification_deliveries (
    operation_id TEXT PRIMARY KEY REFERENCES operations(operation_id),
    acceptance_id TEXT NOT NULL CHECK(length(acceptance_id)=36),
    source_attempt_id TEXT REFERENCES attempt_receipts(attempt_id),
    task_id TEXT NOT NULL CHECK(length(task_id)=36),
    relative_path TEXT NOT NULL,
    size_bytes INTEGER NOT NULL CHECK(size_bytes BETWEEN 1 AND 2097152),
    sha256 TEXT NOT NULL CHECK(length(sha256)=64)
) STRICT;
CREATE INDEX ix_notification_deliveries_acceptance ON notification_deliveries(acceptance_id);
CREATE TABLE notification_delivery_secrets (
    operation_id TEXT NOT NULL REFERENCES notification_deliveries(operation_id),
    field TEXT NOT NULL CHECK(field IN ('webhook-url','webhook-secret','feishu-app-secret','slack-bot-token','smtp-password')),
    revision INTEGER NOT NULL CHECK(revision BETWEEN 0 AND 9007199254740991),
    secret_id TEXT REFERENCES secret_payloads(secret_id),
    PRIMARY KEY(operation_id,field)
) STRICT;
CREATE TABLE notification_delivery_assets (
    operation_id TEXT PRIMARY KEY REFERENCES notification_deliveries(operation_id),
    run_id TEXT NOT NULL,
    digest TEXT NOT NULL,
    kind INTEGER NOT NULL CHECK(kind=0),
    size_bytes INTEGER NOT NULL CHECK(size_bytes BETWEEN 0 AND 1073741824)
) STRICT;
CREATE TABLE notification_delivery_transitions (
    operation_id TEXT NOT NULL REFERENCES notification_deliveries(operation_id),
    ordinal INTEGER NOT NULL CHECK(ordinal BETWEEN 0 AND 2),
    document_json TEXT NOT NULL CHECK(length(document_json) BETWEEN 1 AND 2097152),
    PRIMARY KEY(operation_id,ordinal)
) STRICT;

CREATE TABLE notification_catalog_tasks (
    task_id TEXT PRIMARY KEY REFERENCES notification_task_ids(task_id),
    revision INTEGER NOT NULL CHECK(revision BETWEEN 1 AND 9007199254740991),
    relative_path TEXT NOT NULL,
    size_bytes INTEGER NOT NULL CHECK(size_bytes BETWEEN 1 AND 2097152),
    sha256 TEXT NOT NULL CHECK(length(sha256)=64)
) STRICT;

CREATE TABLE notification_receipt_sources (
    operation_id TEXT PRIMARY KEY REFERENCES operations(operation_id),
    run_id TEXT NOT NULL UNIQUE REFERENCES run_records(run_id),
    attempt_id TEXT NOT NULL UNIQUE REFERENCES attempt_receipts(attempt_id),
    route_count INTEGER NOT NULL CHECK(route_count BETWEEN 0 AND 4096),
    relative_path TEXT NOT NULL,
    size_bytes INTEGER NOT NULL CHECK(size_bytes BETWEEN 1 AND 2097152),
    sha256 TEXT NOT NULL CHECK(length(sha256)=64)
) STRICT;
CREATE TABLE notification_receipt_progress (
    operation_id TEXT PRIMARY KEY REFERENCES notification_receipt_sources(operation_id),
    next_ordinal INTEGER NOT NULL CHECK(next_ordinal BETWEEN 0 AND 4096),
    route_count INTEGER NOT NULL CHECK(route_count BETWEEN 0 AND 4096),
    CHECK(next_ordinal<=route_count)
) STRICT;
CREATE INDEX ix_notification_receipt_pending ON notification_receipt_progress(operation_id) WHERE next_ordinal<route_count;
CREATE TABLE notification_receipt_secrets (
    operation_id TEXT NOT NULL REFERENCES notification_receipt_sources(operation_id),
    task_id TEXT NOT NULL CHECK(length(task_id)=36),
    field TEXT NOT NULL CHECK(field IN ('webhook-url','webhook-secret','feishu-app-secret','slack-bot-token','smtp-password')),
    revision INTEGER NOT NULL CHECK(revision BETWEEN 0 AND 9007199254740991),
    secret_id TEXT REFERENCES secret_payloads(secret_id),
    PRIMARY KEY(operation_id,task_id,field)
) STRICT;
CREATE TABLE notification_receipt_routes (
    operation_id TEXT NOT NULL REFERENCES notification_receipt_sources(operation_id),
    ordinal INTEGER NOT NULL CHECK(ordinal BETWEEN 0 AND 4095),
    relative_path TEXT NOT NULL,
    size_bytes INTEGER NOT NULL CHECK(size_bytes BETWEEN 1 AND 2097152),
    sha256 TEXT NOT NULL CHECK(length(sha256)=64),
    PRIMARY KEY(operation_id,ordinal)
) STRICT;
CREATE TABLE notification_receipt_deliveries (
    operation_id TEXT NOT NULL,
    route_ordinal INTEGER NOT NULL,
    delivery_operation_id TEXT NOT NULL UNIQUE REFERENCES notification_deliveries(operation_id),
    PRIMARY KEY(operation_id,route_ordinal,delivery_operation_id),
    FOREIGN KEY(operation_id,route_ordinal) REFERENCES notification_receipt_routes(operation_id,ordinal)
) STRICT;

CREATE TABLE notification_delivery_pending (
    operation_id TEXT PRIMARY KEY REFERENCES notification_deliveries(operation_id),
    source_operation_id TEXT REFERENCES operations(operation_id),
    state TEXT NOT NULL CHECK(state IN ('queued','sending','unknown'))
) STRICT;
CREATE INDEX notification_delivery_pending_state ON notification_delivery_pending(state,operation_id);
CREATE INDEX notification_delivery_pending_source ON notification_delivery_pending(source_operation_id,state,operation_id);

CREATE TABLE notification_saved_tests (
    operation_id TEXT PRIMARY KEY REFERENCES operations(operation_id),
    delivery_operation_id TEXT NOT NULL UNIQUE REFERENCES notification_deliveries(operation_id) DEFERRABLE INITIALLY DEFERRED,
    relative_path TEXT NOT NULL,
    size_bytes INTEGER NOT NULL CHECK(size_bytes BETWEEN 1 AND 2097152),
    sha256 TEXT NOT NULL CHECK(length(sha256)=64)
) STRICT;

CREATE TABLE scheduled_provider_occurrences(
    occurrence_id TEXT PRIMARY KEY NOT NULL,
    operation_id TEXT NOT NULL UNIQUE REFERENCES operations(operation_id),
    minute_operation_id TEXT NOT NULL REFERENCES schedule_minute_claims(operation_id),
    extension_id TEXT NOT NULL,
    provider_id TEXT NOT NULL,
    task_id TEXT NOT NULL,
    local_date TEXT NOT NULL,
    local_minute INTEGER NOT NULL CHECK(local_minute BETWEEN 0 AND 1439),
    relative_path TEXT NOT NULL,
    size_bytes INTEGER NOT NULL CHECK(size_bytes BETWEEN 1 AND 2097152),
    sha256 TEXT NOT NULL,
    UNIQUE(extension_id,provider_id,task_id,local_date,local_minute)
) STRICT;
CREATE INDEX scheduled_provider_order ON scheduled_provider_occurrences(minute_operation_id,occurrence_id);
CREATE TABLE scheduled_provider_begin(
    occurrence_id TEXT PRIMARY KEY NOT NULL REFERENCES scheduled_provider_occurrences(occurrence_id),
    relative_path TEXT NOT NULL,
    size_bytes INTEGER NOT NULL CHECK(size_bytes BETWEEN 1 AND 2097152),
    sha256 TEXT NOT NULL
) STRICT;
CREATE TABLE scheduled_provider_close(
    occurrence_id TEXT PRIMARY KEY NOT NULL REFERENCES scheduled_provider_occurrences(occurrence_id),
    relative_path TEXT NOT NULL,
    size_bytes INTEGER NOT NULL CHECK(size_bytes BETWEEN 1 AND 2097152),
    sha256 TEXT NOT NULL
) STRICT;
CREATE TABLE scheduled_provider_result(
    occurrence_id TEXT PRIMARY KEY NOT NULL REFERENCES scheduled_provider_occurrences(occurrence_id),
    relative_path TEXT NOT NULL,
    size_bytes INTEGER NOT NULL CHECK(size_bytes BETWEEN 1 AND 2097152),
    sha256 TEXT NOT NULL
) STRICT;
CREATE TABLE scheduled_provider_pending(
    occurrence_id TEXT PRIMARY KEY NOT NULL REFERENCES scheduled_provider_occurrences(occurrence_id),
    trigger_utc_minute INTEGER NOT NULL CHECK(trigger_utc_minute>=0)
) STRICT;
CREATE INDEX scheduled_provider_pending_order ON scheduled_provider_pending(trigger_utc_minute,occurrence_id);

CREATE TABLE notification_scheduled_sources(
    operation_id TEXT PRIMARY KEY NOT NULL REFERENCES operations(operation_id),
    occurrence_id TEXT NOT NULL UNIQUE REFERENCES scheduled_provider_occurrences(occurrence_id),
    relative_path TEXT NOT NULL,
    size_bytes INTEGER NOT NULL CHECK(size_bytes BETWEEN 1 AND 2097152),
    sha256 TEXT NOT NULL
) STRICT;
CREATE TABLE notification_scheduled_deliveries(
    operation_id TEXT NOT NULL REFERENCES notification_scheduled_sources(operation_id),
    task_id TEXT NOT NULL,
    delivery_operation_id TEXT NOT NULL UNIQUE REFERENCES notification_deliveries(operation_id) DEFERRABLE INITIALLY DEFERRED,
    PRIMARY KEY(operation_id,task_id)
) STRICT;

CREATE TABLE execution_log_catalog(
    attempt_id TEXT PRIMARY KEY NOT NULL REFERENCES attempt_receipts(attempt_id),
    run_id TEXT NOT NULL,
    journal_id TEXT NOT NULL UNIQUE REFERENCES operation_journals(journal_id),
    terminal_revision INTEGER NOT NULL CHECK(terminal_revision BETWEEN 0 AND 4095),
    terminal_sha256 TEXT NOT NULL,
    entry_count INTEGER NOT NULL CHECK(entry_count BETWEEN 0 AND 4096)
) STRICT;
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
    UNIQUE(attempt_id,step_id,role)
) STRICT;

CREATE TABLE record_content_retirements(
    operation_id TEXT PRIMARY KEY REFERENCES operations(operation_id),
    run_id TEXT NOT NULL REFERENCES run_records(run_id),
    family_digest TEXT NOT NULL CHECK(length(family_digest)=64),
    cleanup_journal_id TEXT NOT NULL UNIQUE REFERENCES operation_journals(journal_id),
    relative_path TEXT NOT NULL,
    size_bytes INTEGER NOT NULL CHECK(size_bytes BETWEEN 1 AND 2097152),
    sha256 TEXT NOT NULL CHECK(length(sha256)=64)
) STRICT;
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
    UNIQUE(run_id,digest)
) STRICT;
CREATE INDEX record_retired_contents_run_digest ON record_retired_contents(run_id,digest,operation_id);

CREATE TABLE execution_image_catalog(
    attempt_id TEXT PRIMARY KEY NOT NULL REFERENCES attempt_receipts(attempt_id),
    run_id TEXT NOT NULL,
    journal_id TEXT NOT NULL UNIQUE REFERENCES operation_journals(journal_id),
    terminal_revision INTEGER NOT NULL CHECK(terminal_revision BETWEEN 1 AND 4096),
    terminal_sha256 TEXT NOT NULL CHECK(length(terminal_sha256)=64),
    entry_count INTEGER NOT NULL CHECK(entry_count BETWEEN 0 AND 4096)
) STRICT;
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
    CHECK((outcome=0)=(png_path IS NOT NULL) AND (png_path IS NULL OR root_path IS NOT NULL))
) STRICT;
CREATE INDEX execution_image_entries_png ON execution_image_entries(png_sha256,attempt_id) WHERE png_sha256 IS NOT NULL;
CREATE TABLE execution_image_asset_catalog(
    run_id TEXT PRIMARY KEY NOT NULL REFERENCES run_records(run_id),
    final_attempt_id TEXT NOT NULL REFERENCES attempt_receipts(attempt_id),
    family_sha256 TEXT NOT NULL CHECK(length(family_sha256)=64),
    image_count INTEGER NOT NULL CHECK(image_count BETWEEN 0 AND 405504)
) STRICT;

CREATE TABLE extension_catalog_acceptances (
    format TEXT NOT NULL CHECK(format='nexus.store.catalog'),
    channel TEXT NOT NULL CHECK(channel IN ('stable','develop')),
    sequence INTEGER NOT NULL CHECK(sequence BETWEEN 1 AND 9007199254740991),
    generation_id TEXT NOT NULL CHECK(length(generation_id)=36),
    catalog_sha TEXT NOT NULL CHECK(length(catalog_sha)=64),
    key_id TEXT NOT NULL CHECK(length(key_id) BETWEEN 2 AND 80),
    pointer_sha TEXT NOT NULL CHECK(length(pointer_sha)=64),
    pointer_size INTEGER NOT NULL CHECK(pointer_size BETWEEN 1 AND 2097152),
    catalog_size INTEGER NOT NULL CHECK(catalog_size BETWEEN 1 AND 2097152),
    signature_sha TEXT NOT NULL CHECK(length(signature_sha)=64),
    signature_size INTEGER NOT NULL CHECK(signature_size BETWEEN 1 AND 2097152),
    first_operation_id TEXT NOT NULL UNIQUE REFERENCES operations(operation_id),
    PRIMARY KEY(format,channel,sequence),
    UNIQUE(format,channel,generation_id),
    UNIQUE(format,channel,sequence,catalog_sha)
) STRICT;

CREATE TABLE extension_catalog_heads (
    format TEXT NOT NULL CHECK(format='nexus.store.catalog'),
    channel TEXT NOT NULL CHECK(channel IN ('stable','develop')),
    sequence INTEGER NOT NULL CHECK(sequence BETWEEN 1 AND 9007199254740991),
    catalog_sha TEXT NOT NULL CHECK(length(catalog_sha)=64),
    PRIMARY KEY(format,channel),
    FOREIGN KEY(format,channel,sequence,catalog_sha)
        REFERENCES extension_catalog_acceptances(format,channel,sequence,catalog_sha)
) STRICT;

CREATE TABLE extension_installation_revisions (
    extension_id TEXT NOT NULL CHECK(length(extension_id) BETWEEN 1 AND 80),
    revision INTEGER NOT NULL CHECK(revision BETWEEN 1 AND 9007199254740991),
    is_installed INTEGER NOT NULL CHECK(is_installed IN (0,1)),
    package_version TEXT NOT NULL CHECK(length(package_version) BETWEEN 1 AND 80),
    package_kind TEXT NOT NULL CHECK(package_kind IN ('managed-code','data-specialized')),
    archive_sha256 TEXT NOT NULL CHECK(length(archive_sha256)=64),
    manifest_sha256 TEXT NOT NULL CHECK(length(manifest_sha256)=64),
    journal_id TEXT NOT NULL UNIQUE REFERENCES operation_journals(journal_id),
    footprint_sha256 TEXT NOT NULL CHECK(length(footprint_sha256)=64),
    footprint_size INTEGER NOT NULL CHECK(footprint_size BETWEEN 1 AND 2097152),
    PRIMARY KEY(extension_id,revision)
) STRICT;

CREATE TABLE extension_installations (
    extension_id TEXT PRIMARY KEY NOT NULL CHECK(length(extension_id) BETWEEN 1 AND 80),
    revision INTEGER NOT NULL CHECK(revision BETWEEN 1 AND 9007199254740991),
    FOREIGN KEY(extension_id,revision) REFERENCES extension_installation_revisions(extension_id,revision)
) STRICT;

CREATE TABLE trusted_update_feeds (
    format TEXT NOT NULL CHECK(format='nexus.update.feed'),
    channel TEXT NOT NULL CHECK(channel IN ('stable','prerelease')),
    sequence INTEGER NOT NULL CHECK(sequence BETWEEN 1 AND 9007199254740991),
    feed_sha256 TEXT NOT NULL CHECK(length(feed_sha256)=64),
    key_id TEXT NOT NULL CHECK(length(key_id) BETWEEN 2 AND 80),
    feed_size INTEGER NOT NULL CHECK(feed_size BETWEEN 1 AND 2097152),
    signature_sha256 TEXT NOT NULL CHECK(length(signature_sha256)=64),
    signature_size INTEGER NOT NULL CHECK(signature_size BETWEEN 1 AND 2097152),
    first_operation_id TEXT NOT NULL UNIQUE REFERENCES operations(operation_id),
    PRIMARY KEY(format,channel,sequence),
    UNIQUE(format,channel,sequence,feed_sha256)
) STRICT;
CREATE TABLE trusted_update_feed_heads (
    format TEXT NOT NULL CHECK(format='nexus.update.feed'),
    channel TEXT NOT NULL CHECK(channel IN ('stable','prerelease')),
    sequence INTEGER NOT NULL CHECK(sequence BETWEEN 1 AND 9007199254740991),
    feed_sha256 TEXT NOT NULL CHECK(length(feed_sha256)=64),
    PRIMARY KEY(format,channel),
    FOREIGN KEY(format,channel,sequence,feed_sha256)
        REFERENCES trusted_update_feeds(format,channel,sequence,feed_sha256)
) STRICT;

CREATE TABLE maintenance_operations (
    operation_id TEXT PRIMARY KEY NOT NULL REFERENCES operations(operation_id),
    plan_sha256 TEXT NOT NULL CHECK(length(plan_sha256)=64),
    plan_size INTEGER NOT NULL CHECK(plan_size BETWEEN 1 AND 2097152),
    source_sha256 TEXT NOT NULL CHECK(length(source_sha256)=64),
    source_size INTEGER NOT NULL CHECK(source_size BETWEEN 1 AND 2097152),
    state INTEGER NOT NULL CHECK(state BETWEEN 0 AND 4),
    handoff_id TEXT UNIQUE CHECK(handoff_id IS NULL OR length(handoff_id)=36),
    terminal_code TEXT CHECK(terminal_code IS NULL OR length(terminal_code) BETWEEN 1 AND 80),
    CHECK((state IN (0,3))=(handoff_id IS NULL)),
    CHECK((state IN (2,3,4))=(terminal_code IS NOT NULL)),
    CHECK(state<>3 OR terminal_code='cancelled-before-handoff')
) STRICT;
CREATE UNIQUE INDEX maintenance_single_active ON maintenance_operations((1)) WHERE state IN (0,1,4);

CREATE TABLE recovery_capacity_reservations (
    owner_journal_id TEXT PRIMARY KEY NOT NULL REFERENCES operation_journals(journal_id) DEFERRABLE INITIALLY DEFERRED,
    journal_limit INTEGER NOT NULL CHECK(journal_limit BETWEEN 1 AND 10000),
    effect_limit INTEGER NOT NULL CHECK(effect_limit BETWEEN 0 AND 65536),
    job_limit INTEGER NOT NULL CHECK(job_limit BETWEEN 0 AND 10000),
    remaining_journals INTEGER NOT NULL CHECK(remaining_journals BETWEEN 0 AND journal_limit),
    remaining_effects INTEGER NOT NULL CHECK(remaining_effects BETWEEN 0 AND effect_limit),
    remaining_jobs INTEGER NOT NULL CHECK(remaining_jobs BETWEEN 0 AND job_limit),
    released INTEGER NOT NULL CHECK(released IN (0,1)),
    CHECK(released=0 OR remaining_journals+remaining_effects+remaining_jobs=0)
) STRICT;

CREATE TABLE recovery_capacity_members (
    journal_id TEXT PRIMARY KEY NOT NULL REFERENCES operation_journals(journal_id) DEFERRABLE INITIALLY DEFERRED,
    owner_journal_id TEXT NOT NULL REFERENCES recovery_capacity_reservations(owner_journal_id)
) STRICT;
CREATE INDEX recovery_capacity_members_owner ON recovery_capacity_members(owner_journal_id,journal_id);

CREATE TRIGGER recovery_capacity_journal_insert BEFORE INSERT ON operation_journals
BEGIN
    SELECT CASE WHEN NOT EXISTS(SELECT 1 FROM recovery_capacity_members m JOIN recovery_capacity_reservations r USING(owner_journal_id)
        WHERE m.journal_id=NEW.journal_id AND r.released=0 AND r.remaining_journals>0)
        THEN RAISE(ABORT,'recovery_capacity_journal_unreserved') END;
    UPDATE recovery_capacity_reservations SET remaining_journals=remaining_journals-1
        WHERE owner_journal_id=(SELECT owner_journal_id FROM recovery_capacity_members WHERE journal_id=NEW.journal_id);
END;

CREATE TRIGGER recovery_capacity_effect_insert BEFORE INSERT ON file_effects
WHEN NOT EXISTS(SELECT 1 FROM file_effects WHERE journal_id=NEW.journal_id AND step_id=NEW.step_id)
BEGIN
    SELECT CASE WHEN NOT EXISTS(SELECT 1 FROM recovery_capacity_members m JOIN recovery_capacity_reservations r USING(owner_journal_id)
        WHERE m.journal_id=NEW.journal_id AND r.released=0 AND r.remaining_effects>0)
        THEN RAISE(ABORT,'recovery_capacity_effect_unreserved') END;
    UPDATE recovery_capacity_reservations SET remaining_effects=remaining_effects-1
        WHERE owner_journal_id=(SELECT owner_journal_id FROM recovery_capacity_members WHERE journal_id=NEW.journal_id);
END;

CREATE TRIGGER recovery_capacity_job_insert BEFORE INSERT ON journal_writer_jobs
WHEN NOT EXISTS(SELECT 1 FROM journal_writer_jobs WHERE journal_id=NEW.journal_id AND writer_id=NEW.writer_id AND job_name=NEW.job_name)
BEGIN
    SELECT CASE WHEN NOT EXISTS(SELECT 1 FROM recovery_capacity_members m JOIN recovery_capacity_reservations r USING(owner_journal_id)
        WHERE m.journal_id=NEW.journal_id AND r.released=0 AND r.remaining_jobs>0)
        THEN RAISE(ABORT,'recovery_capacity_job_unreserved') END;
    UPDATE recovery_capacity_reservations SET remaining_jobs=remaining_jobs-1
        WHERE owner_journal_id=(SELECT owner_journal_id FROM recovery_capacity_members WHERE journal_id=NEW.journal_id);
END;

CREATE TRIGGER recovery_capacity_terminal_release AFTER UPDATE OF phase ON operation_journals
WHEN NEW.phase='committed'
BEGIN
    UPDATE recovery_capacity_reservations SET remaining_journals=0,remaining_effects=0,remaining_jobs=0,released=1
        WHERE owner_journal_id=(SELECT owner_journal_id FROM recovery_capacity_members WHERE journal_id=NEW.journal_id)
        AND NOT EXISTS(SELECT 1 FROM operation_journals WHERE journal_id=owner_journal_id AND operation_kind='updateApply')
        AND NOT EXISTS(SELECT 1 FROM recovery_capacity_members m JOIN operation_journals j USING(journal_id)
            WHERE m.owner_journal_id=recovery_capacity_reservations.owner_journal_id AND j.phase<>'committed');
END;

CREATE TABLE extension_documents (
    extension_id TEXT NOT NULL CHECK(length(extension_id) BETWEEN 1 AND 80),
    name TEXT NOT NULL CHECK(length(name) BETWEEN 1 AND 80),
    revision INTEGER NOT NULL CHECK(revision BETWEEN 1 AND 9007199254740991),
    content BLOB NOT NULL CHECK(length(content) BETWEEN 1 AND 1048576),
    sha256 TEXT NOT NULL CHECK(length(sha256)=64),
    operation_id TEXT NOT NULL UNIQUE REFERENCES operations(operation_id),
    PRIMARY KEY(extension_id,name)
) STRICT;
CREATE TABLE extension_document_commits (
    operation_id TEXT PRIMARY KEY NOT NULL REFERENCES operations(operation_id),
    extension_id TEXT NOT NULL,
    name TEXT NOT NULL,
    revision INTEGER NOT NULL CHECK(revision BETWEEN 1 AND 9007199254740991),
    sha256 TEXT NOT NULL CHECK(length(sha256)=64)
) STRICT;

CREATE TABLE extension_images (
    extension_id TEXT NOT NULL CHECK(length(extension_id) BETWEEN 1 AND 80),
    image_id TEXT NOT NULL CHECK(length(image_id)=64),
    content_type TEXT NOT NULL CHECK(content_type IN ('image/png','image/jpeg','image/webp')),
    content BLOB NOT NULL CHECK(length(content) BETWEEN 1 AND 8388608),
    width INTEGER NOT NULL CHECK(width BETWEEN 1 AND 8192),
    height INTEGER NOT NULL CHECK(height BETWEEN 1 AND 8192),
    PRIMARY KEY(extension_id,image_id)
) STRICT;
CREATE TABLE extension_image_references (
    extension_id TEXT NOT NULL,
    document_name TEXT NOT NULL,
    image_id TEXT NOT NULL,
    PRIMARY KEY(extension_id,document_name,image_id),
    FOREIGN KEY(extension_id,document_name) REFERENCES extension_documents(extension_id,name),
    FOREIGN KEY(extension_id,image_id) REFERENCES extension_images(extension_id,image_id)
) STRICT;
