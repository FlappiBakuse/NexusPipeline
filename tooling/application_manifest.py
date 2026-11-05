"""Produce the current public application manifest from bound publication inputs."""
import hashlib
import json
from pathlib import Path
import re


HOST = 'NexusPipeline.exe'
HELPER = 'NexusPipeline.update.exe'
ENTRY = 'resources/desktop/NexusPipeline.Desktop.exe'
ASAR = 'resources/desktop/resources/app.asar'
MANIFEST = 'resources/payload-manifest.json'


def canonical(value):
    return json.dumps(value, ensure_ascii=True, sort_keys=True, separators=(',', ':')).encode('utf-8')


def kind(name):
    fixed = {HOST: 'host', HELPER: 'update-entry', 'README.md': 'readme', ENTRY: 'desktop-entry', ASAR: 'desktop-app',
        'resources/desktop/LICENSE': 'license', 'resources/desktop/LICENSES.chromium.html': 'license',
        'resources/desktop/version': 'runtime-manifest'}
    if name in fixed:
        return fixed[name]
    if name.startswith('resources/desktop/'):
        return 'desktop-runtime'
    raise ValueError('Path outside the public application: ' + name)


def create_manifest(root, source, profile, published, desktop, frontend):
    if profile not in ('test', 'production'):
        raise ValueError('Known publication profile required')
    commit, tree = source['sourceCommit'], source['sourceTree']
    if (commit is None) != (tree is None) or any(value is not None and re.fullmatch('[a-f0-9]{40}', value) is None for value in (commit, tree)):
        raise ValueError('Application source identity is invalid')
    if profile == 'production' and (source['sourceMode'] != 'git-commit' or commit is None):
        raise ValueError('Production application publication requires its exact clean Git source')
    source_file = root / 'tooling/application-readme.md'
    raw = Path(source['sourceManifestPath']).read_bytes()
    if hashlib.sha256(raw).hexdigest() != source['sourceSnapshotHash']:
        raise ValueError('Application source manifest changed')
    source_rows = json.loads(raw)['files']
    readme = next(item for item in source_rows if item['path'] == 'tooling/application-readme.md')
    if hashlib.sha256(source_file.read_bytes()).hexdigest() != readme['sha256']:
        raise ValueError('Application README changed from the frozen source')
    rows = []
    for project, name in (('Host', HOST), ('Maintenance', HELPER)):
        records = published[project]
        if len(records) != 1 or records[0]['path'] != 'NexusPipeline.' + project + '.exe':
            raise ValueError('Only single-file product publications belong to the application')
        rows.append(dict(path=name, sizeBytes=records[0]['sizeBytes'], sha256=records[0]['sha256'], kind=kind(name)))
    rows.append(dict(path='README.md', sizeBytes=readme['sizeBytes'], sha256=readme['sha256'], kind='readme'))
    for item in desktop['files']:
        name = 'resources/desktop/' + item['path']
        rows.append(dict(path=name, sizeBytes=item['sizeBytes'], sha256=item['sha256'], kind=kind(name)))
    rows.sort(key=lambda item: item['path'])
    names = [item['path'] for item in rows] + [MANIFEST]
    required = {HOST, HELPER, 'README.md', ENTRY, ASAR, 'resources/desktop/locales/en-US.pak', 'resources/desktop/locales/zh-CN.pak'}
    if not required <= set(names) or any(item['sizeBytes'] <= 0 for item in rows if item['path'] in required):
        raise ValueError('Application public entries and selected locales are incomplete')
    directories = {''} | {'/'.join(name.split('/')[:index]) for name in names for index in range(1, len(name.split('/')))}
    if (len(rows) > 4095 or len({item['path'].casefold() for item in rows}) != len(rows) or
            len(names) + len(directories) > 4096 or
            sum(item['sizeBytes'] for item in rows) > 512 * 1024 * 1024 or
            any(not 0 <= item['sizeBytes'] <= 256 * 1024 * 1024 for item in rows)):
        raise ValueError('Application exceeds the public file or byte bound')
    if rows[0]['sha256'] == rows[1]['sha256']:
        raise ValueError('Host and update entry must be distinct programs')
    desktop_identity = dict(entryPoint=ENTRY, appArchive=ASAR, electronVersion=desktop['runtime']['version'],
        electronArchiveSha256=desktop['runtime']['sha256'],
        packageLockSha256=hashlib.sha256((root / 'tooling/desktop-tools/package-lock.json').read_bytes()).hexdigest(),
        bootstrapProtocolVersion=1, **desktop['runtimeIdentity'])
    inputs = dict(sourceSnapshotHash=source['sourceSnapshotHash'], sourceSha=commit, sourceTreeSha=tree,
        hostVersion='0.18.0', sdkVersion='0.1.0', profile=profile, frontendHash=frontend, desktop=desktop_identity, files=rows)
    return dict(schemaVersion=2, product='NexusPipeline', productVersion='0.18.0', installationGeneration='g0180', rid='win-x64',
        sourceSha=commit, sourceTreeSha=tree, buildId=hashlib.sha256(canonical(inputs)).hexdigest(), frontendHash=frontend,
        desktop=desktop_identity, files=rows)
