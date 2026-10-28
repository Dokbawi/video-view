param([string]$TargetDirectory = $PSScriptRoot, [string]$ArchivePath, [switch]$AcceptDownload)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$version = '9.0.2'
$archiveHash = '60F467265B1E312373DBCD92200C2618A74850F98D3D078E94296BB3FA2047BA'
$url = 'https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.2-essentials_build.zip'
$binaryHashes = @{
    'ffmpeg.exe' = '3256173F3F8BFFD7DF12227C68ADF68025EDB1832273A9530688A7BB1ED8EDEC'
    'ffprobe.exe' = 'F0D36ECBBDD3BCFAC3EFA078C96C7271C2E68B3810595552AC3B7F17E9A65C52'
}

function Assert-NoLinks([string]$Path) {
    $itemPath = [IO.Path]::GetFullPath($Path)
    while ($itemPath) {
        if (Test-Path -LiteralPath $itemPath) {
            if (((Get-Item -LiteralPath $itemPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Install folders must not contain symbolic links or junctions.'
            }
        }
        $itemPath = [IO.Path]::GetDirectoryName($itemPath)
    }
}

$target = [IO.Path]::GetFullPath($TargetDirectory)
if ($target.Length -gt [IO.Path]::GetPathRoot($target).Length) { $target = $target.TrimEnd('\') }
Assert-NoLinks $target
if (-not (Test-Path -LiteralPath (Join-Path $target 'VideoView.exe') -PathType Leaf)) {
    throw 'Extract the entire VideoView ZIP first and run this script beside VideoView.exe.'
}
$toolsPath = Join-Path $target 'tools'
Assert-NoLinks $toolsPath
$installed = $true
foreach ($name in $binaryHashes.Keys) {
    $path = Join-Path $toolsPath $name
    if (-not (Test-Path -LiteralPath $path)) { $installed = $false; continue }
    Assert-NoLinks $path
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $binaryHashes[$name]) {
        throw "Existing $name differs from the supported build. Move your existing tools to a backup folder or select their paths in VideoView settings. Nothing was overwritten."
    }
}
if ($installed) { Write-Host 'Verified media tools are already installed. Open VideoView.exe.'; return }

if (-not $ArchivePath) {
    Write-Host "VideoView requires FFmpeg and FFprobe $version (GPLv3), downloaded from Gyan's official build server."
    Write-Host "Download: $url"
    Write-Host 'About 109 MB. No administrator access or system PATH changes. Recording files are not uploaded.'
    if (-not $AcceptDownload -and (Read-Host 'Download and install into this VideoView folder? [y/N]') -notmatch '^(?i:y|yes)$') {
        Write-Host 'Cancelled. No download or installation performed.'; return
    }
}

$temporaryRoot = Join-Path $target ('.media-tools-install-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
$committed = [Collections.Generic.List[string]]::new()
try {
    if ($ArchivePath) { $archive = [IO.Path]::GetFullPath($ArchivePath) }
    else {
        $archive = Join-Path $temporaryRoot 'ffmpeg.zip'
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $url -OutFile $archive -UseBasicParsing
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $archiveHash) {
        throw 'Download checksum mismatch. No executable was installed or run. Retry with a fresh official download.'
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $entries = @{
            'bin/ffmpeg.exe' = 'ffmpeg.exe'; 'bin/ffprobe.exe' = 'ffprobe.exe'
            'LICENSE' = 'FFmpeg-LICENSE.txt'; 'README.txt' = 'FFmpeg-README.txt'
        }
        foreach ($sourceName in $entries.Keys) {
            $entry = $zip.GetEntry("ffmpeg-$version-essentials_build/$sourceName")
            if (-not $entry -or $entry.Length -eq 0) { throw "Archive is missing $sourceName." }
            $output = Join-Path $temporaryRoot $entries[$sourceName]
            $inputStream = $entry.Open()
            try {
                $outputStream = [IO.File]::Open($output, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
                try { $inputStream.CopyTo($outputStream); $outputStream.Flush() } finally { $outputStream.Dispose() }
            } finally { $inputStream.Dispose() }
        }
    } finally { $zip.Dispose() }
    foreach ($name in $binaryHashes.Keys) {
        if ((Get-FileHash -LiteralPath (Join-Path $temporaryRoot $name) -Algorithm SHA256).Hash -ne $binaryHashes[$name]) {
            throw "Extracted $name checksum mismatch. Nothing was installed."
        }
    }
    Assert-NoLinks $toolsPath
    [IO.Directory]::CreateDirectory($toolsPath) | Out-Null
    foreach ($name in @('FFmpeg-LICENSE.txt', 'FFmpeg-README.txt', 'ffmpeg.exe', 'ffprobe.exe')) {
        $destination = Join-Path $toolsPath $name
        Assert-NoLinks $destination
        if (Test-Path -LiteralPath $destination) {
            if ((Get-FileHash -LiteralPath $destination).Hash -ne (Get-FileHash -LiteralPath (Join-Path $temporaryRoot $name)).Hash) {
                throw "Existing $name differs. Nothing existing was overwritten."
            }
            continue
        }
        [IO.File]::Move((Join-Path $temporaryRoot $name), $destination)
        $committed.Add($destination)
    }
    Write-Host 'Verified FFmpeg and FFprobe installed. You can now open VideoView.exe.'
} catch {
    foreach ($path in $committed) { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force } }
    throw
} finally {
    $resolvedTemporary = [IO.Path]::GetFullPath($temporaryRoot)
    if ([IO.Path]::GetDirectoryName($resolvedTemporary) -ne $target -or [IO.Path]::GetFileName($resolvedTemporary) -notlike '.media-tools-install-*') {
        throw 'Temporary cleanup path validation failed.'
    }
    Assert-NoLinks $resolvedTemporary
    if (Test-Path -LiteralPath $resolvedTemporary) { Remove-Item -LiteralPath $resolvedTemporary -Recurse -Force }
}
