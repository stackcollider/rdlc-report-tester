[Setup]
AppId={{A3F2C1D4-7B8E-4F6A-9D2C-1E5B8A3F7C9D}
AppName=RDLC Report Tester
AppVersion=2.01
AppPublisher=StackCollaider
AppPublisherURL=https://github.com/StackCollaider
AppSupportURL=https://github.com/StackCollaider
AppUpdatesURL=https://github.com/StackCollaider
DefaultDirName={autopf}\RdlcReportTester
DefaultGroupName=RDLC Report Tester
LicenseFile=LICENSE.txt
SetupIconFile=StackCollaider.ico
UninstallDisplayIcon={app}\RdlcRendererWpf.exe
UninstallDisplayName=RDLC Report Tester
OutputBaseFilename=RdlcReportTester-Setup-2.01
OutputDir=.\installer_output
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
WizardStyle=modern
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "launchapp"; Description: "Launch RDLC Report Tester after installation"; GroupDescription: "After installation:"; Flags: unchecked

[Files]
Source: "bin\Release\net8.0-windows\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\RDLC Report Tester"; Filename: "{app}\RdlcRendererWpf.exe"
Name: "{group}\Uninstall RDLC Report Tester"; Filename: "{uninstallexe}"
Name: "{autodesktop}\RDLC Report Tester"; Filename: "{app}\RdlcRendererWpf.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\RdlcRendererWpf.exe"; Description: "Launch RDLC Report Tester"; Flags: nowait postinstall skipifsilent; Tasks: launchapp

[Code]

const
  WebView2RegKey32 = 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2RegKey64 = 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2InstallerURL = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703';
  WebView2InstallerFile = 'MicrosoftEdgeWebview2Setup.exe';

function IsWebView2Installed: Boolean;
var
  version: string;
begin
  Result := RegQueryStringValue(HKLM, WebView2RegKey32, 'pv', version) or
            RegQueryStringValue(HKLM, WebView2RegKey64, 'pv', version) or
            RegQueryStringValue(HKCU, WebView2RegKey32, 'pv', version) or
            RegQueryStringValue(HKCU, WebView2RegKey64, 'pv', version);
  if Result then
    Result := (version <> '') and (version <> '0.0.0.0');
end;

function DownloadAndInstallWebView2: Boolean;
var
  TempFile: string;
  ResultCode: Integer;
begin
  Result := False;
  TempFile := ExpandConstant('{tmp}\' + WebView2InstallerFile);

  WizardForm.Hide;

  if MsgBox(
    'Microsoft Edge WebView2 Runtime is required but not installed.' + #13#10 + #13#10 +
    'Click OK to download and install it now (requires internet connection),' + #13#10 +
    'or Cancel to abort the installation.',
    mbConfirmation, MB_OKCANCEL) = IDCANCEL then
  begin
    WizardForm.Show;
    Exit;
  end;

  try
    DownloadTemporaryFile(WebView2InstallerURL, WebView2InstallerFile, '', nil);
  except
    MsgBox(
      'Failed to download WebView2 Runtime installer.' + #13#10 +
      'Please download it manually from:' + #13#10 +
      WebView2InstallerURL,
      mbError, MB_OK);
    WizardForm.Show;
    Exit;
  end;

  if Exec(TempFile, '/silent /install', '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
  begin
    if ResultCode = 0 then
      Result := True
    else
      MsgBox(
        'WebView2 Runtime installer exited with code: ' + IntToStr(ResultCode) + #13#10 +
        'Please install it manually from:' + #13#10 +
        WebView2InstallerURL,
        mbError, MB_OK);
  end
  else
    MsgBox(
      'Failed to run WebView2 Runtime installer.' + #13#10 +
      'Please install it manually from:' + #13#10 +
      WebView2InstallerURL,
      mbError, MB_OK);

  WizardForm.Show;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;

  if CurPageID = wpReady then
  begin
    if not IsWebView2Installed then
    begin
      if not DownloadAndInstallWebView2 then
      begin
        Result := False;
        Exit;
      end;

      if not IsWebView2Installed then
      begin
        if MsgBox(
          'WebView2 Runtime could not be verified after installation.' + #13#10 +
          'Do you want to continue anyway?',
          mbConfirmation, MB_YESNO) = IDNO then
        begin
          Result := False;
          Exit;
        end;
      end;
    end;
  end;
end;
