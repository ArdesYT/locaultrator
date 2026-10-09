#define MyAppName "LocaUltrator"
#define MyAppVersion "0.8.7"
#define MyAppExeName "LocaUltrator.exe"
#define MyPortableDir "..\artifacts\portable-" + MyAppVersion

[Setup]
AppId={{A42E21BC-0BF4-4BC9-A7E8-6A3CA812E80E}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=LocaUltrator Project
AppComments=Natív játéklokalizáció Gemini API-val és mentett vágólapos munkamóddal
AppReadmeFile={app}\README_HU.md
DefaultDirName={code:GetInstallDirectory}
DefaultGroupName=LocaUltrator
DisableProgramGroupPage=auto
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\release
OutputBaseFilename=LocaUltrator_{#MyAppVersion}_Setup_GUIs_win-x64
SetupIconFile=..\src\LocaUltrator.App\Assets\LocaUltrator.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName=LocaUltrator
UsePreviousAppDir=yes
UsePreviousGroup=yes
UsePreviousLanguage=yes
UsePreviousTasks=yes
CloseApplications=force
CloseApplicationsFilter={#MyAppExeName}
RestartApplications=no
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern dynamic windows11 includetitlebar
WizardSizePercent=110
SetupLogging=yes
VersionInfoVersion=0.8.7.0
VersionInfoProductVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
VersionInfoDescription=LocaUltrator grafikus Windows telepítő
MinVersion=10.0.17763

[Languages]
Name: "hungarian"; MessagesFile: "compiler:Languages\Hungarian.isl"

[Tasks]
Name: "desktopicon"; Description: "Asztali parancsikon létrehozása"; GroupDescription: "Parancsikonok:"; Flags: unchecked

[Files]
Source: "{#MyPortableDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion restartreplace
Source: "{#MyPortableDir}\README_HU.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#MyPortableDir}\API_BIZTONSAG_HU.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#MyPortableDir}\VALTOZASOK_0.8.7_HU.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\LocaUltrator"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{group}\LocaUltrator eltávolítása"; Filename: "{uninstallexe}"
Name: "{autodesktop}\LocaUltrator"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "LocaUltrator elindítása"; Flags: nowait postinstall skipifsilent

[Registry]
Root: HKCU; Subkey: "Software\LocaUltrator"; ValueType: string; ValueName: "InstallLocation"; ValueData: "{app}"; Flags: uninsdeletevalue uninsdeletekeyifempty
Root: HKCU; Subkey: "Software\LocaUltrator"; ValueType: string; ValueName: "Version"; ValueData: "{#MyAppVersion}"; Flags: uninsdeletevalue

[UninstallDelete]
Type: dirifempty; Name: "{app}"

[Code]
var
  IsUpdate: Boolean;

function GetInstallDirectory(Param: String): String;
var
  ExistingDirectory: String;
begin
  if RegQueryStringValue(HKCU, 'Software\LocaUltrator', 'InstallLocation', ExistingDirectory) and
     (Trim(ExistingDirectory) <> '') and FileExists(AddBackslash(ExistingDirectory) + '{#MyAppExeName}') then
  begin
    Result := ExistingDirectory;
    IsUpdate := True;
  end
  else
  begin
    Result := ExpandConstant('{localappdata}\Programs\LocaUltrator');
    IsUpdate := False;
  end;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := IsUpdate and (PageID = wpSelectDir);
end;

procedure InitializeWizard();
var
  ExistingDirectory: String;
begin
  IsUpdate := RegQueryStringValue(HKCU, 'Software\LocaUltrator', 'InstallLocation', ExistingDirectory) and
    (Trim(ExistingDirectory) <> '') and FileExists(AddBackslash(ExistingDirectory) + '{#MyAppExeName}');
  if IsUpdate then
  begin
    WizardForm.WelcomeLabel1.Caption := 'LocaUltrator frissítése';
    WizardForm.WelcomeLabel2.Caption :=
      'A telepítő megtalálta a korábbi példányt. A programot ugyanabban a mappában frissíti, a projektek és beállítások megtartásával.';
  end;
end;
