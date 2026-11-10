"""Bind native Setup lifecycle calls to the same source per-user installer worker."""
import re

from binary_inputs import hash_file


SCRIPT = 'tooling/installer-lifecycle.iss'
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
    code = (root / SCRIPT).read_text(encoding='utf-8')
    values = dict(CONTEXT_CODE=TEST_CONTEXT if assembly['profile'] == 'test' else CONTEXT,
        HELPER_SHA256=helper['sha256'], MANIFEST_SHA256=manifest['sha256'], UNINSTALL_SHA256=facade['sha256'])
    for key, value in values.items():
        code = code.replace('@@' + key + '@@', value)
    if '@@' in code:
        raise ValueError('Unbound installer lifecycle input')
    return dict(APP_ID='{code:InstallerAppId}' if assembly['profile'] == 'test' else 'NexusPipeline.PerUser.0.18',
        PREVIOUS_LANGUAGE='no' if assembly['profile'] == 'test' else 'yes', LIFECYCLE_CODE=code)
