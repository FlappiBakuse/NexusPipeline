import hashlib
import json
import subprocess

from installer_runtime import render_runtime


ADAPTERS = r'''
var
  Scenario: String;
  LaunchCount: Integer;
  QuietMode, DownloadCancelled, FrameworkPresent, Confirmed, Downloaded, DownloadVisible, PackageChanged: Boolean;

function ProbeFramework(const Framework, Required: String): Boolean;
begin
  Result := FrameworkPresent;
end;

function ConfirmRuntime(const Message: String; Kind: TMsgBoxType; Buttons: Integer): Integer;
begin
  Confirmed := True;
  Result := IDYES;
  if Scenario = 'decline' then Result := IDNO;
end;

procedure DownloadClear;
begin
end;

procedure DownloadAdd(const URL, Name, Digest: String);
begin
  if (URL <> 'https://controlled.test/runtime.exe') or (Name <> 'runtime.exe') or (Digest <> 'verified') then
    RaiseException('download_arguments_changed');
end;

procedure DownloadShow;
begin
  DownloadVisible := True;
end;

procedure DownloadHide;
begin
  DownloadVisible := False;
end;

procedure DownloadRuntime;
begin
  Downloaded := True;
  if (Scenario = 'download') or (Scenario = 'cancel') then RaiseException('controlled download failure');
end;

function LinkedRuntime(const Path: String): Boolean;
begin
  Result := Scenario = 'link';
end;

function HashRuntime(const Path: String): String;
begin
  Result := 'verified';
  if (Scenario = 'digest') or PackageChanged then Result := 'changed';
end;

function SignedRuntime(const Path: String): Boolean;
begin
  PackageChanged := Scenario = 'changed-after-signature';
  Result := Scenario <> 'signature';
end;

function LaunchRuntime(const Verb, FileName, Params, WorkingDir: String; Show: Integer;
  Wait: TExecWait; var Code: Integer): Boolean;
begin
  LaunchCount := LaunchCount + 1;
  if (Verb <> 'runas') or (Params <> '/install /quiet /norestart') or (Show <> SW_HIDE) or
    (Wait <> ewWaitUntilTerminated) then RaiseException('launch_arguments_changed');
  Code := 0;
  if Scenario = 'reboot' then Code := 3010;
  if Scenario = 'exit' then Code := 1603;
  if Scenario = 'launch' then Code := 1223;
  Result := Scenario <> 'launch';
  FrameworkPresent := (Code = 0) and (Scenario <> 'still-missing');
end;
'''

CHECKS = r'''
procedure VerifyCase(const Name, ExpectedMessage: String; Asked, Fetched, Restarted: Boolean; Launched: Integer);
var
  Error, Expected: String;
  Restart: Boolean;
begin
  Scenario := Name;
  LaunchCount := 0;
  Confirmed := False;
  Downloaded := False;
  DownloadVisible := False;
  PackageChanged := False;
  FrameworkPresent := Name = 'already';
  QuietMode := Name = 'quiet';
  DownloadCancelled := Name = 'cancel';
  Restart := False;
  Error := InstallFramework('Microsoft.NETCore.App', '10.0.0', 'component',
    'https://controlled.test/runtime.exe', 'runtime.exe', 'verified', Restart);
  Expected := '';
  if ExpectedMessage <> '' then begin
    if Name = 'download' then Expected := FmtMessage(CustomMessage(ExpectedMessage), ['component', 'controlled download failure'])
    else if Name = 'launch' then Expected := FmtMessage(CustomMessage(ExpectedMessage), ['component', SysErrorMessage(1223)])
    else if Name = 'exit' then Expected := FmtMessage(CustomMessage(ExpectedMessage), ['component', '1603'])
    else Expected := FmtMessage(CustomMessage(ExpectedMessage), ['component']);
  end;
  Log('Runtime contract ' + Name + ' confirmed=' + IntToStr(Ord(Confirmed)) + ' downloaded=' + IntToStr(Ord(Downloaded)) +
    ' restart=' + IntToStr(Ord(Restart)) + ' launches=' + IntToStr(LaunchCount));
  if (Error <> Expected) or (Confirmed <> Asked) or (Downloaded <> Fetched) or DownloadVisible or
    (Restart <> Restarted) or (LaunchCount <> Launched) then
    RaiseException('runtime_contract_failed_' + Name);
end;

function InitializeSetup: Boolean;
begin
@@CASES@@
  if not SaveStringToFile('@@RESULT@@', '[runtime]' + #13#10 + 'flow=complete' + #13#10, False) then
    RaiseException('report_failed');
  Result := True;
end;
'''


