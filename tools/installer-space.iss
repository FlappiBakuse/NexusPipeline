type
  TSpaceVolume = record
    Root: String;
    Additional, Available: Int64;
  end;

var
  InstanceProbeKey, InstanceProbeState: String;
  InstanceOldCoreBytes, InstanceOldAllocatedBytes: Int64;
  SpaceVolumes: array of TSpaceVolume;
  ProgramBytes, ProgramAllocated, RecordBytes: Int64;
  SpaceDownloadBytes: Int64;
  SpaceDependenciesChecked: Boolean;

function GetDiskFreeSpaceW(Root: String; var Sectors, Bytes, Free, Total: Cardinal): Boolean;
  external 'GetDiskFreeSpaceW@kernel32.dll stdcall';
function NativeTickCount: Cardinal;
  external 'GetTickCount@kernel32.dll stdcall';

function AllocationUnit(const Path: String): Int64;
var
  Sectors, Bytes, Free, Total: Cardinal;
begin
  if not GetDiskFreeSpaceW(AddBackslash(ExtractFileDrive(Path)), Sectors, Bytes, Free, Total) then
    RaiseException('installer.space_volume');
  Result := Int64(Sectors) * Bytes;
  if Result <= 0 then RaiseException('installer.space_volume');
end;

function Allocated(Bytes, UnitBytes: Int64): Int64;
begin
  Result := ((Bytes + UnitBytes - 1) div UnitBytes) * UnitBytes;
end;

procedure AddSpace(const Path: String; Bytes: Int64);
var
  Root: String;
  I: Integer;
  Total: Int64;
begin
  Root := AddBackslash(ExtractFileDrive(Path));
  for I := 0 to GetArrayLength(SpaceVolumes) - 1 do
    if CompareText(SpaceVolumes[I].Root, Root) = 0 then begin
      SpaceVolumes[I].Additional := SpaceVolumes[I].Additional + Bytes;
      Exit;
    end;
  I := GetArrayLength(SpaceVolumes);
  SetArrayLength(SpaceVolumes, I + 1);
  SpaceVolumes[I].Root := Root;
  SpaceVolumes[I].Additional := Bytes;
  if not GetSpaceOnDisk64(Root, SpaceVolumes[I].Available, Total) then
    RaiseException('installer.space_volume');
end;

function UninstallerSize(const Folder: String): Int64;
var
  Entry: TFindRec;
  Bytes: Int64;
  Path: String;
begin
  Result := 0;
  if not DirExists(Folder) then Exit;
  if FindFirst(AddBackslash(Folder) + 'unins*', Entry) then begin
    try
      repeat
        if (Entry.Attributes and FILE_ATTRIBUTE_DIRECTORY) = 0 then begin
          Path := AddBackslash(Folder) + Entry.Name;
          if HasLinkInPath(Path) or not FileSize64(Path, Bytes) then RaiseException('installer.space_metadata');
          Result := Result + Allocated(Bytes, AllocationUnit(Folder));
        end;
      until not FindNext(Entry);
    finally
      FindClose(Entry);
    end;
  end;
end;

procedure AddPayloadSpace(const Path: String; Bytes: Int64; Plugin: Boolean);
begin
  // Inno records contain UTF-16 paths and flags; reserve a bound per frozen file.
  RecordBytes := RecordBytes + 4096 + 4 * (Length(WizardDirValue) + Length(Path) + 260);
  if Plugin and (not FirstInstall or not WizardIsTaskSelected('bundledplugins')) then Exit;
  ProgramBytes := ProgramBytes + Bytes;
  ProgramAllocated := ProgramAllocated + Allocated(Bytes, AllocationUnit(WizardDirValue));
end;

procedure CalculateSpace;
var
  Manager, Report, Summary: String;
  OldUninstall, RootUninstall, AppBytes, ManagerBytes, TemporaryBytes: Int64;
  I: Integer;
  Enough: Boolean;
begin
  ProgramBytes := 0;
  ProgramAllocated := 0;
  RecordBytes := 0;
  SetArrayLength(SpaceVolumes, 0);
@@SPACE_FILES@@
  Manager := ExpandConstant('{localappdata}\NexusPipeline.Generations\@@GENERATION@@\installer');
  OldUninstall := UninstallerSize(Manager);
  RootUninstall := UninstallerSize(WizardDirValue);
  AppBytes := ProgramAllocated + InstanceOldAllocatedBytes + @@UNINSTALLER_BYTES@@ * 2 + RecordBytes + OldUninstall;
  // The retained uninstall helper is the production Host copy, not the temporary metadata tool.
  ManagerBytes := OldUninstall + RootUninstall + Allocated(@@CORE_BYTES@@, AllocationUnit(Manager)) + RecordBytes + 4 * 1024 * 1024;
  TemporaryBytes := @@SETUP_WORK_BYTES@@ + Allocated(@@METADATA_HELPER_BYTES@@, AllocationUnit(ExpandConstant('{tmp}'))) + SpaceDownloadBytes;
  AddSpace(WizardDirValue, AppBytes);
  AddSpace(Manager, ManagerBytes);
  AddSpace(ExpandConstant('{tmp}'), TemporaryBytes);
  Summary := '';
  Enough := True;
  Report := ExpandConstant('{tmp}\nxp-space.ini');
  if FileExists(Report) and not DeleteFile(Report) then RaiseException('installer.space_report');
  SetIniString('space', 'programBytes', IntToStr(ProgramBytes), Report);
  SetIniString('space', 'oldCoreBytes', IntToStr(InstanceOldCoreBytes), Report);
  SetIniString('space', 'permanentHelperBytes', '@@CORE_BYTES@@', Report);
  SetIniString('space', 'temporaryHelperBytes', '@@METADATA_HELPER_BYTES@@', Report);
  SetIniString('space', 'recordBytes', IntToStr(RecordBytes), Report);
  SetIniString('space', 'appBytes', IntToStr(AppBytes), Report);
  SetIniString('space', 'managerBytes', IntToStr(ManagerBytes), Report);
  SetIniString('space', 'temporaryBytes', IntToStr(TemporaryBytes), Report);
  SetIniString('space', 'downloadBytes', IntToStr(SpaceDownloadBytes), Report);
  SetIniString('space', 'volumeCount', IntToStr(GetArrayLength(SpaceVolumes)), Report);
  for I := 0 to GetArrayLength(SpaceVolumes) - 1 do begin
    if Summary <> '' then Summary := Summary + '; ';
    Summary := Summary + SpaceVolumes[I].Root + ' ' + IntToStr((SpaceVolumes[I].Additional + 1048575) div 1048576) + ' MiB';
    if SpaceVolumes[I].Available < SpaceVolumes[I].Additional then Enough := False;
    SetIniString('volume' + IntToStr(I), 'root', SpaceVolumes[I].Root, Report);
    SetIniString('volume' + IntToStr(I), 'additionalBytes', IntToStr(SpaceVolumes[I].Additional), Report);
    SetIniString('volume' + IntToStr(I), 'availableBytes', IntToStr(SpaceVolumes[I].Available), Report);
  end;
  SetIniString('space', 'enough', IntToStr(Ord(Enough)), Report);
  SetIniString('space', 'summary', Summary, Report);
  SpaceSummary := FmtMessage(CustomMessage('SpaceSummary'), [IntToStr((ProgramBytes + 1048575) div 1048576), Summary]);
  Log('installer space programBytes=' + IntToStr(ProgramBytes) + ' appBytes=' + IntToStr(AppBytes) + ' managerBytes=' + IntToStr(ManagerBytes) + ' temporaryBytes=' + IntToStr(TemporaryBytes));
end;
