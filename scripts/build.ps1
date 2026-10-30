# Собирает готовый к установке комплект в out\TempestVPN: интерфейс, служба, tempest-cli, ядра и скрипты установки.
# Ядра лежат в cores-bundled: install.ps1 ставит их, только если они новее уже установленных.
# Сборка self-contained: на целевом ПК .NET не нужен. Установщик из комплекта собирает build-setup.ps1.
#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$Out = (Join-Path $PSScriptRoot '..\out\TempestVPN')
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$cores = Join-Path $root 'cores'
if (-not (Test-Path (Join-Path $cores 'xray.exe'))) { & (Join-Path $PSScriptRoot 'fetch-cores.ps1') }

if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
New-Item -ItemType Directory -Force $Out | Out-Null

foreach ($project in 'src\PVpn.Service\PVpn.Service.csproj', 'src\PVpn.Cli\PVpn.Cli.csproj', 'src\PVpn.App\PVpn.App.csproj') {
    Write-Host "Публикую $project"
    dotnet publish (Join-Path $root $project) -c Release -r win-x64 --self-contained true -o $Out -p:DebugType=none -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Сборка $project не удалась" }
}

New-Item -ItemType Directory -Force (Join-Path $Out 'cores-bundled') | Out-Null
Copy-Item (Join-Path $cores '*') (Join-Path $Out 'cores-bundled') -Force
foreach ($s in 'install.ps1', 'uninstall.ps1', 'e2e-check.ps1') {
    Copy-Item (Join-Path $PSScriptRoot $s) $Out -Force
}
$size = (Get-ChildItem $Out -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("Готово: {0} ({1:N0} МБ)" -f (Resolve-Path $Out), $size)
