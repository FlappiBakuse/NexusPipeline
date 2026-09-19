"""Host 生产发布边界：固定 tag、production 输出和确定性 ZIP。"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import shutil
import subprocess
import tempfile
import zipfile
from pathlib import Path
from typing import Any, Callable
from xml.etree import ElementTree


REPOSITORY = "FlappiBakuse/NexusPipeline"
VERSION_PATTERN = re.compile(r"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-(beta|rc)\.(0|[1-9]\d*))?$")
PROTECTED_NAMES = {"config", "data", "history", "logs", ".nxp", "plugins"}
TEXT_SUFFIXES = {".css", ".html", ".js", ".json", ".md", ".mjs", ".txt", ".xml", ".yaml", ".yml"}


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


def ensure_production_manifest(root: Path) -> None:
    manifest = root / "app.manifest"
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
    manifest_root: Path | None = None,
) -> dict[str, Any]:
    production_root = production_root.resolve()
    output_dir = output_dir.resolve()
    _require(production_root.is_dir(), f"production 输出目录不存在：{production_root}")
    ensure_production_manifest((manifest_root or production_root.parent).resolve())
    tag = normalized_tag(tag)
    version = tag[1:]
    output_dir.mkdir(parents=True, exist_ok=True)
    zip_path = output_dir / f"NexusPipeline-{version}-win-x64.zip"
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


def build_production(root: Path, output_dir: Path, *, source_sha: str, dotnet: str = "dotnet") -> dict[str, Any]:
    root = root.resolve()
    production_root = output_dir.resolve() / "production"
    if production_root.exists():
        shutil.rmtree(production_root)
    frontend = root / "frontend"
    subprocess.run(["npm", "ci", "--no-audit", "--no-fund", "--prefix", str(frontend)], cwd=root, check=True)
    subprocess.run(["npm", "run", "typecheck", "--prefix", str(frontend)], cwd=root, check=True)
    subprocess.run(["npm", "run", "build", "--prefix", str(frontend)], cwd=root, check=True)
    subprocess.run([dotnet, "publish", str(root / "src" / "NexusPipeline.csproj"), "--configuration", "Release", "--runtime", "win-x64", "--self-contained", "false", "-p:PublishSingleFile=true", "-p:DebugType=none", "-p:DebugSymbols=false", "-p:NexusTestHost=false", "--output", str(production_root)], cwd=root, check=True)
    wwwroot = production_root / "wwwroot"
    if wwwroot.exists():
        shutil.rmtree(wwwroot)
    shutil.copytree(frontend / "dist", wwwroot)
    return archive_production(
        production_root,
        output_dir,
        "v" + project_version(root),
        source_sha=source_sha,
        manifest_root=root,
    )


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="NexusPipeline Host production release boundary")
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--tag", required=True)
    parser.add_argument("--source-sha", required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--build", action="store_true")
    args = parser.parse_args(argv)
    root = args.root.resolve()
    commit = verify_tag(root, args.tag)
    _require(commit == args.source_sha, "tag commit 与 source-sha 不一致")
    result = build_production(root, args.output, source_sha=commit) if args.build else archive_production(root / "release", args.output, args.tag, source_sha=commit)
    print(json.dumps(result, ensure_ascii=False, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
