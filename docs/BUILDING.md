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
./scripts/package-public.ps1
```

개인 테스트용 `publish.ps1` 기본 동작은 로컬 FFmpeg를 포함합니다. 공개 릴리즈는 `package-public.ps1`로 만들어 문서·라이선스·설치 스크립트·체크섬을 포함하고 FFmpeg 실행 파일과 디버그 심볼을 제외합니다.

`src/VideoView.App`은 화면·트레이·전역 키, `VideoView.Platform`은 Windows 캡처·오디오·기록·복구, `VideoView.Core`는 파일 관리·버퍼 정책·편집·내보내기를 담당합니다.
