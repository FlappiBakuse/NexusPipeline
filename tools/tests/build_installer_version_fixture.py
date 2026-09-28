"""Build two isolated native version fixtures; never emit a release candidate."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tarfile

sys.path.insert(0, str(Path(__file__).parents[2]))
from tools.host_installer import INNO_COMPILER_SHA256, build_installer
from tools.host_release import build_production

A_VERSION = "0.16.9001"
B_VERSION = "0.16.9002"


def digest(path: Path) -> str:
    with path.open("rb") as stream:
        result = hashlib.sha256()
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            result.update(block)
        return result.hexdigest()


def git(root: Path, *arguments: str) -> str:
    result = subprocess.run(["git", "-C", str(root), *arguments], capture_output=True,
                            text=True, encoding="utf-8", errors="replace", check=False)
    if result.returncode != 0:
        raise RuntimeError(f"git {arguments}: {result.stderr}")
    return result.stdout.strip()


def write_log_run(command: list[str], cwd: Path, destination: Path) -> None:
    print("[fixture] " + " ".join(command), flush=True)
    with destination.open("w", encoding="utf-8") as log:
        process = subprocess.Popen(command, cwd=cwd, stdout=subprocess.PIPE,
                                   stderr=subprocess.STDOUT, text=True,
                                   encoding="utf-8", errors="replace")
        assert process.stdout is not None
        for line in process.stdout:
            print(line, end="", flush=True)
            log.write(line)
        code = process.wait()
        log.write(f"\nEXIT_CODE={code}\n")
    if code != 0:
        raise RuntimeError(f"fixture command failed {code}: {destination}")


def archive_source(host: Path, commit: str, destination: Path) -> None:
    archive = destination.parent / "a-source.tar"
    command = ["git", "-C", str(host), "archive", "--format=tar", f"--output={archive}", commit]
    subprocess.run(command, check=True)
    destination.mkdir()
    with tarfile.open(archive) as contents:
        for member in contents.getmembers():
            path = Path(member.name)
            if path.is_absolute() or ".." in path.parts or not (member.isfile() or member.isdir()):
                raise RuntimeError("unsafe Git archive path")
        contents.extractall(destination, filter="data")


def copy_worktree_source(host: Path, destination: Path) -> list[str]:
    result = subprocess.run(["git", "-C", str(host), "ls-files", "--cached", "--others",
                             "--exclude-standard", "-z"], capture_output=True, check=True)
    names = sorted({os.fsdecode(item) for item in result.stdout.split(b"\0") if item})
    destination.mkdir()
    for name in names:
        relative = Path(name)
        if relative.is_absolute() or ".." in relative.parts:
            raise RuntimeError("unsafe source path")
        source = host / relative
        if not source.is_file() or source.is_symlink():
            raise RuntimeError(f"source file missing or linked: {name}")
        target = destination / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, target)
    return names


def source_fingerprint(root: Path, names: list[str] | None = None) -> str:
    selected = names or [str(path.relative_to(root)) for path in root.rglob("*") if path.is_file()]
    hash_state = hashlib.sha256()
    for name in sorted(selected):
        path = root / name
        hash_state.update(name.replace("\\", "/").encode("utf-8") + b"\0")
        hash_state.update(bytes.fromhex(digest(path)))
    return hash_state.hexdigest()


def apply_version(root: Path, version: str) -> None:
    project = root / "src" / "NexusPipeline.csproj"
    text = project.read_text(encoding="utf-8")
    old = "<Version>0.16.9</Version>"
    if text.count(old) != 1:
        raise RuntimeError("fixture Host version source changed")
    replacement = (f"<Version>{version}</Version>\n"
                   f"    <AssemblyVersion>{version}.0</AssemblyVersion>\n"
                   f"    <FileVersion>{version}.0</FileVersion>\n"
                   f"    <InformationalVersion>{version}</InformationalVersion>")
    project.write_text(text.replace(old, replacement), encoding="utf-8")


def pe_version(image: Path) -> dict:
    environment = os.environ.copy()
    environment["NXP_FIXTURE_PE_PATH"] = str(image)
    command = [
        "pwsh", "-NoProfile", "-NonInteractive", "-Command",
        "$v=[Diagnostics.FileVersionInfo]::GetVersionInfo($env:NXP_FIXTURE_PE_PATH); "
        "@{FileVersion=$v.FileVersion;ProductVersion=$v.ProductVersion} | ConvertTo-Json -Compress",
    ]
    result = subprocess.run(command, env=environment, capture_output=True, text=True,
                            encoding="utf-8", errors="replace", check=True)
    return json.loads(result.stdout)


def build_one(label: str, root: Path, output: Path, version: str, source_sha: str,
              source_tree: str, plugins: Path, compiler: Path, source_hash: str) -> dict:
    if root.joinpath("tools", "installer.iss.in").is_file() is False:
        raise RuntimeError("fixture installer source missing")
    sdk_project = root / "src" / "NexusPipeline.Plugin.Abstractions" / "NexusPipeline.Plugin.Abstractions.csproj"
    sdk_project_sha = digest(sdk_project)
    marker = root / "frontend" / "public" / f"nxp-version-fixture-{label.lower()}.txt"
    if marker.exists():
        raise RuntimeError("fixture marker collides with source")
    marker.write_text(f"NexusPipeline isolated version fixture {label} {version}\n", encoding="utf-8")
    apply_version(root, version)
    if digest(sdk_project) != sdk_project_sha:
        raise RuntimeError("fixture version overlay changed Plugin Abstractions")
    build_dir = output / "build"
    counter = iter(range(1, 100))

    def runner(command: list[str], cwd: Path) -> None:
        write_log_run(command, cwd, output / f"build-{next(counter):02d}.log")

    result = build_production(root, build_dir, source_sha=source_sha,
                              plugins_root=plugins, runner=runner)
    production = build_dir / "production"
    setup_dir = output / "setup"
    setup_result = build_installer(production, build_dir / "build-metadata.json",
                                   root / "tools" / "runtime-dependencies.json",
                                   compiler, setup_dir, root / "tools" / "installer.iss.in")
    image = production / "nexus-pipeline.exe"
    actual = pe_version(image)
    if not str(actual["FileVersion"]).startswith(version) or not str(actual["ProductVersion"]).startswith(version):
        raise RuntimeError(f"{label} PE version mismatch: {actual}")
    zip_path = Path(result["zip"])
    setup_path = setup_dir / f"NexusPipeline-v{version}-win-x64-setup.exe"
    receipt = {
        "evidenceKind": "TEST_FIXTURE_NOT_FOR_DISTRIBUTION",
        "label": label, "sourceSha": source_sha, "sourceTreeSha": source_tree,
        "sourceFingerprintBeforeVersionOverlay": source_hash,
        "versionOverlayProjectSha256": digest(root / "src" / "NexusPipeline.csproj"),
        "fixtureAsset": {"path": "wwwroot/" + marker.name, "sha256": digest(marker)},
        "sdkProjectSha256": sdk_project_sha,
        "version": version, "peVersion": actual,
        "properties": {"Version": version, "AssemblyVersion": version + ".0",
                       "FileVersion": version + ".0", "InformationalVersion": version},
        "zipSha256": digest(zip_path), "setupSha256": digest(setup_path),
        "imageSha256": digest(image), "compilerSha256": digest(compiler),
        "applicationPayload": result["metadata"]["payloadFiles"],
        "installerMetadata": setup_result,
    }
    (output / "fixture-receipt.json").write_text(
        json.dumps(receipt, ensure_ascii=False, sort_keys=True, indent=2), encoding="utf-8")
    return receipt


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    parser = argparse.ArgumentParser()
    parser.add_argument("--host-root", type=Path, required=True)
    parser.add_argument("--plugins-root", type=Path, required=True)
    parser.add_argument("--compiler", type=Path, required=True)
    parser.add_argument("--npm-cache", type=Path)
    parser.add_argument("--a-ref", default="0a467a74ad37e7881ee1d2e7c6e69b20b0e928dc")
    parser.add_argument("--work", type=Path, required=True)
    args = parser.parse_args()
    host = args.host_root.resolve()
    plugins = args.plugins_root.resolve()
    compiler = args.compiler.resolve()
    work = args.work.resolve()
    if not (host / ".git").exists() or not (plugins / ".git").exists():
        raise RuntimeError("real Host and Plugins Git repositories required")
    if digest(compiler) != INNO_COMPILER_SHA256:
        raise RuntimeError("locked ISCC compiler mismatch")
    if work.exists() or work == host or host in work.parents:
        raise RuntimeError("work must be a new external directory")
    a_sha = git(host, "rev-parse", args.a_ref)
    a_tree = git(host, "rev-parse", a_sha + "^{tree}")
    b_sha = git(host, "rev-parse", "HEAD")
    b_tree = git(host, "rev-parse", "HEAD^{tree}")
    plugins_sha = git(plugins, "rev-parse", "HEAD")
    work.mkdir(parents=True)
    a_root = work / "A" / "source"
    b_root = work / "B" / "source"
    a_root.parent.mkdir()
    b_root.parent.mkdir()
    archive_source(host, a_sha, a_root)
    b_names = copy_worktree_source(host, b_root)
    a_hash = source_fingerprint(a_root)
    b_hash = source_fingerprint(b_root, b_names)
    if digest(b_root / "tools" / "installer-launch.iss") != digest(
        host / "tools" / "installer-launch.iss"):
        raise RuntimeError("B launch source changed while copying")
    environment = os.environ
    environment["TEMP"] = environment["TMP"] = str(work / "runtime")
    Path(environment["TEMP"]).mkdir()
    npm_cache = work / "npm-cache"
    if args.npm_cache:
        source_cache = args.npm_cache.resolve()
        if not source_cache.is_dir() or source_cache.is_symlink():
            raise RuntimeError("npm cache source unavailable")
        shutil.copytree(source_cache, npm_cache)
    else:
        npm_cache.mkdir()
    environment["NPM_CONFIG_CACHE"] = str(npm_cache)
    environment["PYTHONPYCACHEPREFIX"] = str(work / "python-cache")
    environment["NuGetAudit"] = "false"
    environment["NUGET_PACKAGES"] = environment.get("NUGET_PACKAGES", str(work / "nuget"))
    a = build_one("A", a_root, work / "A", A_VERSION, a_sha, a_tree,
                  plugins, compiler, a_hash)
    b = build_one("B", b_root, work / "B", B_VERSION, b_sha, b_tree,
                  plugins, compiler, b_hash)
    if a["version"] == b["version"] or a["imageSha256"] == b["imageSha256"]:
        raise RuntimeError("A/B are not different native versions and bytes")
    for own, other in ((a, b), (b, a)):
        paths = {item["path"] for item in own["applicationPayload"]}
        if own["fixtureAsset"]["path"] not in paths or other["fixtureAsset"]["path"] in paths:
            raise RuntimeError("fixture-specific application file inventory invalid")
    (work / "pair.json").write_text(json.dumps({
        "evidenceKind": "REAL_NATIVE_VERSION_FIXTURE_BUILD",
        "qualification": "BUILD_ONLY_NOT_NATIVE_INSTALL",
        "pluginsSha": plugins_sha, "A": a, "B": b,
    }, ensure_ascii=False, indent=2), encoding="utf-8")
    print("PASS two distinct native version fixtures built; installation experiment remains separate")
    return 0


if __name__ == "__main__":
    sys.exit(main())
