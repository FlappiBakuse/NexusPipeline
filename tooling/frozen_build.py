"""Reuse one successful test build only while its source and binary bytes match."""
import json
from pathlib import Path

from binary_inputs import hash_file, PROJECTS
from source_snapshot import digest, source_files, write_json
from build_inputs import solution_inputs, web_inputs, project_inputs
from sdk_locks import lock_arguments


def compile_receipt(output, component, profile, projects=None):
    path = output / "build-receipt.json"
    if not path.is_file() or (any(value.get("conservativeReasons") for value in component.values())
            if projects else component.get("conservativeReasons")):
        return None
    receipt = json.loads(path.read_bytes())
    if receipt.get("status") != "PASS" or receipt.get("profile") != profile:
        return None
    previous = {name: receipt.get("projectInputs", {}).get(name) for name in projects} if projects else receipt.get("componentInputs")
    if previous != component:
        return None
    lock_arguments(receipt.get('restoreLocks'))
    saved = receipt["binaryManifest"]
    binary_data = Path(saved["path"]).read_bytes()
    if digest(binary_data) != saved["sha256"] or json.loads(binary_data) != capture_build(output):
        raise ValueError("Frozen build artifacts changed")
    original = output / "runs" / receipt["runId"] / "build-receipt.json"
    if original.read_bytes() != path.read_bytes():
        raise ValueError("Original build evidence changed")
    return {"path": str(original), "sha256": digest(original.read_bytes())}


def capture_build(output):
    directory = output / "build/bin"
    records = []
    for path in sorted(directory.rglob("*")):
        if path.is_symlink() or path.is_junction():
            raise ValueError("Build artifacts contain a reparse point")
        if path.is_file():
            records.append({"path": path.relative_to(directory).as_posix(),
                            "sizeBytes": path.stat().st_size, "sha256": hash_file(path)})
    if not records:
        raise ValueError("A reusable build must contain binaries")
    return records


def reuse_build(root, output, group=None, projects=None):
    receipt_path = output / "build-receipt.json"
    if not receipt_path.exists():
        return False
    receipt = json.loads(receipt_path.read_bytes())
    if receipt.get("status") != "PASS" or receipt.get("profile") != "test" or "binaryManifest" not in receipt:
        return False
    source = receipt["source"]
    manifest = Path(source["sourceManifestPath"])
    data = manifest.read_bytes()
    if digest(data) != source["sourceSnapshotHash"]:
        raise ValueError("Frozen source manifest changed")
    expected = json.loads(data)["files"]
    from build_product import build_environment
    env = build_environment(output)
    previous = receipt.get("componentInputs", {})
    selected = projects if projects is not None else PROJECTS.get(group) if group else None
    if selected:
        component = project_inputs(root, "test", env, selected, web_inputs(root, env)["sha256"], previous.get("webArtifacts"))
    else:
        component = solution_inputs(root, "test", env)
        component.update(webInputs=web_inputs(root, env)["sha256"], webArtifacts=previous.get("webArtifacts"))
    if compile_receipt(output, component, "test", selected) is None:
        return False
    current = json.loads((output / "source-receipt.json").read_bytes())
    current_manifest = Path(current["sourceManifestPath"])
    if digest(current_manifest.read_bytes()) != current["sourceSnapshotHash"]:
        raise ValueError("Current source owner manifest changed")
    expected = json.loads(current_manifest.read_bytes())["files"]
    exported = Path(current["exportRoot"])
    actual = {path.relative_to(exported).as_posix() for path in exported.rglob("*") if path.is_file()}
    if actual != {record["path"] for record in expected}:
        raise ValueError("Frozen source export set changed")
    for record in expected:
        path = exported / record["path"]
        if path.is_symlink() or path.is_junction() or hash_file(path) != record["sha256"]:
            raise ValueError("Frozen source export changed")
    return True


def save_build(output, run_root):
    path = run_root / "build-binaries.json"
    write_json(path, capture_build(output))
    return {"path": str(path), "sha256": digest(path.read_bytes())}
