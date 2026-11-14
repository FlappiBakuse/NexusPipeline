CREATE TABLE schema_info (
    singleton INTEGER PRIMARY KEY CHECK(singleton=1),
    format TEXT NOT NULL CHECK(format='nexus.state.domain'),
    schema_version INTEGER NOT NULL CHECK(schema_version=2),
    instance_id TEXT NOT NULL CHECK(length(instance_id)=36),
    domain_name TEXT NOT NULL CHECK(domain_name='content'),
    contract_digest TEXT NOT NULL CHECK(length(contract_digest)=64),
    created_at TEXT NOT NULL
) STRICT;

CREATE TABLE blobs (
    digest TEXT PRIMARY KEY NOT NULL CHECK(length(digest) = 64),
    size_bytes INTEGER NOT NULL CHECK(size_bytes BETWEEN 0 AND 1073741824),
    relative_path TEXT NOT NULL UNIQUE) STRICT;
