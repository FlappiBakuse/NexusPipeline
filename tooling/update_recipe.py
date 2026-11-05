"""Bind complete or exact-base application transport to the public recipe contract."""
import hashlib
import json
import re

from application_manifest import ASAR, ENTRY, HELPER, HOST, MANIFEST, canonical, kind
from desktop_bundle import safe_name


RECIPE = 'package-info/recipe.json'
GUIDES = ('package-info/README.zh-CN.md', 'package-info/README.en-US.md')
CAPABILITIES = ['payload-manifest-v2', 'single-delta-v1', 'transaction-recovery-v1']
FIELDS = {'schemaVersion', 'packageKind', 'product', 'rid', 'installationGeneration', 'target', 'base', 'requirements',
          'includedFiles', 'reusedFiles', 'removedFiles', 'bundledFiles', 'manualCover'}


def require(condition, message):
    if not condition:
        raise ValueError('Update recipe: ' + message)


def fields(value, names):
    require(type(value) is dict and set(value) == set(names), 'unknown or missing fields')


def digest(value):
    return type(value) is str and re.fullmatch('[a-f0-9]{64}', value) is not None


def version_key(value):
    require(type(value) is str and re.fullmatch(r'(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-(beta|rc)\.([1-9][0-9]*))?', value), 'version')
    main, _, suffix = value.partition('-')
    stage, _, number = suffix.partition('.')
    numbers = tuple(map(int, main.split('.')))
    require(all(number <= 2147483647 for number in numbers), 'version component bound')
    return (*numbers, {'beta': 0, 'rc': 1, '': 2}[stage], int(number or 0))


def file_map(items, bundled=False):
    require(type(items) is list and len(items) <= 4096, 'file count')
    result, folded, total = {}, set(), 0
    for item in items:
        fields(item, ('path', 'sizeBytes', 'sha256'))
        name = safe_name(item['path'])
        require(len(name) <= 240, 'path bound')
        if bundled:
            require(any(name.startswith('plugins/' + artifact + '/') for artifact in ('EmulatorSupport', 'LiveScreenshot')), 'bundled path')
        elif name != MANIFEST:
            kind(name)
        require(name.casefold() not in folded and type(item['sizeBytes']) is int and 0 <= item['sizeBytes'] <= 268435456 and digest(item['sha256']), 'file identity')
        total += item['sizeBytes']
        require(total <= 536870912, 'byte bound')
        folded.add(name.casefold()); result[name] = item
    return result


def validate(recipe):
    fields(recipe, FIELDS)
    require(type(recipe['schemaVersion']) is int and recipe['schemaVersion'] == 1 and recipe['packageKind'] in ('portable', 'update') and
        recipe['product'] == 'NexusPipeline' and recipe['rid'] == 'win-x64' and recipe['installationGeneration'] == 'g0180', 'identity')
    target = recipe['target']
    fields(target, ('version', 'buildId', 'frontendHash', 'payloadManifestSha256'))
    require(version_key(target['version']) >= version_key('0.18.0') and
        all(digest(target[name]) for name in ('buildId', 'frontendHash', 'payloadManifestSha256')), 'target identity')
    fields(recipe['requirements'], ('readerProtocol', 'requiredCapabilities', 'routeId'))
    require(recipe['requirements'] == dict(readerProtocol=2, requiredCapabilities=CAPABILITIES, routeId='g0180-no-store-change-v1'), 'reader requirements')
    included, reused, removed = (file_map(recipe[name]) for name in ('includedFiles', 'reusedFiles', 'removedFiles'))
    bundled = file_map(recipe['bundledFiles'], bundled=True)
    require(not bundled or recipe['packageKind'] == 'portable', 'bundled update files')
    names = [name.casefold() for name in (*included, *reused, *removed)]
    require(len(names) == len(set(names)) and len(names) <= 4096, 'overlapping application sets')
    combined = included | reused
    require({HOST, HELPER, 'README.md', ENTRY, ASAR} <= combined.keys() and
        all(combined[name]['sizeBytes'] > 0 for name in (HOST, HELPER, 'README.md', ENTRY, ASAR)), 'required application files')
    require(MANIFEST in included and included[MANIFEST]['sha256'] == target['payloadManifestSha256'], 'manifest identity')
    target_names = {name.casefold() for name in (*combined, *bundled)}
    require(not any('/'.join(name.split('/')[:index]) in target_names for name in target_names for index in range(1, len(name.split('/')))), 'file directory collision')
    require(len(target_names) <= 4096 and sum(item['sizeBytes'] for item in (*combined.values(), *bundled.values())) <= 536870912, 'target bound')
    base = recipe['base']
    if base is not None:
        fields(base, ('releaseTag', 'releaseId', 'assetId', 'assetSha256', 'payloadManifestSha256'))
        require(type(base['releaseTag']) is str and base['releaseTag'].startswith('v') and
            version_key('0.18.0') <= version_key(base['releaseTag'][1:]) < version_key(target['version']) and
            all(type(base[name]) is int and 1 <= base[name] <= 9007199254740991 for name in ('releaseId', 'assetId')) and
            all(digest(base[name]) for name in ('assetSha256', 'payloadManifestSha256')), 'exact base identity')
    require(not (reused or removed) or base is not None, 'missing exact base')
    require(recipe['packageKind'] != 'portable' or base is None and not reused and not removed, 'portable must be complete')
    manual = recipe['manualCover']
    fields(manual, ('allowed', 'minimumSourceVersion', 'exactBaseRequired', 'requiresPostStartReconciliation', 'reason'))
    require(all(type(manual[name]) is bool for name in ('allowed', 'exactBaseRequired', 'requiresPostStartReconciliation')) and
        manual['minimumSourceVersion'] == '0.18.0' and type(manual['reason']) is str and 0 < len(manual['reason']) <= 256, 'manual cover')
    require(not manual['allowed'] or recipe['packageKind'] == 'update' and base is not None and not removed and
        manual['exactBaseRequired'] and manual['requiresPostStartReconciliation'], 'unsafe manual cover')
    return recipe


