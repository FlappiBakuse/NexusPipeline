"""Merge verified Host and maintenance publications into the application layout."""
import json
from pathlib import Path
import shutil

from binary_inputs import hash_file
from desktop_bundle import require_no_links, safe_name
from product_publish import inventory
from source_snapshot import write_json, external_directory, ordinary_path
from initial_extensions import collect_initial


def assemble_product(workspace, output, bundle_root=None, replace_application=False):
    workspace = ordinary_path(workspace).resolve()
    output = external_directory(Path(__file__).resolve().parent.parent, output)
    require_no_links(workspace)
    require_no_links(output)
    if workspace == output or workspace in output.parents or output in workspace.parents:
        raise ValueError('Assembly and publication workspaces must be disjoint')
    pointer = json.loads((workspace / 'publish-current.json').read_bytes())
    receipt = Path(pointer['receipt'])
    require_no_links(receipt)
    if workspace not in receipt.parents or hash_file(receipt) != pointer['sha256']:
        raise ValueError('Publication receipt is not bound to this workspace')
    published = json.loads(receipt.read_bytes())
    if published['status'] != 'PASS_PUBLISHED_NOT_ASSEMBLED':
        raise ValueError('Publication did not complete')
    sources = collect_inputs(workspace, published)
    initial, initial_receipt = collect_initial(bundle_root, published['profile']) if bundle_root is not None else ({}, None)
    expected = [dict(path=name, sizeBytes=path.stat().st_size, sha256=hash_file(path))
                for name, path in sorted(sources.items())]
    ownership = output / 'assembly.json'
    application = output
    previous = dict(files=[])
    if output.exists() and any(output.iterdir()):
        if not ownership.is_file():
            raise ValueError('Existing assembly has no matching ownership receipt')
        else:
            previous = json.loads(ownership.read_bytes())
            if (previous.get('format') != 'nexus.product.assembly' or previous.get('version') != 1 or
                    previous.get('publicationWorkspace') != str(workspace) or owned_inventory(application, previous['files']) != previous['files']):
                raise ValueError('Existing application has changed or unowned files')
            if previous['files'] != expected and not replace_application:
                raise ValueError('Preserve existing acceptance data before replacing the application')
    application.mkdir(parents=True, exist_ok=True)
    record = dict(format='nexus.product.assembly', version=1, status='ASSEMBLY_INCOMPLETE',
        profile=published['profile'], source=published['source'], publicationWorkspace=str(workspace),
        publishReceipt=pointer, initialExtensions=initial_receipt, toolSha256=hash_file(Path(__file__)),
        scope='Host, GUI launcher, maintenance runtime, desktop shell and declared default bundle. Runtime acceptance is separate.')
    known_files = expected + previous['files']
    known = {(item['path'], item['sizeBytes'], item['sha256']) for item in known_files}
    try:
        copy_application(application, sources, expected, previous['files'])
        if not (application / 'runtime/instance.json').exists():
            copy_initial(application, initial)
        record['status'] = 'PASS_APPLICATION_ASSEMBLED'
    finally:
        record['files'] = [item for item in owned_inventory(application, known_files)
                           if (item['path'], item['sizeBytes'], item['sha256']) in known]
        write_json(ownership, record)
    return dict(status='PASS_APPLICATION_ASSEMBLED', application=str(application), receipt=str(ownership))


def ordered_inventory(directory):
    return sorted(inventory(directory), key=lambda item: item['path'])


def owned_inventory(directory, records):
    paths = {}
    for record in records:
        name = safe_name(record['path'])
        parts = name.split('/')
        if parts[0] not in ('NexusPipeline.exe', 'NexusPipeline.update.exe', 'README.md', 'resources'):
            raise ValueError('Unknown public application ownership')
    for name in ('NexusPipeline.exe', 'NexusPipeline.update.exe', 'README.md', 'resources'):
        path = directory / name
        require_no_links(path)
        if not path.exists():
            continue
        if name == 'resources':
            if not path.is_dir():
                raise ValueError('Application resources changed shape')
            for child in path.rglob('*'):
                require_no_links(child)
                if child.is_file():
                    paths[child.relative_to(directory).as_posix()] = child
        elif path.is_file():
            paths[name] = path
        else:
            raise ValueError('Application entry changed shape')
    return [dict(path=name, sizeBytes=path.stat().st_size, sha256=hash_file(path)) for name, path in sorted(paths.items())]


