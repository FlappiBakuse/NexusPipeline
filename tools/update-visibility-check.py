"""更新可见性自检：按宿主更新引擎的当前契约复核已发布 Release 的资产。

复现 UpdateCatalog 对默认更新源的解析结果：
- 只接受受限 Nexus 版本格式的非 draft 发布；
- GitHub Release 的 prerelease 标记必须符合宿主项目发布策略：major=0 或带 beta/rc 后缀；
- stable 渠道只显示 major>=1 且无 beta/rc 后缀的正式 Release；
- 必须同时存在 zip 与 sha256 两项资产，且名称与 tag 派生结果一致；
- 下载主机必须在默认源白名单内；
- 用资产自身的 browser_download_url 下载并校验 SHA256 内容格式与哈希。
"""
import argparse
import hashlib
import io
import json
import re
import urllib.parse
import urllib.request
import zipfile
from pathlib import Path
from xml.etree import ElementTree

REPO = "FlappiBakuse/NexusPipeline"
PROJECT_ROOT = Path(__file__).resolve().parents[1]
DEFAULT_SOURCE_HOST = "api.github.com"
ALLOWED_DOWNLOAD_HOSTS = {
    "api.github.com",
    "github.com",
    "objects.githubusercontent.com",
    "github-releases.githubusercontent.com",
}
VERSION_PATTERN = re.compile(
    r"(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)"
    r"(?:-(beta|rc)\.(0|[1-9]\d*))?"
)


def parse_version(value: str) -> tuple[int, int, int, str | None, int]:
    text = value.strip()
    if text[:1].lower() == "v":
        text = text[1:]
    match = VERSION_PATTERN.fullmatch(text)
    if match is None:
        raise ValueError(f"无效的 Nexus 版本：{value}")
    major, minor, patch = (int(part) for part in match.groups()[:3])
    stage = match.group(4)
    stage_number = int(match.group(5)) if stage is not None else 0
    return major, minor, patch, stage, stage_number


def version_key(version: tuple[int, int, int, str | None, int]) -> tuple[int, int, int, int, int]:
    stage_rank = {"beta": 0, "rc": 1, None: 2}[version[3]]
    return (*version[:3], stage_rank, version[4])


def format_version(version: tuple[int, int, int, str | None, int]) -> str:
    core = ".".join(str(part) for part in version[:3])
    return f"{core}-{version[3]}.{version[4]}" if version[3] else core


def expected_prerelease(version: tuple[int, int, int, str | None, int]) -> bool:
    return version[0] == 0 or version[3] in {"beta", "rc"}


def load_project_tag() -> str:
    project_file = PROJECT_ROOT / "src" / "NexusPipeline.csproj"
    root = ElementTree.parse(project_file).getroot()
    version = next(
        (
            element.text.strip()
            for element in root.iter()
            if element.tag.rsplit("}", 1)[-1] == "Version"
            and element.text
            and element.text.strip()
        ),
        None,
    )
    if version is None:
        raise ValueError(f"{project_file} 缺少 Version 配置")
    return f"v{format_version(parse_version(version))}"


def check(label: str, ok: bool, detail: str = "", failures: list[str] | None = None) -> None:
    print(f"{'PASS' if ok else 'FAIL'}  {label}{'  ' + detail if detail else ''}")
    if not ok and failures is not None:
        failures.append(label)


