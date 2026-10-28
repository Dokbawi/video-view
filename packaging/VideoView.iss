; Build with scripts/package-installer.ps1. Media tools come directly from upstream.
#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef PayloadDir
  #error PayloadDir is required
#endif
#ifdef InstallerTest
  #define InstallId "{{4DE347B4-E824-4059-99EE-CE5A210DC4F0}"
  #define AppMutexName "Local\VideoView.InstallerTest"
  #define SetupSuffix "-test"
#else
  #define InstallId "{{AF40550C-29CD-4487-B8D7-DF90F60EEAA8}"
  #define AppMutexName "Local\VideoView.Desktop"
  #define SetupSuffix ""
#endif

[Setup]
AppId={#InstallId}
AppName=VideoView
AppVersion={#AppVersion}
AppPublisher=Dokbawi
AppPublisherURL=https://github.com/Dokbawi/video-view
AppSupportURL=https://github.com/Dokbawi/video-view/issues
AppUpdatesURL=https://github.com/Dokbawi/video-view/releases
DefaultDirName={localappdata}\Programs\VideoView
DefaultGroupName=VideoView{#SetupSuffix}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
SetupArchitecture=x64
MinVersion=10.0.22000
WizardStyle=modern dynamic
DisableWelcomePage=yes
DisableProgramGroupPage=yes
DisableDirPage=yes
DisableReadyPage=yes
DisableFinishedPage=yes
ShowLanguageDialog=no
AllowNoIcons=yes
SetupIconFile=..\src\VideoView.App\Assets\VideoView.ico
UninstallDisplayIcon={app}\VideoView.exe
OutputBaseFilename=VideoView-Setup-v{#AppVersion}-win-x64{#SetupSuffix}
Compression=lzma2
SolidCompression=yes
ExtraDiskSpaceRequired=350000000
AppMutex={#AppMutexName}
SetupMutex=Local\VideoView.Setup{#SetupSuffix}
CloseApplications=no
RestartApplications=no

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
korean.MediaTitle=미디어 도구 준비
english.MediaTitle=Preparing media tools
korean.MediaDescription=녹음·편집을 준비합니다. 공식 Gyan 다운로드 약 109MB · 완료 후 자동 실행
english.MediaDescription=About 109 MB from Gyan. VideoView will open automatically when ready.
korean.ExtractDescription=다운로드 완료. 미디어 도구를 확인하는 중입니다.
english.ExtractDescription=Download complete. Checking the media tools.
korean.MediaFailed=미디어 도구를 준비하지 못해 설치를 진행하지 않았습니다. 인터넷 연결과 저장 공간을 확인하고 다시 시도해 주세요.
english.MediaFailed=Setup could not prepare the media tools. Installation has not started. Check your connection and free disk space, then retry.
korean.MediaCancelled=준비를 취소했습니다. 앱과 기존 파일은 변경되지 않았습니다.
english.MediaCancelled=Preparation cancelled. The application and existing files have not changed.
korean.DownloadStalled=다운로드 응답이 지연되어 중단했습니다. 인터넷 연결을 확인하고 다시 시도해 주세요. 기존 파일은 변경되지 않았습니다.
english.DownloadStalled=The download stopped responding. Check your connection and retry. Existing files have not changed.
korean.CloseApp=녹화를 저장한 뒤 트레이 메뉴에서 VideoView를 종료하고 다시 시도해 주세요.
english.CloseApp=Save your recording, then exit VideoView from its tray menu and retry.

[Files]
Source: "Prepare-MediaTools.ps1"; Flags: dontcopy
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Excludes: "Install-MediaTools.cmd,Install-MediaTools.ps1,tools\*"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{tmp}\media-ready\ffmpeg.exe"; DestDir: "{app}\tools"; ExternalSize: 110000000; Flags: external ignoreversion
Source: "{tmp}\media-ready\ffprobe.exe"; DestDir: "{app}\tools"; ExternalSize: 110000000; Flags: external ignoreversion
Source: "{tmp}\media-ready\FFmpeg-LICENSE.txt"; DestDir: "{app}\tools"; ExternalSize: 36000; Flags: external ignoreversion
Source: "{tmp}\media-ready\FFmpeg-README.txt"; DestDir: "{app}\tools"; ExternalSize: 50000; Flags: external ignoreversion

[Icons]
Name: "{group}\VideoView"; Filename: "{app}\VideoView.exe"; WorkingDir: "{app}"

[Run]
#ifdef InstallerTest
Filename: "{app}\VideoView.exe"; Parameters: "--ui-smoke ""{app}\installer-launch.png"" --data-directory ""{app}\installer-profile"" --page home"; Flags: nowait skipifsilent
#else
Filename: "{app}\VideoView.exe"; Flags: nowait skipifsilent
#endif

[Code]
const
  MediaUrl = 'https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.2-essentials_build.zip';
  ArchiveHash = '60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba';
  FfmpegHash = '3256173f3f8bffd7df12227c68adf68025edb1832273a9530688a7bb1ed8edec';
  FfprobeHash = 'f0d36ecbbdd3bcfac3efa078c96c7271c2e68b3810595552ac3b7f17e9a65c52';
  MediaLicenseHash = '8ceb4b9ee5adedde47b31e975c1d90c73ad27b6b165a1dcd80c7c545eb65b903';
  MediaReadmeHash = '0342de6bb39dd421cbec2c00d89ad274b640d75ad3f2cab09ffd647ce2d8f949';
var
  DownloadPage: TDownloadWizardPage;
  ExtractPage: TOutputProgressWizardPage;
  LastDownloadProgress, LastDownloadTick: Int64;
  DownloadStalled: Boolean;
  AutomaticStartQueued: Boolean;

function TickMilliseconds: Int64;
  external 'GetTickCount64@kernel32.dll stdcall';

function DownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  if Progress <> LastDownloadProgress then begin
    LastDownloadProgress := Progress;
    LastDownloadTick := TickMilliseconds;
  end;
  DownloadStalled := TickMilliseconds - LastDownloadTick > 45000;
  Result := not DownloadStalled;
end;

function FileAttributes(const FileName: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';

procedure CheckNoLinks(Path: String);
var
  Parent: String;
  Attr: LongWord;
begin
  Path := RemoveBackslashUnlessRoot(ExpandFileName(Path));
  repeat
    Attr := FileAttributes(Path);
    if (Attr <> $FFFFFFFF) and ((Attr and $400) <> 0) then
      RaiseException('Choose a folder without symbolic links or junctions: ' + Path);
    Parent := ExtractFileDir(Path);
    if Parent = Path then Break;
    Path := Parent;
  until Path = '';
end;

function Verified(const Path, Hash: String): Boolean;
begin
  Result := FileExists(Path);
  if Result then Result := CompareText(GetSHA256OfFile(Path), Hash) = 0;
end;

procedure CopyRequired(const Source, Dest: String);
begin
  if not FileCopy(Source, Dest, False) then RaiseException('Cannot prepare media file: ' + ExtractFileName(Source));
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(CustomMessage('MediaTitle'), CustomMessage('MediaDescription'), @DownloadProgress);
  DownloadPage.ShowBaseNameInsteadOfUrl := True;
  ExtractPage := CreateOutputProgressPage(CustomMessage('MediaTitle'), CustomMessage('ExtractDescription'));
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  // Inno retains wpReady when every preceding page is disabled. Queue the normal
  // action once, after page initialization, so a normal EXE launch needs no clicks.
  if (CurPageID = wpReady) and not WizardSilent and not AutomaticStartQueued then begin
    AutomaticStartQueued := True;
    PostMessage(WizardForm.NextButton.Handle, $00F5, 0, 0); // BM_CLICK
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Archive, Ready, Existing: String;
  ErrorText: AnsiString;
  ExitCode: Integer;
begin
  Result := '';
  if CheckForMutexes('{#AppMutexName}') then begin
    Result := CustomMessage('CloseApp');
    Exit;
  end;
  try
    CheckNoLinks(ExpandConstant('{app}\tools'));
    CheckNoLinks(ExpandConstant('{app}\tools\ffmpeg.exe'));
    CheckNoLinks(ExpandConstant('{app}\tools\ffprobe.exe'));
    CheckNoLinks(ExpandConstant('{app}\tools\FFmpeg-LICENSE.txt'));
    CheckNoLinks(ExpandConstant('{app}\tools\FFmpeg-README.txt'));
    Ready := ExpandConstant('{tmp}\media-ready');
    if not ForceDirectories(Ready) then RaiseException('Cannot create media tools staging folder.');
    Existing := ExpandConstant('{app}\tools');
    // Reinstall without another download when the installed binaries are intact.
    // A supplied archive always takes precedence, and never bypasses hash checks.
    Archive := ExpandConstant('{param:MEDIATOOLSARCHIVE|}');
    if (Archive = '') and Verified(Existing + '\ffmpeg.exe', FfmpegHash) and
       Verified(Existing + '\ffprobe.exe', FfprobeHash) and
       Verified(Existing + '\FFmpeg-LICENSE.txt', MediaLicenseHash) and
       Verified(Existing + '\FFmpeg-README.txt', MediaReadmeHash) then begin
      CopyRequired(Existing + '\ffmpeg.exe', Ready + '\ffmpeg.exe');
      CopyRequired(Existing + '\ffprobe.exe', Ready + '\ffprobe.exe');
      CopyRequired(Existing + '\FFmpeg-LICENSE.txt', Ready + '\FFmpeg-LICENSE.txt');
      CopyRequired(Existing + '\FFmpeg-README.txt', Ready + '\FFmpeg-README.txt');
      Log('Reusing verified installed media tools.');
    end else begin
      if Archive = '' then begin
        DownloadPage.Clear;
        LastDownloadProgress := -1;
        LastDownloadTick := TickMilliseconds;
        DownloadStalled := False;
        DownloadPage.Add(MediaUrl, 'ffmpeg-9.0.2.zip', ArchiveHash);
        DownloadPage.Show;
        try
          DownloadPage.Download;
        finally
          DownloadPage.Hide;
        end;
        Archive := ExpandConstant('{tmp}\ffmpeg-9.0.2.zip');
      end;
      if not Verified(Archive, ArchiveHash) then RaiseException('Media archive SHA-256 mismatch.');
      ExtractPage.Show;
      try
        ExtractPage.SetText(CustomMessage('ExtractDescription'), 'FFmpeg 9.0.2');
        ExtractTemporaryFile('Prepare-MediaTools.ps1');
        // Windows' own ZIP library extracts four exact entries; no shell or console UI.
        if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
          '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
          ExpandConstant('{tmp}\Prepare-MediaTools.ps1') + '" -ArchivePath "' + Archive +
          '" -Destination "' + Ready + '"', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
          RaiseException('Cannot start Windows media preparation.');
        if ExitCode <> 0 then begin
          ErrorText := 'Windows media preparation failed.';
          LoadStringFromFile(Ready + '\prepare-error.txt', ErrorText);
          RaiseException(UTF8Decode(ErrorText));
        end;
        if not Verified(Ready + '\ffmpeg.exe', FfmpegHash) or
           not Verified(Ready + '\ffprobe.exe', FfprobeHash) or
           not Verified(Ready + '\FFmpeg-LICENSE.txt', MediaLicenseHash) or
           not Verified(Ready + '\FFmpeg-README.txt', MediaReadmeHash) then
          RaiseException('Media executable SHA-256 mismatch.');
      finally
        ExtractPage.Hide;
      end;
    end;
    Log('Media tools verified. Ready to install VideoView.');
  except
    if DownloadStalled then Result := CustomMessage('DownloadStalled')
    else if DownloadPage.AbortedByUser then Result := CustomMessage('MediaCancelled')
    else Result := CustomMessage('MediaFailed') + #13#10#13#10 + GetExceptionMessage;
    Log(Result);
  end;
end;
