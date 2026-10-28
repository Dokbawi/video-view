param([switch]$SkipFfmpeg)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
try {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET 10 SDK is required: https://dotnet.microsoft.com/download/dotnet/10.0' }
    & dotnet restore VideoView.slnx --nologo --tl:off
    if ($LASTEXITCODE -ne 0) { throw 'NuGet restore failed.' }
    $toolsRoot = Join-Path $projectRoot '.tools\ffmpeg'
    if (-not $SkipFfmpeg -and -not (Test-Path -LiteralPath (Join-Path $toolsRoot 'bin\ffmpeg.exe'))) {
        $version = '9.0.2'
        $sha256 = '60F467265B1E312373DBCD92200C2618A74850F98D3D078E94296BB3FA2047BA'
        $downloads = Join-Path $projectRoot '.tools\downloads'
        New-Item -ItemType Directory -Path $downloads -Force | Out-Null
        $archive = Join-Path $downloads "ffmpeg-$version.zip"
        Invoke-WebRequest -Uri "https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-$version-essentials_build.zip" -OutFile $archive
        if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $sha256) { throw 'FFmpeg SHA-256 mismatch. Archive was not executed or extracted.' }
        $extractRoot = Join-Path $downloads "ffmpeg-$version"
        Expand-Archive -LiteralPath $archive -DestinationPath $extractRoot -Force
        $buildRoot = Join-Path $extractRoot "ffmpeg-$version-essentials_build"
        New-Item -ItemType Directory -Path $toolsRoot -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $buildRoot 'bin') -Destination $toolsRoot -Recurse -Force
        Copy-Item -LiteralPath (Join-Path $buildRoot 'LICENSE') -Destination (Join-Path $toolsRoot 'LICENSE.txt') -Force
    }
    Write-Host 'Ready. Run: dotnet run --project src/VideoView.App'
}
finally { Pop-Location }
