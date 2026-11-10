[Setup]
AppId=@@APP_ID@@
AppName=NexusPipeline
AppVersion=@@VERSION@@
AppVerName=NexusPipeline v@@VERSION@@
AppPublisher=FlappiBakuse
DefaultDirName={localappdata}\NexusPipeline-0.18
DefaultGroupName=NexusPipeline
UninstallDisplayIcon={app}\NexusPipeline.exe
UninstallFilesDir={app}\uninstall
Uninstallable=NewInstallation
PrivilegesRequired=lowest
MinVersion=10.0.22621
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
AllowUNCPath=no
AllowNetworkDrive=no
CloseApplications=no
RestartApplications=no
UsePreviousAppDir=no
UsePreviousGroup=no
UsePreviousLanguage=@@PREVIOUS_LANGUAGE@@
DisableProgramGroupPage=yes
OutputDir=@@OUTPUT@@
OutputBaseFilename=NexusPipeline-v@@VERSION@@-win-x64-Setup
Compression=lzma2
SolidCompression=yes
MergeDuplicateFiles=yes
WizardStyle=modern
DefaultDialogFontName=Microsoft YaHei UI
LanguageDetectionMethod=uilanguage
ShowLanguageDialog=yes

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "zh"; MessagesFile: "@@CHINESE@@"

[CustomMessages]
en.Launch=Start NexusPipeline
zh.Launch=启动 NexusPipeline
en.DesktopIcon=Create a desktop shortcut
zh.DesktopIcon=创建桌面快捷方式
en.RuntimeDownloadTitle=Installing required components
zh.RuntimeDownloadTitle=安装所需组件
en.RuntimeDownloadDetail=Downloading an official Microsoft shared runtime.
zh.RuntimeDownloadDetail=正在下载微软官方共享运行库。
en.RuntimePrompt=NexusPipeline needs %1. Download and install the official Microsoft component now? It remains available to other applications after NexusPipeline is removed.
zh.RuntimePrompt=NexusPipeline 需要 %1。现在下载并安装微软官方组件吗？卸载 NexusPipeline 时会保留共享运行库，供其他软件使用。
en.RuntimeSilent=%1 is missing. Install the required x64 runtime, then run Setup again.
zh.RuntimeSilent=缺少 %1。请安装所需的 x64 运行库后重新运行安装程序。
en.RuntimeCancelled=Installation of %1 was cancelled. Application files have not been installed.
zh.RuntimeCancelled=已取消安装 %1，尚未安装应用文件。
en.RuntimeDownloadFailed=Could not download %1: %2
zh.RuntimeDownloadFailed=下载 %1 失败：%2
en.RuntimeDigestFailed=The downloaded %1 does not match its verified package. Run Setup again.
zh.RuntimeDigestFailed=下载的 %1 与已核验安装包不符，请重新运行安装程序。
en.RuntimeSignatureFailed=The Microsoft signature of %1 could not be verified. Application installation was stopped.
zh.RuntimeSignatureFailed=未通过 %1 的微软签名校验，已停止安装应用。
en.RuntimeLaunchFailed=Could not start the %1 installer: %2
zh.RuntimeLaunchFailed=无法启动 %1 安装程序：%2
en.RuntimeExitFailed=The %1 installer failed with code %2. Application files have not been installed.
zh.RuntimeExitFailed=%1 安装程序失败，代码 %2。尚未安装应用文件。
en.RuntimeRestart=%1 requires a restart. Restart Windows, then run Setup again.
zh.RuntimeRestart=%1 需要重启。请重启 Windows 后重新运行安装程序。
en.RuntimeStillMissing=%1 is still unavailable after installation. Application files have not been installed.
zh.RuntimeStillMissing=安装后仍未检测到 %1，尚未安装应用文件。
en.OwnershipFailed=Installation ownership could not be verified. Close NexusPipeline and retry without changing existing files.
zh.OwnershipFailed=未通过安装归属校验。请关闭 NexusPipeline 后重试，并保留现有文件。
en.OwnershipBusy=Another installer is using this installation. Wait for it to finish, then retry.
zh.OwnershipBusy=其他安装程序正在使用此目录，请等待完成后重试。
en.OwnershipPending=Installation recovery needs attention. Preserve this directory and its diagnostic evidence before retrying.
zh.OwnershipPending=安装恢复需要处理。重试前请保留此目录和诊断材料。
en.OwnershipRuntime=The required .NET 10 x64 runtime is unavailable. Restore it, then retry uninstalling.
zh.OwnershipRuntime=缺少所需的 .NET 10 x64 运行库，请恢复运行库后重试卸载。
en.UninstallKeepData=Keep your settings, accounts, plugins and history for reinstalling later? Choose No to delete this installation's data, or Cancel to keep NexusPipeline installed.
zh.UninstallKeepData=是否保留设置、账号、插件和历史记录，以便以后重新安装？选择“否”将删除此安装的数据，选择“取消”将保留 NexusPipeline。
en.UninstallResumeData=Data deletion was interrupted. Continue deleting this installation's remaining data? Cancel leaves the remaining files untouched.
zh.UninstallResumeData=上次删除数据未完成，是否继续删除此安装的剩余数据？取消将保留剩余文件。

[Tasks]
Name: desktopicon; Description: "{cm:DesktopIcon}"; Flags: unchecked

[Files]
@@FILES@@

[Icons]
Name: "{code:InstallerGroup}\NexusPipeline v@@VERSION@@"; Filename: "{app}\NexusPipeline.exe"; Check: NewInstallation
Name: "{code:InstallerGroup}\NexusPipeline Uninstall"; Filename: "{app}\NexusPipeline.uninstall.exe"; Check: NewInstallation
Name: "{code:InstallerDesktop}\NexusPipeline v@@VERSION@@"; Filename: "{app}\NexusPipeline.exe"; Tasks: desktopicon; Check: NewInstallation

[Run]
Filename: "{app}\NexusPipeline.exe"; Description: "{cm:Launch}"; Flags: nowait postinstall skipifsilent unchecked

[Code]
@@RUNTIME_CODE@@
@@LIFECYCLE_CODE@@

procedure InitializeWizard;
begin
  DependencyDownloadPage := CreateDownloadPage(CustomMessage('RuntimeDownloadTitle'), CustomMessage('RuntimeDownloadDetail'), nil);
  DependencyDownloadPage.ShowBaseNameInsteadOfUrl := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := PrepareFrameworks(NeedsRestart);
  if Result = '' then Result := PrepareOwnership;
end;
