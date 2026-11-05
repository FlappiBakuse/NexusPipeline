"""Package and verify this desktop source with the locked official ASAR producer."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import struct
import subprocess
import time
import uuid

from source_snapshot import external_directory, ordinary_path, write_json


MAXIMUM_BYTES = 2 * 1024 * 1024


def member_name(name):
    return type(name) is str and re.fullmatch(r'[A-Za-z0-9_-]+(?:\.[A-Za-z0-9_-]+)*', name) is not None


def source_entries(root):
    source = ordinary_path(root / 'apps/desktop')
    entries = {}
    for path in sorted(source.iterdir()):
        if path.name in ('README.md', 'runtime-lock.json', 'runtime-profile.json'):
            continue
        ordinary_path(path)
        if not path.is_file() or not member_name(path.name):
            raise ValueError('Desktop source must contain only declared regular app files')
        entries[path.name] = path.read_bytes()
    if not {'main.cjs', 'package.json'} <= entries.keys() or not 1 <= len(entries) <= 64 or sum(map(len, entries.values())) > MAXIMUM_BYTES:
        raise ValueError('Desktop app source exceeds its bound or lacks an entry')
    return entries


def source_records(entries):
    return [dict(path=name, sizeBytes=len(data), sha256=hashlib.sha256(data).hexdigest()) for name, data in sorted(entries.items())]


def verify_lock(root):
    source = root / 'tooling/desktop-tools'
    package = json.loads((source / 'package.json').read_bytes())
    lock = json.loads((source / 'package-lock.json').read_bytes())
    if (lock.get('lockfileVersion') != 3 or lock.get('name') != package['name'] or
            lock.get('version') != package['version'] or lock['packages']['']['dependencies'] != package['dependencies'] or
            lock['packages']['']['engines'] != package['engines']):
        raise ValueError('Desktop ASAR dependency lock differs from its native pins')


def execute(command, work, environment, run, commands):
    started = time.monotonic()
    log = run / (str(len(commands)) + '.log')
    with log.open('wb') as stream:
        completed = subprocess.run(command, cwd=work, env=environment, stdout=stream, stderr=subprocess.STDOUT, timeout=180)
    commands.append(dict(command=command, exitCode=completed.returncode, elapsedSeconds=time.monotonic() - started, log=str(log)))
    if completed.returncode:
        raise ValueError('Desktop ASAR producer failed: ' + str(log))


def prepare_work(root, work, entries):
    work = external_directory(root, work)
    owner = work / 'workspace.json'
    expected = dict(format='nexus.desktop.archive-work', version=1)
    if work.exists() and any(work.iterdir()) and (not owner.is_file() or json.loads(owner.read_bytes()) != expected):
        raise ValueError('Desktop archive workspace has unknown ownership')
    allowed = {'workspace.json', 'app', 'source-files.json', 'package.json', 'package-lock.json', 'node_modules', 'cache', 'runs'}
    if work.exists() and any(path.name not in allowed for path in work.iterdir()):
        raise ValueError('Desktop archive workspace contains unknown files')
    write_json(owner, expected)
    app = ordinary_path(work / 'app')
    manifest = ordinary_path(work / 'source-files.json')
    previous = json.loads(manifest.read_bytes()) if manifest.is_file() else []
    actual = []
    for path in sorted(app.iterdir()) if app.exists() else []:
        ordinary_path(path)
        if not path.is_file():
            raise ValueError('Desktop archive source work contains a non-file')
        actual.append(dict(path=path.name, sizeBytes=path.stat().st_size, sha256=hashlib.sha256(path.read_bytes()).hexdigest()))
    if actual != previous:
        raise ValueError('Desktop archive source work files changed or have unknown bytes')
    app.mkdir(exist_ok=True)
    for record in previous:
        ordinary_path(app / record['path'])
        if record['path'] not in entries:
            (app / record['path']).unlink()
    for name, data in entries.items():
        (app / name).write_bytes(data)
    write_json(manifest, source_records(entries))
    for name in ('package.json', 'package-lock.json'):
        target = ordinary_path(work / name)
        target.write_bytes((root / 'tooling/desktop-tools' / name).read_bytes())
    return work, app


def pack_application(root, work, cache):
    root = root.resolve(strict=True)
    entries = source_entries(root)
    verify_lock(root)
    work, app = prepare_work(root, work, entries)
    cache = external_directory(root, cache)
    temporary = ordinary_path(work / 'cache/tmp')
    temporary.mkdir(parents=True, exist_ok=True)
    environment = dict(os.environ, TEMP=str(temporary), TMP=str(temporary), npm_config_cache=str(cache))
    identity = hashlib.sha256((work / 'package.json').read_bytes() + (work / 'package-lock.json').read_bytes()).hexdigest()
    ordinary_path(work / 'node_modules')
    run = ordinary_path(work / 'runs') / (datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S') + '-' + uuid.uuid4().hex[:8])
    run.mkdir(parents=True)
    report = dict(status='RUNNING', dependencySha256=identity, source=source_records(entries), commands=[])
    try:
        npm = shutil.which('npm.cmd' if os.name == 'nt' else 'npm')
        node = shutil.which('node')
        if npm is None or node is None:
            raise ValueError('Node and npm are required by the pinned desktop archive producer')
        installed = ordinary_path(work / 'node_modules/@electron/asar/package.json')
        pinned = json.loads((work / 'package.json').read_bytes())['dependencies']['@electron/asar']
        execute([npm, 'ci', '--ignore-scripts', '--no-audit', '--no-fund', '--engine-strict'], work, environment, run, report['commands'])
        if json.loads(installed.read_bytes()).get('version') != pinned:
            raise ValueError('Installed ASAR producer differs from its locked version')
        archive = run / 'app.asar'
        execute([node, str(root / 'tooling/desktop_pack.mjs'), str(work / 'node_modules'), str(app), str(archive)],
                work, environment, run, report['commands'])
        verify_archive(root, archive)
        if source_entries(root) != entries:
            raise ValueError('Desktop source changed during ASAR packing')
        report.update(status='PASS', archive=dict(path=str(archive), sha256=hashlib.sha256(archive.read_bytes()).hexdigest()))
        return archive, dict(receipt=str(run / 'receipt.json'), source=report['source'], dependencySha256=identity)
    except Exception as error:
        report.update(status='FAIL', error=str(error))
        raise
    finally:
        write_json(run / 'receipt.json', report)


def read_archive(path):
    ordinary_path(path)
    if not 16 <= path.stat().st_size <= MAXIMUM_BYTES + 65536:
        raise ValueError('Desktop ASAR exceeds its source bound')
    raw = path.read_bytes()
    size, header_size, payload_size, text_size = struct.unpack_from('<IIII', raw)
    if (size != 4 or not 8 <= header_size <= 65536 or payload_size != header_size - 4 or
            8 + ((text_size + 3) // 4) * 4 != header_size or 8 + header_size > len(raw) or
            any(raw[16 + text_size:8 + header_size])):
        raise ValueError('Desktop ASAR header is invalid')
    def pairs(items):
        value = {}
        for key, item in items:
            if key in value:
                raise ValueError('Desktop ASAR contains duplicate fields')
            value[key] = item
        return value
    header = json.loads(raw[16:16 + text_size].decode('utf-8'), object_pairs_hook=pairs)
    if type(header) is not dict or set(header) != {'files'} or type(header['files']) is not dict or not 1 <= len(header['files']) <= 64:
        raise ValueError('Desktop ASAR source header is invalid')
    data = raw[8 + header_size:]
    result, ranges, folded = {}, set(), set()
    for name, value in header['files'].items():
        if (not member_name(name) or name.casefold() in folded or
                type(value) is not dict or not {'size', 'offset'} <= value.keys() or
                value.keys() - {'size', 'offset', 'integrity', 'executable'} or type(value['size']) is not int or
                type(value['offset']) is not str or re.fullmatch(r'[0-9]+', value['offset']) is None or
                ('executable' in value and type(value['executable']) is not bool)):
            raise ValueError('Desktop ASAR contains an unsupported member')
        offset, size = int(value['offset']), value['size']
        if not 0 <= offset <= len(data) or not 0 <= size <= len(data) - offset:
            raise ValueError('Desktop ASAR member exceeds its data bound')
        result[name] = data[offset:offset + size]
        verify_integrity(value.get('integrity'), result[name])
        ranges.add((offset, offset + size))
        folded.add(name.casefold())
    ordered = sorted(ranges)
    if not ordered or ordered[0][0] != 0 or ordered[-1][1] != len(data) or any(left[1] != right[0] for left, right in zip(ordered, ordered[1:])):
        raise ValueError('Desktop ASAR contains unowned or overlapping data')
    return result


def verify_integrity(value, data):
    if (type(value) is not dict or set(value) != {'algorithm', 'hash', 'blockSize', 'blocks'} or
            value['algorithm'] != 'SHA256' or type(value['blockSize']) is not int or not 1 <= value['blockSize'] <= 2 ** 32):
        raise ValueError('Desktop ASAR member lacks its SHA256 integrity record')
    blocks = [hashlib.sha256(data[index:index + value['blockSize']]).hexdigest() for index in range(0, len(data), value['blockSize'])]
    if value['hash'] != hashlib.sha256(data).hexdigest() or value['blocks'] != blocks:
        raise ValueError('Desktop ASAR member integrity differs from its bytes')


def verify_archive(root, path):
    if read_archive(path) != source_entries(root):
        raise ValueError('Desktop ASAR member bytes differ from this source')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, required=True)
    parser.add_argument('--archive', type=Path)
    parser.add_argument('--bundle', type=Path)
    parser.add_argument('--check-lock', action='store_true')
    args = parser.parse_args()
    if args.check_lock:
        verify_lock(args.root)
    elif args.bundle is not None:
        from desktop_bundle import read_desktop_bundle
        read_desktop_bundle(args.root, args.bundle)
    else:
        if args.archive is None:
            parser.error('--archive is required for verification')
        verify_archive(args.root, args.archive)


if __name__ == '__main__':
    main()
