type
  TInstallerGuid = record
    First: Cardinal;
    Second, Third: Word;
    Bytes: array[0..7] of Byte;
  end;

var
  InstallerGuard: Integer;
  InstallerHelper, InstallerTarget, InstallerRoot, InstallerTransaction: String;
  InstallerPrepared, InstallerQualified: Boolean;
  InstallerTransition: Boolean;
  DeleteInstanceData, RemovalPending: Boolean;

function NativeGuid(var Value: TInstallerGuid): Integer;
  external 'CoCreateGuid@ole32.dll stdcall';
function NativeMutex(Attributes: Integer; InitialOwner: Boolean; Name: String): Integer;
  external 'CreateMutexW@kernel32.dll stdcall';
function NativeClose(Handle: Integer): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';
function NativeError: Integer;
  external 'GetLastError@kernel32.dll stdcall';
function NativeEnvironment(Name, Value: String): Boolean;
  external 'SetEnvironmentVariableW@kernel32.dll stdcall';
function NativeEnvironmentClear(Name: String; Value: Integer): Boolean;
  external 'SetEnvironmentVariableW@kernel32.dll stdcall';

@@CONTEXT_CODE@@

function NewInstallation: Boolean;
begin
  Result := not InstallerTransition;
end;

function TransitionWork: String;
begin
  Result := '';
  if InstallerTransition then Result := ' --work-root "' + ExpandConstant('{tmp}\nexus-transition-check') + '"';
end;

function NewTransaction: String;
var
  Value: TInstallerGuid;
begin
  if NativeGuid(Value) <> 0 then RaiseException(CustomMessage('OwnershipFailed'));
  Result := Lowercase(Format('%.8x-%.4x-%.4x-' +
    '%.2x%.2x-%.2x%.2x%.2x%.2x%.2x%.2x', [Value.First, Value.Second, Value.Third, Value.Bytes[0], Value.Bytes[1], Value.Bytes[2],
      Value.Bytes[3], Value.Bytes[4], Value.Bytes[5], Value.Bytes[6], Value.Bytes[7]]));
end;

procedure RestoreEnvironment(const Name, Value: String);
begin
  if Value = '' then NativeEnvironmentClear(Name, 0) else NativeEnvironment(Name, Value);
end;

function RunInstaller(const Arguments: String; var Code: Integer; var Output: TExecOutput): Boolean;
var
  Root, BeforeRoot, BeforeX64: String;
  I: Integer;
begin
  Result := False;
  Code := 1;
  if (InstallerHelper = '') or HasLinkInPath(InstallerHelper) or not FileExists(InstallerHelper) then Exit;
  if CompareText(GetSHA256OfFile(InstallerHelper), '@@HELPER_SHA256@@') <> 0 then Exit;
  Root := RuntimeRoot;
  if HasLinkInPath(Root) or not FileExists(AddBackslash(Root) + 'dotnet.exe') then Exit;
  BeforeRoot := GetEnv('DOTNET_ROOT');
  BeforeX64 := GetEnv('DOTNET_ROOT_X64');
  try
    if not NativeEnvironment('DOTNET_ROOT', Root) or not NativeEnvironment('DOTNET_ROOT_X64', Root) then Exit;
    Result := ExecAndCaptureOutput(InstallerHelper, 'installer-state ' + Arguments,
      '', SW_HIDE, ewWaitUntilTerminated, Code, Output);
    if Result then begin
      for I := 0 to GetArrayLength(Output.StdOut) - 1 do Log(Output.StdOut[I]);
      for I := 0 to GetArrayLength(Output.StdErr) - 1 do Log(Output.StdErr[I]);
      Result := (Code = 0) and not Output.Error;
    end;
  finally
    RestoreEnvironment('DOTNET_ROOT', BeforeRoot);
    RestoreEnvironment('DOTNET_ROOT_X64', BeforeX64);
  end;
end;

function InstallerCall(const Arguments: String): Boolean;
var
  Code: Integer;
  Output: TExecOutput;
begin
  Result := RunInstaller(Arguments, Code, Output);
end;

procedure ReleaseGuard;
begin
  if InstallerGuard <> 0 then NativeClose(InstallerGuard);
  InstallerGuard := 0;
