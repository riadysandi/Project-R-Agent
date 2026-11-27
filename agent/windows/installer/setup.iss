; ==============================================================================
; PROJECT R - ENTERPRISE WINDOWS AGENT INSTALLER SCRIPT (INNO SETUP 6)
; Supports: Windows 7 SP1, 8, 8.1, 10, 11 & Windows Server 2008 R2 - 2025
; ==============================================================================

#ifndef AppArch
  #define AppArch "x64"
#endif
#define MyAppVersion "2.5.1"

[Setup]
AppId={{D8C8431E-2B60-4E38-B7D4-95C7A9075D1E}
AppName=Project R Agent
AppVersion={#MyAppVersion}
AppVerName=Project R Agent v{#MyAppVersion} ({#AppArch})
AppPublisher=Project R Open Source
AppPublisherURL=https://github.com/riadysandi/Project-R
AppSupportURL=https://github.com/riadysandi/Project-R/issues
AppUpdatesURL=https://github.com/riadysandi/Project-R/releases
DefaultDirName={autopf}\Project R\Agent
DefaultGroupName=Project R
DisableProgramGroupPage=yes
OutputBaseFilename=ProjectR-Agent-Setup-{#AppArch}
OutputDir=..\bin
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=commandline
CloseApplications=yes
RestartApplications=no
UninstallDisplayName=Project R Agent ({#AppArch})
SetupIconFile=..\resources\app.ico
WizardImageFile=..\resources\wizard-side.bmp
WizardSmallImageFile=..\resources\wizard-small.bmp

#if AppArch == "x64"
ArchitecturesInstallIn64BitMode=x64
ArchitecturesAllowed=x64
#else
ArchitecturesAllowed=x86 x64
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Dirs]
Name: "{commonappdata}\ProjectR"; Permissions: users-modify
Name: "{commonappdata}\ProjectR\Agent"; Permissions: users-modify

[Files]
Source: "..\bin\ProjectRAgent-{#AppArch}.exe"; DestDir: "{tmp}"; DestName: "ProjectRAgent.exe"; Flags: dontcopy
Source: "..\bin\ProjectRAgent-{#AppArch}.exe"; DestDir: "{app}"; DestName: "ProjectRAgent.exe"; Flags: ignoreversion
Source: "..\bin\ProjectRManager-{#AppArch}.exe"; DestDir: "{app}"; DestName: "ProjectRManager.exe"; Flags: ignoreversion
Source: "..\bin\ProjectRRemoteAgent-{#AppArch}.exe"; DestDir: "{app}"; DestName: "ProjectRRemoteAgent.exe"; Flags: ignoreversion
Source: "..\resources\app.ico"; DestDir: "{app}"; DestName: "app.ico"; Flags: ignoreversion

[Icons]
Name: "{group}\Project R Agent Manager"; Filename: "{app}\ProjectRManager.exe"; IconFilename: "{app}\app.ico"; WorkingDir: "{app}"
Name: "{group}\Uninstall Project R Agent"; Filename: "{uninstallexe}"; IconFilename: "{app}\app.ico"
Name: "{autodesktop}\Project R Agent Manager"; Filename: "{app}\ProjectRManager.exe"; Tasks: desktopicon; IconFilename: "{app}\app.ico"; WorkingDir: "{app}"

[Registry]
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "ProjectRManager"; ValueData: """{app}\ProjectRManager.exe"" --watcher"; Flags: uninsdeletevalue
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "ProjectRRemoteAgent"; ValueData: """{app}\ProjectRRemoteAgent.exe"""; Flags: uninsdeletevalue; MinVersion: 10.0
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; ValueName: "MONASysAssetWarning"; Flags: deletevalue

[InstallDelete]
Type: filesandordirs; Name: "{commonappdata}\MONASys"

[Run]
; Terminate and remove legacy MONASys scheduled tasks and scripts
Filename: "taskkill.exe"; Parameters: "/F /IM wscript.exe"; Flags: runhidden
Filename: "schtasks.exe"; Parameters: "/delete /tn ""MONASys-Browser-Probe"" /f"; Flags: runhidden
Filename: "schtasks.exe"; Parameters: "/delete /tn ""MONASysAssetWarning"" /f"; Flags: runhidden
; Install and start Windows Service automatically
Filename: "{app}\ProjectRAgent.exe"; Parameters: "--install-service"; Flags: runhidden waituntilterminated
; Start native user session lock watcher immediately
Filename: "{app}\ProjectRManager.exe"; Parameters: "--watcher"; Flags: nowait runhidden skipifsilent
; Start native user session remote desktop agent immediately (Windows 10+ only)
Filename: "{app}\ProjectRRemoteAgent.exe"; Flags: nowait runhidden skipifsilent; MinVersion: 10.0
; Launch Manager UI on finish
Filename: "{app}\ProjectRManager.exe"; Description: "{cm:LaunchProgram,Project R Agent Manager}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Stop and remove Windows Service before uninstallation
Filename: "{app}\ProjectRAgent.exe"; Parameters: "--uninstall-service"; Flags: runhidden waituntilterminated
; Terminate any running Manager, Watcher or Remote Agent processes before uninstallation
Filename: "taskkill.exe"; Parameters: "/F /IM ProjectRManager.exe"; Flags: runhidden
Filename: "taskkill.exe"; Parameters: "/F /IM ProjectRRemoteAgent.exe"; Flags: runhidden

[UninstallDelete]
Type: filesandordirs; Name: "{commonappdata}\ProjectR"
Type: filesandordirs; Name: "{app}"

[Code]
var
  ConfigPage: TInputQueryWizardPage;
  ServerUrlEdit: String;
  TokenEdit: String;
  TagEdit: String;
  IsUpdateMode: Boolean;

// Stop service and processes before replacing files on install or upgrade
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  // Stop existing Windows Service and kill Manager watcher before file extraction
  Exec('net.exe', 'stop ProjectRAgent', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('taskkill.exe', '/F /IM ProjectRManager.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('taskkill.exe', '/F /IM ProjectRRemoteAgent.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(500);
end;

// Read command line parameter helper
function GetCmdParam(ParamName: String; DefaultVal: String): String;
var
  I: Integer;
  Param: String;
  Prefix: String;
begin
  Result := DefaultVal;
  Prefix := '/' + Uppercase(ParamName) + '=';
  for I := 1 to ParamCount do
  begin
    Param := ParamStr(I);
    if Pos(Prefix, Uppercase(Param)) = 1 then
    begin
      Result := Copy(Param, Length(Prefix) + 1, Length(Param));
      Exit;
    end;
  end;
end;

// Fast JSON string value extractor
function ExtractJsonStringValue(JsonText, Key: String): String;
var
  SearchKey: String;
  P1, P2: Integer;
begin
  Result := '';
  SearchKey := '"' + Key + '":"';
  P1 := Pos(SearchKey, JsonText);
  if P1 > 0 then
  begin
    P1 := P1 + Length(SearchKey);
    P2 := Pos('"', Copy(JsonText, P1, Length(JsonText)));
    if P2 > 0 then
      Result := Copy(JsonText, P1, P2 - 1);
  end;
end;

// Check if Project R Agent is already installed on this computer
function CheckIfAgentInstalled: Boolean;
var
  ConfigFile: String;
  RegPath: String;
begin
  ConfigFile := ExpandConstant('{commonappdata}\ProjectR\Agent\agent.json');
  RegPath := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{D8C8431E-2B60-4E38-B7D4-95C7A9075D1E}_is1';
  
  Result := FileExists(ConfigFile) or 
            FileExists('C:\ProgramData\ProjectR\Agent\agent.json') or 
            RegKeyExists(HKLM, RegPath) or 
            RegKeyExists(HKCU, RegPath);
end;

// Initialize Custom Wizard Configuration Page
procedure InitializeWizard;
var
  ConfigFile: String;
  TagFile: String;
  ConfigContent: AnsiString;
  TagContent: AnsiString;
  ExistingUrl, ExistingToken, ExistingTag: String;
begin
  ConfigFile := ExpandConstant('{commonappdata}\ProjectR\Agent\agent.json');
  if not FileExists(ConfigFile) and FileExists('C:\ProgramData\ProjectR\Agent\agent.json') then
    ConfigFile := 'C:\ProgramData\ProjectR\Agent\agent.json';

  IsUpdateMode := CheckIfAgentInstalled;

  ExistingUrl := '';
  ExistingToken := '';
  ExistingTag := '';

  if FileExists(ConfigFile) then
  begin
    if LoadStringFromFile(ConfigFile, ConfigContent) then
    begin
      ExistingUrl := ExtractJsonStringValue(String(ConfigContent), 'ServerUrl');
      ExistingToken := ExtractJsonStringValue(String(ConfigContent), 'Token');
      ExistingTag := ExtractJsonStringValue(String(ConfigContent), 'Tag');
    end;
  end;

  TagFile := ExpandConstant('{commonappdata}\ProjectR\Agent\asset.tag');
  if (ExistingTag = '') and FileExists(TagFile) then
  begin
    if LoadStringFromFile(TagFile, TagContent) then
      ExistingTag := Trim(String(TagContent));
  end;

  ConfigPage := CreateInputQueryPage(wpSelectDir,
    'Konfigurasi Endpoint & Identitas Aset',
    'Pengaturan Server dan Identitas Komputer',
    'Pastikan seluruh data di bawah ini telah terisi dengan benar untuk melanjutkan instalasi:');

  ConfigPage.Add('URL Endpoint Server:', False);
  ConfigPage.Add('Deployment Token Otorisasi:', False);
  ConfigPage.Add('Nomor Asset TAG Perangkat:', False);

  // Command-line params override, then existing config, then fallback default
  if GetCmdParam('SERVER', '') <> '' then
    ConfigPage.Values[0] := GetCmdParam('SERVER', '')
  else if ExistingUrl <> '' then
    ConfigPage.Values[0] := ExistingUrl
  else
    ConfigPage.Values[0] := 'https://';

  if GetCmdParam('TOKEN', '') <> '' then
    ConfigPage.Values[1] := GetCmdParam('TOKEN', '')
  else if ExistingToken <> '' then
    ConfigPage.Values[1] := ExistingToken
  else
    ConfigPage.Values[1] := '';

  if GetCmdParam('TAG', '') <> '' then
    ConfigPage.Values[2] := GetCmdParam('TAG', '')
  else if ExistingTag <> '' then
    ConfigPage.Values[2] := ExistingTag
  else
    ConfigPage.Values[2] := '';

  if IsUpdateMode then
  begin
    WizardForm.WelcomeLabel2.Caption :=
      'Pembaruan Sistem Terdeteksi (v{#MyAppVersion}):' + #13#10#13#10 +
      'Project R Agent telah terpasang di komputer ini. Seluruh konfigurasi server, token otorisasi, dan TAG (' + ExistingTag + ') dipertahankan secara otomatis.' + #13#10#13#10 +
      'Klik Next untuk langsung memperbarui file aplikasi ke versi terbaru.';
  end;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  // If agent is already installed, skip entering URL, Token, and TAG completely!
  if (PageID = ConfigPage.ID) and (IsUpdateMode or CheckIfAgentInstalled) then
  begin
    Result := True;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  TmpExe: String;
  ResultCode: Integer;
  StatusFile: String;
  StatusLines: TArrayOfString;
  StatusCode: String;
  StatusMsg: String;
begin
  Result := True;
  if CurPageID = ConfigPage.ID then
  begin
    if IsUpdateMode or CheckIfAgentInstalled then
    begin
      // Existing installation: bypass verification
      Result := True;
      Exit;
    end;

    ServerUrlEdit := Trim(ConfigPage.Values[0]);
    TokenEdit := Trim(ConfigPage.Values[1]);
    TagEdit := Trim(ConfigPage.Values[2]);

    // Strip trailing /panel, /api, etc.
    if Pos('/panel', Lowercase(ServerUrlEdit)) > 0 then
      ServerUrlEdit := Copy(ServerUrlEdit, 1, Pos('/panel', Lowercase(ServerUrlEdit)) - 1);
    if Pos('/api', Lowercase(ServerUrlEdit)) > 0 then
      ServerUrlEdit := Copy(ServerUrlEdit, 1, Pos('/api', Lowercase(ServerUrlEdit)) - 1);

    // Strip trailing slashes
    while (Length(ServerUrlEdit) > 0) and (Copy(ServerUrlEdit, Length(ServerUrlEdit), 1) = '/') do
      Delete(ServerUrlEdit, Length(ServerUrlEdit), 1);

    if ServerUrlEdit = '' then
    begin
      MsgBox('URL Endpoint Server wajib diisi untuk melanjutkan instalasi.', mbError, MB_OK);
      Result := False;
      Exit;
    end;

    // Auto prepend http:// or https:// if protocol missing
    if (Pos('http://', Lowercase(ServerUrlEdit)) <> 1) and (Pos('https://', Lowercase(ServerUrlEdit)) <> 1) then
    begin
      if (Pos('192.168.', ServerUrlEdit) = 1) or (Pos('10.', ServerUrlEdit) = 1) or (Pos('172.', ServerUrlEdit) = 1) or (Pos('127.0.0.1', ServerUrlEdit) = 1) or (Pos('localhost', Lowercase(ServerUrlEdit)) = 1) then
        ServerUrlEdit := 'http://' + ServerUrlEdit
      else
        ServerUrlEdit := 'https://' + ServerUrlEdit;
    end;

    if TokenEdit = '' then
    begin
      MsgBox('Deployment Token Otorisasi wajib diisi untuk melanjutkan instalasi.', mbError, MB_OK);
      Result := False;
      Exit;
    end;

    if TagEdit = '' then
    begin
      MsgBox('Nomor Asset TAG Perangkat wajib diisi untuk melanjutkan instalasi.', mbError, MB_OK);
      Result := False;
      Exit;
    end;

    // Strictly verify deployment token against server before allowing installation!
    ExtractTemporaryFile('ProjectRAgent.exe');
    TmpExe := ExpandConstant('{tmp}\ProjectRAgent.exe');
    StatusFile := AddBackslash(GetTempDir()) + 'projectr_verify_status.txt';
    if FileExists(StatusFile) then DeleteFile(StatusFile);

    if Exec(TmpExe, '--verify-token "' + ServerUrlEdit + '" "' + TokenEdit + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    begin
      if ResultCode <> 0 then
      begin
        StatusCode := '';
        StatusMsg := '';
        if FileExists(StatusFile) and LoadStringsFromFile(StatusFile, StatusLines) then
        begin
          if GetArrayLength(StatusLines) > 0 then StatusCode := StatusLines[0];
          if GetArrayLength(StatusLines) > 1 then StatusMsg := StatusLines[1];
        end;

        if (StatusCode = 'TOKEN_REJECTED') or (ResultCode = 1) then
        begin
          MsgBox('AKSES DITOLAK: Deployment Token Otorisasi tidak valid atau ditolak oleh server Project R.' + #13#10#13#10 +
                 'Token "' + TokenEdit + '" tidak dikenali di server.' + #13#10#13#10 +
                 'Kemungkinan penyebab:' + #13#10 +
                 '1. Token telah diganti atau dibuat baru di web admin (menu Config Receiver).' + #13#10 +
                 '2. Halaman web belum di-refresh setelah menekan tombol ganti token.' + #13#10#13#10 +
                 'Silakan salin Deployment Token aktif terbaru dari web admin Project R sebelum melanjutkan.', mbCriticalError, MB_OK);
        end
        else if (StatusCode = 'NETWORK_ERROR') or (ResultCode = 2) then
        begin
          if StatusMsg = '' then StatusMsg := 'Tidak dapat terhubung ke URL server tersebut.';
          MsgBox('GAGAL TERHUBUNG KE SERVER PROJECT R:' + #13#10#13#10 +
                 'URL Server: ' + ServerUrlEdit + #13#10 +
                 'Detail Masalah: ' + StatusMsg + #13#10#13#10 +
                 'Pastikan komputer ini memiliki koneksi internet aktif dan dapat mengakses URL server tersebut.', mbCriticalError, MB_OK);
        end
        else
        begin
          if StatusMsg = '' then StatusMsg := 'Verifikasi token gagal (Kode Error: ' + IntToStr(ResultCode) + ').';
          MsgBox('GAGAL VERIFIKASI KE SERVER:' + #13#10#13#10 + StatusMsg, mbCriticalError, MB_OK);
        end;

        Result := False;
        Exit;
      end;
    end
    else
    begin
      MsgBox('Gagal menjalankan engine verifikasi token. Instalasi dibatalkan demi keamanan.', mbCriticalError, MB_OK);
      Result := False;
      Exit;
    end;
  end;
end;

// Save configuration file to C:\ProgramData\ProjectR\Agent\agent.json
procedure CurStepChanged(CurStep: TSetupStep);
var
  DataDir: String;
  ConfigFile: String;
  TagFile: String;
  JsonContent: String;
begin
  if CurStep = ssPostInstall then
  begin
    DataDir := ExpandConstant('{commonappdata}\ProjectR\Agent');
    ForceDirectories(DataDir);

    ConfigFile := DataDir + '\agent.json';
    TagFile := DataDir + '\asset.tag';

    // If in update mode and agent.json already exists, do not overwrite existing configuration!
    if not (IsUpdateMode or CheckIfAgentInstalled) or not FileExists(ConfigFile) then
    begin
      if ServerUrlEdit = '' then ServerUrlEdit := GetCmdParam('SERVER', '');
      if TokenEdit = '' then TokenEdit := GetCmdParam('TOKEN', '');
      if TagEdit = '' then TagEdit := GetCmdParam('TAG', '');

      // Strip trailing /panel, /api, etc.
      if Pos('/panel', Lowercase(ServerUrlEdit)) > 0 then
        ServerUrlEdit := Copy(ServerUrlEdit, 1, Pos('/panel', Lowercase(ServerUrlEdit)) - 1);
      if Pos('/api', Lowercase(ServerUrlEdit)) > 0 then
        ServerUrlEdit := Copy(ServerUrlEdit, 1, Pos('/api', Lowercase(ServerUrlEdit)) - 1);

      // Strip trailing slashes
      while (Length(ServerUrlEdit) > 0) and (Copy(ServerUrlEdit, Length(ServerUrlEdit), 1) = '/') do
        Delete(ServerUrlEdit, Length(ServerUrlEdit), 1);

      // Auto prepend http:// or https:// if protocol missing
      if (Pos('http://', Lowercase(ServerUrlEdit)) <> 1) and (Pos('https://', Lowercase(ServerUrlEdit)) <> 1) then
      begin
        if (Pos('192.168.', ServerUrlEdit) = 1) or (Pos('10.', ServerUrlEdit) = 1) or (Pos('172.', ServerUrlEdit) = 1) or (Pos('127.0.0.1', ServerUrlEdit) = 1) or (Pos('localhost', Lowercase(ServerUrlEdit)) = 1) then
          ServerUrlEdit := 'http://' + ServerUrlEdit
        else
          ServerUrlEdit := 'https://' + ServerUrlEdit;
      end;

      JsonContent := '{"ServerUrl":"' + ServerUrlEdit + '","Token":"' + TokenEdit + '","Tag":"' + TagEdit + '","Version":"{#MyAppVersion}","HeartbeatIntervalSeconds":60}';

      SaveStringToFile(ConfigFile, JsonContent, False);
      if TagEdit <> '' then
      begin
        SaveStringToFile(TagFile, TagEdit, False);
      end;
    end;
  end;
end;

// Custom Token Input Dialog for Anti-Tamper Uninstallation
function PromptForUninstallToken(Prompt, Title: String; var Value: String): Boolean;
var
  Form: TSetupForm;
  PromptLabel: TNewStaticText;
  Edit: TNewEdit;
  OKButton, CancelButton: TNewButton;
  BtnWidth: Integer;
begin
  Form := CreateCustomForm(ScaleX(380), ScaleY(116), False, False);
  try
    Form.Caption := Title;
    Form.Position := poScreenCenter;
    Form.BorderStyle := bsDialog;
    Form.ClientWidth := ScaleX(380);
    Form.ClientHeight := ScaleY(116);

    PromptLabel := TNewStaticText.Create(Form);
    PromptLabel.Parent := Form;
    PromptLabel.Left := ScaleX(14);
    PromptLabel.Top := ScaleY(10);
    PromptLabel.Width := Form.ClientWidth - ScaleX(28);
    PromptLabel.Height := ScaleY(30);
    PromptLabel.Caption := Prompt;
    PromptLabel.WordWrap := True;

    Edit := TNewEdit.Create(Form);
    Edit.Parent := Form;
    Edit.Left := ScaleX(14);
    Edit.Top := ScaleY(44);
    Edit.Width := Form.ClientWidth - ScaleX(28);
    Edit.Height := ScaleY(23);
    Edit.Text := Value;

    BtnWidth := ScaleX(75);

    CancelButton := TNewButton.Create(Form);
    CancelButton.Parent := Form;
    CancelButton.Caption := 'Batal';
    CancelButton.Width := BtnWidth;
    CancelButton.Height := ScaleY(24);
    CancelButton.Left := Form.ClientWidth - ScaleX(14) - BtnWidth;
    CancelButton.Top := ScaleY(78);
    CancelButton.ModalResult := mrCancel;
    CancelButton.Cancel := True;

    OKButton := TNewButton.Create(Form);
    OKButton.Parent := Form;
    OKButton.Caption := 'Lanjut';
    OKButton.Width := BtnWidth;
    OKButton.Height := ScaleY(24);
    OKButton.Left := CancelButton.Left - ScaleX(8) - BtnWidth;
    OKButton.Top := ScaleY(78);
    OKButton.ModalResult := mrOk;
    OKButton.Default := True;

    Form.ActiveControl := Edit;

    if Form.ShowModal = mrOk then
    begin
      Value := Edit.Text;
      Result := True;
    end
    else
    begin
      Result := False;
    end;
  finally
    Form.Free;
  end;
end;

// Anti-Tamper Uninstallation Confirmation Guard
function InitializeUninstall(): Boolean;
var
  CmdToken: String;
  InputToken: String;
  AgentExe: String;
  ResultCode: Integer;
  StatusFile: String;
  StatusLines: TArrayOfString;
  StatusCode: String;
  StatusMsg: String;
begin
  CmdToken := GetCmdParam('UNINSTALL_TOKEN', '');
  if CmdToken <> '' then
  begin
    InputToken := CmdToken;
  end
  else
  begin
    InputToken := '';
    if not PromptForUninstallToken(
      'Masukkan Token Otorisasi IT Uninstalasi resmi untuk komputer ini:' + #13#10 +
      '(Dapatkan di web Project R: Menu Config Receiver atau Detail Aset)',
      'Token Uninstalasi IT',
      InputToken) then
    begin
      Result := False;
      Exit;
    end;
  end;

  InputToken := Trim(InputToken);
  if InputToken = '' then
  begin
    MsgBox('Uninstalasi Dibatalkan: Token uninstalasi wajib diisi.', mbCriticalError, MB_OK);
    Result := False;
    Exit;
  end;

  // Strictly verify token with agent executable against server
  AgentExe := ExpandConstant('{app}\ProjectRAgent.exe');
  if FileExists(AgentExe) then
  begin
    StatusFile := AddBackslash(GetTempDir()) + 'projectr_verify_status.txt';
    if FileExists(StatusFile) then DeleteFile(StatusFile);

    if Exec(AgentExe, '--verify-uninstall "' + InputToken + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    begin
      if ResultCode = 0 then
      begin
        Result := True;
        Exit;
      end
      else
      begin
        StatusCode := '';
        StatusMsg := '';
        if FileExists(StatusFile) and LoadStringsFromFile(StatusFile, StatusLines) then
        begin
          if GetArrayLength(StatusLines) > 0 then StatusCode := StatusLines[0];
          if GetArrayLength(StatusLines) > 1 then StatusMsg := StatusLines[1];
        end;

        if (StatusCode = 'NETWORK_ERROR') or (ResultCode = 2) then
        begin
          if StatusMsg = '' then StatusMsg := 'Tidak dapat tersambung ke server.';
          MsgBox('GAGAL TERHUBUNG KE SERVER:' + #13#10#13#10 +
                 'Detail Masalah: ' + StatusMsg + #13#10#13#10 +
                 'Pastikan komputer ini terhubung ke internet untuk memverifikasi token uninstalasi.', mbCriticalError, MB_OK);
        end
        else
        begin
          MsgBox('AKSES DITOLAK: Token uninstalasi tidak valid atau ditolak oleh server Project R.' + #13#10#13#10 +
                 'Token uninstalasi yang Anda masukkan salah. Hubungi Administrator IT untuk token uninstalasi resmi perangkat ini.', mbCriticalError, MB_OK);
        end;
        Result := False;
        Exit;
      end;
    end;
  end;

  MsgBox('AKSES DITOLAK: Gagal memverifikasi otorisasi uninstalasi dengan server Project R.' + #13#10#13#10 +
         'Uninstalasi dibatalkan demi keamanan.', mbCriticalError, MB_OK);
  Result := False;
end;

// Post-uninstall wipe guarantee (Zero Traces)
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{commonappdata}\ProjectR');
    if DirExists(DataDir) then
    begin
      DelTree(DataDir, True, True, True);
    end;
  end;
end;
