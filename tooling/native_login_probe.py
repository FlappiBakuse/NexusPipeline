"""Qualify the real isolated Electron login window against loopback synthetic pages."""
import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import shutil
import subprocess
import time

from desktop_bundle import require_no_links, safe_name, sha
from source_snapshot import digest, source_files, write_json


def prepare_runtime(root, source, cache):
    require_no_links(source)
    require_no_links(cache)
    lock = json.loads((root / "apps/desktop/runtime-lock.json").read_bytes())
    bundle = json.loads((source / "desktop-bundle.json").read_bytes())
    if bundle["status"] != "PASS" or bundle["runtime"] != lock:
        raise ValueError("Runtime bundle must match the pinned official runtime")
    records = []
    for item in bundle["files"]:
        if item["owner"] != "electron-runtime":
            continue
        name = safe_name(item["path"])
        original = source / name
        require_no_links(original)
        if original.stat().st_size != item["sizeBytes"] or sha(original) != item["sha256"]:
            raise ValueError("Retained runtime bytes changed")
        target = cache / ("electron.exe" if name == "NexusPipeline.Desktop.exe" else name)
        require_no_links(target)
        if target.exists():
            if target.stat().st_size != item["sizeBytes"] or sha(target) != item["sha256"]:
                raise ValueError("Fixture runtime cache changed")
        else:
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(original, target)
        records.append({**item, "path": target.relative_to(cache).as_posix()})
    expected = {item["path"] for item in records}
    actual = {item.relative_to(cache).as_posix() for item in cache.rglob("*") if item.is_file()}
    if actual - expected - {"runtime-receipt.json"} or "resources/default_app.asar" not in expected:
        raise ValueError("Unexpected fixture runtime membership")
    receipt = {"status": "PASS", "runtime": lock, "files": records, "sourceBundle": str(source / "desktop-bundle.json"),
               "retention": "One reusable native qualification dependency cache; remove after final same-source delivery and acceptance"}
    write_json(cache / "runtime-receipt.json", receipt)
    return cache / "electron.exe", receipt


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runtime-root", type=Path, required=True)
    parser.add_argument("--cache-root", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    for value in (args.runtime_root, args.cache_root, args.output):
        if not value.is_absolute() or root == value.resolve() or root in value.resolve().parents:
            raise ValueError("Explicit external absolute paths are required")
        require_no_links(value)
    run = args.output / datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    run.mkdir(parents=True)
    files = source_files(root)
    write_json(run / "source-manifest.json", {"sourceMode": "local-uncommitted", "sourceCommit": None, "sourceTree": None, "files": files})
    executable, runtime = prepare_runtime(root, args.runtime_root, args.cache_root)
    command = [str(executable), str(root / "tests/fixtures/native-login.cjs"), "--nexus-test-root=" + str(run)]
    temporary = run / "tmp"
    temporary.mkdir()
    env = dict(os.environ, TEMP=str(temporary), TMP=str(temporary))
    env.pop("ELECTRON_RUN_AS_NODE", None)
    started = time.monotonic()
    error = None
    code = None
    with (run / "native.log").open("wb") as log:
        child = subprocess.Popen(command, cwd=run, env=env, stdout=log, stderr=subprocess.STDOUT,
                                 creationflags=subprocess.CREATE_NO_WINDOW)
        try:
            code = child.wait(timeout=45)
        except subprocess.TimeoutExpired:
            error = "native_fixture_timeout"
            child.kill()
            child.wait(timeout=10)
    receipt = run / "receipt.json"
    native = json.loads(receipt.read_bytes()) if receipt.exists() else None
    unchanged = source_files(root) == files
    no_canary = b"synthetic_fixture_login_token" not in (run / "native.log").read_bytes()
    passed = code == 0 and native is not None and native["status"] == "PASS" and bool(native["cases"]) and all(case["status"] == "PASS" for case in native["cases"]) and unchanged and no_canary
    result = {"status": "PASS" if passed else "FAIL", "command": command, "exitCode": code, "error": error,
              "elapsedSeconds": time.monotonic() - started, "runtimeVersion": runtime["runtime"]["version"],
              "binarySha256": sha(executable), "sourceManifest": str(run / "source-manifest.json"),
              "sourceSnapshotHash": digest((run / "source-manifest.json").read_bytes()), "sourceUnchanged": unchanged,
              "noCanaryInLogs": no_canary, "nativeReceipt": str(receipt) if native else None,
              "scope": "Actual isolated Electron login windows and synthetic loopback only; production Host native TCP integration is separate"}
    write_json(run / "command.json", result)
    print(json.dumps({"status": result["status"], "receipt": str(run / "command.json"), "native": native}))
    return 0 if passed else 1


if __name__ == "__main__":
    raise SystemExit(main())
