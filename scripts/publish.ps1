param([switch]$SkipFfmpeg, [switch]$Zip, [string]$OutputName = 'VideoView-win-x64')
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$projectRoot = Split-Path $PSScriptRoot -Parent
if ($OutputName -notmatch '^VideoView-win-x64(-[A-Za-z0-9.]+)?$') { throw 'Invalid package name.' }
$outputDirectory = Join-Path $projectRoot ('.artifacts\' + $OutputName)
Push-Location $projectRoot
try {
    & dotnet publish src/VideoView.App/VideoView.App.csproj -c Release -r win-x64 --self-contained true -o $outputDirectory --nologo --tl:off -m:1 -nr:false -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    if (-not $SkipFfmpeg) {
        $ffmpegDirectory = Join-Path $projectRoot '.tools\ffmpeg'
        if (-not (Test-Path -LiteralPath (Join-Path $ffmpegDirectory 'bin\ffmpeg.exe'))) { throw 'Run scripts/setup.ps1 before publishing with FFmpeg.' }
        $mediaTools = Join-Path $outputDirectory 'tools'
        New-Item -ItemType Directory -Path $mediaTools -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $ffmpegDirectory 'bin\ffmpeg.exe'),(Join-Path $ffmpegDirectory 'bin\ffprobe.exe') -Destination $mediaTools -Force
        Copy-Item -LiteralPath (Join-Path $ffmpegDirectory 'LICENSE.txt') -Destination (Join-Path $mediaTools 'FFmpeg-LICENSE.txt') -Force
    }
    Copy-Item -LiteralPath 'README.md','THIRD_PARTY_NOTICES.md','LICENSE','NOTICE' -Destination $outputDirectory -Force
    $docsDirectory = Join-Path $outputDirectory 'docs'
    New-Item -ItemType Directory -Path $docsDirectory -Force | Out-Null
    Copy-Item -LiteralPath 'docs/ACCEPTANCE.md','docs/VALIDATION.md','docs/INTERACTION_DESIGN.md','docs/UX_RESEARCH.md' -Destination $docsDirectory -Force
    $licensesDirectory = Join-Path $outputDirectory 'licenses'
    New-Item -ItemType Directory -Path $licensesDirectory -Force | Out-Null
    Copy-Item -LiteralPath '.tools/nuget/naudio/2.2.1/license.txt' -Destination (Join-Path $licensesDirectory 'NAudio.txt') -Force
    foreach ($runtimePackage in @('microsoft.netcore.app.runtime.win-x64','microsoft.windowsdesktop.app.runtime.win-x64')) {
        Get-ChildItem -LiteralPath (Join-Path '.tools/nuget' $runtimePackage) -Recurse -File |
            Where-Object { $_.Name -match '^(LICENSE(\.txt)?|THIRD-PARTY-NOTICES\.TXT)$' } |
            ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $licensesDirectory ($runtimePackage + '-' + $_.Name)) -Force }
    }
    if ($Zip) {
        Compress-Archive -Path (Join-Path $outputDirectory '*') -DestinationPath (Join-Path $projectRoot ('.artifacts\' + $OutputName + '.zip')) -Force
    }
    Write-Host "Ready: $outputDirectory\VideoView.exe"
}
finally { Pop-Location }
