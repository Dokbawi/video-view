param([switch]$Native, [switch]$SilentNative, [int]$SoakSeconds = 0)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
try {
    & dotnet build VideoView.slnx --nologo --tl:off -m:1 -nr:false -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    $testArguments = @()
    if ($Native) { $testArguments += '--native' }
    if ($SilentNative) { $testArguments += '--silent-native' }
    if ($SoakSeconds -gt 0) { $testArguments += '--soak'; $testArguments += $SoakSeconds.ToString() }
    & 'tests\VideoView.Tests\bin\Debug\net10.0-windows10.0.26100.0\VideoView.Tests.exe' @testArguments
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}
finally { Pop-Location }
