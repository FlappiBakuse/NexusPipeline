"""Build the three Windows distribution assets from one verified application."""
import argparse
from datetime import datetime, timezone
import json
from pathlib import Path
import subprocess
import xml.etree.ElementTree as ET
import zipfile
import uuid

from binary_inputs import hash_file
from artifact_lock import artifact_lock
from build_inputs import python_inputs
from desktop_bundle import require_no_links
from application_manifest import MANIFEST, kind
from product_assembly import owned_inventory
from source_snapshot import write_json
import update_recipe
from installer_runtime import render_runtime
from installer_lifecycle import render_lifecycle


COMPILER_SHA256 = '0a8757031b33777e4c9cbffee40f11a5062b36d25cbe144c1db73b6102b80ad7'
CHINESE_SHA256 = '7d544b9bb1d142cfa11f2e5d3cc8abe2e55f8e066c5124e3772675aa236e1278'


def read_assembly(directory):
    require_no_links(directory)
    receipt = directory / 'assembly.json'
    assembly = json.loads(receipt.read_bytes())
    if assembly.get('status') != 'PASS_APPLICATION_ASSEMBLED' or owned_inventory(directory, assembly['files']) != assembly['files']:
        raise ValueError('Application assembly is incomplete or changed')
    source = assembly['source']
    manifest = json.loads(Path(source['sourceManifestPath']).read_bytes())
    original = next(item for item in manifest['files'] if item['path'] == 'Directory.Build.props')
    properties = Path(source['exportRoot']) / 'Directory.Build.props'
    if hash_file(properties) != original['sha256']:
        raise ValueError('Published version source changed')
    version = ET.fromstring(properties.read_bytes()).findtext('.//Version')
    if version != '0.18.0':
        raise ValueError('This product generation requires Host 0.18.0')
    return assembly, version, dict(path=str(receipt), sha256=hash_file(receipt))


def write_archive(path, files):
    with zipfile.ZipFile(path, 'x', compression=zipfile.ZIP_DEFLATED, compresslevel=6, allowZip64=False) as archive:
        for name, original in sorted(files.items()):
            info = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.create_system = 3
            info.external_attr = 0o100644 << 16
            if isinstance(original, bytes):
                archive.writestr(info, original)
            else:
                info.file_size = original.stat().st_size
                with archive.open(info, 'w') as destination, original.open('rb') as source:
                    import shutil
                    shutil.copyfileobj(source, destination)
    with zipfile.ZipFile(path) as archive:
        if set(archive.namelist()) != set(files) or archive.testzip() is not None or any(item.extract_version > 20 for item in archive.infolist()):
            raise ValueError('Distribution archive verification failed')


