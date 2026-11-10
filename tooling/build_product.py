"""Build a frozen byte export with explicitly external dependencies and outputs."""
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import subprocess
import sys
import time
import uuid
import xml.etree.ElementTree as ET

from source_snapshot import ALGORITHM, digest, export_source, source_files, write_json, external_directory
from source_evidence import preserve_source
from frozen_build import save_build, compile_receipt
from build_inputs import solution_inputs, project_inputs
from source_names import verify_names
from source_checks import check_source
from dotnet_sdk import resolve_sdk, sdk_environment, sdk_key
from sdk_locks import prepare_locks, lock_arguments


def update_export(root, output):
    output = external_directory(root, output)
    receipt_path = output / "source-receipt.json"
    if not receipt_path.exists():
        return export_source(root, output)
    import json

    previous = json.loads(receipt_path.read_text(encoding="utf-8"))
    exported = output / "source"
    manifest_path = Path(previous["sourceManifestPath"])
    expected_manifest = output / "source-manifests" / (previous["sourceSnapshotHash"] + ".json")
    if manifest_path.resolve() != expected_manifest or Path(previous["exportRoot"]).resolve() != exported:
        raise ValueError("Build slot ownership does not match its source receipt")
    manifest_bytes = manifest_path.read_bytes()
    if digest(manifest_bytes) != previous["sourceSnapshotHash"]:
        raise ValueError("Previous source manifest was changed")
    old_records = json.loads(manifest_bytes)["files"]
    for record in old_records:
        path = exported / record["path"]
        if not path.is_file() or digest(path.read_bytes()) != record["sha256"]:
            raise ValueError(f"Export contains changed or missing source: {path}")
    existing = {path.relative_to(exported).as_posix() for path in exported.rglob("*") if path.is_file()}
    if existing != {record["path"] for record in old_records}:
        raise ValueError("Export contains unowned files; refusing to update")
    records = source_files(root)
    pending_bytes = {}
    for record in records:
        data = (root / record["path"]).read_bytes()
        if digest(data) != record["sha256"]:
            raise ValueError("Source changed before updating the build slot")
        pending_bytes[record["path"]] = data
    current_paths = {record["path"] for record in records}
    preserve_source(exported, output, manifest_bytes)
    for record in old_records:
        if record["path"] not in current_paths:
            target = (exported / record["path"]).resolve(strict=True)
            if exported not in target.parents:
                raise ValueError("Removal would leave the owned source export")
            target.unlink()
    for record in records:
        data = pending_bytes[record["path"]]
        target = exported / record["path"]
        if not target.exists() or digest(target.read_bytes()) != record["sha256"]:
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
    mutable_manifest = output / "source-manifest.json"
    write_json(mutable_manifest, {"manifestAlgorithm": ALGORITHM, "files": records})
    manifest_path = preserve_source(exported, output, mutable_manifest.read_bytes())
    head = subprocess.run(["git", "-C", str(root), "rev-parse", "--verify", "HEAD"], capture_output=True)
    status = subprocess.run(["git", "-C", str(root), "status", "--porcelain=v1"], capture_output=True, check=True)
    previous.update(sourceSnapshotHash=digest(manifest_path.read_bytes()), sourceManifestPath=str(manifest_path), fileCount=len(records),
                    hasUncommittedChanges=bool(status.stdout), referenceHead=head.stdout.decode().strip() if head.returncode == 0 else None,
                    manifestAlgorithm=ALGORITHM, sourceMode="local-uncommitted",
                    sourceCommit=None, sourceTree=None)
    write_json(receipt_path, previous)
    return previous


def build_environment(output):
    env = os.environ.copy()
    cache = output.parent / "cache"
    temporary = cache / "tmp"
    temporary.mkdir(parents=True, exist_ok=True)
    env.update({
        "TEMP": str(temporary), "TMP": str(temporary),
        "DOTNET_CLI_HOME": str(cache / "dotnet-home"),
        "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1",
        "DOTNET_NOLOGO": "1", "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
        "DOTNET_CLI_UI_LANGUAGE": "en-US",
        "NUGET_PACKAGES": str(cache / "nuget"),
        "NUGET_HTTP_CACHE_PATH": str(cache / "nuget-http"),
        "NUGET_PLUGINS_CACHE_PATH": str(cache / "nuget-plugins"),
        "MSBUILDDISABLENODEREUSE": "1",
    })
    return env


