"""Build local acceptance software from raw working-tree bytes without commits or publication."""
from __future__ import annotations
import argparse
import hashlib
import importlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time
import urllib.request
import zipfile
import zlib

from build_source import source_entries, git, snapshot


def digest(path: Path) -> str:
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def raw_tree(entries, objects: Path) -> str:
    objects.mkdir(parents=True, exist_ok=True)
    def write(kind, data):
        raw = f'{kind} {len(data)}\0'.encode() + data
        sha = hashlib.sha1(raw).hexdigest()
        path = objects / sha[:2] / sha[2:]
        path.parent.mkdir(exist_ok=True)
        if not path.exists():
            path.write_bytes(zlib.compress(raw))
        elif zlib.decompress(path.read_bytes()) != raw:
            raise ValueError('Independent object cache is corrupt')
        return bytes.fromhex(sha)
    tree = {}
    for name, mode, raw in entries:
        current = tree
        parts = name.split('/')
        for part in parts[:-1]:
            current = current.setdefault(part, {})
        current[parts[-1]] = (mode, write('blob', raw))
    def encode(current):
        data = b''
        for name, value in sorted(current.items(), key=lambda item: (item[0] + ('/' if isinstance(item[1], dict) else '')).encode()):
            data += (f'40000 {name}\0'.encode() + encode(value)) if isinstance(value, dict) else f'{value[0]} {name}\0'.encode() + value[1]
        return write('tree', data)
    return encode(tree).hex()