end;

procedure AcquireGuard(const Arguments: String; Uninstalling: Boolean);
var
  Code: Integer;
  Output: TExecOutput;
  Name: String;
begin
  if not RunInstaller(Arguments, Code, Output) then RaiseException(CustomMessage('OwnershipFailed'));
  if Uninstalling then begin
    if GetArrayLength(Output.StdOut) <> 2 then RaiseException(CustomMessage('OwnershipFailed'));
    if (Output.StdOut[1] <> 'choose') and (Output.StdOut[1] <> 'delete') then RaiseException(CustomMessage('OwnershipFailed'));
    RemovalPending := Output.StdOut[1] = 'delete';
  end else if GetArrayLength(Output.StdOut) <> 1 then RaiseException(CustomMessage('OwnershipFailed'));
  Name := Trim(Output.StdOut[0]);
  if Pos('Global\NexusPipeline.Installer.', Name) <> 1 then RaiseException(CustomMessage('OwnershipFailed'));
  InstallerGuard := NativeMutex(0, False, Name);
  if (InstallerGuard = 0) or (NativeError = 183) then begin
    ReleaseGuard;
    RaiseException(CustomMessage('OwnershipBusy'));
  end;
end;

function InitializeSetup: Boolean;
begin
  Result := InstallerAppId('') <> '';
end;

function PrepareOwnership: String;
var
  Root, Action: String;
  Code: Integer;
  Output: TExecOutput;
begin
  Result := '';
  Root := RemoveBackslashUnlessRoot(ExpandFileName(WizardDirValue));
  try
    if InstallerHelper = '' then begin
      ExtractTemporaryFile('nexus-installer-input.exe');
      InstallerHelper := ExpandConstant('{tmp}\nexus-installer-input.exe');
      ExtractTemporaryFile('nexus-installer-target.json');
      InstallerTarget := ExpandConstant('{tmp}\nexus-installer-target.json');
    end;
    if InstallerPrepared then begin
      if CompareText(InstallerRoot, Root) = 0 then begin
        if InstallerTransition then
          if not InstallerCall('preserve-transition --root "' + Root + '" --transaction ' + InstallerTransaction + TransitionWork) then
            RaiseException(CustomMessage('OwnershipPending'));
        Exit;
      end;
      Action := 'cancel-prepared';
      if InstallerTransition then Action := 'cancel-transition';
      if not InstallerCall(Action + ' --root "' + InstallerRoot + '" --transaction ' + InstallerTransaction +
          TransitionWork) then
        RaiseException(CustomMessage('OwnershipPending'));
      InstallerPrepared := False;
      ReleaseGuard;
    end;
    InstallerTransition := False;
    if not InstallerCall('recover-transition --root "' + Root + '" --work-root "' +
        ExpandConstant('{tmp}\nexus-transition-recovery') + '"') then
      RaiseException(CustomMessage('OwnershipPending'));
    if not RunInstaller('check-install --root "' + Root + '"', Code, Output) then begin
      if Code <> 10 then RaiseException(CustomMessage('OwnershipFailed'));
      InstallerTransition := True;
    end;
    AcquireGuard('guard-name --root "' + Root + '"', False);
    InstallerRoot := Root;
    InstallerTransaction := NewTransaction;
    Action := 'begin';
    if InstallerTransition then Action := 'begin-transition';
    if not InstallerCall(Action + ' --root "' + Root + '" --transaction ' + InstallerTransaction + ' --manifest @@MANIFEST_SHA256@@' +
        ' --manifest-file "' + InstallerTarget + '"' +
        ' --work-root "' + ExpandConstant('{tmp}\nexus-install-check') + '"') then
      RaiseException(CustomMessage('OwnershipFailed'));
    InstallerPrepared := True;
    if InstallerTransition then
      if not InstallerCall('preserve-transition --root "' + Root + '" --transaction ' + InstallerTransaction + TransitionWork) then
        RaiseException(CustomMessage('OwnershipPending'));
  except
    Result := GetExceptionMessage;
    if not InstallerPrepared then ReleaseGuard;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Action: String;