def get(url: str, binary: bool = False) -> bytes | str:
    request = urllib.request.Request(
        url,
        headers={
            "User-Agent": "nexus-update-visibility-check",
            "Accept": "application/vnd.github+json",
        },
    )
    with urllib.request.urlopen(request) as response:
        data = response.read()
    return data if binary else data.decode("utf-8")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="校验 NexusPipeline Release 的更新可见性")
    parser.add_argument(
        "tag",
        nargs="?",
        help="目标 tag，例如 v0.15.12；省略时读取 src/NexusPipeline.csproj 的 Version",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    tag = args.tag or load_project_tag()
    version = parse_version(tag)
    tag = f"v{format_version(version)}"
    version_text = format_version(version)
    zip_name = f"NexusPipeline-v{version_text}-win-x64.zip"
    sha_name = f"{zip_name}.sha256"
    failures: list[str] = []

    # 1. 默认更新源 = GitHub Releases API
    releases = json.loads(get(f"https://{DEFAULT_SOURCE_HOST}/repos/{REPO}/releases?per_page=10"))
    check("默认更新源可访问（api.github.com）", isinstance(releases, list), f"{len(releases)} 个发布", failures)
    if not isinstance(releases, list):
        return 1

    # 2. 目标 tag 存在且符合宿主发布分类
    release = next((item for item in releases if isinstance(item, dict) and item.get("tag_name") == tag), None)
    check(f"{tag} 出现在更新源发布列表", release is not None, failures=failures)
    if release is None:
        return 1
    check(f"{tag} 非 draft", release.get("draft") is not True, failures=failures)
    declared_prerelease = release.get("prerelease") is True
    expected = expected_prerelease(version)
    check(
        f"{tag} prerelease 标记符合宿主发布策略",
        declared_prerelease == expected,
        f"实际={declared_prerelease}，预期={expected}",
        failures,
    )

    # 3. 目标版本高于 API 返回的其他合法版本
    other_versions = []
    for item in releases:
        if item is release or not isinstance(item, dict):
            continue
        try:
            candidate = parse_version(str(item.get("tag_name", "")))
        except ValueError:
            continue
        other_versions.append(candidate)
    if other_versions:
        latest_previous = max(other_versions, key=version_key)
        check(
            f"版本高于上一发布 v{format_version(latest_previous)}",
            version_key(version) > version_key(latest_previous),
            failures=failures,
        )

    # 4. 两项资产齐全且命名与 tag 派生一致
    assets = {item.get("name"): item.get("browser_download_url", "") for item in release.get("assets", [])}
    check(f"zip 资产名称匹配 {zip_name}", zip_name in assets, failures=failures)
    check(f"sha256 资产名称匹配 {sha_name}", sha_name in assets, failures=failures)
    if zip_name not in assets or sha_name not in assets:
        return 1

    # 5. 下载主机在白名单内
    for name in (zip_name, sha_name):
        host = urllib.parse.urlparse(assets[name]).hostname
        check(f"{name} 下载主机在默认源白名单内", host in ALLOWED_DOWNLOAD_HOSTS, host or "(空)", failures)

    # 6. 用资产自身的下载 URL 取回并校验
    zip_bytes = get(assets[zip_name], binary=True)
    sha_text = get(assets[sha_name])
    assert isinstance(zip_bytes, bytes)
    assert isinstance(sha_text, str)
    check("zip 资产可下载", len(zip_bytes) > 0, f"{len(zip_bytes)} 字节", failures)
    check("sha256 文件可下载", len(sha_text) > 0, f"{len(sha_text)} 字符", failures)

    zip_hash = hashlib.sha256(zip_bytes).hexdigest()
    check("sha256 文件为纯 hash（不含文件名与空白）", re.fullmatch(r"[0-9a-f]{64}", sha_text) is not None, failures=failures)
    check("sha256 文件哈希与 zip 一致", sha_text == zip_hash, zip_hash, failures)

    with zipfile.ZipFile(io.BytesIO(zip_bytes)) as archive:
        names = archive.namelist()
        check("zip 根布局含 nexus-pipeline.exe", "nexus-pipeline.exe" in names, failures=failures)
        check("zip 根布局含 wwwroot/", any(name.startswith("wwwroot/") for name in names), failures=failures)
        check(
            "zip 不含用户配置与运行数据",
            not any(name.startswith(("config/", "data/", "history/", "logs/")) for name in names),
            failures=failures,
        )
        check(
            "zip 不含绝对路径或上级目录条目",
            not any(name.startswith("/") or ".." in name.split("/") for name in names),
            failures=failures,
        )

    print()
    if failures:
        print(f"更新可见性自检失败：{len(failures)} 项")
        for item in failures:
            print(f"  - {item}")
        return 1
    print("更新可见性自检通过：更新引擎可从默认源识别该发布，两项资产齐全、命名正确、哈希一致。")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
