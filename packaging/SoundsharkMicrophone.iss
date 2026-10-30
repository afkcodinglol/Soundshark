#define AppVersion "1.6.0"
[Setup]
AppId={{A08799CD-8535-492D-A1B4-F4C8B51E8779}
AppName=Soundshark Microphone Support
AppVersion={#AppVersion}
DefaultDirName={commonpf64}\SoundsharkAudio
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=..\outputs\native
OutputBaseFilename=Soundshark-Microphone-Setup-x64
UninstallDisplayIcon={app}\SoundsharkMicrophone.dll
Compression=lzma2/fast
WizardStyle=modern
InfoBeforeFile=..\docs\EXPERIMENTAL-MICROPHONE.txt
SetupLogging=yes
AlwaysRestart=yes
DisableProgramGroupPage=yes
DisableDirPage=yes
[Files]
Source: "..\outputs\native\SoundsharkMicrophone.dll"; DestDir: "{app}"; Flags: ignoreversion restartreplace
Source: "..\scripts\ConfigureMicrophone.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\scripts\ExperimentalAudioPolicy.ps1"; DestDir: "{app}"; Flags: ignoreversion
[Dirs]
Name: "{app}\EndpointBackups"; Flags: uninsneveruninstall
Name: "{app}\PolicyBackup"; Flags: uninsneveruninstall
[Registry]
Root: HKLM; Subkey: "SOFTWARE\Classes\CLSID\{{A08799CD-8535-492D-A1B4-F4C8B51E8779}"; ValueType: string; ValueData: "Soundshark Microphone Mixer"; Flags: uninsdeletekey
Root: HKLM; Subkey: "SOFTWARE\Classes\CLSID\{{A08799CD-8535-492D-A1B4-F4C8B51E8779}\InProcServer32"; ValueType: string; ValueData: "{app}\SoundsharkMicrophone.dll"
Root: HKLM; Subkey: "SOFTWARE\Classes\CLSID\{{A08799CD-8535-492D-A1B4-F4C8B51E8779}\InProcServer32"; ValueName: "ThreadingModel"; ValueType: string; ValueData: "Both"
Root: HKLM; Subkey: "SOFTWARE\Classes\AudioEngine\AudioProcessingObjects\{{A08799CD-8535-492D-A1B4-F4C8B51E8779}"; ValueName: "FriendlyName"; ValueType: string; ValueData: "Soundshark Microphone Mixer"; Flags: uninsdeletekey
Root: HKLM; Subkey: "SOFTWARE\Classes\AudioEngine\AudioProcessingObjects\{{A08799CD-8535-492D-A1B4-F4C8B51E8779}"; ValueName: "Copyright"; ValueType: string; ValueData: "Soundshark"
Root: HKLM; Subkey: "SOFTWARE\Classes\AudioEngine\AudioProcessingObjects\{{A08799CD-8535-492D-A1B4-F4C8B51E8779}"; ValueName: "MajorVersion"; ValueType: dword; ValueData: "1"
Root: HKLM; Subkey: "SOFTWARE\Classes\AudioEngine\AudioProcessingObjects\{{A08799CD-8535-492D-A1B4-F4C8B51E8779}"; ValueName: "MinorVersion"; ValueType: dword; ValueData: "0"
Root: HKLM; Subkey: "SOFTWARE\Classes\AudioEngine\AudioProcessingObjects\{{A08799CD-8535-492D-A1B4-F4C8B51E8779}"; ValueName: "Flags"; ValueType: dword; ValueData: "14"
Root: HKLM; Subkey: "SOFTWARE\Classes\AudioEngine\AudioProcessingObjects\{{A08799CD-8535-492D-A1B4-F4C8B51E8779}"; ValueName: "MinInputConnections"; ValueType: dword; ValueData: "1"
Root: HKLM; Subkey: "SOFTWARE\Classes\AudioEngine\AudioProcessingObjects\{{A08799CD-8535-492D-A1B4-F4C8B51E8779}"; ValueName: "MaxInputConnections"; ValueType: dword; ValueData: "1"
Root: HKLM; Subkey: "SOFTWARE\Classes\AudioEngine\AudioProcessingObjects\{{A08799CD-8535-492D-A1B4-F4C8B51E8779}"; ValueName: "MinOutputConnections"; ValueType: dword; ValueData: "1"
Root: HKLM; Subkey: "SOFTWARE\Classes\AudioEngine\AudioProcessingObjects\{{A08799CD-8535-492D-A1B4-F4C8B51E8779}"; ValueName: "MaxOutputConnections"; ValueType: dword; ValueData: "1"
Root: HKLM; Subkey: "SOFTWARE\Classes\AudioEngine\AudioProcessingObjects\{{A08799CD-8535-492D-A1B4-F4C8B51E8779}"; ValueName: "MaxInstances"; ValueType: dword; ValueData: "4294967295"
Root: HKLM; Subkey: "SOFTWARE\Classes\AudioEngine\AudioProcessingObjects\{{A08799CD-8535-492D-A1B4-F4C8B51E8779}"; ValueName: "NumAPOInterfaces"; ValueType: dword; ValueData: "1"
Root: HKLM; Subkey: "SOFTWARE\Classes\AudioEngine\AudioProcessingObjects\{{A08799CD-8535-492D-A1B4-F4C8B51E8779}"; ValueName: "APOInterface0"; ValueType: string; ValueData: "{{FD7F2B29-24D0-4B5C-B177-592C39F9CA10}"
[Code]
function SafeGuid(const Value: String): Boolean;
var I: Integer;
begin
  Result := (Length(Value) = 38) and (Value[1] = '{') and (Value[38] = '}');
  if not Result then exit;
  for I := 2 to 37 do if Pos(Value[I], '0123456789abcdefABCDEF-') = 0 then begin Result := False; exit; end;
end;
function SafeSid(const Value: String): Boolean;
var I: Integer;
begin
  Result := (Length(Value) > 7) and (Copy(Value, 1, 4) = 'S-1-'); if not Result then exit;
  for I := 5 to Length(Value) do if Pos(Value[I], '0123456789-') = 0 then begin Result := False; exit; end;
end;
function PowerShell(const Parameters: String): Boolean;
var Code: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), '-NoProfile -NonInteractive ' + Parameters, '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
end;
function InitializeSetup(): Boolean;
var Endpoint, Sid, DllPath, CleanupRoot: String;
    CleanupCode: Integer;
begin
  Result := False;
  Endpoint := ExpandConstant('{param:ENDPOINT|}'); Sid := ExpandConstant('{param:USER_SID|}');
  if ExpandConstant('{param:REMOVE_ALL_SUPPORT|0}') = '1' then begin
    if not SafeSid(Sid) then begin MsgBox('The original Windows user identity is required for safe cleanup.', mbError, MB_OK); exit; end;
    CleanupRoot := ExpandConstant('{commonpf64}\SoundsharkAudio');
    if not DirExists(CleanupRoot) then begin MsgBox('Experimental support is not installed at its protected location. No policy changes were made. If protection is disabled, recover the original backups before repair.', mbError, MB_OK); exit; end;
    ExtractTemporaryFile('ConfigureMicrophone.ps1'); ExtractTemporaryFile('ExperimentalAudioPolicy.ps1');
    if not FileCopy(ExpandConstant('{tmp}\ConfigureMicrophone.ps1'), CleanupRoot + '\ConfigureMicrophone.ps1', False) or not FileCopy(ExpandConstant('{tmp}\ExperimentalAudioPolicy.ps1'), CleanupRoot + '\ExperimentalAudioPolicy.ps1', False) then begin MsgBox('Could not update the protected cleanup helpers. No endpoint or policy changes were made.', mbError, MB_OK); exit; end;
    if not PowerShell('-ExecutionPolicy Bypass -File "' + CleanupRoot + '\ConfigureMicrophone.ps1" -RestoreAll -UserSid "' + Sid + '"') then begin MsgBox('Cleanup stopped to preserve the current audio configuration. Original backups are missing or a later effect/policy conflicts. Keep EndpointBackups and PolicyBackup and review SetupLogs with support. No guessed registry repair was performed.', mbError, MB_OK); exit; end;
    if not FileExists(CleanupRoot + '\unins000.exe') then begin MsgBox('Endpoint effects and the saved policy were restored. The component uninstaller is missing; preserve the backups and repair the installation before removing registered component files. Restart Windows.', mbError, MB_OK); exit; end;
    if not Exec(CleanupRoot + '\unins000.exe', '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, CleanupCode) or (CleanupCode <> 0) then begin MsgBox('Endpoint effects and the saved policy were restored, but component removal did not finish. Preserve the backups, review the uninstall log and restart Windows before repair.', mbError, MB_OK); exit; end;
    MsgBox('Backed-up endpoint effects and audio policy were restored. Restart Windows to finish removing experimental support. Existing third-party changes were preserved.', mbInformation, MB_OK);
    exit;
  end;
  if ExpandConstant('{param:REMOVE_SUPPORT|0}') = '1' then begin
    if not SafeGuid(Endpoint) or not SafeSid(Sid) then exit;
    CleanupRoot := ExpandConstant('{commonpf64}\SoundsharkAudio');
    ExtractTemporaryFile('ConfigureMicrophone.ps1'); ExtractTemporaryFile('ExperimentalAudioPolicy.ps1');
    if not FileCopy(ExpandConstant('{tmp}\ConfigureMicrophone.ps1'), CleanupRoot + '\ConfigureMicrophone.ps1', False) or not FileCopy(ExpandConstant('{tmp}\ExperimentalAudioPolicy.ps1'), CleanupRoot + '\ExperimentalAudioPolicy.ps1', False) then begin MsgBox('Could not prepare protected cleanup. No registry repair was attempted.', mbError, MB_OK); exit; end;
    if PowerShell('-ExecutionPolicy Bypass -File "' + ExpandConstant('{commonpf64}\SoundsharkAudio\ConfigureMicrophone.ps1') + '" -Restore -EndpointGuid "' + Endpoint + '" -UserSid "' + Sid + '"') then MsgBox('Microphone support removed for this device. Restart Windows to finish.', mbInformation, MB_OK)
    else MsgBox('Could not restore the microphone. Its current effect was left in place.', mbError, MB_OK);
    exit;
  end;
  if not SafeGuid(Endpoint) or not SafeSid(Sid) then begin MsgBox('Start microphone setup from Soundshark Library Settings.', mbError, MB_OK); exit; end;
  ExtractTemporaryFile('SoundsharkMicrophone.dll');
  DllPath := ExpandConstant('{tmp}\SoundsharkMicrophone.dll');
  StringChangeEx(DllPath, '''', '''''', True);
  if (ExpandConstant('{param:EXPERIMENTAL|0}') <> '1') and not PowerShell('-Command "if ((Get-AuthenticodeSignature -LiteralPath ''' + DllPath + ''').Status -ne ''Valid'') { exit 1 }"') then begin
    MsgBox('This development microphone component is not signed for installation. No microphone configuration was changed. A signed and validated microphone build is required.', mbError, MB_OK); exit;
  end;
  Result := True;
end;
function ExperimentalArgument(): String;
begin
  Result := '';
  if ExpandConstant('{param:EXPERIMENTAL|0}') = '1' then Result := ' -Experimental';
end;
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    if not PowerShell('-ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\ConfigureMicrophone.ps1') + '" -EndpointGuid "' + ExpandConstant('{param:ENDPOINT|}') + '" -UserSid "' + ExpandConstant('{param:USER_SID|}') + '"' + ExperimentalArgument) then
      RaiseException('Endpoint setup did not complete. The previous microphone effect was preserved. See the setup documentation.');
end;
function InitializeUninstall(): Boolean;
begin
  Result := PowerShell('-ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\ConfigureMicrophone.ps1') + '" -RestoreAll');
  if not Result then MsgBox('Uninstall stopped because a microphone effect changed after Soundshark setup. Restore it before removing the audio component.', mbError, MB_OK);
end;