begin
  if CurStep <> ssPostInstall then Exit;
  Action := 'register --root "' + InstallerRoot + '" --transaction ' + InstallerTransaction;
  if InstallerTransition then
    Action := 'apply-transition --root "' + InstallerRoot + '" --transaction ' + InstallerTransaction +
      ' --payload-root "' + ExpandConstant('{tmp}\nexus-transition-payload') + '"' + TransitionWork;
  if not InstallerPrepared or not InstallerCall(Action) then
    RaiseException(CustomMessage('OwnershipPending'));
  InstallerQualified := True;
  ReleaseGuard;
end;

function GetCustomSetupExitCode: Integer;
begin
  Result := 0;
  if InstallerPrepared and not InstallerQualified then Result := 4;
end;

procedure DeinitializeSetup;
var
  Action: String;
begin
  try
    if InstallerPrepared and not InstallerQualified then begin
      Action := 'cancel-prepared';
      if InstallerTransition then Action := 'cancel-transition';
      if not InstallerCall(Action + ' --root "' + InstallerRoot + '" --transaction ' + InstallerTransaction + TransitionWork) then
        Log(CustomMessage('OwnershipPending'));
    end;
  finally
    ReleaseGuard;
  end;
end;

function InitializeUninstall: Boolean;
var
  Source, Entry: String;
  Choice: Integer;
begin
  Result := False;
  try
    if not HasFramework('Microsoft.NETCore.App', '10.0.0') then RaiseException(CustomMessage('OwnershipRuntime'));
    Source := AddBackslash(InstallerManager) + 'nexus-installer-worker.exe';
    if HasLinkInPath(Source) or not FileExists(Source) then RaiseException(CustomMessage('OwnershipFailed'));
    if CompareText(GetSHA256OfFile(Source), '@@HELPER_SHA256@@') <> 0 then RaiseException(CustomMessage('OwnershipFailed'));
    InstallerHelper := ExpandConstant('{tmp}\nexus-installer-remove.exe');
    if not FileCopy(Source, InstallerHelper, True) then RaiseException(CustomMessage('OwnershipFailed'));
    InstallerRoot := ExpandConstant('{app}');
    Entry := AddBackslash(InstallerRoot) + 'NexusPipeline.uninstall.exe';
    if DirExists(Entry) or HasLinkInPath(Entry) or
      (FileExists(Entry) and (CompareText(GetSHA256OfFile(Entry), '@@UNINSTALL_SHA256@@') <> 0)) then
      RaiseException(CustomMessage('OwnershipFailed'));
    AcquireGuard('uninstall-check --root "' + InstallerRoot + '"', True);
    DeleteInstanceData := RemovalPending;
    Choice := IDYES;
    if not UninstallSilent then begin
      if RemovalPending then
        Choice := MsgBox(CustomMessage('UninstallResumeData'), mbConfirmation, MB_OKCANCEL or MB_DEFBUTTON2)
      else
        Choice := MsgBox(CustomMessage('UninstallKeepData'), mbConfirmation, MB_YESNOCANCEL or MB_DEFBUTTON1);
      if (Choice = IDCANCEL) then begin ReleaseGuard; Exit; end;
      if not RemovalPending then DeleteInstanceData := Choice = IDNO;
    end;
    InstallerTransaction := NewTransaction;
    Result := True;
  except
    Log(GetExceptionMessage);
    if not UninstallSilent then MsgBox(GetExceptionMessage, mbError, MB_OK);
    ReleaseGuard;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Action: String;
begin
  if (CurUninstallStep = usPostUninstall) and DeleteInstanceData then begin
    if not InstallerCall('uninstall-finish --root "' + InstallerRoot + '" --transaction ' + InstallerTransaction) then
      RaiseException(CustomMessage('OwnershipPending'));
    Exit;
  end;
  if CurUninstallStep <> usUninstall then Exit;
  if DeleteInstanceData then Action := 'uninstall-delete' else Action := 'uninstall-keep';
  if not InstallerCall(Action + ' --root "' + InstallerRoot + '" --transaction ' + InstallerTransaction +
      ' --work-root "' + ExpandConstant('{tmp}\nexus-uninstall-check') + '"') then
    RaiseException(CustomMessage('OwnershipPending'));
end;

procedure DeinitializeUninstall;
begin
  ReleaseGuard;
end;
