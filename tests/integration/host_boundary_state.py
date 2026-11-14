"""Compare live fixture data while verifying its independent empty scheduler ticks."""
import contextlib
from datetime import datetime, timezone
import hashlib
import json
import sqlite3
import uuid

DOMAINS = ("runtime", "automation", "configuration", "records", "notifications", "extensions", "system", "content")


def strict_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate_scheduler_metadata")
        result[key] = value
    return result


def database_snapshot(runtime):
    with contextlib.closing(sqlite3.connect((runtime / "state/runtime.db").as_uri() + "?mode=ro", uri=True)) as database:
        for domain in DOMAINS[1:]:
            database.execute("ATTACH DATABASE ? AS " + domain, ((runtime / f"state/{domain}.db").as_uri() + "?mode=ro",))
        database.execute("BEGIN")
        identity = json.loads((runtime / "instance.json").read_bytes())["instanceId"]
        for domain in DOMAINS:
            alias = "main" if domain == "runtime" else domain
            schema = database.execute(f"SELECT format,schema_version,instance_id,domain_name FROM {alias}.schema_info;").fetchall()
            if schema != [("nexus.state.domain", 2, identity, domain)]:
                raise ValueError("live_fixture_schema_unknown")
        if any(database.execute("SELECT count(*) FROM automation." + table).fetchone()[0] for table in ("queues", "schedules")):
            raise ValueError("live_fixture_requires_no_scheduled_work")
        excluded = verified_empty_minutes(database, identity)
        definitions, contents = {}, {}
        for domain in DOMAINS:
            alias = "main" if domain == "runtime" else domain
            definitions[domain] = database.execute(f"SELECT type,name,tbl_name,sql FROM {alias}.sqlite_schema ORDER BY type,name;").fetchall()
            tables = [row[0] for row in database.execute(f"SELECT name FROM {alias}.sqlite_schema WHERE type='table' ORDER BY name;")]
            for table in tables:
                if domain == "runtime" and table in ("schedule_minute_claims", "scheduling_watermark"):
                    continue
                rows = database.execute(f'SELECT * FROM {alias}."' + table.replace('"', '""') + '" LIMIT 4097;').fetchall()
                if len(rows) > 4096:
                    raise ValueError("fixture_snapshot_row_limit")
                if domain == "runtime" and table == "operations":
                    rows = [row for row in rows if row[0] not in excluded]
                encoded = [json.dumps(row, ensure_ascii=False, default=lambda value: {"blobHex": value.hex()}) for row in rows]
                contents[domain + "." + table] = sorted(encoded)
        encoded = json.dumps({"schema": definitions, "tables": contents}, ensure_ascii=False, sort_keys=True).encode()
        return hashlib.sha256(encoded).hexdigest()


def verified_empty_minutes(database, instance_id):
    rows = database.execute("SELECT m.operation_id,m.document_json,m.occurrence_count,m.watermark_revision,m.utc_minute,"
        "o.subject_id,o.request_digest,o.operation_kind,o.status,o.initial_journal_id,o.created_at FROM schedule_minute_claims m "
        "JOIN operations o ON o.operation_id=m.operation_id ORDER BY m.watermark_revision;").fetchall()
    excluded = set()
    highest = None
    catalog_revision = database.execute("SELECT revision FROM automation.automation_catalog_state;").fetchone()[0]
    previous_catalog_revision = 0
    for index, row in enumerate(rows):
        operation, text, count, revision, minute, subject, digest, kind, status, journal, created = row
        document = json.loads(text, object_pairs_hook=strict_object)
        scan = document["scan"]
        instant = datetime.fromisoformat(document["observedAtUtc"])
        expected_id = str(uuid.UUID(bytes=hashlib.sha256(
            f"nexus.schedule.minute/1/{instance_id}/{minute}".encode()).digest()[:16]))
        if (set(document) != {"format", "version", "instanceId", "subjectId", "observedAtUtc", "settingsRevision", "scan"}
            or document["format"] != "nexus.schedule.minute" or document["version"] != 1
            or document["instanceId"] != instance_id or document["subjectId"] != "host:scheduler"
            or type(document["version"]) is not int or type(document["settingsRevision"]) is not int
            or document["settingsRevision"] < 0 or instant.utcoffset().total_seconds() != 0
            or int(instant.timestamp()) // 60 != minute or instant > datetime.now(timezone.utc)
            or datetime.fromisoformat(created) != instant
            or set(scan) != {"expectedWatermarkRevision", "catalogRevision", "highestUtcMinute", "occurrences"}
            or scan["occurrences"] != [] or count != 0 or revision != index
            or scan["expectedWatermarkRevision"] != revision or scan["highestUtcMinute"] != minute
            or type(scan["catalogRevision"]) is not int
            or not previous_catalog_revision <= scan["catalogRevision"] <= catalog_revision
            or operation != expected_id or subject != "host:scheduler" or kind != "schedule.minute"
            or status != "completed" or journal is not None or digest != hashlib.sha256(text.encode()).hexdigest()
            or highest is not None and minute <= highest):
            raise ValueError("unexpected_background_scheduler_change")
        excluded.add(operation)
        highest = minute
        previous_catalog_revision = scan["catalogRevision"]
    actual = {row[0] for row in database.execute("SELECT operation_id FROM operations WHERE operation_kind='schedule.minute';")}
    if actual != excluded or database.execute("SELECT count(*) FROM schedule_minute_claims;").fetchone()[0] != len(rows):
        raise ValueError("scheduler_operation_unbound")
    watermark = database.execute("SELECT revision,highest_utc_minute FROM scheduling_watermark;").fetchall()
    if watermark != [(len(rows), highest)]:
        raise ValueError("scheduler_watermark_unbound")
    return excluded
