import hashlib
import json
import subprocess

from installer_runtime import render_runtime


ADAPTERS = r'''
var
  Scenario, Trace: String;
  ProbeCount, LaunchCount: Integer;
  QuietMode, DownloadCancelled: Boolean;

procedure RecordCall(const Name: String);
begin
  Trace := Trace + Name + ';';
end;

function ProbeFramework(const Framework, Required: String): Boolean;
begin
  RecordCall('probe');
  ProbeCount := ProbeCount + 1;
  Result := ((ProbeCount = 1) and (Scenario = 'already')) or
    ((ProbeCount = 2) and (Scenario <> 'still-missing'));
end;

function ConfirmRuntime(const Message: String; Kind: TMsgBoxType; Buttons: Integer): Integer;
begin
  RecordCall('confirm');
  Result := IDYES;
  if Scenario = 'decline' then Result := IDNO;
end;

procedure DownloadClear;
begin
  RecordCall('clear');
end;

procedure DownloadAdd(const URL, Name, Digest: String);
begin
  RecordCall('add');
  if (URL <> 'https://controlled.test/runtime.exe') or (Name <> 'runtime.exe') or (Digest <> 'verified') then
    RaiseException('download_arguments_changed');
end;

procedure DownloadShow;
begin
  RecordCall('show');
end;

procedure DownloadHide;
begin
  RecordCall('hide');
end;

procedure DownloadRuntime;
begin
  RecordCall('download');
  if (Scenario = 'download') or (Scenario = 'cancel') then RaiseException('controlled download failure');
end;

function LinkedRuntime(const Path: String): Boolean;
begin
  RecordCall('link');
  Result := Scenario = 'link';
end;

function HashRuntime(const Path: String): String;
begin
  RecordCall('hash');
  Result := 'verified';
  if Scenario = 'digest' then Result := 'changed';
end;

function SignedRuntime(const Path: String): Boolean;
begin
  RecordCall('signature');
  Result := Scenario <> 'signature';
end;

function LaunchRuntime(const Verb, FileName, Params, WorkingDir: String; Show: Integer;
  Wait: TExecWait; var Code: Integer): Boolean;
begin
  RecordCall('launch');
  LaunchCount := LaunchCount + 1;
  if (Verb <> 'runas') or (Params <> '/install /quiet /norestart') or (Show <> SW_HIDE) or
    (Wait <> ewWaitUntilTerminated) then RaiseException('launch_arguments_changed');
  Code := 0;
  if Scenario = 'reboot' then Code := 3010;
  if Scenario = 'exit' then Code := 1603;
  if Scenario = 'launch' then Code := 1223;
  Result := Scenario <> 'launch';
end;
'''

CHECKS = r'''
procedure VerifyCase(const Name, ExpectedMessage, ExpectedTrace: String; Restarted: Boolean; Launched: Integer);
var
  Error, Expected: String;
  Restart: Boolean;
begin
  Scenario := Name;
  Trace := '';
  ProbeCount := 0;
  LaunchCount := 0;
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
  Log('Runtime contract ' + Name + ' trace=' + Trace + ' restart=' + IntToStr(Ord(Restart)) + ' launches=' + IntToStr(LaunchCount));
  if (Error <> Expected) or (Trace <> ExpectedTrace) or (Restart <> Restarted) or (LaunchCount <> Launched) then
    RaiseException('runtime_contract_failed_' + Name);
end;

function InitializeSetup: Boolean;
begin
@@CASES@@
  if not SaveStringToFile('@@RESULT@@', '[runtime]' + #13#10 + 'controlledCases=13' + #13#10, False) then
    RaiseException('report_failed');
  Result := True;
end;
'''


def case_calls():
    download = 'probe;confirm;clear;add;show;download;hide;'
    verified = download + 'link;hash;signature;hash;launch;'
    cases = [('already', '', 'probe;', False, 0), ('quiet', 'RuntimeSilent', 'probe;', False, 0),
        ('decline', 'RuntimeCancelled', 'probe;confirm;', False, 0),
        ('cancel', 'RuntimeCancelled', download, False, 0), ('download', 'RuntimeDownloadFailed', download, False, 0),
        ('link', 'RuntimeDigestFailed', download + 'link;', False, 0),
        ('digest', 'RuntimeDigestFailed', download + 'link;hash;', False, 0),
        ('signature', 'RuntimeSignatureFailed', download + 'link;hash;signature;', False, 0),
        ('launch', 'RuntimeLaunchFailed', verified, False, 1), ('exit', 'RuntimeExitFailed', verified, False, 1),
        ('reboot', 'RuntimeRestart', verified, True, 1),
        ('still-missing', 'RuntimeStillMissing', verified + 'probe;', False, 1), ('success', '', verified + 'probe;', False, 1)]
    lines = ["  VerifyCase('" + name + "', '" + message + "', '" + trace + "', " + str(restart) + ', ' + str(launches) + ');'
        for name, message, trace, restart, launches in cases]
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
    return report.read_text(encoding='ascii')
