"""Bind Setup lifecycle and prerequisites to the same packaged application."""
import json
import re

from binary_inputs import hash_file
from dotnet_bundle import read_bundle, read_member


LIFECYCLE_SCRIPT = 'tooling/installer-lifecycle.iss'
CONTEXT = r'''
function InstallerAppId(Param: String): String;
begin
  Result := 'NexusPipeline.PerUser.0.18';
end;

function InstallerManager: String;
begin
  Result := ExpandConstant('{localappdata}\NexusPipeline.Generations\g0180\installer');
end;

function InstallerDesktop(Param: String): String;
begin
  Result := ExpandConstant('{userdesktop}');
end;

function InstallerGroup(Param: String): String;
begin
  Result := ExpandConstant('{userprograms}\NexusPipeline');
end;
'''
TEST_CONTEXT = r'''
function InstallerFixture: String;
begin
  Result := GetEnv('NEXUS_TEST_ROOT');
  if (Result = '') or HasLinkInPath(Result) or not DirExists(Result) then
    RaiseException('This test installer requires its isolated test context.');
  Result := RemoveBackslashUnlessRoot(ExpandFileName(Result));
end;

function InstallerScope: String;
var
  Path: String;
  Bytes: AnsiString;
  I: Integer;
begin
  Path := AddBackslash(InstallerFixture) + '.nexus-installer-fixture';
  if HasLinkInPath(Path) or not LoadStringFromFile(Path, Bytes) or (Length(Bytes) <> 32) then
    RaiseException('The test installation context could not be verified.');
  Result := String(Bytes);
  for I := 1 to Length(Result) do
    if Pos(Result[I], '0123456789abcdef') = 0 then
      RaiseException('The test installation context could not be verified.');
end;

function InstallerAppId(Param: String): String;
begin
  Result := 'NexusPipeline.Tests.' + InstallerScope;
end;

function InstallerManager: String;
begin
  Result := AddBackslash(InstallerFixture) + 'installer-manager';
end;

function InstallerDesktop(Param: String): String;
begin
  Result := AddBackslash(InstallerFixture) + 'desktop';
end;

function InstallerGroup(Param: String): String;
begin
  Result := AddBackslash(InstallerFixture) + 'programs\NexusPipeline';
end;
'''


def render_lifecycle(root, application, assembly):
    if assembly['profile'] not in ('test', 'production'):
        raise ValueError('Unsupported installer profile')
    records = {item['path']: item for item in assembly['files']}
    worker = assembly['installerWorker']
    helper = worker['file']
    facade = assembly['uninstallerEntry']['file']
    manifest = records['resources/payload-manifest.json']
    from pathlib import Path
    for item, path in ((helper, Path(worker['path'])), (manifest, application / manifest['path'])):
        if re.fullmatch('[0-9a-f]{64}', item['sha256']) is None or hash_file(path) != item['sha256']:
            raise ValueError('Installer lifecycle input changed')
    code = (root / LIFECYCLE_SCRIPT).read_text(encoding='utf-8')
    values = dict(CONTEXT_CODE=TEST_CONTEXT if assembly['profile'] == 'test' else CONTEXT,
        HELPER_SHA256=helper['sha256'], MANIFEST_SHA256=manifest['sha256'], UNINSTALL_SHA256=facade['sha256'],
        INITIAL_ARGUMENT=("' --initial-extensions \"' + ExpandConstant('{tmp}\\nexus-initial-extensions') + '\"'"
                          if assembly.get('initialExtensions') else "''"))
    for key, value in values.items():
        code = code.replace('@@' + key + '@@', value)
    if '@@' in code:
        raise ValueError('Unbound installer lifecycle input')
    return dict(APP_ID='{code:InstallerAppId}' if assembly['profile'] == 'test' else 'NexusPipeline.PerUser.0.18',
        PREVIOUS_LANGUAGE='no' if assembly['profile'] == 'test' else 'yes', LIFECYCLE_CODE=code)


LOCK = 'tooling/runtime-dependencies.json'
RUNTIME_SCRIPT = 'tooling/installer-runtime.iss'
FRAMEWORKS = ('Microsoft.NETCore.App', 'Microsoft.AspNetCore.App')


