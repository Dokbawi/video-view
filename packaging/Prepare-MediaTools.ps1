# SPDX-License-Identifier: GPL-3.0-only
# Invoked hidden by VideoView setup after the pinned download has been verified.
param([Parameter(Mandatory=$true)][string]$ArchivePath, [Parameter(Mandatory=$true)][string]$Destination)
$ErrorActionPreference = 'Stop'
try {
    # Use framework APIs so inherited PowerShell 7 module paths cannot hide 5.1 cmdlets.
    $hashStream = [IO.File]::OpenRead($ArchivePath)
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try { $archiveHash = [BitConverter]::ToString($sha256.ComputeHash($hashStream)).Replace('-', '') }
    finally { $hashStream.Dispose(); $sha256.Dispose() }
    if ($archiveHash -ne '60F467265B1E312373DBCD92200C2618A74850F98D3D078E94296BB3FA2047BA') {
        throw 'Media archive SHA-256 mismatch.'
    }
    $destinationPath = [IO.Path]::GetFullPath($Destination)
    $check = $destinationPath
    while ($check) {
        if (([IO.File]::Exists($check) -or [IO.Directory]::Exists($check)) -and ([IO.File]::GetAttributes($check) -band [IO.FileAttributes]::ReparsePoint)) { throw 'Media staging folder must not contain links.' }
        $check = [IO.Path]::GetDirectoryName($check)
    }
    $null = [IO.Directory]::CreateDirectory($destinationPath)
    $null = [Reflection.Assembly]::Load('System.IO.Compression.FileSystem, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089')
    $zip = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $entries = @{
            'bin/ffmpeg.exe' = 'ffmpeg.exe'; 'bin/ffprobe.exe' = 'ffprobe.exe'
            'LICENSE' = 'FFmpeg-LICENSE.txt'; 'README.txt' = 'FFmpeg-README.txt'
        }
        foreach ($name in $entries.Keys) {
            $entry = $zip.GetEntry('ffmpeg-9.0.2-essentials_build/' + $name)
            if (-not $entry -or $entry.Length -eq 0) { throw "Missing archive entry: $name" }
            $path = [IO.Path]::Combine($destinationPath, $entries[$name])
            if (([IO.File]::Exists($path) -or [IO.Directory]::Exists($path)) -and ([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint)) { throw 'Media staging file must not be a link.' }
            $inputStream = $entry.Open()
            try {
                $outputStream = [IO.File]::Open($path, [IO.FileMode]::Create, [IO.FileAccess]::Write)
                try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose() }
            } finally { $inputStream.Dispose() }
        }
    } finally { $zip.Dispose() }
    exit 0
} catch {
    [IO.File]::WriteAllText([IO.Path]::Combine($Destination, 'prepare-error.txt'), $_.Exception.Message)
    exit 1
}
