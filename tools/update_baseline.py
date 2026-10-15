"""Freeze an official release payload before using any of its files as a delta base."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import tempfile
import urllib.request
import zipfile

try:
    from . import update_recipe as recipe
    from .application_payload import MANIFEST_PATH, MAX_BYTES, MAX_FILES, MAX_FILE_BYTES, safe_path
except ImportError:
    import update_recipe as recipe
    from application_payload import MANIFEST_PATH, MAX_BYTES, MAX_FILES, MAX_FILE_BYTES, safe_path


def read(package: Path, release: dict, *, target_version: str, application_reader=None) -> tuple[dict, list[dict]]:
    """release must be an independently fetched official GitHub API response."""
    tag = release.get('tag_name', '')
    recipe.require(type(tag) is str and tag.startswith('v') and recipe.version(tag[1:]), 'baseline tag')
    recipe.require(recipe.version_key('0.17.3') <= recipe.version_key(tag[1:]) < recipe.version_key(target_version), 'baseline route')
    recipe.require(release.get('draft') is False and release.get('published_at') is not None
                   and type(release.get('id')) is int and release['id'] > 0, 'baseline unpublished')
    expected = f'NexusPipeline-{tag}-win-x64' + ('.zip' if tag == 'v0.17.3' else '-Portable.zip')
    assets = [asset for asset in release.get('assets', []) if asset.get('name') == expected]
    recipe.require(len(assets) == 1, 'baseline asset missing/duplicate')
    asset = assets[0]
    digest = asset.get('digest', '')
    recipe.require(type(asset.get('id')) is int and asset['id'] > 0 and asset.get('state') == 'uploaded'
                   and type(asset.get('size')) is int and 0 < asset['size'] <= MAX_BYTES
                   and type(digest) is str and digest.startswith('sha256:') and recipe.digest(digest[7:])
                   and asset.get('browser_download_url') == f'https://github.com/FlappiBakuse/NexusPipeline/releases/download/{tag}/{expected}', 'baseline asset identity')
    recipe.require(package.is_file() and not package.is_symlink() and package.stat().st_size == asset['size'], 'baseline package size')
    with package.open('rb') as stream:
        recipe.require(hashlib.file_digest(stream, 'sha256').hexdigest() == digest[7:], 'baseline package digest')
    with zipfile.ZipFile(package) as archive:
        infos = archive.infolist()
        recipe.require(len(infos) <= MAX_FILES + 3 and len({info.filename.casefold() for info in infos}) == len(infos), 'baseline entry count/collision')
        total = 0
        for info in infos:
            safe_path(info.filename)
            recipe.require(not info.is_dir() and (info.external_attr >> 16) & 0o170000 in (0, 0o100000)
                           and info.file_size <= MAX_FILE_BYTES
                           and (not info.file_size or info.compress_size > 0 and info.file_size / info.compress_size <= 250), 'baseline entry')
            total += info.file_size
        recipe.require(total <= MAX_BYTES + 3 * recipe.MAX_METADATA, 'baseline budget')
        manifest_info = archive.getinfo(MANIFEST_PATH)
        recipe.require(0 < manifest_info.file_size <= recipe.MAX_METADATA, 'baseline manifest size')
        raw = archive.read(manifest_info)
        manifest = json.loads(raw)
        recipe.require(type(manifest.get('schemaVersion')) is int and manifest['schemaVersion'] == (1 if tag == 'v0.17.3' else 2)
                       and manifest.get('product') == 'NexusPipeline' and manifest.get('productVersion') == tag[1:]
                       and manifest.get('rid') == 'win-x64' and manifest.get('installationGeneration') == 'g0170', 'baseline payload identity')
        files = recipe.file_map([{key: file[key] for key in ('path', 'sizeBytes', 'sha256')} for file in manifest['files']])
        files[MANIFEST_PATH] = {'path': MANIFEST_PATH, 'sizeBytes': len(raw), 'sha256': hashlib.sha256(raw).hexdigest()}
        if tag != 'v0.17.3':
            transport = recipe.parse(archive.read(recipe.RECIPE_PATH))
            recipe.require(transport['packageKind'] == 'portable' and transport['target']['version'] == tag[1:], 'baseline recipe identity')
            recipe.verify(package, transport)
        else:
            recipe.require(all(info.filename in files or info.filename.startswith(('plugins/EmulatorSupport/', 'plugins/LiveScreenshot/')) for info in infos), 'baseline unknown file')
        with tempfile.TemporaryDirectory(prefix='nxp-baseline-') as temporary:
            directory = Path(temporary)
            for name, file in files.items():
                info = archive.getinfo(name)
                recipe.require(info.file_size == file['sizeBytes'], 'baseline file size')
                data = archive.read(info)
                recipe.require(hashlib.sha256(data).hexdigest() == file['sha256'], 'baseline file digest')
                destination = directory / name
                destination.parent.mkdir(parents=True, exist_ok=True)
                destination.write_bytes(data)
            if application_reader is None:
                try:
                    from .application_reader import read as application_reader
                except ImportError:
                    from application_reader import read as application_reader
            identity = application_reader(directory, tag[1:])
            recipe.require(identity['buildId'] == manifest['buildId'] and identity['frontendHash'] == manifest['frontendHash'], 'baseline embedded identity')
    return ({'releaseTag': tag, 'releaseId': release['id'], 'assetId': asset['id'], 'assetSha256': digest[7:],
             'payloadManifestSha256': files[MANIFEST_PATH]['sha256']}, sorted(files.values(), key=lambda file: file['path']))


def release_assets(api: str, release_id: int) -> list[dict]:
    recipe.require(type(release_id) is int and release_id > 0, 'release identity')
    assets, ids, names = [], set(), set()
    for page in range(1, 6):
        request = urllib.request.Request(f'{api}/{release_id}/assets?per_page=100&page={page}',
            headers={'Accept': 'application/vnd.github+json', 'User-Agent': 'NexusPipeline-build'})
        with urllib.request.urlopen(request, timeout=60) as response:
            raw = response.read(8 * 1024 * 1024 + 1)
        recipe.require(len(raw) <= 8 * 1024 * 1024, 'asset list budget')
        entries = json.loads(raw)
        recipe.require(type(entries) is list and len(entries) <= 100, 'asset list')
        for asset in entries:
            recipe.require(type(asset) is dict and type(asset.get('id')) is int and asset['id'] > 0
                and type(asset.get('name')) is str and asset['name'] and asset['id'] not in ids
                and asset['name'] not in names, 'asset identity collision')
            ids.add(asset['id']); names.add(asset['name']); assets.append(asset)
        recipe.require(len(assets) <= 400, 'asset count budget')
        if len(entries) < 100:
            return assets
    raise ValueError('Update recipe: asset pagination budget')


def acquire(directory: Path, target_version: str) -> tuple[dict, list[dict]] | None:
    recipe.require(recipe.version(target_version), 'target version')
    directory.mkdir(parents=True, exist_ok=True)
    api = 'https://api.github.com/repos/FlappiBakuse/NexusPipeline/releases'
    candidates = []
    for page in range(1, 11):
        request = urllib.request.Request(f'{api}?per_page=100&page={page}', headers={'Accept': 'application/vnd.github+json', 'User-Agent': 'NexusPipeline-build'})
        with urllib.request.urlopen(request, timeout=60) as response:
            raw = response.read(8 * 1024 * 1024 + 1)
        recipe.require(len(raw) <= 8 * 1024 * 1024, 'release list budget')
        releases = json.loads(raw)
        recipe.require(type(releases) is list and len(releases) <= 100, 'release list')
        for release in releases:
            tag = release.get('tag_name', '')
            if not isinstance(tag, str) or not tag.startswith('v') or not recipe.version(tag[1:]):
                continue
            version = tag[1:]
            if (release.get('draft') is False and release.get('published_at') is not None
                    and recipe.version_key('0.17.3') <= recipe.version_key(version) < recipe.version_key(target_version)
                    and bool(version.startswith('0.') or '-' in version) == bool(target_version.startswith('0.') or '-' in target_version)
                    and release.get('prerelease') == bool(version.startswith('0.') or '-' in version)):
                candidates.append(release)
        if len(releases) < 100:
            break
    else:
        raise ValueError('Update recipe: release pagination budget')
    if not candidates:
        return None
    release = max(candidates, key=lambda item: recipe.version_key(item['tag_name'][1:]))
    release = {**release, 'assets': release_assets(api, release.get('id'))}
    tag = release['tag_name']
    name = f'NexusPipeline-{tag}-win-x64' + ('.zip' if tag == 'v0.17.3' else '-Portable.zip')
    assets = [asset for asset in release['assets'] if asset.get('name') == name]
    recipe.require(len(assets) == 1, 'baseline asset missing/duplicate')
    asset = assets[0]
    url = f'https://github.com/FlappiBakuse/NexusPipeline/releases/download/{tag}/{name}'
    recipe.require(asset.get('browser_download_url') == url and type(asset.get('size')) is int and 0 < asset['size'] <= MAX_BYTES, 'baseline download identity')
    package = directory / name
    if not package.exists():
        with urllib.request.urlopen(urllib.request.Request(url, headers={'User-Agent': 'NexusPipeline-build'}), timeout=60) as response, package.open('xb') as destination:
            received = 0
            while block := response.read(1024 * 1024):
                received += len(block)
                recipe.require(received <= asset['size'], 'baseline download budget')
                destination.write(block)
    baseline = read(package, release, target_version=target_version)
    receipt = directory / 'verified-baseline.json'
    receipt.write_text(json.dumps({'identity': baseline[0], 'files': baseline[1]}, sort_keys=True) + '\n', encoding='utf-8')
    return baseline
