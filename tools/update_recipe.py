"""Bounded transport recipes; package metadata never owns application files."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import re
import zipfile

try:
    from .application_payload import ASAR, ENTRY, MANIFEST_PATH, MAX_BYTES, MAX_FILES, MAX_FILE_BYTES, NATIVE, safe_path
except ImportError:
    from application_payload import ASAR, ENTRY, MANIFEST_PATH, MAX_BYTES, MAX_FILES, MAX_FILE_BYTES, NATIVE, safe_path

RECIPE_PATH = 'package-info/recipe.json'
GUIDES = {'package-info/README.zh-CN.md', 'package-info/README.en-US.md'}
CAPABILITIES = ['payload-manifest-v2', 'single-delta-v1', 'transaction-recovery-v1']
MAX_METADATA = 2 * 1024 * 1024
FIELDS = {'schemaVersion', 'packageKind', 'product', 'rid', 'installationGeneration', 'target',
          'base', 'requirements', 'includedFiles', 'reusedFiles', 'removedFiles', 'bundledFiles', 'manualCover'}


def require(condition: bool, reason: str) -> None:
    if not condition:
        raise ValueError('Update recipe: ' + reason)


def fields(value: object, names: set[str]) -> None:
    require(type(value) is dict and set(value) == names, 'unknown or missing fields')


def digest(value: object) -> bool:
    return type(value) is str and re.fullmatch('[0-9a-f]{64}', value) is not None


def version(value: object) -> bool:
    return type(value) is str and re.fullmatch(r'(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-(?:beta|rc)\.[1-9][0-9]*)?', value) is not None


def version_key(value: str) -> tuple:
    main, _, suffix = value.partition('-')
    stage, _, number = suffix.partition('.')
    return (*map(int, main.split('.')), {'beta': 0, 'rc': 1, '': 2}[stage], int(number or 0))


def parse(raw: bytes) -> dict:
    require(0 < len(raw) <= MAX_METADATA, 'metadata limit')

    def pairs(items):
        result = {}
        for key, value in items:
            require(key not in result, 'duplicate key')
            result[key] = value
        return result

    return validate(json.loads(raw, object_pairs_hook=pairs))


def application_path(name: object) -> str:
    name = safe_path(name)
    require(name in ('NexusPipeline.exe', 'NexusPipeline.update.exe', 'NexusPipeline Update.exe', 'README.md', MANIFEST_PATH)
            or name.startswith('resources/desktop/'), 'path outside application')
    return name


def file_map(items: object, *, bundled: bool = False) -> dict[str, dict]:
    require(type(items) is list and len(items) <= MAX_FILES, 'file count')
    result, folded, total = {}, set(), 0
    for item in items:
        fields(item, {'path', 'sizeBytes', 'sha256'})
        name = safe_path(item['path']) if bundled else application_path(item['path'])
        if bundled:
            require(any(name.startswith('plugins/' + plugin + '/') for plugin in ('EmulatorSupport', 'LiveScreenshot')), 'bundled path')
        require(name.casefold() not in folded, 'duplicate file')
        require(type(item['sizeBytes']) is int and 0 <= item['sizeBytes'] <= MAX_FILE_BYTES
                and digest(item['sha256']), 'file identity')
        total += item['sizeBytes']
        require(total <= MAX_BYTES, 'byte budget')
        folded.add(name.casefold())
        result[name] = item
    return result


def validate(recipe: dict) -> dict:
    fields(recipe, FIELDS)
    require(type(recipe['schemaVersion']) is int and recipe['schemaVersion'] == 1, 'schema')
    require(recipe['packageKind'] in ('portable', 'update') and recipe['product'] == 'NexusPipeline'
            and recipe['rid'] == 'win-x64' and recipe['installationGeneration'] == 'g0170', 'identity')
    target = recipe['target']
    fields(target, {'version', 'buildId', 'frontendHash', 'payloadManifestSha256'})
    require(version(target['version']) and all(digest(target[key]) for key in ('buildId', 'frontendHash', 'payloadManifestSha256')), 'target')
    fields(recipe['requirements'], {'readerProtocol', 'requiredCapabilities', 'routeId'})
    require(type(recipe['requirements']['readerProtocol']) is int and recipe['requirements']['readerProtocol'] == 2
            and recipe['requirements']['requiredCapabilities'] == CAPABILITIES
            and recipe['requirements']['routeId'] == 'g0170-no-store-change-v1', 'requirements')
    included, reused, removed = (file_map(recipe[key]) for key in ('includedFiles', 'reusedFiles', 'removedFiles'))
    bundled = file_map(recipe['bundledFiles'], bundled=True)
    require(not bundled or recipe['packageKind'] == 'portable', 'bundled files only in portable')
    names = [name.casefold() for name in (*included, *reused, *removed)]
    require(len(names) == len(set(names)) and len(names) <= MAX_FILES, 'overlapping file sets')
    target_names = {name.casefold() for name in (*included, *reused, *bundled)}
    require(not any('/'.join(name.split('/')[:index]) in target_names
                    for name in target_names for index in range(1, len(name.split('/')))), 'file directory conflict')
    require(len(target_names) <= MAX_FILES and sum(item['sizeBytes'] for item in (*included.values(), *reused.values(), *bundled.values())) <= MAX_BYTES, 'target budget')
    require(MANIFEST_PATH in included and included[MANIFEST_PATH]['sha256'] == target['payloadManifestSha256'], 'manifest identity')
    required = {'NexusPipeline.exe', 'NexusPipeline.update.exe', 'README.md', ENTRY, ASAR, NATIVE}
    combined = included | reused
    require(required <= combined.keys() and all(combined[name]['sizeBytes'] > 0 for name in required), 'required files')
    base = recipe['base']
    if base is not None:
        fields(base, {'releaseTag', 'releaseId', 'assetId', 'assetSha256', 'payloadManifestSha256'})
        require(type(base['releaseTag']) is str and base['releaseTag'].startswith('v') and version(base['releaseTag'][1:])
                and all(type(base[k]) is int and base[k] > 0 for k in ('releaseId', 'assetId'))
                and all(digest(base[k]) for k in ('assetSha256', 'payloadManifestSha256')), 'base identity')
        require(version_key('0.17.3') <= version_key(base['releaseTag'][1:]) < version_key(target['version']), 'unsupported base version')
    require(not (reused or removed) or base is not None, 'missing exact base')
    require(recipe['packageKind'] != 'portable' or base is None and not reused and not removed, 'portable must be complete')
    manual = recipe['manualCover']
    fields(manual, {'allowed', 'minimumSourceVersion', 'exactBaseRequired', 'requiresPostStartReconciliation', 'reason'})
    require(all(type(manual[key]) is bool for key in ('allowed', 'exactBaseRequired', 'requiresPostStartReconciliation'))
            and manual['minimumSourceVersion'] == '0.17.3'
            and type(manual['reason']) is str and 0 < len(manual['reason']) <= 256, 'manual cover')
    require(not manual['allowed'] or recipe['packageKind'] == 'update' and base is not None and not removed
            and manual['exactBaseRequired'] and manual['requiresPostStartReconciliation'], 'unsafe manual cover')
    return recipe


def encode(recipe: dict) -> bytes:
    return json.dumps(validate(recipe), sort_keys=True, separators=(',', ':')).encode('utf-8')


def guides(recipe: dict) -> dict[str, bytes]:
    validate(recipe)
    target, base = recipe['target'], recipe['base']
    exact = base['releaseTag'] + ' / ' + base['payloadManifestSha256'] if base else 'none / 无'
    included = '\n'.join('- ' + item['path'] for item in recipe['includedFiles'])
    reused = '\n'.join('- ' + item['path'] for item in recipe['reusedFiles']) or '- none / 无'
    identity = (f"Target / 目标: {target['version']}\nBuild: {target['buildId']}\n"
                f"Platform / 平台: win-x64 / g0170\nMinimum / 最低来源: 0.17.3\nExact baseline / 精确基线: {exact}\n")
    english = """Setup installs or maintains an installed instance. Portable is for a new empty directory and has no Uninstall entry or uninstall registration. Update is not a complete application and cannot be used for a new installation.
