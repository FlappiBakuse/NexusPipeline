CREATE TABLE schema_info (
    singleton INTEGER PRIMARY KEY CHECK(singleton=1),
    format TEXT NOT NULL CHECK(format='nexus.state.domain'),
    schema_version INTEGER NOT NULL CHECK(schema_version=2),
    instance_id TEXT NOT NULL CHECK(length(instance_id)=36),
    domain_name TEXT NOT NULL CHECK(domain_name='runtime'),
    contract_digest TEXT NOT NULL CHECK(length(contract_digest)=64),
    created_at TEXT NOT NULL
) STRICT;

CREATE TABLE operations (
    operation_id TEXT PRIMARY KEY NOT NULL CHECK(length(operation_id) = 36),
    subject_id TEXT NOT NULL CHECK(length(subject_id) BETWEEN 1 AND 256),
    request_digest TEXT NOT NULL CHECK(length(request_digest) = 64),
    operation_kind TEXT NOT NULL CHECK(length(operation_kind) BETWEEN 1 AND 120),
    status TEXT NOT NULL CHECK(status IN ('accepted', 'completed')),
    created_at TEXT NOT NULL,
    initial_journal_id TEXT REFERENCES operation_journals(journal_id)) STRICT;

CREATE TABLE operation_journals (
    journal_id TEXT PRIMARY KEY NOT NULL CHECK(length(journal_id) = 36),
    operation_id TEXT NOT NULL REFERENCES operations(operation_id),
    operation_kind TEXT NOT NULL CHECK(operation_kind IN ('configExchange','configEdit','fileCleanup','runAttempt','updateApply','extensionApply','assetPrepare','secretPrepare')),
    phase TEXT NOT NULL CHECK(phase IN ('prepared','applying','applied','reverting','reverted','committed','quarantined')),
    revision INTEGER NOT NULL CHECK(revision >= 1),
    plan_path TEXT NOT NULL,
    plan_digest TEXT NOT NULL CHECK(length(plan_digest) = 64),
    created_at TEXT NOT NULL) STRICT;

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
    PRIMARY KEY(journal_id,writer_id)) STRICT;

CREATE TABLE journal_writer_jobs (
    journal_id TEXT NOT NULL,
    writer_id TEXT NOT NULL,
    job_name TEXT NOT NULL CHECK(length(job_name)=50),
    session_id INTEGER NOT NULL CHECK(session_id>=0),
    PRIMARY KEY(journal_id,writer_id,job_name),
    FOREIGN KEY(journal_id,writer_id) REFERENCES journal_writers(journal_id,writer_id)) STRICT;

CREATE TABLE journal_effect_gates (
    journal_id TEXT PRIMARY KEY NOT NULL REFERENCES operation_journals(journal_id),
    state TEXT NOT NULL CHECK(state IN ('unentered','entered','sealed')),
    revision INTEGER NOT NULL CHECK(revision BETWEEN 1 AND 9007199254740991)) STRICT;

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
    FOREIGN KEY(journal_id,writer_id,job_name) REFERENCES journal_writer_jobs(journal_id,writer_id,job_name)) STRICT;

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
    FOREIGN KEY(journal_id,writer_id) REFERENCES journal_writers(journal_id,writer_id)) STRICT;

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
        REFERENCES execution_target_command_jobs(journal_id,command_id,command_sequence)) STRICT;

CREATE TABLE journal_writer_job_closures (
    journal_id TEXT NOT NULL,
    writer_id TEXT NOT NULL,
    job_name TEXT NOT NULL,
    plan_path TEXT NOT NULL,
    plan_digest TEXT NOT NULL CHECK(length(plan_digest)=64),
    plan_size INTEGER NOT NULL CHECK(plan_size BETWEEN 1 AND 2097152),
    PRIMARY KEY(journal_id,writer_id,job_name),
    FOREIGN KEY(journal_id,writer_id,job_name) REFERENCES journal_writer_jobs(journal_id,writer_id,job_name)) STRICT;

CREATE TABLE journal_resources (
    journal_id TEXT NOT NULL REFERENCES operation_journals(journal_id),
    resource_key TEXT NOT NULL CHECK(length(resource_key) BETWEEN 1 AND 512),
    PRIMARY KEY(journal_id,resource_key)) STRICT;

CREATE TABLE journal_resource_claims (
    journal_id TEXT NOT NULL,
    resource_key TEXT NOT NULL,
    mode INTEGER NOT NULL CHECK(mode IN (0,1)),
    PRIMARY KEY(journal_id,resource_key),
    FOREIGN KEY(journal_id,resource_key) REFERENCES journal_resources(journal_id,resource_key)) STRICT;

