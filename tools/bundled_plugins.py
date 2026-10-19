"""Resolve bundled stable packages from a fixed Plugins checkout."""
from __future__ import annotations

import hashlib
import io
import json
from pathlib import Path
import re
import zipfile

REPOSITORY = 'FlappiBakuse/NexusPipeline-Plugins'
ARTIFACTS = {'EmulatorSupport', 'LiveScreenshot'}


def host_api_version(host_root: Path) -> str:
    api_source = (host_root / 'src/NexusPipeline.Plugin.Abstractions/PluginApi.cs').read_text(encoding='utf-8')
    api_class = re.search(r'public static class PluginApiVersion\s*\{([^}]+)\}', api_source)
    if api_class is None:
        raise ValueError('无法读取 Host Plugin API 版本')
    parts = [re.findall(r'public const int ' + part + r'\s*=\s*(\d+)\s*;', api_class[1]) for part in ('Major', 'Minor')]
    if not all(len(part) == 1 for part in parts):
        raise ValueError('Host Plugin API 版本无效')
    return '.'.join(part[0] for part in parts)


def stage(production: Path, partner: Path, policy_path: Path) -> dict:
    try:
        from .host_release import (_require, _safe_package_entry, parse_version, project_version,
                                   MAX_SINGLE_ENTRY_BYTES, MAX_PACKAGE_UNCOMPRESSED_BYTES,
                                   MAX_COMPRESSION_RATIO)
    except ImportError:
        from host_release import (_require, _safe_package_entry, parse_version, project_version,
                                  MAX_SINGLE_ENTRY_BYTES, MAX_PACKAGE_UNCOMPRESSED_BYTES,
                                  MAX_COMPRESSION_RATIO)
    policy = json.loads(policy_path.read_bytes())
    _require(set(policy) == {'schemaVersion', 'repository', 'plugins'}
             and policy['schemaVersion'] == 1 and policy['repository'] == REPOSITORY
             and isinstance(policy['plugins'], list) and len(policy['plugins']) == 2
             and all(isinstance(item, dict) and set(item) == {'artifactName'} for item in policy['plugins'])
             and {item['artifactName'] for item in policy['plugins']} == ARTIFACTS,
             '预装插件选择策略无效')
    projection = partner / 'catalog.json'
    canonical = partner / 'metadata/catalog/stable.json'
    _require(projection.is_file() and not projection.is_symlink() and not canonical.is_symlink(),
             '官方稳定 catalog 路径无效')
    if canonical.exists():
        _require(canonical.is_file() and canonical.read_bytes() == projection.read_bytes(),
                 '官方稳定 catalog canonical 与根投影不一致')
    catalog = json.loads(projection.read_text(encoding='utf-8-sig'))
    _require(catalog.get('schemaVersion') == 2 and catalog.get('repository') == REPOSITORY,
             '官方稳定 catalog 来源无效')
    host_root = policy_path.resolve().parents[4]
    host_version = parse_version(project_version(host_root))
    api_version = host_api_version(host_root)
    resolved, payload = [], {}
    for selection in policy['plugins']:
        artifact = selection['artifactName']
        entries = [entry for entry in catalog['plugins'] if entry.get('artifactName', '').casefold() == artifact.casefold()]
        _require(len(entries) == 1 and entries[0]['artifactName'] == artifact,
                 f'官方稳定 catalog 插件身份无效：{artifact}')
        entry = entries[0]
        _require(entry.get('kind') == 'managed-code' and entry.get('apiVersion') == api_version,
                 f'预装插件类型或 Plugin API 不兼容：{artifact}')
        version = entry['version']
        parse_version(version)
        minimum = parse_version(entry['minHostVersion'])
        _require((minimum[:3], 2 if minimum[3] is None else (0 if minimum[3] == 'beta' else 1), minimum[4])
                 <= (host_version[:3], 2 if host_version[3] is None else (0 if host_version[3] == 'beta' else 1), host_version[4]),
                 f'预装插件最低宿主版本不兼容：{artifact}')
        _require(entry['packageUrl'] == f'https://raw.githubusercontent.com/{REPOSITORY}/main/packages/{artifact}/{artifact}-{version}.zip'
                 and re.fullmatch('[0-9a-f]{64}', entry['sha256']) is not None
                 and type(entry['sizeBytes']) is int and 0 < entry['sizeBytes'] <= MAX_PACKAGE_UNCOMPRESSED_BYTES,
                 f'官方稳定插件包身份无效：{artifact}')
        package = partner / 'packages' / artifact / f'{artifact}-{version}.zip'
        _require(package.is_file() and not any(path.is_symlink() or path.is_junction() for path in (package, *package.parents)),
                 f'官方稳定插件包缺失或包含链接：{artifact}')
        raw = package.read_bytes()
        _require(len(raw) == entry['sizeBytes'] and hashlib.sha256(raw).hexdigest() == entry['sha256'],
                 f'官方稳定插件包字节不符：{artifact}')
        files, contents, folded, total = [], {}, set(), 0
        with zipfile.ZipFile(io.BytesIO(raw)) as archive:
            infos = archive.infolist()
            _require(0 < len(infos) <= 512, f'预装插件文件数无效：{artifact}')
            for info in infos:
                name = _safe_package_entry(info.orig_filename, inner_plugin=True)
                total += info.file_size
                _require(name == info.orig_filename == info.filename and '\\' not in name and not info.is_dir()
                         and name.casefold() not in folded
                         and (info.external_attr >> 16) & 0o170000 in (0, 0o100000)
                         and 0 <= info.file_size <= MAX_SINGLE_ENTRY_BYTES and total <= MAX_PACKAGE_UNCOMPRESSED_BYTES
                         and (info.file_size == 0 or info.compress_size > 0 and info.file_size / info.compress_size <= MAX_COMPRESSION_RATIO),
                         f'预装插件条目无效：{artifact}/{name}')
                folded.add(name.casefold())
                data = archive.read(info)
                contents[name] = data
                files.append({'path': name, 'sizeBytes': len(data), 'sha256': hashlib.sha256(data).hexdigest()})
        _require(not any('/'.join(name.split('/')[:index]).casefold() in folded
                         for name in contents for index in range(1, len(name.split('/')))),
                 f'预装插件文件/目录冲突：{artifact}')
        _require('plugin.json' in contents, f'预装插件 manifest 缺失：{artifact}')
        plugin = json.loads(contents['plugin.json'])
        fields = ('name', 'artifactName', 'version', 'kind', 'apiVersion', 'minHostVersion')
        _require(plugin.get('schemaVersion') == 2 and all(plugin.get(field) == entry.get(field) for field in fields),
                 f'预装插件 manifest 与 catalog 不一致：{artifact}')
        resolved.append({**{field: entry[field] for field in fields}, 'packageSha256': entry['sha256'],
                         'packageSizeBytes': len(raw), 'files': sorted(files, key=lambda file: file['path'])})
        payload[artifact] = contents
    target = production / 'plugins'
    _require(not target.exists(), '生产 staging 已存在插件目录')
    for artifact, contents in payload.items():
        for name, data in contents.items():
            destination = target / artifact / name
            destination.parent.mkdir(parents=True, exist_ok=True)
            destination.write_bytes(data)
    return {'schemaVersion': 1, 'repository': REPOSITORY, 'plugins': resolved}


def write_manifest(path: Path, manifest: dict) -> None:
    path.write_text(json.dumps(manifest, ensure_ascii=False, sort_keys=True, indent=2) + '\n', encoding='utf-8')


if __name__ == '__main__':
    import argparse
    parser = argparse.ArgumentParser(allow_abbrev=False)
    for name in ('policy', 'plugins-root', 'output'):
        parser.add_argument('--' + name, type=Path, required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    write_manifest(args.output / 'BundledPlugins.json', stage(args.output, args.plugins_root, args.policy))
