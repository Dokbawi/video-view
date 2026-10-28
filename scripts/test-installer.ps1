param(
    [string]$SetupPath = (Join-Path (Split-Path $PSScriptRoot -Parent) '.artifacts\VideoView-Setup-v0.5.1-win-x64.exe'),
    [string]$ArchivePath = (Join-Path (Split-Path $PSScriptRoot -Parent) '.tools\downloads\ffmpeg.zip'),
    [switch]$Download,
    [switch]$IsolatedIdentity
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$setup = [IO.Path]::GetFullPath($SetupPath)
$archive = [IO.Path]::GetFullPath($ArchivePath)
$appId = if ($IsolatedIdentity) { '{4DE347B4-E824-4059-99EE-CE5A210DC4F0}' } else { '{AF40550C-29CD-4487-B8D7-DF90F60EEAA8}' }
$mutexName = if ($IsolatedIdentity) { 'Local\VideoView.InstallerTest' } else { 'Local\VideoView.Desktop' }
$registryPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\' + $appId + '_is1'
if (Test-Path -LiteralPath $registryPath) { throw 'Use an account without an existing VideoView installer registration.' }
if (-not $IsolatedIdentity -and (Get-Process VideoView -ErrorAction SilentlyContinue)) { throw 'Exit VideoView before running installer tests, or build a private /DInstallerTest variant and pass -IsolatedIdentity.' }
$testRoot = Join-Path $root ('.artifacts\installer-test-' + [guid]::NewGuid().ToString('N'))
$installDir = Join-Path $testRoot 'installed app'
$group = if ($IsolatedIdentity) { 'VideoView-test' } else { 'VideoView' }
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) ($group + '\VideoView.lnk')
if (Test-Path -LiteralPath $shortcut) { throw 'An existing shortcut would be overwritten. Use a clean test account.' }
New-Item -ItemType Directory -Path $testRoot | Out-Null
$results = [Collections.Generic.List[string]]::new()
function Assert([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw "FAILED: $Name" }
    $results.Add($Name)
    Write-Host "PASS: $Name"
}
function Run-Setup([string]$Name, [string]$Extra = '') {
    $log = Join-Path $testRoot ($Name + '.log')
    $arguments = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /LANG=korean /TASKS="" /DIR="' + $installDir + '" /GROUP="' + $group + '" /LOG="' + $log + '" ' + $Extra
    $process = Start-Process -FilePath $setup -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
    return $process.ExitCode
}
function Uninstall-OwnedTest {
    $uninstaller = Join-Path $installDir 'unins000.exe'
    if (-not (Test-Path -LiteralPath $uninstaller)) { return }
    $registered = Get-ItemProperty -LiteralPath $registryPath -ErrorAction Stop
    if ($registered.InstallLocation.TrimEnd('\') -ne $installDir) { throw 'Test uninstall registration does not match the isolated test folder.' }
    if (-not ([IO.Path]::GetFullPath($uninstaller).StartsWith($testRoot + '\', [StringComparison]::OrdinalIgnoreCase))) { throw 'Uninstaller escaped the test folder.' }
    $process = Start-Process -FilePath $uninstaller -ArgumentList ('/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG="' + (Join-Path $testRoot 'uninstall.log') + '"') -WindowStyle Hidden -PassThru -Wait
    if ($process.ExitCode -ne 0) { throw "Uninstall failed: $($process.ExitCode)" }
}
try {
    $badArchive = Join-Path $testRoot 'invalid.zip'
    [IO.File]::WriteAllText($badArchive, 'intentionally invalid test archive')
    Assert ((Run-Setup 'invalid-archive' ('/MEDIATOOLSARCHIVE="' + $badArchive + '"')) -ne 0) 'Invalid archive rejected'
    Assert (-not (Test-Path -LiteralPath $installDir)) 'Failed prerequisite leaves app folder absent'
    Assert (-not (Test-Path -LiteralPath $registryPath)) 'Failed prerequisite leaves installation unregistered'
    Assert ((Run-Setup 'missing-archive' ('/MEDIATOOLSARCHIVE="' + (Join-Path $testRoot 'missing.zip') + '"')) -ne 0) 'Missing archive rejected'

    $mutex = [Threading.Mutex]::new($true, $mutexName)
    try { Assert ((Run-Setup 'app-running' ('/MEDIATOOLSARCHIVE="' + $archive + '"')) -ne 0) 'Running app blocks installation without forced termination' }
    finally { $mutex.ReleaseMutex(); $mutex.Dispose() }

    $mediaArgument = if ($Download) { '' } else { '/MEDIATOOLSARCHIVE="' + $archive + '"' }
    Assert ((Run-Setup 'install' $mediaArgument) -eq 0) 'Installation succeeds'
    Assert ((Get-Item (Join-Path $installDir 'VideoView.exe')).VersionInfo.FileVersion -eq '0.5.1.0') 'Installed app version matches'
    Assert ((Get-FileHash (Join-Path $installDir 'tools\ffmpeg.exe')).Hash -eq '3256173F3F8BFFD7DF12227C68ADF68025EDB1832273A9530688A7BB1ED8EDEC') 'Installed FFmpeg verified'
    Assert ((Get-FileHash (Join-Path $installDir 'tools\ffprobe.exe')).Hash -eq 'F0D36ECBBDD3BCFAC3EFA078C96C7271C2E68B3810595552AC3B7F17E9A65C52') 'Installed FFprobe verified'
    Assert (Test-Path -LiteralPath (Join-Path $installDir 'tools\FFmpeg-LICENSE.txt')) 'Upstream media license installed'
    Assert ([IO.File]::ReadAllText((Join-Path $installDir 'LICENSE')).Contains('Version 3, 29 June 2007')) 'GPL v3 license installed'
    Assert ([IO.File]::ReadAllText((Join-Path $installDir 'NOTICE')).Contains('GPL-3.0-only')) 'Copyright and source notice installed'
    Assert (Test-Path -LiteralPath $shortcut) 'Start menu shortcut created'
    Assert (-not (Test-Path -LiteralPath (Join-Path $installDir 'Install-MediaTools.cmd'))) 'Manual setup script unnecessary in installed app'

    $recording = Join-Path $installDir 'my-recording.mp4'
    & (Join-Path $installDir 'tools\ffmpeg.exe') -hide_banner -loglevel error -f lavfi -i 'color=c=black:s=320x180:r=30' -f lavfi -i 'anullsrc=r=48000:cl=stereo' -t 1 -c:v libx264 -c:a aac $recording
    Assert ($LASTEXITCODE -eq 0) 'Installed media tools encode silent MP4'
    $probe = & (Join-Path $installDir 'tools\ffprobe.exe') -v error -show_entries format=duration -of json $recording | ConvertFrom-Json
    Assert ([double]::Parse($probe.format.duration, [Globalization.CultureInfo]::InvariantCulture) -ge 1) 'Installed FFprobe reads output'
    $recordingHash = (Get-FileHash -LiteralPath $recording).Hash
    $settings = Join-Path $installDir 'settings.json'
    [IO.File]::WriteAllText($settings, '{"installerTestSentinel":true}')
    $settingsHash = (Get-FileHash -LiteralPath $settings).Hash
    Assert ((Run-Setup 'reinstall') -eq 0) 'Reinstallation succeeds'
    Assert ([IO.File]::ReadAllText((Join-Path $testRoot 'reinstall.log')).Contains('Reusing verified installed media tools.')) 'Reinstallation reuses verified tools without download'
    Assert ((Get-FileHash -LiteralPath $recording).Hash -eq $recordingHash) 'Reinstallation preserves recording'
    Assert ((Get-FileHash -LiteralPath $settings).Hash -eq $settingsHash) 'Reinstallation preserves user settings'
    Assert ((Run-Setup 'failed-upgrade' ('/MEDIATOOLSARCHIVE="' + $badArchive + '"')) -ne 0) 'Failed upgrade rejected'
    Assert ((Get-FileHash -LiteralPath $recording).Hash -eq $recordingHash) 'Failed upgrade preserves recording'
    Assert (Test-Path -LiteralPath (Join-Path $installDir 'VideoView.exe')) 'Failed upgrade preserves installed app'

    $smokeImage = Join-Path $testRoot 'installed-app.png'
    $dataDir = Join-Path $testRoot 'isolated-profile'
    $app = Start-Process -FilePath (Join-Path $installDir 'VideoView.exe') -ArgumentList ('--ui-smoke "' + $smokeImage + '" --data-directory "' + $dataDir + '" --page home') -WindowStyle Hidden -PassThru -Wait
    Assert ($app.ExitCode -eq 0 -and (Test-Path -LiteralPath $smokeImage)) 'Installed app renders with isolated profile'

    Uninstall-OwnedTest
    Assert (-not (Test-Path -LiteralPath (Join-Path $installDir 'VideoView.exe'))) 'Uninstall removes application'
    Assert (-not (Test-Path -LiteralPath (Join-Path $installDir 'tools\ffmpeg.exe'))) 'Uninstall removes installed media tools'
    Assert (-not (Test-Path -LiteralPath $registryPath)) 'Uninstall removes app registration'
    Assert (-not (Test-Path -LiteralPath $shortcut)) 'Uninstall removes shortcut'
    Assert ((Get-FileHash -LiteralPath $recording).Hash -eq $recordingHash) 'Uninstall preserves user recording even inside app folder'
    Assert ((Get-FileHash -LiteralPath $settings).Hash -eq $settingsHash) 'Uninstall preserves user settings'
    [pscustomobject]@{ Passed = $results.Count; Download = [bool]$Download; IsolatedIdentity = [bool]$IsolatedIdentity; SetupSha256 = (Get-FileHash $setup).Hash; Checks = $results; Directory = $testRoot } | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $testRoot 'results.json')
    Write-Host "Results: $testRoot"
} finally {
    if (Test-Path -LiteralPath $registryPath) { Uninstall-OwnedTest }
}
