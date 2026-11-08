"""Assemble the selected, pinned Electron runtime and this source's desktop shell."""
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import stat
import zipfile

from source_snapshot import write_json
from desktop_archive import pack_application, source_entries, source_records, verify_archive, verify_lock
from desktop_branding import brand_executable
from desktop_runtime import read_profile, selected_member, runtime_identity


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def safe_name(name):
    value = PurePosixPath(name)
    if not name or not re.fullmatch(r'[A-Za-z0-9_./-]+', name) or value.is_absolute() or value.as_posix() != name:
        raise ValueError('Invalid desktop archive path')
    for part in value.parts:
        if part in ('.', '..') or part.endswith(('.', ' ')) or part.split('.')[0].upper() in {
            'CON', 'PRN', 'AUX', 'NUL', *(f'COM{i}' for i in range(1, 10)), *(f'LPT{i}' for i in range(1, 10))}:
            raise ValueError('Invalid desktop archive component')
    return name


def require_no_links(path):
    for item in (path, *path.parents):
        if item.is_symlink() or item.is_junction():
            raise ValueError('Desktop paths cannot traverse links')


def bundle_desktop(root, archive, output):
    output = output.absolute()
    archive = archive.absolute()
    require_no_links(archive)
    require_no_links(output)
    output = output.resolve()
    root = root.resolve(strict=True)
    if output == root or output in root.parents or root in output.parents:
        raise ValueError('Desktop output must be outside and disjoint from the source tree')
    if output in archive.parents:
        raise ValueError('Runtime input cannot be part of the output')
    source = root / 'apps/desktop'
    lock = json.loads((source / 'runtime-lock.json').read_text(encoding='utf-8'))
    profile = read_profile(root)
    if archive.stat().st_size != lock['sizeBytes'] or sha(archive) != lock['sha256']:
        raise ValueError('Electron archive does not match its pinned runtime')
    files = {}
    with zipfile.ZipFile(archive) as package:
        entries = [entry for entry in package.infolist() if not entry.is_dir()]
        if not entries or len(entries) > 512 or sum(entry.file_size for entry in entries) > 1073741824:
            raise ValueError('Electron archive exceeds bundle bounds')
        names = [safe_name(entry.filename) for entry in entries]
        if len(set(name.casefold() for name in names)) != len(names):
            raise ValueError('Duplicate desktop archive paths')
        for entry, name in zip(entries, names):
            kind = stat.S_IFMT(entry.external_attr >> 16)
            if kind not in (0, stat.S_IFREG):
                raise ValueError('Only regular runtime files may be bundled')
            if not selected_member(name, profile):
                continue
            target = 'NexusPipeline.Desktop.exe' if name == 'electron.exe' else name
            files[target] = (package.read(entry), 'electron-runtime')
    app, application = pack_application(root, output.parent / 'desktop-work', output.parent / 'cache/npm')
    if any(name == 'resources/app.asar' or name.startswith(('resources/app/', 'resources/app.asar.unpacked/')) for name in files):
        raise ValueError('Runtime archive contains product app files')
    files['resources/app.asar'] = (app.read_bytes(), 'desktop-source')
    if 'NexusPipeline.Desktop.exe' not in files:
        raise ValueError('Desktop entry is missing')
    branded, branding = brand_executable(files['NexusPipeline.Desktop.exe'][0], (source / 'nexuspipeline.ico').read_bytes(), output.parent / 'desktop-branding')
    files['NexusPipeline.Desktop.exe'] = (branded, 'electron-runtime')
    expected = {name: {'path': name, 'sha256': hashlib.sha256(data).hexdigest(), 'sizeBytes': len(data), 'owner': owner}
                for name, (data, owner) in files.items()}
    runtime = runtime_identity(root, lock, expected.values())
    receipt_path = output / 'desktop-bundle.json'
    previous = {}
    if output.exists() and any(output.iterdir()):
        if not receipt_path.is_file():
            raise ValueError('Existing output has no bundle ownership receipt')
        saved = json.loads(receipt_path.read_text(encoding='utf-8'))
        if saved.get('format') != 'nexus.desktop.bundle' or saved.get('version') != 1:
            raise ValueError('Unknown desktop output format')
        previous = {safe_name(item['path']): item for item in saved['files']}
        actual = {path.relative_to(output).as_posix() for path in output.rglob('*') if path.is_file()}
        if actual != set(previous) | {'desktop-bundle.json'}:
            raise ValueError('Unknown files in desktop output')
        for name, record in previous.items():
            path = output / name
            require_no_links(path)
            if path.stat().st_size != record['sizeBytes'] or sha(path) != record['sha256']:
                raise ValueError('Changed bytes in desktop output')
    output.mkdir(parents=True, exist_ok=True)
    for name in previous.keys() - expected.keys():
        (output / name).unlink()
    for name, (data, _owner) in files.items():
        path = output / name
        require_no_links(path)
        path.parent.mkdir(parents=True, exist_ok=True)
        if not path.exists() or sha(path) != expected[name]['sha256']:
            path.write_bytes(data)
    write_json(receipt_path, {'format': 'nexus.desktop.bundle', 'version': 1, 'status': 'PASS',
        'runtime': lock, 'runtimeIdentity': runtime, 'branding': branding, 'application': application, 'files': [expected[name] for name in sorted(expected)],
        'scope': 'Pinned Electron shell loading the same embedded Host Web assets as browser mode'})
    return {'status': 'PASS', 'output': str(output), 'files': len(files), 'runtimeVersion': lock['version']}


