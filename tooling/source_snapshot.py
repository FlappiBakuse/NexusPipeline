"""Export original source bytes with a stable, non-self-referential identity."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
from source_evidence import preserve_source
from git_source import clean_commit, git_identity

ALGORITHM = "nexus-source-manifest-sha256-v2-lf"


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes((json.dumps(value, ensure_ascii=False, indent=2) + "\n").encode("utf-8"))


def digest(data):
    return hashlib.sha256(data).hexdigest()


def ordinary_path(path):
    path = Path(path)
    if not path.is_absolute():
        raise ValueError("An explicit absolute path is required")
    for item in (path, *path.parents):
        if item.is_symlink() or item.is_junction():
            raise ValueError("Paths cannot traverse a link or reparse point")
    return path


def external_directory(root, output):
    source = ordinary_path(root).resolve(strict=True)
    target = ordinary_path(output).resolve()
    compared = []
    for value in (source, target):
        text = str(value)
        if os.name == "nt":
            text = "\\\\" + text[8:] if text.startswith("\\\\?\\UNC\\") else text.removeprefix("\\\\?\\")
        compared.append(Path(text))
    source_key, target_key = compared
    if source_key == target_key or source_key in target_key.parents or target_key in source_key.parents:
        raise ValueError("Output must be outside and disjoint from the source tree")
    return target


def external_output(root, output):
    target = external_directory(root, output)
    if target.exists() and any(target.iterdir()):
        raise ValueError("Output must be a new or empty directory; existing bytes are preserved")
    return target


def source_files(root):
    result = subprocess.run(
        ["git", "-C", str(root), "ls-files", "--cached", "--others", "--exclude-standard", "-z"],
        check=True, capture_output=True,
    )
    paths = set(result.stdout.decode("utf-8").split("\0")) - {""}
    deleted = subprocess.run(["git", "-C", str(root), "ls-files", "--deleted", "-z"],
                             check=True, capture_output=True)
    paths.difference_update(deleted.stdout.decode("utf-8").split("\0"))
    folded = {}
    records = []
    for relative in sorted(paths, key=lambda p: p.encode("utf-8")):
        relative = relative.replace("\\", "/")
        if relative.casefold() in folded:
            raise ValueError(f"Case-colliding source paths: {folded[relative.casefold()]}, {relative}")
        folded[relative.casefold()] = relative
        path = root / relative
        if path.is_symlink() or path.is_junction():
            raise ValueError(f"Source links are not permitted: {relative}")
        for parent in path.parents:
            if parent == root:
                break
            if parent.is_symlink() or parent.is_junction():
                raise ValueError(f"Source link parent: {relative}")
        if not path.is_file() or root not in path.resolve().parents:
            raise ValueError(f"Missing, external or non-file source input: {relative}")
        relative = path.resolve(strict=True).relative_to(root).as_posix()
        data = path.read_bytes()
        records.append({"path": relative, "sizeBytes": len(data), "sha256": digest(data), "mode": "100644"})
    if not records:
        raise ValueError("A source snapshot cannot be empty")
    return sorted(records, key=lambda record: record["path"].encode("utf-8"))


def export_source(root, output, git_commit=None):
    root = Path(root).resolve(strict=True)
    output = external_output(root, output)
    records = source_files(root)
    identity = git_identity(root, git_commit, records) if git_commit is not None else {
        "sourceMode": "local-uncommitted", "sourceCommit": None, "sourceTree": None}
    output.mkdir(parents=True, exist_ok=True)
    exported = output / "source"
    exported.mkdir()
    for record in records:
        data = (root / record["path"]).read_bytes()
        if digest(data) != record["sha256"]:
            raise ValueError(f"Source changed during export: {record['path']}")
        destination = exported / record["path"]
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(data)
    manifest_path = output / "source-manifest.json"
    write_json(manifest_path, {"manifestAlgorithm": ALGORITHM, "files": records})
    manifest_path = preserve_source(exported, output, manifest_path.read_bytes())
    head = subprocess.run(["git", "-C", str(root), "rev-parse", "--verify", "HEAD"], capture_output=True)
    status = subprocess.run(["git", "-C", str(root), "status", "--porcelain=v1"], capture_output=True, check=True)
    if git_commit is not None and clean_commit(root, git_commit) != identity["sourceTree"]:
        raise ValueError("Git source changed during export")
    receipt = {
        **identity,
        "referenceHead": head.stdout.decode().strip() if head.returncode == 0 else None,
        "hasUncommittedChanges": bool(status.stdout),
        "manifestAlgorithm": ALGORITHM,
        "sourceSnapshotHash": digest(manifest_path.read_bytes()),
        "sourceManifestPath": str(manifest_path),
        "exportRoot": str(exported),
        "fileCount": len(records),
    }
    write_json(output / "source-receipt.json", receipt)
    return receipt
