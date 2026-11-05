"""Fingerprint application build artifacts used by a native test invocation."""
from pathlib import Path

from source_snapshot import digest, write_json


PROJECTS = {
    "contracts": ["NexusPipeline.ContractTests"],
    "core": ["NexusPipeline.CoreTests"],
    "windows": ["NexusPipeline.WindowsTests", "OwnedWriter", "NexusPipeline.Host"],
    "windows-secrets": ["NexusPipeline.WindowsTests"],
    "storage": ["NexusPipeline.StorageTests", "OwnedWriter", "NexusPipeline.Host"],
    "storage-checkin": ["NexusPipeline.StorageTests", "OwnedWriter", "NexusPipeline.Host"],
    "storage-wal": ["NexusPipeline.StorageTests", "OwnedWriter", "NexusPipeline.Host"],
    "storage-startup": ["NexusPipeline.StorageTests", "OwnedWriter", "NexusPipeline.Host"],
    "storage-secrets": ["NexusPipeline.StorageTests", "OwnedWriter", "NexusPipeline.Host"],
    "storage-settings": ["NexusPipeline.StorageTests", "OwnedWriter", "NexusPipeline.Host"],
    "storage-config-revisions": ["NexusPipeline.StorageTests", "OwnedWriter", "NexusPipeline.Host"],
    "storage-config-output": ["NexusPipeline.StorageTests", "OwnedWriter", "NexusPipeline.Host"],
    "storage-config-controller": ["NexusPipeline.StorageTests", "OwnedWriter", "NexusPipeline.Host"],
    "storage-config-cross-volume": ["NexusPipeline.StorageTests", "OwnedWriter", "NexusPipeline.Host"],
    "storage-automation": ["NexusPipeline.StorageTests", "OwnedWriter", "NexusPipeline.Host"],
    "storage-judgement": ["NexusPipeline.StorageTests", "OwnedWriter", "NexusPipeline.Host"],
    "storage-file-logs": ["NexusPipeline.StorageTests", "OwnedWriter", "NexusPipeline.Host"],
    "storage-execution": ["NexusPipeline.StorageTests", "OwnedWriter", "NexusPipeline.Host"],
    "storage-provider": ["NexusPipeline.StorageTests", "OwnedWriter", "NexusPipeline.Host"],
    "storage-android": ["NexusPipeline.StorageTests", "OwnedWriter", "NexusPipeline.Host"],
    "host-boundary": ["NexusPipeline.Host"],
    "tooling": ["NexusPipeline.CoreTests"],
}

for projects in PROJECTS.values():
    if "OwnedWriter" in projects:
        projects.append("TargetGame")
for group in ("storage", "storage-execution", "storage-android"):
    PROJECTS[group].append("AndroidTool")
PROJECTS["storage"].extend(["ManagedExtension", "NexusPipeline.Maintenance"])


def capture_binary_inputs(build_root, run_root, group, projects=None):
    binary_root = build_root / "build/bin"
    records = []
    selected = PROJECTS[group] if projects is None else projects
    for project in selected:
        directory = binary_root / project / "release"
        if not directory.is_dir():
            raise ValueError(f"Missing native suite artifacts: {project}")
        for path in sorted(directory.rglob("*")):
            if path.is_symlink() or path.is_junction():
                raise ValueError("Native suite artifacts contain a reparse point")
            if path.is_file():
                records.append({"path": path.relative_to(binary_root).as_posix(),
                                "sizeBytes": path.stat().st_size, "sha256": hash_file(path)})
    manifest = run_root / "application-binary-inputs.json"
    write_json(manifest, {"scope": "Application build artifacts; shared framework and OS identities are recorded separately",
                          "group": group, "projects": selected, "binaryRoot": str(binary_root), "files": records})
    return {"path": str(manifest), "sha256": digest(manifest.read_bytes()), "fileCount": len(records)}, records


def verify_binary_inputs(build_root, records):
    expected = {record["path"] for record in records}
    actual = set()
    for project in {Path(record["path"]).parts[0] for record in records}:
        for path in (build_root / "build/bin" / project / "release").rglob("*"):
            if path.is_file():
                actual.add(path.relative_to(build_root / "build/bin").as_posix())
    if expected != actual:
        raise ValueError("Application artifact set changed during the native suite")
    for record in records:
        path = build_root / "build/bin" / record["path"]
        if not path.is_file() or path.stat().st_size != record["sizeBytes"] or hash_file(path) != record["sha256"]:
            raise ValueError("Application binary input changed during the native suite")


def hash_file(path):
    import hashlib

    hasher = hashlib.sha256()
    with Path(path).open("rb") as stream:
        while chunk := stream.read(1_048_576):
            hasher.update(chunk)
    return hasher.hexdigest()
