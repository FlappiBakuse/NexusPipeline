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
    RaiseException('安装元数据 ' + Action + ' 未通过；保留检查点，错误码 ' + IntToStr(Code) + '。');
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
    RaiseException('安装元数据补偿进程未启动；保留检查点，原生错误 ' + IntToStr(Result) + '。');
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
    if Code = 1223 then Result := '授权请求已取消'
    else if Code = 5 then Result := '访问被拒绝'
    else if Code = 2 then Result := '交接程序不存在'
    else if Code = 32 then Result := '交接程序被占用'
    else Result := '交接程序无法启动，系统错误 ' + IntToStr(Code);
  end else
    Result := '交接进程退出码 ' + IntToStr(Code);
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
    RaiseException(InstallerLaunchFailure(Launched, Code) + '；保留 staging 和事务现场。');
  if Resolution <> 0 then
    RaiseException('升级结果与已提交的元数据不一致；保留检查点。');
end;