def case_calls():
    cases = [('already', '', False, False, False, 0), ('quiet', 'RuntimeSilent', False, False, False, 0),
        ('decline', 'RuntimeCancelled', True, False, False, 0),
        ('cancel', 'RuntimeCancelled', True, True, False, 0), ('download', 'RuntimeDownloadFailed', True, True, False, 0),
        ('link', 'RuntimeDigestFailed', True, True, False, 0), ('digest', 'RuntimeDigestFailed', True, True, False, 0),
        ('signature', 'RuntimeSignatureFailed', True, True, False, 0),
        ('changed-after-signature', 'RuntimeSignatureFailed', True, True, False, 0),
        ('launch', 'RuntimeLaunchFailed', True, True, False, 1), ('exit', 'RuntimeExitFailed', True, True, False, 1),
        ('reboot', 'RuntimeRestart', True, True, True, 1),
        ('still-missing', 'RuntimeStillMissing', True, True, False, 1), ('success', '', True, True, False, 1)]
    lines = [f"  VerifyCase('{name}', '{message}', {asked}, {fetched}, {restart}, {launches});"
        for name, message, asked, fetched, restart, launches in cases]
    return cases, '\n'.join(lines)


def native_flow(root, work, software, compiler):
    runtime = render_runtime(root, software)
    begin = runtime.index('function InstallFramework(')
    end = runtime.index('function PrepareFrameworks(', begin)
    original = runtime[begin:end]
    replacements = {'HasFramework(': 'ProbeFramework(', 'WizardSilent': 'QuietMode', 'MsgBox(': 'ConfirmRuntime(',
        'DependencyDownloadPage.Clear': 'DownloadClear', 'DependencyDownloadPage.Add': 'DownloadAdd',
        'DependencyDownloadPage.Show': 'DownloadShow', 'DependencyDownloadPage.Hide': 'DownloadHide',
        'DependencyDownloadPage.Download': 'DownloadRuntime', 'DependencyDownloadPage.AbortedByUser': 'DownloadCancelled',
        'HasLinkInPath(': 'LinkedRuntime(', 'GetSHA256OfFile(': 'HashRuntime(',
        'VerifyMicrosoftSignature(': 'SignedRuntime(', 'ShellExec(': 'LaunchRuntime('}
    controlled = original
    for before, after in replacements.items():
        controlled = controlled.replace(before, after)
    cases, calls = case_calls()
    report = work / 'native-result.ini'
    checks = CHECKS.replace('@@CASES@@', calls).replace('@@RESULT@@', str(report).replace("'", "''"))
    template = (root / 'tooling/installer.iss').read_text(encoding='utf-8')
    languages = template[template.index('[Languages]'):template.index('[Tasks]')].replace('@@CHINESE@@', str(root / 'tooling/ChineseSimplified.isl'))
    script = work / 'native-flow.iss'
    script.write_text('[Setup]\nAppName=Nexus runtime flow contract\nAppVersion=0.18.0\nCreateAppDir=no\nUninstallable=no\n'
        'PrivilegesRequired=lowest\nArchitecturesAllowed=x64compatible\nArchitecturesInstallIn64BitMode=x64compatible\n'
        'DisableProgramGroupPage=yes\nOutputDir=' + str(work) + '\nOutputBaseFilename=native-flow\n' + languages +
        '\n[Code]\n' + ADAPTERS + controlled + checks, encoding='utf-8', newline='\n')
    built = subprocess.run([str(compiler), '/Qp', str(script)], capture_output=True, timeout=60)
    (work / 'compile.log').write_bytes(built.stdout + built.stderr)
    if built.returncode:
        raise ValueError('Native runtime flow compilation failed')
    command = [str(work / 'native-flow.exe'), '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-',
        '/LANG=en', '/LOG=' + str(work / 'native.log')]
    result = subprocess.run(command, capture_output=True, timeout=60, creationflags=subprocess.CREATE_NO_WINDOW)
    (work / 'command.json').write_text(json.dumps(dict(command=command, exitCode=result.returncode,
        scope='Actual compiled production control flow with controlled external effects; no runtime installation',
        productionFunctionSha256=hashlib.sha256(original.encode()).hexdigest(), adapters=replacements, cases=[item[0] for item in cases]),
        indent=2) + '\n', encoding='utf-8')
    if result.returncode or not report.is_file():
        raise ValueError('Native runtime flow contract failed')
