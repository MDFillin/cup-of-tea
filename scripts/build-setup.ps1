# Собирает установщик out\TempestVPN-Setup-<версия>.exe: комплект (build.ps1) и Inno Setup 6 (installer\TempestVPN.iss).
# Inno Setup: winget install JRSoftware.InnoSetup
#Requires -Version 5.1
[CmdletBinding()]
param(
    # Взять уже собранный out\TempestVPN.
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') }

$iscc = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Не найден Inno Setup 6 (ISCC.exe): winget install JRSoftware.InnoSetup' }

$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
& $iscc /Q "/DAppVersion=$version" (Join-Path $root 'installer\TempestVPN.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup завершился с кодом $LASTEXITCODE" }
$setup = Join-Path $root "out\TempestVPN-Setup-$version.exe"
Write-Host ("Готово: {0} ({1:N0} МБ)" -f $setup, ((Get-Item $setup).Length / 1MB))