def copy_application(application, sources, expected, previous):
    before = {item['path']: item for item in previous}
    after = {item['path']: item for item in expected}
    for item in expected:
        path = application / item['path']
        if path.exists() and item['path'] not in before:
            raise ValueError('New application destination already contains unowned bytes')
    for name, item in before.items():
        if after.get(name) != item:
            (application / name).unlink()
    def parents(names):
        return {'/'.join(name.split('/')[:index]) for name in names for index in range(1, len(name.split('/')))}
    for name in sorted(parents(before) - parents(after), key=lambda name: (name.count('/'), name), reverse=True):
        directory = application / name
        require_no_links(directory)
        directory.rmdir()
    for item in expected:
        destination = application / item['path']
        require_no_links(destination)
        destination.parent.mkdir(parents=True, exist_ok=True)
        if not destination.exists():
            shutil.copyfile(sources[item['path']], destination)
    if owned_inventory(application, expected) != expected:
        raise ValueError('Assembly changed while copying verified inputs')


def copy_initial(application, inputs):
    for name, value in inputs.items():
        destination = application / name
        require_no_links(destination)
        content = value if isinstance(value, bytes) else value.read_bytes()
        if destination.exists() and (not destination.is_file() or destination.read_bytes() != content):
            raise ValueError('Initial extension destination contains unowned bytes')
        destination.parent.mkdir(parents=True, exist_ok=True)
        if not destination.exists():
            destination.write_bytes(content)


def collect_inputs(workspace, published):
    sources = {}
    for project, records in published['published'].items():
        entries = {'Host': 'NexusPipeline.exe', 'Maintenance': 'NexusPipeline.update.exe',
                   'Launcher': 'resources/desktop/NexusPipeline.Launcher.exe'}
        if project not in entries:
            raise ValueError('Unknown product publication')
        directory = workspace / 'publish' / project.lower()
        if inventory(directory) != records:
            raise ValueError('Published component changed')
        for record in records:
            name = entries[project]
            if record['path'] != 'NexusPipeline.' + project + '.exe':
                raise ValueError('Product publication must contain its single-file entry')
            path = directory / record['path']
            if name in sources and hash_file(sources[name]) != record['sha256']:
                raise ValueError('Product component byte conflict: ' + name)
            sources[name] = path
    desktop = workspace / 'desktop'
    if hash_file(desktop / 'desktop-bundle.json') != published['desktopReceiptSha256']:
        raise ValueError('Desktop ownership receipt changed')
    bundle = json.loads((desktop / 'desktop-bundle.json').read_bytes())
    actual = inventory(desktop)
    expected = [dict(path=item['path'], sizeBytes=item['sizeBytes'], sha256=item['sha256']) for item in bundle['files']]
    if (bundle.get('status') != 'PASS' or sorted(expected, key=lambda item: item['path']) !=
            sorted((item for item in actual if item['path'] != 'desktop-bundle.json'), key=lambda item: item['path'])):
        raise ValueError('Desktop publication changed')
    for record in expected:
        name = 'resources/desktop/' + record['path']
        if name in sources:
            raise ValueError('Desktop runtime conflicts with a published product entry')
        sources[name] = desktop / record['path']
    for entry in ('NexusPipeline.exe', 'NexusPipeline.update.exe', 'resources/desktop/NexusPipeline.Launcher.exe',
                  'resources/desktop/NexusPipeline.Desktop.exe'):
        if entry not in sources:
            raise ValueError('Product entry is missing: ' + entry)
    source = published['source']
    source_manifest = Path(source['sourceManifestPath'])
    if hash_file(source_manifest) != source['sourceSnapshotHash']:
        raise ValueError('Published source manifest changed')
    record = next(item for item in json.loads(source_manifest.read_bytes())['files'] if item['path'] == 'tooling/application-readme.md')
    readme = Path(source['exportRoot']) / record['path']
    require_no_links(readme)
    if readme.stat().st_size != record['sizeBytes'] or hash_file(readme) != record['sha256']:
        raise ValueError('Application README differs from its bound source')
    sources['README.md'] = readme
    manifest = published['applicationManifest']
    path = ordinary_path(Path(manifest['path']))
    if workspace not in path.parents or hash_file(path) != manifest['sha256']:
        raise ValueError('Application manifest differs from its bound publication')
    sources['resources/payload-manifest.json'] = path
    return sources