def create(application, manifest_bytes, package_kind, base=None, base_files=None, bundled_files=None):
    require(application['schemaVersion'] == 2 and application['installationGeneration'] == 'g0180' and
        json.loads(manifest_bytes) == application, 'application manifest')
    target_files = [{key: item[key] for key in ('path', 'sizeBytes', 'sha256')} for item in application['files']]
    target_files.append(dict(path=MANIFEST, sizeBytes=len(manifest_bytes), sha256=hashlib.sha256(manifest_bytes).hexdigest()))
    target, previous = file_map(target_files), file_map(base_files or [])
    require((base is None) == (not previous), 'base inventory required')
    if base is not None:
        fields(base, ('releaseTag', 'releaseId', 'assetId', 'assetSha256', 'payloadManifestSha256'))
        require(MANIFEST in previous and previous[MANIFEST]['sha256'] == base['payloadManifestSha256'], 'base manifest binding')
    reused = [item for name, item in target.items() if name != MANIFEST and previous.get(name) == item]
    included = [item for item in target.values() if item not in reused]
    removed = [item for name, item in previous.items() if name not in target]
    manual = package_kind == 'update' and base is not None and not removed and HELPER in previous
    return validate(dict(schemaVersion=1, packageKind=package_kind, product='NexusPipeline', rid='win-x64', installationGeneration='g0180',
        target=dict(version=application['productVersion'], buildId=application['buildId'], frontendHash=application['frontendHash'],
            payloadManifestSha256=target[MANIFEST]['sha256']), base=base,
        requirements=dict(readerProtocol=2, requiredCapabilities=CAPABILITIES, routeId='g0180-no-store-change-v1'),
        includedFiles=sorted(included, key=lambda item:item['path']), reusedFiles=sorted(reused, key=lambda item:item['path']),
        removedFiles=sorted(removed, key=lambda item:item['path']), bundledFiles=sorted(bundled_files or [], key=lambda item:item['path']),
        manualCover=dict(allowed=manual, minimumSourceVersion='0.18.0', exactBaseRequired=base is not None,
            requiresPostStartReconciliation=True, reason='exact-base-online-reconciliation' if manual else 'use-setup-or-new-portable-directory')))


def encode(recipe):
    return canonical(validate(recipe))


def parse(raw):
    require(type(raw) is bytes and 0 < len(raw) <= 2097152 and not raw.startswith(b'\xef\xbb\xbf'), 'metadata bound or encoding')
    def pairs(items):
        value = {}
        for key, item in items:
            require(key not in value, 'duplicate metadata field')
            value[key] = item
        return value
    return validate(json.loads(raw, object_pairs_hook=pairs))


def guides(recipe):
    validate(recipe)
    target, base = recipe['target'], recipe['base']
    identity = (f"Target / 目标: {target['version']}\nBuild: {target['buildId']}\nPlatform / 平台: win-x64 / g0180\n"
        f"Exact baseline / 精确基线: {base['releaseTag'] + ' / ' + base['payloadManifestSha256'] if base else 'none / 无'}\n")
    chinese = 'Setup 用于安装或维护安装版；Portable 用于新的空目录。Update 交由宿主执行，包内说明和配方不覆盖应用文件。\n0.17 与 0.18 使用独立新目录，不复制旧数据。更新、移动或备份前退出本实例的宿主、桌面、托盘与任务进程，保留 runtime 和已有插件。\n'
    english = 'Use Setup for installation or installed-instance maintenance and Portable for a new empty directory. Let the Host apply Update; package metadata does not own application files.\nKeep 0.17 and 0.18 in separate directories. Exit the instance Host, desktop, tray and task processes before updating, moving or backing up; preserve runtime and existing plugins.\n'
    if recipe['manualCover']['allowed']:
        chinese += '仅上列精确基线允许手动覆盖。先在实例外保留完整备份，仅复制下列包含文件，保留复用文件；不要复制 package-info 或改动用户数据。重启后需在线确权或同版本 Setup 完成安装归属校验；失败时退出已核验进程并恢复匹配的应用与状态，保留未知进程和 journal。\n'
        english += 'Manual copying requires the exact baseline above. Keep a complete external backup, copy only included application files and preserve reused files; do not copy package-info or change user data. After restart, online ownership reconciliation or same-version Setup is required. On failure, exit verified instance processes and restore matching application and state; preserve unknown processes and journals.\n'
    else:
        chinese += '当前包不支持手动复制覆盖。请通过宿主更新、Setup 或新的空 Portable 目录使用。\n'
        english += 'This package does not support manual copying. Use the Host update transaction, Setup or a new empty Portable directory.\n'
    included = '\n'.join('- ' + item['path'] for item in recipe['includedFiles'])
    reused = '\n'.join('- ' + item['path'] for item in recipe['reusedFiles']) or '- none / 无'
    tail = '\nIncluded application files / 包含应用文件:\n' + included + '\n\nReused files to preserve / 保留复用文件:\n' + reused + '\n'
    return {GUIDES[0]: (identity + chinese + tail).encode('utf-8'), GUIDES[1]: (identity + english + tail).encode('utf-8')}
