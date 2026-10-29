#ifndef AppVersion
  #define AppVersion "0.1.1"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\channels\cn"
#endif
#ifndef OutputName
  #define OutputName "BigFishSetup-CN"
#endif
[Setup]
AppId={{484E381B-BC96-40A3-A1D8-663CEB33D41A}
AppName=BigFish Installer
AppVersion={#AppVersion}
AppPublisher=BigFish Installer contributors
DefaultDirName={localappdata}\Programs\BigFish
DefaultGroupName=BigFish Installer
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.19045
OutputDir=..\artifacts\release
OutputBaseFilename={#OutputName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\BigFish.exe
LicenseFile=..\LICENSE
CloseApplications=yes
RestartApplications=no
DisableProgramGroupPage=yes
ShowLanguageDialog=no

[Languages]
Name: "chinesesimp"; MessagesFile: "Languages\ChineseSimplified.isl"

[Files]
Source: "{#PublishDir}\BigFish.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\docs\*"; DestDir: "{app}\docs"; Flags: ignoreversion recursesubdirs
Source: "..\third-party\*"; DestDir: "{app}\third-party"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{group}\BigFish Installer"; Filename: "{app}\BigFish.exe"
Name: "{userdesktop}\BigFish Installer"; Filename: "{app}\BigFish.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; Flags: unchecked

[Run]
Filename: "{app}\BigFish.exe"; Description: "启动大肥鱼安装器"; Flags: nowait postinstall skipifsilent

; No UninstallDelete entries: workspace, DSH home, credentials and sessions remain intact.
