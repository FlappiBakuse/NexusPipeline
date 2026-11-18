"""Produce the Plugins mirror of Host-owned release and artifact tools."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess

FILES = ("release_provenance.py", "release_github.py", "release_candidate.py", "git_source.py", "artifact_paths.mjs")
REPOSITORY = "FlappiBakuse/NexusPipeline"


def header(name):
    prefix = "//" if name.endswith(".mjs") else "#"
    return (prefix + " Generated from NexusPipeline/tooling/" + name + "; edit the Host owner.\n").encode()


def inputs(owner):
    return {name: (owner / "tooling" / name).read_bytes() for name in FILES}


def provenance(owner, values):
    result = subprocess.run(["git", "-C", str(owner), "log", "-1", "--format=%H", "--",
                             *["tooling/" + name for name in FILES]], capture_output=True, check=True)
    commit = result.stdout.decode().strip()
    clean = bool(commit)
    for name, data in values.items():
        original = subprocess.run(["git", "-C", str(owner), "show", commit + ":tooling/" + name], capture_output=True)
        clean = clean and original.returncode == 0 and original.stdout == data
    return dict(repository=REPOSITORY, sourceMode="committed" if clean else "local-uncommitted",
                sourceCommit=commit if clean else None, referenceCommit=commit or None)


def sync(owner, consumer, check=False):
    owner, consumer = owner.resolve(strict=True), consumer.resolve(strict=True)
    if owner == consumer or owner in consumer.parents or consumer in owner.parents:
        raise ValueError("Separate Host and Plugins source roots are required")
    values = inputs(owner)
    manifest = {"format": "nexus.shared-tooling", "version": 1, "owner": provenance(owner, values),
                "producer": "tooling/sync_shared_tooling.py", "files": [
                    {"path": "tooling/" + name, "sourceSha256": hashlib.sha256(data).hexdigest()}
                    for name, data in values.items()]}
    payload = (json.dumps(manifest, indent=2) + "\n").encode()
    targets = {consumer / "tooling" / name: header(name) + data for name, data in values.items()}
    targets[consumer / "tooling/shared-tooling.json"] = payload
    changed = [path for path, data in targets.items() if not path.is_file() or path.read_bytes() != data]
    if check and changed:
        raise ValueError("Shared tools are not synchronized: " + ", ".join(path.name for path in changed))
    if not check:
        for path in changed:
            path.write_bytes(targets[path])
    return manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--plugins-root", required=True, type=Path)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    manifest = sync(Path(__file__).resolve().parents[1], args.plugins_root, args.check)
    print(json.dumps({"status": "PASS", "mode": "check" if args.check else "generate", "owner": manifest["owner"], "files": len(FILES)}))


if __name__ == "__main__":
    main()