For the first transition from v0.17.3 use this release's Setup or the exact manual route below. The old reader cannot discover this package layout. Later updates use Host or Update.
Verify the ZIP SHA256 against the digest table in the same official GitHub Release before opening it. Package contents do not provide independent proof of their own authenticity.
"""
    chinese = """Setup 用于首次安装或维护安装版。Portable 只能在新的空目录解压，不含 Uninstall 或卸载注册。Update 不是完整软件，不能单独运行或用于新装。
v0.17.3 首次进入新协议请使用本次 Setup 或下述精确手动路线；旧客户端不能发现此包布局。后续正常更新可使用宿主或 Update。
解压前将 ZIP SHA256 与同次官方 GitHub Release 的摘要表核对；包内内容不能独立证明自身真实性。
"""
    if recipe['manualCover']['allowed']:
        english += """Manual application-file copying is allowed only for the exact baseline above with intact application files and no pending recovery.
1. Exit Host, desktop, tray and this instance's updater; finish all tasks and configuration edits. Closing the window alone is insufficient.
2. Back up the entire instance outside its directory, including application files, config, data, history, logs, plugins and .nxp. Do not overwrite an earlier backup.
3. Extract into a new temporary directory. Copy only the listed application files to the matching paths in the old instance. Do not copy package-info, delete reused desktop files or change user directories and installed plugins.
4. Start NexusPipeline.exe and check version, integrity and maintenance status. An installed instance may require online independent official-package verification to reconcile ownership, or the same-version Setup. Pending/offline verification does not mean uninstall or maintenance is repaired. Portable does not gain Uninstall or registration.
5. If copying is interrupted or versions disagree, exit all verified instance processes and restore matching application files and state from the complete backup. Preserve unknown processes and recovery journals for diagnosis; do not delete journals to force startup.
"""
        chinese += """仅上述精确基线、应用文件完整且没有待恢复现场时允许手动复制应用文件。
