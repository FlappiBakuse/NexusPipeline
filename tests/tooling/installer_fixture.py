import io
import json
import os
from pathlib import Path
import struct
import subprocess

from binary_inputs import hash_file
from dotnet_bundle import MARKER
from installer_runtime import FRAMEWORKS, read_dependencies, render_runtime
from product_assets import COMPILER_SHA256
from source_snapshot import ordinary_path


def input_path(name):
    value = os.environ.get(name)
    if not value:
        raise FileNotFoundError(f'{name} is required for the selected installer test')
    return ordinary_path(value)


def verify_input(path, digest, size=None):
    actual_size = path.stat().st_size
    if (size is not None and actual_size != size) or hash_file(path) != digest:
        raise ValueError(f'Installer fixture differs from its pinned input: {path}')
    return dict(path=str(path), sha256=digest, sizeBytes=actual_size)


def compiler_inputs(_root):
    return dict(compiler=verify_input(input_path('NEXUS_INSTALLER_COMPILER'), COMPILER_SHA256))


def native_inputs(root):
    inputs = compiler_inputs(root)
    receipt = json.loads(input_path('NEXUS_INSTALLER_RUNTIME_RECEIPT').read_bytes())
    expected = {item['sha256']: item['sizeBytes'] for item in read_dependencies(root).values()}
    records = receipt.get('dependencies') if isinstance(receipt, dict) else None
    if (not isinstance(records, list) or receipt.get('status') != 'PASS_OFFICIAL_RUNTIME_HASHES' or
            len(records) != len(expected) or any(not isinstance(item, dict) or
                not isinstance(item.get('sha256'), str) or not isinstance(item.get('path'), str) for item in records) or
            {item['sha256'] for item in records} != expected.keys()):
        raise ValueError('Installer runtime receipt must bind both pinned Microsoft packages')
    inputs['dependencies'] = [verify_input(ordinary_path(item['path']), item['sha256'], expected[item['sha256']])
                              for item in records]
    return inputs


def bundle(project, frameworks):
    native = bytearray(512)
    native[:2] = b'MZ'; native[64:68] = b'PE\0\0'; native[400:432] = MARKER
    struct.pack_into('<I', native, 60, 64)
    struct.pack_into('<HH', native, 68, 0x8664, 1)
    struct.pack_into('<H', native, 84, 112)
    struct.pack_into('<H', native, 88, 0x20b)
    struct.pack_into('<II', native, 216, 256, 256)
    stream = io.BytesIO(native); stream.seek(512)
    entries = []
    prefix = 'NexusPipeline.' + project
    for name, kind, data in ((prefix + '.dll', 1, b'controlled-managed'), (prefix + '.deps.json', 3, b'{}'),
            (prefix + '.runtimeconfig.json', 4, json.dumps(dict(runtimeOptions=frameworks)).encode())):
        entries.append((name, stream.tell(), len(data), kind)); stream.write(data)
    header = stream.tell()
    stream.write(struct.pack('<IIi', 6, 0, len(entries)))
    identity = b'controlled-framework-fixture'
    stream.write(bytes([len(identity)]) + identity)
    stream.write(struct.pack('<qqqqQ', entries[1][1], entries[1][2], entries[2][1], entries[2][2], 0))
    for name, position, size, kind in entries:
        raw = name.encode(); stream.write(struct.pack('<qqqB', position, size, 0, kind)); stream.write(bytes([len(raw)]) + raw)
    raw = bytearray(stream.getvalue()); struct.pack_into('<q', raw, 392, header)
    return bytes(raw)


