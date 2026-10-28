param([string]$SourceDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) '.artifacts\VideoView-win-x64-public'))
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$source = [IO.Path]::GetFullPath($SourceDirectory)
$exe = Join-Path $source 'VideoView.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw 'Publish the Windows x64 app first.' }
$version = ([xml](Get-Content (Join-Path $root 'src\VideoView.App\VideoView.App.csproj') -Raw)).Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$' -or (Get-Item -LiteralPath $exe).VersionInfo.FileVersion -ne "$version.0") {
    throw 'Published app version does not match the project. Publish again before packaging.'
}
$artifacts = Join-Path $root '.artifacts'
$stage = Join-Path $artifacts ('public-stage-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
foreach ($file in Get-ChildItem -LiteralPath $source -Recurse -File) {
    if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Package source contains a link.' }
    $relative = $file.FullName.Substring($source.TrimEnd('\').Length + 1)
    if ($relative -like 'tools\*' -or $relative -like 'docs\*' -or $file.Extension -eq '.pdb') { continue }
    if ($file.Name -match '^(settings\.json|.*\.recording\.json)$' -or $file.Extension -in @('.mkv','.mp4','.m4a','.mka','.log','.key','.pfx')) {
        throw "Unexpected private/generated payload: $relative"
    }
    $destination = Join-Path $stage $relative
    New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destination
}
foreach ($name in @('README.md','THIRD_PARTY_NOTICES.md','CHANGELOG.md','CONTRIBUTING.md')) {
    Copy-Item -LiteralPath (Join-Path $root $name) -Destination $stage -Force
}
foreach ($name in @('LICENSE','NOTICE')) { Copy-Item -LiteralPath (Join-Path $root $name) -Destination $stage -Force }
Copy-Item -LiteralPath (Join-Path $root 'docs') -Destination $stage -Recurse -Force
Copy-Item -LiteralPath (Join-Path $root 'licenses') -Destination $stage -Recurse -Force
Copy-Item -LiteralPath (Join-Path $root 'packaging\Install-MediaTools.ps1'),(Join-Path $root 'packaging\Install-MediaTools.cmd') -Destination $stage
$zipName = "VideoView-v$version-win-x64.zip"
$zipPath = Join-Path $artifacts $zipName
$temporaryZip = $zipPath + '.' + [guid]::NewGuid().ToString('N') + '.partial'
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $temporaryZip, [IO.Compression.CompressionLevel]::Optimal, $false)
$archive = [IO.Compression.ZipFile]::OpenRead($temporaryZip)
try {
    foreach ($required in @('VideoView.exe','Install-MediaTools.cmd','Install-MediaTools.ps1','README.md','LICENSE','NOTICE','licenses/Vortice.Windows.txt','licenses/SharpGenTools.txt')) {
        if (-not $archive.GetEntry($required)) { throw "Missing package entry: $required" }
    }
    if (@($archive.Entries | Where-Object { $_.FullName -match '(^|/)(ffmpeg|ffprobe)\.exe$|\.pdb$|settings\.json$|\.recording\.json$' }).Count) {
        throw 'Public ZIP contains an excluded payload.'
    }
} finally { $archive.Dispose() }
Move-Item -LiteralPath $temporaryZip -Destination $zipPath -Force
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $artifacts 'SHA256SUMS.txt'), "$hash  $zipName`n", [Text.UTF8Encoding]::new($false))
[pscustomobject]@{ Version = $version; Stage = $stage; Zip = $zipPath; Bytes = (Get-Item $zipPath).Length; Sha256 = $hash }