1. 退出宿主、桌面、托盘和本实例更新进程，结束任务及配置编辑；仅关闭窗口不够。
2. 将整个实例备份到实例外，包括应用文件、config、data、history、logs、plugins 和 .nxp，不覆盖旧备份。
3. 解压到新的临时目录，仅将下列应用文件复制到原实例的相同路径。不要复制 package-info，不删除复用的桌面文件，不改动用户目录或已有插件。
4. 启动 NexusPipeline.exe，核对版本、完整性及维护状态。安装版可能需要联网取得独立官方包证明完成归属校验，或运行同版本 Setup；离线或待校验不能视为卸载和维护能力已修复。便携版不会生成 Uninstall 或卸载注册。
5. 复制中断或版本不一致时，退出已核验属于本实例的进程，用完整备份恢复匹配的应用和状态。未知进程和恢复 journal 保留诊断，不删 journal 强行启动。
"""
    else:
        english += 'Manual copying is not supported: ' + recipe['manualCover']['reason'] + '. Use Setup or a new empty Portable directory. Preserve the old instance and do not import unknown old formats.\n'
        chinese += '不支持纯复制覆盖：' + recipe['manualCover']['reason'] + '。请使用 Setup 或新的空 Portable 目录；保留旧实例，不导入未知旧格式。\n'
    tail = '\nApplication files in ZIP / 包内应用文件:\n' + included + '\n\nReused files to preserve / 必须保留的复用文件:\n' + reused + '\n'
    return {'package-info/README.en-US.md': (identity + '\n' + english + tail).encode(),
            'package-info/README.zh-CN.md': (identity + '\n' + chinese + tail).encode()}


def asset_names(tag: str) -> set[str]:
    require(type(tag) is str and tag.startswith('v') and version(tag[1:]), 'release tag')
    prefix = 'NexusPipeline-' + tag + '-win-x64-'
    return {prefix + suffix for suffix in ('Setup.exe', 'Portable.zip', 'Update.zip')}


def create(application: dict, manifest_bytes: bytes, kind: str, base: dict | None = None,
           base_files: list[dict] | None = None, *, bundled_files: list[dict] | None = None) -> dict:
    require(type(application.get('schemaVersion')) is int and application.get('schemaVersion') == 2 and application.get('product') == 'NexusPipeline'
            and application.get('rid') == 'win-x64' and application.get('installationGeneration') == 'g0170', 'payload schema/identity')
    require(json.loads(manifest_bytes) == application, 'manifest bytes')
    target_files = [{key: item[key] for key in ('path', 'sizeBytes', 'sha256')} for item in application['files']]
    target_files.append({'path': MANIFEST_PATH, 'sizeBytes': len(manifest_bytes), 'sha256': hashlib.sha256(manifest_bytes).hexdigest()})
    target = file_map(target_files)
    previous = file_map(base_files or [])
    require((base is None) == (not previous), 'base inventory missing')
    if base is not None:
        require(MANIFEST_PATH in previous and previous[MANIFEST_PATH]['sha256'] == base['payloadManifestSha256'], 'base manifest mismatch')
    reused = [item for name, item in target.items() if name != MANIFEST_PATH and previous.get(name) == item]
    included = [item for item in target.values() if item not in reused]
    removed = [item for name, item in previous.items() if name not in target]
    manual = kind == 'update' and base is not None and not removed and 'NexusPipeline.update.exe' in previous
    return validate({'schemaVersion': 1, 'packageKind': kind, 'product': 'NexusPipeline', 'rid': 'win-x64',
                     'installationGeneration': 'g0170',
                     'target': {'version': application['productVersion'], 'buildId': application['buildId'],
                                'frontendHash': application['frontendHash'], 'payloadManifestSha256': target[MANIFEST_PATH]['sha256']},
                     'base': base,
                     'requirements': {'readerProtocol': 2, 'requiredCapabilities': CAPABILITIES, 'routeId': 'g0170-no-store-change-v1'},
                     'includedFiles': sorted(included, key=lambda item: item['path']),
                     'reusedFiles': sorted(reused, key=lambda item: item['path']),
                     'removedFiles': sorted(removed, key=lambda item: item['path']),
                     'bundledFiles': sorted(bundled_files or [], key=lambda item: item['path']),
                     'manualCover': {'allowed': manual, 'minimumSourceVersion': '0.17.3', 'exactBaseRequired': base is not None,
                                     'requiresPostStartReconciliation': True,
                                     'reason': 'exact-base-online-reconciliation' if manual else 'use-setup-or-new-portable-directory'}})


def generate(production: Path, output: Path, recipe: dict, guides: dict[str, bytes]) -> None:
    validate(recipe)
    require(set(guides) == GUIDES and all(type(raw) is bytes and 0 < len(raw) <= MAX_METADATA for raw in guides.values()), 'guides')
    with zipfile.ZipFile(output, 'x', compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        entries = {item['path']: item for item in recipe['includedFiles'] + recipe['bundledFiles']}
        for name in sorted(set(entries) | GUIDES | {RECIPE_PATH}):
            if name in entries:
                path = production / name
                for parent in (path, *path.parents):
                    require(not parent.is_symlink() and not (getattr(parent.lstat(), 'st_file_attributes', 0) & 0x400), 'linked input')
                    if parent == production:
                        break
                raw = path.read_bytes()
                require(len(raw) == entries[name]['sizeBytes'] and hashlib.sha256(raw).hexdigest() == entries[name]['sha256'], 'input changed')
            else:
                raw = encode(recipe) if name == RECIPE_PATH else guides[name]
            info = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            info.create_system = 0
            info.external_attr = 0o644 << 16
            info.compress_type = zipfile.ZIP_DEFLATED
            archive.writestr(info, raw)


def verify(package: Path, expected: dict) -> dict:
    validate(expected)
    with zipfile.ZipFile(package) as archive:
        infos = archive.infolist()
        require(len(infos) <= MAX_FILES + 3 and len({item.filename.casefold() for item in infos}) == len(infos), 'archive duplicate/count')
        entries = {item['path']: item for item in expected['includedFiles'] + expected['bundledFiles']}
        require({item.filename for item in infos} == set(entries) | GUIDES | {RECIPE_PATH}, 'archive file set')
        for info in infos:
            require(not info.is_dir() and (info.external_attr >> 16) & 0o170000 in (0, 0o100000), 'special entry')
            limit = entries[info.filename]['sizeBytes'] if info.filename in entries else MAX_METADATA
            require(info.file_size <= limit and (not info.file_size or info.compress_size > 0 and info.file_size / info.compress_size <= 250), 'entry budget')
            with archive.open(info) as stream:
                actual = hashlib.file_digest(stream, 'sha256').hexdigest()
            if info.filename in entries:
                require(info.file_size == limit and actual == entries[info.filename]['sha256'], 'entry identity')
        require(parse(archive.read(RECIPE_PATH)) == expected, 'recipe changed')
        application = json.loads(archive.read(MANIFEST_PATH))
        target = file_map([{key: item[key] for key in ('path', 'sizeBytes', 'sha256')} for item in application['files']])
        actual = file_map(expected['includedFiles'] + expected['reusedFiles'])
        del actual[MANIFEST_PATH]
        require(target == actual and application['schemaVersion'] == 2 and application['productVersion'] == expected['target']['version']
                and application['buildId'] == expected['target']['buildId'] and application['frontendHash'] == expected['target']['frontendHash'], 'target tree differs')
    return expected
