"""Host 生产发布边界：固定 tag、production 输出和确定性 ZIP。"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import tempfile
import zipfile
from pathlib import Path
from typing import Any, Callable
from xml.etree import ElementTree

try:
    from .pe_manifest import verify_embedded_manifest
except ImportError:
    from pe_manifest import verify_embedded_manifest


REPOSITORY = "FlappiBakuse/NexusPipeline"
VERSION_PATTERN = re.compile(r"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-(beta|rc)\.(0|[1-9]\d*))?$")
PROTECTED_NAMES = {"config", "data", "history", "logs", ".nxp", "plugins"}
TEXT_SUFFIXES = {".css", ".html", ".js", ".json", ".md", ".mjs", ".txt", ".xml", ".yaml", ".yml"}
MAX_PACKAGE_ENTRIES = 8192
MAX_PACKAGE_UNCOMPRESSED_BYTES = 512 * 1024 * 1024
WINDOWS_RESERVED_NAMES = {"CON", "PRN", "AUX", "NUL", *(f"COM{x}" for x in '123456789¹²³'), *(f"LPT{x}" for x in '123456789¹²³')}


class HostReleaseError(ValueError):
    """生产发布输入或资产边界无效。"""


def _require(condition: bool, message: str) -> None:
    if not condition:
        raise HostReleaseError(message)


def parse_version(value: str) -> tuple[int, int, int, str | None, int]:
    text = value[1:] if value.startswith("v") else value
    match = VERSION_PATTERN.fullmatch(text)
    _require(match is not None, f"版本格式无效：{value}")
    stage = match.group(4)
    return int(match.group(1)), int(match.group(2)), int(match.group(3)), stage, int(match.group(5) or 0)


def normalized_tag(value: str) -> str:
    version = parse_version(value)
    text = f"{version[0]}.{version[1]}.{version[2]}"
    if version[3]:
        text += f"-{version[3]}.{version[4]}"
    return "v" + text


def project_version(root: Path, commit: str | None = None) -> str:
    project = root / "src" / "NexusPipeline.csproj"
    if commit is None:
        document = ElementTree.parse(project)
    else:
        raw = subprocess.run(["git", "show", f"{commit}:src/NexusPipeline.csproj"], cwd=root, check=False, capture_output=True, text=True, encoding="utf-8", errors="replace")
        _require(raw.returncode == 0, f"无法读取 tag commit 的 csproj：{commit}")
        document = ElementTree.fromstring(raw.stdout)
    values = [element.text.strip() for element in document.iter() if element.tag.rsplit("}", 1)[-1] == "Version" and element.text and element.text.strip()]
    _require(len(values) == 1, "Host csproj 必须有唯一 Version")
    return values[0]


def git_output(root: Path, *arguments: str) -> str:
    result = subprocess.run(["git", *arguments], cwd=root, check=False, capture_output=True, text=True, encoding="utf-8", errors="replace")
    if result.returncode != 0:
        raise HostReleaseError(f"git {' '.join(arguments)} 失败：{result.stderr.strip()}")
    return result.stdout.strip()


def verify_tag(root: Path, tag: str, *, qualification_verifier: Callable[[str], None] | None = None) -> str:
    tag = normalized_tag(tag)
    commit = git_output(root, "rev-parse", f"refs/tags/{tag}^{{commit}}")
    _require(re.fullmatch(r"[0-9a-f]{40}", commit) is not None, "tag 未解析为 commit")
    main_ref = "refs/remotes/origin/main"
    try:
        git_output(root, "rev-parse", main_ref)
    except HostReleaseError:
        main_ref = "main"
    git_output(root, "merge-base", "--is-ancestor", commit, main_ref)
    declared = project_version(root, commit)
    _require(declared == tag[1:], f"tag 版本与 csproj 不一致：{tag} / {declared}")
    if qualification_verifier is not None:
        qualification_verifier(commit)
    return commit


def ensure_source_manifest(manifest_path: Path) -> None:
    manifest = manifest_path.resolve()
    _require(manifest.is_file() and not manifest.is_symlink(), f"source manifest 不存在：{manifest}")
    document = ElementTree.parse(manifest)
    levels = [element.attrib.get("level") for element in document.iter() if element.tag.rsplit("}", 1)[-1] == "requestedExecutionLevel"]
    _require(levels == ["requireAdministrator"], "production manifest 必须声明 requireAdministrator")


def _safe_archive_name(path: Path, production_root: Path) -> str:
    relative = path.relative_to(production_root).as_posix()
    _require(relative and not relative.startswith("../") and ".." not in relative.split("/"), f"发布路径越界：{relative}")
    _require(relative.split("/", 1)[0] not in PROTECTED_NAMES, f"发布资产包含受保护运行数据：{relative}")
    return relative


def archive_production(
    production_root: Path,
    output_dir: Path,
    tag: str,
    *,
    source_sha: str,
    sdk_sha: str = "",
    build_tool: str = "host_release.py",
    manifest_path: Path | None = None,
) -> dict[str, Any]:
    production_root = production_root.resolve()
    output_dir = output_dir.resolve()
    _require(production_root.is_dir(), f"production 输出目录不存在：{production_root}")
    ensure_source_manifest((manifest_path or production_root.parent / "src" / "app.manifest").resolve())
    tag = normalized_tag(tag)
    version = tag[1:]
    output_dir.mkdir(parents=True, exist_ok=True)
    zip_path = output_dir / f"NexusPipeline-{tag}-win-x64.zip"
    if zip_path.exists():
        zip_path.unlink()
    files = []
    for path in sorted(production_root.rglob("*")):
        if not path.is_file() or path == zip_path:
            continue
        relative = _safe_archive_name(path, production_root)
        files.append((relative, path))
    with zipfile.ZipFile(zip_path, "w", compression=zipfile.ZIP_STORED) as archive:
        for relative, path in files:
            info = zipfile.ZipInfo(relative, date_time=(1980, 1, 1, 0, 0, 0))
            info.create_system = 0
            info.external_attr = 0o644 << 16
            data = path.read_bytes()
            if path.suffix.casefold() in TEXT_SUFFIXES:
                data = data.replace(b"\r\n", b"\n").replace(b"\r", b"\n")
            archive.writestr(info, data)
    digest = hashlib.sha256(zip_path.read_bytes()).hexdigest()
    sha_path = output_dir / f"{zip_path.name}.sha256"
    sha_path.write_text(digest + "\n", encoding="ascii")
    metadata = {
        "schemaVersion": 1,
        "sourceSha": source_sha,
        "tag": tag,
        "version": version,
        "mode": "production",
        "sdkSha": sdk_sha,
        "buildTool": build_tool,
        "sha256": digest,
        "sizeBytes": zip_path.stat().st_size,
    }
    (output_dir / "build-metadata.json").write_text(json.dumps(metadata, ensure_ascii=False, sort_keys=True) + "\n", encoding="utf-8")
    _require(hashlib.sha256(zip_path.read_bytes()).hexdigest() == digest, "生产 ZIP 二次 SHA256 校验失败")
    return {"zip": str(zip_path), "sha": str(sha_path), "metadata": metadata}


def _run_checked(command: list[str], cwd: Path, runner: Callable[[list[str], Path], None] | None) -> None:
    if runner is not None:
        runner(command, cwd)
        return
    subprocess.run(command, cwd=cwd, check=True)


def build_production(
    root: Path,
    output_dir: Path,
    *,
    source_sha: str,
    dotnet: str = "dotnet",
    runner: Callable[[list[str], Path], None] | None = None,
) -> dict[str, Any]:
    root = root.resolve()
    production_root = output_dir.resolve() / "production"
    if production_root.exists():
        shutil.rmtree(production_root)
    frontend = root / "frontend"
    npm = "npm.cmd" if os.name == "nt" else "npm"
    _run_checked([npm, "ci", "--no-audit", "--no-fund"], frontend, runner)
    _run_checked([npm, "run", "typecheck"], frontend, runner)
    _run_checked([npm, "run", "build"], frontend, runner)
    _run_checked([dotnet, "publish", str(root / "src" / "NexusPipeline.csproj"), "--configuration", "Release", "--runtime", "win-x64", "--self-contained", "false", "-p:PublishSingleFile=true", "-p:DebugType=none", "-p:DebugSymbols=false", "-p:NexusTestHost=false", "--output", str(production_root)], root, runner)
    verify_embedded_manifest(production_root / "nexus-pipeline.exe", "requireAdministrator")
    wwwroot = production_root / "wwwroot"
    if wwwroot.exists():
        shutil.rmtree(wwwroot)
    shutil.copytree(frontend / "dist", wwwroot)
    return archive_production(
        production_root,
        output_dir,
        "v" + project_version(root),
        source_sha=source_sha,
        manifest_path=root / "src" / "app.manifest",
    )


def _safe_package_entry(name: str) -> str:
    normalized = name.replace("\\", "/").removesuffix("/")
    _require(
        bool(normalized)
        and not normalized.startswith("/")
        and re.match(r"^[A-Za-z]:", normalized) is None,
        f"生产 ZIP 条目路径非法：{name}",
    )
    parts = normalized.split("/")
    _require(all(part not in {"", ".", ".."} for part in parts), f"生产 ZIP 条目路径非法：{name}")
    _require(all(not any(ord(char) < 32 or char in '<>:"|?*' for char in part) and not part.endswith((" ", ".")) and part.split('.', 1)[0].upper() not in WINDOWS_RESERVED_NAMES for part in parts), f"生产 ZIP 条目路径非法：{name}")
    _require(parts[0].casefold() not in {name.casefold() for name in PROTECTED_NAMES}, f"生产 ZIP 包含受保护运行数据：{name}")
    return normalized


def _validate_package_layout(infos: list[zipfile.ZipInfo]) -> dict[str, zipfile.ZipInfo]:
    _require(len(infos) <= MAX_PACKAGE_ENTRIES, "生产 ZIP 条目超过上限")
    names: dict[str, zipfile.ZipInfo] = {}
    folded_names: set[str] = set()
    files: set[str] = set()
    size = 0
    for info in infos:
        normalized = _safe_package_entry(info.orig_filename)
        folded = normalized.casefold()
        _require(folded not in folded_names, f"生产 ZIP 条目重复或大小写冲突：{info.filename}")
        folded_names.add(folded)
        directory = info.filename.endswith(("/", "\\"))
        mode = (info.external_attr >> 16) & 0o170000
        _require(mode in ({0, 0o040000} if directory else {0, 0o100000}), f"生产 ZIP 禁止特殊类型：{info.filename}")
        size += info.file_size
        _require(0 <= info.file_size <= MAX_PACKAGE_UNCOMPRESSED_BYTES and size <= MAX_PACKAGE_UNCOMPRESSED_BYTES, "生产 ZIP 展开大小超过上限")
        _require(not directory or info.file_size == 0, "生产 ZIP 目录不得携带载荷")
        if not directory:
            files.add(folded)
        names[normalized] = info
    for name in folded_names:
        parts = name.split('/')
        _require(not any('/'.join(parts[:index]) in files for index in range(1, len(parts))), f"生产 ZIP 文件/目录冲突：{name}")
    _require({name for name in files if name.endswith('.exe')} == {'nexus-pipeline.exe'}, "生产 ZIP 必须仅包含唯一宿主 EXE")
    return names


def verify_received_package(
    package_path: Path,
    metadata_path: Path,
    *,
    expected_source_sha: str,
    expected_tag: str,
    manifest_verifier: Callable[[Path, str], Any] | None = None,
) -> dict[str, Any]:
    """Validate a local or freshly downloaded production asset before publication."""

    package_path = package_path.resolve()
    metadata_path = metadata_path.resolve()
    _require(package_path.is_file() and not package_path.is_symlink(), f"生产 ZIP 不存在或不是普通文件：{package_path}")
    try:
        metadata = json.loads(metadata_path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise HostReleaseError(f"生产 metadata 无效：{metadata_path}") from exc
    _require(isinstance(metadata, dict) and metadata.get("schemaVersion") == 1, "生产 metadata schemaVersion 无效")
    expected_tag = normalized_tag(expected_tag)
    _require(re.fullmatch(r"[0-9a-f]{40}", expected_source_sha) is not None, "expected source SHA 无效")
    _require(metadata.get("sourceSha") == expected_source_sha, "生产 ZIP sourceSha 不匹配")
    _require(metadata.get("tag") == expected_tag, "生产 ZIP tag 不匹配")
    _require(metadata.get("version") == expected_tag[1:], "生产 ZIP version 不匹配")
    _require(metadata.get("mode") == "production", "生产 ZIP mode 无效")
    digest = hashlib.sha256(package_path.read_bytes()).hexdigest()
    _require(metadata.get("sha256") == digest, "生产 ZIP SHA256 不匹配")
    _require(metadata.get("sizeBytes") == package_path.stat().st_size, "生产 ZIP sizeBytes 不匹配")

    try:
        with zipfile.ZipFile(package_path) as archive:
            names = _validate_package_layout(archive.infolist())
            exe = names.get("nexus-pipeline.exe")
            _require(exe is not None and not exe.filename.endswith(("/", "\\")), "生产 ZIP 必须包含根目录 nexus-pipeline.exe")
            verifier = manifest_verifier or verify_embedded_manifest
            with tempfile.TemporaryDirectory(prefix="nxp-host-package-verify-") as temporary:
                executable = Path(temporary) / "nexus-pipeline.exe"
                executable.write_bytes(archive.read(exe))
                try:
                    verifier(executable, "requireAdministrator")
                except Exception as exc:
                    raise HostReleaseError("生产 ZIP 可执行文件 manifest 未通过 requireAdministrator 校验") from exc
    except zipfile.BadZipFile as exc:
        raise HostReleaseError(f"生产 ZIP 无效：{package_path}") from exc
    return metadata


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="NexusPipeline Host production release boundary")
    parser.add_argument("command", nargs="?", choices=("release", "verify-package"), default="release")
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--tag")
    parser.add_argument("--source-sha")
    parser.add_argument("--output", type=Path)
    parser.add_argument("--build", action="store_true")
    parser.add_argument("--package", type=Path)
    parser.add_argument("--metadata", type=Path)
    parser.add_argument("--expected-source-sha")
    parser.add_argument("--expected-tag")
    args = parser.parse_args(argv)
    root = args.root.resolve()
    if args.command == "verify-package":
        for value, label in ((args.package, "--package"), (args.metadata, "--metadata"), (args.expected_source_sha, "--expected-source-sha"), (args.expected_tag, "--expected-tag")):
            _require(value is not None, f"{label} is required")
        result = verify_received_package(args.package, args.metadata, expected_source_sha=args.expected_source_sha, expected_tag=args.expected_tag)
        print(json.dumps(result, ensure_ascii=False, sort_keys=True))
        return 0
    for value, label in ((args.tag, "--tag"), (args.source_sha, "--source-sha"), (args.output, "--output")):
        _require(value is not None, f"{label} is required")
    commit = verify_tag(root, args.tag)
    _require(commit == args.source_sha, "tag commit 与 source-sha 不一致")
    result = build_production(root, args.output, source_sha=commit) if args.build else archive_production(root / "release", args.output, args.tag, source_sha=commit, manifest_path=root / "src" / "app.manifest")
    print(json.dumps(result, ensure_ascii=False, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
