"""Preserve immutable source manifests and deduplicated original file bytes."""
import hashlib
import json
import os
from pathlib import Path


def preserve_source(exported, output, manifest_bytes):
    exported = Path(exported).resolve(strict=True)
    output = Path(output).resolve(strict=True)
    records = json.loads(manifest_bytes)["files"]
    blobs = output / "source-blobs"
    manifests = output / "source-manifests"
    for directory in (blobs, manifests):
        if directory.is_symlink() or directory.is_junction():
            raise ValueError("Source evidence directory is a reparse point")
        directory.mkdir(exist_ok=True)
    for record in records:
        path = exported / record["path"]
        if path.is_symlink() or path.is_junction() or exported not in path.resolve(strict=True).parents:
            raise ValueError("Source evidence input leaves its owned export")
        data = path.read_bytes()
        fingerprint = hashlib.sha256(data).hexdigest()
        if fingerprint != record["sha256"] or len(data) != record["sizeBytes"]:
            raise ValueError("Source evidence input changed")
        write_immutable(blobs / fingerprint, data)
    fingerprint = hashlib.sha256(manifest_bytes).hexdigest()
    manifest = manifests / (fingerprint + ".json")
    write_immutable(manifest, manifest_bytes)
    return manifest


def write_immutable(path, data):
    if path.is_symlink() or path.is_junction():
        raise ValueError("Source evidence file is a reparse point")
    if path.exists():
        if path.read_bytes() != data:
            raise ValueError("Immutable source evidence bytes changed")
        return
    with path.open("xb") as stream:
        stream.write(data)
        stream.flush()
        os.fsync(stream.fileno())