def build_product(root, output, profile, projects=None):
    output = external_directory(root, output)
    source = update_export(root, output)
    run_id = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S") + "-" + uuid.uuid4().hex[:8]
    run_root = output / "runs" / run_id
    run_root.mkdir(parents=True)
    archived_manifest = output / "source-manifests" / (source["sourceSnapshotHash"] + ".json")
    archived_manifest.parent.mkdir(exist_ok=True)
    if not archived_manifest.exists():
        archived_manifest.write_bytes(Path(source["sourceManifestPath"]).read_bytes())
    source = {**source, "sourceManifestPath": str(archived_manifest)}
    source_root = Path(source["exportRoot"])
    env = build_environment(output)
    sdk = resolve_sdk(source_root, env)
    msbuild_env = sdk_environment(env, sdk)
    cache = output.parent / "cache"
    selected = sorted(set(projects) | {"NexusPipeline.Boundaries"}) if projects is not None else None
    source_records = json.loads(archived_manifest.read_bytes())["files"]
    component = solution_inputs(source_root, profile, env, selected, source_records, sdk=sdk)
    web_receipt = None
    if "NexusPipeline.Host" in component["projectNames"]:
        from web_build import build_web
        web_error = None
        try:
            web_code = build_web(root, output, source=source)
            web_receipt = json.loads((output / "web-receipt.json").read_bytes())
        except (OSError, ValueError) as error:
            web_code, web_error = 1, str(error)
            print(f"ERROR: {web_error}", file=sys.stderr, flush=True)
        if web_code:
            web_statuses = web_receipt["responsibilities"] if web_receipt is not None else {}
            return write_build_result(output, run_root, {
                "status": "FAIL", "reason": "web_build_failed", "error": web_error,
                "profile": profile, "runId": run_id, "source": source, "requestedProjects": projects,
                "componentInputs": component, "web": web_receipt, "exitCode": web_code, "compileExitCode": None,
                "responsibilities": {kind: {"status": web_statuses.get(kind, "NOT_RUN")}
                                     for kind in ("lint", "typecheck", "test", "build")},
            })
        source = web_receipt["source"]
        source_root = Path(source["exportRoot"])
        component["webInputs"] = web_receipt["componentInputs"]["sha256"]
        component["webArtifacts"] = web_receipt["artifacts"]
    solution = source_root / "NexusPipeline.slnx"
    if selected is not None:
        tree = ET.parse(solution)
        for node in list(tree.getroot()):
            if Path(node.attrib["Path"]).stem not in component["projectNames"]:
                tree.getroot().remove(node)
            else:
                node.set("Path", str(source_root / node.attrib["Path"]))
        solution = run_root / "selected.slnx"
        tree.write(solution, encoding="utf-8", xml_declaration=True)
    reusable = compile_receipt(output, component, profile)
    locks = (prepare_locks(source_root, component['projectNames'], run_root, cache / 'nuget', sdk, msbuild_env)
             if reusable is None else json.loads(Path(reusable['path']).read_bytes()).get('restoreLocks'))
    command = [
        sdk['path'], "build", str(solution),
        "--configuration", "Release", "--nologo", "--disable-build-servers", "-m:1",
        f"-p:NexusArtifactsRoot={output / 'build'}",
        f"-p:NexusSdkKey={sdk_key(sdk)}",
        f"-p:NexusPackagesRoot={cache / 'nuget'}",
        f"-p:NexusTestHost={'true' if profile == 'test' else 'false'}",
    ]
    command.extend(lock_arguments(locks))
    if web_receipt is not None:
        command.append(f"-p:NexusWebRoot={web_receipt['output']}")
    started_at = datetime.now(timezone.utc).isoformat()
    started = time.monotonic()
    if reusable is None:
        print(f"build/{profile}: {' '.join(command)}", flush=True)
        with (run_root / "build.log").open("w", encoding="utf-8", newline="\n") as log:
            process = subprocess.Popen(command, cwd=source_root, env=msbuild_env, stdout=subprocess.PIPE,
                                       stderr=subprocess.STDOUT, encoding="utf-8", errors="replace")
            for line in process.stdout:
                print(line, end="", flush=True)
                log.write(line)
            code = process.wait()
    else:
        code = 0
        (run_root / "build.log").write_text("Compilation reused after component and binary verification.\n", encoding="utf-8")
    compile_code = code
    lock_arguments(locks)
    source_check = None
    names = None
    if code == 0:
        try:
            code = check_source(source_root, source_records)
            source_check = {"exitCode": code}
        except (OSError, ValueError) as error:
            code = 1
            source_check = {"exitCode": code, "error": str(error)}
            print(f"ERROR: {error}", file=sys.stderr, flush=True)
    if code == 0:
        names = verify_names(source_root, output, run_root, env, source_records, sdk['path'])
        code = names["exitCode"]
    boundaries = None
    if code == 0:
        boundary_command = [sdk['path'], str(output / "build/bin/NexusPipeline.Boundaries/release/NexusPipeline.Boundaries.dll"),
                            "--source-boundaries", str(source_root)]
        boundary_report = run_root / 'source-boundaries.json'
        with boundary_report.open('w', encoding='utf-8', newline='\n') as stream:
            code = subprocess.run(boundary_command, cwd=source_root, env=env, stdout=stream, stderr=subprocess.STDOUT).returncode
        boundaries = {'command': boundary_command, 'exitCode': code, 'nativeReport': str(boundary_report)}
    api_command = [sdk['path'], str(output / "build/bin/NexusPipeline.Boundaries/release/NexusPipeline.Boundaries.dll"),
                   "--sdk-surface", str(output / "build/bin/NexusPipeline.Extensibility/release/NexusPipeline.Extensibility.dll"),
                   "--verify", str(source_root / "contracts/sdk-public-api.json")]
    api_code = None
    if code == 0 and "NexusPipeline.Extensibility" in component["projectNames"]:
        with (run_root / "sdk-api.log").open("w", encoding="utf-8", newline="\n") as api_log:
            api_code = subprocess.run(api_command, cwd=source_root, env=env, stdout=api_log,
                                      stderr=subprocess.STDOUT).returncode
        code = api_code
    lint_status = "NOT_RUN" if compile_code else "FAIL" if code else "PASS"
    sdk_api = {"command": api_command, "exitCode": api_code, "nativeLog": str(run_root / "sdk-api.log")}
    receipt = {
        "responsibilities": {
            "lint": {"status": lint_status, "sourceCheck": source_check, "sourceNames": names,
                     "sourceBoundaries": boundaries, "sdkApi": sdk_api},
            "typecheck": {"status": "PASS" if compile_code == 0 else "FAIL", "evidence": str(run_root / "build.log")},
            "test": {"status": "NOT_RUN"},
            "build": {"status": "PASS" if compile_code == 0 else "FAIL", "reusedCompilation": reusable},
        },
        "compileExitCode": compile_code,
        "web": web_receipt,
        "sdkApi": sdk_api,
        "sourceNames": names,
        "sourceBoundaries": boundaries,
        "status": "PASS" if code == 0 else "FAIL", "profile": profile,
        "runId": run_id, "nativeLog": str(run_root / "build.log"),
        "scope": "Selected project closure" if selected is not None else "Complete solution; no product acceptance",
        "requestedProjects": projects,
        "componentInputs": component, "reusedCompilation": reusable, "restoreLocks": locks,
        "projectInputs": project_inputs(source_root, profile, env, component["projectNames"],
            component.get("webInputs"), component.get("webArtifacts"), source_records, sdk=sdk),
        "source": source, "command": command if reusable is None else None, "startedAt": started_at,
        "elapsedSeconds": time.monotonic() - started, "exitCode": code,
    }
    if code == 0:
        receipt["binaryManifest"] = save_build(output, run_root)
    return write_build_result(output, run_root, receipt)


def write_build_result(output, run_root, receipt):
    write_json(run_root / "build-receipt.json", receipt)
    write_json(output / "build-receipt.json", receipt)
    return receipt["exitCode"]
