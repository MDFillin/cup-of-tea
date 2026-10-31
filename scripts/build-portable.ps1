# Собирает портативную версию out\CupOfTea-Portable-<версия>.zip: без установки и прав администратора.
# Работает только режим прокси (TUN требует службу); серверы и настройки лежат в папке data рядом с программой
# и зашифрованы для текущего пользователя Windows (на другом ПК их придётся добавить заново).
#Requires -Version 5.1
[CmdletBinding()]
param(
    # Взять уже собранный out\CupOfTea.
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
if (-not $PSScriptRoot) { $PSScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path }
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') }

$bundle = Join-Path $root 'out\CupOfTea'
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$portable = Join-Path $root 'out\CupOfTea-Portable'
if (Test-Path $portable) { Remove-Item $portable -Recurse -Force }
Copy-Item $bundle $portable -Recurse

# Ядра — сразу в cores (установщика, который их разложит, нет); служба и скрипты установки не нужны.
Rename-Item (Join-Path $portable 'cores-bundled') 'cores'
Get-ChildItem $portable -Filter 'CupOfTea.Service*' | Remove-Item -Force
foreach ($s in 'install.ps1', 'uninstall.ps1', 'e2e-check.ps1') { Remove-Item (Join-Path $portable $s) -ErrorAction SilentlyContinue }
'Портативная версия: данные в папке data рядом с программой.' | Set-Content (Join-Path $portable 'portable.txt') -Encoding utf8

$zip = Join-Path $root "out\CupOfTea-Portable-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $portable '*') -DestinationPath $zip -CompressionLevel Optimal
Write-Host ("Готово: {0} ({1:N0} МБ)" -f $zip, ((Get-Item $zip).Length / 1MB))
