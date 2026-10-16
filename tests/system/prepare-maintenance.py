"""Build an isolated native Inno fixture with the same installer and maintenance code."""
import argparse
import hashlib
import json
import re
from pathlib import Path
import subprocess
import sys
import uuid
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
from tools import host_installer, installer_artwork, update_baseline, update_packages, update_recipe

parser = argparse.ArgumentParser(allow_abbrev=False)
for name in ('application', 'target', 'inputs', 'compiler', 'baseline-package', 'baseline-release', 'output'):
    parser.add_argument('--' + name, type=Path, required=True)
args = parser.parse_args()
if args.output.exists():
    parser.error('Native fixture already exists')
official = update_baseline.read(args.baseline_package, json.loads(args.baseline_release.read_bytes()), target_version='0.17.4')
if host_installer.sha256(args.compiler) != host_installer.INNO_COMPILER_SHA256:
    parser.error('Inno compiler digest mismatch')
args.output.mkdir(parents=True)
scope = uuid.uuid4().hex
(args.output / '.nxp-installer-lab').write_text(scope)
inputs = json.loads((args.inputs / 'application-inputs.json').read_bytes())
identity = json.loads(Path(inputs['identity']).read_bytes())
files = [{'path': p.relative_to(args.application).as_posix(), 'sizeBytes': p.stat().st_size, 'sha256': host_installer.sha256(p)}
         for p in args.application.rglob('*') if p.is_file() and not p.relative_to(args.application).parts[0] == 'plugins']
files.sort(key=lambda p: p['path'])
installer_artwork.generate(Path(__file__).resolve().parents[2] / 'src/NexusPipeline.ico', args.output)
metadata = {'version': '0.17.4', 'sha256': 'a' * 64, 'buildId': identity['buildId'], 'buildInputs': identity['buildInputs'],
            'uninstallerBytes': (args.compiler.parent / 'Setup.e32').stat().st_size,
            'setupWorkBytes': (args.compiler.parent / 'Setup.e32').stat().st_size * 2}
script = host_installer.render_script(Path('tools/installer.iss.in').read_text(encoding='utf-8'),
    production_root=args.application, output_dir=args.output, metadata=metadata, files=files,
    dependencies=host_installer.dependency_pair(Path('tools/runtime-dependencies.json')),
    metadata_helper=Path(inputs['metadataHelper']), uninstaller=Path(inputs['uninstaller']))
owner_key = 'Software\\NexusPipeline\\Tests\\Installer\\' + scope
native_key = r'Software\Microsoft\Windows\CurrentVersion\Uninstall\NexusPipeline.Tests.' + scope + '_is1'
script = script.replace(r'Software\NexusPipeline.Generations\g0170\Installer', owner_key)
script = script.replace(r'Software\Microsoft\Windows\CurrentVersion\Uninstall\NexusPipeline.PerUser.g0170_is1', native_key)
script = script.replace('AppId=NexusPipeline.PerUser.g0170', 'AppId=NexusPipeline.Tests.' + scope)
script = script.replace(r'{localappdata}\NexusPipeline.Generations\g0170\installer', str(args.output / 'manager'))
script = script.replace(r'{localappdata}\NexusPipeline.Generations\g0170\registration-recovery', str(args.output / 'registration-recovery'))
script = script.replace("Join-Path $env:LOCALAPPDATA ''NexusPipeline.Generations\\g0170\\installer''", "''" + str(args.output / 'manager') + "''")
script = script.replace('{localappdata}', str(args.output / 'localappdata'))
script = script.replace('{group}', str(args.output / 'shortcuts')).replace('{autodesktop}', str(args.output / 'desktop'))
script = script.replace('NexusPipeline.SingleInstance', 'NexusPipeline.TestHost.SingleInstance.native-' + scope)
script = script.replace('NexusPipeline.Setup.g0170', 'NexusPipeline.TestSetup.' + scope)
script = script.replace("ShellExec('runas',", "ShellExec('',")
probe_start = script.index('if not SaveStringToFile(Script,')
probe_end = script.index('False) then Exit;', probe_start)
probe = script[probe_start:probe_end]
probe = '\n'.join(line.replace('exit 1', "throw ''installer.probe.rejected." + str(index) + "''")
                  for index, line in enumerate(probe.splitlines()))
