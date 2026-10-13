from __future__ import annotations

import hashlib
import json
from pathlib import Path
import zipfile

try:
    from .application_payload import MANIFEST_PATH, runtime_files, safe_path
except ImportError:
    from application_payload import MANIFEST_PATH, runtime_files, safe_path

COMPONENT_IDS = ("host-core", "desktop-app", "desktop-runtime")
ROUTE_ID = "g0170-no-store-change-v1"
CAPABILITIES = ["payload-manifest-v1", "migration-snapshot-v1", "transaction-recovery-v1"]


def asset_names(tag: str) -> set[str]:
    prefix = f"NexusPipeline-{tag}-win-x64"
    return {prefix + "-update.json", *(prefix + "-" + component + ".zip" for component in COMPONENT_IDS)}


def target_manifest(tag: str, application: dict, payload: list[dict], source_root: Path) -> dict:
    if application['productVersion'] != tag.removeprefix('v') or application['installationGeneration'] != 'g0170':
        raise ValueError('Unsupported target migration generation/version')
    upstream = runtime_files(source_root)
    prefix = f"NexusPipeline-{tag}-win-x64"
    entries = []
    seen = set()
    manifest = None
    for item in payload:
        name = safe_path(item['path'])
        if name.startswith('plugins/'):
            continue
        if name.casefold() in seen or type(item['sizeBytes']) is not int or item['sizeBytes'] < 0:
            raise ValueError('Invalid target file identity')
        seen.add(name.casefold())
        if name in upstream:
            component = 'desktop-runtime'
        elif name.startswith('resources/desktop/'):
            component = 'desktop-app'
        elif name in ('NexusPipeline.exe', 'README.md', MANIFEST_PATH):
            component = 'host-core'
        else:
            raise ValueError('File outside component inventory: ' + name)
        entries.append({**item, 'componentId': component})
        if name == MANIFEST_PATH:
            manifest = item
    if manifest is None or {item['componentId'] for item in entries} != set(COMPONENT_IDS):
        raise ValueError('Incomplete target components')
    if {item['path'] for item in entries} != {item['path'] for item in application['files']} | {MANIFEST_PATH}:
        raise ValueError('Target and application file sets differ')
    by_path = {item['path']: item for item in entries}
    for item in application['files']:
        if any(by_path[item['path']][key] != item[key] for key in ('sizeBytes', 'sha256')):
            raise ValueError('Application manifest bytes differ from full package')
    return {
        'schemaVersion': 1, 'product': 'NexusPipeline',
        'target': {'version': tag.removeprefix('v'), 'rid': 'win-x64',
                   'installationGeneration': application['installationGeneration'],
                   'buildId': application['buildId'], 'frontendHash': application['frontendHash'],
                   'payloadManifestSha256': manifest['sha256']},
        'requirements': {'readerProtocol': 1, 'runnerHandshakeProtocol': 1, 'requiredCapabilities': CAPABILITIES},
        'fullPackage': {'assetName': prefix + '.zip'},
        'components': [{'id': component, 'assetName': prefix + '-' + component + '.zip'} for component in COMPONENT_IDS],
        'files': sorted(entries, key=lambda item: item['path']),
        'migration': {'routeId': ROUTE_ID, 'sourceGeneration': 'g0170', 'targetGeneration': 'g0170',
                      'stores': [], 'migratorIds': [], 'runner': {'kind': 'target-host', 'entryPath': 'NexusPipeline.exe'}},
    }


def generate(production: Path, output: Path, tag: str, application: dict, payload: list[dict], source_root: Path) -> dict:
    manifest = target_manifest(tag, application, payload, source_root)
    for component in manifest['components']:
        selected = [item for item in manifest['files'] if item['componentId'] == component['id']]
        with zipfile.ZipFile(output / component['assetName'], 'x', compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
            for item in selected:
                file = production / item['path']
                if not file.is_file() or file.is_symlink():
                    raise ValueError('Missing/linked component input')
                info = zipfile.ZipInfo(item['path'], date_time=(1980, 1, 1, 0, 0, 0))
                info.create_system = 0
                info.external_attr = 0o644 << 16
                info.compress_type = zipfile.ZIP_DEFLATED
                info.compress_level = 9
                digest, size = hashlib.sha256(), 0
                with file.open('rb') as source, archive.open(info, 'w') as destination:
                    while block := source.read(1024 * 1024):
                        digest.update(block)
                        size += len(block)
                        destination.write(block)
                if size != item['sizeBytes'] or digest.hexdigest() != item['sha256']:
                    raise ValueError('Frozen production input changed: ' + item['path'])
    target = output / f'NexusPipeline-{tag}-win-x64-update.json'
    with target.open('x', encoding='utf-8', newline='\n') as stream:
        stream.write(json.dumps(manifest, sort_keys=True, separators=(',', ':')) + '\n')
    return manifest


def verify(output: Path, package: Path, metadata: dict, source_root: Path) -> dict:
    tag = metadata['tag']
    target = output / f'NexusPipeline-{tag}-win-x64-update.json'
    if not target.is_file() or target.is_symlink() or target.stat().st_size > 2 * 1024 * 1024:
        raise ValueError('Invalid target manifest asset')
    with zipfile.ZipFile(package) as archive:
        application = json.loads(archive.read(MANIFEST_PATH))
    expected = target_manifest(tag, application, metadata['payloadFiles'], source_root)
    expected_bytes = (json.dumps(expected, sort_keys=True, separators=(',', ':')) + '\n').encode()
    if target.read_bytes() != expected_bytes:
        raise ValueError('Target manifest differs from frozen full package')
    for component in expected['components']:
        file = output / component['assetName']
        if not file.is_file() or file.is_symlink():
            raise ValueError('Missing/linked component asset')
        selected = {item['path']: item for item in expected['files'] if item['componentId'] == component['id']}
        with zipfile.ZipFile(file) as archive:
            infos = archive.infolist()
            if len(infos) != len(selected) or {item.filename for item in infos} != set(selected):
                raise ValueError('Component file set differs from target')
            for info in infos:
                expected_file = selected[info.filename]
                if info.is_dir() or (info.external_attr >> 16) & 0o170000 not in (0, 0o100000):
                    raise ValueError('Component contains a link/special entry')
                if info.file_size != expected_file['sizeBytes']:
                    raise ValueError('Component file size mismatch')
                with archive.open(info) as stream:
                    digest = hashlib.file_digest(stream, 'sha256').hexdigest()
                if digest != expected_file['sha256']:
                    raise ValueError('Component file digest mismatch')
    return expected
