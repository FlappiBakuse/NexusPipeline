CREATE TABLE schema_info (
    singleton INTEGER PRIMARY KEY CHECK(singleton=1),
    format TEXT NOT NULL CHECK(format='nexus.state.domain'),
    schema_version INTEGER NOT NULL CHECK(schema_version=2),
    instance_id TEXT NOT NULL CHECK(length(instance_id)=36),
    domain_name TEXT NOT NULL CHECK(domain_name='extensions'),
    contract_digest TEXT NOT NULL CHECK(length(contract_digest)=64),
    created_at TEXT NOT NULL
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
    first_operation_id TEXT NOT NULL UNIQUE,
    PRIMARY KEY(format,channel,sequence),
    UNIQUE(format,channel,generation_id),
    UNIQUE(format,channel,sequence,catalog_sha)) STRICT;

CREATE TABLE extension_catalog_heads (
    format TEXT NOT NULL CHECK(format='nexus.store.catalog'),
    channel TEXT NOT NULL CHECK(channel IN ('stable','develop')),
    sequence INTEGER NOT NULL CHECK(sequence BETWEEN 1 AND 9007199254740991),
    catalog_sha TEXT NOT NULL CHECK(length(catalog_sha)=64),
    PRIMARY KEY(format,channel),
    FOREIGN KEY(format,channel,sequence,catalog_sha)
        REFERENCES extension_catalog_acceptances(format,channel,sequence,catalog_sha)) STRICT;

CREATE TABLE extension_installation_revisions (
    extension_id TEXT NOT NULL CHECK(length(extension_id) BETWEEN 1 AND 80),
    revision INTEGER NOT NULL CHECK(revision BETWEEN 1 AND 9007199254740991),
    is_installed INTEGER NOT NULL CHECK(is_installed IN (0,1)),
    package_version TEXT NOT NULL CHECK(length(package_version) BETWEEN 1 AND 80),
    package_kind TEXT NOT NULL CHECK(package_kind IN ('managed-code','data-specialized')),
    archive_sha256 TEXT NOT NULL CHECK(length(archive_sha256)=64),
    manifest_sha256 TEXT NOT NULL CHECK(length(manifest_sha256)=64),
    journal_id TEXT NOT NULL UNIQUE,
    footprint_sha256 TEXT NOT NULL CHECK(length(footprint_sha256)=64),
    footprint_size INTEGER NOT NULL CHECK(footprint_size BETWEEN 1 AND 2097152),
    PRIMARY KEY(extension_id,revision)) STRICT;

CREATE TABLE extension_installations (
    extension_id TEXT PRIMARY KEY NOT NULL CHECK(length(extension_id) BETWEEN 1 AND 80),
    revision INTEGER NOT NULL CHECK(revision BETWEEN 1 AND 9007199254740991),
    FOREIGN KEY(extension_id,revision) REFERENCES extension_installation_revisions(extension_id,revision)) STRICT;

CREATE TABLE extension_documents (
    extension_id TEXT NOT NULL CHECK(length(extension_id) BETWEEN 1 AND 80),
    name TEXT NOT NULL CHECK(length(name) BETWEEN 1 AND 80),
    revision INTEGER NOT NULL CHECK(revision BETWEEN 1 AND 9007199254740991),
    content BLOB NOT NULL CHECK(length(content) BETWEEN 1 AND 1048576),
    sha256 TEXT NOT NULL CHECK(length(sha256)=64),
    operation_id TEXT NOT NULL UNIQUE,
    PRIMARY KEY(extension_id,name)) STRICT;

CREATE TABLE extension_document_commits (
    operation_id TEXT PRIMARY KEY NOT NULL,
    extension_id TEXT NOT NULL,
    name TEXT NOT NULL,
    revision INTEGER NOT NULL CHECK(revision BETWEEN 1 AND 9007199254740991),
    sha256 TEXT NOT NULL CHECK(length(sha256)=64)) STRICT;

CREATE TABLE extension_images (
    extension_id TEXT NOT NULL CHECK(length(extension_id) BETWEEN 1 AND 80),
    image_id TEXT NOT NULL CHECK(length(image_id)=64),
    content_type TEXT NOT NULL CHECK(content_type IN ('image/png','image/jpeg','image/webp')),
    content BLOB NOT NULL CHECK(length(content) BETWEEN 1 AND 8388608),
    width INTEGER NOT NULL CHECK(width BETWEEN 1 AND 8192),
    height INTEGER NOT NULL CHECK(height BETWEEN 1 AND 8192),
    PRIMARY KEY(extension_id,image_id)) STRICT;

CREATE TABLE extension_image_references (
    extension_id TEXT NOT NULL,
    document_name TEXT NOT NULL,
    image_id TEXT NOT NULL,
    PRIMARY KEY(extension_id,document_name,image_id),
    FOREIGN KEY(extension_id,document_name) REFERENCES extension_documents(extension_id,name),
    FOREIGN KEY(extension_id,image_id) REFERENCES extension_images(extension_id,image_id)) STRICT;