def application(path, version='10.0.0', fault=None):
    path.mkdir()
    values = [dict(name=name, version=version) for name in FRAMEWORKS]
    host = dict(frameworks=values)
    helper = dict(framework=values[0])
    if fault == 'self-contained':
        host = dict(includedFrameworks=values)
    if fault == 'conflicting':
        helper = dict(framework=dict(name=FRAMEWORKS[0], version='10.0.1'))
    if fault == 'unknown':
        host['frameworks'] = [dict(name='Microsoft.WindowsDesktop.App', version=version), values[0]]
    (path / 'NexusPipeline.exe').write_bytes(bundle('Host', host))
    (path / 'NexusPipeline.update.exe').write_bytes(bundle('Maintenance', helper))
    desktop = path / 'resources/desktop'
    desktop.mkdir(parents=True)
    (desktop / 'NexusPipeline.Launcher.exe').write_bytes(bundle('Launcher', dict(framework=values[0])))


def native_probe(root, work, software, compiler, packages):
    runtime = render_runtime(root, software)
    template = (root / 'tooling/installer.iss').read_text(encoding='utf-8')
    languages = template[template.index('[Languages]'):template.index('[Tasks]')].replace('@@CHINESE@@', str(root / 'tooling/ChineseSimplified.isl'))
    report = work / 'native-result.ini'
    calls = []
    for item in packages:
        path = item['path'].replace("'", "''")
        calls.append(f"  if not VerifyMicrosoftSignature('{path}') then RaiseException('official_signature_rejected');")
    code = """
function InitializeSetup: Boolean;
var
  Restart: Boolean;
  Error: String;
begin
  if not HasFramework('Microsoft.NETCore.App', '10.0.0') then RaiseException('core_not_detected');
  if not HasFramework('Microsoft.AspNetCore.App', '10.0.0') then RaiseException('aspnet_not_detected');
  if HasFramework('Microsoft.NETCore.App', '10.1.0') then RaiseException('wrong_version_accepted');
  if HasFramework('Microsoft.WindowsDesktop.App', '999.0.0') then RaiseException('foreign_framework_accepted');
  Restart := False;
  Error := PrepareFrameworks(Restart);
  if (Error <> '') or Restart then RaiseException('installed_runtime_not_reused');
  Error := InstallFramework('Microsoft.NETCore.App', '10.1.0', 'controlled missing runtime', 'unused', 'unused.exe', 'unused', Restart);
  if (Error = '') or Restart then RaiseException('silent_missing_not_rejected');
@@SIGNATURES@@
  if VerifyMicrosoftSignature(ExpandConstant('{srcexe}')) then RaiseException('unsigned_package_accepted');
  if not SaveStringToFile('@@RESULT@@', '[runtime]' + #13#10 + 'probe=complete' + #13#10, False) then RaiseException('report_failed');
  Result := True;
end;
""".replace('@@SIGNATURES@@', '\n'.join(calls)).replace('@@RESULT@@', str(report).replace("'", "''"))
    script = work / 'native-probe.iss'
    script.write_text('[Setup]\nAppName=Nexus runtime contract\nAppVersion=0.18.0\nCreateAppDir=no\nUninstallable=no\n'
        'PrivilegesRequired=lowest\nArchitecturesAllowed=x64compatible\nArchitecturesInstallIn64BitMode=x64compatible\n'
        'DisableProgramGroupPage=yes\nOutputDir=' + str(work) + '\nOutputBaseFilename=native-probe\n' + languages + '\n[Code]\n' + runtime + code,
        encoding='utf-8', newline='\n')
    built = subprocess.run([str(compiler), '/Qp', str(script)], capture_output=True, timeout=60)
    (work / 'compile.log').write_bytes(built.stdout + built.stderr)
    if built.returncode:
        raise ValueError('Native prerequisite probe compilation failed')
    command = [str(work / 'native-probe.exe'), '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-',
        '/LANG=en', '/LOG=' + str(work / 'native.log')]
    result = subprocess.run(command, capture_output=True, timeout=60, creationflags=subprocess.CREATE_NO_WINDOW)
    (work / 'command.json').write_text(json.dumps(dict(command=command, exitCode=result.returncode), indent=2) + '\n', encoding='utf-8')
    if result.returncode or not report.is_file():
        raise ValueError('Native prerequisite probe failed')
