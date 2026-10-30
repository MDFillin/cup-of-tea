# Разворачивает сборку out\TempestVPN в тестовой ВМ и проверяет TUN:
#   1) копирует комплект в C:\PVpnSetup и ставит службу (от pvpnadmin);
#   2) переносит ссылки (по умолчанию my-*) и импортирует их от pvpn, временные файлы удаляет;
#   3) для каждого профиля: svc connect --test, e2e-check.ps1, svc disconnect (от pvpn).
# Пример: scripts\vm\deploy-and-test.ps1 -Profiles my-hy2,my-xhttp
[CmdletBinding()]
param(
    [string[]]$Profiles = @('my-hy2'),
    [string]$LinkFilter = 'my-',
    [int]$TestSeconds = 300,
    [switch]$SkipDeploy
)
$ErrorActionPreference = 'Stop'
# tempest-cli и PowerShell в ВМ пишут UTF-8.
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$root = Split-Path (Split-Path $PSScriptRoot)
$vm = Join-Path $PSScriptRoot 'vm.ps1'
$package = Join-Path $root 'out\TempestVPN'
$setup = 'C:\PVpnSetup'
# copyto кладёт содержимое out\TempestVPN прямо в $setup.
$guestPackage = $setup
# Команды для ВМ — без двойных кавычек (см. vm.ps1).
$cli = "& 'C:\Program Files\Tempest VPN\tempest-cli.exe'"

function Step([string]$Text) { Write-Host "`n=== $Text" -ForegroundColor Cyan }

if (-not (Test-Path (Join-Path $package 'TempestVPN.Service.exe'))) { throw "Нет сборки в $package — сначала scripts\build.ps1" }

if (-not $SkipDeploy) {
    Step 'Копирую комплект в ВМ'
    & $vm run -Command "Remove-Item $setup -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory $setup | Out-Null"
    & $vm copy -Source $package -Dest $setup

    Step 'Ставлю службу (pvpnadmin)'
    & $vm run -Command "& $guestPackage\install.ps1 -Source $guestPackage -UserSid (Get-LocalUser pvpn).SID.Value; Get-Service TempestVPN"

    Step "Переношу ссылки $LinkFilter* (pvpn)"
    $name = 'pvpn-links-' + [guid]::NewGuid().ToString('N') + '.txt'
    $hostLinks = Join-Path $env:TEMP $name
    try {
        & (Join-Path $package 'tempest-cli.exe') export --filter $LinkFilter | Set-Content $hostLinks -Encoding utf8
        & $vm copy -Source $hostLinks -Dest $setup
        & $vm run -AsUser -Command "$cli import $setup\$name; $cli list"
    }
    finally {
        Remove-Item $hostLinks -ErrorAction SilentlyContinue
        & $vm run -Command "Remove-Item $setup\pvpn-links-*.txt -Force -ErrorAction SilentlyContinue"
    }
}

foreach ($p in $Profiles) {
    Step "Подключаю $p и проверяю (pvpn)"
    & $vm run -AsUser -TimeoutSec ($TestSeconds + 120) -Command (
        "$cli svc connect $p --test $TestSeconds; " +
        "& $guestPackage\e2e-check.ps1; " +
        "$cli svc disconnect; $cli svc status")
}