CREATE TABLE file_effects (
    journal_id TEXT NOT NULL REFERENCES operation_journals(journal_id),
    step_id TEXT NOT NULL CHECK(length(step_id) BETWEEN 1 AND 80),
    action TEXT NOT NULL CHECK(action IN ('preserveOriginal','installCandidate','retireCandidate','restoreOriginal','removeOwned')),
    state TEXT NOT NULL CHECK(state IN ('pending','started','completed','notExecuted','quarantined','reverted')),
    intent_digest TEXT NOT NULL CHECK(length(intent_digest) = 64),
    receipt_digest TEXT CHECK(receipt_digest IS NULL OR length(receipt_digest) = 64),
    PRIMARY KEY(journal_id,step_id)) STRICT;

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
    PRIMARY KEY(resource_key,journal_id)) STRICT;

CREATE INDEX resource_isolations_journal ON resource_isolations(journal_id,resource_key);

CREATE TABLE recovery_index_state (
    singleton INTEGER PRIMARY KEY CHECK(singleton = 1),
    ready INTEGER NOT NULL CHECK(ready IN (0,1)),
    revision INTEGER NOT NULL CHECK(revision >= 1)) STRICT;

CREATE TABLE execution_binding_successes (
    run_id TEXT NOT NULL CHECK(length(run_id)=36),
    binding_id TEXT NOT NULL CHECK(length(binding_id)=36),
    journal_id TEXT NOT NULL REFERENCES operation_journals(journal_id),
    started_at_utc_ticks INTEGER NOT NULL CHECK(started_at_utc_ticks>0),
    PRIMARY KEY(run_id,binding_id)) STRICT;

CREATE INDEX execution_binding_successes_day ON execution_binding_successes(binding_id,started_at_utc_ticks);

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
    FOREIGN KEY(journal_id,writer_id) REFERENCES journal_writers(journal_id,writer_id)) STRICT;

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
    FOREIGN KEY(journal_id,launcher_id) REFERENCES execution_provider_launcher_roots(journal_id,launcher_id)) STRICT;

CREATE TABLE scheduling_watermark (
    singleton INTEGER PRIMARY KEY CHECK(singleton=1),
    revision INTEGER NOT NULL CHECK(revision BETWEEN 0 AND 9007199254740991),
    highest_utc_minute INTEGER CHECK(highest_utc_minute IS NULL OR highest_utc_minute>=0)) STRICT;

CREATE TABLE schedule_minute_claims (
    operation_id TEXT PRIMARY KEY NOT NULL REFERENCES operations(operation_id),
    document_json TEXT NOT NULL CHECK(length(document_json) BETWEEN 2 AND 2097152),
    occurrence_count INTEGER NOT NULL CHECK(occurrence_count BETWEEN 0 AND 4096),
    watermark_revision INTEGER UNIQUE NOT NULL CHECK(watermark_revision BETWEEN 0 AND 9007199254740991),
    utc_minute INTEGER UNIQUE NOT NULL CHECK(utc_minute>=0)) STRICT;

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
    UNIQUE(first_operation_id,position)) STRICT;

CREATE INDEX scheduled_occurrences_waiting ON scheduled_occurrences(trigger_utc_minute,occurrence_id);

CREATE TABLE schedule_minute_occurrences (
    operation_id TEXT NOT NULL REFERENCES schedule_minute_claims(operation_id),
    position INTEGER NOT NULL CHECK(position BETWEEN 0 AND 4095),
    occurrence_id TEXT NOT NULL REFERENCES scheduled_occurrences(occurrence_id),
    PRIMARY KEY(operation_id,position),
    UNIQUE(operation_id,occurrence_id)) STRICT;

CREATE TABLE execution_run_budget_states (
    run_id TEXT PRIMARY KEY NOT NULL CHECK(length(run_id)=36),
    last_operation_id TEXT NOT NULL UNIQUE REFERENCES operations(operation_id),
    observation_count INTEGER NOT NULL CHECK(observation_count BETWEEN 1 AND 4096)) STRICT;

CREATE TABLE execution_run_budget_observations (
    operation_id TEXT PRIMARY KEY NOT NULL REFERENCES operations(operation_id),
    run_id TEXT NOT NULL CHECK(length(run_id)=36),
    ordinal INTEGER NOT NULL CHECK(ordinal BETWEEN 0 AND 4095),
    document_json TEXT NOT NULL CHECK(length(document_json) BETWEEN 2 AND 2097152),
    journal_id TEXT REFERENCES operation_journals(journal_id),
    UNIQUE(run_id,ordinal)) STRICT;

