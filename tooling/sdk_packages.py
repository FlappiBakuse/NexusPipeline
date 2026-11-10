"""Build source-bound SDK packages, compile consumers and optionally test lifecycle behavior."""
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time
import zipfile

from build_product import build_environment
from sdk_workspace import records, synchronize
from source_snapshot import digest, source_files, write_json, ordinary_path
from dotnet_sdk import resolve_sdk, sdk_environment, sdk_key


def require_build(root, build):
    receipt_path = build / "build-receipt.json"
    receipt = json.loads(receipt_path.read_bytes())
    if receipt["profile"] not in {"production", "test"} or receipt["exitCode"] != 0:
        raise ValueError("SDK packages require a successful current Host build")
    if not {"NexusPipeline.Extensibility", "NexusPipeline.Boundaries"}.issubset(receipt.get("componentInputs", {}).get("projectNames", [])) or receipt.get("sdkApi", {}).get("exitCode") != 0:
        raise ValueError("SDK packages require the current SDK and its successful compiled API verification")
    source = receipt["source"]
    manifest = Path(source["sourceManifestPath"])
    if digest(manifest.read_bytes()) != source["sourceSnapshotHash"]:
        raise ValueError("SDK producer source manifest changed")
    expected = json.loads(manifest.read_bytes())["files"]
    if source_files(root) != expected:
        raise ValueError("Current Host sources differ from the SDK producer build")
    frozen = Path(source["exportRoot"])
    for item in expected:
        data = (frozen / item["path"]).read_bytes()
        if len(data) != item["sizeBytes"] or digest(data) != item["sha256"]:
            raise ValueError("SDK producer export changed")
    return receipt, frozen, expected


