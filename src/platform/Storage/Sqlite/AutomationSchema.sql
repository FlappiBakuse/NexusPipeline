CREATE TABLE schema_info (
    singleton INTEGER PRIMARY KEY CHECK(singleton=1),
    format TEXT NOT NULL CHECK(format='nexus.state.domain'),
    schema_version INTEGER NOT NULL CHECK(schema_version=2),
    instance_id TEXT NOT NULL CHECK(length(instance_id)=36),
    domain_name TEXT NOT NULL CHECK(domain_name='automation'),
    contract_digest TEXT NOT NULL CHECK(length(contract_digest)=64),
    created_at TEXT NOT NULL
) STRICT;

CREATE TABLE automation_catalog_state (
    singleton INTEGER PRIMARY KEY CHECK(singleton=1),
    revision INTEGER NOT NULL CHECK(revision BETWEEN 0 AND 9007199254740991)) STRICT;

CREATE TABLE automation_roots (
    root_id TEXT PRIMARY KEY NOT NULL CHECK(length(root_id)=36),
    revision INTEGER NOT NULL CHECK(revision BETWEEN 1 AND 9007199254740991),
    name TEXT NOT NULL CHECK(length(name) BETWEEN 1 AND 120),
    kind INTEGER NOT NULL CHECK(kind IN (0,1)),
    path TEXT NOT NULL CHECK(length(path) BETWEEN 1 AND 32767)) STRICT;

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
    CHECK((kind=0 AND extension_id IS NULL) OR (kind=1 AND extension_id IS NOT NULL AND update_configuration_at_finish=1))) STRICT;

CREATE TABLE automation_paths (
    automation_id TEXT NOT NULL REFERENCES automations(automation_id),
    role INTEGER NOT NULL CHECK(role BETWEEN 0 AND 2),
    ordinal INTEGER NOT NULL CHECK(ordinal BETWEEN 0 AND 63),
    root_id TEXT NOT NULL REFERENCES automation_roots(root_id),
    relative_path TEXT NOT NULL CHECK(length(relative_path) BETWEEN 1 AND 1024),
    PRIMARY KEY(automation_id,role,ordinal)) STRICT;

CREATE TABLE automation_run_days_state (
    singleton INTEGER PRIMARY KEY CHECK(singleton=1),
    local_day INTEGER NOT NULL CHECK(local_day BETWEEN 0 AND 3652058)) STRICT;

CREATE TABLE "users" (
    user_id TEXT PRIMARY KEY NOT NULL CHECK(length(user_id)=36),
    revision INTEGER NOT NULL CHECK(revision BETWEEN 1 AND 9007199254740991),
    name TEXT NOT NULL CHECK(length(name) BETWEEN 1 AND 120),
    user_order INTEGER NOT NULL CHECK(user_order BETWEEN 0 AND 4095),
    binding_overrides_json TEXT NOT NULL CHECK(json_valid(binding_overrides_json) AND json_type(binding_overrides_json)='object'),
    notes TEXT NOT NULL DEFAULT '' CHECK(length(notes)<=4000),
    avatar TEXT NOT NULL DEFAULT '' CHECK(length(avatar)<=65536)) STRICT;

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
    UNIQUE(user_id,automation_id)) STRICT;

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
    schedule_enabled INTEGER NOT NULL DEFAULT 1 CHECK(schedule_enabled IN (0,1))) STRICT;

CREATE TABLE queue_items (
    queue_id TEXT NOT NULL REFERENCES queues(queue_id),
    ordinal INTEGER NOT NULL CHECK(ordinal BETWEEN 0 AND 4095),
    automation_id TEXT NOT NULL REFERENCES automations(automation_id),
    PRIMARY KEY(queue_id,ordinal)) STRICT;

CREATE TABLE "schedules" (
    schedule_id TEXT PRIMARY KEY NOT NULL CHECK(length(schedule_id)=36),
    revision INTEGER NOT NULL CHECK(revision BETWEEN 1 AND 9007199254740991),
    queue_id TEXT NOT NULL REFERENCES queues(queue_id),
    enabled INTEGER NOT NULL CHECK(enabled IN (0,1)),
    time_zone_id TEXT NOT NULL CHECK(length(time_zone_id) BETWEEN 1 AND 128),
    weekdays_json TEXT NOT NULL CHECK(length(weekdays_json) BETWEEN 1 AND 32),
    local_minute INTEGER NOT NULL CHECK(local_minute BETWEEN 0 AND 1439),
    schedule_order INTEGER NOT NULL DEFAULT 0 CHECK(schedule_order BETWEEN 0 AND 4095)) STRICT;

CREATE TABLE automation_mutations (
    operation_id TEXT PRIMARY KEY NOT NULL,
    catalog_revision INTEGER NOT NULL CHECK(catalog_revision BETWEEN 1 AND 9007199254740991)) STRICT;

CREATE TABLE automation_imports (
    operation_id TEXT PRIMARY KEY NOT NULL,
    journal_id TEXT NOT NULL UNIQUE,
    state INTEGER NOT NULL CHECK(state BETWEEN 0 AND 3),
    catalog_revision INTEGER CHECK(catalog_revision IS NULL OR catalog_revision BETWEEN 0 AND 9007199254740991),
    reason_code TEXT NOT NULL CHECK(length(reason_code) BETWEEN 1 AND 80),
    CHECK((state IN (0,3) AND catalog_revision IS NULL) OR (state IN (1,2) AND catalog_revision IS NOT NULL))) STRICT;

CREATE TABLE automation_assets (
    asset_id TEXT PRIMARY KEY NOT NULL CHECK(length(asset_id)=36),
    import_id TEXT NOT NULL REFERENCES automation_imports(operation_id),
    package_path TEXT NOT NULL CHECK(length(package_path) BETWEEN 1 AND 240),
    digest TEXT NOT NULL,
    UNIQUE(import_id,package_path)) STRICT;

CREATE TABLE automation_asset_owners (
    asset_id TEXT NOT NULL REFERENCES automation_assets(asset_id),
    automation_id TEXT NOT NULL REFERENCES automations(automation_id),
    PRIMARY KEY(asset_id,automation_id)) STRICT;

CREATE INDEX automation_assets_digest ON automation_assets(digest);

CREATE INDEX automation_asset_owners_automation ON automation_asset_owners(automation_id);

INSERT INTO "automation_catalog_state" VALUES(1,0);
