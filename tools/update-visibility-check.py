"""更新可见性自检：按宿主更新引擎的当前契约复核已发布 Release 的资产。

复现 UpdateCatalog 对默认更新源的解析结果：
- 只接受 tag 形如 vX.Y.Z 的非 draft 发布；
- prerelease 渠道接受 prerelease 发布；
- 必须同时存在 zip 与 sha256 两项资产，且名称与 tag 派生结果一致；
- 下载主机必须在默认源白名单内；
- 用资产自身的 browser_download_url 下载并校验 SHA256 内容格式与哈希。
"""
import hashlib
import io
import json
import re
import sys
import urllib.parse
import urllib.request
import zipfile

REPO = "FlappiBakuse/NexusPipeline"
TAG = "v0.15.8"
VERSION = TAG[1:]
ZIP_NAME = f"NexusPipeline-v{VERSION}-win-x64.zip"
SHA_NAME = f"{ZIP_NAME}.sha256"
ALLOWED_DOWNLOAD_HOSTS = {"api.github.com", "github.com", "objects.githubusercontent.com", "github-releases.githubusercontent.com"}
DEFAULT_SOURCE_HOST = "api.github.com"

failures = []


def check(label, ok, detail=""):
    print(f"{'PASS' if ok else 'FAIL'}  {label}{'  ' + detail if detail else ''}")
    if not ok:
        failures.append(label)


def get(url, binary=False):
    request = urllib.request.Request(url, headers={"User-Agent": "nexus-update-visibility-check", "Accept": "application/vnd.github+json"})
    with urllib.request.urlopen(request) as response:
        data = response.read()
    return data if binary else data.decode("utf-8")


# 1. 默认更新源 = GitHub Releases API
releases = json.loads(get(f"https://{DEFAULT_SOURCE_HOST}/repos/{REPO}/releases?per_page=10"))
check("默认更新源可访问（api.github.com）", isinstance(releases, list), f"{len(releases)} 个发布")

# 2. 目标 tag 存在且非 draft
release = next((item for item in releases if item.get("tag_name") == TAG), None)
check(f"{TAG} 出现在更新源发布列表", release is not None)
if release is None:
    sys.exit(1)
check(f"{TAG} 非 draft", release.get("draft") is not True)
check(f"{TAG} 标记为 prerelease（v1.0.0 前契约）", release.get("prerelease") is True)

# 3. tag 可被引擎解析为严格递增的 SemVer
match = re.fullmatch(r"v(\d+)\.(\d+)\.(\d+)", TAG)
check("tag 符合 vX.Y.Z 解析规则", match is not None)
parsed = tuple(int(part) for part in match.groups()) if match else None
check("版本高于上一发布 v0.15.7", parsed > (0, 15, 7), f"{parsed} > (0, 15, 7)")

# 4. 两项资产齐全且命名与 tag 派生一致
assets = {item.get("name"): item.get("browser_download_url", "") for item in release.get("assets", [])}
check(f"zip 资产名称匹配 {ZIP_NAME}", ZIP_NAME in assets)
check(f"sha256 资产名称匹配 {SHA_NAME}", SHA_NAME in assets)
if ZIP_NAME not in assets or SHA_NAME not in assets:
    sys.exit(1)

# 5. 下载主机在白名单内
for name in (ZIP_NAME, SHA_NAME):
    host = urllib.parse.urlparse(assets[name]).hostname
    check(f"{name} 下载主机在默认源白名单内", host in ALLOWED_DOWNLOAD_HOSTS, host or "(空)")

# 6. 用资产自身的下载 URL 取回并校验
zip_bytes = get(assets[ZIP_NAME], binary=True)
sha_text = get(assets[SHA_NAME])
check("zip 资产可下载", len(zip_bytes) > 0, f"{len(zip_bytes)} 字节")
check("sha256 资产可下载", len(sha_text) > 0, f"{len(sha_text)} 字符")

zip_hash = hashlib.sha256(zip_bytes).hexdigest()
check("sha256 文件为纯 hash（不含文件名与空白）", re.fullmatch(r"[0-9a-f]{64}", sha_text) is not None)
check("sha256 文件哈希与 zip 一致", sha_text == zip_hash, zip_hash)

with zipfile.ZipFile(io.BytesIO(zip_bytes)) as archive:
    names = archive.namelist()
    check("zip 根布局含 nexus-pipeline.exe", "nexus-pipeline.exe" in names)
    check("zip 根布局含 wwwroot/", any(name.startswith("wwwroot/") for name in names))
    check("zip 不含用户配置与运行数据", not any(name.startswith(("config/", "data/", "history/", "logs/")) for name in names))
    check("zip 不含绝对路径或上级目录条目", not any(name.startswith("/") or ".." in name.split("/") for name in names))

print()
if failures:
    print(f"更新可见性自检失败：{len(failures)} 项")
    for item in failures:
        print(f"  - {item}")
    sys.exit(1)
print("更新可见性自检通过：更新引擎可从默认源识别该发布，两项资产齐全、命名正确、哈希一致。")