CREATE TABLE schedule_occurrence_outcomes (
    occurrence_id TEXT PRIMARY KEY NOT NULL REFERENCES scheduled_occurrences(occurrence_id),
    operation_id TEXT NOT NULL UNIQUE REFERENCES operations(operation_id),
    reason_code TEXT NOT NULL CHECK(reason_code IN ('schedule_source_unavailable','schedule_source_cancelled','schedule_budget_continuity_unknown'))) STRICT;

CREATE TABLE execution_start_sources (
    operation_id TEXT PRIMARY KEY NOT NULL REFERENCES operations(operation_id),
    subject_id TEXT NOT NULL CHECK(length(subject_id) BETWEEN 1 AND 256),
    run_id TEXT NOT NULL CHECK(length(run_id)=36),
    journal_id TEXT NOT NULL UNIQUE REFERENCES operation_journals(journal_id),
    request_json TEXT NOT NULL CHECK(length(request_json) BETWEEN 2 AND 2097152),
    retry_source_operation_id TEXT REFERENCES execution_start_sources(operation_id),
    attempt_ordinal INTEGER NOT NULL CHECK(attempt_ordinal BETWEEN 1 AND 99),
    UNIQUE(run_id,attempt_ordinal),
    CHECK(retry_source_operation_id IS NULL OR retry_source_operation_id!=operation_id)) STRICT;

CREATE INDEX execution_start_sources_run ON execution_start_sources(run_id,operation_id);

CREATE TABLE execution_retry_intents (
    operation_id TEXT PRIMARY KEY NOT NULL REFERENCES operations(operation_id),
    run_id TEXT NOT NULL CHECK(length(run_id)=36),
    previous_attempt_id TEXT NOT NULL UNIQUE,
    previous_journal_id TEXT NOT NULL REFERENCES operation_journals(journal_id),
    next_operation_id TEXT NOT NULL UNIQUE CHECK(length(next_operation_id)=36),
    next_attempt_id TEXT NOT NULL UNIQUE CHECK(length(next_attempt_id)=36),
    next_journal_id TEXT NOT NULL UNIQUE CHECK(length(next_journal_id)=36),
    next_ordinal INTEGER NOT NULL CHECK(next_ordinal BETWEEN 2 AND 99),
    document_json TEXT NOT NULL CHECK(length(document_json) BETWEEN 2 AND 2097152),
    UNIQUE(run_id,next_ordinal)) STRICT;

CREATE TABLE execution_retry_outcomes (
    retry_operation_id TEXT PRIMARY KEY NOT NULL REFERENCES execution_retry_intents(operation_id),
    operation_id TEXT NOT NULL UNIQUE REFERENCES operations(operation_id),
    reason_code TEXT NOT NULL CHECK(reason_code IN ('retry_budget_unknown','retry_budget_exhausted','retry_sources_unavailable','retry_authority_revoked','retry_run_cancelled'))) STRICT;

CREATE TABLE startup_queue_batches (
    generation TEXT PRIMARY KEY CHECK(length(generation)=36),
    operation_id TEXT NOT NULL UNIQUE REFERENCES operations(operation_id),
    document_json TEXT NOT NULL CHECK(length(document_json) BETWEEN 1 AND 2097152)) STRICT;

CREATE TABLE startup_queue_occurrences (
    occurrence_id TEXT PRIMARY KEY CHECK(length(occurrence_id)=36),
    generation TEXT NOT NULL REFERENCES startup_queue_batches(generation),
    queue_id TEXT NOT NULL CHECK(length(queue_id)=36),
    run_id TEXT NOT NULL UNIQUE CHECK(length(run_id)=36),
    start_operation_id TEXT NOT NULL UNIQUE CHECK(length(start_operation_id)=36),
    UNIQUE(generation,queue_id)) STRICT;

CREATE TABLE startup_queue_outcomes (
    occurrence_id TEXT PRIMARY KEY REFERENCES startup_queue_occurrences(occurrence_id),
    reason_code TEXT NOT NULL CHECK(reason_code IN ('startup_owner_restarted','schedule_definition_changed','schedule_definition_deleted','schedule_settings_changed','schedule_inputs_unavailable','schedule_budget_continuity_unknown'))) STRICT;

