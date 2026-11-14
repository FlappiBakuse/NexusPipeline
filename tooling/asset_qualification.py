"""Exercise original Windows Setup and Portable assets in an isolated output root."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import time
import urllib.request
import zipfile
import uuid

from binary_inputs import hash_file
from desktop_bundle import require_no_links
from product_assembly import ordered_inventory, owned_inventory
from initial_extensions import records, from_assembly
from source_snapshot import write_json
import update_recipe


def execute(command, output, label, environment):
    with (output / (label + '.stdout.log')).open('wb') as stdout, (output / (label + '.stderr.log')).open('wb') as stderr:
        result = subprocess.run(command, stdout=stdout, stderr=stderr, env=environment,
            timeout=180, creationflags=subprocess.CREATE_NO_WINDOW)
    return dict(command=command, exitCode=result.returncode)


def probe(application, runtime, output, label, environment):
    host = application / 'NexusPipeline.exe'
    version = subprocess.run([str(host), '--version'], capture_output=True, env=environment,
        timeout=20, creationflags=subprocess.CREATE_NO_WINDOW)
    if version.returncode or version.stdout.strip() != b'0.18.0.0':
        raise ValueError('Standalone version invocation failed')
    log = output / (label + '.host.stdout.log')
    with log.open('wb') as stdout, (output / (label + '.host.stderr.log')).open('wb') as stderr:
        process = subprocess.Popen([str(host), 'serve', '--runtime-root', str(runtime), '--port', '0'],
            stdout=stdout, stderr=stderr, env=environment, creationflags=subprocess.CREATE_NO_WINDOW)
        try:
            deadline = time.monotonic() + 60
            ready = None
            while time.monotonic() < deadline:
                if process.poll() is not None:
                    raise ValueError('Standalone Host exited before readiness')
                lines = log.read_text(encoding='utf-8').splitlines()
                if lines:
                    ready = json.loads(lines[0])
                    break
                time.sleep(0.1)
            if ready is None:
                raise ValueError('Standalone Host readiness timed out')
            with urllib.request.urlopen(ready['address'] + '/control/v1/status', timeout=10) as response:
                status = json.load(response)
            with urllib.request.urlopen(ready['address'] + '/control/v1/extensions', timeout=10) as response:
                extensions = json.load(response)['items']
            if status['processId'] != process.pid or status['instanceId'] != ready['instanceId']:
                raise ValueError('Standalone Host identity mismatch')
            installed = [item for item in extensions if item['isInstalled']]
            if len(installed) != 2 or any(not item['activationId'] for item in installed):
                raise ValueError('Default bundled extensions did not activate')
            if (application / 'plugins').exists() or (application / 'package-info/initial-extensions').exists():
                raise ValueError('First-install inputs were not consumed')
            initialized = json.loads((runtime / 'initial-extensions.json').read_bytes())
            if not initialized['complete'] or initialized['instanceId'] != ready['instanceId']:
                raise ValueError('First-install completion was not bound to this instance')
            return dict(processId=process.pid, instanceId=ready['instanceId'], extensions=installed)
        finally:
            if process.poll() is None:
                process.terminate()
            process.wait(timeout=20)


def extract(portable, destination, application_records, initial_records):
    destination.mkdir()
    with zipfile.ZipFile(portable) as archive:
        metadata = {update_recipe.RECIPE, *update_recipe.GUIDES}
        expected = {record['path'] for record in application_records + initial_records} | metadata
        if len(archive.namelist()) != len(expected) or set(archive.namelist()) != expected:
            raise ValueError('Portable contains an unexpected or repeated member')
        for name in archive.namelist():
            path = destination / name
            if not path.resolve().is_relative_to(destination.resolve()):
                raise ValueError('Portable member escapes its destination')
        recipe = update_recipe.parse(archive.read(update_recipe.RECIPE))
        declared = recipe['includedFiles'] + recipe['bundledFiles']
        if recipe['packageKind'] != 'portable' or sorted(declared, key=lambda item: item['path']) != sorted(application_records + initial_records, key=lambda item: item['path']):
            raise ValueError('Portable recipe differs from the assembly')
        archive.extractall(destination)
    if owned_inventory(destination, application_records) != application_records:
        raise ValueError('Extracted Portable differs from the assembly')


def reject_missing_runtime(application, output, environment):
    empty = output / 'empty-dotnet'
    empty.mkdir()
    missing = dict(environment, DOTNET_ROOT=str(empty), DOTNET_ROOT_X64=str(empty), DOTNET_DISABLE_GUI_ERRORS='1')
    results = []
    for entry in ('NexusPipeline.exe', 'NexusPipeline.update.exe'):
        label = Path(entry).stem + '-missing-runtime'
        result = execute([str(application / entry), '--version'], output, label, missing)
        if result['exitCode'] & 0xffffffff != 0x80008083:
            raise ValueError('Framework-dependent executable did not reject the missing shared runtime')
        results.append(result)
    return results


def runtime_snapshot(runtime):
    directory = Path('\\\\?\\' + str(runtime)) if os.name == 'nt' else runtime
    objects = []
    for path in sorted([directory, *directory.rglob('*')]):
        require_no_links(path)
        status = path.stat()
        objects.append((path.relative_to(directory).as_posix(), status.st_dev, status.st_ino))
    return dict(files=ordered_inventory(directory), objects=objects)


def reinstall_setup(command, target, manager, records, preserved, owned, output, environment):
    runtime = target / 'runtime'
    before = runtime_snapshot(runtime)
    if before['files'] != preserved:
        raise ValueError('Retained runtime differs from the completed native uninstall')
    installed = execute(command + ['/LOG=' + str(output / 'setup-reinstall.log')], output, 'setup-reinstall', environment)
    if installed['exitCode'] or owned_inventory(target, records) != records:
        raise ValueError('Retained Setup reinstall differs from the assembly')
    if runtime_snapshot(runtime) != before:
        raise ValueError('Retained Setup reinstall changed runtime bytes or physical objects')
    result = execute([str(manager / 'nexus-installer-worker.exe'), 'installer-state', 'inspect', '--root', str(target)],
        output, 'reinstall-ownership', environment)
    current = json.loads((output / 'reinstall-ownership.stdout.log').read_bytes())
    if result['exitCode'] or current != owned:
        raise ValueError('Retained Setup reinstall changed installation or runtime identity')
    host = probe(target, runtime, output, 'reinstall', environment)
    if host['instanceId'] != owned['runtimeId']:
        raise ValueError('Reinstalled Host did not reopen the original runtime')
    after = runtime_snapshot(runtime)
    removed = execute([str(target / 'uninstall/unins000.exe'), '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
        '/LOG=' + str(output / 'reinstall-uninstall.log')], output, 'reinstall-uninstall', environment)
    if removed['exitCode'] or owned_inventory(target, records) or runtime_snapshot(runtime) != after:
        raise ValueError('Reinstalled native uninstall did not preserve runtime data')
    return dict(installation=installed, ownership=current, runtimeFilesPreserved=len(preserved),
        runtimePhysicalObjectsPreserved=len(before['objects']), host=host, uninstall=removed)


def qualify_setup(setup, output, records, environment, results, profile):
    import winreg
    key = r'Software\Microsoft\Windows\CurrentVersion\Uninstall\NexusPipeline.PerUser.0.18_is1'
    if profile == 'test':
        fixture = output / 'installer-context'
        fixture.mkdir()
        scope = uuid.uuid4().hex
        (fixture / '.nexus-installer-fixture').write_text(scope, encoding='ascii')
        environment = dict(environment, NEXUS_TEST_ROOT=str(fixture))
        key = 'Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\NexusPipeline.Tests.' + scope + '_is1'
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, key):
            raise ValueError('Existing installer registration must remain untouched')
    except FileNotFoundError:
        pass
    target = output / 'setup'
    target.mkdir()
    sentinel = target / 'preserve-existing.txt'
    sentinel.write_bytes(b'Existing files must prevent installation.\n')
    command = [str(setup), '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-',
        '/LANG=en', '/DIR=' + str(target), '/GROUP=NexusPipeline qualification', '/NOICONS']
    rejected = execute(command + ['/LOG=' + str(output / 'setup-reject.log')], output, 'setup-reject', environment)
    if rejected['exitCode'] == 0 or list(target.iterdir()) != [sentinel]:
        raise ValueError('Setup failed to reject a nonempty destination')
    if sentinel.read_bytes() != b'Existing files must prevent installation.\n':
        raise ValueError('Setup altered the existing destination')
    sentinel.unlink()
    results['setupReject'] = rejected
    installed = execute(command + ['/LOG=' + str(output / 'setup-install.log')], output, 'setup-install', environment)
    results['setupInstall'] = installed
    if installed['exitCode'] or owned_inventory(target, records) != records:
        raise ValueError('Setup installation differs from the assembly')
    manager = fixture / 'installer-manager' if profile == 'test' else Path(os.environ['LOCALAPPDATA']) / 'NexusPipeline.Generations/g0180/installer'
    inspect = execute([str(manager / 'nexus-installer-worker.exe'), 'installer-state', 'inspect', '--root', str(target)],
        output, 'setup-ownership', environment)
    results['setupOwnership'] = inspect
    owned = json.loads((output / 'setup-ownership.stdout.log').read_bytes())
    if inspect['exitCode'] or owned['kind'] != 'owned' or owned['productVersion'] != '0.18.0':
        raise ValueError('Actual Setup did not register its installation ownership')
    results['installedOwnership'] = owned
    results['setupHost'] = probe(target, target / 'runtime', output, 'setup', environment)
    marker = target / 'runtime/user-preservation.txt'
    marker.write_bytes(b'User data survives uninstall.\n')
    preserved = runtime_snapshot(target / 'runtime')['files']
    uninstalled = execute([str(target / 'uninstall/unins000.exe'), '/VERYSILENT', '/SUPPRESSMSGBOXES',
        '/NORESTART', '/LOG=' + str(output / 'setup-uninstall.log')], output, 'setup-uninstall', environment)
    results['setupUninstall'] = uninstalled
    if uninstalled['exitCode'] or owned_inventory(target, records):
        raise ValueError('Setup uninstall failed to remove its application')
    if runtime_snapshot(target / 'runtime')['files'] != preserved:
        raise ValueError('Setup uninstall changed runtime data')
    inspect = execute([str(manager / 'nexus-installer-worker.exe'), 'installer-state', 'inspect', '--root', str(target)],
        output, 'uninstall-ownership', environment)
    retained = json.loads((output / 'uninstall-ownership.stdout.log').read_bytes())
    if inspect['exitCode'] or retained['kind'] != 'retained' or retained['runtimeId'] != owned['runtimeId']:
        raise ValueError('Actual native uninstall did not retain its owned runtime identity')
    results['retainedOwnership'] = retained
    results['preservedRuntime'] = preserved
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, key):
            raise ValueError('Setup uninstall left its registration')
    except FileNotFoundError:
        pass
    results['retainedReinstall'] = reinstall_setup(command, target, manager, records, preserved, owned, output, environment)


def run(args):
    output = args.output.absolute()
    for path in (output, args.assets_receipt, args.assembly_receipt):
        require_no_links(path)
    if output.exists():
        raise ValueError('Qualification output must be a new isolated directory')
    assets = json.loads(args.assets_receipt.read_bytes())
    assembly = json.loads(args.assembly_receipt.read_bytes())
    if assets['status'] != 'PASS_THREE_ASSETS_BUILT_NOT_QUALIFIED' or hash_file(args.assembly_receipt) != assets['assembly']['sha256']:
        raise ValueError('Assets do not bind the specified assembly')
    paths = {}
    for item in assets['assets']:
        path = args.assets_receipt.parent / item['name']
        if path.stat().st_size != item['sizeBytes'] or hash_file(path) != item['sha256']:
            raise ValueError('Asset identity mismatch')
        paths[item['name'].rsplit('-', 1)[-1]] = path
    output.mkdir(parents=True)
    runtime_root = args.dotnet_root
    require_no_links(runtime_root)
    if not runtime_root.is_absolute() or not (runtime_root / 'dotnet.exe').is_file():
        raise ValueError('Specify the installed x64 .NET 10 root for runtime qualification')
    environment = dict(os.environ, TEMP=str(output), TMP=str(output), DOTNET_ROOT=str(runtime_root),
        DOTNET_ROOT_X64=str(runtime_root), DOTNET_MULTILEVEL_LOOKUP='0',
        DOTNET_BUNDLE_EXTRACT_BASE_DIR=str(output / 'bundle-extraction'))
    report = dict(status='RUNNING', assetsReceiptSha256=hash_file(args.assets_receipt),
        assemblyReceiptSha256=hash_file(args.assembly_receipt), results={})
    try:
        portable = output / 'portable'
        extract(paths['Portable.zip'], portable, assembly['files'], records(from_assembly(assembly)))
        report['results']['missingRuntime'] = reject_missing_runtime(portable, output, environment)
        report['results']['portableHost'] = probe(portable, portable / 'runtime', output, 'portable', environment)
        qualify_setup(paths['Setup.exe'], output, assembly['files'], environment, report['results'], assembly['profile'])
        report['status'] = 'PASS_SETUP_PORTABLE_RUNTIME'
        report['scope'] = 'Original assets, exact single-file payload, missing-runtime rejection, HTTP startup with installed .NET 10, default plugins, nonempty target rejection, actual Setup registration, retained-data reinstall with preserved runtime bytes and physical objects, original installation/runtime identity and actual native uninstall preserving exact runtime data. Desktop, data deletion and active current-generation transition qualification are separate.'
    except Exception as error:
        report.update(status='FAIL', error=str(error))
        raise
    finally:
        write_json(output / 'qualification.json', report)
    return dict(status=report['status'], receipt=str(output / 'qualification.json'))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('assets-receipt', 'assembly-receipt', 'output', 'dotnet-root'):
        parser.add_argument('--' + name, required=True, type=Path)
    print(json.dumps(run(parser.parse_args()), indent=2))


if __name__ == '__main__':
    main()