def read_desktop_bundle(root, directory):
    if not directory.is_absolute() or not directory.is_dir():
        raise ValueError('Desktop bundle must be an explicit existing absolute directory')
    require_no_links(directory)
    directory = directory.resolve(strict=True)
    manifest = directory / 'desktop-bundle.json'
    require_no_links(manifest)
    saved = json.loads(manifest.read_bytes())
    lock = json.loads((root / 'apps/desktop/runtime-lock.json').read_bytes())
    if (saved.get('format') != 'nexus.desktop.bundle' or saved.get('version') != 1 or
            saved.get('status') != 'PASS' or saved.get('runtime') != lock):
        raise ValueError('Desktop bundle must identify the current pinned runtime')
    branding = saved.get('branding', {})
    if (branding.get('iconSha256') != sha(root / 'apps/desktop/nexuspipeline.ico') or
            branding.get('producerSha256') != sha(root / 'tooling/desktop_branding.py') or
            branding.get('executableSha256') != sha(directory / 'NexusPipeline.Desktop.exe')):
        raise ValueError('Desktop branding differs from its product inputs')
    records = saved.get('files')
    if not isinstance(records, list) or not 1 <= len(records) <= 512:
        raise ValueError('Desktop bundle inventory is invalid')
    names = [safe_name(record['path']) for record in records]
    if len(set(name.casefold() for name in names)) != len(names) or sum(item['sizeBytes'] for item in records) > 1073741824:
        raise ValueError('Desktop bundle exceeds its complete inventory bound')
    actual = set()
    for path in directory.rglob('*'):
        require_no_links(path)
        if path.is_file():
            actual.add(path.relative_to(directory).as_posix())
    if actual != set(names) | {'desktop-bundle.json'}:
        raise ValueError('Desktop bundle has unknown or missing files')
    owned = []
    for record in records:
        path = directory / record['path']
        if (record.get('owner') not in ('electron-runtime', 'desktop-source') or
                path.stat().st_size != record['sizeBytes'] or sha(path) != record['sha256']):
            raise ValueError('Desktop bundle bytes differ from their ownership manifest')
        if record['owner'] == 'desktop-source':
            owned.append(record['path'])
    if owned != ['resources/app.asar'] or 'NexusPipeline.Desktop.exe' not in names:
        raise ValueError('Desktop bundle lacks this source archive or the Electron entry')
    if saved.get('runtimeIdentity') != runtime_identity(root, lock, records):
        raise ValueError('Desktop runtime profile identity differs from this source')
    verify_lock(root)
    verify_archive(root, directory / 'resources/app.asar')
    if saved.get('application', {}).get('source') != source_records(source_entries(root)):
        raise ValueError('Desktop source inventory differs from its archive receipt')
    return dict(root=str(directory), manifestSha256=sha(manifest), runtime=lock, files=records)