def read_dependencies(root):
    value = json.loads((root / LOCK).read_bytes())
    if set(value) != {'schemaVersion', 'dependencies'} or value['schemaVersion'] != 1 or len(value['dependencies']) != 2:
        raise ValueError('Invalid Setup runtime lock')
    result = {}
    for item in value['dependencies']:
        if (set(item) != {'framework', 'minimumVersion', 'version', 'rid', 'url', 'sizeBytes', 'sha512', 'sha256', 'metadataUrl'} or
                item['framework'] not in FRAMEWORKS or item['framework'] in result or item['rid'] != 'win-x64' or
                item['minimumVersion'] != '10.0.0' or item['version'] != '10.0.14' or
                type(item['sizeBytes']) is not int or not 0 < item['sizeBytes'] <= 134217728 or
                re.fullmatch('[a-f0-9]{64}', item['sha256']) is None or re.fullmatch('[a-f0-9]{128}', item['sha512']) is None or
                item['metadataUrl'] != 'https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json'):
            raise ValueError('Invalid Setup runtime identity')
        prefix = 'Runtime/' if item['framework'] == FRAMEWORKS[0] else 'aspnetcore/Runtime/'
        name = 'dotnet-runtime-' if item['framework'] == FRAMEWORKS[0] else 'aspnetcore-runtime-'
        expected = 'https://builds.dotnet.microsoft.com/dotnet/' + prefix + item['version'] + '/' + name + item['version'] + '-win-x64.exe'
        if item['url'] != expected:
            raise ValueError('Setup runtime must use its fixed official package')
        result[item['framework']] = item
    return result


def required_frameworks(application, installer=None, uninstaller=None):
    result = {}
    programs = [(application / 'NexusPipeline.exe', 'Host'), (application / 'NexusPipeline.update.exe', 'Maintenance'),
                (application / 'resources/desktop/NexusPipeline.Launcher.exe', 'Launcher')]
    if installer is not None:
        programs.append((installer, 'Installer'))
    if uninstaller is not None:
        programs.append((uninstaller, 'Uninstaller'))
    for path, project in programs:
        with path.open('rb') as stream:
            files = read_bundle(stream)
            raw = read_member(stream, files, 'NexusPipeline.' + project + '.runtimeconfig.json', 4)
        options = json.loads(raw)['runtimeOptions']
        if 'includedFrameworks' in options:
            raise ValueError('Setup requires framework-dependent product programs')
        if options.get('rollForward') not in (None, 'Minor', 'LatestPatch') or options.get('applyPatches') is False:
            raise ValueError('Setup requires product framework patch roll-forward')
        frameworks = options.get('frameworks') or [options['framework']]
        for framework in frameworks:
            name, version = framework['name'], framework['version']
            if name not in FRAMEWORKS or name in result and result[name] != version:
                raise ValueError('Conflicting product framework declarations')
            result[name] = version
    if set(result) != set(FRAMEWORKS):
        raise ValueError('Product framework closure is incomplete')
    return result


def render_runtime(root, application, installer=None, uninstaller=None):
    dependencies = read_dependencies(root)
    required = required_frameworks(application, installer, uninstaller)
    for framework in FRAMEWORKS:
        item = dependencies[framework]
        if required[framework] != item['minimumVersion']:
            raise ValueError('Setup runtime minimum differs from the actual program')
    core, web = (dependencies[name] for name in FRAMEWORKS)
    values = {'CORE_MINIMUM': core['minimumVersion'], 'WEB_MINIMUM': web['minimumVersion'], 'WEB_VERSION': web['version'],
              'CORE_INSTALL': install_call(core, 'CoreVersion', 'CoreDescription'),
              'WEB_INSTALL': install_call(web, "'" + web['minimumVersion'] + "'", "'ASP.NET Core Runtime 10 x64'")}
    script = (root / RUNTIME_SCRIPT).read_text(encoding='utf-8')
    for name, value in values.items():
        script = script.replace('@@' + name + '@@', value)
    return script


def install_call(item, minimum, description):
    url = item['url']
    package = ', '.join(f"'{value}'" for value in (url, url.rsplit('/', 1)[1], item['sha256']))
    return f"InstallFramework('{item['framework']}', {minimum}, '{item['version']}', {description}, {package}, NeedsRestart)"
