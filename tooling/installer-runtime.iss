var
  DependencyDownloadPage: TDownloadWizardPage;

function NativeAttributes(Path: String): Cardinal;
  external 'GetFileAttributesW@kernel32.dll stdcall';

function HasLinkInPath(const Path: String): Boolean;
var
  Current, Parent: String;
  Attributes: Cardinal;
begin
  Result := True;
  Current := RemoveBackslashUnlessRoot(ExpandFileName(Path));
  while Current <> '' do begin
    Attributes := NativeAttributes(Current);
    if (Attributes <> $FFFFFFFF) and ((Attributes and FILE_ATTRIBUTE_REPARSE_POINT) <> 0) then Exit;
    Parent := ExtractFileDir(Current);
    if Parent = Current then Break;
    Current := Parent;
  end;
  Result := False;
end;

function RuntimeRoot: String;
begin
  if not RegQueryStringValue(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64', 'InstallLocation', Result) then begin
    Result := ExpandConstant('{commonpf64}\dotnet');
    if IsArm64 then Result := AddBackslash(Result) + 'x64';
  end;
  Result := RemoveBackslashUnlessRoot(ExpandFileName(Result));
end;

function ReadRuntimes(var Root: String; var Output: TExecOutput): Boolean;
var
  Dotnet: String;
  Code: Integer;
begin
  Result := False;
  if not IsX64Compatible then Exit;
  Root := RuntimeRoot;
  Dotnet := AddBackslash(Root) + 'dotnet.exe';
  if not FileExists(Dotnet) or HasLinkInPath(Dotnet) then Exit;
  try
    if not ExecAndCaptureOutput(Dotnet, '--list-runtimes', '', SW_HIDE, ewWaitUntilTerminated, Code, Output) then Exit;
  except
    Exit;
  end;
  Result := (Code = 0) and not Output.Error;
end;

function LatestFramework(const Root, Framework, Required: String; const Output: TExecOutput): String;
var
  Prefix, ExpectedPath, Line, VersionText: String;
  I, Marker: Integer;
  Version, Minimum, Maximum, Latest: Int64;
begin
  Result := '';
  if not StrToVersion(Required, Minimum) or not StrToVersion('10.1.0', Maximum) then Exit;
  ExpectedPath := AddBackslash(Root) + 'shared\' + Framework;
  if HasLinkInPath(ExpectedPath) then Exit;
  Latest := -1;
  Prefix := Framework + ' ';
  for I := 0 to GetArrayLength(Output.StdOut) - 1 do begin
    Line := Output.StdOut[I];
    Marker := Pos(' [', Line);
    if (Copy(Line, 1, Length(Prefix)) = Prefix) and (Marker > Length(Prefix)) then begin
      VersionText := Copy(Line, Length(Prefix) + 1, Marker - Length(Prefix) - 1);
      if StrToVersion(VersionText, Version) and (Version >= Minimum) and (Version < Maximum) and
         (Version > Latest) and
         (CompareText(Copy(Line, Marker + 2, Length(ExpectedPath)), ExpectedPath) = 0) and
         (Copy(Line, Marker + 2 + Length(ExpectedPath), MaxInt) = ']') then begin
        Result := VersionText;
        Latest := Version;
      end;
    end;
  end;
  if (Result <> '') and HasLinkInPath(AddBackslash(ExpectedPath) + Result) then Result := '';
end;

function FrameworkVersion(const Framework, Required: String): String;
var
  Root: String;
  Output: TExecOutput;
begin
  Result := '';
  if ReadRuntimes(Root, Output) then Result := LatestFramework(Root, Framework, Required, Output);
end;

function HasFramework(const Framework, Required: String): Boolean;
begin
  Result := FrameworkVersion(Framework, Required) <> '';
end;

function NewerVersion(const Left, Right: String): Boolean;
var
  A, B: Int64;
begin
  Result := StrToVersion(Left, A) and StrToVersion(Right, B) and (A > B);
end;

function VerifyMicrosoftSignature(const Package: String): Boolean;
var
  Script, Params: String;
  Code, I: Integer;
  Output: TExecOutput;
begin
  Result := False;
  Script := ExpandConstant('{tmp}\nexus-runtime-signature.ps1');
  if not SaveStringToFile(Script,
    'param([string]$Package)' + #13#10 +
    '$ErrorActionPreference = ''Stop''' + #13#10 +
    '[Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)' + #13#10 +
    'try {' + #13#10 +
    'Import-Module (Join-Path $PSHOME ''Modules\Microsoft.PowerShell.Security\Microsoft.PowerShell.Security.psd1'') -ErrorAction Stop' + #13#10 +
    '$s = Get-AuthenticodeSignature -LiteralPath $Package' + #13#10 +
    'Write-Output (''Runtime signature status='' + $s.Status.ToString())' + #13#10 +
    'if ($s.Status -ne ''Valid'' -or $s.SignerCertificate.Subject -notmatch ''(^|,\s*)O=Microsoft Corporation(,|$)'') { exit 1 }' + #13#10 +
    '} catch { Write-Output $_.Exception.Message; exit 1 }' + #13#10, False) then Exit;
  Params := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + Script + '" -Package "' + Package + '"';
  Result := ExecAndCaptureOutput(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Params,
    '', SW_HIDE, ewWaitUntilTerminated, Code, Output);
  Log('Runtime signature verifier launched=' + IntToStr(Ord(Result)) + ' code=' + IntToStr(Code));
  for I := 0 to GetArrayLength(Output.StdOut) - 1 do Log(Output.StdOut[I]);
  for I := 0 to GetArrayLength(Output.StdErr) - 1 do Log(Output.StdErr[I]);
  Result := Result and (Code = 0) and not Output.Error;
end;

function InstallFramework(const Framework, Required, Available, Description, URL, FileName, Digest: String;
  var NeedsRestart: Boolean): String;
var
  Package: String;
  Code: Integer;
begin
  Result := '';
  if HasFramework(Framework, Required) then Exit;
  if NewerVersion(Required, Available) then begin
    Result := FmtMessage(CustomMessage('RuntimeManual'), [Description]);
    Exit;
  end;
  if WizardSilent then begin
    Result := FmtMessage(CustomMessage('RuntimeSilent'), [Description]);
    Exit;
  end;
  if MsgBox(FmtMessage(CustomMessage('RuntimePrompt'), [Description]), mbConfirmation, MB_YESNO) <> IDYES then begin
    Result := FmtMessage(CustomMessage('RuntimeCancelled'), [Description]);
    Exit;
  end;
  DependencyDownloadPage.Clear;
  DependencyDownloadPage.Add(URL, FileName, Digest);
  DependencyDownloadPage.Show;
  try
    try
      DependencyDownloadPage.Download;
    except
      if DependencyDownloadPage.AbortedByUser then Result := FmtMessage(CustomMessage('RuntimeCancelled'), [Description])
      else Result := FmtMessage(CustomMessage('RuntimeDownloadFailed'), [Description, GetExceptionMessage]);
    end;
  finally
    DependencyDownloadPage.Hide;
  end;
  if Result <> '' then Exit;
  Package := ExpandConstant('{tmp}\') + FileName;
  if HasLinkInPath(Package) or (CompareText(GetSHA256OfFile(Package), Digest) <> 0) then begin
    Result := FmtMessage(CustomMessage('RuntimeDigestFailed'), [Description]);
    Exit;
  end;
  if not VerifyMicrosoftSignature(Package) or (CompareText(GetSHA256OfFile(Package), Digest) <> 0) then begin
    Result := FmtMessage(CustomMessage('RuntimeSignatureFailed'), [Description]);
    Exit;
  end;
  if not ShellExec('runas', Package, '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, Code) then begin
    Result := FmtMessage(CustomMessage('RuntimeLaunchFailed'), [Description, SysErrorMessage(Code)]);
    Exit;
  end;
  if Code = 3010 then begin
    NeedsRestart := True;
    Result := FmtMessage(CustomMessage('RuntimeRestart'), [Description]);
    Exit;
  end;
  if Code <> 0 then Result := FmtMessage(CustomMessage('RuntimeExitFailed'), [Description, IntToStr(Code)])
  else if not HasFramework(Framework, Required) then Result := FmtMessage(CustomMessage('RuntimeStillMissing'), [Description]);
end;

function WebCoreMinimum(const Root, WebVersion: String): String;
var
  Config, Script, Params: String;
  Code: Integer;
  Output: TExecOutput;
begin
  Result := '';
  Config := AddBackslash(Root) + 'shared\Microsoft.AspNetCore.App\' + WebVersion + '\Microsoft.AspNetCore.App.runtimeconfig.json';
  if not FileExists(Config) or HasLinkInPath(Config) then Exit;
  Script := ExpandConstant('{tmp}\nexus-runtime-requirement.ps1');
  if not SaveStringToFile(Script,
    'param([string]$Config)' + #13#10 +
    '$ErrorActionPreference = ''Stop''' + #13#10 +
    'Import-Module (Join-Path $PSHOME ''Modules\Microsoft.PowerShell.Utility\Microsoft.PowerShell.Utility.psd1'')' + #13#10 +
    '$options = (ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($Config))).runtimeOptions' + #13#10 +
    '$core = $options.framework' + #13#10 +
    'if ($options.tfm -cne ''net10.0'' -or $options.rollForward -cne ''LatestPatch'' -or' + #13#10 +
    '    $core.name -cne ''Microsoft.NETCore.App'' -or $core.version -cnotmatch ''^10\.0\.[0-9]+$'') { exit 1 }' + #13#10 +
    '[Console]::WriteLine($core.version)' + #13#10, False) then Exit;
  Params := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + Script + '" -Config "' + Config + '"';
  try
    if not ExecAndCaptureOutput(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Params,
      '', SW_HIDE, ewWaitUntilTerminated, Code, Output) then Exit;
  except
    Exit;
  end;
  if (Code = 0) and not Output.Error and (GetArrayLength(Output.StdOut) = 1) then
    Result := Output.StdOut[0];
end;

function RequiredCore(const WebMinimum: String): String;
begin
  Result := '@@CORE_MINIMUM@@';
  if NewerVersion(WebMinimum, Result) then Result := WebMinimum;
end;

function PrepareFrameworks(var NeedsRestart: Boolean): String;
var
  Root, WebVersion, WebMinimum, CoreVersion, CoreDescription: String;
  Output: TExecOutput;
begin
  WebVersion := '';
  if ReadRuntimes(Root, Output) then
    WebVersion := LatestFramework(Root, 'Microsoft.AspNetCore.App', '@@WEB_MINIMUM@@', Output);
  if WebVersion = '' then begin
    WebVersion := '@@WEB_VERSION@@';
    WebMinimum := WebVersion;
  end else WebMinimum := WebCoreMinimum(Root, WebVersion);
  if WebMinimum = '' then begin
    Result := FmtMessage(CustomMessage('RuntimeFrameworkInvalid'), [WebVersion]);
    Exit;
  end;
  CoreVersion := RequiredCore(WebMinimum);
  CoreDescription := '.NET Core Runtime 10 x64 (>= ' + CoreVersion + ')';
  Log('Runtime prerequisite ASP.NET Core=' + WebVersion + ', minimum Core=' + CoreVersion);
  Result := @@CORE_INSTALL@@;
  if Result <> '' then Exit;
  Result := @@WEB_INSTALL@@;
  if Result <> '' then Exit;
  if ReadRuntimes(Root, Output) then begin
    WebVersion := LatestFramework(Root, 'Microsoft.AspNetCore.App', '@@WEB_MINIMUM@@', Output);
    if WebVersion <> '' then begin
      WebMinimum := WebCoreMinimum(Root, WebVersion);
      if WebMinimum = '' then begin
        Result := FmtMessage(CustomMessage('RuntimeFrameworkInvalid'), [WebVersion]);
        Exit;
      end;
      CoreVersion := LatestFramework(Root, 'Microsoft.NETCore.App', RequiredCore(WebMinimum), Output);
      if CoreVersion <> '' then begin
        Log('Runtime prerequisites resolved ASP.NET Core=' + WebVersion + ', Core=' + CoreVersion);
        Exit;
      end;
    end;
  end;
  Result := FmtMessage(CustomMessage('RuntimeStillMissing'), ['.NET Core / ASP.NET Core Runtime 10 x64']);
end;