CREATE TABLE completion_candidates (
    run_id TEXT PRIMARY KEY CHECK(length(run_id)=36),
    operation_id TEXT NOT NULL UNIQUE REFERENCES operations(operation_id),
    attempt_id TEXT NOT NULL UNIQUE,
    subject_id TEXT NOT NULL,
    intent_json TEXT NOT NULL CHECK(length(intent_json) BETWEEN 1 AND 2097152),
    total_plan_path TEXT NOT NULL,
    total_plan_size INTEGER NOT NULL CHECK(total_plan_size BETWEEN 1 AND 2097152),
    total_plan_sha256 TEXT NOT NULL CHECK(length(total_plan_sha256)=64),
    owner_generation TEXT NOT NULL CHECK(length(owner_generation)=36)) STRICT;

CREATE TABLE completion_candidate_outcomes (
    run_id TEXT PRIMARY KEY REFERENCES completion_candidates(run_id),
    reason_code TEXT NOT NULL CHECK(length(reason_code) BETWEEN 1 AND 80)) STRICT;

CREATE TABLE completion_actions (
    operation_id TEXT PRIMARY KEY REFERENCES operations(operation_id),
    subject_id TEXT NOT NULL,
    request_digest TEXT NOT NULL CHECK(length(request_digest)=64),
    frozen_json TEXT NOT NULL CHECK(length(frozen_json) BETWEEN 1 AND 2097152)) STRICT;

CREATE TABLE completion_action_members (
    operation_id TEXT NOT NULL REFERENCES completion_actions(operation_id),
    run_id TEXT NOT NULL UNIQUE REFERENCES completion_candidates(run_id),
    PRIMARY KEY(operation_id,run_id)) STRICT;

CREATE TABLE completion_action_transitions (
    operation_id TEXT NOT NULL REFERENCES completion_actions(operation_id),
    ordinal INTEGER NOT NULL CHECK(ordinal BETWEEN 0 AND 16),
    snapshot_json TEXT NOT NULL CHECK(length(snapshot_json) BETWEEN 1 AND 2097152),
    PRIMARY KEY(operation_id,ordinal)) STRICT;

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
    UNIQUE(extension_id,provider_id,task_id,local_date,local_minute)) STRICT;

CREATE INDEX scheduled_provider_order ON scheduled_provider_occurrences(minute_operation_id,occurrence_id);

CREATE TABLE scheduled_provider_begin(
    occurrence_id TEXT PRIMARY KEY NOT NULL REFERENCES scheduled_provider_occurrences(occurrence_id),
    relative_path TEXT NOT NULL,
    size_bytes INTEGER NOT NULL CHECK(size_bytes BETWEEN 1 AND 2097152),
    sha256 TEXT NOT NULL) STRICT;

CREATE TABLE scheduled_provider_close(
    occurrence_id TEXT PRIMARY KEY NOT NULL REFERENCES scheduled_provider_occurrences(occurrence_id),
    relative_path TEXT NOT NULL,
    size_bytes INTEGER NOT NULL CHECK(size_bytes BETWEEN 1 AND 2097152),
    sha256 TEXT NOT NULL) STRICT;

CREATE TABLE scheduled_provider_result(
    occurrence_id TEXT PRIMARY KEY NOT NULL REFERENCES scheduled_provider_occurrences(occurrence_id),
    relative_path TEXT NOT NULL,
    size_bytes INTEGER NOT NULL CHECK(size_bytes BETWEEN 1 AND 2097152),
    sha256 TEXT NOT NULL) STRICT;

CREATE TABLE scheduled_provider_pending(
    occurrence_id TEXT PRIMARY KEY NOT NULL REFERENCES scheduled_provider_occurrences(occurrence_id),
    trigger_utc_minute INTEGER NOT NULL CHECK(trigger_utc_minute>=0)) STRICT;

CREATE INDEX scheduled_provider_pending_order ON scheduled_provider_pending(trigger_utc_minute,occurrence_id);

CREATE TABLE recovery_capacity_reservations (
    owner_journal_id TEXT PRIMARY KEY NOT NULL REFERENCES operation_journals(journal_id) DEFERRABLE INITIALLY DEFERRED,
    journal_limit INTEGER NOT NULL CHECK(journal_limit BETWEEN 1 AND 10000),
    effect_limit INTEGER NOT NULL CHECK(effect_limit BETWEEN 0 AND 65536),
    job_limit INTEGER NOT NULL CHECK(job_limit BETWEEN 0 AND 10000),
    remaining_journals INTEGER NOT NULL CHECK(remaining_journals BETWEEN 0 AND journal_limit),
    remaining_effects INTEGER NOT NULL CHECK(remaining_effects BETWEEN 0 AND effect_limit),
    remaining_jobs INTEGER NOT NULL CHECK(remaining_jobs BETWEEN 0 AND job_limit),
    released INTEGER NOT NULL CHECK(released IN (0,1)),
    CHECK(released=0 OR remaining_journals+remaining_effects+remaining_jobs=0)) STRICT;

