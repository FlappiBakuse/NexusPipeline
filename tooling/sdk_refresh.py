"""Regenerate SDK API descriptors from compiled public assemblies and TypeScript declarations."""
import argparse
from datetime import datetime, timezone
import json
from pathlib import Path
import shutil
import subprocess
import sys
import time

from build_product import build_environment, update_export
from sdk_workspace import synchronize
from source_snapshot import digest, source_files, write_json, external_directory


def refresh(root, output):
    output = external_directory(root, output)
    root = root.resolve()
    if root == output or root in output.parents or output in root.parents:
        raise ValueError("SDK generation output must be outside source")
    if any(item.is_symlink() or item.is_junction() for item in (output, *output.parents)):
        raise ValueError("SDK generation output crosses a link")
    marker = output / "sdk-refresh-owner.json"
    expected = (json.dumps({"format": "nexus.sdk.refresh", "version": 1, "repository": str(root)}, sort_keys=True) + "\n").encode()
    if output.exists() and any(output.iterdir()) and (not marker.is_file() or marker.read_bytes() != expected):
        raise ValueError("SDK generation workspace is not owned by this repository")
    output.mkdir(parents=True, exist_ok=True)
    if not marker.exists():
        with marker.open("xb") as stream:
            stream.write(expected)
    run = output / "runs" / datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
    run.mkdir(parents=True)
    env = build_environment(output)
    env.update(PYTHONDONTWRITEBYTECODE="1", npm_config_cache=str(output.parent / "cache/npm"))
    commands = []

    def execute(command, cwd, label):
        started = time.monotonic()
        result = subprocess.run(command, cwd=cwd, env=env, capture_output=True, encoding="utf-8", errors="replace")
        log = run / (label + ".log")
        log.write_text(result.stdout + result.stderr, encoding="utf-8", newline="\n")
        commands.append({"command": command, "cwd": str(cwd), "exitCode": result.returncode,
                         "elapsedSeconds": time.monotonic() - started, "log": str(log), "sha256": digest(log.read_bytes())})
        write_json(run / "commands.json", commands)
        if result.returncode:
            raise ValueError("SDK descriptor generation failed: " + str(log))

    execute([sys.executable, "-B", str(root / "tooling/contract_codegen.py"), "--shapes-only", "--prune-owned"], root, "shapes")
    source = update_export(root, output / "snapshot")
    frozen = Path(source["exportRoot"])
    for name, relative in [("sdk", "sdk/dotnet/NexusPipeline.Extensibility.csproj"),
                           ("boundaries", "tooling/boundaries/NexusPipeline.Boundaries.csproj")]:
        execute(["dotnet", "build", str(frozen / relative), "--configuration", "Release", "--nologo",
                 "--disable-build-servers", "-m:1", f"-p:NexusArtifactsRoot={output / 'build'}",
                 f"-p:NexusPackagesRoot={output.parent / 'cache/nuget'}"], frozen, name)
    assembly = output / "build/bin/NexusPipeline.Extensibility/release/NexusPipeline.Extensibility.dll"
    reader = output / "build/bin/NexusPipeline.Boundaries/release/NexusPipeline.Boundaries.dll"
    execute(["dotnet", str(reader), "--sdk-surface", str(assembly), "--write",
             str(root / "contracts/sdk-public-api.json")], frozen, "dotnet-api")
    sdk = output / "frontend-sdk"
    synchronize(frozen / "sdk/frontend", sdk, output / "frontend-owner.json")
    node, npm = shutil.which("node"), shutil.which("npm")
    if not node or not npm:
        raise ValueError("SDK generation requires Node and npm")
    npm_cli = str(Path(npm).parent / "node_modules/npm/bin/npm-cli.js")
    installed = sdk / "node_modules/typescript/package.json"
    if not installed.exists():
        execute([node, npm_cli, "ci", "--ignore-scripts", "--no-audit", "--no-fund"], sdk, "frontend-dependencies")
    if json.loads(installed.read_bytes())["version"] != "5.9.3":
        raise ValueError("SDK TypeScript compiler differs from the lock")
    execute([node, str(sdk / "node_modules/typescript/bin/tsc"), "-p", str(sdk / "tsconfig.json")], sdk, "frontend-declarations")
    execute([node, str(frozen / "tooling/sdk_frontend_surface.mjs"),
             str(sdk / "node_modules/typescript/lib/typescript.js"), str(sdk / "dist"), "--write",
             str(root / "contracts/sdk-frontend-api.json")], frozen, "frontend-api")
    expected = json.loads(Path(source["sourceManifestPath"]).read_bytes())["files"]
    changed = {"contracts/sdk-public-api.json", "contracts/sdk-frontend-api.json"}
    if [item for item in source_files(root) if item["path"] not in changed] != [item for item in expected if item["path"] not in changed]:
        raise ValueError("Source changed during SDK descriptor generation")
    execute([sys.executable, "-B", str(root / "tooling/contract_codegen.py"), "--prune-owned"], root, "identity")
    receipt = {"status": "GENERATED", "scope": "Compiled SDK descriptors; package consumers and product validation not run",
               "source": source, "commands": commands, "sdkVersion": "0.1.0",
               "contractFingerprint": json.loads((root / "contracts/fingerprint.json").read_bytes())["contractFingerprint"]}
    write_json(run / "generation-receipt.json", receipt)
    print(json.dumps({"status": "GENERATED", "receipt": str(run / "generation-receipt.json")}))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    refresh(Path(__file__).resolve().parent.parent, args.output)


if __name__ == "__main__":
    main()