def freeze(root: Path, target: Path, manifest_path: Path, objects: Path) -> dict:
    entries = source_entries(root)
    manifest = [{'path': name, 'mode': mode, 'sizeBytes': len(raw), 'sha256': hashlib.sha256(raw).hexdigest()} for name, mode, raw in entries]
    if target.exists():
        previous = json.loads(manifest_path.read_bytes())['files']
        for file in previous:
            path = target / file['path']
            if not path.is_file() or digest(path) != file['sha256']:
                raise ValueError('Frozen source changed outside the local builder: ' + file['path'])
        current = {item[0] for item in entries}
        for file in previous:
            if file['path'] not in current:
                (target / file['path']).unlink()
        for name, _, raw in entries:
            path = target / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(raw)
    else:
        snapshot(root, target, entries)
    for file in manifest:
        if digest(target / file['path']) != file['sha256']:
            raise ValueError('Raw snapshot mismatch')
    raw_manifest = json.dumps(manifest, ensure_ascii=False, sort_keys=True, separators=(',', ':')).encode()
    identity = {'baseSourceCommit': git(root, 'rev-parse', 'HEAD'), 'sourceKind': 'working-tree',
                'sourceTreeSha': raw_tree(entries, objects), 'sourceManifestSha256': hashlib.sha256(raw_manifest).hexdigest(), 'files': manifest}
    difference = subprocess.check_output(['git', 'diff', '--binary', 'HEAD'], cwd=root)
    diff_path = manifest_path.with_suffix('.diff')
    diff_path.write_bytes(difference)
    identity.update(worktreeDiffPath=str(diff_path), worktreeDiffSha256=hashlib.sha256(difference).hexdigest(),
        gitIndexSha256=hashlib.sha256(subprocess.check_output(['git', 'ls-files', '--stage', '-z'], cwd=root)).hexdigest(),
        gitRefsSha256=hashlib.sha256(subprocess.check_output(['git', 'show-ref'], cwd=root)).hexdigest())
    manifest_path.write_text(json.dumps(identity, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    return identity


def resolve_compiler(cache: Path, supplied: Path | None) -> Path:
    from host_installer import INNO_COMPILER_SHA256, INNO_DISTRIBUTION_SHA256
    if supplied is not None:
        return supplied.resolve()
    directory = cache / 'inno'
    directory.mkdir(parents=True, exist_ok=True)
    executable = directory / 'compiler/ISCC.exe'
    if executable.exists():
        if digest(executable) != INNO_COMPILER_SHA256:
            raise ValueError('Inno compiler digest mismatch')
        return executable
    distribution = directory / 'innosetup-6.7.3.exe'
    if not distribution.exists():
        staging = distribution.with_suffix('.download')
        urllib.request.urlretrieve('https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe', staging)
        if digest(staging) != INNO_DISTRIBUTION_SHA256:
            raise ValueError('Inno distribution digest mismatch')
        staging.rename(distribution)
    if digest(distribution) != INNO_DISTRIBUTION_SHA256:
        raise ValueError('Inno distribution digest mismatch')
    script = """$ErrorActionPreference='Stop'
$p=Start-Process -FilePath $args[0] -ArgumentList @('/PORTABLE=1',('/DIR="'+$args[1]+'"'),'/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -WindowStyle Hidden -Wait -PassThru
exit $p.ExitCode
"""
    installer_script = directory / 'extract.ps1'
    with installer_script.open('x', encoding='utf-8-sig') as stream:
        stream.write(script)
    try:
        subprocess.run(['powershell.exe', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', str(installer_script), str(distribution), str(directory/'compiler')], check=True)
    finally:
        installer_script.unlink()
    if digest(executable) != INNO_COMPILER_SHA256:
        raise ValueError('Inno compiler digest mismatch')
    return executable


def build(host: Path, plugins: Path, output: Path, compiler: Path | None, cache: Path) -> dict:
    output = output.resolve()
    if output.is_relative_to(host) or output.is_relative_to(plugins) or output in (host.parent, plugins.parent) or output.is_symlink() or output.is_junction():
        raise ValueError('Local acceptance output must be external to the repositories')
    output.mkdir(parents=True, exist_ok=True)
    if cache.resolve().is_relative_to(host) or cache.resolve().is_relative_to(plugins):
        raise ValueError('Local acceptance cache must be external to the repositories')
    marker = output / 'local-build-owner.json'
    if not marker.exists() and any(output.iterdir()):
        raise ValueError('Unknown nonempty local build output')
    if marker.exists() and json.loads(marker.read_bytes()) != {'producer': 'local_acceptance.py', 'host': str(host), 'plugins': str(plugins)}:
        raise ValueError('Unknown local build owner')
    if (output / 'acceptance').exists():
        raise ValueError('Existing acceptance software must be preserved before rebuilding')
    marker.write_text(json.dumps({'producer': 'local_acceptance.py', 'host': str(host), 'plugins': str(plugins)}), encoding='utf-8')
    cache.mkdir(parents=True, exist_ok=True)
    temporary = output / 'tmp'
    temporary.mkdir(exist_ok=True)
    os.environ.update(TEMP=str(temporary), TMP=str(temporary), NUGET_PACKAGES=str(cache/'nuget'),
        NUGET_HTTP_CACHE_PATH=str(cache/'nuget-http'), NUGET_PLUGINS_CACHE_PATH=str(cache/'nuget-plugins'),
        npm_config_cache=str(cache/'npm'), DOTNET_CLI_HOME=str(cache/'dotnet'), DOTNET_ADD_GLOBAL_TO_PATH='false',
        DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE='true',
        ELECTRON_SKIP_BINARY_DOWNLOAD='1', PYTHONDONTWRITEBYTECODE='1', PYTHONUTF8='1')
    frozen = output / 'source'
    frozen.mkdir(exist_ok=True)
    source = frozen / 'Host'
    partner = frozen / 'Plugins'
    host_input = freeze(host, source, output/'host-source.json', output/'objects/host')
    plugin_input = freeze(plugins, partner, output/'plugins-source.json', output/'objects/plugins')
    receipt = {'schemaVersion': 1, 'mode': 'local-acceptance', 'sourceKind': 'working-tree', 'producer': 'local',
               'publishable': False, 'humanAcceptance': 'PENDING', 'status': 'FAIL', 'host': host_input,
               'plugins': plugin_input, 'steps': [], 'startedAt': time.time()}
    receipt_path = output/'local-acceptance-receipt.json'
    logs = output/'logs'
    logs.mkdir(exist_ok=True)
    attempt = len(list(logs.glob('attempt-*.json'))) + 1
    def command(label, arguments, cwd=source):
        started = time.monotonic()
        log = logs/f'{attempt:02d}-{len(receipt["steps"]):02d}.log'
        print(label, flush=True)
        with log.open('w', encoding='utf-8') as stream:
            child = subprocess.Popen([str(item) for item in arguments], cwd=cwd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, encoding='utf-8', errors='replace')
            for line in child.stdout:
                stream.write(line)
                print(line, end='', flush=True)
            code = child.wait()
        receipt['steps'].append({'label': label, 'command': [str(item) for item in arguments], 'exitCode': code, 'elapsedSeconds': time.monotonic()-started, 'log': str(log)})
        if code:
            raise subprocess.CalledProcessError(code, arguments)
    try:
        for name in ('application-inputs', 'production', 'installer-helper', 'installer', 'plugin-packages'):
            path = output/name
            if path.exists():
                if path.is_symlink() or path.is_junction():
                    raise ValueError('Linked build output')
                shutil.rmtree(path)
        sys.path.insert(0, str(source/'tools'))
        application_build = importlib.import_module('application_build')
        payload = importlib.import_module('application_payload')
        release = importlib.import_module('host_release')
        installer = importlib.import_module('host_installer')
        pe = importlib.import_module('pe_manifest')
        compiler = resolve_compiler(cache, compiler)
        npm = 'npm.cmd'
        command('Host frontend dependencies', [npm, 'ci', '--no-audit', '--no-fund'], source/'frontend')
        command('Host frontend typecheck', [npm, 'run', 'typecheck'], source/'frontend')
        command('Host frontend build', [npm, 'run', 'build'], source/'frontend')
        runtime = json.loads((source/'desktop/electron-runtime.json').read_bytes())
        archive = cache/runtime['archive']
        if not archive.exists():
            staging = archive.with_suffix('.download')
            urllib.request.urlretrieve(runtime['url'], staging)
            if digest(staging) != runtime['archiveSha256']:
                raise ValueError('Electron archive digest mismatch')
            staging.rename(archive)
        if digest(archive) != runtime['archiveSha256']:
            raise ValueError('Electron archive digest mismatch')
        inputs = application_build.prepare(source, output/'application-inputs', source_sha=host_input['baseSourceCommit'],
            source_tree_sha=host_input['sourceTreeSha'], partner_sha=plugin_input['baseSourceCommit'], workflow_sha=host_input['baseSourceCommit'], electron_archive=archive)
        publish = ['dotnet', 'publish', 'src/NexusPipeline.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false',
            '-p:PublishSingleFile=true', '-p:DebugType=none', '-p:DebugSymbols=false', '-p:UseSharedCompilation=false', '--disable-build-servers',
            '-p:NexusTestHost=false', '-p:NexusFrontendProps='+inputs['frontendProps'], '-p:NexusBuildIdentityPath='+inputs['identity']]
        production = output/'production'
        command('Production Host', [*publish, '-o', production])
        pe.verify_embedded_manifest(production/'NexusPipeline.exe', 'requireAdministrator')
        helper = output/'installer-helper'
        command('Production metadata helper', [*publish, '-p:NexusInstallerMetadataHelper=true', '-o', helper])
        pe.verify_embedded_manifest(helper/'nxp-metadata-helper.exe', 'asInvoker')
        shutil.copytree(inputs['desktopBundle'], production/'resources/desktop')
        shutil.copy2(source/'README.md', production/'README.md')
        release.stage_bundled_plugins(production, partner, source/'src/Modules/Plugins/Repository/BundledPlugins.json')
        identity = json.loads(Path(inputs['identity']).read_bytes())
        manifest = payload.create(production, identity, source)
        payload.validate(production, manifest, identity, source)
        package = output/'NexusPipeline-v0.17.2-local-acceptance-win-x64.zip'
        package.unlink(missing_ok=True)
        files = [{'path': path.relative_to(production).as_posix(), 'sizeBytes': path.stat().st_size, 'sha256': digest(path)}
                 for path in sorted(production.rglob('*')) if path.is_file()]
        with zipfile.ZipFile(package, 'x', compression=zipfile.ZIP_DEFLATED, compresslevel=6) as bundle:
            for file in files:
                info = zipfile.ZipInfo(file['path'], date_time=(1980,1,1,0,0,0))
                info.create_system=0; info.external_attr=0o644<<16; info.compress_type=zipfile.ZIP_DEFLATED
                bundle.writestr(info, (production/file['path']).read_bytes())
        with zipfile.ZipFile(package) as bundle:
            for file in files:
                if hashlib.sha256(bundle.read(file['path'])).hexdigest() != file['sha256']:
                    raise ValueError('Local ZIP payload mismatch')
        metadata = {'mode': 'local-acceptance', 'producer': 'local', 'publishable': False, 'version': '0.17.2', 'tag': 'v0.17.2',
                    'sourceSha': host_input['baseSourceCommit'], 'sha256': digest(package), 'payloadFiles': files,
                    'buildInputs': identity['buildInputs'], 'buildId': identity['buildId'], 'frontendHash': identity['frontendHash']}
        setup = installer.compile_payload(production, metadata, source/'tools/runtime-dependencies.json', compiler,
            output/'installer', source/'tools/installer.iss.in', helper/'nxp-metadata-helper.exe')
        packages = output/'plugin-packages'
        packages.mkdir(exist_ok=True)
        plugin_results = []
        for plugin_json in sorted((partner/'plugins').glob('*/*/plugin.json')):
            plugin = json.loads(plugin_json.read_bytes())
            artifact = plugin['artifactName']
            report = packages/(artifact+'.json')
            command('Package '+artifact, [sys.executable, partner/'tools/repo.py', 'package', '--root', partner,
                    '--artifact', artifact, '--output', packages/artifact, '--report', report, '--host-root', source], partner)
            plugin_results.append(json.loads(report.read_bytes()))
        for root, original in ((host, host_input), (plugins, plugin_input)):
            current = source_entries(root)
            current_manifest = [{'path': name, 'mode': mode, 'sizeBytes': len(raw), 'sha256': hashlib.sha256(raw).hexdigest()} for name, mode, raw in current]
            if current_manifest != original['files'] or git(root, 'rev-parse', 'HEAD') != original['baseSourceCommit']:
                raise ValueError('Working source changed during the build')
            for command_name, field in (('ls-files', 'gitIndexSha256'), ('show-ref', 'gitRefsSha256')):
                arguments = ['git', command_name] + (['--stage', '-z'] if command_name == 'ls-files' else [])
                if hashlib.sha256(subprocess.check_output(arguments, cwd=root)).hexdigest() != original[field]:
                    raise ValueError('Original Git index or refs changed during the build')
        acceptance = output/'acceptance'
        shutil.copytree(production, acceptance)
        shutil.rmtree(acceptance/'plugins')
        (acceptance/'plugins').mkdir()
        for result in plugin_results:
            # Repository packaging already validates the complete plugin ZIP.
            artifact = result['artifactName']
            candidates = list((packages/artifact).glob('*.zip'))
            if len(candidates) != 1:
                raise ValueError('Ambiguous plugin package')
            destination = acceptance/'plugins'/artifact
            destination.mkdir()
            with zipfile.ZipFile(candidates[0]) as bundle:
                for item in bundle.infolist():
                    name = release._safe_package_entry(item.filename, inner_plugin=True)
                    target = destination/name
                    target.parent.mkdir(parents=True, exist_ok=True)
                    target.write_bytes(bundle.read(item))
        software_files = [{'path': file.relative_to(acceptance).as_posix(), 'sizeBytes': file.stat().st_size, 'sha256': digest(file)} for file in sorted(acceptance.rglob('*')) if file.is_file()]
        (output/'acceptance-files.json').write_text(json.dumps(software_files, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
        expected_plugins = {json.loads(file.read_bytes())['artifactName'] for file in (partner/'plugins').glob('*/*/plugin.json')}
        if {result['artifactName'] for result in plugin_results} != expected_plugins:
            raise ValueError('Acceptance must contain every active source plugin')
        receipt.update(status='BUILD_PASS', buildId=identity['buildId'], buildInputs=identity['buildInputs'],
            frontendHash=identity['frontendHash'], zip={'path': str(package), 'sha256': digest(package)},
            setup={'path': str(output/'installer/NexusPipeline-v0.17.2-win-x64-setup.exe'), 'sha256': setup['setupSha256']},
            software=str(acceptance), pluginBuilds=plugin_results)
        receipt.update(hostVersion='0.17.2', gameCheckInVersion='0.4.2', installationGeneration='g0170',
            baseSourceCommit=host_input['baseSourceCommit'], sourceTreeSha=host_input['sourceTreeSha'],
            hostSourceManifestSha256=host_input['sourceManifestSha256'], pluginsBaseCommit=plugin_input['baseSourceCommit'],
            pluginsSourceManifestSha256=plugin_input['sourceManifestSha256'])
        receipt['artifacts'] = [receipt['zip'], receipt['setup']] + [
            {'path': str(next((packages/result['artifactName']).glob('*.zip'))), 'sha256': result['sha256'], 'version': result['version']}
            for result in plugin_results]
        for name, item in (('zip', receipt['zip']), ('setup', receipt['setup'])):
            Path(item['path']+'.sha256').write_text(item['sha256']+'  '+Path(item['path']).name+'\n', encoding='utf-8')
        return receipt
    finally:
        receipt['finishedAt']=time.time()
        receipt_path.write_text(json.dumps(receipt, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
        (logs/f'attempt-{attempt:02d}.json').write_text(json.dumps(receipt, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')


def main():
    parser=argparse.ArgumentParser(allow_abbrev=False)
    parser.add_argument('command', choices=['build'])
    for name in ('host-root','plugins-root','output'):
        parser.add_argument('--'+name, type=Path, required=True)
    for name in ('compiler','cache'):
        parser.add_argument('--'+name, type=Path)
    args=parser.parse_args()
    build(args.host_root.resolve(), args.plugins_root.resolve(), args.output, args.compiler, (args.cache or args.output/'cache').resolve())


if __name__=='__main__':
    main()
