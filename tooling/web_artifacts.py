"""Bind reused browser assets to their original immutable build receipt."""
import json
from pathlib import Path

from source_snapshot import digest


def artifact_records(directory):
    directory = Path(directory)
    records = []
    for path in sorted(directory.rglob("*")):
        if path.is_symlink() or path.is_junction():
            raise ValueError("Browser artifact cannot be a link")
        if path.is_file():
            data = path.read_bytes()
            records.append({"path": path.relative_to(directory).as_posix(), "sizeBytes": len(data), "sha256": digest(data)})
    if not records:
        raise ValueError("Reusable browser assets are missing")
    return records


def reuse_web(output, component):
    latest = output / "web-receipt.json"
    if not latest.is_file():
        return None
    previous = json.loads(latest.read_bytes())
    if previous.get("status") != "PASS" or previous.get("componentInputs") != component or not previous.get("artifacts"):
        return None
    original = Path(previous.get("originalReceipt", ""))
    if not original.is_file() or digest(original.read_bytes()) != previous.get("originalReceiptSha256"):
        raise ValueError("Original browser build evidence changed")
    compiled = json.loads(original.read_bytes())
    if any(previous.get(key) != compiled.get(key) for key in ("status", "componentInputs", "artifacts", "output", "responsibilities")):
        raise ValueError("Browser reuse metadata differs from its original compiler evidence")
    if artifact_records(previous["output"]) != previous["artifacts"]:
        raise ValueError("Reusable browser artifact set or bytes changed")
    return previous
