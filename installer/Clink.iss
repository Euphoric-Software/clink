; Clink installer (Inno Setup 6). Build: ISCC.exe /DAppVersion=0.4.2 [/DAppFileVersion=0.4.2.0] [/DSourceExe=path] [/DOutDir=path] installer\Clink.iss
; Per-user install (no admin) into %LOCALAPPDATA%\Programs\Clink. Needs Inno Setup 6.3+ (x64compatible). The same AppId makes a newer setup upgrade in place.
; Fixed per-user location (no directory page). Uninstall removes only the program, the autostart entry and %LOCALAPPDATA%\Clink\updates
; (downloads); it never touches %APPDATA%\Clink (settings) or clips.

#ifndef AppVersion
  #error Pass the version: ISCC.exe /DAppVersion=X.Y.Z installer\Clink.iss
#endif
#ifndef AppFileVersion
  #define AppFileVersion "0.0.0.0"
#endif
#ifndef SourceExe
  #define SourceExe "..\dist\Clink.exe"
#endif
#ifndef OutDir
  #define OutDir "..\dist\installer"
#endif

[Setup]
; Fixed forever: changing it breaks upgrade-in-place. Doubled brace is Inno's escape for a literal brace.
AppId={{7D4A9E3B-5C21-4F6A-9B08-2E1C6A5D3F74}
AppName=Clink
AppVersion={#AppVersion}
AppPublisher=Euphoric Software
AppPublisherURL=https://euphoric.software
VersionInfoVersion={#AppFileVersion}
VersionInfoProductName=Clink
DefaultDirName={autopf}\Clink
DefaultGroupName=Clink
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
AppMutex=Clink.SingleInstance.6F1B
CloseApplications=yes
RestartApplications=no
SetupIconFile=..\Clink\Assets\clink.ico
UninstallDisplayIcon={app}\Clink.exe
UninstallDisplayName=Clink
OutputDir={#OutDir}
OutputBaseFilename=Clink-{#AppVersion}-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; DestName: "Clink.exe"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Clink"; Filename: "{app}\Clink.exe"
Name: "{autodesktop}\Clink"; Filename: "{app}\Clink.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Clink.exe"; Description: "{cm:LaunchProgram,Clink}"; Flags: nowait postinstall skipifsilent unchecked

[UninstallDelete]
Type: files; Name: "{app}\Clink.exe.bak"
Type: files; Name: "{app}\Clink.exe.new"
Type: files; Name: "{app}\Clink.exe.failed"
; Downloaded updates only. Never %APPDATA%\Clink (settings) or the clips folder.
Type: filesandordirs; Name: "{localappdata}\Clink\updates"

[Code]
const
  DotNetUrl = 'https://dotnet.microsoft.com/download/dotnet/10.0';

function DirHas10(const Pattern: string): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if FindFirst(ExpandConstant(Pattern), FindRec) then
  try
    Result := True;
  finally
    FindClose(FindRec);
  end;
end;

function HasDesktopRuntime10: Boolean;
var
  Names: TArrayOfString;
  I: Integer;
begin
  Result := False;
  // The .NET installers register in the 32-bit registry view (HKLM\SOFTWARE\WOW6432Node) even for the x64 runtime.
  if RegGetValueNames(HKLM32, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if Copy(Names[I], 1, 3) = '10.' then
      begin
        Result := True;
        Exit;
      end;
  // Folder fallbacks (zip installs, registry not written).
  if DirHas10('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*') then
  begin
    Result := True;
    Exit;
  end;
  if DirHas10('{commonpf64}\dotnet\x64\shared\Microsoft.WindowsDesktop.App\10.*') then
    Result := True;
end;
function InitializeSetup: Boolean;
var
  ErrCode: Integer;
begin
  Result := True;
  if not HasDesktopRuntime10 then
  begin
    if WizardSilent then
      Log('.NET Desktop Runtime 10 (x64) not found; continuing (silent).')
    else if MsgBox('Clink needs the .NET Desktop Runtime 10 (x64), which was not found on this PC.' + #13#10 + #13#10 +
        'Choose Yes to open the download page (setup then continues), or No to continue without it. ' +
        'Clink will not start until the runtime is installed.', mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', DotNetUrl, '', '', SW_SHOWNORMAL, ewNoWait, ErrCode);
  end;
end;

// Path of the exe inside a Run value such as: "C:\x\Clink.exe" --minimized
function RunValueExePath(const Value: string): string;
var
  P: Integer;
  S: string;
begin
  S := Trim(Value);
  if (Length(S) > 0) and (S[1] = '"') then
  begin
    Delete(S, 1, 1);
    P := Pos('"', S);
    if P > 0 then S := Copy(S, 1, P - 1);
  end
  else
  begin
    P := Pos(' --', S);
    if P > 0 then S := Copy(S, 1, P - 1);
  end;
  Result := S;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Value, ExePath, AppDir: string;
begin
  if CurUninstallStep = usUninstall then
  begin
    // Remove the autostart entry only when it points into this install.
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Clink', Value) then
    begin
      ExePath := Lowercase(RunValueExePath(Value));
      AppDir := Lowercase(RemoveBackslash(ExpandConstant('{app}'))) + '\';
      if Copy(ExePath, 1, Length(AppDir)) = AppDir then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Clink');
    end;
  end;
end;