CREATE TABLE recovery_capacity_members (
    journal_id TEXT PRIMARY KEY NOT NULL REFERENCES operation_journals(journal_id) DEFERRABLE INITIALLY DEFERRED,
    owner_journal_id TEXT NOT NULL REFERENCES recovery_capacity_reservations(owner_journal_id)) STRICT;

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

CREATE TABLE manual_work_runs (
    operation_id TEXT PRIMARY KEY REFERENCES operations(operation_id),
    extension_id TEXT NOT NULL,
    provider_id TEXT NOT NULL,
    task_id TEXT NOT NULL,
    relative_path TEXT NOT NULL,
    size_bytes INTEGER NOT NULL CHECK(size_bytes BETWEEN 1 AND 1048576),
    sha256 TEXT NOT NULL CHECK(length(sha256)=64)) STRICT;

CREATE TABLE manual_work_begin (
    operation_id TEXT PRIMARY KEY REFERENCES manual_work_runs(operation_id),
    relative_path TEXT NOT NULL,
    size_bytes INTEGER NOT NULL CHECK(size_bytes BETWEEN 1 AND 1048576),
    sha256 TEXT NOT NULL CHECK(length(sha256)=64)) STRICT;

CREATE TABLE manual_work_result (
    operation_id TEXT PRIMARY KEY REFERENCES manual_work_runs(operation_id),
    relative_path TEXT NOT NULL,
    size_bytes INTEGER NOT NULL CHECK(size_bytes BETWEEN 1 AND 1048576),
    sha256 TEXT NOT NULL CHECK(length(sha256)=64)) STRICT;

CREATE INDEX manual_work_tasks ON manual_work_runs(extension_id,provider_id,task_id);

CREATE TABLE work_identity_versions (
    extension_id TEXT NOT NULL,
    field TEXT NOT NULL,
    identity_version TEXT NOT NULL,
    PRIMARY KEY(extension_id,field)) STRICT;

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
    CHECK((parent_id IS NULL AND role_id='' AND server_id='') OR (parent_id IS NOT NULL AND role_id<>''))) STRICT;

CREATE TABLE work_effect_events (
    claim_id TEXT NOT NULL REFERENCES work_effect_claims(claim_id),
    sequence INTEGER NOT NULL CHECK(sequence>0),
    operation_id TEXT NOT NULL REFERENCES operations(operation_id),
    run_id TEXT NOT NULL,
    attempt_id TEXT NOT NULL,
    kind TEXT NOT NULL CHECK(kind IN ('acquired','submitted','confirmed')),
    evidence_code TEXT,
    PRIMARY KEY(claim_id,sequence),
    CHECK((kind='confirmed' AND evidence_code IS NOT NULL) OR (kind<>'confirmed' AND evidence_code IS NULL))) STRICT;

CREATE INDEX work_effect_children ON work_effect_claims(parent_id);

CREATE TABLE work_effect_rotations (
    claim_id TEXT NOT NULL REFERENCES work_effect_claims(claim_id),
    operation_id TEXT NOT NULL,
    source_operation_id TEXT NOT NULL REFERENCES operations(operation_id),
    run_id TEXT NOT NULL,
    attempt_id TEXT NOT NULL,
    before_secret_id TEXT NOT NULL,
    before_revision INTEGER NOT NULL CHECK(before_revision>0),
    after_secret_id TEXT NOT NULL,
    after_revision INTEGER NOT NULL CHECK(after_revision=before_revision+1),
    PRIMARY KEY(claim_id,operation_id),
    CHECK(before_secret_id<>after_secret_id)) STRICT;

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
    UNIQUE(claim_id,operation_id)) STRICT;

CREATE TABLE work_effect_epoch_results (
    claim_id TEXT NOT NULL,
    ordinal INTEGER NOT NULL,
    operation_id TEXT NOT NULL REFERENCES operations(operation_id),
    run_id TEXT NOT NULL,
    attempt_id TEXT NOT NULL,
    PRIMARY KEY(claim_id,ordinal),
    FOREIGN KEY(claim_id,ordinal) REFERENCES work_effect_epochs(claim_id,ordinal)) STRICT;

CREATE INDEX execution_target_roots_step ON execution_target_job_roots(journal_id,step_id);

INSERT INTO "recovery_index_state" VALUES(1,0,1);

INSERT INTO "scheduling_watermark" VALUES(1,0,NULL);
