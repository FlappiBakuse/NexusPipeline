"""Compile the per-user installer from an already frozen Host production payload.

This tool only builds the setup EXE. It never runs Setup or installs a runtime.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import subprocess
from pathlib import Path

try:
    from .host_release import HostReleaseError, PAYLOAD_ROOTS, _safe_archive_name, _safe_package_entry, _require, verify_received_package
except ImportError:
    from host_release import HostReleaseError, PAYLOAD_ROOTS, _safe_archive_name, _safe_package_entry, _require, verify_received_package


INNO_COMPILER_SHA256 = "0a8757031b33777e4c9cbffee40f11a5062b36d25cbe144c1db73b6102b80ad7"
INNO_DISTRIBUTION_SHA256 = "9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732"


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def verified_payload(root: Path, metadata: dict) -> list[dict]:
    root = root.resolve()
    _require(root.is_dir() and not root.is_symlink(), "production staging 无效")
    listed = metadata.get("payloadFiles")
    _require(isinstance(listed, list) and listed, "缺少冻结的 production 载荷清单")
    by_name: dict[str, dict] = {}
    for item in listed:
        _require(isinstance(item, dict) and set(item) == {"path", "sizeBytes", "sha256"}, "载荷清单字段无效")
        name = item["path"]
        _require(isinstance(name, str) and _safe_package_entry(name) == name, "载荷路径无效")
        _require(name not in by_name, "载荷清单重复路径")
        by_name[name] = item
    actual: dict[str, Path] = {}
    for path in root.rglob("*"):
        if path.is_file():
            name = _safe_archive_name(path, root)
            _require(name.casefold() not in {entry.casefold() for entry in actual}, "载荷存在大小写冲突")
            actual[name] = path
    _require(set(actual) == set(by_name), "安装器与 ZIP 的 staging 文件集合不同")
    _require({name.split("/", 1)[0] for name in actual} == PAYLOAD_ROOTS, "应用载荷根项不完整")
    for name, path in actual.items():
        item = by_name[name]
        _require(path.stat().st_size == item["sizeBytes"] and sha256(path) == item["sha256"],
                 f"安装器与 ZIP 的 staging 字节不同：{name}")
    return [by_name[name] for name in sorted(by_name)]


def dependency_pair(path: Path) -> dict[str, dict]:
    document = json.loads(path.read_text(encoding="utf-8"))
    items = document.get("dependencies")
    _require(document.get("schemaVersion") == 1 and isinstance(items, list) and len(items) == 2,
             "依赖锁定清单无效")
    by_framework = {item.get("framework"): item for item in items}
    _require(set(by_framework) == {"Microsoft.WindowsDesktop.App", "Microsoft.AspNetCore.App"}, "依赖框架不完整")
    for item in items:
        _require(item.get("version") == "8.0.31" and item.get("rid") == "win-x64", "依赖版本或架构不符")
        _require(isinstance(item.get("url"), str) and item["url"].startswith("https://builds.dotnet.microsoft.com/dotnet/"),
                 "依赖下载 URL 非固定微软官方包")
        _require(re.fullmatch(r"[0-9a-f]{64}", item.get("sha256", "")) is not None, "依赖 SHA256 无效")
    return by_framework


def render_script(template: str, *, production_root: Path, output_dir: Path,
                  metadata: dict, files: list[dict], dependencies: dict[str, dict]) -> str:
    version = metadata.get("version")
    _require(isinstance(version, str) and re.fullmatch(r"\d+\.\d+\.\d+", version) is not None,
             "安装器版本无效")
    for path in (production_root, output_dir):
        _require('"' not in str(path) and ';' not in str(path) and "\n" not in str(path), "安装器路径无法安全写入脚本")
    file_lines: list[str] = []
    delete_lines: list[str] = []
    stage_checks: list[str] = []
    for item in files:
        relative = item["path"]
        source = production_root.joinpath(*relative.split("/"))
        dest_parent = relative.rsplit("/", 1)[0] if "/" in relative else ""
        dest = "{app}" + ("\\" + dest_parent.replace("/", "\\") if dest_parent else "")
        flags = "ignoreversion uninsneveruninstall"
        if relative.startswith("plugins/"):
            flags += " onlyifdoesntexist"
        file_lines.append(f'Source: "{source}"; DestDir: "{dest}"; Flags: {flags}; Check: IsFreshInstall')
        if not relative.startswith("plugins/"):
            delete_lines.append(f"  DeleteOwnedFile('{relative.replace('/', chr(92))}', '{item['sha256']}');")
            stage_parent = "{app}\\.nxp-update\\staging\\" + version
            if dest_parent:
                stage_parent += "\\" + dest_parent.replace("/", "\\")
            file_lines.append(f'Source: "{source}"; DestDir: "{stage_parent}"; Flags: {flags}; Check: IsTrustedUpgrade')
            stage_checks.append(f"  VerifyStagedFile('{relative.replace('/', chr(92))}', '{item['sha256']}');")
    replacements = {
        "@@VERSION@@": version,
        "@@OUTPUT_DIR@@": str(output_dir),
        "@@FILES@@": "\n".join(file_lines),
        "@@DELETE_OWNED@@": "\n".join(delete_lines),
        "@@VERIFY_STAGED@@": "\n".join(stage_checks),
        "@@DESKTOP_URL@@": dependencies["Microsoft.WindowsDesktop.App"]["url"],
        "@@DESKTOP_SHA256@@": dependencies["Microsoft.WindowsDesktop.App"]["sha256"],
        "@@ASPNET_URL@@": dependencies["Microsoft.AspNetCore.App"]["url"],
        "@@ASPNET_SHA256@@": dependencies["Microsoft.AspNetCore.App"]["sha256"],
    }
    for needle, replacement in replacements.items():
        template = template.replace(needle, replacement)
    _require("@@" not in template, "安装器脚本占位符未填完")
    return template


def build_installer(production_root: Path, metadata_path: Path, dependency_path: Path,
                    compiler: Path, output_dir: Path, template_path: Path) -> dict:
    production_root = production_root.resolve()
    metadata_path = metadata_path.resolve()
    compiler = compiler.resolve()
    output_dir = output_dir.resolve()
    _require(not output_dir.exists(), "安装器输出目录已存在，拒绝覆盖")
    _require(compiler.is_file() and sha256(compiler) == INNO_COMPILER_SHA256,
             "Inno Setup 6.7.3 编译器字节与固定版本不符")
    metadata = json.loads(metadata_path.read_text(encoding="utf-8"))
    _require(isinstance(metadata, dict) and isinstance(metadata.get("tag"), str)
             and isinstance(metadata.get("sourceSha"), str), "生产 ZIP 元数据身份无效")
    package = metadata_path.parent / f"NexusPipeline-{metadata['tag']}-win-x64.zip"
    verify_received_package(package, metadata_path,
                            expected_source_sha=metadata["sourceSha"], expected_tag=metadata["tag"])
    files = verified_payload(production_root, metadata)
    dependencies = dependency_pair(dependency_path)
    script = render_script(template_path.read_text(encoding="utf-8"), production_root=production_root,
                           output_dir=output_dir, metadata=metadata, files=files, dependencies=dependencies)
    output_dir.mkdir(parents=True)
    script_path = output_dir / "installer.generated.iss"
    script_path.write_text(script, encoding="utf-8")
    command = [str(compiler), "/Q", str(script_path)]
    completed = subprocess.run(command, check=False, capture_output=True, text=True, encoding="utf-8", errors="replace")
    (output_dir / "iscc.stdout.log").write_text(completed.stdout, encoding="utf-8")
    (output_dir / "iscc.stderr.log").write_text(completed.stderr, encoding="utf-8")
    _require(completed.returncode == 0, f"Inno Setup 编译失败，退出码 {completed.returncode}")
    setup = output_dir / f"NexusPipeline-v{metadata['version']}-win-x64-setup.exe"
    _require(set(path.name for path in output_dir.iterdir()) == {
        script_path.name, "iscc.stdout.log", "iscc.stderr.log", setup.name,
    }, "安装器输出文件集合不符")
    digest = sha256(setup)
    sidecar = output_dir / f"{setup.name}.sha256"
    sidecar.write_text(digest, encoding="ascii")
    result = {"schemaVersion": 1, "sourceSha": metadata["sourceSha"],
              "tag": metadata["tag"], "version": metadata["version"], "zipSha256": metadata["sha256"],
              "setupSha256": digest, "setupSizeBytes": setup.stat().st_size,
              "payloadFiles": files, "compilerSha256": INNO_COMPILER_SHA256,
              "compilerDistributionSha256": INNO_DISTRIBUTION_SHA256,
              "dependencies": list(dependencies.values())}
    (output_dir / "installer-build-metadata.json").write_text(json.dumps(result, ensure_ascii=False, sort_keys=True) + "\n", encoding="utf-8")
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--production-root", type=Path, required=True)
    parser.add_argument("--metadata", type=Path, required=True)
    parser.add_argument("--dependencies", type=Path, default=Path(__file__).with_name("runtime-dependencies.json"))
    parser.add_argument("--compiler", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = build_installer(args.production_root, args.metadata, args.dependencies, args.compiler,
                             args.output, Path(__file__).with_name("installer.iss.in"))
    print(json.dumps({key: result[key] for key in ("version", "zipSha256", "setupSha256", "setupSizeBytes")}, sort_keys=True))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except HostReleaseError as error:
        raise SystemExit(str(error)) from error
