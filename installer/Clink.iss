; Clink installer (Inno Setup 6). Build: ISCC.exe /DAppVersion=0.4.2 /DSourceExe=path [/DAppFileVersion=0.4.2.0] [/DOutDir=path] installer\Clink.iss
;   [/DDotNetRuntimeUrl=https://... /DDotNetRuntimeSha256=<64 hex> [/DDotNetRuntimeVersion=10.0.14]] [/DNoticesFile=path]
; SourceExe is mandatory and must be the FRAMEWORK-DEPENDENT Clink.exe (the installer flavour needs the .NET 10 Desktop Runtime).
; DotNetRuntimeUrl/DotNetRuntimeSha256 come from tools\resolve-dotnet-runtime.ps1 (SHA-256 of the verified Microsoft installer).
; Without DotNetRuntimeUrl (offline local compile) there is no runtime task and Setup only warns when the runtime is missing.
; Per-user install (no admin) into %LOCALAPPDATA%\Programs\Clink. Needs Inno Setup 6.3+ (x64compatible). The same AppId makes a newer setup upgrade in place.
; Fixed per-user location (no directory page). Uninstall removes only the program, the autostart entry and %LOCALAPPDATA%\Clink\updates
; (downloads); it never touches %APPDATA%\Clink (settings) or clips.

#ifndef AppVersion
  #error Pass the version: ISCC.exe /DAppVersion=X.Y.Z /DSourceExe=path installer\Clink.iss
#endif
#ifndef AppFileVersion
  #define AppFileVersion "0.0.0.0"
#endif
#ifndef SourceExe
  #error Pass the framework-dependent exe: ISCC.exe /DSourceExe=path\Clink-framework.exe (no default, so the self-contained exe is never packed by accident)
#endif
#ifndef OutDir
  #define OutDir "..\dist\installer"
#endif
#ifdef DotNetRuntimeUrl
  #ifndef DotNetRuntimeSha256
    #error DotNetRuntimeUrl needs DotNetRuntimeSha256 (64 hex characters, SHA-256 of the installer; see tools\resolve-dotnet-runtime.ps1)
  #endif
  #ifndef DotNetRuntimeVersion
    #define DotNetRuntimeVersion "10"
  #endif
  #if Pos('"', DotNetRuntimeVersion) > 0 || Pos("'", DotNetRuntimeVersion) > 0
    #error DotNetRuntimeVersion must not contain quote characters
  #endif
  ; Compile-time guards (ISPP: C-like operators; string literals may use either quote, the other quote needs no escaping).
  ; The values are pasted into Pascal string literals in [Code], so a quote must never get through.
  #if Len(DotNetRuntimeSha256) != 64
    #error DotNetRuntimeSha256 must be exactly 64 hex characters (SHA-256 of the installer; see tools\resolve-dotnet-runtime.ps1)
  #endif
  #if Copy(DotNetRuntimeUrl, 1, 8) != "https://"
    #error DotNetRuntimeUrl must start with https://
  #endif
  #if Pos("'", DotNetRuntimeUrl) > 0 || Pos('"', DotNetRuntimeUrl) > 0 || Pos("'", DotNetRuntimeSha256) > 0 || Pos('"', DotNetRuntimeSha256) > 0
    #error DotNetRuntimeUrl and DotNetRuntimeSha256 must not contain quote characters
  #endif
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
; Setup itself stays unelevated in per-user mode: the .NET runtime installer (a WiX Burn bundle) asks for UAC by itself.
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
#ifdef DotNetRuntimeUrl
; Shown only when the runtime is missing; ticked by default in the interactive wizard. With /SILENT or /VERYSILENT it runs only
; when /TASKS= or /MERGETASKS= names it explicitly (see ShouldInstallRuntime in [Code]).
Name: "dotnetruntime"; Description: "Install the Microsoft .NET {#DotNetRuntimeVersion} Desktop Runtime (required to run Clink)"; GroupDescription: "Required component:"; Check: NeedsDesktopRuntime
#endif

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; DestName: "Clink.exe"; Flags: ignoreversion
#ifdef NoticesFile
Source: "{#NoticesFile}"; DestDir: "{app}"; DestName: "THIRD-PARTY-NOTICES.txt"; Flags: ignoreversion
#endif

[Icons]
Name: "{autoprograms}\Clink"; Filename: "{app}\Clink.exe"
Name: "{autodesktop}\Clink"; Filename: "{app}\Clink.exe"; Tasks: desktopicon

[Run]
; Offered only when the runtime is present (it may have just been installed by the task above).
Filename: "{app}\Clink.exe"; Description: "{cm:LaunchProgram,Clink}"; Flags: nowait postinstall skipifsilent unchecked; Check: HasDesktopRuntime10

[UninstallDelete]
Type: files; Name: "{app}\Clink.exe.bak"
Type: files; Name: "{app}\Clink.exe.new"
Type: files; Name: "{app}\Clink.exe.failed"
; Downloaded updates only. Never %APPDATA%\Clink (settings) or the clips folder.
Type: filesandordirs; Name: "{localappdata}\Clink\updates"

[Code]
const
  DotNetUrl = 'https://dotnet.microsoft.com/download/dotnet/10.0';
  RuntimeFileName = 'windowsdesktop-runtime.exe';
  RuntimeTaskName = 'dotnetruntime';
  // Delphi TFileStream open/share mode values (fmOpenRead = $0000, fmShareDenyWrite = $0020), spelled as numbers so the
  // script does not depend on the fm* constants being registered in Inno's script engine.
  StreamOpenRead = $0000;
  StreamShareDenyWrite = $0020;
  ErrorElevationRequired = 740;
  StillActiveExitCode = 259;  // STILL_ACTIVE: no real exit code was obtained
#ifdef DotNetRuntimeUrl
  RuntimeUrl = '{#DotNetRuntimeUrl}';
  RuntimeSha256 = '{#DotNetRuntimeSha256}';
#endif

var
  RuntimeDownloaded: Boolean;     // file is in {tmp} and its SHA-256 matched when downloaded
  RuntimeRestartNeeded: Boolean;  // installer returned 3010 or 1641
#ifdef DotNetRuntimeUrl
  RuntimeDownloadPage: TDownloadWizardPage;
#endif

// ---------------------------------------------------------------------------------------------------------------------
// Runtime detection. Mirrors the framework-dependent apphost with AppHostDotNetSearch=Global: the install root is the
// InstallLocation registered by the .NET installers (HKLM, 32-bit view) when present, else <Program Files>\dotnet.
// Then shared\Microsoft.WindowsDesktop.App\10.x.y must exist as a folder with a plain release version name.
// ---------------------------------------------------------------------------------------------------------------------

function IsAllDigits(const S: string): Boolean;
var
  I: Integer;
begin
  Result := Length(S) > 0;
  for I := 1 to Length(S) do
    if (S[I] < '0') or (S[I] > '9') then
      Result := False;
end;

// True for names like 10.0.14 (no preview/rc suffix: those do not satisfy a 10.0.x release requirement).
function IsRuntime10FolderName(const Name: string): Boolean;
var
  Rest: string;
  P: Integer;
begin
  Result := False;
  if Copy(Name, 1, 3) <> '10.' then Exit;
  Rest := Copy(Name, 4, Length(Name));
  P := Pos('.', Rest);
  if P < 2 then Exit;
  Result := IsAllDigits(Copy(Rest, 1, P - 1)) and IsAllDigits(Copy(Rest, P + 1, Length(Rest)));
end;

// True when Root\shared\Microsoft.WindowsDesktop.App holds a plain 10.x.y folder.
function HasDesktopRuntime10In(const Root: string): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if FindFirst(AddBackslash(Root) + 'shared\Microsoft.WindowsDesktop.App\10.*', FindRec) then
  try
    repeat
      if ((FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and IsRuntime10FolderName(FindRec.Name) then
        Result := True;
    until Result or not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

function HasDesktopRuntime10: Boolean;
var
  Registered: string;
begin
  Registered := '';
  if RegQueryStringValue(HKLM32, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64', 'InstallLocation', Registered) then
    Registered := Trim(Registered);
  if Registered <> '' then
    Result := HasDesktopRuntime10In(Registered)
  else
  begin
    // No registered x64 location. On ARM64 the x64 apphost (running emulated) defaults to <Program Files>\dotnet\x64; the
    // native <Program Files>\dotnet root there holds ARM64 runtimes, which cannot run the x64 Clink and must not count.
    if IsArm64 then
      Result := HasDesktopRuntime10In(ExpandConstant('{commonpf64}\dotnet\x64'))
    else
      Result := HasDesktopRuntime10In(ExpandConstant('{commonpf64}\dotnet'));
  end;
end;

function NeedsDesktopRuntime: Boolean;
begin
  Result := not HasDesktopRuntime10;
end;

// ---------------------------------------------------------------------------------------------------------------------
// Silent installs. Inno selects every task that is not flagged "unchecked" when /SILENT or /VERYSILENT is used without
// /TASKS, so WizardIsTaskSelected alone cannot tell "default" from "asked for". The runtime is only installed silently when
// the command line names the task: /TASKS="...,dotnetruntime" or /MERGETASKS="...,dotnetruntime". ParamStr returns the
// documented parameters with quotes already removed; items are comma separated, a leading "*" is ignored and "!name" deselects.
// Each /TASKS= or /MERGETASKS= parameter REPLACES the list of the previous one (Inno: Setup.MainFunc.pas, SetStringsFromCommaString
// on InitTasks for both switches; /TASKS= also deselects all other tasks, /MERGETASKS= keeps the defaults). Only whether the
// runtime task is named matters here, so the answer is reset before each parameter and the last one wins.
// A task list loaded with /LOADINF is deliberately not honoured (so it stays opt-in by command line).
//
// Silent and unattended installs (notes):
//  - UsePreviousTasks (default yes) remembers an earlier untick of the runtime task for interactive upgrades. Accepted:
//    the user chose it, and the ready page warns when the runtime is missing and will not be installed.
//  - /VERYSILENT /TASKS=dotnetruntime may end with a restart request (runtime installer exit code 3010). Setup then reboots
//    unless /NORESTART is passed. Unattended callers should pass /NORESTART.
//  - A non-elevated silent install still shows the runtime installer's own UAC prompt, even with /quiet. For fully
//    unattended deployments run Setup elevated (or use /ALLUSERS once an all-users mode exists).
// ---------------------------------------------------------------------------------------------------------------------

procedure ApplyTaskList(const ListIn, TaskName: string; var Selected: Boolean);
var
  List, Item: string;
  P: Integer;
begin
  List := Trim(ListIn);
  if (Length(List) > 0) and (List[1] = '"') then Delete(List, 1, 1);
  if (Length(List) > 0) and (List[Length(List)] = '"') then Delete(List, Length(List), 1);
  while List <> '' do
  begin
    P := Pos(',', List);
    if P = 0 then
    begin
      Item := List;
      List := '';
    end
    else
    begin
      Item := Copy(List, 1, P - 1);
      Delete(List, 1, P);
    end;
    Item := Lowercase(Trim(Item));
    if (Length(Item) > 0) and (Item[1] = '*') then Delete(Item, 1, 1);
    if Item = TaskName then
      Selected := True
    else if Item = '!' + TaskName then
      Selected := False;
  end;
end;

function CommandLineSelectsTask(const TaskName: string): Boolean;
var
  I: Integer;
  Arg: string;
begin
  Result := False;
  for I := 1 to ParamCount do
  begin
    Arg := ParamStr(I);
    if Lowercase(Copy(Arg, 1, 7)) = '/tasks=' then
    begin
      Result := False;  // replaces the previous list
      ApplyTaskList(Copy(Arg, 8, Length(Arg)), TaskName, Result);
    end
    else if Lowercase(Copy(Arg, 1, 12)) = '/mergetasks=' then
    begin
      Result := False;  // replaces the previous list
      ApplyTaskList(Copy(Arg, 13, Length(Arg)), TaskName, Result);
    end;
  end;
end;

// True when the command line has the given switch (case-insensitive), for example '/VERYSILENT'. WizardSilent is True for
// both /SILENT and /VERYSILENT and Inno 6.7 has no script function that tells them apart.
function CommandLineHasSwitch(const Switch: string): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if SameText(ParamStr(I), Switch) then
      Result := True;
end;

// ---------------------------------------------------------------------------------------------------------------------
// Runtime task: download (ready page), verify, install (preparing step). Setup is never failed because of the runtime.
// ---------------------------------------------------------------------------------------------------------------------

function ShouldInstallRuntime: Boolean;
begin
  Result := False;
#ifdef DotNetRuntimeUrl
  if not HasDesktopRuntime10 then
    if WizardIsTaskSelected(RuntimeTaskName) then
      Result := (not WizardSilent) or CommandLineSelectsTask(RuntimeTaskName);
#endif
end;

function RuntimeFilePath: string;
begin
  Result := ExpandConstant('{tmp}\') + RuntimeFileName;
end;

// Tell the user (dialog unless silent; always logged) and optionally offer the official download page.
procedure WarnRuntime(const Msg: string);
var
  ErrCode: Integer;
begin
  Log('.NET runtime: ' + Msg);
  if WizardSilent then Exit;
  if SuppressibleMsgBox(Msg + #13#10#13#10 + 'Clink will not start until the .NET 10 Desktop Runtime (x64) is installed. ' +
      'Open the official download page now?' + #13#10 + DotNetUrl, mbInformation, MB_YESNO, IDNO) = IDYES then
    // As the original (non-elevated) user, so the browser does not run with Setup's rights.
    ShellExecAsOriginalUser('open', DotNetUrl, '', '', SW_SHOWNORMAL, ewNoWait, ErrCode);
end;

#ifdef DotNetRuntimeUrl
function IsHexString(const S: string): Boolean;
var
  I: Integer;
begin
  Result := Length(S) > 0;
  for I := 1 to Length(S) do
    if not (((S[I] >= '0') and (S[I] <= '9')) or ((S[I] >= 'a') and (S[I] <= 'f')) or ((S[I] >= 'A') and (S[I] <= 'F'))) then
      Result := False;
end;

function RuntimeConfigIsSane: Boolean;
begin
  Result := (Length(RuntimeSha256) = 64) and IsHexString(RuntimeSha256) and (Lowercase(Copy(RuntimeUrl, 1, 8)) = 'https://');
  if not Result then
    Log('.NET runtime: DotNetRuntimeUrl must be https and DotNetRuntimeSha256 must be 64 hex characters.');
end;

// Verifies the downloaded file and, only if it matches, runs it. TOCTOU: the file sits in a per-user temp folder, so it is
// opened first with a deny-write handle (FILE_SHARE_READ only: no other process can write it, and without share-delete it
// cannot be renamed or deleted either). The SHA-256 is computed from that same handle and the handle stays open across
// Exec, so a same-user process cannot swap the file between the check and the launch. Reading and mapping the image for
// CreateProcess is still allowed by the share mode.
// Result: 0 = ran (ResultCode valid), 1 = not run, file missing or failed the check, 2 = verified but could not be started
// (ResultCode = error code).
function RunVerifiedRuntime(const Params: string; var ResultCode: Integer): Integer;
var
  F: TFileStream;
  Verified: Boolean;
begin
  Result := 1;
  ResultCode := 0;
  if not FileExists(RuntimeFilePath) then Exit;
  try
    F := TFileStream.Create(RuntimeFilePath, StreamOpenRead or StreamShareDenyWrite);
  except
    Log('Could not open the runtime installer for verification: ' + GetExceptionMessage);
    Exit;
  end;
  try
    Verified := False;
    try
      Verified := SameText(GetSHA256OfStream(F), RuntimeSha256);
    except
      Log('SHA-256 check failed: ' + GetExceptionMessage);
    end;
    if not Verified then
      Result := 1
    else if Exec(RuntimeFilePath, Params, '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
      Result := 0
    else
    begin
      Result := 2;
      if ResultCode = ErrorElevationRequired then
      begin
        // Exec cannot raise the UAC prompt itself. ShellExec with "runas" can. When it got a process handle Inno returns the
        // real exit code (so 3010/1641 still request a restart); only if that is missing (STILL_ACTIVE) is success judged
        // by looking for the runtime afterwards.
        Log('.NET runtime installer needs elevation (740), retrying with the runas verb.');
        if ShellExec('runas', RuntimeFilePath, Params, '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
        begin
          Result := 0;
          if ResultCode = StillActiveExitCode then
          begin
            Log('.NET runtime installer (runas) gave no exit code (259); success is judged by detection.');
            if HasDesktopRuntime10 then ResultCode := 0 else ResultCode := 1603;
          end
          else
            Log('.NET runtime installer (runas) finished with its real exit code ' + IntToStr(ResultCode) + '.');
        end
        else
          Log('runas failed: ' + SysErrorMessage(ResultCode) + ' (code ' + IntToStr(ResultCode) + ').');
      end;
    end;
  finally
    F.Free;
  end;
end;

procedure InitializeWizard;
begin
  RuntimeDownloadPage := CreateDownloadPage('Downloading the .NET Desktop Runtime',
    'Setup is downloading the Microsoft .NET Desktop Runtime and checking it.', nil);
  RuntimeDownloadPage.ShowBaseNameInsteadOfUrl := True;
end;

// Interactive: progress page with retry. Silent: plain DownloadTemporaryFile (no UI), no retry.
procedure DownloadRuntime;
var
  Retry: Boolean;
begin
  RuntimeDownloaded := False;
  if not RuntimeConfigIsSane then Exit;
  if WizardSilent then
  begin
    try
      DownloadTemporaryFile(RuntimeUrl, RuntimeFileName, RuntimeSha256, nil);
      RuntimeDownloaded := True;
    except
      Log('.NET runtime download failed: ' + GetExceptionMessage);
    end;
    Exit;
  end;
  repeat
    Retry := False;
    RuntimeDownloadPage.Clear;
    RuntimeDownloadPage.Add(RuntimeUrl, RuntimeFileName, RuntimeSha256);
    RuntimeDownloadPage.Show;
    try
      try
        RuntimeDownloadPage.Download;
        RuntimeDownloaded := True;
      except
        if RuntimeDownloadPage.AbortedByUser then
          Log('.NET runtime download aborted by user.')
        else
        begin
          Log('.NET runtime download failed: ' + GetExceptionMessage);
          Retry := SuppressibleMsgBox('The .NET Desktop Runtime could not be downloaded or did not pass its integrity check:' + #13#10 +
            AddPeriod(GetExceptionMessage) + #13#10#13#10 +
            'Yes: try again.' + #13#10 + 'No: continue without it (you can install it later).',
            mbError, MB_YESNO, IDNO) = IDYES;
        end;
      end;
    finally
      RuntimeDownloadPage.Hide;
    end;
  until not Retry;
end;

procedure InstallRuntime;
var
  ResultCode: Integer;
  Params: string;
  Retry: Boolean;
begin
  // /VERYSILENT: no UI at all (/quiet). /SILENT and interactive: the installer shows its progress (/passive).
  if WizardSilent and CommandLineHasSwitch('/VERYSILENT') then
    Params := '/install /quiet /norestart'
  else
    Params := '/install /passive /norestart';
  repeat
    Retry := False;
    ResultCode := 0;
    // Verified and run through one deny-write handle (see RunVerifiedRuntime).
    case RunVerifiedRuntime(Params, ResultCode) of
      1:
        begin
          WarnRuntime('The downloaded .NET Desktop Runtime installer is missing or failed its integrity check, so it was not run.');
          Exit;
        end;
      2:
        begin
          WarnRuntime('The .NET Desktop Runtime installer could not be started: ' + SysErrorMessage(ResultCode) + ' (code ' + IntToStr(ResultCode) + ').');
          Exit;
        end;
    end;
    Log('.NET runtime installer exit code ' + IntToStr(ResultCode));
    if (ResultCode = 0) or (ResultCode = 3010) or (ResultCode = 1641) then
    begin
      if (ResultCode <> 0) then RuntimeRestartNeeded := True;
      if not HasDesktopRuntime10 then
        WarnRuntime('The .NET Desktop Runtime installer finished but the runtime was not detected.');
    end
    else if (ResultCode = 1223) or (ResultCode = 1602) then
      WarnRuntime('The .NET Desktop Runtime installation was cancelled.')
    else if ResultCode = 1618 then
    begin
      if WizardSilent then
        WarnRuntime('Another installation is in progress (code 1618), so the .NET Desktop Runtime was not installed.')
      else
        Retry := SuppressibleMsgBox('Another installation is already in progress (code 1618), so the .NET Desktop Runtime could not be installed.' + #13#10#13#10 +
          'Finish or close the other installation, then choose Yes to try again, or No to continue without it.',
          mbError, MB_YESNO, IDNO) = IDYES;
      if (not Retry) and (not WizardSilent) then
        WarnRuntime('The .NET Desktop Runtime was not installed because another installation was in progress.');
    end
    else if HasDesktopRuntime10 then
      Log('Installer returned ' + IntToStr(ResultCode) + ' but the runtime is present (for example a newer one was already installed).')
    else
      WarnRuntime('The .NET Desktop Runtime installer failed with code ' + IntToStr(ResultCode) + '.');
  until not Retry;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;  // never block Setup because of the runtime
  if (CurPageID = wpReady) and ShouldInstallRuntime then
    DownloadRuntime;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';  // an empty string lets Setup continue; the runtime never fails Setup
  if not ShouldInstallRuntime then Exit;
  if not RuntimeDownloaded then
  begin
    WarnRuntime('The .NET Desktop Runtime was not downloaded, so it was not installed.');
    Exit;
  end;
  InstallRuntime;
  DeleteFile(RuntimeFilePath);
end;
#endif

// Reported at the end of Setup: restart requested by the runtime installer (3010/1641).
function NeedRestart(): Boolean;
begin
  Result := RuntimeRestartNeeded;
end;

// Logged once after the files are installed (also covers silent installs and builds without DotNetRuntimeUrl).
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    if not HasDesktopRuntime10 then
      Log('Clink needs the .NET 10 Desktop Runtime; it was not installed');
end;

// Ready page: say plainly when Clink will be installed without its runtime.
function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := '';
  if MemoDirInfo <> '' then Result := Result + MemoDirInfo + NewLine + NewLine;
  if MemoGroupInfo <> '' then Result := Result + MemoGroupInfo + NewLine + NewLine;
  if MemoTasksInfo <> '' then Result := Result + MemoTasksInfo + NewLine + NewLine;
  if (not HasDesktopRuntime10) and (not ShouldInstallRuntime) then
    Result := Result + 'Warning: the .NET 10 Desktop Runtime (x64) was not found and Setup will not install it.' + NewLine +
      Space + 'Clink will not start until it is installed from ' + DotNetUrl;
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
