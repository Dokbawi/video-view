param(
    [string]$SourceDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) '.artifacts\VideoView-win-x64-public'),
    [string]$Compiler = (Join-Path (Split-Path $PSScriptRoot -Parent) '.tools\inno\ISCC.exe')
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not (Test-Path -LiteralPath $Compiler -PathType Leaf)) { throw 'Inno Setup 7.1 or later is required. Pass -Compiler with the path to ISCC.exe.' }
$package = & (Join-Path $PSScriptRoot 'package-public.ps1') -SourceDirectory $SourceDirectory
$output = Join-Path $root '.artifacts'
& $Compiler '/Qp' "/DAppVersion=$($package.Version)" "/DPayloadDir=$($package.Stage)" "/O$output" (Join-Path $root 'packaging\VideoView.iss') | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$setup = Join-Path $output "VideoView-Setup-v$($package.Version)-win-x64.exe"
$checksums = foreach ($path in @($setup, $package.Zip)) {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant(), [IO.Path]::GetFileName($path)
}
[IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS.txt'), ($checksums -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
[pscustomobject]@{ Version = $package.Version; Setup = $setup; Zip = $package.Zip; Stage = $package.Stage; Checksums = $checksums }
