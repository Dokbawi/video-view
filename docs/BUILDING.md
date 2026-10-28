# 소스 빌드

Windows 11 x64와 .NET 10 SDK가 필요합니다.

```powershell
git clone https://github.com/Dokbawi/video-view.git
cd video-view
./scripts/setup.ps1
dotnet run --project src/VideoView.App
```

`setup.ps1`은 NuGet을 `.tools/nuget`에 복원하고 SHA-256으로 확인한 FFmpeg 9.0.2를 `.tools/ffmpeg`에 준비합니다. 기존 도구를 사용하려면 `-SkipFfmpeg`로 실행하고 앱 설정에서 두 실행 파일 경로를 지정합니다.

## 검사

```powershell
./scripts/test.ps1 -SilentNative
```

합성 데이터·자체 창을 사용하며 `.artifacts/tests`에 결과를 만듭니다. `-SilentNative`는 스피커 테스트음을 내는 루프백 검사를 제외합니다. `-Native`와 테스트 실행 파일의 `--tone`·`--fixture`는 실제 소리가 날 수 있습니다.

```powershell
# 305초 무음 버퍼 기록과 최근 300초 반복 저장
& ./tests/VideoView.Tests/bin/Debug/net10.0-windows10.0.26100.0/VideoView.Tests.exe --buffer-soak
```

## 배포

```powershell
./scripts/publish.ps1 -SkipFfmpeg -OutputName VideoView-win-x64-public
./scripts/package-installer.ps1 -Compiler 'C:\path\to\Inno Setup 7\ISCC.exe'
```

설치 파일 빌드에는 [Inno Setup 7.1 이상](https://jrsoftware.org/isdl.php)이 필요합니다. `.tools/inno/ISCC.exe`에 준비했다면 `-Compiler`는 생략할 수 있습니다. 공식 설치 파일을 사용할 때 배포처의 서명·체크섬을 확인하세요.

`package-installer.ps1`은 프로젝트 버전과 배포 EXE 버전이 같은지 확인하고 `.artifacts`에 설치 EXE, 포터블 ZIP, 두 파일의 `SHA256SUMS.txt`를 만듭니다. 공개 패키지에는 문서·라이선스를 포함하며 FFmpeg 실행 파일·디버그 심볼·사용자 설정·녹화 파일은 제외합니다. 포터블 ZIP만 필요하면 `package-public.ps1`을 실행하세요. 개인 테스트용 `publish.ps1` 기본 동작은 로컬 FFmpeg를 포함하므로 그대로 공개하지 않습니다.

설치 EXE는 선택 페이지 없이 진행 창을 표시하고 준비가 끝나면 앱을 실행합니다. 앱 복사 전에 FFmpeg 9.0.2 ZIP을 다운로드하고 고정 SHA-256을 검증한 뒤, Windows PowerShell 5.1의 ZIP 기능으로 필요한 파일 4개만 임시 폴더에 추출합니다. 별도 콘솔 창은 표시하지 않습니다. 실패하면 설치를 시작하지 않습니다. `/MEDIATOOLSARCHIVE="C:\path\ffmpeg-9.0.2-essentials_build.zip"`으로 이미 받은 공식 ZIP을 재사용할 수도 있으며 동일한 검증을 거칩니다. `/DIR="C:\path\VideoView"`로 설치 위치를 지정할 수 있습니다. 설치 제거에는 설치 프로그램이 기록한 파일만 포함되고 사용자 녹화·설정은 삭제하지 않습니다.

설치 흐름 회귀 검사는 `scripts/test-installer.ps1`을 사용합니다. 실제 HKCU 설치 등록과 시작 메뉴 바로가기를 생성한 뒤 제거하므로, VideoView가 설치되지 않은 테스트 계정에서 실행하세요. 녹음·화면 캡처는 하지 않습니다.

기존 앱을 유지하면서 검증하려면 동일한 `.iss`를 `/DInstallerTest`로 별도 빌드하고 검사 스크립트에 `-SetupPath <test.exe> -IsolatedIdentity`를 전달합니다. 테스트 빌드는 AppId·뮤텍스를 분리하고 자동 실행에 격리된 무음 UI 렌더 인자를 추가합니다. `-Download`는 실제 공식 서버에서 다운로드합니다. `-test.exe`는 공개 릴리즈에 올리지 않습니다.

`src/VideoView.App`은 화면·트레이·전역 키, `VideoView.Platform`은 Windows 캡처·오디오·기록·복구, `VideoView.Core`는 파일 관리·버퍼 정책·편집·내보내기를 담당합니다.
