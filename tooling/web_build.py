"""Build the same Web assets for the Host and Electron in an owned external slot."""
import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time
import uuid

from build_product import update_export, build_environment
from source_snapshot import digest, write_json, external_directory
from build_inputs import web_inputs
from web_artifacts import artifact_records, reuse_web


def verify_lock(root):
    project = root / "apps/web"
    package = json.loads((project / "package.json").read_bytes())
    lock = json.loads((project / "package-lock.json").read_bytes())
    if lock.get("lockfileVersion") != 3 or lock.get("name") != package["name"] or lock.get("version") != package["version"]:
        raise ValueError("Web native dependency lock identity differs")
    for group in ("dependencies", "devDependencies"):
        if lock["packages"][""][group] != package[group]:
            raise ValueError("Web dependency pins differ from their native lock")


def build_web(root, output, update_lock=False, source=None, prepare_only=False):
    output = external_directory(root, output)
    source = source or update_export(root, output)
    frozen = Path(source["exportRoot"])
    work = output / "web-workspace"
    run = output / "web-runs" / (datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S") + "-" + uuid.uuid4().hex[:8])
    run.mkdir(parents=True)
    env = build_environment(output)
    component = web_inputs(frozen, env)
    reusable = None if update_lock or prepare_only else reuse_web(output, component)
    if reusable is not None:
        receipt = {**reusable, "source": source, "commands": [], "reused": True,
                   "scope": "Browser assets reused after current component and artifact verification"}
        write_json(run / "receipt.json", receipt)
        write_json(output / "web-receipt.json", receipt)
        return 0
    ownership = work / "owned-source.json"
    previous = json.loads(ownership.read_bytes()) if ownership.exists() else []
    names = []
    for name in ('startup-content.css', 'nexuspipeline.ico'):
        relative = 'apps/desktop/' + name
        names.append(relative)
        target = work / relative
        if target.is_symlink() or target.is_junction():
            raise ValueError("Linked Web build source")
        target.parent.mkdir(parents=True, exist_ok=True)
        data = (frozen / relative).read_bytes()
        if not target.exists() or target.read_bytes() != data:
            target.write_bytes(data)
    for prefix in ("apps/web", "sdk/frontend/src"):
        for path in sorted((frozen / prefix).rglob("*")):
            if not path.is_file():
                continue
            name = path.relative_to(frozen).as_posix()
            names.append(name)
            target = work / name
            if target.is_symlink() or target.is_junction():
                raise ValueError("Linked Web build source")
            target.parent.mkdir(parents=True, exist_ok=True)
            if not target.exists() or target.read_bytes() != path.read_bytes():
                target.write_bytes(path.read_bytes())
    for name in set(previous) - set(names):
        path = (work / name).resolve()
        if work not in path.parents:
            raise ValueError("Web source removal outside owned slot")
        path.unlink()
    write_json(ownership, names)
    project = work / "apps/web"
    env.setdefault("npm_config_cache", str(output.parent / "cache/npm"))
    npm = shutil.which("npm.cmd" if os.name == "nt" else "npm")
    if not npm:
        raise ValueError("npm is required to build Web source")
    lock = project / "package-lock.json"
    commands = []
    if update_lock:
        commands.append([npm, "install", "--package-lock-only", "--ignore-scripts", "--no-audit", "--no-fund"])
    else:
        if not lock.is_file():
            raise ValueError("Resolve the pinned Web lock with --update-lock first")
        marker = project / "node_modules/.nexus-lock"
        if not marker.exists() or marker.read_text() != digest(lock.read_bytes()):
            commands.append([npm, "ci", "--ignore-scripts", "--no-audit", "--no-fund"])
        node = shutil.which("node")
        if not prepare_only:
            commands.extend([[node, str(frozen / "tooling/frontend_names.mjs"), "--root", str(frozen), "--modules", str(project / "node_modules")],
                         [node, str(frozen / "tooling/frontend_boundaries.mjs"), "--root", str(frozen), "--modules", str(project / "node_modules"), "--output", str(run / 'boundaries.json')],
                         [node, str(project / "node_modules/vue-tsc/bin/vue-tsc.js"), "--noEmit"],
                         [node, str(project / "node_modules/vite/bin/vite.js"), "build"]])
    results = []
    code = 0
    for index, command in enumerate(commands):
        started = time.monotonic()
        log_path = run / f"{index}.log"
        print("web: " + " ".join(command), flush=True)
        with log_path.open("w", encoding="utf-8", newline="\n") as log:
            process = subprocess.Popen(command, cwd=project, env=env, stdout=subprocess.PIPE,
                                       stderr=subprocess.STDOUT, encoding="utf-8", errors="replace")
            for line in process.stdout:
                print(line, end="", flush=True)
                log.write(line)
            code = process.wait()
        responsibility = "lint" if any("frontend_names" in part or "frontend_boundaries" in part for part in command) else "typecheck" if any("vue-tsc" in part for part in command) else "build" if command[-1] == "build" else "preparation"
        results.append({"responsibility": responsibility, "command": command, "exitCode": code, "elapsedSeconds": time.monotonic() - started, "log": str(log_path)})
        if code:
            break
        if len(command) > 1 and command[1] == "ci":
            (project / "node_modules/.nexus-lock").write_text(digest(lock.read_bytes()), encoding="utf-8")
    if not code and update_lock:
        if (root / "apps/web/package.json").read_bytes() != (project / "package.json").read_bytes():
            raise ValueError("Web pins changed during lock resolution")
        (root / "apps/web/package-lock.json").write_bytes(lock.read_bytes().replace(b"\r\n", b"\n"))
    receipt = {"status": "PASS" if code == 0 else "FAIL", "source": source, "commands": results,
               "responsibilities": {"preparation": prepare_only or update_lock,
                   **{kind: "NOT_RUN" if not any(item["responsibility"] == kind for item in results) else
                       "PASS" if all(item["exitCode"] == 0 for item in results if item["responsibility"] == kind) else "FAIL"
                       for kind in ("lint", "typecheck", "build")}, "test": "NOT_RUN"},
               "componentInputs": component, "reused": False,
               "output": str(project if prepare_only else project / "dist"),
               "scope": "dependency-preparation-only" if prepare_only else "lock-resolution" if update_lock else "Web typecheck and production assets"}
    if code == 0 and not update_lock and not prepare_only:
        receipt["artifacts"] = artifact_records(project / "dist")
        original = run / "compiled.json"
        write_json(original, receipt)
        receipt.update(originalReceipt=str(original), originalReceiptSha256=digest(original.read_bytes()))
    write_json(run / "receipt.json", receipt)
    write_json(output / ("web-preparation.json" if prepare_only else "web-receipt.json"), receipt)
    return code


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--update-lock", action="store_true")
    parser.add_argument("--check-lock", action="store_true")
    parser.add_argument("--prepare-only", action="store_true", help="Sync owned Web source and locked dependencies without compiling product assets")
    args = parser.parse_args()
    if args.check_lock:
        verify_lock(Path(__file__).resolve().parent.parent)
        raise SystemExit(0)
    if args.output is None:
        parser.error("--output is required for Web builds")
    if args.prepare_only and args.update_lock:
        parser.error("--prepare-only cannot update the dependency lock")
    raise SystemExit(build_web(Path(__file__).resolve().parent.parent, args.output, args.update_lock, prepare_only=args.prepare_only))