def build_sdk(root, build, consumers, output, cache, compile_only=False):
    root, build, consumers, output, cache = [ordinary_path(path).resolve() for path in (root, build, consumers, output, cache)]
    for target in (build, output, cache):
        if any(source == target or source in target.parents or target in source.parents for source in (root, consumers)):
            raise ValueError("SDK build, package and cache roots must be outside source inputs")
        if any(path.is_symlink() or path.is_junction() for path in (target, *target.parents)):
            raise ValueError("SDK output root crosses a link")
    receipt, frozen, host_records = require_build(root, build)
    fingerprint = json.loads((frozen / "contracts/fingerprint.json").read_bytes())["contractFingerprint"]
    run_root = output / "runs" / datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
    packages = run_root / "packages"
    packages.mkdir(parents=True)
    temporary = cache / "sdk-tmp"
    temporary.mkdir(parents=True, exist_ok=True)
    environment = build_environment(build)
    sdk = resolve_sdk(frozen, environment)
    if sdk != receipt['componentInputs']['context']['dotnet']:
        raise ValueError('SDK packaging requires the same resolved SDK as the compiled inputs')
    env = {key: value for key, value in sdk_environment(environment, sdk).items() if key.upper() != "PATH"}
    tools = [shutil.which('node'), sdk['path'], shutil.which('git')]
    if any(path is None for path in tools):
        raise ValueError("SDK packaging requires node, dotnet and git")
    env["PATH"] = os.pathsep.join(dict.fromkeys([str(Path(path).parent) for path in tools] +
        [str(Path(sys.executable).parent), str(Path(os.environ["SystemRoot"]) / "System32"), os.environ["SystemRoot"]]))
    env.update(TEMP=str(temporary), TMP=str(temporary), npm_config_cache=str(cache / "npm"))
    commands = []
    responsibilities = dict(lint="NOT_RUN", typecheck="NOT_RUN", test="NOT_RUN", build="NOT_RUN")
    validation_mode = "compile-only" if compile_only else "lifecycle"
    stage = None

    def run(command, cwd, label, additions=None):
        if command[0] == "npm":
            npm = shutil.which("npm")
            if not npm:
                raise ValueError("npm is unavailable")
            command = [shutil.which("node"), str(Path(npm).parent / "node_modules/npm/bin/npm-cli.js"), *command[1:]]
        started = time.monotonic()
        result = subprocess.run(command, cwd=cwd, env={**env, **(additions or {})}, capture_output=True,
                                encoding="utf-8", errors="replace", timeout=180)
        log = run_root / (label + ".log")
        log.write_bytes((result.stdout + result.stderr).encode())
        commands.append({"command": command, "cwd": str(cwd), "exitCode": result.returncode,
                         "elapsedSeconds": time.monotonic() - started, "nativeLog": str(log), "nativeLogSha256": digest(log.read_bytes())})
        write_json(run_root / "commands.json", commands)
        if result.returncode:
            raise ValueError("SDK step failed: " + label + "; see " + str(log))
        return result.stdout

    try:
        consumer_records = {kind: records(consumers / kind) for kind in ("dotnet", "frontend")}
        stage = "build"
        run([sdk['path'], "pack", str(frozen / "sdk/dotnet/NexusPipeline.Extensibility.csproj"), "--no-build", "--no-restore",
             "--configuration", "Release", "--output", str(packages), f"-p:NexusArtifactsRoot={build / 'build'}",
             f'-p:NexusSdkKey={sdk_key(sdk)}',
             f"-p:NexusPackagesRoot={build.parent / 'cache/nuget'}"], frozen, "dotnet-pack")
        nupkg = packages / "NexusPipeline.Extensibility.0.1.0.nupkg"
        with zipfile.ZipFile(nupkg) as archive:
            assembly = archive.read("lib/net10.0/NexusPipeline.Extensibility.dll")
            if assembly != (build / "build/bin/NexusPipeline.Extensibility/release/NexusPipeline.Extensibility.dll").read_bytes():
                raise ValueError("Packed SDK differs from the built assembly")
            public = {name: archive.read(name) for name in archive.namelist() if name.startswith("contracts/") and not name.endswith("/")}
            expected = {"contracts/" + path.relative_to(frozen / "contracts/sdk-public").as_posix(): path.read_bytes()
                        for path in (frozen / "contracts/sdk-public").rglob("*") if path.is_file()}
            if public != expected:
                raise ValueError("Packed public SDK contracts differ from the exact producer closure")
        dotnet = output / "dotnet/source"
        stage = "typecheck"
        synchronize(consumers / "dotnet", dotnet, output / "workspace-locks/dotnet.json")
        run([sdk['path'], "build", str(dotnet / "ProtocolConsumer.csproj"), "-m:1", "--disable-build-servers",
             "-p:UseArtifactsOutput=true", f"-p:ArtifactsPath={output / 'dotnet/build'}",
             f"-p:BaseIntermediateOutputPath={output / 'dotnet/build/obj' / sdk_key(sdk)}/",
             f"-p:RestorePackagesPath={output / 'dotnet/packages' / digest(nupkg.read_bytes())}", f"-p:RestoreSources={packages}"], frozen, "dotnet-consumer-build")
        binary = output / "dotnet/build/bin/ProtocolConsumer/debug"
        if (binary / "NexusPipeline.Extensibility.dll").read_bytes() != assembly:
            raise ValueError("Independent consumer loaded a stale same-version SDK")
        frontend = output / "frontend-sdk"
        stage = "build"
        synchronize(frozen / "sdk/frontend", frontend, output / "workspace-locks/sdk-frontend.json")
        if not (frontend / "node_modules/typescript/lib/typescript.js").is_file():
            run(["npm", "ci", "--ignore-scripts", "--no-audit", "--no-fund"], frontend, "frontend-dependencies")
        compiler = frontend / "node_modules/typescript/lib/typescript.js"
        if json.loads((frontend / "node_modules/typescript/package.json").read_bytes())["version"] != "5.9.3":
            raise ValueError("SDK TypeScript compiler differs from its explicit pin")
        run(["npm", "run", "build"], frontend, "frontend-build", {"NEXUS_SDK_SOURCE_ROOT": str(frozen)})
        run(["npm", "pack", "--ignore-scripts", "--pack-destination", str(packages)], frontend, "frontend-pack")
        tar = packages / "nexuspipeline-extension-sdk-0.1.0.tgz"
        immutable = tar.with_name("nexuspipeline-extension-sdk-0.1.0-" + digest(tar.read_bytes()) + ".tgz")
        tar.rename(immutable)
        responsibilities["build"] = "PASS"
        web = output / "frontend-consumer"
        stage = "typecheck"
        synchronize(consumers / "frontend", web, output / "workspace-locks/frontend-consumer.json")
        run(["npm", "install", "--no-save", "--package-lock=false", "--ignore-scripts", "--no-audit", "--no-fund", str(immutable)], web, "frontend-consumer-package")
        run(["node", str(frontend / "node_modules/typescript/bin/tsc"), "--strict", "--module", "NodeNext", "--moduleResolution", "NodeNext",
             "--target", "ES2023", "--outDir", "dist", "consumer.ts"], web, "frontend-consumer-build")
        emitted = {path.relative_to(frontend / "dist").as_posix(): digest(path.read_bytes()) for path in (frontend / "dist").rglob("*") if path.is_file()}
        installed = web / "node_modules/@nexuspipeline/extension-sdk/dist"
        if emitted != {path.relative_to(installed).as_posix(): digest(path.read_bytes()) for path in installed.rglob("*") if path.is_file()}:
            raise ValueError("Installed frontend SDK differs from the built artifact")
        responsibilities["typecheck"] = "PASS"
        if not compile_only:
            stage = "test"
            lifecycle = run(["node", "--test", "--test-reporter=tap", str(frozen / "tests/contracts/frontend-scope.test.mjs")], frozen,
                            "frontend-lifecycle", {"NEXUS_SDK_ENTRY": str(frontend / "dist/index.js")})
            totals = dict(re.findall(r"^# (tests|pass|fail) (\d+)$", lifecycle, re.MULTILINE))
            if totals.get("tests", "0") == "0" or totals.get("pass") != totals["tests"] or totals.get("fail") != "0":
                raise ValueError("SDK lifecycle tests are missing, incomplete or failed")
            responsibilities["test"] = "PASS"
        stage = None
        if source_files(root) != host_records or any(records(consumers / kind) != consumer_records[kind] for kind in consumer_records):
            raise ValueError("SDK producer or consumer source inputs changed during packaging")
        result = {"status": "PASS", "scope": "Current packaged SDK and independent compiled consumers; no plugin business qualification",
                  "validationMode": validation_mode, "runtimeConsumers": "NOT_RUN",
                  "lifecycle": responsibilities["test"], "responsibilities": responsibilities,
                  "contractFingerprint": fingerprint, "hostSource": receipt["source"], "dotnet": sdk,
                  "hostBuildReceiptSha256": digest((build / "build-receipt.json").read_bytes()),
                  "commands": commands, "consumerInputs": consumer_records, "consumerRoot": str(consumers), "frontendSourceRoot": str(frozen),
                  "frontendApiDescriptorSha256": digest((frozen / "contracts/sdk-frontend-api.json").read_bytes()), "frontendCompilerSha256": digest(compiler.read_bytes()),
                  "packages": [{"path": str(path), "sha256": digest(path.read_bytes()), "sizeBytes": path.stat().st_size} for path in (nupkg, immutable)]}
        write_json(run_root / "sdk-consumer-receipt.json", result)
        write_json(output / "sdk-consumers-current.json", {"receipt": str(run_root / "sdk-consumer-receipt.json"), **result})
        return {"status": "PASS", "contractFingerprint": fingerprint, "receipt": str(run_root / "sdk-consumer-receipt.json")}
    except Exception as failure:
        if stage is not None:
            responsibilities[stage] = "FAIL"
        write_json(run_root / "sdk-consumer-receipt.json", {"status": "FAIL", "error": str(failure), "hostSource": receipt["source"],
            "commands": commands, "validationMode": validation_mode, "runtimeConsumers": "NOT_RUN",
            "lifecycle": responsibilities["test"], "responsibilities": responsibilities})
        raise