probe = re.sub(r"'} catch \{ throw ''installer\.probe\.rejected\.[0-9]+'' \}'",
               "'} catch { [IO.File]::WriteAllText($ResultFile + ''.failure'', $_.Exception.ToString()); exit 1 }'", probe)
script = script[:probe_start] + probe + script[probe_end:]
import os
runtime = Path(os.environ['DOTNET_ROOT'])
script = script.replace(r"ExpandConstant('{commonpf64}\dotnet\dotnet.exe')", "'" + str(runtime / 'dotnet.exe') + "'")
script = script.replace(r"ExpandConstant('{commonpf64}\dotnet\shared\')", "'" + str(runtime / 'shared') + chr(92) + "'")
script = script.replace('Compression=lzma2', 'Compression=zip').replace('SolidCompression=yes', 'SolidCompression=no')
script_path = args.output / 'installer.generated.iss'
script_path.write_text(script, encoding='utf-8')
result = subprocess.run([str(args.compiler), '/Q', str(script_path)], capture_output=True, text=True, encoding='utf-8', errors='replace')
(args.output / 'iscc.stdout.log').write_text(result.stdout, encoding='utf-8')
(args.output / 'iscc.stderr.log').write_text(result.stderr, encoding='utf-8')
if result.returncode:
    raise SystemExit(result.returncode)
shared_official = os.environ.get('NEXUS_MAINTENANCE_OFFICIAL_DIRECTORY')
official_root = Path(shared_official).resolve() if shared_official else args.output / 'official-v0.17.3'
with zipfile.ZipFile(args.baseline_package) as archive:
    expected_files = set()
    for entry in archive.infolist():
        path = Path(entry.filename)
        if path.is_absolute() or '..' in path.parts:
            raise ValueError('Unsafe official package')
        if entry.is_dir():
            continue
        expected_files.add(path.as_posix())
        if shared_official:
            file = official_root / path
            if file.resolve() != file or not file.is_file():
                raise ValueError('Shared official input is linked or missing')
            with archive.open(entry) as stream:
                expected_hash = hashlib.file_digest(stream, 'sha256').hexdigest()
            if host_installer.sha256(file) != expected_hash:
                raise ValueError('Shared official input bytes differ')
    if shared_official:
        if {p.relative_to(official_root).as_posix() for p in official_root.rglob('*') if p.is_file()} != expected_files:
            raise ValueError('Shared official input has unknown files')
    else:
        archive.extractall(official_root)
raw = (args.target / update_recipe.MANIFEST_PATH).read_bytes()
manifest = json.loads(raw)
payload = [{key: file[key] for key in ('path', 'sizeBytes', 'sha256')} for file in manifest['files']]
payload.append({'path': update_recipe.MANIFEST_PATH, 'sizeBytes': len(raw), 'sha256': hashlib.sha256(raw).hexdigest()})
transport = update_packages.generate(args.target, args.output, 'v0.17.5', manifest, payload)
package = args.output / 'NexusPipeline-v0.17.5-win-x64-Portable.zip'
(args.output / 'NexusPipeline-v0.17.5-win-x64-Setup.exe').write_bytes(b'not-executed-metadata-only-fixture')
(args.output / 'target-transport.json').write_text(json.dumps(transport, indent=2), encoding='utf-8')
(args.output / 'scope.json').write_text(json.dumps({'id': scope, 'setup': str(args.output / 'NexusPipeline-v0.17.4-win-x64-Setup.exe'),
    'officialBaseline': str(official_root), 'officialIdentity': official[0], 'targetPackage': str(package),
    'classification': 'ASINVOKER_NATIVE_FIXTURE', 'productionUacTested': False,
    'fixtureChanges': ['unique HKCU AppId and ownership key', 'manager, shortcuts and temporary files under test root',
                       'existing TestHost mutex scope', 'asInvoker maintenance handoff', 'pinned external runtime location', 'ZIP compression', 'failure-only native probe diagnostics'],
    'scriptSha256': hashlib.sha256(script_path.read_bytes()).hexdigest(),
    'sourceRoot': str(Path(__file__).resolve().parents[2])}, indent=2), encoding='utf-8')
print('Native fixture compiled; official baseline authenticated, unmodified and isolated')
