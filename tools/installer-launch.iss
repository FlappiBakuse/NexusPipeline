function RunInstallerMetadata(const Action, Extra: String; var Code: Integer): Boolean;
var
  Params: String;
begin
  Params := 'installer-state metadata-' + Action + ' --root "' + ExpandConstant('{app}') +
    '" --transaction ' + TransactionId + Extra;
  Result := ShellExec('', MetadataHelperPath, Params, ExpandConstant('{app}'),
    SW_HIDE, ewWaitUntilTerminated, Code);
  Log('installer metadata action=' + Action + ' launched=' + IntToStr(Ord(Result)) +
    ' code=' + IntToStr(Code) + ' transaction=' + TransactionId);
end;

procedure RequireInstallerMetadata(const Action, Extra: String);
var
  Code: Integer;
begin
  Code := 0;
  if not RunInstallerMetadata(Action, Extra, Code) or (Code <> 0) then
    RaiseException(FmtMessage(CustomMessage('MetadataFailed'), [Action, IntToStr(Code)]));
end;

function ResolveInstallerMetadata(Launched: Boolean; ChildExit: Integer;
  Registration: Boolean): Integer;
var
  Extra: String;
begin
  if Launched then Extra := ' --launched true' else Extra := ' --launched false';
  Extra := Extra + ' --child-exit ' + IntToStr(ChildExit);
  if Registration then Extra := Extra + ' --registration true'
  else Extra := Extra + ' --registration false';
  Result := 0;
  if not RunInstallerMetadata('resolve', Extra, Result) then
    RaiseException(FmtMessage(CustomMessage('MetadataCompensationFailed'), [IntToStr(Result)]));
end;

function LaunchInstallerHelper(const Helper, Params, Path: String; var Code: Integer): Boolean;
begin
#ifdef NEXUS_INSTALLER_TEST
  Result := ContractLaunch(Code);
#else
  Result := ShellExec('runas', Helper, Params, Path, SW_HIDE, ewWaitUntilTerminated, Code);
#endif
  Log('installer worker launched=' + IntToStr(Ord(Result)) + ' code=' +
    IntToStr(Code) + ' transaction=' + TransactionId);
end;

function InstallerLaunchFailure(Launched: Boolean; Code: Integer): String;
begin
  if not Launched then begin
    if Code = 1223 then Result := CustomMessage('ElevationCancelled')
    else if Code = 5 then Result := CustomMessage('AccessDenied')
    else if Code = 2 then Result := CustomMessage('HandoffMissing')
    else if Code = 32 then Result := CustomMessage('HandoffBusy')
    else Result := FmtMessage(CustomMessage('HandoffSystemError'), [IntToStr(Code)]);
  end else
    Result := FmtMessage(CustomMessage('HandoffExitCode'), [IntToStr(Code)]);
end;

procedure ApplyInstallerLaunch(const Helper, Params, Path: String;
  var Launched, Resolved: Boolean; var Code: Integer);
var
  Resolution: Integer;
begin
  Launched := LaunchInstallerHelper(Helper, Params, Path, Code);
  Resolution := ResolveInstallerMetadata(Launched, Code, False);
  Resolved := True;
  if not Launched or (Code <> 0) then
    RaiseException(FmtMessage(CustomMessage('HandoffRetained'), [InstallerLaunchFailure(Launched, Code)]));
  if Resolution <> 0 then
    RaiseException(CustomMessage('UpgradeMetadataMismatch'));
end;
