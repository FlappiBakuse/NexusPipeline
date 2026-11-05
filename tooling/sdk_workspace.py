"""Reuse SDK workspaces only while every owned source byte still matches its receipt."""
import json
from pathlib import Path

from source_snapshot import digest, write_json

OUTPUTS = {"node_modules", "dist", "bin", "obj", ".git"}


def records(root):
    root = Path(root).resolve(strict=True)
    result = []
    pending = [root]
    while pending:
        directory = pending.pop()
        for path in sorted(directory.iterdir()):
            if path.name in OUTPUTS:
                continue
            if path.is_symlink() or path.is_junction():
                raise ValueError("SDK source workspace contains a link")
            if path.is_dir():
                pending.append(path)
            elif path.is_file():
                data = path.read_bytes()
                result.append({"path": path.relative_to(root).as_posix(), "sizeBytes": len(data), "sha256": digest(data)})
    if len(result) > 128 or sum(item["sizeBytes"] for item in result) > 8 * 1024 * 1024:
        raise ValueError("SDK workspace source budget exceeded")
    return sorted(result, key=lambda item: item["path"])


def synchronize(source, target, ownership):
    source = Path(source).resolve(strict=True)
    target = Path(target).resolve()
    ownership = Path(ownership).resolve()
    if source == target or source in target.parents or target in source.parents or ownership == target or target in ownership.parents:
        raise ValueError("SDK source and output ownership must be disjoint")
    for path in (source, target, ownership):
        if any(parent.is_symlink() or parent.is_junction() for parent in (path, *path.parents)):
            raise ValueError("SDK workspace crosses a link")
    incoming = records(source)
    if not incoming:
        raise ValueError("SDK workspace source cannot be empty")
    existing = records(target) if target.exists() else []
    if ownership.exists():
        previous = json.loads(ownership.read_bytes())
        if previous["target"] != str(target) or existing != previous["records"]:
            raise ValueError("SDK workspace contains changed or unowned source bytes")
    elif existing and existing != incoming:
        raise ValueError("Existing SDK workspace cannot be adopted without exact incoming bytes")
    current = {item["path"] for item in incoming}
    for item in existing:
        if item["path"] not in current:
            path = (target / item["path"]).resolve(strict=True)
            if target not in path.parents:
                raise ValueError("SDK source removal leaves the owned workspace")
            path.unlink()
    for item in incoming:
        data = (source / item["path"]).read_bytes()
        if digest(data) != item["sha256"]:
            raise ValueError("SDK source changed while synchronizing")
        destination = target / item["path"]
        destination.parent.mkdir(parents=True, exist_ok=True)
        if not destination.exists() or destination.read_bytes() != data:
            destination.write_bytes(data)
    write_json(ownership, {"target": str(target), "records": incoming})
    return incoming