def prepare_output(root, output):
    if root == output or root in output.parents or output in root.parents:
        raise ValueError('Use an external candidate output directory')
    previous = None
    if output.exists():
        previous = json.loads((output / 'assets-receipt.json').read_bytes())
        if previous.get('status') != 'PASS_THREE_ASSETS_BUILT_NOT_QUALIFIED':
            raise ValueError('Previous candidate is incomplete')
        allowed = {'assets-receipt.json', 'installer.iss', 'installer.log', 'runs'}
        allowed.update(item['name'] for item in previous['assets'])
        if any(item.name not in allowed for item in output.iterdir()):
            raise ValueError('Candidate workspace contains unowned material')
        for item in previous['assets']:
            if hash_file(output / item['name']) != item['sha256']:
                raise ValueError('Previous candidate bytes changed')
    output.mkdir(parents=True, exist_ok=True)
    run = output / 'runs' / (datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S') + '-' + uuid.uuid4().hex[:8])
    run.mkdir(parents=True)
    if previous is not None:
        (run / 'previous-receipt.json').write_bytes((output / 'assets-receipt.json').read_bytes())
        for name in ('installer.iss', 'installer.log'):
            if (output / name).exists():
                (run / ('previous-' + name)).write_bytes((output / name).read_bytes())
    producers = python_inputs(root, ['product_assets.py']) | {root / name for name in (
        'tooling/installer.iss', 'tooling/installer-runtime.iss', 'tooling/installer-lifecycle.iss',
        'tooling/runtime-dependencies.json', 'tooling/ChineseSimplified.isl')}
    inputs = []
    for path in sorted(producers):
        relative = path.relative_to(root).as_posix()
        copy = run / 'source' / relative
        copy.parent.mkdir(parents=True, exist_ok=True)
        copy.write_bytes(path.read_bytes())
        inputs.append(dict(path=relative, sha256=hash_file(copy)))
    return run, inputs


def application_manifest(directory, assembly, version):
    pointer = assembly['publishReceipt']
    receipt = Path(pointer['receipt'])
    if hash_file(receipt) != pointer['sha256']:
        raise ValueError('Bound publication receipt changed')
    published = json.loads(receipt.read_bytes())
    manifest_bytes = (directory / MANIFEST).read_bytes()
    if hash_file(directory / MANIFEST) != published['applicationManifest']['sha256']:
        raise ValueError('Application manifest differs from its publication')
    manifest = json.loads(manifest_bytes)
    expected = [dict(**item, kind=kind(item['path'])) for item in assembly['files']
                if item['path'] != MANIFEST and not item['path'].startswith('plugins/')]
    if (manifest['schemaVersion'] != 2 or manifest['installationGeneration'] != 'g0180' or
            manifest['productVersion'] != version or manifest['files'] != expected):
        raise ValueError('Public manifest differs from the assembled application')
    return manifest, manifest_bytes


def transport(application, manifest, manifest_bytes, package_kind, bundled):
    recipe = update_recipe.create(manifest, manifest_bytes, package_kind, bundled_files=bundled)
    files = {item['path']: application / item['path'] for item in recipe['includedFiles'] + recipe['bundledFiles']}
    files.update(update_recipe.guides(recipe))
    files[update_recipe.RECIPE] = update_recipe.encode(recipe)
    size = sum(len(value) if isinstance(value, bytes) else value.stat().st_size for value in files.values())
    if len(files) > 4096 or size > 536870912:
        raise ValueError('Distribution transport exceeds the public admission bound')
    return files


def installer(root, application, assembly, version, output, compiler):
    require_no_links(compiler)
    if hash_file(compiler) != COMPILER_SHA256:
        raise ValueError('Installer compiler is not the pinned executable')
    chinese = root / 'tooling/ChineseSimplified.isl'
    if hash_file(chinese) != CHINESE_SHA256:
        raise ValueError('Installer translation changed')
    values = dict(VERSION=version, OUTPUT=str(output), CHINESE=str(chinese))
    if any(any(character in value for character in ('"', '\n', '\r', ';')) for value in (*values.values(), str(application))):
        raise ValueError('Installer input cannot be represented safely')
    rows = []
    for item in assembly['files']:
        relative = Path(item['path'])
        source = application / relative
        suffix = '' if relative.parent == Path('.') else '\\' + str(relative.parent)
        rows.append(f'Source: "{source}"; DestDir: "{{app}}{suffix}"; Flags: ignoreversion; Check: NewInstallation')
        if not item['path'].startswith('plugins/'):
            rows.append(f'Source: "{source}"; DestDir: "{{tmp}}\\nexus-transition-payload{suffix}"; '
                        'Flags: ignoreversion; Check: not NewInstallation')
    pointer = assembly['publishReceipt']
    receipt = Path(pointer['receipt'])
    if hash_file(receipt) != pointer['sha256']:
        raise ValueError('Installer publication receipt changed')
    publication = json.loads(receipt.read_bytes())
    worker = publication['installerWorker']
    worker_path = Path(worker['path'])
    require_no_links(worker_path)
    if any(character in str(worker_path) for character in ('"', '\n', '\r', ';')):
        raise ValueError('Installer worker path cannot be represented safely')
    if worker['file']['path'] != 'NexusPipeline.Installer.exe' or worker_path.stat().st_size != worker['file']['sizeBytes'] or hash_file(worker_path) != worker['file']['sha256']:
        raise ValueError('Private installer worker changed')
    rows.append(f'Source: "{worker_path}"; DestName: "nexus-installer-input.exe"; Flags: dontcopy')
    facade = publication['uninstallerEntry']
    facade_path = Path(facade['path'])
    require_no_links(facade_path)
    if (any(character in str(facade_path) for character in ('"', '\n', '\r', ';')) or
            facade['workerSha256'] != worker['file']['sha256'] or facade['file']['path'] != 'NexusPipeline.Uninstaller.exe' or
            facade_path.stat().st_size != facade['file']['sizeBytes'] or hash_file(facade_path) != facade['file']['sha256']):
        raise ValueError('Public uninstaller entry changed')
    rows.append(f'Source: "{facade_path}"; DestDir: "{{app}}"; DestName: "NexusPipeline.uninstall.exe"; Flags: ignoreversion; Check: NewInstallation')
    rows.append(f'Source: "{application / MANIFEST}"; DestName: "nexus-installer-target.json"; Flags: dontcopy')
    values['FILES'] = '\n'.join(rows)
    values['RUNTIME_CODE'] = render_runtime(root, application, worker_path, facade_path)
    values.update(render_lifecycle(root, application, {**assembly, 'installerWorker': worker, 'uninstallerEntry': facade}))
    script = (root / 'tooling/installer.iss').read_text(encoding='utf-8')
    for key, value in values.items():
        script = script.replace('@@' + key + '@@', value)
    script_path = output / 'installer.iss'
    script_path.write_text(script, encoding='utf-8-sig', newline='\n')
    result = subprocess.run([str(compiler), '/Qp', str(script_path)], capture_output=True, timeout=600)
    (output / 'installer.log').write_bytes(result.stdout + result.stderr)
    if result.returncode:
        raise ValueError('Installer compilation failed; inspect installer.log')


def build_assets(args):
    root = Path(__file__).resolve().parent.parent
    output = args.output.absolute()
    require_no_links(output)
    if root == output or root in output.parents or output in root.parents:
        raise ValueError('Use an external candidate output directory')
    with artifact_lock(output):
        return build_locked(args)


def build_locked(args):
    root = Path(__file__).resolve().parent.parent
    assembly, version, identity = read_assembly(args.assembly_root)
    output = args.output.absolute()
    require_no_links(output)
    run, inputs = prepare_output(root, output)
    report = dict(status='RUNNING', profile=assembly['profile'], assembly=identity, assets=[], producerInputs=inputs)
    try:
        application = args.assembly_root
        base = 'NexusPipeline-v' + version + '-win-x64-'
        manifest, manifest_bytes = application_manifest(application, assembly, version)
        bundled = [item for item in assembly['files'] if item['path'].startswith('plugins/')]
        write_archive(run / (base + 'Portable.zip'), transport(application, manifest, manifest_bytes, 'portable', bundled))
        write_archive(run / (base + 'Update.zip'), transport(application, manifest, manifest_bytes, 'update', []))
        report['updateManifestSha256'] = hash_file(application / MANIFEST)
        installer(root, application, assembly, version, run, args.compiler)
        if owned_inventory(application, assembly['files']) != assembly['files'] or hash_file(Path(identity['path'])) != identity['sha256']:
            raise ValueError('Candidate source application changed')
        if any(hash_file(root / item['path']) != item['sha256'] for item in inputs):
            raise ValueError('Asset producer source changed')
        report['assets'] = [dict(name=base + suffix, sizeBytes=(run / (base + suffix)).stat().st_size,
            sha256=hash_file(run / (base + suffix))) for suffix in ('Setup.exe', 'Portable.zip', 'Update.zip')]
        for item in report['assets']:
            (run / item['name']).replace(output / item['name'])
        report['status'] = 'PASS_THREE_ASSETS_BUILT_NOT_QUALIFIED'
    except Exception as error:
        report.update(status='FAIL', error=str(error))
        raise
    finally:
        write_json(run / 'assets-receipt.json', report)
    write_json(output / 'assets-receipt.json', report)
    return dict(status=report['status'], receipt=str(output / 'assets-receipt.json'))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('assembly-root', 'output', 'compiler'):
        parser.add_argument('--' + name, type=Path, required=True)
    print(json.dumps(build_assets(parser.parse_args()), indent=2))


if __name__ == '__main__':
    main()
